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
/// <summary>
/// An imported sweep: the result, and what was left out of it.
///
/// <para><see cref="Note"/> is empty when every run became a point. It is not an error — the import
/// succeeded — but a sweep that is not the whole file has to say so, so it travels back to the
/// dialog as well as onto the stored result's own source notes.</para>
/// </summary>
public sealed record SweepImportResult(long ResultId, string Note);

/// <summary>
/// What the points of an imported sweep are stepped <i>along</i>, which the open measurement
/// decides rather than the file: a <c>.step</c> is a parameter list and the <c>.raw</c> says
/// nothing about what the parameter meant. A tone stepped across the band is a THD-vs-frequency
/// curve; a drive level stepped up is a THD-vs-amplitude one; the same reader builds both.
/// </summary>
public enum SimulationSweepAxis
{
    /// <summary>Each run is a point at the tone its own analysis located.</summary>
    Frequency,

    /// <summary>Each run is a point at its own level — the drive trace's where one was picked.</summary>
    Amplitude,
}

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
    IReadOnlyList<int>? SweepIndices = null,
    /// <summary>
    /// Which axis the runs are points on, when they are imported as a sweep at all. Read only by
    /// <see cref="SimulationImportService.ImportSweepAsync"/>.
    /// </summary>
    SimulationSweepAxis SweepAxis = SimulationSweepAxis.Frequency,
    /// <summary>
    /// The load the DUT drives, for the amplitude sweep's output-power axis. Zero — the default —
    /// leaves <c>PowerWatt</c> at zero, which the chart drops on a log axis, so the watt axis is
    /// simply empty rather than wrong.
    /// </summary>
    double LoadOhms = 0.0);

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
    SimulationImportProgress progress,
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
                    "It was saved with LTspice's waveform compression on, which discards most of the samples — " +
                    "add `.options plotwinsize=0` to the schematic and run it again.",
                SpiceFlavour.QSpice =>
                    $"Its coarsest timestep is {axis.MaxStepS * 1e6:F1} us, which can only carry " +
                    $"{representableHz / 1e3:F1} kHz. QSpice chooses its own steps unless told otherwise — give " +
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
                "Saved at single precision. That is almost always fine — measured, it changes harmonic levels by " +
                "nothing down to -110 dBc — but `.options numdgt=7` writes full precision if you need it.");
        }

        double resampleRateHz = axis.ResampleRateHz(measurementHiFreqHz);
        if (axis.IsAdaptive(measurementHiFreqHz))
        {
            warnings.Add(
                $"This run uses adaptive timesteps, from {axis.MinStepS * 1e12:G3} ps to " +
                $"{axis.MaxStepS * 1e6:F1} us. It will be resampled at {resampleRateHz / 1e6:F2} MHz, which is " +
                "far more than the audio band needs — but a run with a fixed maximum timestep is easier to " +
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
                "it is a stepped (`.step`) run — a stepped file from either simulator carries a `stepped` flag, " +
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
            $"This run saves {durationS * 1e3:F0} ms, which gives {binHz:F2} Hz resolution — {severity}. " +
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

        // The dialog's own counter: one step per run, cleared whatever happens -- see
        // SimulationImportProgress.
        progress.Begin(chosen.Count);
        try
        {
            var resultIds = new List<long>(chosen.Count);
            foreach (var sweep in chosen)
            {
                resultIds.Add(await ImportRunAsync(measurementTypeId, request, info, header, indices, columns, sweep, ct));
                progress.Advance();
            }

            return resultIds;
        }
        finally
        {
            progress.Clear();
        }
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
    public async Task<SweepImportResult> ImportSweepAsync(
        long measurementTypeId, SimulationImportRequest request, CancellationToken ct)
    {
        // The counter is cleared here rather than at the end of the body, so a refusal -- which is
        // a throw from the middle of it -- does not leave a stale reading for the next dialog.
        try
        {
            return await ImportSweepCoreAsync(measurementTypeId, request, ct);
        }
        finally
        {
            progress.Clear();
        }
    }

    private async Task<SweepImportResult> ImportSweepCoreAsync(
        long measurementTypeId, SimulationImportRequest request, CancellationToken ct)
    {
        var info = Inspect(
            request.Path, request.MeasurementHiFreqHzOrDefault(), request.MeasurementLoFreqHz, ct);
        var header = LtspiceRawFile.ReadHeader(request.Path);
        var chosen = ResolveSweeps(info, request.SweepIndices);

        bool amplitude = request.SweepAxis == SimulationSweepAxis.Amplitude;

        if (chosen.Count < 2)
        {
            string example = amplitude
                ? "the drive level — e.g. `.step oct param amplitude 1m 500m 5` beside a `.tran`, with the " +
                  "source's amplitude written as `{amplitude}`"
                : "the tone frequency — e.g. `.step param freq 1k 10k 1k` beside a `.tran`";
            throw new ArgumentException(
                $"A sweep needs more than one point, and this file holds {chosen.Count} run" +
                $"{(chosen.Count == 1 ? "" : "s")}. A `.step` over {example} is what makes a transient run into " +
                "a sweep. Import it on an FFT analysis measurement instead to read it as a spectrum.");
        }

        var indices = new List<int> { header.TimeVariable!.Index, request.LeftTraceIndex };
        if (request.RightTraceIndex is { } right && right != request.LeftTraceIndex)
            indices.Add(right);

        bool driveFromRight = amplitude && indices.Count > 2;

        double[][] wholeColumns = LtspiceRawFile.ReadColumns(request.Path, header, indices, ct);
        var traceNames = indices.Skip(1).Select(i => header.Variables[i].Name).ToList();
        var traceUnits = indices.Skip(1).Select(i => header.Variables[i].Unit).ToList();

        // What the dialog's spinner counts. Cleared in the finally below, so a refusal leaves no
        // reading behind.
        progress.Begin(chosen.Count);

        SimulationAnalysis? firstSpec = null;
        var skipped = new List<SkippedRun>();
        var runs = new List<(SimulationSweep Sweep, List<ThdSweepPoint> Points)>(chosen.Count);
        double analysisRateHz = 0.0;
        int fftSize = 0;

        foreach (var sweep in chosen)
        {
            ct.ThrowIfCancellationRequested();

            var (spec, rendered, samples) = AnalyseRun(request, info, header, indices, wholeColumns, sweep, ct);
            progress.Advance();

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

            // A run whose tone the analysis cannot find is left out rather than refusing the
            // import. A `.step` that runs past the band's top takes every run up there with it --
            // six of the reported file's sixty-one -- and refusing over them threw away the
            // fifty-five that were good, with a message the user never saw because it rendered
            // above the fold. Reported 2026-10-19. Each one is named in the summary the dialog
            // prints afterwards, so nothing goes quietly.
            //
            // Every trace is checked, including the drive: one whose tone is missing cannot place
            // its run on the level axis either.
            int before = skipped.Count;
            for (int channel = 0; channel < samples.Count; channel++)
            {
                if (rendered.Meters[channel] is not { SnrDb: > 0.0f })
                    skipped.Add(new SkippedRun(sweep, channel, rendered.AnalysisRateHz, SkipReason.NoTone));
            }

            if (skipped.Count > before)
                continue;

            // On the amplitude axis a second trace is the *drive node*, not a second measured
            // channel: the x of such a sweep is the level going in, so that is what the trace is
            // for, and reading it as another output would put the source's own (flat, distortion-
            // free) curve on the chart instead of giving the points somewhere to sit.
            double driveDbv = driveFromRight ? rendered.Meters[1]!.FundamentalDbv : double.NaN;
            int channelCount = driveFromRight ? 1 : samples.Count;

            var runPoints = new List<ThdSweepPoint>(channelCount);
            for (int channel = 0; channel < channelCount; channel++)
            {
                var meters = rendered.Meters[channel]!;

                runPoints.Add(SweepCaptureSupport.PointFromMeters(
                    // The located fundamental is the point's own frequency as well as its reading:
                    // a simulation has no "frequency that was asked for" to carry separately, since
                    // the step's label is a parameter value rather than a tone.
                    freqHz: meters.FundamentalFreqHz,
                    channel: channel,
                    meters: meters,
                    // The drive node's own measured level where one was picked. Without it there is
                    // no generator level this side of the schematic, so gain is relative to the
                    // fundamental itself -- i.e. 0 dB -- and the chart's "input voltage" axis
                    // repeats the output one. Said once here rather than invented per point.
                    generatorAmpDbv: driveFromRight ? driveDbv : meters.FundamentalDbv,
                    loadOhms: request.LoadOhms,
                    noiseVrms: 0.0,
                    previewFft: rendered.Previews[channel],
                    previewScope: null));
            }

            runs.Add((sweep, runPoints));
        }

        // Two runs reading one frequency are one point on the axis, so the later is left out the
        // same way. It is a resolution limit rather than a mistake -- `.step oct param freq 10 40k
        // 5` is 1.5 Hz apart at the bottom, which a 200 ms run's 5 Hz bins cannot separate -- and
        // the summary says so.
        if (!amplitude)
            DropRepeatedTones(runs, skipped);

        if (runs.Count < 2)
        {
            // The one refusal left, and the only place the extra analysis is worth its cost: with
            // nothing to import, where the tones actually are is the whole of what the user needs
            // to know.
            double? tone = skipped.Count > 0 && skipped[0].Reason == SkipReason.NoTone
                ? LoudestLineHz(
                    request, info, header, indices, wholeColumns,
                    skipped[0].Sweep, skipped[0].AnalysisRateHz, skipped[0].Channel, ct)
                : null;

            string found = tone is { } hz
                ? $" The loudest line in {StepName(skipped[0].Sweep, chosen.Count)} is at {FormatHz(hz)}."
                : "";

            throw new FormatException(
                $"Only {runs.Count} of the {chosen.Count} runs could be placed on this sweep's axis, and a sweep " +
                $"needs at least two points. {SkippedSentence(skipped, chosen.Count, request, analysisRateHz / Math.Max(fftSize, 1))}{found} Widen the " +
                "measurement band, lengthen the simulation so the steps fall on separate bins, or import this " +
                "on an FFT analysis measurement instead, where each run becomes its own spectrum.");
        }

        var spec0 = firstSpec!;

        if (amplitude)
            RequireLevelSpan([.. runs.Select(run => run.Points)]);

        // Ordered by the x the chart will read: the tone on a frequency sweep, the level on an
        // amplitude one, so the stored points run along their own axis rather than along the order
        // the steps happened to be written in.
        var ordered = amplitude
            ? runs.OrderBy(run => run.Points[0].GeneratorDbv).ToList()
            : runs.OrderBy(run => run.Points[0].FreqHz).ToList();
        var points = ordered.SelectMany(run => run.Points).ToList();

        // The runs that were left out travel with the result as well as back to the dialog: a
        // stored sweep has to be able to say it is not the whole file.
        var notes = skipped.Count == 0
            ? info.Warnings
            : (IReadOnlyList<string>)[.. info.Warnings, SkippedSentence(skipped, chosen.Count, request, analysisRateHz / fftSize)];

        string paramsJson = amplitude
            ? AmplitudeSweepParamsJson(spec0, [.. ordered.Select(run => run.Points)], driveFromRight, request.LoadOhms, analysisRateHz, fftSize, notes)
            : SweepParamsJson(spec0, points, traceNames, analysisRateHz, fftSize, notes);

        var captures = ordered
            .Select((run, index) => new SweepCaptureInput(
                index,
                run.Points[0].FreqHz,
                MeasurementSaveService.BuildSweepMetrics(run.Points),
                MeasurementSaveService.BuildSweepPreviews(run.Points)))
            .ToList();

        string name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{Path.GetFileNameWithoutExtension(request.Path)} - {traceNames[0]}"
            : request.Name.Trim();

        long resultId = await measurementRepository.SaveSweepResultAsync(
            new SweepSaveRequest(
                measurementTypeId, name, request.Notes, paramsJson,
                DeviceName: null, Serial: null, FwVer: null, IsSimulated: true,
                analysisRateHz, fftSize,
                Channels: ordered[0].Points.Count,
                InRangeDbv: null, OutRangeDbv: null,
                Captures: captures,
                Source: MeasurementSource.Simulation),
            ct);

        logger.LogInformation(
            "Imported stepped simulation {Name} from {Path} as a {Axis} sweep: {Traces}, {Points} points over " +
            "{Low:F2}-{High:F2} {Unit}, analysed at {AnalysisKHz:F1} kHz / {FftSize} points",
            name, request.Path, amplitude ? "THD-vs-amplitude" : "THD-vs-frequency",
            string.Join(" + ", traceNames), captures.Count,
            amplitude ? ordered[0].Points[0].GeneratorDbv : captures[0].FreqHz,
            amplitude ? ordered[^1].Points[0].GeneratorDbv : captures[^1].FreqHz,
            amplitude ? "dBV" : "Hz",
            analysisRateHz / 1e3, fftSize);

        string note = SkippedSentence(skipped, chosen.Count, request, analysisRateHz / fftSize);
        if (note.Length > 0)
            logger.LogInformation("Imported sweep {Name}: {Skipped}", name, note);

        return new SweepImportResult(resultId, note);
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
        int fftSize,
        IReadOnlyList<string> notes)
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
                Simulation = WithNotes(spec.ToParameters(analysisRateHz).Simulation, notes),
            },
            SerializerOptions);
    }

    /// <summary>
    /// The simulation block with the import's own notes on its source — the runs that were left
    /// out, which is the one thing a stored sweep cannot otherwise say about itself.
    /// </summary>
    private static SimulationParameters? WithNotes(SimulationParameters? simulation, IReadOnlyList<string> notes) =>
        simulation is null ? null : simulation with { Source = simulation.Source with { Warnings = notes } };

    /// <summary>
    /// The settings document for a sweep imported along the <b>level</b> axis.
    ///
    /// <para>The same shape a measured THD-vs-amplitude run writes, filled from what the runs
    /// turned out to be rather than from a ladder anyone asked for: a <c>.step</c> is a parameter
    /// list, so its levels need not be octave-spaced at all, and the step count per octave is
    /// reported as what they came out as. The fields describing a generator say
    /// <c>NotApplicable</c> by being zero/absent the way the frequency sweep's do.</para>
    /// </summary>
    private string AmplitudeSweepParamsJson(
        SimulationAnalysis spec,
        IReadOnlyList<List<ThdSweepPoint>> ordered,
        bool driveFromRight,
        double loadOhms,
        double analysisRateHz,
        int fftSize,
        IReadOnlyList<string> notes)
    {
        double startVolt = DbvToVolt(ordered[0][0].GeneratorDbv);
        double endVolt = DbvToVolt(ordered[^1][0].GeneratorDbv);
        double octaves = startVolt > 0.0 && endVolt > startVolt ? Math.Log2(endVolt / startVolt) : 0.0;
        int stepsPerOctave = octaves > 0.0 ? (int)Math.Round((ordered.Count - 1) / octaves) : 0;

        return JsonSerializer.Serialize(
            new ThdVsAmplitudeParameters(
                // Every run plays the same tone -- that is what makes this a level sweep -- so the
                // first run's measured fundamental is the sweep's frequency.
                FixedFreqHz: ordered[0][0].MeasuredFreqHz,
                StartAmplitudeVolt: startVolt,
                EndAmplitudeVolt: endVolt,
                StepsPerOctave: Math.Max(stepsPerOctave, 1),
                LoadOhms: loadOhms,
                ProcessLeft: true,
                // A drive trace is the x axis rather than a second channel, so it is not processed
                // as one -- see the point loop.
                ProcessRight: !driveFromRight && ordered[0].Count > 1,
                FftSize: fftSize,
                Window: spec.Window,
                AverageCount: 1,
                SampleRateHz: analysisRateHz,
                MeasureNoiseFloor: false)
            {
                Simulation = WithNotes(spec.ToParameters(analysisRateHz).Simulation, notes),
            },
            SerializerOptions);
    }

    private static double DbvToVolt(double dbv) =>
        double.IsFinite(dbv) ? Math.Pow(10.0, dbv / 20.0) : 0.0;

    /// <summary>
    /// Refuses a set of runs that do not step the level, which on this axis is the counterpart of
    /// <see cref="RequireDistinctTones"/>: a <c>.step</c> over a component value plays every run at
    /// the same drive, and the points would then stack on one x.
    ///
    /// <para>The <i>span</i> rather than pairwise distinctness, and that is deliberate — an
    /// amplifier compressing at the top of its range genuinely delivers two steps at almost the
    /// same level, and refusing that would throw away the most interesting point of the curve. A
    /// decibel over the whole set is far below any real amplitude sweep (the reference run spans
    /// 54 dB) and far above the fraction of a decibel a non-level step can produce through a
    /// component's own effect on gain.</para>
    /// </summary>
    private static void RequireLevelSpan(IReadOnlyList<List<ThdSweepPoint>> runs)
    {
        double lowest = runs.Min(run => run[0].GeneratorDbv);
        double highest = runs.Max(run => run[0].GeneratorDbv);
        if (highest - lowest >= MinimumLevelSpanDb)
            return;

        throw new FormatException(
            $"These {runs.Count} runs all measure about the same level — {lowest:F2} to {highest:F2} dBV, " +
            $"a span of {highest - lowest:F2} dB — so there is no amplitude sweep here; a point per level is " +
            "what makes one. If the `.step` changes something other than the drive, import this on an FFT " +
            "analysis measurement instead, where each run becomes its own spectrum.");
    }

    /// <summary>How much level a set of runs has to span before it is a sweep at all. See <see cref="RequireLevelSpan"/>.</summary>
    private const double MinimumLevelSpanDb = 1.0;

    private enum SkipReason
    {
        /// <summary>No tone inside the measurement band — almost always a step past its top or bottom.</summary>
        NoTone,

        /// <summary>The same frequency an earlier run read, which at this resolution is the same point.</summary>
        RepeatedTone,
    }

    /// <summary>One run that could not be placed on the sweep's axis, and what it would take to say why.</summary>
    private readonly record struct SkippedRun(
        SimulationSweep Sweep, int Channel, double AnalysisRateHz, SkipReason Reason, double RepeatedHz = 0.0);

    /// <summary>
    /// Leaves out every run after the first that reads a frequency an earlier one already read.
    ///
    /// <para>Two runs on one x are one point, and the later of them has nothing to add. It is a
    /// resolution limit rather than a mistake — `.step oct param freq 10 40k 5` is 1.5 Hz apart at
    /// the bottom of its range, which a 200 ms run's 5 Hz bins cannot separate — so it is reported
    /// with the record length rather than refused.</para>
    /// </summary>
    private static void DropRepeatedTones(
        List<(SimulationSweep Sweep, List<ThdSweepPoint> Points)> runs, List<SkippedRun> skipped)
    {
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            // Walked backwards so a removal cannot renumber the runs still to be looked at; the
            // *earliest* run at a frequency is the one kept, which is why the match is against the
            // runs before it rather than against what survives.
            double hz = runs[i].Points[0].FreqHz;
            bool repeat = runs
                .Take(i)
                .Any(earlier => Math.Abs(earlier.Points[0].FreqHz - hz) <= hz * 1e-6);

            if (!repeat)
                continue;

            skipped.Add(new SkippedRun(runs[i].Sweep, 0, 0.0, SkipReason.RepeatedTone, hz));
            runs.RemoveAt(i);
        }
    }

    /// <summary>
    /// What was left out, as one sentence for the dialog and for the stored result. Empty when
    /// nothing was.
    /// </summary>
    private static string SkippedSentence(
        IReadOnlyList<SkippedRun> skipped, int runCount, SimulationImportRequest request, double binWidthHz)
    {
        if (skipped.Count == 0)
            return "";

        string band = $"{FormatHz(request.MeasurementLoFreqHz)} – {FormatHz(request.MeasurementHiFreqHzOrDefault())}";
        var parts = new List<string>(2);

        // In step order, whatever order they were found in -- DropRepeatedTones walks backwards so
        // a removal cannot renumber what it has yet to look at, and a list that reads 26, 17, 15,
        // 11 Hz looks like a different fault from one that reads 11, 15, 17, 26.
        var noTone = Ordered(skipped, SkipReason.NoTone);
        if (noTone.Count > 0)
            parts.Add($"{NameRuns(noTone, runCount)} — no tone inside the measurement band ({band})");

        var repeated = Ordered(skipped, SkipReason.RepeatedTone);
        if (repeated.Count > 0)
        {
            string bins = binWidthHz > 0.0 ? $", which {FormatHz(binWidthHz)} bins cannot tell apart" : "";
            parts.Add($"{NameRuns(repeated, runCount)} — the same frequency as an earlier run{bins}");
        }

        return $"Left out {skipped.Count} of the {runCount} runs: {string.Join("; ", parts)}.";
    }

    private static List<SkippedRun> Ordered(IReadOnlyList<SkippedRun> skipped, SkipReason reason) =>
        [.. skipped.Where(run => run.Reason == reason).OrderBy(run => run.Sweep.Index)];

    /// <summary>
    /// The runs by name, cut off at <see cref="NamedRunLimit"/> so a file where most of the steps
    /// are out of band does not answer with a wall of them.
    /// </summary>
    private static string NameRuns(IReadOnlyList<SkippedRun> skipped, int runCount)
    {
        var named = skipped
            .Take(NamedRunLimit)
            .Select(run => $"{StepName(run.Sweep, runCount)}{(run.Channel == 0 ? "" : " (right trace)")}");

        string list = string.Join(", ", named);
        int rest = skipped.Count - NamedRunLimit;
        return rest > 0 ? $"{list} and {rest} more" : list;
    }

    private const int NamedRunLimit = 6;

    /// <summary>A frequency as a reader writes one — <c>20 Hz</c>, <c>20.5 kHz</c>.</summary>
    private static string FormatHz(double hz) =>
        hz >= 1_000.0
            ? $"{hz / 1_000.0:0.###} kHz"
            : $"{hz:0.###} Hz";

    /// <summary>
    /// The loudest line in one run with the band opened to everything the analysis can see, or
    /// null when there is no tone there either.
    ///
    /// <para>Only ever called on the way to refusing a run, which is what makes a second analysis
    /// of it affordable: the band is the only thing changed, so the record, the window and the
    /// analysis rate are the ones the import was already using and the answer is about the run
    /// rather than about a different reading of it.</para>
    /// </summary>
    private double? LoudestLineHz(
        SimulationImportRequest request,
        SimulationFileInfo info,
        LtspiceRawHeader header,
        IReadOnlyList<int> indices,
        double[][] wholeColumns,
        SimulationSweep sweep,
        double analysisRateHz,
        int channel,
        CancellationToken ct)
    {
        try
        {
            // Half the analysis rate is everything there is; a hair under it, because the band's
            // top has to sit inside the spectrum rather than on its last bin.
            double nyquist = analysisRateHz / 2.0 * 0.999;
            if (!(nyquist > request.MeasurementHiFreqHzOrDefault()))
                return null;

            var wide = request with { MeasurementHiFreqHz = nyquist };
            var (_, rendered, _) = AnalyseRun(wide, info, header, indices, wholeColumns, sweep, ct);
            var meters = rendered.Meters.ElementAtOrDefault(channel);
            return meters is { SnrDb: > 0.0f } ? meters.FundamentalFreqHz : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A diagnosis is a nicety; the refusal it decorates is not. Anything that goes wrong
            // here leaves the plain message rather than replacing one failure with another.
            return null;
        }
    }

    /// <summary>
    /// Refuses a set of runs that do not sweep anything, which is what a <c>.step</c> over something
    /// other than the tone produces: N runs of the same frequency, i.e. N points stacked on one x.
    /// Those are a perfectly good set of spectra, so the message says where to take them.
    /// </summary>
    private static void RequireDistinctTones(
        IReadOnlyList<List<ThdSweepPoint>> runs,
        IReadOnlyList<SimulationSweep> sweeps,
        double binWidthHz,
        double usableS)
    {
        for (int i = 1; i < runs.Count; i++)
        {
            for (int j = 0; j < i; j++)
            {
                // A bin apart at most: two runs that land on the same frequency to this precision
                // are the same point, whatever the arithmetic says about the last digit.
                if (Math.Abs(runs[i][0].FreqHz - runs[j][0].FreqHz) > runs[i][0].FreqHz * 1e-6)
                    continue;

                // Two *different* steps reading one frequency is the commoner cause, and it is a
                // resolution limit rather than a mistake: a `.step oct param freq 10 40k 5` is
                // 1.5 Hz apart at the bottom, which a 200 ms run's 5 Hz bins cannot separate.
                // Reported 2026-10-19. Named first, because "the step changes something other than
                // the tone" is the wrong thing to go and check in that case.
                throw new FormatException(
                    $"{StepName(sweeps[j], runs.Count)} and {StepName(sweeps[i], runs.Count)} both measure " +
                    $"{FormatHz(runs[i][0].FreqHz)}, so there is no sweep here — a point per frequency is what " +
                    $"makes one. The record is {usableS * 1e3:0.##} ms, so the bins are " +
                    $"{FormatHz(binWidthHz)} and two steps closer together than that land on the same one: " +
                    "simulate for longer (one second of saved run per hertz of spacing), or space the steps " +
                    "more widely at the bottom of the sweep, or untick the ones that collide. If the step " +
                    "changes something other than the tone, import this on an FFT analysis measurement " +
                    "instead, where each run becomes its own spectrum.");
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
                $"{spec.StartTimeS * 1e3:F3} ms — the earlier part of the run was not imported. Import the file " +
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
