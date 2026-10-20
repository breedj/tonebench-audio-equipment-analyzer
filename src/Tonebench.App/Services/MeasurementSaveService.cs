using System.Text.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Tonebench.App.Endpoints;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Orchestrates "save current capture" and "apply a measurement-type's parameters back to the live
/// app". Kept out of the endpoint file so it is testable without a host, matching
/// <c>GeneratorEndpoints.TryMapChannel</c>.
/// </summary>
public sealed class MeasurementSaveService(
    DeviceSessionService session,
    SpectrumPipeline spectrumPipeline,
    ThdSweepRunner sweepRunner,
    ImdSweepRunner imdSweepRunner,
    FrequencyResponseRunner responseRunner,
    SteppedResponseRunner steppedResponseRunner,
    CrosstalkRunner crosstalkRunner,
    OutputImpedanceRunner outputImpedanceRunner,
    ImpulseResponseRunner impulseRunner,
    ImpedanceRunner impedanceRunner,
    ProjectRepository projectRepository,
    MeasurementRepository measurementRepository,
    IOptions<JsonOptions> jsonOptions,
    UserWeightingSetting? userWeighting = null,
    // Optional and last, like the weighting above it: dozens of tests construct this service
    // directly, and a load is not something a save needs in order to work. DI fills it in the app.
    LoadSessionService? load = null)
{
    // Reuses the app's own HTTP JSON options (JsonStringEnumConverter etc, configured once in
    // Program.cs) rather than a second, independently-drifting JsonSerializerOptions instance —
    // params_json round-trips WindowType/AveragingMode/GeneratorKind the same way the REST API does.
    private JsonSerializerOptions SerializerOptions => jsonOptions.Value.SerializerOptions;

    /// <summary>
    /// The <c>measurement_type.kind</c> value for the live FFT spectrum screen. Mirrors
    /// web/src/measurements/kinds.ts.
    ///
    /// <para>The one kind with no predicate of its own, because it is what every <c>Is*Kind</c>
    /// below says no to -- see <see cref="HasNoLiveConfiguration"/>. Named here all the same, for
    /// the one place that has to ask the question positively rather than by elimination
    /// (<see cref="SetResultAnnotationsAsync"/>): a future kind must be refused there, not treated
    /// as an FFT result.</para>
    /// </summary>
    public const string FftSpectrumKind = "FFTSpectrum";

    /// <summary>The <c>measurement_type.kind</c> value for a THD-vs-frequency sweep. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string ThdVsFrequencyKind = "ThdVsFrequency";

    /// <summary>The <c>measurement_type.kind</c> value for a THD-vs-amplitude sweep. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string ThdVsAmplitudeKind = "ThdVsAmplitude";

    /// <summary>The <c>measurement_type.kind</c> value for an IMD-vs-amplitude sweep. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string ImdVsAmplitudeKind = "ImdVsAmplitude";

    /// <summary>
    /// Whether a kind's result is a run of sweep points rather than a single spectrum. The
    /// distinction, not the individual kind, is what every branch here actually cares about: a
    /// sweep type has no live server state to snapshot, its params_json is not an
    /// <see cref="FftSpectrumParameters"/>, and its result rows carry no spectra.
    /// </summary>
    public static bool IsSweepKind(string kind) => kind is ThdVsFrequencyKind or ThdVsAmplitudeKind or ImdVsAmplitudeKind;

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

    /// <summary>The <c>measurement_type.kind</c> value for a crosstalk measurement. Mirrors web/src/measurements/kinds.ts.</summary>
    public const string CrosstalkKind = "Crosstalk";

    /// <summary>An amplifier's output impedance and damping factor by the load-step method — <see cref="OutputImpedanceRunner"/>.</summary>
    public const string OutputImpedanceKind = "OutputImpedance";

    /// <summary>
    /// Whether a kind's result is a crosstalk curve. Its own predicate rather than a third arm of
    /// <see cref="IsFrequencyResponseKind"/>, for the impedance reason: it stores its curves in the
    /// <c>response_trace</c> rows, but the start endpoint, the settings record and the report
    /// sections are its own. What it shares is the storage and the chart.
    /// </summary>
    public static bool IsCrosstalkKind(string kind) => kind is CrosstalkKind;

    /// <summary>
    /// Its own predicate, like <see cref="IsImpedanceKind"/>: the result's Zout row is an impedance
    /// curve (ohms in dB) and its gain rows are response curves, so it needs its own arm wherever
    /// either family branches.
    /// </summary>
    public static bool IsOutputImpedanceKind(string kind) => kind is OutputImpedanceKind;

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
        IsSweepKind(kind) || IsFrequencyResponseKind(kind) || IsImpulseResponseKind(kind) || IsImpedanceKind(kind) || IsCrosstalkKind(kind)
        || IsOutputImpedanceKind(kind);

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
            // The band a new sweep starts on, matching the panels' own default -- see
            // BandwidthField.vue. Written rather than left null: a *configured* type does have
            // a band, and null is reserved for a row that predates the setting.
            MeasurementLoFreqHz: 20.0,
            MeasurementHiFreqHz: 20000.0,
            // Off, and written rather than left null so the stored type says so: a chart that
            // refits its own axes the moment a run ends moves the window out from under whoever
            // set it, which is Joost's call, 2026-10-17. The checkbox is still there.
            ChartAutofitOnDone: false), SerializerOptions);

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
            // The band a new sweep starts on, matching the panels' own default -- see
            // BandwidthField.vue. Written rather than left null: a *configured* type does have
            // a band, and null is reserved for a row that predates the setting.
            MeasurementLoFreqHz: 20.0,
            MeasurementHiFreqHz: 20000.0,
            ChartAutofitOnDone: false), SerializerOptions);

    /// <summary>
    /// A brand-new IMD-vs-amplitude type's <c>params_json</c>, on the same terms as
    /// <see cref="DefaultAmplitudeSweepParametersJson"/> and kept in step with
    /// <c>ImdSweepConfigPanel.vue</c>'s own defaults: the SMPTE pair, the QA40x's order default.
    /// </summary>
    public string DefaultImdSweepParametersJson()
    {
        var smpte = IntermodulationPresets.Resolve(IntermodulationPreset.Smpte);
        return JsonSerializer.Serialize(new ImdVsAmplitudeParameters(
            Preset: nameof(IntermodulationPreset.Smpte),
            ToneLowHz: smpte.LowHz,
            ToneHighHz: smpte.HighHz,
            LowToHighRatio: smpte.LowToHighRatio,
            Method: smpte.Method.ToString(),
            MaxOrder: smpte.DefaultMaxOrder,
            StartLevelVolt: 0.001,
            EndLevelVolt: 1.0,
            StepsPerOctave: 5,
            LoadOhms: 8.0,
            ProcessLeft: true,
            ProcessRight: false,
            FftSize: ThdSweepRequest.AmplitudeSweepFftSize,
            Window: ImdSweepRequest.Window,
            AverageCount: 4,
            SampleRateHz: ThdSweepRequest.AmplitudeSweepSampleRateHz,
            MeasureNoiseFloor: true,
            // The band a new sweep starts on, matching the panels' own default -- see
            // BandwidthField.vue. Written rather than left null: a *configured* type does have
            // a band, and null is reserved for a row that predates the setting.
            MeasurementLoFreqHz: 20.0,
            MeasurementHiFreqHz: 20000.0,
            ChartAutofitOnDone: false), SerializerOptions);
    }

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
    /// A brand-new output-impedance type's <c>params_json</c>, kept in step with
    /// <c>OutputImpedanceConfigPanel.vue</c>'s own defaults: the tutorial's run — 20 Hz–20 kHz at 3
    /// points per octave, 1 W into 8 Ω, the damping factor against 8 Ω — with 4 Ω as the second load
    /// rather than the tutorial's open circuit (safe for a tube amplifier), and magnitude only, since
    /// the referenced method needs a second cable the bench may not have.
    /// </summary>
    public string DefaultOutputImpedanceParametersJson() =>
        JsonSerializer.Serialize(new OutputImpedanceParameters(
            StartFreqHz: 20.0,
            EndFreqHz: 20000.0,
            StepsPerOctave: 3,
            LevelMode: nameof(ThdSweepLevelMode.OutputPower),
            LevelValue: 1.0,
            LevelUnit: nameof(ThdSweepLevelUnit.Watt),
            LoadOhms: 8.0,
            SecondLoadOhms: 4.0,
            NominalOhms: 8.0,
            RightChannelIsReference: false,
            FftSize: OutputImpedanceRequest.DefaultFftSize,
            Window: WindowType.Hann,
            AverageCount: 1,
            SampleRateHz: OutputImpedanceRequest.DefaultSampleRateHz,
            AdaptiveResolution: true), SerializerOptions);

    /// <summary>
    /// A brand-new crosstalk type's <c>params_json</c>, kept in step with
    /// <c>CrosstalkConfigPanel.vue</c>'s own defaults: the tutorial's grid (log steps, 20 Hz–20 kHz),
    /// both directions, and a generator level rather than an output-referred one — the safe
    /// default against an amplifier whose gain is not yet known.
    /// </summary>
    public string DefaultCrosstalkParametersJson() =>
        JsonSerializer.Serialize(new CrosstalkParameters(
            StartFreqHz: 20.0,
            EndFreqHz: 20000.0,
            StepsPerOctave: 5,
            Direction: nameof(CrosstalkDirection.Both),
            LevelMode: nameof(ThdSweepLevelMode.GeneratorVoltage),
            LevelValue: -20.0,
            LevelUnit: nameof(ThdSweepLevelUnit.Dbv),
            LoadOhms: 8.0,
            FftSize: CrosstalkRequest.DefaultFftSize,
            Window: WindowType.Hann,
            AverageCount: 1,
            SampleRateHz: CrosstalkRequest.DefaultSampleRateHz,
            AdaptiveResolution: true), SerializerOptions);

    /// <summary>
    /// A brand-new impedance type's <c>params_json</c>, kept in step with
    /// <c>ImpedanceConfigPanel.vue</c>'s own defaults.
    ///
    /// <para>100 ohms is what a typical bench fixture is built with, and the left channel as
    /// reference is how one is normally wired — note that <c>--simulate</c> needs the opposite, since
    /// its fixture models sit on the left channel. The sweep starts at 10 Hz rather than the
    /// frequency response's 2 Hz: a driver's resonance is the point of this measurement and 10 Hz is
    /// below any of them, while a decade lower costs signal-to-noise for nothing. No calibration is
    /// stored until the user captures one.</para>
    ///
    /// <para><b>Pink noise, ten records, 1/12 octave</b> (Joost, 2026-10-20). Noise is the default
    /// stimulus because it is the one whose averaging keeps paying: its records are independent
    /// draws, so the noise in H falls with the count, where a chirp's are repeats of one
    /// deterministic sweep — and a divider measurement is noise-limited by construction, with the
    /// DUT channel tens of dB below the reference. The old 2 records at 1/48 octave were chosen so a
    /// high-Q resonance peak would not be blunted, and they are kept for the <i>stepped</i> stimulus,
    /// which measures one tone at a time and pays its averages at every point of its grid. The
    /// per-stimulus map below is what lets the three differ; see
    /// <c>web/src/impedance/stimulusSettings.ts</c>, which holds the same table.</para>
    ///
    /// <para>The calibration stays a <b>chirp</b> whatever the measurement plays: a standard is a
    /// curve in frequency, so the fast stimulus captures it and the slow one need never be used for
    /// it. The stepped grid's defaults are there so switching the dropdown lands on something
    /// sensible rather than on zeros — 20 Hz to 20 kHz at 5 points per octave, the same shape the
    /// stepped-sine response starts from.</para>
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
            Stimulus: nameof(ImpedanceStimulus.PinkNoise),
            ReferenceOhms: 100.0,
            RightChannelIsReference: false,
            FrequencySpanHz: 24000.0,
            ResolutionMultiplier: 1,
            ChirpStartHz: 10.0,
            LevelValue: -20.0,
            LevelUnit: nameof(ThdSweepLevelUnit.Dbv),
            SmoothingDenominator: ImpedanceStimulusDefaults.PinkNoise.SmoothingDenominator ?? 12,
            AverageCount: ImpedanceStimulusDefaults.PinkNoise.AverageCount ?? 10,
            CalibrationLevelValue: ImpedanceRequest.DefaultCalibrationLevelDbv,
            CableMode: nameof(ImpedanceCableMode.None),
            FixedCableOhms: 0.0,
            FixedCableMillihenry: 0.0,
            CalibrationStimulus: nameof(ImpedanceStimulus.Chirp),
            StartFreqHz: 20.0,
            EndFreqHz: 20000.0,
            StepsPerOctave: 5,
            AdaptiveResolution: true,
            UseGlobalFixture: useGlobalFixture)
        {
            ByStimulus = ImpedanceStimulusDefaults.All,
        }, SerializerOptions);

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

        if (IsCrosstalkKind(kind))
            return existingParamsJson ?? DefaultCrosstalkParametersJson();

        if (IsOutputImpedanceKind(kind))
            return existingParamsJson ?? DefaultOutputImpedanceParametersJson();

        if (IsImpedanceKind(kind))
            return existingParamsJson ?? DefaultImpedanceParametersJson(globalCalibrationAvailable);

        if (IsImpulseResponseKind(kind))
            return existingParamsJson ?? DefaultImpulseResponseParametersJson();

        if (IsFrequencyResponseKind(kind))
            return existingParamsJson ?? DefaultFrequencyResponseParametersJson();

        // Before the generic sweep arm: it is a sweep kind (same result shape), but its defaults are
        // its own, and the THD amplitude sweep's JSON would deserialize into a request with no tones.
        if (kind == ImdVsAmplitudeKind)
            return existingParamsJson ?? DefaultImdSweepParametersJson();

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
    public async Task<long> SaveAsync(
        long measurementTypeId,
        string name,
        string? notes,
        IReadOnlyList<JsonElement>? annotations,
        CancellationToken ct)
    {
        if (session.Acquiring)
            throw new InvalidOperationException("Stop acquisition before saving a measurement — only a stopped, stable trace is saved.");

        var snapshots = spectrumPipeline.LatestSnapshots();
        if (snapshots.Length == 0)
            throw new InvalidOperationException("No spectrum has been captured yet.");

        // The settle time is a run record, set here and not in BuildParameters: that method also
        // writes a type's own params_json, where the field has to stay null.
        var parameters = BuildParameters() with
        {
            CaptureDelayMs = session.LastAppliedCaptureDelayMs,
            ExternalGainLeftDb = session.ExternalGainLeftDb,
            ExternalGainRightDb = session.ExternalGainRightDb,
            ProgrammableLoadKind = LoadKindStamp,
            ProgrammableLoadOhms = LoadOhmsStamp,
        };
        // The pipeline knows the tones it measured with but not which template put them there;
        // the type does. Carried over only when the type's stored pair is the one that ran.
        parameters = parameters with { ImdPreset = await ResolveImdPresetNameAsync(measurementTypeId, parameters, ct) };
        // The DUT gain, the chart unit and the generator panel's level unit are client-staged and
        // reach the server only when the type is saved, so a result takes them from its type: the
        // "Noise in nV/sqrt(Hz)" row a report recomputes needs the gain that was on screen, and the
        // type's is the best record of it.
        //
        // The chart's annotations are client-staged in exactly the same way, but they are *sent with
        // the save* rather than only read back from the type, and the difference matters: nobody
        // edits the DUT gain in the minute before pressing Save, whereas a user annotates a trace in
        // order to save it, so the type's stored copy would routinely be one edit stale and the
        // result would print yesterday's notes. The type's copy is the fallback for a save path that
        // sends none (an older client, or a call from a test) -- so null means "not sent" and an
        // empty list means "none", which is the same distinction PUT /api/measurements/{id} makes.
        parameters = parameters with { DutGainDb = null, ChartUnit = null, GeneratorLevelUnit = null, ChartAnnotations = annotations };
        if (await ReadStoredTypeParametersAsync(measurementTypeId, ct) is { } typeParameters)
            parameters = parameters with
            {
                DutGainDb = typeParameters.DutGainDb,
                ChartUnit = typeParameters.ChartUnit,
                GeneratorLevelUnit = typeParameters.GeneratorLevelUnit,
                ChartAnnotations = annotations ?? typeParameters.ChartAnnotations,
            };

        // The weighting a result records is the one the saved trace was produced under -- the
        // snapshot's, not the live options', which may have changed since the capture. One
        // weighting for both channels: the pipeline applies one spec to every channel of a tick.
        if (snapshots[0].Weighting is { } snapshotWeighting)
            parameters = parameters with
            {
                Weighting = snapshotWeighting.Standard.ToString(),
                WeightingUser1 = snapshotWeighting.User1,
                WeightingUser2 = snapshotWeighting.User2,
            };
        string paramsJson = JsonSerializer.Serialize(parameters, SerializerOptions);
        string? genJson = JsonSerializer.Serialize(session.EffectiveGeneratorConfig, SerializerOptions);

        var spectra = snapshots
            .Select(s => new StoredSpectrumInput(
                ChannelIndex(s.Channel), s.Window, s.Sums, s.SampleRateHz, s.BinSizeHz, SpectrumNorm.Amplitude, s.Dbv))
            .ToList();

        var metrics = new List<MeasurementMetric>();
        foreach (var snapshot in snapshots)
        {
            AppendChannelMetrics(
                metrics, ChannelIndex(snapshot.Channel), snapshot.Meters, snapshot.Noise, snapshot.BinSizeHz);
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
    /// The metric rows one analysed channel contributes. Shared with the simulation import
    /// (<see cref="SimulationImportService"/>) rather than reimplemented there, and that is
    /// load-bearing rather than tidy: the names are a contract several readers depend on, and
    /// <c>thd_percent</c>, <c>thd_n_percent</c> and <c>snr_db</c> in particular are what the
    /// history list quotes as a result's headline while <c>fundamental_hz</c> is what
    /// <c>SelectHeadlineCapture</c> picks a capture by. A second copy that drifted would leave an
    /// imported result looking blank in the list for no visible reason.
    /// </summary>
    internal static void AppendChannelMetrics(
        List<MeasurementMetric> metrics, int channel, SpectrumMeters? meters, NoiseFigures? noise, double binSizeHz)
    {
        // Ahead of the fundamental check: these two need none, and a noise measurement with
        // the generator off is exactly the case where there is none.
        if (noise is { } n)
        {
            metrics.Add(new MeasurementMetric(channel, "band_rms_dbv", n.BandRmsDbv, "dBV"));
            metrics.Add(new MeasurementMetric(channel, "noise_density_dbv_per_root_hz", n.NoiseDensityDbvPerRootHz, "dBV/√Hz"));
        }

        // No fundamental found in band for this channel this tick -- its spectrum is still
        // saved, just with no derived metric rows to go with it. See SpectrumSnapshot.Meters.
        if (meters is not { } m)
            return;

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
            metrics.Add(new MeasurementMetric(channel, $"h{harmonic.Order}_hz", harmonic.Bin * binSizeHz, "Hz"));
            metrics.Add(new MeasurementMetric(channel, $"h{harmonic.Order}_dbc", harmonic.Dbc, "dBc"));
        }

        if (m.Intermodulation is { } imd)
            metrics.AddRange(IntermodulationMetrics(channel, imd));
        if (m.Multitone is { } multitone)
            metrics.AddRange(MultitoneMetrics(channel, multitone));
    }

    /// <summary>
    /// Saves the last completed sweep as one measurement result with one capture per frequency.
    ///
    /// Reads <see cref="ThdSweepRunner.Status"/> rather than taking the points as an argument, for
    /// the same reason <see cref="SaveAsync"/> reads the pipeline's retained snapshot: what gets
    /// saved must be exactly what is on screen, not a separately-supplied copy that could differ.
    /// A cancelled sweep is saveable -- a partial sweep still measured the part it covered.
    /// </summary>
    public async Task<long> SaveSweepAsync(long measurementTypeId, string name, string? notes, IReadOnlyList<JsonElement>? annotations, CancellationToken ct)
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
                    CaptureDelayMs: session.LastAppliedCaptureDelayMs,
                ExternalGainLeftDb: session.ExternalGainLeftDb, ExternalGainRightDb: session.ExternalGainRightDb,
                ProgrammableLoadKind: LoadKindStamp, ProgrammableLoadOhms: LoadOhmsStamp,
                    StopThdPercent: request.StopThdPercent,
                    MeasurementLoFreqHz: request.MeasurementLoFreqHz,
                    MeasurementHiFreqHz: request.MeasurementHiFreqHz,
                    ChartAnnotations: annotations),
                SerializerOptions)
            : JsonSerializer.Serialize(
                new ThdVsFrequencyParameters(
                    request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave,
                    request.LevelMode.ToString(), request.LevelValue, request.LevelUnit.ToString(),
                    request.LoadOhms, request.ProcessLeft, request.ProcessRight,
                    request.FftSize, request.Window, request.AverageCount, request.SampleRateHz,
                    request.MeasureNoiseFloor, status.GeneratorAmpDbv,
                    request.AdaptiveResolution, request.ThdMethod.ToString(),
                    CaptureDelayMs: session.LastAppliedCaptureDelayMs,
                ExternalGainLeftDb: session.ExternalGainLeftDb, ExternalGainRightDb: session.ExternalGainRightDb,
                ProgrammableLoadKind: LoadKindStamp, ProgrammableLoadOhms: LoadOhmsStamp,
                    MeasurementLoFreqHz: request.MeasurementLoFreqHz,
                    MeasurementHiFreqHz: request.MeasurementHiFreqHz,
                    ChartAnnotations: annotations),
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
    /// Saves the IMD sweep currently on screen as one measurement result -- <see cref="SaveSweepAsync"/>'s
    /// shape (one capture per level, metric rows per channel, the previews) so the stored result reads
    /// back through the same <c>GetSweepPointsAsync</c> path. Reads <see cref="ImdSweepRunner.Status"/>
    /// for the same reason that method reads its runner's.
    /// </summary>
    public async Task<long> SaveImdSweepAsync(long measurementTypeId, string name, string? notes, IReadOnlyList<JsonElement>? annotations, CancellationToken ct)
    {
        var status = imdSweepRunner.Status;
        if (status.Running)
            throw new InvalidOperationException("Wait for the sweep to finish (or cancel it) before saving.");
        if (status.Points.Count == 0)
            throw new InvalidOperationException("No IMD sweep has been run yet.");

        var request = imdSweepRunner.LastRequest
            ?? throw new InvalidOperationException("No IMD sweep has been run yet.");

        string paramsJson = JsonSerializer.Serialize(
            new ImdVsAmplitudeParameters(
                request.Preset.ToString(), request.ToneLowHz, request.ToneHighHz, request.LowToHighRatio,
                request.Method.ToString(), request.MaxOrder,
                request.StartLevelVolt, request.EndLevelVolt, request.StepsPerOctave, request.LoadOhms,
                request.ProcessLeft, request.ProcessRight,
                request.FftSize, ImdSweepRequest.Window, request.AverageCount, request.SampleRateHz,
                request.MeasureNoiseFloor, request.MaxProductFreqHz,
                CaptureDelayMs: session.LastAppliedCaptureDelayMs,
                ExternalGainLeftDb: session.ExternalGainLeftDb, ExternalGainRightDb: session.ExternalGainRightDb,
                ProgrammableLoadKind: LoadKindStamp, ProgrammableLoadOhms: LoadOhmsStamp,
                StopImdPercent: request.StopImdPercent,
                MeasurementLoFreqHz: request.MeasurementLoFreqHz,
                MeasurementHiFreqHz: request.MeasurementHiFreqHz,
                ChartAnnotations: annotations),
            SerializerOptions);

        // Grouped by the level, which is the axis; capture.freq_hz carries the low tone, which is
        // true and keeps GetSweepPointsAsync's fundamental_hz recovery working unchanged.
        var captures = status.Points
            .GroupBy(p => p.GeneratorSineEquivalentDbv)
            .OrderBy(g => g.Key)
            .Select((group, index) => new SweepCaptureInput(
                index,
                group.First().ToneLowHz,
                BuildImdSweepMetrics(group),
                BuildImdSweepPreviews(group)))
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
    /// One level's metrics, per channel. <c>fundamental_hz</c> (the low tone) keeps
    /// <c>GetSweepPointsAsync</c>'s frequency recovery working; <c>generator_dbv</c>/<c>generator_volt</c>
    /// vary from point to point, which is what marks the result level-stepped for the CSV exporter and
    /// the headline picker; <c>fundamental_dbv</c> is the sine-equivalent output so the history headline
    /// reads a level. Deliberately no <c>thd_percent</c>: nothing must mistake this for a THD result.
    /// </summary>
    private static List<MeasurementMetric> BuildImdSweepMetrics(IEnumerable<ImdSweepPoint> pointsAtOneLevel)
    {
        var metrics = new List<MeasurementMetric>();
        foreach (var point in pointsAtOneLevel)
        {
            int ch = point.Channel;
            metrics.Add(new MeasurementMetric(ch, "fundamental_hz", point.ToneLowHz, "Hz"));
            metrics.Add(new MeasurementMetric(ch, "fundamental_dbv", point.SineEquivalentDbv, "dBV"));
            metrics.Add(new MeasurementMetric(ch, "gain_db", point.GainDb, "dB"));
            metrics.Add(new MeasurementMetric(ch, "power_watt", point.PowerWatt, "W"));
            metrics.Add(new MeasurementMetric(ch, "noise_percent", point.NoisePercent, "%"));
            metrics.Add(new MeasurementMetric(ch, "generator_dbv", point.GeneratorSineEquivalentDbv, "dBV"));
            metrics.Add(new MeasurementMetric(ch, "generator_volt", Math.Pow(10.0, point.GeneratorSineEquivalentDbv / 20.0), "V"));
            metrics.Add(new MeasurementMetric(ch, "imd_percent", point.ImdPercent, "%"));
            metrics.Add(new MeasurementMetric(ch, "imd_tone_low_hz", point.ToneLowHz, "Hz"));
            metrics.Add(new MeasurementMetric(ch, "imd_tone_high_hz", point.ToneHighHz, "Hz"));
            metrics.Add(new MeasurementMetric(ch, "imd_tone_low_dbv", point.ToneLowDbv, "dBV"));
            metrics.Add(new MeasurementMetric(ch, "imd_tone_high_dbv", point.ToneHighDbv, "dBV"));
            metrics.Add(new MeasurementMetric(ch, "imd_sine_equivalent_dbv", point.SineEquivalentDbv, "dBV"));
            metrics.Add(new MeasurementMetric(ch, "imd_combined_rms_dbv", point.CombinedRmsDbv, "dBV"));

            for (int i = 0; i < point.OrderPercent.Count; i++)
                metrics.Add(new MeasurementMetric(ch, $"im{i + 2}_percent", point.OrderPercent[i], "%"));
        }

        return metrics;
    }

    private static List<CapturePreviewInput> BuildImdSweepPreviews(IEnumerable<ImdSweepPoint> pointsAtOneLevel)
    {
        var previews = new List<CapturePreviewInput>();
        foreach (var point in pointsAtOneLevel)
        {
            if (point.PreviewFft is { } fft)
                previews.Add(new CapturePreviewInput(point.Channel, CapturePreviewKind.Fft, fft.X, fft.Min, fft.Max));
            if (point.PreviewScope is { } scope)
                previews.Add(new CapturePreviewInput(point.Channel, CapturePreviewKind.Scope, scope.X, scope.Min, scope.Max));
        }

        // Same guard as BuildSweepPreviews: one point per channel per level, or the UNIQUE constraint
        // on capture_preview would phrase the runner's bug as a 500.
        var duplicate = previews.GroupBy(p => (p.Channel, p.Kind)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            var first = pointsAtOneLevel.FirstOrDefault();
            throw new InvalidOperationException(
                $"The IMD sweep produced {duplicate.Count()} '{duplicate.Key.Kind}' previews for channel {duplicate.Key.Channel} "
                + $"at {first?.GeneratorSineEquivalentDbv ?? double.NaN:0.##} dBV, but a sweep point may only be measured once per channel. "
                + "Re-run the sweep before saving; if this repeats, restart the app and report it.");
        }

        return previews;
    }

    /// <summary>
    /// Saves the frequency-response curves currently on screen as one measurement result.
    ///
    /// <para>Reads <see cref="FrequencyResponseRunner.Status"/> rather than taking the traces as an
    /// argument, for the same reason <see cref="SaveSweepAsync"/> reads the runner's: what is saved
    /// must be exactly what is displayed, not a separately-supplied copy that could differ.</para>
    /// </summary>
    public async Task<long> SaveFrequencyResponseAsync(long measurementTypeId, string name, string? notes, IReadOnlyList<JsonElement>? annotations, CancellationToken ct)
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
                CaptureDelayMs: session.LastAppliedCaptureDelayMs,
                ExternalGainLeftDb: session.ExternalGainLeftDb, ExternalGainRightDb: session.ExternalGainRightDb,
                ProgrammableLoadKind: LoadKindStamp, ProgrammableLoadOhms: LoadOhmsStamp,
                ChartAnnotations: annotations),
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
    /// The stepped run's per-point captures as capture rows, one per point, both inputs together.
    ///
    /// <para>Grouped by frequency because that is what a point is — the two inputs are measured from
    /// one record — and because <c>capture_preview</c> is UNIQUE on (capture, channel, kind), so two
    /// points of one channel landing in one capture would reach SQLite as a raw constraint
    /// violation. A grid cannot produce the same frequency twice, so a duplicate here would be a
    /// runner fault; it is dropped rather than failing the save, since the previews are an aid to
    /// reading the result and the curves are the result.</para>
    /// </summary>
    private static List<ResponsePreviewCaptureInput> BuildResponsePointPreviews(
        IReadOnlyList<ResponsePointPreview>? previews)
    {
        var captures = new List<ResponsePreviewCaptureInput>();
        if (previews is null)
            return captures;

        int seqIndex = 1; // 0 is the curves' own capture.
        foreach (var group in previews.GroupBy(p => p.FreqHz).OrderBy(g => g.Key))
        {
            var rows = new List<CapturePreviewInput>();
            foreach (var preview in group.GroupBy(p => p.Channel).Select(g => g.First()))
            {
                if (preview.Fft is { } fft)
                    rows.Add(new CapturePreviewInput(preview.Channel, CapturePreviewKind.Fft, fft.X, fft.Min, fft.Max));
                if (preview.Scope is { } scope)
                    rows.Add(new CapturePreviewInput(preview.Channel, CapturePreviewKind.Scope, scope.X, scope.Min, scope.Max));
            }

            if (rows.Count > 0)
                captures.Add(new ResponsePreviewCaptureInput(seqIndex++, group.Key, rows));
        }

        return captures;
    }

    /// <summary>
    /// Saves the crosstalk traces the runner holds — one <c>response_trace</c> row per direction
    /// measured, with the as-run generator level of each pass in <c>params_json</c>.
    /// </summary>
    public async Task<long> SaveCrosstalkAsync(long measurementTypeId, string name, string? notes, IReadOnlyList<JsonElement>? annotations, CancellationToken ct)
    {
        var status = crosstalkRunner.Status;
        if (status.Running)
            throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before saving.");
        if (status.Traces.Count == 0)
            throw new InvalidOperationException("No crosstalk has been measured yet.");

        var request = crosstalkRunner.LastRequest
            ?? throw new InvalidOperationException("No crosstalk has been measured yet.");
        var levels = crosstalkRunner.GeneratorAmpDbvByKind;

        string paramsJson = JsonSerializer.Serialize(
            new CrosstalkParameters(
                request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave,
                request.Direction.ToString(),
                request.LevelMode.ToString(), request.LevelValue, request.LevelUnit.ToString(),
                request.LoadOhms,
                request.FftSize, request.Window, request.AverageCount, request.SampleRateHz,
                request.AdaptiveResolution,
                GeneratorAmpDbvLeftToRight: levels.TryGetValue(FrequencyResponseTraceKind.CrosstalkLeftToRight, out double l2r) ? l2r : null,
                GeneratorAmpDbvRightToLeft: levels.TryGetValue(FrequencyResponseTraceKind.CrosstalkRightToLeft, out double r2l) ? r2l : null,
                CaptureDelayMs: session.LastAppliedCaptureDelayMs,
                ExternalGainLeftDb: session.ExternalGainLeftDb, ExternalGainRightDb: session.ExternalGainRightDb,
                ProgrammableLoadKind: LoadKindStamp, ProgrammableLoadOhms: LoadOhmsStamp,
                ChartAnnotations: annotations),
            SerializerOptions);

        var info = session.Info;
        var saveRequest = new ResponseSaveRequest(
            measurementTypeId, name, notes,
            paramsJson,
            info?.ProductName, info?.SerialNumber, info?.FirmwareVersion, info?.IsSimulated ?? false,
            status.SampleRateHz, status.FftSize,
            Channels: 2,
            session.InputRangeDbv, session.OutputRangeDbv,
            [.. status.Traces.Select(ToTraceInput)]);

        return await measurementRepository.SaveResponseResultAsync(saveRequest, ct);
    }

    /// <summary>
    /// Saves the output-impedance run on screen as one measurement result: the Zout curve as a
    /// <c>zout</c> row in the impedance units, and the two gain passes it was derived from as
    /// <c>gain_load1</c> / <c>gain_load2</c> rows in plain dB — so a stored result can still say what
    /// the two gains were, and the range both were read at is on its parameters.
    /// </summary>
    public async Task<long> SaveOutputImpedanceAsync(long measurementTypeId, string name, string? notes, IReadOnlyList<JsonElement>? annotations, CancellationToken ct)
    {
        var status = outputImpedanceRunner.Status;
        if (status.Running)
            throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before saving.");
        if (status.Trace is null)
            throw new InvalidOperationException("No output impedance has been measured yet.");

        var request = outputImpedanceRunner.LastRequest
            ?? throw new InvalidOperationException("No output impedance has been measured yet.");
        var (firstGain, secondGain) = outputImpedanceRunner.GainTraces;

        string paramsJson = JsonSerializer.Serialize(
            new OutputImpedanceParameters(
                request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave,
                request.LevelMode.ToString(), request.LevelValue, request.LevelUnit.ToString(),
                request.LoadOhms, request.SecondLoadOhms, request.NominalOhms, request.RightChannelIsReference,
                request.FftSize, request.Window, request.AverageCount, request.SampleRateHz,
                request.AdaptiveResolution,
                GeneratorAmpDbv: status.GeneratorAmpDbv,
                InputRangeDbv: status.InputRangeDbv,
                CaptureDelayMs: session.LastAppliedCaptureDelayMs,
                ExternalGainLeftDb: session.ExternalGainLeftDb, ExternalGainRightDb: session.ExternalGainRightDb,
                ProgrammableLoadKind: LoadKindStamp, ProgrammableLoadOhms: LoadOhmsStamp,
                ChartAnnotations: annotations),
            SerializerOptions);

        var traces = new List<ResponseTraceInput> { ToImpedanceTraceInput(status.Trace, ResponseTraceKind.OutputImpedance) };
        if (firstGain is not null)
            traces.Add(ToTraceInput(firstGain));
        if (secondGain is not null)
            traces.Add(ToTraceInput(secondGain));

        var info = session.Info;
        var saveRequest = new ResponseSaveRequest(
            measurementTypeId, name, notes,
            paramsJson,
            info?.ProductName, info?.SerialNumber, info?.FirmwareVersion, info?.IsSimulated ?? false,
            status.SampleRateHz, status.FftSize,
            Channels: 2,
            session.InputRangeDbv, session.OutputRangeDbv,
            traces);

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
    public async Task<long> SaveSteppedResponseAsync(long measurementTypeId, string name, string? notes, IReadOnlyList<JsonElement>? annotations, CancellationToken ct)
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
                CaptureDelayMs: session.LastAppliedCaptureDelayMs,
                ExternalGainLeftDb: session.ExternalGainLeftDb, ExternalGainRightDb: session.ExternalGainRightDb,
                ProgrammableLoadKind: LoadKindStamp, ProgrammableLoadOhms: LoadOhmsStamp,
                ChartAnnotations: annotations),
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
            [.. status.Traces.Select(ToTraceInput)],
            // The only measurement that takes these: a chirp has no points, and the two stepped
            // runners that are not this one (crosstalk, output impedance) do not capture them.
            BuildResponsePointPreviews(status.PointPreviews));

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
    public async Task<long> SaveImpulseResponseAsync(long measurementTypeId, string name, string? notes, IReadOnlyList<JsonElement>? annotations, CancellationToken ct)
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
                CaptureDelayMs: session.LastAppliedCaptureDelayMs,
                ExternalGainLeftDb: session.ExternalGainLeftDb, ExternalGainRightDb: session.ExternalGainRightDb,
                ProgrammableLoadKind: LoadKindStamp, ProgrammableLoadOhms: LoadOhmsStamp,
                ChartAnnotations: annotations),
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
    public async Task<long> SaveImpedanceAsync(long measurementTypeId, string name, string? notes, IReadOnlyList<JsonElement>? annotations, CancellationToken ct)
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
                CaptureDelayMs: session.LastAppliedCaptureDelayMs,
                ExternalGainLeftDb: session.ExternalGainLeftDb, ExternalGainRightDb: session.ExternalGainRightDb,
                ProgrammableLoadKind: LoadKindStamp, ProgrammableLoadOhms: LoadOhmsStamp,
                ChartAnnotations: annotations),
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
        ToImpedanceTraceInput(trace, ImpedanceTraceKindName(trace.Kind));

    /// <param name="kindName">The stored kind — the trace's own for an impedance result, <see cref="ResponseTraceKind.OutputImpedance"/> for a Zout curve, which is an impedance trace on the wire.</param>
    /// <remarks>
    /// <c>internal</c> for the same reason <see cref="ToTraceInput"/> is: the simulation import
    /// builds its own traces and has to store them in exactly the shape a measured one is stored
    /// in, and a second converter is a second place for the zeroed bandwidth blocks to drift.
    /// </remarks>
    internal static ResponseTraceInput ToImpedanceTraceInput(ImpedanceTrace trace, string kindName) =>
        new(
            kindName,
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

    internal static ResponseTraceInput ToTraceInput(FrequencyResponseTrace trace) =>
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
            trace.ReferenceMode.ToString(),
            trace.GroupDelayS,
            trace.PhaseDelayS);

    internal static string TraceKindName(FrequencyResponseTraceKind kind) => kind switch
    {
        FrequencyResponseTraceKind.Left => ResponseTraceKind.Left,
        FrequencyResponseTraceKind.Right => ResponseTraceKind.Right,
        FrequencyResponseTraceKind.Gain => ResponseTraceKind.Gain,
        FrequencyResponseTraceKind.CrosstalkLeftToRight => ResponseTraceKind.CrosstalkLeftToRight,
        FrequencyResponseTraceKind.CrosstalkRightToLeft => ResponseTraceKind.CrosstalkRightToLeft,
        FrequencyResponseTraceKind.GainLoad1 => ResponseTraceKind.GainLoad1,
        FrequencyResponseTraceKind.GainLoad2 => ResponseTraceKind.GainLoad2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown response trace kind."),
    };

    /// <summary>
    /// One frequency's preview traces, per channel and kind (FFT/scope) -- absent entirely for a
    /// point whose channel never captured one (the sweep runner always turns capture on, so in
    /// practice this only happens for a sweep saved before this feature existed... except there is
    /// no such sweep, since it isn't re-run from old state; kept defensive rather than assumed).
    /// </summary>
    /// <remarks><c>internal</c> for the reason <see cref="BuildSweepMetrics"/> is.</remarks>
    internal static List<CapturePreviewInput> BuildSweepPreviews(IEnumerable<ThdSweepPoint> pointsAtOneFrequency)
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
    /// <remarks><c>internal</c> so the stepped-simulation import stores its points in exactly this shape — see <c>SweepCaptureSupport.PointFromMeters</c>.</remarks>
    internal static List<MeasurementMetric> BuildSweepMetrics(IEnumerable<ThdSweepPoint> pointsAtOneFrequency)
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
    /// <summary>
    /// The metric rows one channel's intermodulation result becomes: the headline figure, both
    /// tones, the two level conventions, each order's figure and every product (frequency, level,
    /// dBc and its (m, n) combination). Method and max order are in params_json, not repeated here.
    /// Shared by the FFT save and the IMD sweep so a stored FFT capture and a stored sweep point
    /// spell the same names.
    /// </summary>
    internal static IEnumerable<MeasurementMetric> IntermodulationMetrics(int channel, IntermodulationResult imd)
    {
        yield return new MeasurementMetric(channel, "imd_percent", imd.ImdPercent, "%");
        yield return new MeasurementMetric(channel, "imd_db", imd.ImdDb, "dB");
        yield return new MeasurementMetric(channel, "imd_tone_low_hz", imd.ToneLowFreqHz, "Hz");
        yield return new MeasurementMetric(channel, "imd_tone_high_hz", imd.ToneHighFreqHz, "Hz");
        yield return new MeasurementMetric(channel, "imd_tone_low_dbv", imd.ToneLowDbv, "dBV");
        yield return new MeasurementMetric(channel, "imd_tone_high_dbv", imd.ToneHighDbv, "dBV");
        yield return new MeasurementMetric(channel, "imd_sine_equivalent_dbv", imd.SineEquivalentDbv, "dBV");
        yield return new MeasurementMetric(channel, "imd_combined_rms_dbv", imd.CombinedRmsDbv, "dBV");
        yield return new MeasurementMetric(channel, "imd_reference_dbv", imd.ReferenceDbv, "dBV");

        foreach (var (order, ratio) in imd.PerOrder)
            yield return new MeasurementMetric(channel, $"im{order}_percent", ratio * 100.0, "%");

        for (int i = 0; i < imd.Products.Count; i++)
        {
            var product = imd.Products[i];
            yield return new MeasurementMetric(channel, $"imd_p{i}_hz", product.FrequencyHz, "Hz");
            yield return new MeasurementMetric(channel, $"imd_p{i}_dbv", product.AmplitudeDbv, "dBV");
            yield return new MeasurementMetric(channel, $"imd_p{i}_dbc", product.Dbc, "dBc");
            yield return new MeasurementMetric(channel, $"imd_p{i}_order", product.Order, null);
            yield return new MeasurementMetric(channel, $"imd_p{i}_m", product.M, null);
            yield return new MeasurementMetric(channel, $"imd_p{i}_n", product.N, null);
        }
    }

    /// <summary>
    /// The multitone figures as metric rows: the total, the comb, every tone (<c>mt_t{i}_*</c>) and
    /// every <em>resolved</em> gap (<c>mt_b{i}_*</c>, i = the index of the tone at its lower edge).
    /// Unresolved gaps get no rows -- their figures are minus infinity, which the metrics dictionary
    /// could not serialize (a NaN or an infinity truncates the whole response, see
    /// <c>ImpedanceStatusJsonTests</c>) -- and the client reads a gap with no rows as unresolved.
    /// </summary>
    internal static IEnumerable<MeasurementMetric> MultitoneMetrics(int channel, MultitoneResult multitone)
    {
        yield return new MeasurementMetric(channel, "tdn_percent", multitone.TdnPercent, "%");
        yield return new MeasurementMetric(channel, "tdn_db", multitone.TdnDb, "dB");
        yield return new MeasurementMetric(channel, "mt_comb_dbv", multitone.CombDbv, "dBV");
        yield return new MeasurementMetric(channel, "mt_tone_count", multitone.Tones.Count, null);
        yield return new MeasurementMetric(channel, "mt_band_count", multitone.Bands.Count, null);
        yield return new MeasurementMetric(channel, "mt_resolved_bands", multitone.ResolvedBands, null);

        for (int i = 0; i < multitone.Tones.Count; i++)
        {
            var tone = multitone.Tones[i];
            yield return new MeasurementMetric(channel, $"mt_t{i}_hz", tone.FreqHz, "Hz");
            yield return new MeasurementMetric(channel, $"mt_t{i}_dbv", tone.AmplitudeDbv, "dBV");
        }

        for (int i = 0; i < multitone.Bands.Count; i++)
        {
            var band = multitone.Bands[i];
            if (!band.Resolved)
                continue;
            yield return new MeasurementMetric(channel, $"mt_b{i}_hz", band.HighestFreqHz, "Hz");
            yield return new MeasurementMetric(channel, $"mt_b{i}_dbv", band.HighestDbv, "dBV");
            yield return new MeasurementMetric(channel, $"mt_b{i}_dbr", band.HighestDbr, "dBr");
            yield return new MeasurementMetric(channel, $"mt_b{i}_dn_dbr", band.DnDbr, "dBr");
        }
    }

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

        // An FFT params_json that is not a whole configuration is not applied at all. Deserializing
        // a partial document does not fail: a missing generator is null and a missing harmonic count
        // or band is 0, and those zeros used to go straight onto the pipeline before the null was
        // even reached -- so the type 500'd on selection *and* left every sweep run afterwards
        // measuring THD with zero harmonics (the sweep runners inherit HarmonicCount and the band
        // from the live options), which read as "no data" on two screens at once. Nothing but this
        // method ever writes those fields, and no endpoint accepts a zero for them, so a zero is a
        // document that was never complete, not a setting. The live state is handed back instead,
        // and the stored document is rewritten from it there and then -- keeping only the chart
        // preferences it did hold -- so the type is whole again for its next selection rather than
        // only once the user happens to change something.
        if (!IsCompleteFftConfiguration(parameters))
        {
            await projectRepository.UpdateTypeAsync(
                type.Id, type.Name, type.Notes, HealedFftParamsJson(type.ParamsJson), ct);
            return ToAppliedResponse(BuildParameters());
        }

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
            // Rebuilt from the stored fields, or cleared: a type without IMD must not inherit the
            // previous type's spec, or its single 1 kHz tone reads the noise floor as IMD.
            Intermodulation = IntermodulationSpecOf(parameters),
            // Same rule: a type saved without a weighting (or by a build that had none) is
            // unweighted, not "whatever the last type left behind".
            Weighting = WeightingSpecOf(parameters),
            ScopeResidual = parameters.ScopeResidual ?? false,
        };

        return ToAppliedResponse(parameters);
    }

    /// <summary>
    /// The IMD template name a saved FFT result records: null without a spec, the owning type's
    /// stored name when that type's pair is the pair that was measured, "Custom" otherwise (a pair
    /// typed by hand, or the tones edited since the type was saved).
    /// </summary>
    /// <summary>The type's stored FFT parameters, or null for a missing type, another kind, or unreadable JSON.</summary>
    private async Task<FftSpectrumParameters?> ReadStoredTypeParametersAsync(long measurementTypeId, CancellationToken ct)
    {
        var type = await projectRepository.GetTypeAsync(measurementTypeId, ct);
        if (type is null || HasNoLiveConfiguration(type.Kind) || IsSweepKind(type.Kind))
            return null;
        try
        {
            return JsonSerializer.Deserialize<FftSpectrumParameters>(type.ParamsJson, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the programmable load was doing, for a result's <c>params_json</c>: its kind's name and
    /// the ohms it was presenting.
    ///
    /// <para>(null, null) for a bench with no load selected, which is the overwhelming majority —
    /// and the reason the kind is recorded beside the ohms rather than the ohms alone. "No load on
    /// this bench" and "a load whose relays have not been read back" are different facts, and a
    /// single null field could not tell them apart.</para>
    ///
    /// <para>Reads <see cref="LoadSessionService.StatusSnapshot"/>, which touches no hardware: a
    /// save must never be the thing that goes to an instrument.</para>
    ///
    /// <para>The two properties below read it separately, one per named argument, which keeps each
    /// of the ten save sites to a single line — the thing that makes them reviewable as one diff.
    /// Safe because the snapshot only moves when someone presses a load control, and a save is
    /// itself a separate press.</para>
    /// </summary>
    private (string? Kind, int? Ohms) LoadStamp()
    {
        var snapshot = load?.StatusSnapshot;
        return snapshot is null || snapshot.Kind == ProgrammableLoadKind.None
            ? (null, null)
            : (snapshot.Kind.ToString(), snapshot.ImpedanceOhms);
    }

    private string? LoadKindStamp => LoadStamp().Kind;

    private int? LoadOhmsStamp => LoadStamp().Ohms;

    private async Task<string?> ResolveImdPresetNameAsync(long measurementTypeId, FftSpectrumParameters parameters, CancellationToken ct)
    {
        if (parameters.ImdToneLowHz is not { } low || parameters.ImdToneHighHz is not { } high)
            return null;

        var type = await projectRepository.GetTypeAsync(measurementTypeId, ct);
        if (type is null || HasNoLiveConfiguration(type.Kind))
            return "Custom";

        FftSpectrumParameters? stored;
        try
        {
            stored = JsonSerializer.Deserialize<FftSpectrumParameters>(type.ParamsJson, SerializerOptions);
        }
        catch (JsonException)
        {
            return "Custom";
        }

        bool samePair = stored?.ImdToneLowHz is { } storedLow && stored.ImdToneHighHz is { } storedHigh
            && Math.Abs(storedLow - low) < 1e-6 && Math.Abs(storedHigh - high) < 1e-6;
        return samePair && !string.IsNullOrEmpty(stored!.ImdPreset) ? stored.ImdPreset : "Custom";
    }

    /// <summary>The stored weighting, or <see cref="WeightingSpec.None"/> when the row predates weighting or names an unknown standard.</summary>
    private static WeightingSpec WeightingSpecOf(FftSpectrumParameters parameters)
    {
        var standard = Enum.TryParse<WeightingStandard>(parameters.Weighting, ignoreCase: true, out var parsed)
            ? parsed
            : WeightingStandard.None;
        return new WeightingSpec(standard, parameters.WeightingUser1 ?? false, parameters.WeightingUser2 ?? false);
    }

    private static IntermodulationSpec? IntermodulationSpecOf(FftSpectrumParameters parameters)
    {
        if (parameters.ImdToneLowHz is not { } low || parameters.ImdToneHighHz is not { } high)
            return null;
        if (!Enum.TryParse<IntermodulationMethod>(parameters.ImdMethod, ignoreCase: true, out var method))
            return null;
        if (!(low > 0.0) || !(high > low))
            return null;

        return new IntermodulationSpec(low, high, method, parameters.ImdMaxOrder ?? IntermodulationPresets.DefaultMaxOrder(method), parameters.ImdMaxProductFreqHz);
    }

    /// <summary>
    /// Replaces a stored result's own annotations, whatever kind it is -- the Edit measurement
    /// dialog's list, which is the only way to reach them once a result is saved (nothing puts a
    /// stored result back on a chart).
    /// </summary>
    /// <remarks>
    /// <para><b>Splices a <see cref="JsonNode"/> rather than deserializing and re-serializing</b>,
    /// and that is the load-bearing part: a result's <c>params_json</c> is the record of a run,
    /// possibly written by another build, and a round trip through today's
    /// <see cref="FftSpectrumParameters"/> would silently drop every field this build does not know
    /// and rewrite every null -- i.e. it would change what the result says it measured in order to
    /// add a note about it. Same technique and same reason as <see cref="HealedFftParamsJson"/>.</para>
    ///
    /// <para>Every kind, since annotations left the FFT screen. This method never did anything
    /// FFT-specific -- it splices one field into one document -- so opening it up was deleting the
    /// guard that used to refuse the other kinds, and nothing else. A document that will not parse
    /// is still refused, never replaced.</para>
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No such result, or it is in the trash.</exception>
    /// <exception cref="InvalidOperationException">The result's stored parameters cannot be read.</exception>
    public async Task SetResultAnnotationsAsync(
        long resultId,
        IReadOnlyList<JsonElement> annotations,
        CancellationToken ct)
    {
        var detail = await measurementRepository.GetResultAsync(resultId, ct)
            ?? throw new KeyNotFoundException($"There is no measurement result {resultId}.");

        JsonObject? stored;
        try
        {
            stored = JsonNode.Parse(detail.ParamsJson) as JsonObject;
        }
        catch (JsonException)
        {
            stored = null;
        }
        if (stored is null)
            throw new InvalidOperationException("This result's stored parameters cannot be read.");

        stored["chartAnnotations"] = JsonSerializer.SerializeToNode(annotations, SerializerOptions);
        await measurementRepository.UpdateResultParamsJsonAsync(resultId, stored.ToJsonString(SerializerOptions), ct);
    }

    /// <summary>
    /// Whether a stored FFT record holds everything <see cref="ApplyAsync"/> pushes onto the
    /// pipeline and the device. Public so a test can pin the signature of a document that was
    /// written incomplete: no generator, no harmonics, an empty band.
    /// </summary>
    public static bool IsCompleteFftConfiguration(FftSpectrumParameters parameters) =>
        parameters.Generator is { Left: not null, Right: not null }
        && parameters.FftSize > 0
        && parameters.SampleRateHz > 0
        && parameters.HarmonicCount > 0
        && parameters.MeasurementLoFreqHz >= 0
        && parameters.MeasurementHiFreqHz > parameters.MeasurementLoFreqHz;

    /// <summary>
    /// The live state as a whole FFT document, with whatever <c>chart*</c> preferences the
    /// incomplete <paramref name="storedJson"/> did carry laid over it: those describe how the
    /// screen was being looked at and were never the broken part.
    /// </summary>
    internal string HealedFftParamsJson(string storedJson)
    {
        var healed = JsonNode.Parse(BuildCurrentParametersJson())!.AsObject();
        JsonObject? stored;
        try
        {
            stored = JsonNode.Parse(storedJson) as JsonObject;
        }
        catch (JsonException)
        {
            stored = null;
        }

        if (stored is not null)
        {
            foreach (var (key, value) in stored)
            {
                if (key.StartsWith("chart", StringComparison.OrdinalIgnoreCase))
                    healed[key] = value?.DeepClone();
            }
        }

        return healed.ToJsonString(SerializerOptions);
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
            parameters.ChartShowSpectrogram,
            parameters.ChartShowOscilloscope,
            parameters.ChartShowHarmonics,
            parameters.ChartShowOtherPeaks,
            parameters.ImdPreset,
            parameters.ImdMethod,
            parameters.ImdMaxOrder,
            parameters.Weighting,
            parameters.WeightingUser1,
            parameters.WeightingUser2,
            parameters.DutGainDb,
            parameters.ChartUnit,
            parameters.ChartShowWeightingCurve,
            parameters.ScopeResidual,
            parameters.GeneratorLevelUnit,
            parameters.ChartShowFftTrace,
            parameters.ChartShowHarmonicMarkers,
            parameters.ChartHarmonicsSort,
            parameters.ChartOtherPeaksSort,
            parameters.ChartHarmonicMarkerUnit,
            parameters.LoadOhms,
            parameters.ChartAnnotations);

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
            staged?.ChartShowSpectrogram, staged?.ChartShowOscilloscope, staged?.ChartShowHarmonics, staged?.ChartShowOtherPeaks)
        {
            // The spec itself is live pipeline state (the FFT screen pushes it through
            // spectrum-config the moment the stimulus changes), so it is read from the options like
            // FftSize; only the template name is a staged, client-side notion.
            ImdPreset = options.Intermodulation is null ? null : staged?.ImdPreset ?? "Custom",
            ImdMethod = options.Intermodulation?.Method.ToString(),
            ImdMaxOrder = options.Intermodulation?.MaxOrder,
            ImdToneLowHz = options.Intermodulation?.ToneLowHz,
            ImdToneHighHz = options.Intermodulation?.ToneHighHz,
            ImdMaxProductFreqHz = options.Intermodulation?.MaxProductFreqHz,
            // Live pipeline state, like the IMD spec; the user tables' names come from the setting
            // so a result can say which file weighted it. Only recorded when the slot is in effect.
            Weighting = options.Weighting.Standard.ToString(),
            WeightingUser1 = options.Weighting.User1,
            WeightingUser2 = options.Weighting.User2,
            WeightingUser1Name = options.Weighting.User1 ? userWeighting?.Get(1)?.Name : null,
            WeightingUser2Name = options.Weighting.User2 ? userWeighting?.Get(2)?.Name : null,
            DutGainDb = staged?.DutGainDb,
            ChartUnit = staged?.ChartUnit,
            ChartShowWeightingCurve = staged?.ChartShowWeightingCurve,
            ChartShowFftTrace = staged?.ChartShowFftTrace,
            ChartShowHarmonicMarkers = staged?.ChartShowHarmonicMarkers,
            ChartHarmonicsSort = staged?.ChartHarmonicsSort,
            ChartOtherPeaksSort = staged?.ChartOtherPeaksSort,
            ChartHarmonicMarkerUnit = staged?.ChartHarmonicMarkerUnit,
            LoadOhms = staged?.LoadOhms,
            ChartAnnotations = staged?.ChartAnnotations,
            GeneratorLevelUnit = staged?.GeneratorLevelUnit,
            // Live pipeline state, like the IMD spec: the oscilloscope's Residual checkbox pushes
            // it through spectrum-config the moment it is toggled.
            ScopeResidual = options.ScopeResidual,
        };
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
    /// <para>Autofit-when-done used to be switched on here as well. It is not any more — see the
    /// body — and the two are not alike: a silent generator has nothing to Run, where a chart that
    /// does not refit itself simply stays where it was put.</para>
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
        // "Autofit when measurement done" is deliberately *not* forced on here, and a stored type's
        // null already reads as off (useChartDisplay.ts). It was forced on for a new type until
        // 2026-10-17: a chart that refits itself the moment a run ends moves the window out from
        // under whoever set it, so it is now an opt-in on every kind, which is also the one answer
        // the six screens can all give (Joost's call).
        return JsonSerializer.Serialize(parameters, SerializerOptions);
    }

    private static int ChannelIndex(StreamChannel channel) => channel == StreamChannel.Left ? 0 : 1;
}
