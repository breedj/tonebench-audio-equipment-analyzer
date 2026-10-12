using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.App.Services;

/// <summary>One column of a <c>.raw</c> file as the import dialog offers it.</summary>
public sealed record SimulationTrace(int Index, string Name, string Unit);

/// <summary>
/// What <see cref="SimulationImportService.Inspect"/> found in a <c>.raw</c> file: enough for the
/// dialog to offer its trace pickers, show the arithmetic the user's choices imply, and say what is
/// wrong with the file before anything is committed.
/// </summary>
/// <summary>
/// One run inside a <c>.raw</c> file. An ordinary transient holds exactly one; a <c>.step</c>ped one
/// holds a run per step, written end to end with the time axis restarting at each.
/// </summary>
/// <param name="Label">
/// What the simulator called this step, e.g. <c>freq=2000</c>, from the sibling <c>.log</c>. Null
/// when there is nothing to read it from — every QSpice run, which writes no log at all.
/// </param>
public sealed record SimulationSweep(
    int Index,
    int FirstPoint,
    int PointCount,
    double StartTimeS,
    double EndTimeS,
    string? Label)
{
    public double DurationS => EndTimeS - StartTimeS;
}

public sealed record SimulationFileInfo(
    string Path,
    string Title,
    string? Date,
    string Plotname,
    IReadOnlyList<string> Flags,
    int PointCount,
    double StartTimeS,
    double EndTimeS,
    double DurationS,
    double NativeRateHz,
    double MinStepS,
    double MedianStepS,
    double MaxStepS,
    IReadOnlyList<SimulationTrace> Traces,
    IReadOnlyList<string> Warnings,
    /// <summary>
    /// The runs the file holds, always at least one. The figures above describe the <b>first</b>:
    /// on a stepped run every step is the same directive solved at a different parameter, so its
    /// duration and its grid describe them all closely enough for the dialog's arithmetic, and each
    /// sweep carries its own span regardless.
    /// </summary>
    IReadOnlyList<SimulationSweep> Sweeps)
{
    /// <summary>The largest power-of-two FFT the run's duration can fill at its own native rate.</summary>
    public int LargestUsableFftSize => SimulationAnalysis.LargestFftSize(DurationS, NativeRateHz);
}

/// <summary>What to import, and how to analyse it. Every field here is re-appliable afterwards.</summary>
public sealed record SimulationImportRequest(
    string Path,
    int LeftTraceIndex,
    int? RightTraceIndex,
    double StartTimeS,
    double LevelOffsetDb,
    int FftSize,
    WindowType Window,
    double MeasurementLoFreqHz,
    double MeasurementHiFreqHz,
    string? Name,
    string? Notes,
    bool PeriodicRecord = true,
    /// <summary>
    /// Which runs of a <c>.step</c>ped file to import, or null for all of them. On an FFT
    /// measurement each becomes a result of its own; on a THD-vs-frequency measurement they become
    /// the points of one sweep.
    /// </summary>
    IReadOnlyList<int>? SweepIndices = null);

