using System.Numerics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// What <see cref="SimulationResponseImportService.Inspect"/> found in an <c>.ac</c> file: enough
/// for the dialog to offer its trace pickers, show what the sweep covers, and say what is wrong
/// with it before anything is committed.
/// </summary>
/// <summary>
/// One sweep of an <c>.ac</c> file. An ordinary run holds exactly one; a <c>.step</c>ped run holds
/// one per step, written end to end, which is why every caller walks a list rather than reading the
/// file's figures directly.
/// </summary>
/// <param name="FirstPoint">
/// Where it begins in the file's own numbering, after any leading 0 Hz point has been left out —
/// so the pair (FirstPoint, PointCount) is what the importer reads and what the user can count to.
/// </param>
/// <param name="Label">
/// What the simulator called this step, e.g. <c>freq=2000</c>, read from the sibling <c>.log</c>.
/// Null when there is nothing to read it from, which is every QSpice run: it prints its
/// <c>.step</c> lines to the console and writes no log file at all.
/// </param>
public sealed record AcSimulationSweep(
    int Index,
    int FirstPoint,
    int PointCount,
    double StartFreqHz,
    double EndFreqHz,
    double PointsPerDecade,
    bool IsLogSpaced,
    string? Label);

/// <summary>
/// What <see cref="SimulationResponseImportService.Inspect"/> found in an <c>.ac</c> file: enough
/// for the dialog to offer its trace pickers, show what the sweep covers, and say what is wrong
/// with it before anything is committed.
/// </summary>
/// <param name="PointCount">
/// The <b>first sweep's</b>, not the file's — as are <c>StartFreqHz</c>, <c>EndFreqHz</c>,
/// <c>PointsPerDecade</c> and <c>IsLogSpaced</c>. On a stepped <c>.ac</c> run every step shares one
/// grid (measured: all three steps of both generated fixtures are 121 points over 1 Hz – 1 MHz), so
/// these describe every sweep; <c>Sweeps</c> carries each one's own figures regardless.
/// </param>
public sealed record AcSimulationFileInfo(
    string Path,
    string Title,
    string? Date,
    string Plotname,
    IReadOnlyList<string> Flags,
    int PointCount,
    double StartFreqHz,
    double EndFreqHz,
    double PointsPerDecade,
    bool IsLogSpaced,
    IReadOnlyList<SimulationTrace> Traces,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AcSimulationSweep> Sweeps);

/// <summary>What to import from an <c>.ac</c> run, and how to analyse it.</summary>
/// <param name="ReferenceTraceIndex">
/// The node to divide the output by, or null to take the output as the transfer function on its
/// own. Dividing is what a bench measurement does, and it is the only way to be right when the
/// simulator's AC source is not exactly 1 — measured on the QSpice reference circuit, whose source
/// sags to 0.9914 at 1 kHz, that is 0.075 dB.
/// </param>
/// <param name="SweepIndices">
/// Which sweeps of a <c>.step</c>ped file to import, or null for all of them. Each becomes a result
/// of its own, because <c>UNIQUE(capture_id, kind)</c> allows one curve per kind per result — which
/// is also what makes them independently overlayable and separately deletable.
/// </param>
public sealed record AcSimulationImportRequest(
    string Path,
    int OutputTraceIndex,
    int? ReferenceTraceIndex,
    double LevelOffsetDb,
    double AnalysisLowHz,
    double AnalysisHighHz,
    BandwidthReferenceSpec Reference,
    string? Name,
    string? Notes,
    IReadOnlyList<int>? SweepIndices = null);

