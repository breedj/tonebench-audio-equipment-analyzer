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
    IReadOnlyList<string> Warnings)
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
    bool PeriodicRecord = true);

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
        var axis = TimeAxis.Of(time);

        var warnings = new List<string>();

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

        if (!axis.IsMonotonic)
        {
            throw new FormatException(
                $"{Path.GetFileName(path)} has a time axis that goes backwards at point {axis.FirstNonMonotonicIndex}. " +
                "A stepped (`.step`) run writes several sweeps into one file, which this import cannot separate; " +
                "run a single simulation instead.");
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
            PointCount: header.PointCount,
            StartTimeS: axis.StartS,
            EndTimeS: axis.EndS,
            DurationS: axis.DurationS,
            NativeRateHz: resampleRateHz,
            MinStepS: axis.MinStepS,
            MedianStepS: axis.MedianStepS,
            MaxStepS: axis.MaxStepS,
            Traces: traces,
            Warnings: warnings);
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

    /// <summary>Imports the selected traces into <paramref name="measurementTypeId"/>.</summary>
    public async Task<long> ImportAsync(
        long measurementTypeId, SimulationImportRequest request, CancellationToken ct)
    {
        var info = Inspect(
            request.Path, request.MeasurementHiFreqHzOrDefault(), request.MeasurementLoFreqHz, ct);
        var header = LtspiceRawFile.ReadHeader(request.Path);

        var indices = new List<int> { header.TimeVariable!.Index, request.LeftTraceIndex };
        if (request.RightTraceIndex is { } right && right != request.LeftTraceIndex)
            indices.Add(right);

        double[][] columns = LtspiceRawFile.ReadColumns(request.Path, header, indices, ct);
        double[] time = columns[0];

        // One uniform grid at the simulator's own rate. Doing this once, here, rather than keeping
        // the non-uniform axis: the decimation below is a standard, filterable operation on a
        // uniform series, and what this discards is content above the simulator's own grid, which
        // its stepping could not represent anyway.
        double startTimeS = Math.Clamp(request.StartTimeS, info.StartTimeS, info.EndTimeS);
        double usableS = info.EndTimeS - startTimeS;
        int nativeCount = Resampler.SampleCount(usableS, info.NativeRateHz);
        if (nativeCount < 2)
            throw new ArgumentException(
                $"Analysing from {startTimeS * 1e3:F3} ms leaves nothing of a run that ends at " +
                $"{info.EndTimeS * 1e3:F3} ms.", nameof(request));

        var samples = new List<double[]>();
        var removedDcV = new List<double>();
        for (int c = 1; c < columns.Length; c++)
        {
            var uniform = new double[nativeCount];
            Resampler.ToUniform(time, columns[c], startTimeS, info.NativeRateHz, uniform);

            // The node's operating point comes off first, then the samples are rounded to the
            // precision they will be stored at. Both orderings matter and both are measured --
            // see SimulationParameters.RemovedDcV and PcmCodec.QuantizeInPlace.
            removedDcV.Add(PcmCodec.RemoveMeanInPlace(uniform));
            PcmCodec.QuantizeInPlace(uniform);
            samples.Add(uniform);
        }

        var traceNames = indices.Skip(1).Select(i => header.Variables[i].Name).ToList();
        var traceUnits = indices.Skip(1).Select(i => header.Variables[i].Unit).ToList();

        var spec = SimulationAnalysis.Describe(
            info, request, startTimeS, usableS, traceNames, traceUnits, removedDcV);
        var rendered = Render(samples, info.NativeRateHz, spec, ct);

        string name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{Path.GetFileNameWithoutExtension(request.Path)} - {traceNames[0]}"
            : request.Name.Trim();

        long resultId = await measurementRepository.SaveSimulationResultAsync(
            new SimulationSaveRequest(
                measurementTypeId, name, request.Notes, spec.ToParamsJson(SerializerOptions, rendered.AnalysisRateHz),
                rendered.AnalysisRateHz, spec.FftSize, info.NativeRateHz,
                samples, rendered.Spectra, rendered.Metrics),
            ct);

        logger.LogInformation(
            "Imported simulation {Name} from {Path}: {Traces}, {Points} points over {Duration:F3} ms, " +
            "native {NativeKHz:F1} kHz, analysed at {AnalysisKHz:F1} kHz / {FftSize} points ({BinHz:F2} Hz bins), " +
            "DC removed {RemovedDc}",
            name, request.Path, string.Join(" + ", traceNames), info.PointCount, usableS * 1e3,
            info.NativeRateHz / 1e3, rendered.AnalysisRateHz / 1e3, spec.FftSize,
            rendered.AnalysisRateHz / spec.FftSize,
            string.Join(" + ", removedDcV.Zip(traceUnits).Select(p => $"{p.First:G6} {p.Second}")));

        return resultId;
    }

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
        }

        return new RenderedSimulation(analysisRateHz, spectra, metrics);
    }

    private static void RequireTransient(LtspiceRawHeader header, string path)
    {
        string name = Path.GetFileName(path);

        if (header.Analysis != LtspiceAnalysis.Transient)
        {
            string what = header.Analysis switch
            {
                LtspiceAnalysis.OperatingPoint =>
                    "an operating-point file (the .op.raw that LTspice writes beside every run), which holds one " +
                    "solution and no waveform",
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

    private sealed record RenderedSimulation(
        double AnalysisRateHz,
        IReadOnlyList<StoredSpectrumInput> Spectra,
        IReadOnlyList<MeasurementMetric> Metrics);
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