/// <summary>
/// Imports an LTspice transient run as a measurement result, and re-analyses one that is already
/// stored.
///
/// <para>The shape of it: the selected columns are put on a uniform grid at the simulator's own
/// rate and stored as <c>capture.pcm</c>, which is the durable part; everything else -- the window,
/// the FFT size, the level offset, the analysis band, the start time -- is applied on the way to a
/// <c>spectrum</c> row and can be changed afterwards without the file, which matters because an
/// LTspice <c>.raw</c> is overwritten by the next run and deleted without warning.</para>
///
/// <para>The analysis itself is <see cref="SpectrumRecordAnalyzer"/>, i.e. the same arithmetic the
/// live FFT screen runs, so an imported simulation's THD is the number the screen would have shown
/// for the same samples.</para>
/// </summary>
public sealed class SimulationImportService(
    MeasurementRepository measurementRepository,
    IOptions<JsonOptions> jsonOptions,
    ILogger<SimulationImportService> logger)
{
    private readonly IFftBackend _fft = new FftFlatBackend();

    // The app's own options, so a stored params_json round-trips identically to every other one --
    // the same reason MeasurementSaveService takes them rather than making its own.
    private JsonSerializerOptions SerializerOptions => jsonOptions.Value.SerializerOptions;

    /// <summary>
    /// Reads the header and the time axis, and says what is wrong with the file. Nothing is
    /// committed and no trace data is read beyond the time column.
    /// </summary>
    /// <exception cref="FileNotFoundException">No file at the path.</exception>
    /// <exception cref="FormatException">Not a readable LTspice transient run; the message says why.</exception>
    public SimulationFileInfo Inspect(
        string path,
        double measurementHiFreqHz = 20_000.0,
        double measurementLoFreqHz = 20.0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("No such file.", path);

        var header = LtspiceRawFile.ReadHeader(path);
        RequireTransient(header, path);

        double[] time = LtspiceRawFile.ReadColumns(path, header, [header.TimeVariable!.Index], ct)[0];
        var sweeps = SplitSweeps(time, header, path);

        // Every check below is about one run, so they are applied to the first -- on a stepped file
        // the others are the same directive solved at a different parameter, so a timestep that can
        // carry the band in one carries it in all of them.
        var first = sweeps[0];
        var axis = TimeAxis.Of(time.AsSpan(first.FirstPoint, first.PointCount));

        var warnings = new List<string>();

        if (sweeps.Count > 1)
        {
            warnings.Add(
                $"This is a stepped (`.step`) run: {sweeps.Count} runs in one file." +
                (sweeps[^1].Label is null
                    ? " They are numbered, because the simulator records what it stepped only on the console."
                    : ""));
        }

        // The hard test, and it is about the data rather than the flags: a gap of `maxStep` cannot
        // represent anything above 1/(2*maxStep), so a file whose coarsest step cannot carry the
        // top of the measurement band cannot be analysed over that band at all. Measured on a real
        // compressed run: 537 us of gap, i.e. 930 Hz, against a 20 kHz band.
        double representableHz = 0.5 / Math.Max(axis.MaxStepS, double.Epsilon);
        if (representableHz < measurementHiFreqHz)
        {
            // Advice in the writer's own dialect. LTspice's usual cause is waveform compression,
            // which it announces by *not* setting `nocompression`; QSpice has no such flag and its
            // usual cause is simply a transient directive with no maximum timestep.
            string why = header.Flavour switch
            {
                SpiceFlavour.LTspice when !header.IsUncompressed =>
                    "It was saved with LTspice's waveform compression on, which discards most of the samples -- " +
                    "add `.options plotwinsize=0` to the schematic and run it again.",
                SpiceFlavour.QSpice =>
                    $"Its coarsest timestep is {axis.MaxStepS * 1e6:F1} us, which can only carry " +
                    $"{representableHz / 1e3:F1} kHz. QSpice chooses its own steps unless told otherwise -- give " +
                    "the transient directive a maximum step, e.g. `.tran 0 100m 0 1u`.",
                _ =>
                    $"Its coarsest timestep is {axis.MaxStepS * 1e6:F1} us, which can only carry " +
                    $"{representableHz / 1e3:F1} kHz. Re-run it with a maximum timestep fine enough for the band.",
            };
            throw new FormatException(
                $"{Path.GetFileName(path)} is too coarsely sampled to measure to {measurementHiFreqHz / 1e3:F1} kHz. {why}");
        }

        if (header.Flavour == SpiceFlavour.LTspice && !header.IsUncompressed)
        {
            warnings.Add(
                "This run was saved with LTspice's waveform compression on (`.options plotwinsize=0` switches it " +
                "off). Samples have been discarded, and distortion figures read high as a result.");
        }

        // Resolution. The bin width of the finished spectrum is 1/duration and nothing else can
        // improve it -- not the FFT size, not the simulator's timestep -- so a run that is simply
        // too short has to say so here, where the fix (simulate for longer) is still available.
        // Judged against the *bottom* of the measurement band, which is what binds: at 10.5 Hz
        // bins a 20 Hz band edge is two bins wide, while 1 kHz is ninety-five.
        ResolutionWarning(axis.DurationS, measurementLoFreqHz, warnings);

        if (header.Flavour == SpiceFlavour.LTspice && !header.ValuesAreDouble)
        {
            warnings.Add(
                "Saved at single precision. That is almost always fine -- measured, it changes harmonic levels by " +
                "nothing down to -110 dBc -- but `.options numdgt=7` writes full precision if you need it.");
        }

        double resampleRateHz = axis.ResampleRateHz(measurementHiFreqHz);
        if (axis.IsAdaptive(measurementHiFreqHz))
        {
            warnings.Add(
                $"This run uses adaptive timesteps, from {axis.MinStepS * 1e12:G3} ps to " +
                $"{axis.MaxStepS * 1e6:F1} us. It will be resampled at {resampleRateHz / 1e6:F2} MHz, which is " +
                "far more than the audio band needs -- but a run with a fixed maximum timestep is easier to " +
                "trust, and much smaller. Give the transient directive a maximum step, e.g. " +
                "`.tran 0 <stop> <start> 1u`.");
        }

        var traces = header.Variables
            .Where(v => !v.IsTime)
            .Select(v => new SimulationTrace(v.Index, v.Name, v.Unit))
            .ToList();
        if (traces.Count == 0)
            throw new FormatException($"{Path.GetFileName(path)} carries no traces besides time.");

        return new SimulationFileInfo(
            Path: Path.GetFullPath(path),
            Title: header.Title,
            Date: header.Date,
            Plotname: header.Plotname,
            Flags: [.. header.Flags],
            PointCount: first.PointCount,
            StartTimeS: axis.StartS,
            EndTimeS: axis.EndS,
            DurationS: axis.DurationS,
            NativeRateHz: resampleRateHz,
            MinStepS: axis.MinStepS,
            MedianStepS: axis.MedianStepS,
            MaxStepS: axis.MaxStepS,
            Traces: traces,
            Warnings: warnings,
            Sweeps: sweeps);
    }

    /// <summary>
    /// Splits a file into the runs it holds and names each one, which is what makes a
    /// <c>.step</c>ped transient readable instead of refused.
    ///
    /// <para><see cref="LtspiceRawFile.SweepStarts"/> cuts wherever the axis fails to increase, so
    /// every segment is strictly increasing by construction and there is no monotonicity left to
    /// check. The header's <c>stepped</c> flag is what tells a stepped run from a damaged one: both
    /// simulators set it, so a time axis that restarts without it is a corrupt or rewritten file,
    /// and turning that into several plausible-looking results would be worse than refusing it.</para>
    ///
    /// <para>The sweeps are <b>not the same length</b> — measured on a generated
    /// <c>.step param freq 1k 3k 1k</c> run, 5353 / 5682 / 5964 points from LTspice and
    /// 5311 / 5604 / 5949 from QSpice, because each run takes the timesteps it needs.</para>
    /// </summary>
    private static List<SimulationSweep> SplitSweeps(double[] time, LtspiceRawHeader header, string path)
    {
        string name = Path.GetFileName(path);
        var starts = LtspiceRawFile.SweepStarts(time);

        if (starts.Length > 1 && !header.IsStepped)
        {
            throw new FormatException(
                $"{name} has a time axis that goes backwards at point {starts[1]}, and its header does not say " +
                "it is a stepped (`.step`) run -- a stepped file from either simulator carries a `stepped` flag, " +
                "and this one does not. Re-run the simulation.");
        }

        var labels = starts.Length > 1 ? SpiceStepLog.ReadLabels(path) : [];
        var sweeps = new List<SimulationSweep>(starts.Length);

        for (int s = 0; s < starts.Length; s++)
        {
            int from = starts[s];
            int upTo = s + 1 < starts.Length ? starts[s + 1] : time.Length;
            int count = upTo - from;
            string where = starts.Length > 1 ? $" in run {s + 1} of {starts.Length}" : "";

            if (count < 2)
                throw new FormatException($"{name} has {count} point{where}, which is not a waveform.");

            sweeps.Add(new SimulationSweep(
                Index: s,
                FirstPoint: from,
                PointCount: count,
                StartTimeS: time[from],
                EndTimeS: time[upTo - 1],
                Label: s < labels.Count ? labels[s] : null));
        }

        return sweeps;
    }

    /// <summary>
    /// Adds a warning when the run is too short to resolve the bottom of the measurement band.
    /// </summary>
    /// <remarks>
    /// Two thresholds rather than one, so this does not cry wolf. Below a quarter of the band's
    /// lowest frequency the resolution is merely coarse down there and the rest of the spectrum is
    /// unaffected; at half or worse there are fewer than two bins below the band edge and the
    /// figures that integrate over the band stop meaning much.
    /// </remarks>
    private static void ResolutionWarning(double durationS, double measurementLoFreqHz, List<string> warnings)
    {
        if (durationS <= 0.0 || measurementLoFreqHz <= 0.0)
            return;

        double binHz = 1.0 / durationS;
        double binsBelowBandEdge = measurementLoFreqHz / binHz;
        if (binsBelowBandEdge >= 4.0)
            return;

        // What a comfortable four bins would take.
        double neededS = 4.0 / measurementLoFreqHz;
        string severity = binsBelowBandEdge < 2.0
            ? $"too coarse to resolve the bottom of the band: {measurementLoFreqHz:G4} Hz is " +
              $"{binsBelowBandEdge:F1} bins in"
            : $"coarse at the bottom of the band: {measurementLoFreqHz:G4} Hz is only " +
              $"{binsBelowBandEdge:F1} bins in";

        warnings.Add(
            $"This run saves {durationS * 1e3:F0} ms, which gives {binHz:F2} Hz resolution -- {severity}. " +
            $"Resolution is 1 divided by the saved length and no setting here can improve it, so save at " +
            $"least {neededS * 1e3:F0} ms (`.tran 0 <stop> <start>` with stop - start >= {neededS * 1e3:F0} ms) " +
            "if you need the low end.");
    }

    /// <summary>
    /// Imports the selected traces into <paramref name="measurementTypeId"/>, one result per run.
    ///
    /// <para>An ordinary file returns one id; a <c>.step</c>ped one returns an id per step it was
    /// asked for, each a spectrum of its own. For a stepped run whose steps are a tone moving across
    /// the band, <see cref="ImportSweepAsync"/> is the other thing to do with the same file.</para>
    /// </summary>
    public async Task<IReadOnlyList<long>> ImportAsync(
        long measurementTypeId, SimulationImportRequest request, CancellationToken ct)
    {
        var info = Inspect(
            request.Path, request.MeasurementHiFreqHzOrDefault(), request.MeasurementLoFreqHz, ct);
        var header = LtspiceRawFile.ReadHeader(request.Path);
        var chosen = ResolveSweeps(info, request.SweepIndices);

        var indices = new List<int> { header.TimeVariable!.Index, request.LeftTraceIndex };
        if (request.RightTraceIndex is { } right && right != request.LeftTraceIndex)
            indices.Add(right);

        // One pass over the file whatever the run count: the columns hold every run end to end, and
        // each SimulationSweep is the window into them.
        double[][] columns = LtspiceRawFile.ReadColumns(request.Path, header, indices, ct);

        var resultIds = new List<long>(chosen.Count);
        foreach (var sweep in chosen)
            resultIds.Add(await ImportRunAsync(measurementTypeId, request, info, header, indices, columns, sweep, ct));

        return resultIds;
    }

    /// <summary>Which runs an import asked for, refusing an index the file does not have.</summary>
    private static List<SimulationSweep> ResolveSweeps(SimulationFileInfo info, IReadOnlyList<int>? requested)
    {
        if (requested is null || requested.Count == 0)
            return [.. info.Sweeps];

        var chosen = new List<SimulationSweep>(requested.Count);
        foreach (int index in requested.Distinct().Order())
        {
            if (index < 0 || index >= info.Sweeps.Count)
            {
                throw new ArgumentException(
                    $"This file holds {info.Sweeps.Count} run{(info.Sweeps.Count == 1 ? "" : "s")}, so there is " +
                    $"no run {index + 1} to import.");
            }

            chosen.Add(info.Sweeps[index]);
        }

        return chosen;
    }

    private async Task<long> ImportRunAsync(
        long measurementTypeId,
        SimulationImportRequest request,
        SimulationFileInfo info,
        LtspiceRawHeader header,
        IReadOnlyList<int> indices,
        double[][] wholeColumns,
        SimulationSweep sweep,
        CancellationToken ct)
    {
        var (spec, rendered, samples) = AnalyseRun(request, info, header, indices, wholeColumns, sweep, ct);

        string stem = string.IsNullOrWhiteSpace(request.Name)
            ? $"{Path.GetFileNameWithoutExtension(request.Path)} - {spec.Source.LeftTrace}"
            : request.Name.Trim();
        string name = info.Sweeps.Count <= 1
            ? stem
            : $"{stem} - {sweep.Label ?? $"step {sweep.Index + 1} of {info.Sweeps.Count}"}";

        long resultId = await measurementRepository.SaveSimulationResultAsync(
            new SimulationSaveRequest(
                measurementTypeId, name, request.Notes, spec.ToParamsJson(SerializerOptions, rendered.AnalysisRateHz),
                rendered.AnalysisRateHz, spec.FftSize, info.NativeRateHz,
                samples, rendered.Spectra, rendered.Metrics),
            ct);

        logger.LogInformation(
            "Imported simulation {Name} from {Path}: {Traces}, run {Run} of {Runs}, {Points} points over " +
            "{Duration:F3} ms, native {NativeKHz:F1} kHz, analysed at {AnalysisKHz:F1} kHz / {FftSize} points " +
            "({BinHz:F2} Hz bins)",
            name, request.Path, spec.Source.LeftTrace, sweep.Index + 1, info.Sweeps.Count,
            sweep.PointCount, sweep.DurationS * 1e3, info.NativeRateHz / 1e3,
            rendered.AnalysisRateHz / 1e3, spec.FftSize, rendered.AnalysisRateHz / spec.FftSize);

        return resultId;
    }

    /// <summary>
    /// Imports a <c>.step</c>ped transient run as a <b>THD vs frequency</b> sweep: one point per
    /// step, measured the way the live screen would measure it.
    ///
    /// <para>This is what a stepped <c>.tran</c> is usually <i>for</i>. A tone stepped across the
    /// band and run through a transient analysis is a distortion-against-frequency measurement
    /// written in another form, and reading it as a stack of spectra leaves the user to compare
    /// eight charts by eye when the thing they wanted is one curve.</para>
    ///
    /// <para><b>Each point's x value is the fundamental the analysis located</b>, not a frequency
    /// taken from the step's label — which is why this needs no <c>.log</c> at all, and why a
    /// QSpice run (which writes none) works identically. It is also the same convention a real
    /// sweep uses: <c>ThdSweepPoint.MeasuredFreqHz</c> exists precisely because the tone that
    /// arrives is the one worth believing.</para>
    ///
    /// <para>The points are built by <see cref="SweepCaptureSupport.PointFromMeters"/>, the same
    /// function the real runner uses, so a simulated sweep and a measured one are the same
    /// arithmetic over the same metric names — and read back, exported and reported by code that
    /// cannot tell them apart.</para>
    /// </summary>
    public async Task<long> ImportSweepAsync(
        long measurementTypeId, SimulationImportRequest request, CancellationToken ct)
    {
        var info = Inspect(
            request.Path, request.MeasurementHiFreqHzOrDefault(), request.MeasurementLoFreqHz, ct);
        var header = LtspiceRawFile.ReadHeader(request.Path);
        var chosen = ResolveSweeps(info, request.SweepIndices);

        if (chosen.Count < 2)
        {
            throw new ArgumentException(
                $"A sweep needs more than one point, and this file holds {chosen.Count} run" +
                $"{(chosen.Count == 1 ? "" : "s")}. A `.step` over the tone frequency is what makes a transient " +
                "run into a sweep -- e.g. `.step param freq 1k 10k 1k` beside a `.tran`. Import it on an FFT " +
                "analysis measurement instead to read it as a spectrum.");
        }

        var indices = new List<int> { header.TimeVariable!.Index, request.LeftTraceIndex };
        if (request.RightTraceIndex is { } right && right != request.LeftTraceIndex)
            indices.Add(right);

        double[][] wholeColumns = LtspiceRawFile.ReadColumns(request.Path, header, indices, ct);
        var traceNames = indices.Skip(1).Select(i => header.Variables[i].Name).ToList();
        var traceUnits = indices.Skip(1).Select(i => header.Variables[i].Unit).ToList();

        SimulationAnalysis? firstSpec = null;
        var points = new List<ThdSweepPoint>();
        var runs = new List<List<ThdSweepPoint>>(chosen.Count);
        double analysisRateHz = 0.0;
        int fftSize = 0;

        foreach (var sweep in chosen)
        {
            ct.ThrowIfCancellationRequested();

            var (spec, rendered, samples) = AnalyseRun(request, info, header, indices, wholeColumns, sweep, ct);

            // The *first* run's, not the last. A periodic record nudges the analysis rate to fit
            // whole cycles of that point's own tone, so every point has a slightly different one and
            // no single figure is the sweep's -- taking the first makes the stored figure
            // deterministic rather than "whichever step happened to be last".
            if (firstSpec is null)
            {
                firstSpec = spec;
                analysisRateHz = rendered.AnalysisRateHz;
                fftSize = spec.FftSize;
            }

            var runPoints = new List<ThdSweepPoint>(samples.Count);
            for (int channel = 0; channel < samples.Count; channel++)
            {
                var meters = rendered.Meters[channel];
                RequireLocatedTone(meters, sweep, chosen.Count, request, channel);

                runPoints.Add(SweepCaptureSupport.PointFromMeters(
                    // The located fundamental is the point's own frequency as well as its reading:
                    // a simulation has no "frequency that was asked for" to carry separately, since
                    // the step's label is a parameter value rather than a tone.
                    freqHz: meters.FundamentalFreqHz,
                    channel: channel,
                    meters: meters,
                    // An AC-coupled simulated source has no generator level this side of the
                    // schematic, so gain is relative to the fundamental itself -- i.e. 0 dB -- and
                    // the chart's "input voltage" axis has nothing to show. Said once here rather
                    // than invented per point.
                    generatorAmpDbv: meters.FundamentalDbv,
                    loadOhms: 0.0,
                    noiseVrms: 0.0,
                    previewFft: rendered.Previews[channel],
                    previewScope: null));
            }

            runs.Add(runPoints);
            points.AddRange(runPoints);
        }

        RequireDistinctTones(runs, chosen);

        // One capture per *run*, not per measured frequency. A run is a sweep point by
        // construction, where the frequency is a reading -- and two runs can read the same one,
        // which grouping by it turns into a capture holding two points per channel and a
        // "UNIQUE constraint failed: capture_preview" long after the import. Reported 2026-10-12 as
        // a 500 naming two 'fft' previews at 250 Hz, from a run whose tone sat below the
        // fundamental search floor and locked onto the next run's frequency.
        var ordered = runs.OrderBy(run => run[0].FreqHz).ToList();
        var spec0 = firstSpec!;
        string paramsJson = SweepParamsJson(spec0, points.OrderBy(p => p.FreqHz).ToList(), traceNames, analysisRateHz, fftSize);

        var captures = ordered
            .Select((run, index) => new SweepCaptureInput(
                index,
                run[0].FreqHz,
                MeasurementSaveService.BuildSweepMetrics(run),
                MeasurementSaveService.BuildSweepPreviews(run)))
            .ToList();

        string name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{Path.GetFileNameWithoutExtension(request.Path)} - {traceNames[0]}"
            : request.Name.Trim();

        long resultId = await measurementRepository.SaveSweepResultAsync(
            new SweepSaveRequest(
                measurementTypeId, name, request.Notes, paramsJson,
                DeviceName: null, Serial: null, FwVer: null, IsSimulated: true,
                analysisRateHz, fftSize,
                Channels: traceNames.Count,
                InRangeDbv: null, OutRangeDbv: null,
                Captures: captures,
                Source: MeasurementSource.Simulation),
            ct);

        logger.LogInformation(
            "Imported stepped simulation {Name} from {Path} as a sweep: {Traces}, {Points} points over " +
            "{LowHz:F1}-{HighHz:F1} Hz, analysed at {AnalysisKHz:F1} kHz / {FftSize} points",
            name, request.Path, string.Join(" + ", traceNames), captures.Count,
            captures[0].FreqHz, captures[^1].FreqHz, analysisRateHz / 1e3, fftSize);

        return resultId;
    }

    /// <summary>
    /// A <c>ThdVsFrequency</c> settings document for an imported sweep. The run settings that exist
    /// are filled from the analysis; the ones that describe a generator do not exist here and say
    /// so, the same way the AC import writes <c>LevelMode: "NotApplicable"</c> rather than a
    /// plausible-looking level.
    /// </summary>
    private string SweepParamsJson(
        SimulationAnalysis spec,
        IReadOnlyList<ThdSweepPoint> points,
        IReadOnlyList<string> traceNames,
        double analysisRateHz,
        int fftSize)
    {
        double lowHz = points[0].FreqHz;
        double highHz = points[^1].FreqHz;

        // The grid the steps happened to land on, reported rather than prescribed: a `.step` is a
        // parameter list, so its points need not be octave-spaced at all, and this is what they came
        // out as. Zero when there is no span to measure one over.
        double octaves = lowHz > 0.0 && highHz > lowHz ? Math.Log2(highHz / lowHz) : 0.0;
        int stepsPerOctave = octaves > 0.0 ? (int)Math.Round((points.Count - 1) / octaves) : 0;

        return JsonSerializer.Serialize(
            new ThdVsFrequencyParameters(
                StartFreqHz: lowHz,
                EndFreqHz: highHz,
                StepsPerOctave: Math.Max(stepsPerOctave, 1),
                LevelMode: "NotApplicable",
                LevelValue: 0.0,
                LevelUnit: "",
                LoadOhms: 0.0,
                ProcessLeft: true,
                ProcessRight: traceNames.Count > 1,
                FftSize: fftSize,
                Window: spec.Window,
                AverageCount: 1,
                SampleRateHz: analysisRateHz,
                MeasureNoiseFloor: false)
            {
                Simulation = spec.ToParameters(analysisRateHz).Simulation,
            },
            SerializerOptions);
    }

    /// <summary>
    /// Refuses a run whose tone the analysis could not find, rather than storing what it found
    /// instead.
    ///
    /// <para><b>A negative SNR is the whole test</b>, and it is exact rather than a heuristic: SNR
    /// here is the located fundamental against everything else in the band, so at or below 0 dB the
    /// thing being called the fundamental is quieter than the rest of the record — which no tone
    /// is. Measured on the case that prompted this: a 50 Hz step analysed with a 200 Hz search
    /// floor read <c>-86.07 dBV, SNR -95.4 dB</c> at 250 Hz, against <c>9.36 dBV, SNR +57 dB</c>
    /// for the steps that were found.</para>
    /// </summary>
    private static void RequireLocatedTone(
        // [NotNull] so the caller may read the meters straight afterwards: this method either
        // returns with them non-null or throws, which the compiler cannot see through the pattern.
        [NotNull] SpectrumMeters? meters, SimulationSweep sweep, int runCount, SimulationImportRequest request, int channel)
    {
        if (meters is { SnrDb: > 0.0f })
            return;

        string where = $"{StepName(sweep, runCount)}{(channel == 0 ? "" : " (right trace)")}";
        throw new FormatException(
            $"No tone was found in {where}: either that run produced nothing, or the loudest thing in the " +
            "analysed band is quieter than the rest of it, which no tone is. The band is " +
            $"{request.MeasurementLoFreqHz:G4}-{request.MeasurementHiFreqHzOrDefault():G4} Hz, and a tone " +
            "outside it cannot be measured -- widen the band, or untick that run and import the rest.");
    }

    /// <summary>
    /// Refuses a set of runs that do not sweep anything, which is what a <c>.step</c> over something
    /// other than the tone produces: N runs of the same frequency, i.e. N points stacked on one x.
    /// Those are a perfectly good set of spectra, so the message says where to take them.
    /// </summary>
    private static void RequireDistinctTones(
        IReadOnlyList<List<ThdSweepPoint>> runs, IReadOnlyList<SimulationSweep> sweeps)
    {
        for (int i = 1; i < runs.Count; i++)
        {
            for (int j = 0; j < i; j++)
            {
                // A bin apart at most: two runs that land on the same frequency to this precision
                // are the same point, whatever the arithmetic says about the last digit.
                if (Math.Abs(runs[i][0].FreqHz - runs[j][0].FreqHz) > runs[i][0].FreqHz * 1e-6)
                    continue;

                throw new FormatException(
                    $"{StepName(sweeps[j], runs.Count)} and {StepName(sweeps[i], runs.Count)} both measure " +
                    $"{runs[i][0].FreqHz:G6} Hz, so there is no sweep here -- a point per frequency is what makes " +
                    "one. If the step changes something other than the tone, import this on an FFT analysis " +
                    "measurement instead, where each run becomes its own spectrum.");
            }
        }
    }

    private static string StepName(SimulationSweep sweep, int count) =>
        sweep.Label is { } label ? $"step {label}" : $"step {sweep.Index + 1} of {count}";

    /// <summary>
    /// Re-analyses a stored simulation from its own samples at new settings, rewriting its
    /// spectrum, its metrics and its <c>params_json</c>. The samples are untouched.
    /// </summary>
    /// <returns>False when that result carries no stored samples, i.e. is not a simulation.</returns>
    public async Task<bool> ReanalyzeAsync(long resultId, SimulationReanalyzeRequest request, CancellationToken ct)
    {
        var detail = await measurementRepository.GetResultAsync(resultId, ct);
        if (detail is null)
            return false;

        var pcm = await measurementRepository.GetCapturePcmAsync(detail.CaptureId, ct);
        if (pcm is null)
            return false;

        var stored = SimulationAnalysis.FromParamsJson(detail.ParamsJson, SerializerOptions)
            ?? throw new InvalidOperationException(
                "This result has no stored simulation settings, so it cannot be re-analysed.");

        var spec = RequireAnalysable(stored.With(request), pcm, stored);
        var rendered = Render(pcm.Channels, pcm.SampleRateHz, spec, ct);

        await measurementRepository.ReplaceRenderedSpectrumAsync(
            resultId, spec.ToParamsJson(SerializerOptions, rendered.AnalysisRateHz), rendered.AnalysisRateHz, spec.FftSize,
            rendered.Spectra, rendered.Metrics, ct);

        logger.LogInformation(
            "Re-analysed simulation {ResultId} at {FftSize} points / {Window} / {OffsetDb:F2} dB: " +
            "{AnalysisKHz:F1} kHz, {BinHz:F2} Hz bins",
            resultId, spec.FftSize, spec.Window, spec.LevelOffsetDb,
            rendered.AnalysisRateHz / 1e3, rendered.AnalysisRateHz / spec.FftSize);

        return true;
    }

    /// <summary>
    /// Checks a re-analysis against what the stored samples can actually answer for, refusing with
    /// a message that names the limit rather than quietly clamping to it.
    ///
    /// <para>The import validates the same things against the <i>file</i>, which is no longer
    /// available here -- so these are deliberately not the same checks. What binds a re-analysis is
    /// the samples: they begin where the import left them and end where the run did, so the start
    /// can only move forward and the FFT size is bounded by what is left after it.</para>
    /// </summary>
    private static SimulationAnalysis RequireAnalysable(
        SimulationAnalysis spec, StoredPcm pcm, SimulationAnalysis stored)
    {
        // Half a sample of slack, so re-sending the stored start time back unchanged -- which is
        // what a dialog prefilled from params_json does -- is never read as asking for less.
        double tolerance = 0.5 / Math.Max(pcm.SampleRateHz, 1.0);
        if (spec.StartTimeS < spec.SamplesStartS - tolerance)
        {
            throw new ArgumentException(
                $"This result's samples start at {spec.SamplesStartS * 1e3:F3} ms, so it cannot be analysed from " +
                $"{spec.StartTimeS * 1e3:F3} ms -- the earlier part of the run was not imported. Import the file " +
                "again from an earlier start time if you need it.");
        }

        int available = pcm.Channels[0].Length - spec.SampleOffset(pcm.SampleRateHz);
        if (available < 2)
        {
            throw new ArgumentException(
                $"Analysing from {spec.StartTimeS * 1e3:F3} ms leaves nothing of samples that end at " +
                $"{stored.Source.SourceEndS * 1e3:F3} ms.");
        }

        int largest = SimulationAnalysis.LargestFftSize(available / pcm.SampleRateHz, pcm.SampleRateHz);
        if (spec.FftSize < SimulationAnalysis.MinFftSize || spec.FftSize > largest)
        {
            throw new ArgumentException(
                $"{spec.FftSize} is not a usable FFT size for this result: it holds {available:N0} samples from " +
                $"{spec.StartTimeS * 1e3:F3} ms, so {SimulationAnalysis.MinFftSize} to {largest} can be filled " +
                "without interpolating past the simulator's own resolution.");
        }

        if (!(spec.MeasurementLoFreqHz > 0.0) || spec.MeasurementHiFreqHz <= spec.MeasurementLoFreqHz)
        {
            throw new ArgumentException(
                $"The measurement band {spec.MeasurementLoFreqHz:G6}-{spec.MeasurementHiFreqHz:G6} Hz is not a band.");
        }

        return spec;
    }

    /// <summary>
    /// Samples in, stored spectrum and metric rows out. The level offset is applied here rather
    /// than to the stored samples, which is what keeps it changeable.
    /// </summary>
    private RenderedSimulation Render(
        IReadOnlyList<double[]> samples, double nativeRateHz, SimulationAnalysis spec, CancellationToken ct)
    {
        var options = spec.ToPipelineOptions();
        double analysisRateHz = spec.BaseAnalysisRateHz;

        // Where in the stored samples this record starts. Zero at import, where the samples were
        // written from exactly this point; non-zero only after a re-analysis has moved the start
        // forward -- and then it has to be a skip rather than a shorter record, or the analysis
        // would trim the end of the run instead of the settling at its front. See
        // SimulationParameters.PcmStartS.
        int skip = Math.Clamp(spec.SampleOffset(nativeRateHz), 0, Math.Max(0, samples[0].Length - 2));

        // Two passes when the record is to be made periodic, because the fundamental it has to be
        // periodic in is something only an analysis can tell us. The first pass is at the base rate
        // and is thrown away; its only output is `FundamentalFreqHz`. Cheap next to being wrong:
        // without it every harmonic of a tone that does not divide the record leaks across its
        // neighbours, and the leakage is what a THD figure would read as distortion.
        if (spec.PeriodicRecord)
        {
            var probeRecord = new double[spec.FftSize];
            Resampler.Decimate(samples[0].AsSpan(skip), nativeRateHz, analysisRateHz, probeRecord);
            var probe = SpectrumRecordAnalyzer.Analyze(probeRecord, analysisRateHz, options, _fft);
            if (probe.Meters is { FundamentalFreqHz: > 0f } m)
                analysisRateHz = spec.PeriodicRateHz(m.FundamentalFreqHz);
        }

        double gain = Math.Pow(10.0, spec.LevelOffsetDb / 20.0);

        var spectra = new List<StoredSpectrumInput>();
        var metrics = new List<MeasurementMetric>();
        var meters = new List<SpectrumMeters?>();
        var previews = new List<SweepPreviewSeries?>();
        var record = new double[spec.FftSize];

        for (int channel = 0; channel < samples.Count; channel++)
        {
            ct.ThrowIfCancellationRequested();

            Resampler.Decimate(samples[channel].AsSpan(skip), nativeRateHz, analysisRateHz, record);
            if (gain != 1.0)
            {
                for (int i = 0; i < record.Length; i++)
                    record[i] *= gain;
            }

            var analysis = SpectrumRecordAnalyzer.Analyze(record, analysisRateHz, options, _fft);

            spectra.Add(new StoredSpectrumInput(
                channel, options.Window, analysis.Sums, analysisRateHz, analysis.BinSizeHz,
                SpectrumNorm.Amplitude, analysis.Dbv));

            MeasurementSaveService.AppendChannelMetrics(
                metrics, channel, analysis.Meters, analysis.Noise, analysis.BinSizeHz);

            meters.Add(analysis.Meters);
            previews.Add(BuildPreview(analysis.Dbv, analysis.BinSizeHz, spec.MeasurementLoFreqHz));
        }

        return new RenderedSimulation(analysisRateHz, spectra, metrics, meters, previews);
    }

    /// <summary>
    /// The small decimated spectrum a sweep point carries, in the same log-spaced min/max
    /// convention the live pipeline's own preview uses.
    ///
    /// <para>Built from the finished dBV trace rather than from the power spectrum the pipeline
    /// decimates, and that is exact rather than approximate: min and max commute with any
    /// increasing function, and dBV is one of power. What it saves is keeping a second copy of the
    /// power array alive purely to decimate it.</para>
    /// </summary>
    private static SweepPreviewSeries? BuildPreview(double[] dbv, double binSizeHz, double displayLoFreqHz)
    {
        if (dbv.Length < 3)
            return null;

        int loBin = Math.Clamp((int)Math.Round(displayLoFreqHz / binSizeHz), 1, dbv.Length - 2);
        int hiBin = dbv.Length - 1;
        int columnCount = Math.Clamp(hiBin - loBin + 1, 1, SpectrumPipeline.PreviewMaxColumns);

        var values = dbv.AsSpan(0, hiBin + 1);
        var columns = new MinMax[columnCount];
        var centerBins = new double[columnCount];
        SpectrumDecimator.DecimateLogWithCenters(values, columns, centerBins, loBin);

        var freqHz = new float[columnCount];
        var dbvMin = new float[columnCount];
        var dbvMax = new float[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            freqHz[i] = (float)(centerBins[i] * binSizeHz);
            dbvMin[i] = (float)columns[i].Min;
            dbvMax[i] = (float)columns[i].Max;
        }

        return new SweepPreviewSeries(freqHz, dbvMin, dbvMax);
    }

    /// <summary>
    /// Slices one run out of the file's columns, puts it on a uniform grid and analyses it — the
    /// work both import targets do per run, differing only in what they then store.
    /// </summary>
    private (SimulationAnalysis Spec, RenderedSimulation Rendered, List<double[]> Samples) AnalyseRun(
        SimulationImportRequest request,
        SimulationFileInfo info,
        LtspiceRawHeader header,
        IReadOnlyList<int> indices,
        double[][] wholeColumns,
        SimulationSweep sweep,
        CancellationToken ct)
    {
        var columns = wholeColumns
            .Select(c => c.AsSpan(sweep.FirstPoint, sweep.PointCount).ToArray())
            .ToArray();
        double[] time = columns[0];
        var runInfo = info with
        {
            PointCount = sweep.PointCount,
            StartTimeS = sweep.StartTimeS,
            EndTimeS = sweep.EndTimeS,
            DurationS = sweep.DurationS,
        };

        double startTimeS = Math.Clamp(request.StartTimeS, runInfo.StartTimeS, runInfo.EndTimeS);
        double usableS = runInfo.EndTimeS - startTimeS;
        int nativeCount = Resampler.SampleCount(usableS, runInfo.NativeRateHz);
        if (nativeCount < 2)
        {
            throw new ArgumentException(
                $"Analysing from {startTimeS * 1e3:F3} ms leaves nothing of a run that ends at " +
                $"{runInfo.EndTimeS * 1e3:F3} ms.", nameof(request));
        }

        var samples = new List<double[]>();
        var removedDcV = new List<double>();
        for (int c = 1; c < columns.Length; c++)
        {
            var uniform = new double[nativeCount];
            Resampler.ToUniform(time, columns[c], startTimeS, runInfo.NativeRateHz, uniform);
            removedDcV.Add(PcmCodec.RemoveMeanInPlace(uniform));
            PcmCodec.QuantizeInPlace(uniform);
            samples.Add(uniform);
        }

        var traceNames = indices.Skip(1).Select(i => header.Variables[i].Name).ToList();
        var traceUnits = indices.Skip(1).Select(i => header.Variables[i].Unit).ToList();

        var spec = SimulationAnalysis.Describe(
            runInfo, request, startTimeS, usableS, traceNames, traceUnits, removedDcV, sweep);

        return (spec, Render(samples, runInfo.NativeRateHz, spec, ct), samples);
    }

    private static void RequireTransient(LtspiceRawHeader header, string path)
    {
        string name = Path.GetFileName(path);

        if (header.Analysis != LtspiceAnalysis.Transient)
        {
            string what = header.Analysis switch
            {
                // Simulator-neutral, like RequireAc's arm: LTspice writes a .op.raw and QSpice a
                // .qopraw, and naming only the one leaves half the users reading about a file they
                // do not have.
                LtspiceAnalysis.OperatingPoint =>
                    "an operating-point file (the .op.raw or .qopraw the simulator writes beside every run), " +
                    "which holds one solution and no waveform",
                LtspiceAnalysis.AcAnalysis =>
                    "an AC analysis, which is a frequency response rather than a waveform. Open it on a " +
                    "Frequency response measurement instead",
                LtspiceAnalysis.Fft =>
                    "LTspice's own FFT export, which is already a spectrum. Import the transient run itself instead, " +
                    "so the window and the resolution are this app's to choose",
                _ => $"a \"{header.Plotname}\" run",
            };
            throw new FormatException($"{name} is {what}. This import reads a transient (.tran) run.");
        }

        if (header.IsComplex)
            throw new FormatException($"{name} holds complex data, not a time-domain waveform.");
        if (header.TimeVariable is null)
            throw new FormatException($"{name} has no time variable, so it is not a waveform.");
    }

    /// <param name="Meters">
    /// Per channel, in channel order, or null for a channel whose record held no fundamental. The
    /// spectrum import does not read these — its figures go out as metric rows — but the sweep
    /// import is built entirely from them.
    /// </param>
    /// <param name="Previews">
    /// Per channel, the small decimated spectrum a sweep point carries. Built here because this is
    /// where the full-resolution trace exists; null for a channel with no record.
    /// </param>
    private sealed record RenderedSimulation(
        double AnalysisRateHz,
        IReadOnlyList<StoredSpectrumInput> Spectra,
        IReadOnlyList<MeasurementMetric> Metrics,
        IReadOnlyList<SpectrumMeters?> Meters,
        IReadOnlyList<SweepPreviewSeries?> Previews);
}

/// <summary>What to change about a stored simulation's analysis. Anything left null stays as it was.</summary>
public sealed record SimulationReanalyzeRequest(
    double? StartTimeS = null,
    double? LevelOffsetDb = null,
    int? FftSize = null,
    WindowType? Window = null,
    double? MeasurementLoFreqHz = null,
    double? MeasurementHiFreqHz = null,
    bool? PeriodicRecord = null);

internal static class SimulationImportRequestExtensions
{
    internal static double MeasurementHiFreqHzOrDefault(this SimulationImportRequest request) =>
        request.MeasurementHiFreqHz > 0 ? request.MeasurementHiFreqHz : 20_000.0;
}