/// <summary>
/// Imports a simulator's <c>.ac</c> run as a frequency-response result, and re-analyses one that is
/// already stored.
///
/// <para>A sibling of <see cref="SimulationImportService"/> rather than more arms inside it. The
/// two share the parser and nothing else: everything that one does after reading the header is
/// time-domain — the uniform regrid, the anti-alias decimation, the FFT, the periodic-record probe
/// — and so is every one of its refusals. <b>Waveform compression, maximum timestep and
/// <c>1 / duration</c> resolution are transient concepts, and firing any of them on a frequency
/// sweep would be reading a field that does not apply.</b></para>
///
/// <para>The analysis itself is <see cref="SteppedCaptureSupport.BuildPointTrace"/>, i.e. the same
/// arithmetic a stepped-sine sweep runs, because the data has the same shape: log-spaced discrete
/// points, each one exact, with no decimation and nothing to smooth. That is also what makes a
/// stored sweep losslessly re-analysable — the rows <i>are</i> the curve.</para>
/// </summary>
public sealed class SimulationResponseImportService(
    MeasurementRepository measurementRepository,
    IOptions<JsonOptions> jsonOptions,
    ILogger<SimulationResponseImportService> logger)
{
    /// <summary>Below this the corner search is interpolating over gaps too wide to locate one in.</summary>
    private const double SparseSweepPointsPerDecade = 20.0;

    /// <summary>How far a reference curve may vary across the sweep before it is worth mentioning.</summary>
    private const double ReferenceSagToleranceDb = 0.1;

    private JsonSerializerOptions SerializerOptions => jsonOptions.Value.SerializerOptions;

    /// <summary>
    /// Reads the header and the frequency axis, and says what is wrong with the file. Nothing is
    /// committed and no trace data is read beyond the frequency column.
    /// </summary>
    public AcSimulationFileInfo Inspect(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("No such file.", path);

        var header = LtspiceRawFile.ReadHeader(path);
        RequireAc(header, path);

        var frequency = LtspiceRawFile.ReadComplexColumns(path, header, [header.FrequencyVariable!.Index], ct)[0];
        double[] whole = [.. frequency.Select(c => c.Real)];
        var sweeps = SplitSweeps(whole, header, path);

        // The first sweep is what the file's own figures describe, and on a stepped `.ac` run every
        // step shares its grid, so the warnings below are the whole file's as well.
        var first = sweeps[0];
        int skipped = first.FirstPoint;
        var freqHz = whole.AsSpan(first.FirstPoint, first.PointCount);
        var axis = AcFrequencyAxis.Of(freqHz);

        var warnings = new List<string>();

        if (sweeps.Count > 1)
        {
            warnings.Add(
                // Only the count: what a stepped import then does with the sweeps is said beside the
                // checkboxes that pick them, the way the transient dialog's pair already divides it.
                // Said in both places it was the same sentence twice, one under the other.
                $"This is a stepped (`.step`) run: {sweeps.Count} sweeps in one file." +
                (sweeps[^1].Label is null
                    ? " They are numbered, because the simulator records what it stepped only on the console."
                    : ""));
        }

        if (skipped > 0)
        {
            warnings.Add(
                $"The first {(skipped == 1 ? "point of this sweep is" : $"{skipped} points of this sweep are")} " +
                "at 0 Hz, and " + (skipped == 1 ? "it has" : "they have") + " been left out: DC has no phase " +
                "delay and no place on a logarithmic axis. `.ac lin` from 0 does this. The rest of the sweep " +
                $"is imported unchanged, from {axis.StartHz:G4} Hz up.");
        }

        if (!axis.IsLogSpaced)
        {
            warnings.Add(
                "This sweep is not logarithmically spaced, so almost every point sits in its top decade and the " +
                "bottom of the band is drawn from a handful of them. Use `.ac dec 100 <start> <stop>` rather than " +
                "`.ac lin`.");
        }
        else if (axis.PointsPerDecade < SparseSweepPointsPerDecade)
        {
            warnings.Add(
                $"This sweep has {axis.PointsPerDecade:F0} points per decade, which is " +
                $"{(Math.Pow(10.0, 1.0 / Math.Max(axis.PointsPerDecade, 1.0)) - 1.0) * 100.0:F0} % between " +
                "points — a -3 dB corner can be located no more closely than that. Raise the density, e.g. " +
                "`.ac dec 100 <start> <stop>`.");
        }

        var traces = header.Variables
            .Where(v => !v.IsFrequency)
            .Select(v => new SimulationTrace(v.Index, v.Name, v.Unit))
            .ToList();
        if (traces.Count == 0)
            throw new FormatException($"{Path.GetFileName(path)} carries no traces besides frequency.");

        return new AcSimulationFileInfo(
            Path: Path.GetFullPath(path),
            Title: header.Title,
            Date: header.Date,
            Plotname: header.Plotname,
            Flags: [.. header.Flags],
            PointCount: axis.PointCount,
            StartFreqHz: axis.StartHz,
            EndFreqHz: axis.EndHz,
            PointsPerDecade: axis.PointsPerDecade,
            IsLogSpaced: axis.IsLogSpaced,
            Traces: traces,
            Warnings: warnings,
            Sweeps: sweeps);
    }

    /// <summary>
    /// Splits a file into its sweeps and validates each one on its own, which is what makes a
    /// <c>.step</c>ped run readable instead of refused.
    ///
    /// <para><see cref="LtspiceRawFile.SweepStarts"/> cuts at every point where the axis fails to
    /// increase, so <b>every segment it returns is strictly increasing by construction</b> — there
    /// is no monotonicity left to check, only the leading-DC slice and the sizes.</para>
    ///
    /// <para>The header's <c>stepped</c> flag is what tells a stepped run from a damaged one. Both
    /// simulators set it, and a file with a backwards axis that does <i>not</i> claim to be stepped
    /// is still refused — the arithmetic would otherwise cheerfully turn a corrupt or
    /// viewer-rewritten file into several plausible-looking results.</para>
    /// </summary>
    private static List<AcSimulationSweep> SplitSweeps(double[] whole, LtspiceRawHeader header, string path)
    {
        string name = Path.GetFileName(path);

        // Before splitting, because a run of equal values is a restart by SweepStarts' reckoning:
        // an all-DC file would otherwise be reported as a stepped run of one-point sweeps, which is
        // arithmetically true and useless to read.
        if (LeadingDcPoints(whole) == whole.Length)
            throw new FormatException($"{name} has no point above 0 Hz, so it is not a sweep. Every point is DC.");

        var starts = LtspiceRawFile.SweepStarts(whole);

        if (starts.Length > 1 && !header.IsStepped)
        {
            throw new FormatException(
                $"{name} has a frequency axis that goes backwards at point {starts[1]}, and its header does not " +
                "say it is a stepped (`.step`) run — a stepped file from either simulator carries a `stepped` " +
                "flag, and this one does not. Re-run the simulation.");
        }

        var labels = starts.Length > 1 ? SpiceStepLog.ReadLabels(path) : [];
        var sweeps = new List<AcSimulationSweep>(starts.Length);

        for (int s = 0; s < starts.Length; s++)
        {
            int from = starts[s];
            int upTo = s + 1 < starts.Length ? starts[s + 1] : whole.Length;
            int skipped = LeadingDcPoints(whole.AsSpan(from, upTo - from));
            from += skipped;
            int count = upTo - from;

            string where = starts.Length > 1 ? $" of sweep {s + 1} of {starts.Length}" : "";

            if (count < 2)
            {
                throw new FormatException(
                    $"{name} has {count} point{where} above 0 Hz, which is not a sweep.");
            }

            if (whole[from] <= 0.0)
            {
                throw new FormatException(
                    $"{name} has a negative frequency at point {from}, so it is not a frequency sweep this " +
                    "import can read.");
            }

            if (count > AcSimulationAnalysis.MaxPoints)
            {
                throw new FormatException(
                    $"{name} holds {count:N0} points{where}, more than the " +
                    $"{AcSimulationAnalysis.MaxPoints:N0} this import stores. A `.ac dec 100` sweep of the audio " +
                    "band is a few hundred; use a lower density, or a narrower range.");
            }

            var axis = AcFrequencyAxis.Of(whole.AsSpan(from, count));
            sweeps.Add(new AcSimulationSweep(
                Index: s,
                FirstPoint: from,
                PointCount: count,
                StartFreqHz: axis.StartHz,
                EndFreqHz: axis.EndHz,
                PointsPerDecade: axis.PointsPerDecade,
                IsLogSpaced: axis.IsLogSpaced,
                Label: s < labels.Count ? labels[s] : null));
        }

        return sweeps;
    }

    /// <summary>
    /// Imports the selected traces into <paramref name="measurementTypeId"/>, one result per sweep.
    ///
    /// <para>An ordinary run returns one id. A <c>.step</c>ped run returns one per step it was asked
    /// for, because <c>UNIQUE(capture_id, kind)</c> holds one curve of each kind per result — and
    /// separate results are the better shape anyway: the History list already groups imported
    /// simulations together, each can be shown or hidden on its own, and each keeps its own corners
    /// and its own re-analysis.</para>
    /// </summary>
    public async Task<IReadOnlyList<long>> ImportAsync(
        long measurementTypeId, AcSimulationImportRequest request, CancellationToken ct)
    {
        var info = Inspect(request.Path, ct);
        var header = LtspiceRawFile.ReadHeader(request.Path);
        request.Reference.Validate();

        var chosen = ResolveSweeps(info, request.SweepIndices);

        int frequencyIndex = header.FrequencyVariable!.Index;
        var indices = new List<int> { frequencyIndex, request.OutputTraceIndex };
        bool hasReference = request.ReferenceTraceIndex is { } referenceIndex
            && referenceIndex != request.OutputTraceIndex;
        if (hasReference)
            indices.Add(request.ReferenceTraceIndex!.Value);

        // One pass over the file whatever the sweep count: the columns hold every sweep end to end,
        // and each AcSimulationSweep is the window into them.
        var columns = LtspiceRawFile.ReadComplexColumns(request.Path, header, indices, ct);

        var resultIds = new List<long>(chosen.Count);
        foreach (var sweep in chosen)
            resultIds.Add(await ImportSweepAsync(measurementTypeId, request, info, header, columns, sweep, hasReference, ct));

        return resultIds;
    }

    /// <summary>Which sweeps an import asked for, refusing an index the file does not have.</summary>
    private static List<AcSimulationSweep> ResolveSweeps(AcSimulationFileInfo info, IReadOnlyList<int>? requested)
    {
        if (requested is null || requested.Count == 0)
            return [.. info.Sweeps];

        var chosen = new List<AcSimulationSweep>(requested.Count);
        foreach (int index in requested.Distinct().Order())
        {
            if (index < 0 || index >= info.Sweeps.Count)
            {
                // No parameter name: this message is read by a user in a dialog, and
                // ArgumentException appends "(Parameter 'requested')" to anything that names one.
                throw new ArgumentException(
                    $"This file holds {info.Sweeps.Count} sweep{(info.Sweeps.Count == 1 ? "" : "s")}, so there " +
                    $"is no sweep {index + 1} to import.");
            }

            chosen.Add(info.Sweeps[index]);
        }

        return chosen;
    }

    private async Task<long> ImportSweepAsync(
        long measurementTypeId,
        AcSimulationImportRequest request,
        AcSimulationFileInfo info,
        LtspiceRawHeader header,
        Complex[][] columns,
        AcSimulationSweep sweep,
        bool hasReference,
        CancellationToken ct)
    {
        double[] freqHz = [.. columns[0].Skip(sweep.FirstPoint).Take(sweep.PointCount).Select(c => c.Real)];
        Complex[] output = [.. columns[1].Skip(sweep.FirstPoint).Take(sweep.PointCount)];
        Complex[]? reference = hasReference
            ? [.. columns[2].Skip(sweep.FirstPoint).Take(sweep.PointCount)]
            : null;

        var warnings = new List<string>(info.Warnings);
        AddCurveWarnings(warnings, freqHz, reference, request);

        var source = new AcSimulationSource(
            Path: info.Path,
            Title: info.Title,
            Date: info.Date,
            Plotname: info.Plotname,
            Flags: info.Flags,
            OutputTrace: header.Variables[request.OutputTraceIndex].Name,
            OutputUnit: header.Variables[request.OutputTraceIndex].Unit,
            ReferenceTrace: hasReference ? header.Variables[request.ReferenceTraceIndex!.Value].Name : null,
            ReferenceUnit: hasReference ? header.Variables[request.ReferenceTraceIndex!.Value].Unit : null,
            PointCount: sweep.PointCount,
            StartFreqHz: sweep.StartFreqHz,
            EndFreqHz: sweep.EndFreqHz,
            PointsPerDecade: sweep.PointsPerDecade,
            Warnings: warnings,
            SweepCount: info.Sweeps.Count,
            SweepIndex: info.Sweeps.Count > 1 ? sweep.Index : null,
            SweepLabel: sweep.Label);

        var spec = new AcSimulationAnalysis(
            source, request.LevelOffsetDb, request.AnalysisLowHz, request.AnalysisHighHz,
            request.Reference, RightChannelIsReference: hasReference);

        // The curves the rows are built from, in the same shape a re-analysis will read back: dB
        // and *unwrapped* degrees. Going through the same two arrays at import is what makes the
        // two paths produce identical numbers -- see CanonicalGainPhase.
        var outputCurve = ToCurve(output);
        var referenceCurve = reference is null ? null : ToCurve(reference);
        var traces = BuildTraces(freqHz, outputCurve, referenceCurve, spec);

        string name = SweepName(request, source, sweep, info.Sweeps.Count);

        long resultId = await measurementRepository.SaveResponseResultAsync(
            new ResponseSaveRequest(
                measurementTypeId, name, request.Notes, spec.ToParamsJson(SerializerOptions),
                DeviceName: null, Serial: null, FwVer: null, IsSimulated: true,
                // A simulated sweep has no sample rate and no record length: it is N points of
                // exact arithmetic. Zero rather than a plausible-looking figure, for the reason
                // the transient import writes a silenced generator.
                SampleRateHz: 0.0, FftSize: sweep.PointCount,
                Channels: hasReference ? 2 : 1,
                InRangeDbv: null, OutRangeDbv: null,
                Traces: traces,
                PointPreviews: null,
                Source: MeasurementSource.Simulation),
            ct);

        logger.LogInformation(
            "Imported AC simulation {Name} from {Path}: {Output}{Reference}, sweep {Sweep} of {Sweeps}, " +
            "{Points} points over {StartHz:F3}-{EndHz:F0} Hz ({PerDecade:F0}/decade), band {LowHz:F0}-{HighHz:F0} Hz",
            name, request.Path, source.OutputTrace,
            source.ReferenceTrace is null ? "" : $" / {source.ReferenceTrace}",
            sweep.Index + 1, info.Sweeps.Count,
            sweep.PointCount, sweep.StartFreqHz, sweep.EndFreqHz, sweep.PointsPerDecade,
            spec.AnalysisLowHz, spec.AnalysisHighHz);

        return resultId;
    }

    /// <summary>
    /// What a result is called. One sweep keeps the name the user typed, or the file and trace;
    /// several have to be told apart, so each carries its step's own label — the simulator's own
    /// words where there are any (<c>freq=2000</c>), and its position where there are not.
    /// </summary>
    private static string SweepName(
        AcSimulationImportRequest request, AcSimulationSource source, AcSimulationSweep sweep, int sweepCount)
    {
        string stem = string.IsNullOrWhiteSpace(request.Name)
            ? $"{Path.GetFileNameWithoutExtension(request.Path)} - {source.OutputTrace}"
            : request.Name.Trim();

        if (sweepCount <= 1)
            return stem;

        return $"{stem} - {sweep.Label ?? $"step {sweep.Index + 1} of {sweepCount}"}";
    }

    /// <summary>
    /// Imports an <c>.ac</c> run as an <b>impedance</b> result: one voltage trace over one current
    /// trace, which is Z in ohms at every frequency the simulator solved.
    ///
    /// <para>The same file-level refusals as the response import — it goes through the same
    /// <c>Inspect</c> — and the same one-result-per-sweep shape. What differs is two lines of
    /// arithmetic and where the rows land.</para>
    ///
    /// <para><b>Both traces are required and their units are checked</b>, because an impedance is
    /// volts over amps and nothing else. <see cref="LtspiceVariable.Unit"/> already maps
    /// <c>device_current</c> and its relatives to A, so the mistake can be named rather than
    /// divided. Requiring the current also removes the "drive it with 1 A and read the voltage as
    /// ohms" shortcut's assumption without removing the shortcut: with a 1 A source that column is
    /// a constant 1, and dividing by it costs nothing.</para>
    ///
    /// <para><b>The sign is the trap, and the data settles it.</b> LTspice's <c>I(V1)</c> flows
    /// <i>into</i> the source's + terminal, so V/I comes out half a turn round as often as not —
    /// the same signature <see cref="ImpedanceMath.NegativeRealFraction"/> already exists to catch
    /// on a bench measurement, where it can only warn because the fix there is physical rewiring.
    /// Here the fix is a sign and the data proves which one, since a passive one-port cannot
    /// present a negative real part over most of its band. So
    /// <see cref="AcImpedanceCurrentDirection.Auto"/> flips it and says so in a warning, and the
    /// two explicit settings are there for anyone who disagrees.</para>
    /// </summary>
    public async Task<IReadOnlyList<long>> ImportImpedanceAsync(
        long measurementTypeId, AcImpedanceImportRequest request, CancellationToken ct)
    {
        var info = Inspect(request.Path, ct);
        var header = LtspiceRawFile.ReadHeader(request.Path);
        var chosen = ResolveSweeps(info, request.SweepIndices);

        var voltage = header.Variables[RequireTraceIndex(header, request.VoltageTraceIndex, "voltage")];
        var current = header.Variables[RequireTraceIndex(header, request.CurrentTraceIndex, "current")];
        RequireUnit(voltage, "V", "a voltage", request.Path);
        RequireUnit(current, "A", "a current", request.Path);

        var columns = LtspiceRawFile.ReadComplexColumns(
            request.Path, header, [header.FrequencyVariable!.Index, voltage.Index, current.Index], ct);

        var resultIds = new List<long>(chosen.Count);
        foreach (var sweep in chosen)
        {
            resultIds.Add(await ImportImpedanceSweepAsync(
                measurementTypeId, request, info, voltage, current, columns, sweep, ct));
        }

        return resultIds;
    }

    private async Task<long> ImportImpedanceSweepAsync(
        long measurementTypeId,
        AcImpedanceImportRequest request,
        AcSimulationFileInfo info,
        LtspiceVariable voltage,
        LtspiceVariable current,
        Complex[][] columns,
        AcSimulationSweep sweep,
        CancellationToken ct)
    {
        double[] freqHz = [.. columns[0].Skip(sweep.FirstPoint).Take(sweep.PointCount).Select(c => c.Real)];
        Complex[] v = [.. columns[1].Skip(sweep.FirstPoint).Take(sweep.PointCount)];
        Complex[] i = [.. columns[2].Skip(sweep.FirstPoint).Take(sweep.PointCount)];

        var warnings = new List<string>(info.Warnings);
        var z = DivideToImpedance(v, i, request.CurrentDirection, warnings, out var resolvedDirection);

        var trace = ImpedanceTraceBuilder.BuildPoints(
            ImpedanceTraceKind.Impedance,
            freqHz,
            z,
            // Exact arithmetic on an exact curve, so there is no second estimate for it to agree
            // with -- which is what coherence reports. The same 1.0 BuildPointTrace writes.
            [.. Enumerable.Repeat(1.0, freqHz.Length)]);

        var source = new AcSimulationSource(
            Path: info.Path,
            Title: info.Title,
            Date: info.Date,
            Plotname: info.Plotname,
            Flags: info.Flags,
            OutputTrace: voltage.Name,
            OutputUnit: voltage.Unit,
            ReferenceTrace: current.Name,
            ReferenceUnit: current.Unit,
            PointCount: sweep.PointCount,
            StartFreqHz: sweep.StartFreqHz,
            EndFreqHz: sweep.EndFreqHz,
            PointsPerDecade: sweep.PointsPerDecade,
            Warnings: warnings,
            SweepCount: info.Sweeps.Count,
            SweepIndex: info.Sweeps.Count > 1 ? sweep.Index : null,
            SweepLabel: sweep.Label);

        string stem = string.IsNullOrWhiteSpace(request.Name)
            ? $"{Path.GetFileNameWithoutExtension(request.Path)} - {voltage.Name}/{current.Name}"
            : request.Name.Trim();
        string name = info.Sweeps.Count <= 1
            ? stem
            : $"{stem} - {sweep.Label ?? $"step {sweep.Index + 1} of {info.Sweeps.Count}"}";

        long resultId = await measurementRepository.SaveResponseResultAsync(
            new ResponseSaveRequest(
                measurementTypeId, name, request.Notes,
                ImpedanceParamsJson(source, sweep, resolvedDirection),
                DeviceName: null, Serial: null, FwVer: null, IsSimulated: true,
                SampleRateHz: 0.0, FftSize: sweep.PointCount,
                Channels: 2,
                InRangeDbv: null, OutRangeDbv: null,
                Traces: [MeasurementSaveService.ToImpedanceTraceInput(trace, ResponseTraceKind.Impedance)],
                PointPreviews: null,
                Source: MeasurementSource.Simulation),
            ct);

        logger.LogInformation(
            "Imported AC simulation {Name} from {Path} as impedance: {Voltage}/{Current}, sweep {Sweep} of " +
            "{Sweeps}, {Points} points over {StartHz:F3}-{EndHz:F0} Hz, peak {PeakDb:F2} dB-ohm at {PeakHz:F1} Hz",
            name, request.Path, voltage.Name, current.Name, sweep.Index + 1, info.Sweeps.Count,
            sweep.PointCount, sweep.StartFreqHz, sweep.EndFreqHz, trace.PeakDbOhms, trace.PeakFreqHz);

        return resultId;
    }

    /// <summary>
    /// Z = V / I, with the current's reference direction resolved.
    ///
    /// <para>A zero current reads as zero ohms rather than as infinity: a non-finite value on this
    /// curve is stored by SQLite as NULL and truncates the JSON response part way through on the
    /// way back out — the <c>ImpedanceStatusJsonTests</c> rule — and a node the source cannot drive
    /// has no impedance worth reporting anyway.</para>
    /// </summary>
    /// <param name="resolved">
    /// What <see cref="AcImpedanceCurrentDirection.Auto"/> decided, so the result can record which
    /// way round its stored curve faces — which is what makes the direction changeable afterwards.
    /// Never <c>Auto</c>.
    /// </param>
    private static Complex[] DivideToImpedance(
        Complex[] voltage,
        Complex[] current,
        AcImpedanceCurrentDirection direction,
        List<string> warnings,
        out AcImpedanceCurrentDirection resolved)
    {
        var z = new Complex[voltage.Length];
        for (int k = 0; k < z.Length; k++)
            z[k] = current[k] == Complex.Zero ? Complex.Zero : voltage[k] / current[k];

        bool invert = direction == AcImpedanceCurrentDirection.Inverted;
        if (direction == AcImpedanceCurrentDirection.Auto)
        {
            double negative = ImpedanceMath.NegativeRealFraction(z, 0, z.Length - 1);
            if (negative > 0.5)
            {
                invert = true;
                warnings.Add(
                    $"{negative * 100.0:F0} % of this curve had a negative real part, which a passive one-port " +
                    "cannot present, so the current's sign has been flipped. That is the usual case rather than " +
                    "a fault: a simulator's source current flows into its own + terminal, which puts V/I half a " +
                    "turn round. Re-analyse the result to set the direction by hand if that is not what you meant.");
            }
        }

        if (invert)
        {
            for (int k = 0; k < z.Length; k++)
                z[k] = -z[k];
        }

        resolved = invert ? AcImpedanceCurrentDirection.Inverted : AcImpedanceCurrentDirection.AsStored;
        return z;
    }

    /// <summary>
    /// Turns a stored imported impedance over, or leaves it alone, without the file.
    ///
    /// <para><b>The direction is the one thing here that <i>is</i> re-appliable</b>, which an
    /// earlier version of this import got wrong by saying there was nothing. Negating an impedance
    /// leaves the magnitude untouched and moves the phase half a turn, so the stored row carries
    /// everything needed to turn it back over — and the stored direction says which way it is
    /// facing now. Nothing else about an impedance import can change: there is no band, no corner
    /// search and no reference level, and which two traces were divided is settled at import
    /// because the other columns were never read.</para>
    ///
    /// <para><c>Auto</c> is re-decided against the <i>stored</i> curve rather than remembered, so
    /// asking for it on a result that is already the right way up is a no-op — which is what it
    /// should be, since the curve it is judging is the corrected one.</para>
    /// </summary>
    /// <returns>False when the result is not an imported AC impedance.</returns>
    public async Task<bool> ReanalyzeImpedanceAsync(
        long resultId, AcImpedanceReanalyzeRequest request, CancellationToken ct)
    {
        var detail = await measurementRepository.GetResultAsync(resultId, ct);
        if (detail is null)
            return false;

        // Guarded, because this is reached from a button on a row and the row cannot know what shape
        // its own params_json is. Another kind's document does not merely lack the AC block -- it
        // fails to deserialize at all, with "expected number" out of a field that happens to share a
        // name, which would reach the user as a 500 instead of the sentence below.
        ImpedanceParameters? parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<ImpedanceParameters>(detail.ParamsJson, SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }

        if (parameters?.AcSimulation is not { } simulation)
            return false;

        var rows = await measurementRepository.GetResponseTracesAsync(resultId, ct);
        var stored = rows.FirstOrDefault(r => r.Kind == ResponseTraceKind.Impedance);
        if (stored is null)
            return false;

        double[] freqHz = [.. stored.FreqHz.Select(f => (double)f)];
        var z = new Complex[freqHz.Length];
        for (int k = 0; k < z.Length; k++)
        {
            z[k] = Complex.FromPolarCoordinates(
                ImpedanceMath.FromDbOhms(stored.MagnitudeMaxDb[k]),
                stored.PhaseDeg[k] * Math.PI / 180.0);
        }

        var facing = EnumParsingOrDefault(simulation.CurrentDirection);
        var wanted = request.CurrentDirection ?? facing;
        if (wanted == AcImpedanceCurrentDirection.Auto)
        {
            wanted = ImpedanceMath.NegativeRealFraction(z, 0, z.Length - 1) > 0.5
                ? Opposite(facing)
                : facing;
        }

        if (wanted != facing)
        {
            for (int k = 0; k < z.Length; k++)
                z[k] = -z[k];
        }

        var trace = ImpedanceTraceBuilder.BuildPoints(
            ImpedanceTraceKind.Impedance, freqHz, z, [.. Enumerable.Repeat(1.0, freqHz.Length)]);

        string paramsJson = JsonSerializer.Serialize(
            parameters with { AcSimulation = simulation with { CurrentDirection = wanted.ToString() } },
            SerializerOptions);

        await measurementRepository.ReplaceResponseTracesAsync(
            resultId,
            paramsJson,
            [MeasurementSaveService.ToImpedanceTraceInput(trace, ResponseTraceKind.Impedance)],
            ct);

        logger.LogInformation(
            "Re-analysed imported impedance {ResultId}: current {From} -> {To}, peak {PeakDb:F2} dB-ohm at {PeakHz:F1} Hz",
            resultId, facing, wanted, trace.PeakDbOhms, trace.PeakFreqHz);

        return true;
    }

    /// <summary>
    /// A row written before the direction was recorded says nothing about which way it faces, and
    /// <c>AsStored</c> is the honest reading: it is what the curve would have been called had the
    /// field existed, since flipping it is what the stored warning would have said.
    /// </summary>
    private static AcImpedanceCurrentDirection EnumParsingOrDefault(string? stored) =>
        Enum.TryParse(stored, out AcImpedanceCurrentDirection parsed) && parsed != AcImpedanceCurrentDirection.Auto
            ? parsed
            : AcImpedanceCurrentDirection.AsStored;

    private static AcImpedanceCurrentDirection Opposite(AcImpedanceCurrentDirection direction) =>
        direction == AcImpedanceCurrentDirection.Inverted
            ? AcImpedanceCurrentDirection.AsStored
            : AcImpedanceCurrentDirection.Inverted;

    /// <summary>
    /// The settings document an imported impedance carries. Most of <see cref="ImpedanceParameters"/>
    /// describes a bench fixture, and the honest answer for a V/I division is that there is none:
    /// no reference resistor, no calibration standards, no drive level. Zeros and nulls say that,
    /// where plausible-looking figures would claim a fixture that never existed — the reasoning
    /// behind the response import's <c>LevelMode: "NotApplicable"</c>.
    /// </summary>
    private string ImpedanceParamsJson(
        AcSimulationSource source, AcSimulationSweep sweep, AcImpedanceCurrentDirection direction) =>
        JsonSerializer.Serialize(
            new ImpedanceParameters(
                Method: ImpedanceMethod.ReferenceResistor.ToString(),
                Stimulus: ImpedanceStimulus.SteppedSine.ToString(),
                ReferenceOhms: 0.0,
                RightChannelIsReference: false,
                FrequencySpanHz: sweep.EndFreqHz,
                ResolutionMultiplier: 1,
                ChirpStartHz: sweep.StartFreqHz,
                LevelValue: 0.0,
                LevelUnit: "",
                SmoothingDenominator: 0,
                AverageCount: 1,
                CableMode: ImpedanceCableMode.None.ToString(),
                FixedCableOhms: 0.0,
                FixedCableMillihenry: 0.0,
                StartFreqHz: sweep.StartFreqHz,
                EndFreqHz: sweep.EndFreqHz)
            {
                AcSimulation = new AcSimulationParameters(
                    source, 0.0, sweep.StartFreqHz, sweep.EndFreqHz, direction.ToString()),
            },
            SerializerOptions);

    private static int RequireTraceIndex(LtspiceRawHeader header, int index, string what)
    {
        if (index < 0 || index >= header.Variables.Count)
            throw new ArgumentException($"There is no trace {index} to read the {what} from.");

        return index;
    }

    private static void RequireUnit(LtspiceVariable variable, string unit, string what, string path)
    {
        if (variable.Unit == unit)
            return;

        string isWhat = variable.Unit.Length == 0 ? $"a \"{variable.Type}\" trace" : $"in {variable.Unit}";
        throw new FormatException(
            $"{Path.GetFileName(path)}'s {variable.Name} is {isWhat}, not {what}. An impedance is volts over " +
            "amps, so this import needs one of each — a node voltage, and the current through the source " +
            "driving it.");
    }

    /// <summary>
    /// Re-analyses a stored AC simulation from its own curves at new settings, rewriting its
    /// <c>response_trace</c> rows and its <c>params_json</c>.
    ///
    /// <para>Nothing is re-read from the file and nothing is lost: the rows hold the sweep at full
    /// resolution, so this is a lossless re-reading of them.</para>
    /// </summary>
    /// <returns>False when the result is not an imported AC sweep.</returns>
    public async Task<bool> ReanalyzeAsync(long resultId, AcSimulationReanalyzeRequest request, CancellationToken ct)
    {
        var detail = await measurementRepository.GetResultAsync(resultId, ct);
        if (detail is null)
            return false;

        var stored = AcSimulationAnalysis.FromParamsJson(detail.ParamsJson, SerializerOptions);
        if (stored is null)
            return false;

        var rows = await measurementRepository.GetResponseTracesAsync(resultId, ct);
        var gainRow = rows.FirstOrDefault(r => r.Kind == ResponseTraceKind.Gain);
        var leftRow = rows.FirstOrDefault(r => r.Kind == ResponseTraceKind.Left);
        var rightRow = rows.FirstOrDefault(r => r.Kind == ResponseTraceKind.Right);
        if (gainRow is null)
            return false;

        var spec = stored.With(request);
        spec.Reference.Validate();

        if (spec.RightChannelIsReference != stored.RightChannelIsReference && (leftRow is null || rightRow is null))
        {
            throw new ArgumentException(
                "This result holds only one curve, so there is nothing to divide it by — which of two traces is " +
                "the reference can only be changed on a result that imported both. Import the file again with a " +
                "reference trace if you need it.");
        }

        double[] freqHz = [.. gainRow.FreqHz.Select(f => (double)f)];

        AcCurve outputCurve;
        AcCurve? referenceCurve;
        if (leftRow is not null && rightRow is not null)
        {
            // Both curves are stored raw, so the gain is rebuilt from them exactly as at import.
            outputCurve = ToCurve(leftRow);
            referenceCurve = ToCurve(rightRow);
        }
        else
        {
            // Only the gain was stored, and it carries whatever level offset it was stored with.
            // Taking that back off is what keeps the offset a setting rather than a one-way edit.
            outputCurve = ToCurve(gainRow);
            for (int i = 0; i < outputCurve.MagnitudeDb.Length; i++)
                outputCurve.MagnitudeDb[i] -= stored.LevelOffsetDb;
            referenceCurve = null;
        }

        var traces = BuildTraces(freqHz, outputCurve, referenceCurve, spec);
        await measurementRepository.ReplaceResponseTracesAsync(
            resultId, spec.ToParamsJson(SerializerOptions), traces, ct);

        logger.LogInformation(
            "Re-analysed AC simulation {ResultId} over {LowHz:F0}-{HighHz:F0} Hz, reference {Mode} " +
            "{RefLow:F0}-{RefHigh:F0} Hz, offset {Offset:F2} dB",
            resultId, spec.AnalysisLowHz, spec.AnalysisHighHz, spec.Reference.Mode,
            spec.Reference.LowHz, spec.Reference.HighHz, spec.LevelOffsetDb);

        return true;
    }

    /// <summary>One curve as everything downstream wants it: dB, and unwrapped degrees.</summary>
    private sealed record AcCurve(double[] MagnitudeDb, double[] PhaseDeg);

    private static AcCurve ToCurve(Complex[] values)
    {
        var magnitudeDb = new double[values.Length];
        var phaseDeg = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            magnitudeDb[i] = 20.0 * Math.Log10(Math.Max(Complex.Abs(values[i]), double.Epsilon));
            phaseDeg[i] = values[i].Phase * 180.0 / Math.PI;
        }

        PhaseUnwrapper.Unwrap(phaseDeg, phaseDeg);
        return new AcCurve(magnitudeDb, phaseDeg);
    }

    private static AcCurve ToCurve(StoredResponseTrace row) => new(
        [.. row.MagnitudeMaxDb.Select(v => (double)v)],
        [.. row.PhaseDeg.Select(v => (double)v)]);

    /// <summary>
    /// The gain phase, defined once so the import and every later re-analysis agree to the last
    /// digit.
    ///
    /// <para>Subtracting two unwrapped curves is not quite the same as unwrapping their ratio: the
    /// two differ by a whole number of turns. Group delay cannot see that — a constant
    /// differentiates away — but <b>phase delay divides the absolute phase by frequency</b>, so a
    /// stray 360° is a real error in the curve. Bringing sample 0 back into (−180°, 180°] pins the
    /// answer, and doing it on both paths means they cannot drift.</para>
    /// </summary>
    private static double[] CanonicalGainPhase(double[] outputDeg, double[] referenceDeg)
    {
        var phase = new double[outputDeg.Length];
        for (int i = 0; i < phase.Length; i++)
            phase[i] = outputDeg[i] - referenceDeg[i];

        double shift = Math.Ceiling((phase[0] - 180.0) / 360.0) * 360.0;
        if (shift != 0.0)
        {
            for (int i = 0; i < phase.Length; i++)
                phase[i] -= shift;
        }

        return phase;
    }

    /// <summary>
    /// The rows a result holds: a <c>gain</c> curve always — it is the transfer function, which is
    /// the whole point of the import — plus the two singles when a reference was imported, so the
    /// result keeps what it was derived from and can be re-analysed against a different one.
    /// </summary>
    private static List<ResponseTraceInput> BuildTraces(
        double[] freqHz, AcCurve output, AcCurve? reference, AcSimulationAnalysis spec)
    {
        (int firstIndex, int lastIndex) = BandIndices(freqHz, spec.AnalysisLowHz, spec.AnalysisHighHz);

        double[] gainDb;
        double[] gainPhase;
        if (reference is null)
        {
            gainDb = [.. output.MagnitudeDb];
            gainPhase = output.PhaseDeg;
        }
        else if (spec.RightChannelIsReference)
        {
            gainDb = Subtract(output.MagnitudeDb, reference.MagnitudeDb);
            gainPhase = CanonicalGainPhase(output.PhaseDeg, reference.PhaseDeg);
        }
        else
        {
            gainDb = Subtract(reference.MagnitudeDb, output.MagnitudeDb);
            gainPhase = CanonicalGainPhase(reference.PhaseDeg, output.PhaseDeg);
        }

        if (spec.LevelOffsetDb != 0.0)
        {
            for (int i = 0; i < gainDb.Length; i++)
                gainDb[i] += spec.LevelOffsetDb;
        }

        var traces = new List<ResponseTraceInput>
        {
            MeasurementSaveService.ToTraceInput(SteppedCaptureSupport.BuildPointTrace(
                FrequencyResponseTraceKind.Gain, freqHz, gainDb, gainPhase, spec.Reference,
                firstIndex, lastIndex)),
        };

        if (reference is not null)
        {
            // The two singles are stored as the simulator gave them, with no offset applied: the
            // offset is a claim about how the bench sees the output, not about the node, and
            // keeping them raw is what lets the gain be rebuilt from them at any later offset.
            traces.Add(MeasurementSaveService.ToTraceInput(SteppedCaptureSupport.BuildPointTrace(
                FrequencyResponseTraceKind.Left, freqHz, output.MagnitudeDb, output.PhaseDeg,
                reference: null, firstIndex, lastIndex)));
            traces.Add(MeasurementSaveService.ToTraceInput(SteppedCaptureSupport.BuildPointTrace(
                FrequencyResponseTraceKind.Right, freqHz, reference.MagnitudeDb, reference.PhaseDeg,
                reference: null, firstIndex, lastIndex)));
        }

        return traces;
    }

    private static double[] Subtract(double[] a, double[] b)
    {
        var result = new double[a.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = a[i] - b[i];
        return result;
    }

    /// <summary>
    /// The slice of the sweep the corner and reference searches run over. Returned as indices
    /// rather than applied to the arrays, because the whole curve is always stored — see
    /// <see cref="SteppedCaptureSupport.BuildPointTrace"/>'s own remark on why the band is a window
    /// and never a slice.
    /// </summary>
    private static (int FirstIndex, int LastIndex) BandIndices(double[] freqHz, double lowHz, double highHz)
    {
        int first = 0;
        while (first < freqHz.Length - 1 && freqHz[first] < lowHz)
            first++;

        int last = freqHz.Length - 1;
        while (last > first && freqHz[last] > highHz)
            last--;

        return (first, last);
    }

    private void AddCurveWarnings(
        List<string> warnings, double[] freqHz, Complex[]? reference, AcSimulationImportRequest request)
    {
        if (freqHz[0] > request.AnalysisLowHz || freqHz[^1] < request.AnalysisHighHz)
        {
            warnings.Add(
                $"The sweep covers {freqHz[0]:G4}-{freqHz[^1]:G4} Hz, which does not reach across the " +
                $"{request.AnalysisLowHz:G4}-{request.AnalysisHighHz:G4} Hz band being analysed. Corners outside " +
                "the sweep read as clamped. Widen the `.ac` directive if you need them.");
        }

        if (reference is null)
        {
            warnings.Add(
                "No reference trace was picked, so these levels are dB relative to 1 V. That is the circuit's " +
                "gain only if the simulator's AC source amplitude is 1 (LTspice's `AC 1` default); pick the input " +
                "node as the reference to be sure.");
            return;
        }

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (var value in reference)
        {
            double db = 20.0 * Math.Log10(Math.Max(Complex.Abs(value), double.Epsilon));
            min = Math.Min(min, db);
            max = Math.Max(max, db);
        }

        if (max - min > ReferenceSagToleranceDb)
        {
            warnings.Add(
                $"The reference trace varies by {max - min:F2} dB across the sweep, so it is loaded by the " +
                "circuit rather than being a stiff source. Dividing by it — which this import is doing — is " +
                "what makes the result the circuit's own gain.");
        }
    }

    /// <summary>
    /// How many points at the front of the sweep sit at 0 Hz, and are therefore left out of the
    /// import rather than refused with it.
    ///
    /// <para><c>.ac lin 1001 0 20k</c> is a reasonable thing to type and LTspice runs it, putting one
    /// DC point in front of an otherwise perfectly good sweep. DC is the one part of it that could
    /// never be drawn here — the chart's frequency axis is logarithmic, phase delay is
    /// <c>phase / f</c>, and <see cref="BandwidthAnalyzer"/> interpolates crossings in log f — so
    /// dropping it loses nothing and refusing the file costs the user the whole run. Without this,
    /// <see cref="GroupDelay.PhaseDelayOnGrid"/> was the thing that spoke for the import, with
    /// <i>"Phase delay is undefined at or below 0 Hz. (Parameter 'freqHz')"</i>.</para>
    ///
    /// <para>This is a <b>slice of the source</b>, applied before any trace is built, and not the
    /// band window <see cref="SteppedCaptureSupport.BuildPointTrace"/> takes — a window is a search
    /// range that narrowing and widening must return from unchanged, where this really does remove a
    /// point. It is applied identically in <c>Inspect</c> and <c>ImportAsync</c>, and re-analysis
    /// reads the stored rows, which no longer hold the point, so all three agree with no second code
    /// path.</para>
    /// </summary>
    private static int LeadingDcPoints(ReadOnlySpan<double> freqHz)
    {
        int skipped = 0;
        while (skipped < freqHz.Length && freqHz[skipped] == 0.0)
            skipped++;

        return skipped;
    }

    /// <summary>
    /// Refuses anything that is not a readable AC sweep, naming what the file is and, where the
    /// mistake is simply the wrong screen, where it does belong.
    /// </summary>
    private static void RequireAc(LtspiceRawHeader header, string path)
    {
        string name = Path.GetFileName(path);

        if (header.Analysis != LtspiceAnalysis.AcAnalysis)
        {
            string what = header.Analysis switch
            {
                LtspiceAnalysis.Transient =>
                    "a transient (.tran) run, which is a waveform rather than a frequency response. Open it on " +
                    "an FFT analysis measurement instead",
                LtspiceAnalysis.OperatingPoint =>
                    "an operating-point file (the .op.raw or .qopraw the simulator writes beside every run), " +
                    "which holds one solution and no sweep",
                LtspiceAnalysis.Fft =>
                    "an FFT export, which is a spectrum of a transient run. Import the transient run itself on " +
                    "an FFT analysis measurement",
                _ => $"a \"{header.Plotname}\" run",
            };
            throw new FormatException($"{name} is {what}. This import reads an AC (.ac) sweep.");
        }

        if (!header.IsComplex)
        {
            throw new FormatException(
                $"{name} says it is an AC analysis but its data is not complex, so it carries no phase. " +
                "Re-run the simulation.");
        }

        if (header.FrequencyVariable is null)
            throw new FormatException($"{name} has no frequency variable, so it is not a sweep.");

        if (header.PointCount < 2)
            throw new FormatException($"{name} holds {header.PointCount} point, which is not a sweep.");

        // The point cap is deliberately *not* here: `No. Points` is the grand total over every sweep
        // of a stepped run, so a cap applied to it refuses a file whose individual sweeps are small
        // -- 771 points stepped 85 times reaches 65 535. It is checked per sweep in SplitSweeps,
        // which is the number that decides how much is stored.
    }
}
