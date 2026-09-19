using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Qa403.App.Endpoints;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// Orchestrates "save current capture" and "apply a measurement-type's parameters back to the live
/// app". Kept out of the endpoint file so it is testable without a host, matching
/// <c>GeneratorEndpoints.TryMapChannel</c>.
/// </summary>
public sealed class MeasurementSaveService(
    DeviceSessionService session,
    SpectrumPipeline spectrumPipeline,
    ThdSweepRunner sweepRunner,
    FrequencyResponseRunner responseRunner,
    SteppedResponseRunner steppedResponseRunner,
    ImpulseResponseRunner impulseRunner,
    ImpedanceRunner impedanceRunner,
    ProjectRepository projectRepository,
    MeasurementRepository measurementRepository,
    IOptions<JsonOptions> jsonOptions)
{
    // Reuses the app's own HTTP JSON options (JsonStringEnumConverter etc, configured once in
    // Program.cs) rather than a second, independently-drifting JsonSerializerOptions instance —
    // params_json round-trips WindowType/AveragingMode/GeneratorKind the same way the REST API does.
    private JsonSerializerOptions SerializerOptions => jsonOptions.Value.SerializerOptions;

    /// <summary>The <c>measurement_type.kind</c> value for a THD-vs-frequency sweep. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string ThdVsFrequencyKind = "ThdVsFrequency";

    /// <summary>The <c>measurement_type.kind</c> value for a THD-vs-amplitude sweep. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string ThdVsAmplitudeKind = "ThdVsAmplitude";

    /// <summary>
    /// Whether a kind's result is a run of sweep points rather than a single spectrum. The
    /// distinction, not the individual kind, is what every branch here actually cares about: a
    /// sweep type has no live server state to snapshot, its params_json is not an
    /// <see cref="FftSpectrumParameters"/>, and its result rows carry no spectra.
    /// </summary>
    public static bool IsSweepKind(string kind) => kind is ThdVsFrequencyKind or ThdVsAmplitudeKind;

    /// <summary>The <c>measurement_type.kind</c> value for a chirp frequency response. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string FrequencyResponseKind = "FrequencyResponse";

    /// <summary>The <c>measurement_type.kind</c> value for a stepped-sine Bode plot. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string SteppedResponseKind = "SteppedResponse";

    /// <summary>
    /// Whether a kind's result is a set of frequency-response curves. Its own predicate rather than
    /// a third arm of <see cref="IsSweepKind"/>: a response shares with a sweep only the property
    /// that it has no live state to snapshot. Its result rows are curves, not points, so every path
    /// that reads a result has to tell the two apart, and a kind that answered true to "is a sweep"
    /// would be read back through <c>GetSweepPointsAsync</c> and come out empty.
    ///
    /// <para>Both response kinds answer true, and that is the point of the predicate rather than an
    /// oversight: a chirp and a stepped sine are acquired completely differently but produce the
    /// same curves, save into the same <c>response_trace</c> rows, and are read back, exported and
    /// overlaid by exactly the same code. Everything downstream branches on <em>this</em>, not on
    /// the individual kind. Only <see cref="ResolveTypeParamsJson"/> and the save methods, which
    /// deal in each kind's own settings record, have to tell them apart.</para>
    /// </summary>
    public static bool IsFrequencyResponseKind(string kind) => kind is FrequencyResponseKind or SteppedResponseKind;

    /// <summary>The <c>measurement_type.kind</c> value for a swept-sine impulse response. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string ImpulseResponseKind = "ImpulseResponse";

    /// <summary>
    /// Whether a kind's result is an impulse response.
    ///
    /// <para>Deliberately <b>not</b> a third arm of <see cref="IsFrequencyResponseKind"/>, even
    /// though an impulse-response result does store gated magnitude and phase curves in the same
    /// <c>response_trace</c> rows. That predicate decides which start endpoint the UI drives and
    /// which reader a stored result goes through, and an impulse response has more to read back than
    /// curves — the impulse itself and the harmonic curves — so a kind that answered true to it
    /// would load half a measurement and show no sign of the rest.</para>
    /// </summary>
    public static bool IsImpulseResponseKind(string kind) => kind is ImpulseResponseKind;

    /// <summary>The <c>measurement_type.kind</c> value for an impedance measurement. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string ImpedanceKind = "Impedance";

    /// <summary>
    /// Whether a kind's result is an impedance curve.
    ///
    /// <para>Its own predicate rather than a third arm of <see cref="IsFrequencyResponseKind"/>, for
    /// the same reason <see cref="IsImpulseResponseKind"/> is: impedance does store its curves in the
    /// <c>response_trace</c> rows, but that predicate also decides which start endpoint the UI drives,
    /// which settings record <see cref="ResolveTypeParamsJson"/> writes and how a result is exported —
    /// and impedance differs in all three. What it shares is the storage, nothing else.</para>
    /// </summary>
    public static bool IsImpedanceKind(string kind) => kind is ImpedanceKind;

    /// <summary>Whether a kind's configuration lives only in its panel, with nothing live to snapshot — see <see cref="ResolveTypeParamsJson"/>.</summary>
    public static bool HasNoLiveConfiguration(string kind) =>
        IsSweepKind(kind) || IsFrequencyResponseKind(kind) || IsImpulseResponseKind(kind) || IsImpedanceKind(kind);

    /// <summary>
    /// A brand-new sweep type's <c>params_json</c>. Unlike an FFT type there is nothing live to
    /// snapshot — a configured sweep exists only as SweepConfigPanel's form — so a new one starts
    /// from that panel's own defaults, kept in step with it. The fixed analysis settings
    /// (192 kHz / 131072 / Hann) are not offered as choices in either place; see the panel's
    /// remarks on why.
    ///
    /// Serialized through <see cref="SerializerOptions"/> like every other params_json, not with a
    /// bare <c>JsonSerializer.Serialize</c>: the default options write PascalCase keys and a
    /// numeric <c>Window</c>, which the browser reads as an object with none of the fields it wants
    /// and a window it cannot name — a new sweep type silently came up with the panel's hard-coded
    /// defaults instead of its own stored ones.
    /// </summary>
    public string DefaultSweepParametersJson() =>
        JsonSerializer.Serialize(new ThdVsFrequencyParameters(
            StartFreqHz: 20.0,
            EndFreqHz: 20000.0,
            StepsPerOctave: 5,
            LevelMode: nameof(ThdSweepLevelMode.GeneratorVoltage),
            LevelValue: -20.0,
            LevelUnit: nameof(ThdSweepLevelUnit.Dbv),
            LoadOhms: 8.0,
            ProcessLeft: true,
            ProcessRight: false,
            FftSize: 131072,
            Window: WindowType.Hann,
            AverageCount: 4,
            SampleRateHz: 192000.0,
            MeasureNoiseFloor: true,
            AdaptiveResolution: true,
            ThdMethod: nameof(Services.ThdMethod.Conventional),
            // On for a new measurement only: a stored type keeps its own (null reads as off), so
            // nothing saved earlier starts refitting its axes -- see SweepDisplayPanel.vue.
            ChartAutofitOnDone: true), SerializerOptions);

    /// <summary>
    /// A brand-new THD-vs-amplitude type's <c>params_json</c>, on the same terms as
    /// <see cref="DefaultSweepParametersJson"/> and kept in step with
    /// <c>AmplitudeSweepConfigPanel.vue</c>'s own defaults.
    /// </summary>
    public string DefaultAmplitudeSweepParametersJson() =>
        JsonSerializer.Serialize(new ThdVsAmplitudeParameters(
            FixedFreqHz: 1000.0,
            StartAmplitudeVolt: 0.001,
            EndAmplitudeVolt: 1.0,
            StepsPerOctave: 5,
            LoadOhms: 8.0,
            ProcessLeft: true,
            ProcessRight: false,
            FftSize: ThdSweepRequest.AmplitudeSweepFftSize,
            Window: WindowType.Hann,
            AverageCount: 4,
            SampleRateHz: ThdSweepRequest.AmplitudeSweepSampleRateHz,
            MeasureNoiseFloor: true,
            ChartAutofitOnDone: true), SerializerOptions);

    /// <summary>
    /// A brand-new frequency-response type's <c>params_json</c>, on the same terms as
    /// <see cref="DefaultSweepParametersJson"/> and kept in step with
    /// <c>FrequencyResponseConfigPanel.vue</c>'s own defaults. Those defaults are the previous
    /// app's: 96 kHz span, 1.46 Hz bins, 0 dBV of DUT output, 1/24 octave, one average, and the
    /// right input as the reference.
    /// </summary>
    public string DefaultFrequencyResponseParametersJson() =>
        JsonSerializer.Serialize(new FrequencyResponseParameters(
            FrequencySpanHz: 96000.0,
            ResolutionMultiplier: 1,
            LevelMode: nameof(ThdSweepLevelMode.OutputVoltage),
            LevelValue: 0.0,
            LevelUnit: nameof(ThdSweepLevelUnit.Dbv),
            RightChannelIsReference: true,
            SmoothingDenominator: 24,
            AverageCount: 1,
            LoadOhms: 8.0,
            BandwidthReference: nameof(BandwidthReferenceMode.BandMedian),
            BandwidthReferenceLowHz: BandwidthReferenceSpec.DefaultLowHz,
            BandwidthReferenceHighHz: BandwidthReferenceSpec.DefaultHighHz), SerializerOptions);

    /// <summary>
    /// A brand-new impulse-response type's <c>params_json</c>, on the same terms as
    /// <see cref="DefaultFrequencyResponseParametersJson"/> and kept in step with
    /// <c>ImpulseResponseConfigPanel.vue</c>'s own defaults.
    ///
    /// <para>No reference by default: a stored loopback has to be captured before it can be picked,
    /// and defaulting to the right channel would silently require the user to have wired it.</para>
    /// </summary>
    public string DefaultImpulseResponseParametersJson() =>
        JsonSerializer.Serialize(new ImpulseResponseParameters(
            FrequencySpanHz: 96000.0,
            ResolutionMultiplier: 1,
            LevelMode: nameof(ThdSweepLevelMode.OutputVoltage),
            LevelValue: 0.0,
            LevelUnit: nameof(ThdSweepLevelUnit.Dbv),
            SmoothingDenominator: 24,
            AverageCount: 1,
            ReferenceMode: nameof(ImpulseReferenceMode.None),
            ReferenceCalibrationId: null,
            GatePreMs: ImpulseResponseRequest.DefaultGatePreMs,
            GatePostMs: ImpulseResponseRequest.DefaultGatePostMs,
            GateWindow: nameof(WindowType.Hann),
            ExtractHarmonics: true,
            MaxHarmonicOrder: ImpulseResponseRequest.DefaultMaxHarmonicOrder,
            ChirpStartHz: FrequencyResponseRequest.DefaultChirpStartHz,
            LoadOhms: 8.0), SerializerOptions);

    /// <summary>
    /// A brand-new stepped-sine response type's <c>params_json</c>, on the same terms as
    /// <see cref="DefaultSweepParametersJson"/> and kept in step with
    /// <c>SteppedResponseConfigPanel.vue</c>'s own defaults.
    ///
    /// <para>The grid defaults match the THD sweep's (20 Hz–20 kHz, 5 per octave), since that is the
    /// stimulus this measurement borrows. <c>AdaptiveResolution</c> defaults <b>on</b>, unlike the
    /// THD sweep's — see <see cref="SteppedResponseRequest.EffectiveSampleRateHz"/>: with no
    /// harmonics to keep in band it costs nothing and saves three quarters of the run's USB and FFT
    /// load.</para>
    /// </summary>
    public string DefaultSteppedResponseParametersJson() =>
        JsonSerializer.Serialize(new SteppedResponseParameters(
            StartFreqHz: 20.0,
            EndFreqHz: 20000.0,
            StepsPerOctave: 5,
            LevelMode: nameof(ThdSweepLevelMode.GeneratorVoltage),
            LevelValue: -20.0,
            LevelUnit: nameof(ThdSweepLevelUnit.Dbv),
            LoadOhms: 8.0,
            GenerateLeft: true,
            GenerateRight: true,
            FftSize: SteppedResponseRequest.DefaultFftSize,
            Window: WindowType.Hann,
            AverageCount: 1,
            SampleRateHz: SteppedResponseRequest.DefaultSampleRateHz,
            AdaptiveResolution: true,
            RightChannelIsReference: true,
            BandwidthReference: nameof(BandwidthReferenceMode.BandMedian),
            BandwidthReferenceLowHz: BandwidthReferenceSpec.DefaultLowHz,
            BandwidthReferenceHighHz: BandwidthReferenceSpec.DefaultHighHz), SerializerOptions);

    /// <summary>
    /// A brand-new impedance type's <c>params_json</c>, kept in step with
    /// <c>ImpedanceConfigPanel.vue</c>'s own defaults.
    ///
    /// <para>100 ohms is what a typical bench fixture is built with, and the left channel as
    /// reference is how one is normally wired — note that <c>--simulate</c> needs the opposite, since
    /// its fixture models sit on the left channel. The sweep starts at 10 Hz rather than the
    /// frequency response's 2 Hz: a driver's resonance is the point of this measurement and 10 Hz is
    /// below any of them, while a decade lower costs signal-to-noise for nothing. Smoothing is light
    /// (1/48 octave) so a high-Q resonance peak is not blunted, and no calibration is stored until
    /// the user captures one.</para>
    ///
    /// <para>The chirp is the default for both the measurement and the calibration. The stepped
    /// grid's defaults are there so switching the dropdown lands on something sensible rather than on
    /// zeros — 20 Hz to 20 kHz at 5 points per octave, the same shape the stepped-sine response
    /// starts from.</para>
    /// </summary>
    /// <param name="useGlobalFixture">
    /// Whether to start the new measurement on the globally stored fixture calibration. True
    /// whenever one exists, because that is nearly always what is wanted: the alternative is a
    /// measurement with no calibration at all, which reads a plausible impedance that is wrong by
    /// the two ADC channels' mismatch and by the test leads. A user who wants this measurement's own
    /// standards unticks it and captures them, and that choice is then stored on the type.
    /// </param>
    public string DefaultImpedanceParametersJson(bool useGlobalFixture = false) =>
        JsonSerializer.Serialize(new ImpedanceParameters(
            Method: nameof(ImpedanceMethod.ReferenceResistor),
            Stimulus: nameof(ImpedanceStimulus.Chirp),
            ReferenceOhms: 100.0,
            RightChannelIsReference: false,
            FrequencySpanHz: 24000.0,
            ResolutionMultiplier: 1,
            ChirpStartHz: 10.0,
            LevelValue: -20.0,
            LevelUnit: nameof(ThdSweepLevelUnit.Dbv),
            SmoothingDenominator: 48,
            AverageCount: 2,
            CalibrationLevelValue: ImpedanceRequest.DefaultCalibrationLevelDbv,
            CableMode: nameof(ImpedanceCableMode.None),
            FixedCableOhms: 0.0,
            FixedCableMillihenry: 0.0,
            CalibrationStimulus: nameof(ImpedanceStimulus.Chirp),
            StartFreqHz: 20.0,
            EndFreqHz: 20000.0,
            StepsPerOctave: 5,
            AdaptiveResolution: true,
            UseGlobalFixture: useGlobalFixture), SerializerOptions);

    /// <summary>
    /// What a create-or-update of a measurement type should write to <c>params_json</c>.
    ///
    /// The interesting case is an omitted <paramref name="requestedJson"/>, which means "snapshot
    /// whatever is configured right now". For an FFT type that is the live pipeline/device state,
    /// which the host can build on its own. For a sweep it is not: a configured sweep exists only
    /// as SweepConfigPanel's form, and <see cref="BuildCurrentParametersJson"/> would hand back an
    /// <see cref="FftSpectrumParameters"/> — replacing the type's sweep settings with something
    /// <see cref="ApplyAsync"/> cannot read back. So a sweep keeps <paramref name="existingParamsJson"/>
    /// instead, or starts from the panel's defaults when there is none.
    /// </summary>
    /// <param name="existingParamsJson">The type's current stored params, or null when creating one.</param>
    /// <param name="globalCalibrationAvailable">
    /// Whether a global impedance fixture calibration is stored, which is what a brand-new impedance
    /// type starts on. Passed in rather than read here because this method is synchronous and the
    /// configuration database is not; both callers are already in async handlers. Defaults to false,
    /// so every other kind and every existing test call site is unaffected.
    /// </param>
    public string ResolveTypeParamsJson(
        string? requestedJson,
        string kind,
        string? existingParamsJson,
        StagedMeasurementConfig? staged,
        bool globalCalibrationAvailable = false)
    {
        if (!string.IsNullOrWhiteSpace(requestedJson))
            return requestedJson;

        // Before the broad predicate below, which both response kinds answer true to: a new stepped
        // type seeded from the chirp panel's defaults would come up configured for a measurement it
        // cannot run.
        if (kind == SteppedResponseKind)
            return existingParamsJson ?? DefaultSteppedResponseParametersJson();

        if (IsImpedanceKind(kind))
            return existingParamsJson ?? DefaultImpedanceParametersJson(globalCalibrationAvailable);

        if (IsImpulseResponseKind(kind))
            return existingParamsJson ?? DefaultImpulseResponseParametersJson();

        if (IsFrequencyResponseKind(kind))
            return existingParamsJson ?? DefaultFrequencyResponseParametersJson();

        if (IsSweepKind(kind))
        {
            return existingParamsJson
                ?? (kind == ThdVsAmplitudeKind ? DefaultAmplitudeSweepParametersJson() : DefaultSweepParametersJson());
        }

        // Creating (no stored params yet) with nothing staged: seed from the live state, but with a
        // generator channel switched on so the new type has something to Run.
        if (existingParamsJson is null && staged is null)
            return BuildDefaultParametersJsonForNewType();

        return BuildCurrentParametersJson(staged);
    }

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

        // The settle time is a run record, set here and not in BuildParameters: that method also
        // writes a type's own params_json, where the field has to stay null.
        var parameters = BuildParameters() with { CaptureDelayMs = session.LastAppliedCaptureDelayMs };
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
    /// Saves the last completed sweep as one measurement result with one capture per frequency.
    ///
    /// Reads <see cref="ThdSweepRunner.Status"/> rather than taking the points as an argument, for
    /// the same reason <see cref="SaveAsync"/> reads the pipeline's retained snapshot: what gets
    /// saved must be exactly what is on screen, not a separately-supplied copy that could differ.
    /// A cancelled sweep is saveable -- a partial sweep still measured the part it covered.
    /// </summary>
    public async Task<long> SaveSweepAsync(long measurementTypeId, string name, string? notes, CancellationToken ct)
    {
        var status = sweepRunner.Status;
        if (status.Running)
            throw new InvalidOperationException("Wait for the sweep to finish (or cancel it) before saving.");
        if (status.Points.Count == 0)
            throw new InvalidOperationException("No sweep has been run yet.");

        var request = sweepRunner.LastRequest
            ?? throw new InvalidOperationException("No sweep has been run yet.");

        bool amplitudeAxis = request.Axis == SweepAxis.Amplitude;

        string paramsJson = amplitudeAxis
            ? JsonSerializer.Serialize(
                new ThdVsAmplitudeParameters(
                    request.FixedFreqHz, request.StartAmplitudeVolt, request.EndAmplitudeVolt,
                    request.StepsPerOctave, request.LoadOhms, request.ProcessLeft, request.ProcessRight,
                    request.FftSize, request.Window, request.AverageCount, request.SampleRateHz,
                    request.MeasureNoiseFloor,
                    CaptureDelayMs: session.LastAppliedCaptureDelayMs),
                SerializerOptions)
            : JsonSerializer.Serialize(
                new ThdVsFrequencyParameters(
                    request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave,
                    request.LevelMode.ToString(), request.LevelValue, request.LevelUnit.ToString(),
                    request.LoadOhms, request.ProcessLeft, request.ProcessRight,
                    request.FftSize, request.Window, request.AverageCount, request.SampleRateHz,
                    request.MeasureNoiseFloor, status.GeneratorAmpDbv,
                    request.AdaptiveResolution, request.ThdMethod.ToString(),
                    CaptureDelayMs: session.LastAppliedCaptureDelayMs),
                SerializerOptions);

        // One capture per sweep point, in sweep order. Points arrive per channel, so group them back
        // together first -- both channels of one point share a capture row. The grouping key is the
        // sweep's own axis: grouping an amplitude sweep by frequency would collapse all fifty of its
        // points into a single capture, since they all play the same tone.
        var captures = (amplitudeAxis
                ? status.Points.GroupBy(p => p.GeneratorDbv)
                : status.Points.GroupBy(p => p.FreqHz))
            .OrderBy(g => g.Key)
            .Select((group, index) => new SweepCaptureInput(
                index,
                // capture.freq_hz stays the tone that was played, on either axis -- for an amplitude
                // sweep that is the one fixed frequency, which is true and keeps
                // GetSweepPointsAsync's fundamental_hz recovery working unchanged.
                group.First().FreqHz,
                BuildSweepMetrics(group),
                BuildSweepPreviews(group)))
            .ToList();

        var info = session.Info;
        var saveRequest = new SweepSaveRequest(
            measurementTypeId, name, notes,
            paramsJson,
            info?.ProductName, info?.SerialNumber, info?.FirmwareVersion, info?.IsSimulated ?? false,
            request.SampleRateHz, request.FftSize,
            (request.ProcessLeft ? 1 : 0) + (request.ProcessRight ? 1 : 0),
            session.InputRangeDbv, session.OutputRangeDbv,
            captures);

        return await measurementRepository.SaveSweepResultAsync(saveRequest, ct);
    }

    /// <summary>
    /// Saves the frequency-response curves currently on screen as one measurement result.
    ///
    /// <para>Reads <see cref="FrequencyResponseRunner.Status"/> rather than taking the traces as an
    /// argument, for the same reason <see cref="SaveSweepAsync"/> reads the runner's: what is saved
    /// must be exactly what is displayed, not a separately-supplied copy that could differ.</para>
    /// </summary>
    public async Task<long> SaveFrequencyResponseAsync(long measurementTypeId, string name, string? notes, CancellationToken ct)
    {
        var status = responseRunner.Status;
        if (status.Running)
            throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before saving.");
        if (status.Traces.Count == 0)
            throw new InvalidOperationException("No frequency response has been measured yet.");

        var request = responseRunner.LastRequest
            ?? throw new InvalidOperationException("No frequency response has been measured yet.");

        string paramsJson = JsonSerializer.Serialize(
            new FrequencyResponseParameters(
                request.FrequencySpanHz, request.ResolutionMultiplier,
                request.LevelMode.ToString(), request.LevelValue, request.LevelUnit.ToString(),
                request.RightChannelIsReference, request.SmoothingDenominator, request.AverageCount,
                status.GeneratorAmpDbv,
                ChirpStartHz: request.ChirpStartHz,
                LoadOhms: request.LoadOhms,
                BandwidthReference: request.BandwidthReference.ToString(),
                BandwidthReferenceLowHz: request.BandwidthReferenceLowHz,
                BandwidthReferenceHighHz: request.BandwidthReferenceHighHz,
                CaptureDelayMs: session.LastAppliedCaptureDelayMs),
            SerializerOptions);

        var info = session.Info;
        var saveRequest = new ResponseSaveRequest(
            measurementTypeId, name, notes,
            paramsJson,
            info?.ProductName, info?.SerialNumber, info?.FirmwareVersion, info?.IsSimulated ?? false,
            request.SampleRateHz, request.FftSize,
            Channels: 2,
            session.InputRangeDbv, session.OutputRangeDbv,
            [.. status.Traces.Select(ToTraceInput)]);

        return await measurementRepository.SaveResponseResultAsync(saveRequest, ct);
    }

    /// <summary>
    /// Saves the stepped-sine curves currently on screen as one measurement result.
    ///
    /// <para>Everything below the parameters record is shared with
    /// <see cref="SaveFrequencyResponseAsync"/> — same <see cref="ResponseSaveRequest"/>, same
    /// <c>response_trace</c> rows, same reader on the way back out. Only the settings differ, and
    /// only because the two are configured differently.</para>
    ///
    /// <para>Reads <see cref="SteppedResponseRunner.Status"/> rather than taking the traces as an
    /// argument, for the same reason the other two save methods read their runner's: what is saved
    /// must be exactly what is displayed, not a separately-supplied copy that could differ.</para>
    /// </summary>
    public async Task<long> SaveSteppedResponseAsync(long measurementTypeId, string name, string? notes, CancellationToken ct)
    {
        var status = steppedResponseRunner.Status;
        if (status.Running)
            throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before saving.");
        if (status.Traces.Count == 0)
            throw new InvalidOperationException("No stepped response has been measured yet.");

        var request = steppedResponseRunner.LastRequest
            ?? throw new InvalidOperationException("No stepped response has been measured yet.");

        string paramsJson = JsonSerializer.Serialize(
            new SteppedResponseParameters(
                request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave,
                request.LevelMode.ToString(), request.LevelValue, request.LevelUnit.ToString(),
                request.LoadOhms, request.GenerateLeft, request.GenerateRight,
                request.FftSize, request.Window, request.AverageCount, request.SampleRateHz,
                request.AdaptiveResolution, request.RightChannelIsReference,
                status.GeneratorAmpDbv,
                BandwidthReference: request.BandwidthReference.ToString(),
                BandwidthReferenceLowHz: request.BandwidthReferenceLowHz,
                BandwidthReferenceHighHz: request.BandwidthReferenceHighHz,
                CaptureDelayMs: session.LastAppliedCaptureDelayMs),
            SerializerOptions);

        var info = session.Info;
        var saveRequest = new ResponseSaveRequest(
            measurementTypeId, name, notes,
            paramsJson,
            info?.ProductName, info?.SerialNumber, info?.FirmwareVersion, info?.IsSimulated ?? false,
            // The as-run rate and size, which under adaptive resolution are neither of the requested
            // ones -- and the size is the one the *first* point used, since it varies across the run.
            status.SampleRateHz, status.FftSize,
            Channels: 2,
            session.InputRangeDbv, session.OutputRangeDbv,
            [.. status.Traces.Select(ToTraceInput)]);

        return await measurementRepository.SaveResponseResultAsync(saveRequest, ct);
    }


    /// <summary>
    /// Saves the impulse response currently on screen as one measurement result.
    ///
    /// <para>The gated magnitude and phase curves go into the same <c>response_trace</c> rows a
    /// frequency response uses, because they are the same curves; only the impulse responses and the
    /// harmonic curves need tables of their own.</para>
    ///
    /// <para>The impulse is written at <b>full resolution</b>, not as the decimated display columns
    /// the status carries — a decimated impulse response cannot be re-gated or re-transformed, which
    /// is the only reason to keep one. It is truncated around the peak to
    /// <see cref="MaxStoredIrSamples"/> so a row is bounded at every sample rate.</para>
    /// </summary>
    public async Task<long> SaveImpulseResponseAsync(long measurementTypeId, string name, string? notes, CancellationToken ct)
    {
        var status = impulseRunner.Status;
        if (status.Running)
            throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before saving.");
        if (status.Impulses.Count == 0)
            throw new InvalidOperationException("No impulse response has been measured yet.");

        var request = impulseRunner.LastRequest
            ?? throw new InvalidOperationException("No impulse response has been measured yet.");

        var impulse = impulseRunner.LastImpulse
            ?? throw new InvalidOperationException("No impulse response has been measured yet.");

        string paramsJson = JsonSerializer.Serialize(
            new ImpulseResponseParameters(
                request.FrequencySpanHz, request.ResolutionMultiplier,
                request.LevelMode.ToString(), request.LevelValue, request.LevelUnit.ToString(),
                request.SmoothingDenominator, request.AverageCount,
                request.ReferenceMode.ToString(), request.ReferenceCalibrationId,
                request.GatePreMs, request.GatePostMs, request.GateWindow.ToString(),
                request.ExtractHarmonics, request.MaxHarmonicOrder,
                status.GeneratorAmpDbv, request.ChirpStartHz, request.LoadOhms,
                CaptureDelayMs: session.LastAppliedCaptureDelayMs),
            SerializerOptions);

        var info = session.Info;
        var saveRequest = new ImpulseSaveRequest(
            measurementTypeId, name, notes,
            paramsJson,
            info?.ProductName, info?.SerialNumber, info?.FirmwareVersion, info?.IsSimulated ?? false,
            request.SampleRateHz, request.FftSize,
            Channels: 2,
            session.InputRangeDbv, session.OutputRangeDbv,
            [.. status.Traces.Select(ToTraceInput)],
            [ToImpulseInput(request, status, impulse)],
            [.. status.Harmonics.Select(ToHarmonicInput)]);

        return await measurementRepository.SaveImpulseResultAsync(saveRequest, ct);
    }

    /// <summary>
    /// How much of the gated impulse response is stored, either side of its peak. At the finest
    /// resolution the gated span is 1.45 s of which everything past a few hundred milliseconds is
    /// analyzer noise floor, so this bounds a row at every sample rate while keeping far more than
    /// any display gate or decay analysis reads.
    /// </summary>
    public const int MaxStoredIrSamples = 131072;

    private static ImpulseTraceInput ToImpulseInput(
        ImpulseResponseRequest request, ImpulseResponseStatus status, ImpulseResponseResult impulse)
    {
        var displayed = status.Impulses.First(i => i.Kind == FrequencyResponseTraceKind.Left);

        // Centred on the peak and read circularly, because the impulse response of a periodic sweep
        // is a circular record: a peak near index 0 has its pre-impulse ringing at the far end of the
        // array, and a linear read would replace that half with the record's own tail.
        int keep = Math.Min(MaxStoredIrSamples, impulse.Gated.Length);
        int before = keep / 2;

        var ir = new float[keep];
        for (int j = 0; j < keep; j++)
        {
            int index = impulse.PeakIndex - before + j;
            ir[j] = (float)impulse.Gated[((index % impulse.Gated.Length) + impulse.Gated.Length) % impulse.Gated.Length];
        }

        return new ImpulseTraceInput(
            ImpulseTraceKind.Left,
            request.SampleRateHz,
            PeakIndex: before,
            displayed.PeakVolts,
            request.GatePreMs,
            request.GatePostMs,
            request.GateWindow.ToString(),
            displayed.ResolutionLimitHz,
            request.ReferenceMode.ToString(),
            status.ReferenceCalibrationName,
            status.LatencySamples,
            ir);
    }

    private static HarmonicCurveInput ToHarmonicInput(HarmonicCurve curve) =>
        new(curve.Order, curve.UpperValidHz, curve.FundamentalHz, curve.LevelDbc);

    /// <summary>
    /// Saves the impedance curves currently on screen. Reads the runner's own status rather than
    /// taking the data as an argument, so what is stored is exactly what is displayed.
    /// </summary>
    public async Task<long> SaveImpedanceAsync(long measurementTypeId, string name, string? notes, CancellationToken ct)
    {
        var status = impedanceRunner.Status;
        if (status.Running)
            throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before saving.");
        if (status.Traces.Count == 0)
            throw new InvalidOperationException("No impedance has been measured yet.");

        var request = impedanceRunner.LastRequest
            ?? throw new InvalidOperationException("No impedance has been measured yet.");

        // The calibrations are stored with the result too, not just on the type: a saved measurement
        // that cannot say which fixture calibration produced it is not reproducible.
        string paramsJson = JsonSerializer.Serialize(
            new ImpedanceParameters(
                request.Method.ToString(),
                request.Stimulus.ToString(),
                request.ReferenceOhms,
                request.RightChannelIsReference,
                request.FrequencySpanHz,
                request.ResolutionMultiplier,
                request.ChirpStartHz,
                request.LevelValue,
                request.LevelUnit.ToString(),
                request.SmoothingDenominator,
                request.AverageCount,
                request.CableMode.ToString(),
                request.FixedCableOhms,
                request.FixedCableMillihenry,
                // Named from here on: this record has a run of defaulted parameters, and a positional
                // call silently shifts every one of them the next time a field is inserted.
                CalibrationLevelValue: request.CalibrationLevelValue,
                AnalyzerInputOhms: request.AnalyzerInputOhms,
                ApplySweepGate: request.ApplySweepGate,
                OpenCal: request.OpenCal,
                ShortCal: request.ShortCal,
                GeneratorAmpDbv: status.GeneratorAmpDbv,
                CalibrationStimulus: request.CalibrationStimulus.ToString(),
                StartFreqHz: request.StartFreqHz,
                EndFreqHz: request.EndFreqHz,
                StepsPerOctave: request.StepsPerOctave,
                AdaptiveResolution: request.AdaptiveResolution,
                CaptureDelayMs: session.LastAppliedCaptureDelayMs),
            SerializerOptions);

        var info = session.Info;
        var saveRequest = new ResponseSaveRequest(
            measurementTypeId, name, notes,
            paramsJson,
            info?.ProductName, info?.SerialNumber, info?.FirmwareVersion, info?.IsSimulated ?? false,
            request.SampleRateHz, request.FftSize,
            Channels: 2,
            session.InputRangeDbv, session.OutputRangeDbv,
            [.. status.Traces.Select(ToImpedanceTraceInput)]);

        return await measurementRepository.SaveResponseResultAsync(saveRequest, ct);
    }

    /// <summary>
    /// An impedance curve as a <c>response_trace</c> row.
    ///
    /// <para>The two bandwidth blocks are written as zeros: -1 dB and -3 dB corners are a response
    /// concept and mean nothing on an impedance curve, but the columns are NOT NULL. Nothing reads
    /// them back for these rows — the reader branches on <c>kind</c> — and the peak, which for an
    /// impedance <i>is</i> meaningful, is the resonance.</para>
    /// </summary>
    private static ResponseTraceInput ToImpedanceTraceInput(ImpedanceTrace trace) =>
        new(
            ImpedanceTraceKindName(trace.Kind),
            trace.StartFreqHz,
            trace.EndFreqHz,
            trace.PeakDbOhms,
            trace.PeakFreqHz,
            new ResponseBandwidth(0.0, 0.0, false, false),
            new ResponseBandwidth(0.0, 0.0, false, false),
            trace.FreqHz,
            trace.MagnitudeMinDbOhms,
            trace.MagnitudeMaxDbOhms,
            trace.PhaseDeg,
            trace.Coherence);

    private static string ImpedanceTraceKindName(ImpedanceTraceKind kind) => kind switch
    {
        ImpedanceTraceKind.Impedance => ResponseTraceKind.Impedance,
        ImpedanceTraceKind.OpenCal => ResponseTraceKind.ImpedanceOpen,
        ImpedanceTraceKind.ShortCal => ResponseTraceKind.ImpedanceShort,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown impedance trace kind."),
    };

    private static ResponseTraceInput ToTraceInput(FrequencyResponseTrace trace) =>
        new(
            TraceKindName(trace.Kind),
            trace.StartFreqHz,
            trace.EndFreqHz,
            trace.PeakDb,
            trace.PeakFreqHz,
            new ResponseBandwidth(trace.OneDb.LowerHz, trace.OneDb.UpperHz, trace.OneDb.LowerClamped, trace.OneDb.UpperClamped),
            new ResponseBandwidth(trace.ThreeDb.LowerHz, trace.ThreeDb.UpperHz, trace.ThreeDb.LowerClamped, trace.ThreeDb.UpperClamped),
            trace.FreqHz,
            trace.MagnitudeMinDb,
            trace.MagnitudeMaxDb,
            trace.PhaseDeg,
            trace.Coherence,
            trace.ReferenceDb,
            trace.ReferenceMode.ToString());

    private static string TraceKindName(FrequencyResponseTraceKind kind) => kind switch
    {
        FrequencyResponseTraceKind.Left => ResponseTraceKind.Left,
        FrequencyResponseTraceKind.Right => ResponseTraceKind.Right,
        FrequencyResponseTraceKind.Gain => ResponseTraceKind.Gain,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown response trace kind."),
    };

    /// <summary>
    /// One frequency's preview traces, per channel and kind (FFT/scope) -- absent entirely for a
    /// point whose channel never captured one (the sweep runner always turns capture on, so in
    /// practice this only happens for a sweep saved before this feature existed... except there is
    /// no such sweep, since it isn't re-run from old state; kept defensive rather than assumed).
    /// </summary>
    private static List<CapturePreviewInput> BuildSweepPreviews(IEnumerable<ThdSweepPoint> pointsAtOneFrequency)
    {
        var previews = new List<CapturePreviewInput>();
        foreach (var point in pointsAtOneFrequency)
        {
            if (point.PreviewFft is { } fft)
                previews.Add(new CapturePreviewInput(point.Channel, CapturePreviewKind.Fft, fft.X, fft.Min, fft.Max));
            if (point.PreviewScope is { } scope)
                previews.Add(new CapturePreviewInput(point.Channel, CapturePreviewKind.Scope, scope.X, scope.Min, scope.Max));
        }

        // capture_preview is UNIQUE on (capture_id, channel, kind), so two points on one channel at
        // the same frequency reach SQLite as a raw constraint violation -- a 500 with a stack trace,
        // long after the sweep that caused it, naming nothing the user could act on. The grouping in
        // SaveSweepAsync is what should make this impossible (one point per channel per frequency),
        // so reaching here means the sweep runner produced something it should not have; say which
        // point, rather than letting the database phrase it.
        var duplicate = previews
            .GroupBy(p => (p.Channel, p.Kind))
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            var first = pointsAtOneFrequency.FirstOrDefault();
            double freqHz = first?.FreqHz ?? double.NaN;
            double genDbv = first?.GeneratorDbv ?? double.NaN;
            throw new InvalidOperationException(
                $"The sweep produced {duplicate.Count()} '{duplicate.Key.Kind}' previews for channel " +
                $"{duplicate.Key.Channel} at {freqHz:0.###} Hz / {genDbv:0.##} dBV, but a sweep point may only be " +
                "measured once per channel. Re-run the sweep before saving; if this repeats, restart the app and report it.");
        }

        return previews;
    }

    /// <summary>
    /// One frequency's metrics, per channel. Names follow the same convention
    /// <see cref="SaveAsync"/> uses (<c>thd_percent</c>, <c>h{n}_dbc</c>, ...) so the metric index
    /// and the CSV exporter treat a sweep point exactly like a spectrum capture.
    /// </summary>
    private static List<MeasurementMetric> BuildSweepMetrics(IEnumerable<ThdSweepPoint> pointsAtOneFrequency)
    {
        var metrics = new List<MeasurementMetric>();
        foreach (var point in pointsAtOneFrequency)
        {
            int ch = point.Channel;
            metrics.Add(new MeasurementMetric(ch, "fundamental_hz", point.FreqHz, "Hz"));
            metrics.Add(new MeasurementMetric(ch, "fundamental_dbv", point.FundamentalDbv, "dBV"));
            metrics.Add(new MeasurementMetric(ch, "gain_db", point.GainDb, "dB"));
            metrics.Add(new MeasurementMetric(ch, "power_watt", point.PowerWatt, "W"));
            metrics.Add(new MeasurementMetric(ch, "thd_percent", point.ThdPercent, "%"));
            metrics.Add(new MeasurementMetric(ch, "thd_n_percent", point.ThdNPercent, "%"));
            metrics.Add(new MeasurementMetric(ch, "snr_db", point.SnrDb, "dB"));
            metrics.Add(new MeasurementMetric(ch, "d6plus_percent", point.D6PlusPercent, "%"));
            metrics.Add(new MeasurementMetric(ch, "noise_percent", point.NoisePercent, "%"));

            // The level this point was played at. Constant across a frequency sweep and the x axis of
            // an amplitude one -- and, being written by every sweep, also what tells the two apart
            // when a stored sweep is read back without its params_json (CsvExporter.ExportSweep,
            // MeasurementRepository.SelectHeadlineCapture both key off it varying).
            metrics.Add(new MeasurementMetric(ch, "generator_dbv", point.GeneratorDbv, "dBV"));
            metrics.Add(new MeasurementMetric(ch, "generator_volt", Math.Pow(10.0, point.GeneratorDbv / 20.0), "V"));

            for (int i = 0; i < point.HarmonicPercent.Count; i++)
                metrics.Add(new MeasurementMetric(ch, $"h{i + 2}_percent", point.HarmonicPercent[i], "%"));

            // The frequency-normalized figures, written only where they exist: a conventional sweep
            // has none, and a normalized one has none at the points near the top of its extended
            // grid. Absent, not 0 -- the same reasoning as the short harmonic list above, since a 0
            // reads back as a real -inf dBc point.
            if (point.NormalizedThdPercent is { } nThd)
                metrics.Add(new MeasurementMetric(ch, "nthd_percent", nThd, "%"));
            if (point.NormalizedD6PlusPercent is { } nD6Plus)
                metrics.Add(new MeasurementMetric(ch, "nd6plus_percent", nD6Plus, "%"));
            if (point.NormalizedHarmonicPercent is { } normalized)
            {
                for (int i = 0; i < normalized.Count; i++)
                {
                    if (normalized[i] is { } value)
                        metrics.Add(new MeasurementMetric(ch, $"nh{i + 2}_percent", value, "%"));
                }
            }
        }

        return metrics;
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

        // A sweep or response type's params_json is its own record, not an FftSpectrumParameters,
        // and none of it belongs on the spectrum pipeline either -- each runner applies its own
        // analysis settings when it starts, and the config panel owns the rest. So this hands back
        // the live device state and touches nothing.
        //
        // Deliberately the broad predicate rather than IsSweepKind: deserializing another kind's
        // JSON as an FftSpectrumParameters does not throw, it yields an object full of nulls, and
        // the first read of one is a NullReferenceException 500 when the type is merely *selected*.
        // That is what a response type did before this line was widened.
        if (HasNoLiveConfiguration(type.Kind))
            return ToAppliedResponse(BuildParameters());

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

        return ToAppliedResponse(parameters);
    }

    private static MeasurementTypeAppliedResponse ToAppliedResponse(FftSpectrumParameters parameters) =>
        new(
            DeviceSessionService.ToResponse(parameters.Generator.Left),
            DeviceSessionService.ToResponse(parameters.Generator.Right),
            parameters.Generator.SnapToBin,
            parameters.SampleRateHz,
            parameters.ChannelCount,
            parameters.BlockFrameCount,
            parameters.InputRangeDbv,
            parameters.OutputRangeDbv,
            parameters.AutoRangeEnabled,
            parameters.SelectedMetrics,
            parameters.ScopePeriods,
            parameters.ChartYTopDbv,
            parameters.ChartYBottomDbv,
            parameters.ChartXFromHz,
            parameters.ChartXToHz,
            parameters.ChartThickLines,
            parameters.ChartShowDataPoints,
            parameters.ChartShowMinMax,
            parameters.ChartShowPeakHold,
            parameters.ChartAutofitOnDone,
            parameters.ChartShowWaterfall,
            parameters.ChartShowOscilloscope,
            parameters.ChartShowHarmonics,
            parameters.ChartShowOtherPeaks);

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

        double sampleRateHz = staged?.SampleRateHz ?? streamConfig?.SampleRateHz ?? 192000.0;
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
            generator, staged?.SelectedMetrics, staged?.ScopePeriods,
            staged?.ChartYTopDbv, staged?.ChartYBottomDbv, staged?.ChartXFromHz, staged?.ChartXToHz,
            staged?.ChartThickLines, staged?.ChartShowDataPoints, staged?.ChartShowMinMax, staged?.ChartShowPeakHold,
            staged?.ChartAutofitOnDone,
            staged?.ChartShowWaterfall, staged?.ChartShowOscilloscope, staged?.ChartShowHarmonics, staged?.ChartShowOtherPeaks);
    }

    public string BuildCurrentParametersJson(StagedMeasurementConfig? staged = null) =>
        JsonSerializer.Serialize(BuildParameters(staged), SerializerOptions);

    /// <summary>
    /// Same as <see cref="BuildCurrentParametersJson"/> with no staged config, except a brand-new
    /// type gets sensible defaults instead of cloning whatever the live session happens to be
    /// sitting on: the left channel is forced on when the generator is fully silent, and SnapToBin
    /// is always forced on.
    ///
    /// <para>The channel case: "fully silent" is the common case here — stopRun() disables both
    /// channels on every project/type switch (see deviceControl.ts), so by the time "+ New
    /// measurement" is pressed the live generator is silent regardless of what any previous type
    /// actually had configured — and a brand-new type needs something to Run rather than two
    /// disabled channels.</para>
    ///
    /// <para>Autofit-when-done is switched on for the same reason SnapToBin is: it is the default a
    /// fresh measurement should start from, and the live session carries no value for it at all.</para>
    ///
    /// <para>SnapToBin is unconditional rather than gated like the channel: unlike "silent", "off"
    /// has no fallback state to detect it against — <see cref="GeneratorConfig.SnapToBin"/> is a
    /// plain bool, so a session left with it off from an earlier experiment (or a type that was
    /// itself saved with it off, which is a legitimate choice for one measurement) would otherwise
    /// silently seed every subsequent "+ New measurement" the same way, off having crept in as the
    /// session's new normal instead of staying the deliberate exception it's meant to be.</para>
    ///
    /// <para>Deliberately not folded into <see cref="BuildParameters"/> itself: that method also
    /// backs the update-type endpoint's own blank-params fallback, which a bulk project save relies
    /// on to clone each dirty-but-not-currently-open type's live state verbatim (see
    /// ProjectsEndpoints.cs's PUT handler and useProjects.saveProject) — forcing either default
    /// there would silently rewrite those types' saved generator config.</para>
    /// </summary>
    public string BuildDefaultParametersJsonForNewType()
    {
        var parameters = BuildParameters();
        if (!parameters.Generator.Left.Enabled && !parameters.Generator.Right.Enabled)
            parameters = parameters with { Generator = parameters.Generator with { Left = parameters.Generator.Left with { Enabled = true } } };
        if (!parameters.Generator.SnapToBin)
            parameters = parameters with { Generator = parameters.Generator with { SnapToBin = true } };
        // "Autofit when measurement done" starts on for a new type. Only here, never in
        // BuildParameters: a stored type's null keeps reading as off, so nothing saved before this
        // default existed starts refitting its axes on the next load.
        parameters = parameters with { ChartAutofitOnDone = true };
        return JsonSerializer.Serialize(parameters, SerializerOptions);
    }

    private static int ChannelIndex(StreamChannel channel) => channel == StreamChannel.Left ? 0 : 1;
}
