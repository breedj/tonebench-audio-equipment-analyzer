using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Qa403.App.Endpoints;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;

namespace Qa403.App.Services;

/// <summary>
/// Orchestrates "save current capture" and "apply a measurement-type's parameters back to the live
/// app". Kept out of the endpoint file so it is testable without a host, matching
/// <c>GeneratorEndpoints.TryMapChannel</c>.
/// </summary>
public sealed class MeasurementSaveService(
    DeviceSessionService session,
    SpectrumPipeline spectrumPipeline,
    ProjectRepository projectRepository,
    MeasurementRepository measurementRepository,
    IOptions<JsonOptions> jsonOptions)
{
    // Reuses the app's own HTTP JSON options (JsonStringEnumConverter etc, configured once in
    // Program.cs) rather than a second, independently-drifting JsonSerializerOptions instance —
    // params_json round-trips WindowType/AveragingMode/GeneratorKind the same way the REST API does.
    private JsonSerializerOptions SerializerOptions => jsonOptions.Value.SerializerOptions;

    /// <summary>
    /// Saves the pipeline's currently retained snapshot(s) as one measurement result. Throws
    /// <see cref="InvalidOperationException"/> while acquiring (the snapshot is meant to be a
    /// stable, stopped trace — see docs on why streaming data itself is never persisted) or when
    /// nothing has been captured yet; the endpoint layer turns both into a 409.
    /// </summary>
    public async Task<long> SaveAsync(long measurementTypeId, string name, string? notes, CancellationToken ct)
    {
        if (session.Acquiring)
            throw new InvalidOperationException("Stop acquisition before saving a measurement — only a stopped, stable trace is saved.");

        var snapshots = spectrumPipeline.LatestSnapshots();
        if (snapshots.Length == 0)
            throw new InvalidOperationException("No spectrum has been captured yet.");

        var parameters = BuildParameters();
        string paramsJson = JsonSerializer.Serialize(parameters, SerializerOptions);
        string? genJson = JsonSerializer.Serialize(session.EffectiveGeneratorConfig, SerializerOptions);

        var spectra = snapshots
            .Select(s => new StoredSpectrumInput(
                ChannelIndex(s.Channel), s.Window, s.Sums, s.SampleRateHz, s.BinSizeHz, SpectrumNorm.Amplitude, s.Dbv))
            .ToList();

        var metrics = new List<MeasurementMetric>();
        foreach (var snapshot in snapshots)
        {
            // No fundamental found in band for this channel this tick — its spectrum is still
            // saved above (via `spectra`), just with no derived metric rows to go with it. See
            // SpectrumSnapshot.Meters.
            if (snapshot.Meters is not { } m)
                continue;

            int channel = ChannelIndex(snapshot.Channel);

            metrics.Add(new MeasurementMetric(channel, "thd_percent", m.ThdPercent, "%"));
            metrics.Add(new MeasurementMetric(channel, "thd_n_percent", m.ThdNPercent, "%"));
            metrics.Add(new MeasurementMetric(channel, "snr_db", m.SnrDb, "dB"));
            metrics.Add(new MeasurementMetric(channel, "fundamental_hz", m.FundamentalFreqHz, "Hz"));
            metrics.Add(new MeasurementMetric(channel, "fundamental_dbv", m.FundamentalDbv, "dBV"));
            metrics.Add(new MeasurementMetric(channel, "noise_floor_dbv", m.NoiseFloorDbv, "dBV"));
            metrics.Add(new MeasurementMetric(channel, "peak_dbv", m.PeakDbv, "dBV"));
            metrics.Add(new MeasurementMetric(channel, "total_rms_dbv", m.TotalRmsDbv, "dBV"));

            foreach (var harmonic in m.Harmonics)
            {
                metrics.Add(new MeasurementMetric(channel, $"h{harmonic.Order}_hz", harmonic.Bin * snapshot.BinSizeHz, "Hz"));
                metrics.Add(new MeasurementMetric(channel, $"h{harmonic.Order}_dbc", harmonic.Dbc, "dBc"));
            }
        }

        var info = session.Info;
        var request = new MeasurementSaveRequest(
            measurementTypeId, name, notes, paramsJson,
            info?.ProductName, info?.SerialNumber, info?.FirmwareVersion, info?.IsSimulated ?? false,
            parameters.SampleRateHz, parameters.FftSize, snapshots.Length,
            session.InputRangeDbv, session.OutputRangeDbv, genJson,
            spectra, metrics);

        return await measurementRepository.SaveResultAsync(request, ct);
    }

    /// <summary>
    /// Loads a measurement-type's configured parameters for the UI to stage — touches nothing on
    /// the device. Not even <see cref="DeviceSessionService.SetInputRangeAsync"/>: on real hardware
    /// that steps the attenuator relay, which is audible the instant a measurement is selected, not
    /// just when the generator plays. <see cref="spectrumPipeline"/>'s options are the one exception,
    /// since those are pure software state with no device I/O at all. Everything else — ranges,
    /// auto-range, generator, acquisition — is returned here and only actually applied once the user
    /// presses Run (see AcquisitionPanel.vue's run/stop and App.vue's runMeasurement).
    /// Throws <see cref="KeyNotFoundException"/> if the type doesn't exist.
    /// </summary>
    public async Task<MeasurementTypeAppliedResponse> ApplyAsync(long measurementTypeId, CancellationToken ct)
    {
        var type = await projectRepository.GetTypeAsync(measurementTypeId, ct)
            ?? throw new KeyNotFoundException($"Measurement type {measurementTypeId} not found.");

        var parameters = JsonSerializer.Deserialize<FftSpectrumParameters>(type.ParamsJson, SerializerOptions)
            ?? throw new InvalidOperationException($"Measurement type {measurementTypeId} has no usable parameters.");

        spectrumPipeline.Options = spectrumPipeline.Options with
        {
            FftSize = parameters.FftSize,
            Window = parameters.Window,
            Averaging = parameters.Averaging,
            AverageCount = parameters.AverageCount,
            MeasurementLoFreqHz = parameters.MeasurementLoFreqHz,
            MeasurementHiFreqHz = parameters.MeasurementHiFreqHz,
            NotchOctaves = parameters.NotchOctaves,
            HarmonicCount = parameters.HarmonicCount,
            FundamentalMinSearchHz = parameters.FundamentalMinSearchHz,
            OtherPeaksCount = parameters.OtherPeaksCount,
            ProcessLeft = parameters.ProcessLeft,
            ProcessRight = parameters.ProcessRight,
        };

        return new MeasurementTypeAppliedResponse(
            DeviceSessionService.ToResponse(parameters.Generator.Left),
            DeviceSessionService.ToResponse(parameters.Generator.Right),
            parameters.Generator.SnapToBin,
            parameters.SampleRateHz,
            parameters.ChannelCount,
            parameters.BlockFrameCount,
            parameters.InputRangeDbv,
            parameters.OutputRangeDbv,
            parameters.AutoRangeEnabled,
            parameters.SelectedMetrics);
    }

    /// <summary>
    /// The live pipeline/device state as an <see cref="FftSpectrumParameters"/> snapshot, optionally
    /// overridden by <paramref name="staged"/> for the generator/acquisition/range fields. Used for
    /// a save's as-run parameters (no override — a result is only saveable once stopped, at which
    /// point the device state IS the truth) and, via <see cref="BuildCurrentParametersJson"/>, to
    /// seed a newly-created or freshly-saved measurement-type — an empty <c>"{}"</c> default would
    /// fail to deserialize (every property is required), and the live device alone would miss
    /// whatever the Acquisition/Generator panels have staged but never pushed via Run.
    /// </summary>
    private FftSpectrumParameters BuildParameters(StagedMeasurementConfig? staged = null)
    {
        var options = spectrumPipeline.Options;
        var streamConfig = session.StreamConfig;

        double sampleRateHz = staged?.SampleRateHz ?? streamConfig?.SampleRateHz ?? 48000.0;
        int channelCount = staged?.ChannelCount ?? streamConfig?.ChannelCount ?? 2;
        double inputRangeDbv = staged?.InputRangeDbv ?? session.InputRangeDbv;
        bool? autoRangeEnabled = staged is not null ? staged.AutoRangeEnabled : session.AutoRangeEnabled;

        GeneratorConfig generator = session.EffectiveGeneratorConfig;
        if (staged is not null
            && GeneratorEndpoints.TryMapChannel(staged.GeneratorLeft, "Left", out var left, out _)
            && GeneratorEndpoints.TryMapChannel(staged.GeneratorRight, "Right", out var right, out _))
        {
            generator = new GeneratorConfig(left, right) { SnapToBin = staged.SnapToBin ?? true };
        }

        return new FftSpectrumParameters(
            options.FftSize, options.Window, options.Averaging, options.AverageCount,
            options.MeasurementLoFreqHz, options.MeasurementHiFreqHz, options.NotchOctaves,
            options.HarmonicCount, options.FundamentalMinSearchHz, options.OtherPeaksCount,
            options.ProcessLeft, options.ProcessRight,
            sampleRateHz, channelCount,
            streamConfig?.BlockFrameCount ?? DeviceSessionService.MinBlockFrameCount,
            inputRangeDbv, session.OutputRangeDbv, autoRangeEnabled,
            generator, staged?.SelectedMetrics);
    }

    public string BuildCurrentParametersJson(StagedMeasurementConfig? staged = null) =>
        JsonSerializer.Serialize(BuildParameters(staged), SerializerOptions);

    private static int ChannelIndex(StreamChannel channel) => channel == StreamChannel.Left ? 0 : 1;
}
