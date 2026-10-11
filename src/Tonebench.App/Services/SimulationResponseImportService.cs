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
    IReadOnlyList<string> Warnings);

/// <summary>What to import from an <c>.ac</c> run, and how to analyse it.</summary>
/// <param name="ReferenceTraceIndex">
/// The node to divide the output by, or null to take the output as the transfer function on its
/// own. Dividing is what a bench measurement does, and it is the only way to be right when the
/// simulator's AC source is not exactly 1 — measured on the QSpice reference circuit, whose source
/// sags to 0.9914 at 1 kHz, that is 0.075 dB.
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
    string? Notes);

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
        double[] freqHz = [.. frequency.Select(c => c.Real)];
        var axis = AcFrequencyAxis.Of(freqHz);

        if (!axis.IsMonotonic)
        {
            throw new FormatException(
                $"{Path.GetFileName(path)} has a frequency axis that goes backwards at point " +
                $"{axis.FirstNonMonotonicIndex}. A stepped (`.step`) run writes several sweeps into one file, " +
                "which this import cannot separate; run a single simulation instead.");
        }

        var warnings = new List<string>();

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
                "points -- a -3 dB corner can be located no more closely than that. Raise the density, e.g. " +
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
            Warnings: warnings);
    }

    /// <summary>Imports the selected traces into <paramref name="measurementTypeId"/>.</summary>
    public async Task<long> ImportAsync(
        long measurementTypeId, AcSimulationImportRequest request, CancellationToken ct)
    {
        var info = Inspect(request.Path, ct);
        var header = LtspiceRawFile.ReadHeader(request.Path);
        request.Reference.Validate();

        int frequencyIndex = header.FrequencyVariable!.Index;
        var indices = new List<int> { frequencyIndex, request.OutputTraceIndex };
        bool hasReference = request.ReferenceTraceIndex is { } referenceIndex
            && referenceIndex != request.OutputTraceIndex;
        if (hasReference)
            indices.Add(request.ReferenceTraceIndex!.Value);

        var columns = LtspiceRawFile.ReadComplexColumns(request.Path, header, indices, ct);
        double[] freqHz = [.. columns[0].Select(c => c.Real)];
        Complex[] output = columns[1];
        Complex[]? reference = hasReference ? columns[2] : null;

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
            PointCount: info.PointCount,
            StartFreqHz: info.StartFreqHz,
            EndFreqHz: info.EndFreqHz,
            PointsPerDecade: info.PointsPerDecade,
            Warnings: warnings);

        var spec = new AcSimulationAnalysis(
            source, request.LevelOffsetDb, request.AnalysisLowHz, request.AnalysisHighHz,
            request.Reference, RightChannelIsReference: hasReference);

        // The curves the rows are built from, in the same shape a re-analysis will read back: dB
        // and *unwrapped* degrees. Going through the same two arrays at import is what makes the
        // two paths produce identical numbers -- see CanonicalGainPhase.
        var outputCurve = ToCurve(output);
        var referenceCurve = reference is null ? null : ToCurve(reference);
        var traces = BuildTraces(freqHz, outputCurve, referenceCurve, spec);

        string name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{Path.GetFileNameWithoutExtension(request.Path)} - {source.OutputTrace}"
            : request.Name.Trim();

        long resultId = await measurementRepository.SaveResponseResultAsync(
            new ResponseSaveRequest(
                measurementTypeId, name, request.Notes, spec.ToParamsJson(SerializerOptions),
                DeviceName: null, Serial: null, FwVer: null, IsSimulated: true,
                // A simulated sweep has no sample rate and no record length: it is N points of
                // exact arithmetic. Zero rather than a plausible-looking figure, for the reason
                // the transient import writes a silenced generator.
                SampleRateHz: 0.0, FftSize: info.PointCount,
                Channels: hasReference ? 2 : 1,
                InRangeDbv: null, OutRangeDbv: null,
                Traces: traces,
                PointPreviews: null,
                Source: MeasurementSource.Simulation),
            ct);

        logger.LogInformation(
            "Imported AC simulation {Name} from {Path}: {Output}{Reference}, {Points} points over " +
            "{StartHz:F3}-{EndHz:F0} Hz ({PerDecade:F0}/decade), band {LowHz:F0}-{HighHz:F0} Hz",
            name, request.Path, source.OutputTrace,
            source.ReferenceTrace is null ? "" : $" / {source.ReferenceTrace}",
            info.PointCount, info.StartFreqHz, info.EndFreqHz, info.PointsPerDecade,
            spec.AnalysisLowHz, spec.AnalysisHighHz);

        return resultId;
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
                "This result holds only one curve, so there is nothing to divide it by -- which of two traces is " +
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
                "circuit rather than being a stiff source. Dividing by it -- which this import is doing -- is " +
                "what makes the result the circuit's own gain.");
        }
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
                    "an operating-point file (the .op.raw that the simulator writes beside every run), which " +
                    "holds one solution and no sweep",
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

        if (header.PointCount > AcSimulationAnalysis.MaxPoints)
        {
            throw new FormatException(
                $"{name} holds {header.PointCount:N0} points, more than the {AcSimulationAnalysis.MaxPoints:N0} " +
                "this import stores. A `.ac dec 100` sweep of the audio band is a few hundred; use a lower " +
                "density, or a narrower range.");
        }
    }
}
