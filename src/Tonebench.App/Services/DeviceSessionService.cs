using Tonebench.App.Endpoints;
using Tonebench.App.Diagnostics;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;
using Tonebench.Device.Split;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Owns the single <see cref="IAnalyzerDevice"/> for the app's lifetime: connects once at
/// startup, tracks the range/generator/acquisition state the device itself doesn't expose
/// getters for, and drains acquisition blocks into the <see cref="SpectrumPipeline"/> for the
/// duration of each acquisition run.
/// </summary>
public sealed class DeviceSessionService(
    AnalyzerDeviceFactory deviceFactory, SpectrumPipeline spectrumPipeline, CaptureDelaySetting captureDelay,
    ILogger<DeviceSessionService> logger, ExternalGainSetting? externalGain = null,
    SoundCardSetting? soundCard = null, StreamRecorder? streamRecorder = null,
    AutoRangeStepDownDelaySetting? stepDownDelay = null, SplitDeviceSetting? split = null)
    : IDisposable, IMeasurementRun
{
    /// <summary>
    /// The step-down delay the ranger of a device that has none of its own uses — the same
    /// Configuration setting the QA403's own ranger is given, for the same question. Optional so
    /// every pinned-device test constructor keeps working; the default is the document's default.
    /// </summary>
    private readonly AutoRangeStepDownDelaySetting _stepDownDelay = stepDownDelay ?? new AutoRangeStepDownDelaySetting();

    /// <summary>
    /// Chooses the input range while acquiring on a device with no ranger of its own — null on a
    /// QA403, which ranges inside the device, and null while nothing is acquiring. Built per
    /// acquisition, so it can never outlive the device whose ladder it was given (see
    /// <see cref="SwitchDeviceAsync"/>) and so its under-range clock starts fresh with the run.
    /// See <see cref="SessionInputRanger"/> for why this is not in the browser any more.
    /// </summary>
    private SessionInputRanger? _sessionRanger;
    private readonly ExternalGainSetting _externalGain = externalGain ?? new ExternalGainSetting();

    /// <summary>
    /// The sound card's own front-end gain, which is remembered separately from the QA403's — see
    /// <see cref="ApplyEffectiveExternalGain"/>.
    /// </summary>
    private readonly SoundCardSetting _soundCard = soundCard ?? new SoundCardSetting();

    /// <summary>
    /// Which two devices a composite analyzer is made of, when that is the kind in use. Read rather
    /// than type-tested, so "is the capturing half a sound card" has one answer and the settings
    /// document stays the thing that decides it -- the split endpoint reopens the device on a
    /// change, so the two cannot drift apart.
    /// </summary>
    private readonly SplitDeviceSetting _split = split ?? new SplitDeviceSetting();

    /// <summary>
    /// The external gain to fall back to when the analyzer is not the sound card, i.e. the one
    /// <c>acquisition.externalGain</c> holds, with the unit that document reads it in. Seeded from
    /// whatever the setting carries at construction, which is what <c>Program</c> loaded from that
    /// document at startup.
    /// </summary>
    private (double Left, double Right, string Unit)? _sharedExternalGainDb;

    /// <summary>
    /// The device this session is talking to. A <b>field</b> rather than a constructor parameter
    /// because it is replaceable at runtime — see <see cref="SwitchDeviceAsync"/>, and
    /// <see cref="AnalyzerDeviceFactory"/> for why the container no longer owns its lifetime.
    /// </summary>
    private IAnalyzerDevice _device = deviceFactory.Create(deviceFactory.InitialKind);

    private DeviceKind _kind = deviceFactory.InitialKind;

    /// <summary>
    /// Copies whichever front-end gain belongs to the analyzer now in use into the one setting the
    /// pipeline reads.
    ///
    /// <para>There is exactly one place that undoes an input gain — <c>SpectrumPipeline.Enqueue</c>,
    /// via <see cref="ExternalGainSetting"/> — with the three ranging seams and the
    /// <c>params_json</c> recording already worked out against it. A sound card's front-end is
    /// remembered separately from the QA403's, because they are different benches, so the choice is
    /// which document feeds that one setting rather than a second gain concept. Everything
    /// downstream is untouched.</para>
    /// </summary>
    private void ApplyEffectiveExternalGain()
    {
        if (InputIsSoundCard)
        {
            // Captured lazily rather than at construction, because Program loads the external-gain
            // document into the setting after the container has built this service -- seeding it in
            // a field initializer would bank a zero and lose the QA403's divider on the way back.
            _sharedExternalGainDb ??= (_externalGain.LeftDb, _externalGain.RightDb, Tonebench.Data.ExternalGainConfig.DefaultUnit);

            Tonebench.Device.SoundCard.SoundCardSettings card = _soundCard.DeviceSettings;
            _externalGain.LeftDb = Clamp(card.FrontEndGainLeftDb);
            _externalGain.RightDb = Clamp(card.FrontEndGainRightDb);
            return;
        }

        if (_sharedExternalGainDb is not { } shared)
            return;

        _externalGain.LeftDb = Clamp(shared.Left);
        _externalGain.RightDb = Clamp(shared.Right);

        static double Clamp(double db) =>
            double.IsFinite(db) ? Math.Clamp(db, -Tonebench.Data.ExternalGainConfig.MaxAbsDb, Tonebench.Data.ExternalGainConfig.MaxAbsDb) : 0.0;
    }

    /// <summary>
    /// Remembers the analyzer-independent external gain and re-applies whichever is effective.
    /// Called by <c>PUT /api/config/external-gain</c>, whose document does not describe the sound
    /// card.
    /// </summary>
    public void SetSharedExternalGain(double leftDb, double rightDb, string? unit = null)
    {
        _sharedExternalGainDb = (leftDb, rightDb, Tonebench.Data.ExternalGainConfig.NormaliseUnit(unit));
        ApplyEffectiveExternalGain();
    }

    /// <summary>
    /// How the status bar reads the effective gain — <c>"Db"</c> or <c>"Times"</c>. Follows the same
    /// bench the two figures do, because it is stored beside them; see
    /// <see cref="Tonebench.Data.ExternalGainConfig.Unit"/> for why it is per bench rather than global.
    /// Never null, so the client has no "which unit did it mean" case to handle.
    /// </summary>
    public string ExternalGainUnit =>
        InputIsSoundCard
            ? Tonebench.Data.ExternalGainConfig.NormaliseUnit(_soundCard.Config.GainDisplayUnit)
            : _sharedExternalGainDb?.Unit ?? Tonebench.Data.ExternalGainConfig.DefaultUnit;

    /// <summary>Re-applies the sound card's gain after its settings change, without a reconnect.</summary>
    public void RefreshSoundCardGain()
    {
        if (InputIsSoundCard)
            ApplyEffectiveExternalGain();
    }

    /// <summary>
    /// Whether the <i>capturing</i> device is a sound card — directly, or as one half of a split.
    ///
    /// <para>The front-end gain is the question this answers, and it belongs to whichever device is
    /// measuring: it describes a divider or a preamp in front of an <i>input</i>, and the card's own
    /// document remembers it separately from the QA40x's because they are different benches. A split
    /// whose generator is a card and whose capture is a QA403 therefore uses the QA403's figure,
    /// which is the one in front of the input the samples come from.</para>
    /// </summary>
    private bool InputIsSoundCard =>
        _kind == DeviceKind.SoundCard
        || (_kind == DeviceKind.Split && _split.InputKind == DeviceKind.SoundCard);

    /// <summary>
    /// Whether a sound card is anywhere on this bench. What the status bar's card fields and the
    /// card dialog gate on: on a split the card may be the half that only plays, and its device
    /// name, its calibration and its control panel are all still worth reaching.
    ///
    /// <para>Public because three callers outside this class ask the same question and had each
    /// asked it as <c>Kind == SoundCard</c>, which is false on every split: saving the card document
    /// would not rebuild the device (so a new card, channel pair or calibration never reached the
    /// bench in use), and an issue report left the whole card block out — on exactly the bench whose
    /// faults are hardest to read without it.</para>
    /// </summary>
    public bool UsesSoundCard =>
        _kind == DeviceKind.SoundCard
        || (_kind == DeviceKind.Split
            && (_split.InputKind == DeviceKind.SoundCard || _split.OutputKind == DeviceKind.SoundCard));

    /// <summary>
    /// The capturing half's kind, which on anything but a split is the kind itself. Reported so the
    /// status bar can name both devices and keep the telemetry rows a QA40x half still has.
    /// </summary>
    public DeviceKind InputDeviceKind => _kind == DeviceKind.Split ? _split.InputKind : _kind;

    /// <summary>The generating half's kind. See <see cref="InputDeviceKind"/>.</summary>
    public DeviceKind OutputDeviceKind => _kind == DeviceKind.Split ? _split.OutputKind : _kind;

    /// <summary>
    /// The two clocks this bench is running on, or null where one device both plays and captures.
    /// Everything in the clock-drift feature asks this — what the response screens are offered, what
    /// the drift test stores it against, and what the dialog says it measured between.
    ///
    /// <para>Answered here because this is the one place that knows all three of the analyzer kind,
    /// the sound card's document and the split's halves. The endpoints would otherwise each have to
    /// take two more settings to ask the same question three different ways.</para>
    /// </summary>
    public BenchClocks? BenchClocks => Services.BenchClocks.For(_kind, _soundCard.Config, _split);

    /// <summary>
    /// The rate the generator is being driven at. The stream's own rate on every device with one
    /// clock, and the generating half's own on a composite — see
    /// <c>IAnalyzerDevice.GeneratorSampleRateHz</c>.
    ///
    /// <para>Every rate-dependent <i>generator</i> quantity reads this rather than the analysis
    /// rate: a square's quantised period and the level that follows from it, the periodic-noise
    /// crest factor the output range is derived from, and the chirp's period in generator frames.
    /// Tone snapping deliberately does not — it snaps to <i>analysis</i> bins, which is the whole
    /// reason tones survive two clocks.</para>
    /// </summary>
    public double GeneratorSampleRateHz =>
        _device.GeneratorSampleRateHz ?? (_streamConfig?.SampleRateHz ?? _lastSampleRateHz);

    /// <summary>
    /// The generator rate a measurement <i>request</i> should carry: the device's own where it has
    /// one, and <b>0</b> — "whatever rate this run is analysed at" — where it does not.
    ///
    /// <para><b>Not <see cref="GeneratorSampleRateHz"/>, and the difference cost a bench run.</b>
    /// That one answers with a concrete figure for the generator arithmetic, falling back to the
    /// last rate anything streamed at. A runner asks this <i>before</i> its stream exists, so that
    /// fallback is the rate of whatever ran last — and a request built from it claims a rate
    /// mismatch that is not there. Measured on a QA403-plus-D50 bench: a 96 kHz chirp started after
    /// a 48 kHz tone had its band capped at 19.2 kHz, the band a 48 kHz generator would impose, and
    /// its reference built at the wrong rate entirely. Coherence 0.48, a response the DUT never
    /// had.</para>
    ///
    /// <para>Zero is the honest answer because the request already reads it that way: its
    /// <c>GeneratorRateHz</c> falls back to the analysis rate, which is what "follow the capture
    /// rate" means.</para>
    /// </summary>
    public double GeneratorRateOverrideHz => _device.GeneratorSampleRateHz ?? 0.0;

    /// <summary>
    /// The widest frequency span this analyzer can actually analyse: the largest entry of
    /// <see cref="FrequencyResponseRequest.SupportedSpansHz"/> whose implied rate — twice it, since
    /// a span is analysed to Nyquist — is one this analyzer has.
    ///
    /// <para>Read by the impedance endpoint, because a broadband calibration standard is captured
    /// at the widest span available rather than at the measurement's own, so one capture serves
    /// every span it is later measured at. That was the literal 96 kHz of
    /// <c>ImpedanceRequest.MaxSpanHz</c> until 2026-11-14, which is wrong for any analyzer that
    /// does not reach 192 kHz: the capture is then refused outright by
    /// <see cref="RequireSupportedSampleRate"/> rather than merely being wider than asked for.</para>
    ///
    /// <para><b>It intersects with the span list rather than halving the fastest rate</b>, and that
    /// is the whole of the correctness here. A card reporting 176.4 kHz would give 88.2 kHz by
    /// simple halving, which is not a span this app has: the transform length is derived from the
    /// span through <see cref="FrequencyResponseRequest.BaseBinHz"/> and only the 48 kHz family
    /// lands on a power of two, so that figure would fail the run with
    /// "Input length must be a power of two" and name nothing anybody chose.</para>
    ///
    /// <para><b>Zero where no offered span is reachable</b> — a device that has not connected
    /// (<see cref="DeviceCapabilities"/> is only valid after <c>ConnectAsync</c>), or one whose
    /// rates are all outside the 48 kHz family. The caller reads zero as "not known" and keeps its
    /// own default rather than computing a span out of nothing.</para>
    /// </summary>
    public double WidestAnalysisSpanHz
    {
        get
        {
            IReadOnlyList<double> rates = Capabilities.SampleRatesHz;
            double widest = 0.0;

            foreach (double span in FrequencyResponseRequest.SupportedSpansHz)
            {
                if (span > widest && rates.Any(rate => Math.Abs(rate - (span * 2.0)) < 1e-6))
                    widest = span;
            }

            return widest;
        }
    }

    /// <summary>
    /// The rates a stepped sweep may choose among on this analyzer: every entry of
    /// <see cref="SweepResolution.CandidateSampleRatesHz"/> the device can <b>both</b> capture and
    /// play.
    ///
    /// <para><b>Both, because a sweep plays a tone and measures it.</b> That is the one place this
    /// app intersects the two ladders, and <see cref="DeviceCapabilities.OutputSampleRatesHz"/>'s
    /// own remarks explain why it must not be done in the capabilities themselves: a QA403
    /// capturing at 384 kHz with a 192 kHz DAC playing is a real measurement, and intersecting
    /// there would delete the input mode that rate exists for. A sweep is the opposite case — it
    /// has no capture-only variant, so a rate the generator cannot reach is a rate the run would be
    /// refused at by <see cref="RequireGeneratorRateUsable"/>, after the user had chosen it.</para>
    ///
    /// <para><b>Empty falls back to the DSP default</b> rather than to nothing: a device that has
    /// not connected reports no capabilities at all, and a sweep ladder of zero rates has no answer
    /// to give. The fallback is what the sweeps ran on before this existed, so an unconnected bench
    /// estimates exactly as it used to.</para>
    ///
    /// <para>Note a QA403 lands on <c>[48, 96, 192]</c> here with the 384 kHz capture it now always
    /// has as a split's half — because its <i>generator</i> stops at 192 kHz to protect the output
    /// relay. 384 kHz reaches this list only when the playing half is something else, which is
    /// exactly the bench that asked for it.</para>
    /// </summary>
    public IReadOnlyList<double> SweepSampleRatesHz
    {
        get
        {
            DeviceCapabilities caps = Capabilities;
            IReadOnlyList<double> capture = caps.SampleRatesHz;
            IReadOnlyList<double> play = caps.OutputSampleRatesHz;

            var usable = SweepResolution.CandidateSampleRatesHz
                .Where(rate => Has(capture, rate) && Has(play, rate))
                .ToArray();

            return usable.Length > 0 ? usable : SweepResolution.SupportedSampleRatesHz;

            static bool Has(IReadOnlyList<double> rates, double rate) =>
                rates.Any(r => Math.Abs(r - rate) < 1e-6);
        }
    }

    /// <summary>
    /// A session pinned to one already-constructed device, with nothing to switch to. Every test in
    /// the app builds a session this way, and the shape of that call is what this overload exists to
    /// keep — the real app goes through the factory.
    /// </summary>
    public DeviceSessionService(IAnalyzerDevice device, SpectrumPipeline spectrumPipeline, ILogger<DeviceSessionService> logger)
        : this(AnalyzerDeviceFactory.Pinned(device), spectrumPipeline, new CaptureDelaySetting(), logger)
    {
    }

    /// <summary>The pinned-device overload with a capture delay (and, optionally, an external gain) of the test's choosing.</summary>
    public DeviceSessionService(
        IAnalyzerDevice device, SpectrumPipeline spectrumPipeline, CaptureDelaySetting captureDelay,
        ILogger<DeviceSessionService> logger, ExternalGainSetting? externalGain = null)
        : this(AnalyzerDeviceFactory.Pinned(device), spectrumPipeline, captureDelay, logger, externalGain)
    {
    }

    /// <summary>The pinned-device overload with a stream recorder, for the tests that record one.</summary>
    public DeviceSessionService(
        IAnalyzerDevice device, SpectrumPipeline spectrumPipeline, CaptureDelaySetting captureDelay,
        ILogger<DeviceSessionService> logger, StreamRecorder streamRecorder)
        : this(AnalyzerDeviceFactory.Pinned(device), spectrumPipeline, captureDelay, logger,
            streamRecorder: streamRecorder)
    {
    }

    /// <summary>
    /// The settle time currently configured — see <see cref="CaptureDelaySetting"/>. The runners
    /// read it to size their per-point timeouts; the discard itself is applied here, not by them.
    /// </summary>
    public double CaptureDelayMs => captureDelay.Ms;

    /// <summary>The gain in front of the left input, in dB -- see <see cref="ExternalGainSetting"/>.</summary>
    public double ExternalGainLeftDb => _externalGain.LeftDb;

    /// <summary>The gain in front of the right input, in dB.</summary>
    public double ExternalGainRightDb => _externalGain.RightDb;

    /// <summary>
    /// A DUT-referred level as the analyzer's input will see it, i.e. with the external input gain
    /// put back. The one conversion the runners need: a level <em>target</em> names the DUT's
    /// output, while the input-range ladder is in the analyzer's own dBV, so a 0 dBV target through
    /// a 20 dB divider wants the range for −20 dBV. With no measured channel to name, the wider of
    /// the two results is returned -- one range serves both inputs, and too wide is the safe error.
    /// </summary>
    public double ToAnalyzerDbv(double dutDbv, StreamChannel? measuredChannel = null) =>
        measuredChannel is { } ch
            ? dutDbv + _externalGain.GainDb(ch)
            : dutDbv + Math.Max(_externalGain.LeftDb, _externalGain.RightDb);

    /// <summary>
    /// The settle time the most recent stimulus change or acquisition start actually discarded, or
    /// null when none has happened yet. What a saved result records, rather than the setting's
    /// current value: the two differ only if the setting was edited between a run and its save,
    /// and the record is meant to say what the run used.
    /// </summary>
    public double? LastAppliedCaptureDelayMs { get; private set; }

    /// <summary>Real QA403 hardware queue depth (docs/tonebench-stack-spec.md §2.1) — also the floor enforced on <see cref="StartAcquisitionAsync"/>'s <c>BlockFrameCount</c>.</summary>
    public const int MinBlockFrameCount = 1024;

    private readonly object _streamLock = new();

    /// <summary>
    /// Serializes every device-session-mutating operation (generator, input/output range,
    /// start/stop acquisition) against every other one. Without this, two concurrent requests
    /// could interleave: one enqueues a register write onto the currently active USB transfer
    /// pump (e.g. <see cref="SetOutputRangeAsync"/>'s pump-queued write) while another tears that
    /// same pump down (<see cref="StopStreamIfRunningAsync"/>), cancelling the pump's own token
    /// out from under the still-pending write — observed as <c>TaskCanceledException</c> out of
    /// <c>Qa40xTransport.WriteRegisterAsync</c> every time a Single capture auto-stops (the
    /// client's Stop call disables the generator right after stopping acquisition, and that
    /// disable's own output-range write must never race a concurrent teardown of the same pump).
    /// Each public entry point below acquires this once and delegates to a private "Core"
    /// implementation; internal cross-calls (e.g. <see cref="StartAcquisitionAsync"/> stopping a
    /// previous run) call the Core methods directly rather than re-entering the lock, since
    /// <see cref="SemaphoreSlim"/> is not reentrant.
    /// </summary>
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private DeviceInfo? _info;
    private double _inputRangeDbv;
    private double _outputRangeDbv;

    /// <summary>
    /// Survives a device switch and a reconnect deliberately — it describes what the person at the
    /// bench asked for, not the handle. A reconnect re-applies it to the new device rather than
    /// silently reverting to <see cref="RangingMode.Auto"/> behind a status bar still showing
    /// "Manual". It does not survive a restart: see <see cref="Services.RangingMode"/>.
    /// </summary>
    private RangingMode _rangingMode = RangingMode.Auto;

    /// <summary>The effective generator config — tone frequencies already snapped, if snapping is on. This is what the device is playing and what status reports.</summary>
    private GeneratorConfig _generatorConfig = GeneratorConfig.Off;

    /// <summary>
    /// The unsnapped config exactly as the client asked for it. Kept so <see cref="ReapplyGeneratorAsync"/>
    /// can re-snap from the original request when the FFT size or sample rate changes — re-snapping a
    /// previously snapped frequency would let it drift a bin at a time.
    /// </summary>
    private GeneratorConfig _requestedGeneratorConfig = GeneratorConfig.Off;

    /// <summary>
    /// The last generator config that actually played something — i.e. with a channel enabled. Null
    /// until the first one.
    ///
    /// <para>Exists for one job: seeding the output range when <see cref="SetRangingModeAsync"/>
    /// switches into <see cref="RangingMode.Manual"/>. Stop disables both channels, and a silent
    /// generator correctly derives the <i>smallest</i> output range — the quietest state the DAC
    /// has, which is what a noise-floor measurement wants. Pinning that is useless: switching to
    /// Manual while stopped handed the user -12 dBV every time, and the next Run then clipped
    /// against it. The stimulus that was playing is the honest answer to "what was Auto using".</para>
    /// </summary>
    private GeneratorConfig? _lastVoicedGeneratorConfig;

    /// <summary>Last sample rate the client told us about, for snapping tones while no stream is running.</summary>
    private double _lastSampleRateHz = 48000.0;

    /// <summary>Backs <see cref="OutputClippedDuringAcquisition"/>; zeroed by the acquisition start.</summary>
    private bool _outputClippedDuringAcquisition;

    /// <summary>
    /// The device's <see cref="IAnalyzerDevice.StreamAsync"/> loop and the spectrum-facing "Acquiring" state
    /// are deliberately two different things. The QA403's DAC only clocks samples out while the same USB
    /// stream that carries ADC data is running (<see cref="IAnalyzerDevice.ConfigureGeneratorAsync"/> alone
    /// changes nothing on the wire — see <see cref="Hardware.Qa40xUsbDevice.ChunkPump.BuildTxChunk"/>), so
    /// the generator needs this stream running independently of whether the UI has pressed "Start
    /// Acquisition". <see cref="_streamCts"/>/<see cref="_streamTask"/> track that underlying stream;
    /// <see cref="_isAcquiring"/> tracks only whether received blocks are being forwarded to
    /// <see cref="spectrumPipeline"/>. <see cref="EnsureStreamRunningAsync"/> starts (or restarts, on a
    /// config change) the stream; <see cref="StopStreamIfRunningAsync"/> tears it down. Whichever of
    /// acquisition/generator asked for the stream last "owns" its <see cref="AcquisitionConfig"/> — see
    /// <see cref="StartAcquisitionAsync"/> and <see cref="ConfigureGeneratorAsync"/>.
    /// </summary>
    private CancellationTokenSource? _streamCts;

    private Task? _streamTask;
    private AcquisitionConfig? _streamConfig;
    private volatile bool _isAcquiring;
    private volatile bool _deviceLost;

    /// <summary>Whether <see cref="OnDeviceLost"/> is already subscribed — see <see cref="ConnectAsync"/>, which may run more than once.</summary>
    private bool _deviceLostHooked;

    /// <summary>
    /// Bumped by every successful <see cref="ConnectAsync"/>. <see cref="OnDeviceLost"/> hands its
    /// generation to the background teardown, which then does nothing if the user has already
    /// reconnected in the meantime — otherwise a teardown scheduled microseconds before a Connect
    /// could stop the *new* session's pipeline.
    /// </summary>
    private int _connectionGeneration;

    /// <summary>
    /// <see cref="Environment.TickCount64"/> of the last liveness ping, or -1 for "never". Throttles
    /// <see cref="GetStatusAsync"/>: several browser tabs polling at 2 s each must not turn into
    /// several register exchanges per second on the wire.
    /// </summary>
    private long _lastPingTicks = -1;

    /// <summary>Floor on the gap between liveness pings, just under the UI's own 2 s status poll so a single poller still gets one per poll.</summary>
    private const int LivenessPingIntervalMs = 1500;

    public DeviceCapabilities Capabilities => _device.Capabilities;

    public bool Acquiring => _isAcquiring;

    /// <summary>An acquisition is something the project file cannot change under — see <see cref="IMeasurementRun"/>.</summary>
    bool IMeasurementRun.Running => _isAcquiring;

    /// <summary>
    /// Nothing: a plain acquisition is not a measurement run. It is stopped by
    /// <see cref="StopAcquisitionAsync"/> on its own explicit call, which the client's switch makes
    /// right after cancelling the runners.
    /// </summary>
    Task IMeasurementRun.CancelAsync() => Task.CompletedTask;

    /// <summary>Whether a device has been brought up. False from startup until <see cref="ConnectAsync"/> first succeeds — the app runs fine with no QA403 attached.</summary>
    public bool Connected => _info is not null;

    /// <summary>Null until <see cref="ConnectAsync"/> completes.</summary>
    public DeviceInfo? Info => _info;

    public double InputRangeDbv => _device.InputRangeDbv;

    /// <summary>
    /// RX chunks the device has dropped since connect. Exposed so a caller that owns a measurement
    /// window can bracket it — <see cref="ThdSweepRunner"/> reads this either side of every point, so
    /// a hole in the sample stream can be attributed to the frequency it actually contaminated
    /// instead of to the run as a whole.
    /// </summary>
    public long DroppedRxChunks => _device.DroppedRxChunks;

    /// <summary>
    /// Assembled blocks dropped after calibration because this service's own drain loop fell behind
    /// — see <see cref="IAnalyzerDevice.DroppedBlocks"/>. Reported alongside
    /// <see cref="DroppedRxChunks"/> rather than folded into it, because the two name different
    /// halves of the pipeline and only reporting one of them is what hid this for weeks.
    /// </summary>
    public long DroppedBlocks => _device.DroppedBlocks;

    public double OutputRangeDbv => _outputRangeDbv;

    /// <summary>
    /// Who is choosing the two analog ranges — see <see cref="Services.RangingMode"/>. Always
    /// <see cref="RangingMode.Auto"/> at startup; nothing persists it.
    /// </summary>
    public RangingMode RangingMode => _rangingMode;

    /// <summary>
    /// True when the generator is asking for more than the output range's full scale, so what
    /// reaches the DUT is clipped.
    ///
    /// <para><b>Reachable under <see cref="RangingMode.Auto"/> as well, which this said it was not.</b>
    /// The claim was that Auto derives the range from this very config so it is false by
    /// construction — but <see cref="OutputRangeSelector.Select"/> ends with "or the largest
    /// supported range when nothing fits (better a range that clips than one that clips harder)", and
    /// a level above the top range's full scale is exactly that case. Measured: pink noise at 12 dBV
    /// on a QA403 wants a 15.8 V peak against the 18 dBV range's 11.2 V, in Auto, and the flag is
    /// true. Pinned by <c>OutputRangeClipping_IsReportedInAutoWhenTheLevelOverrunsTheTopRange</c>.</para>
    ///
    /// <para>On the status so the status bar can say it. A clipped DAC is not visible anywhere else:
    /// the generator panel still reads the level that was asked for, the spectrum shows a tone at
    /// roughly the right frequency, and only the harmonics give it away — which is precisely the
    /// measurement the user is looking at.</para>
    /// </summary>
    public bool OutputRangeClipping =>
        _info is not null && OutputRangeSelector.Clips(_generatorConfig, _outputRangeDbv, GeneratorSampleRateHz);

    /// <summary>
    /// Whether the generator has clipped at any point since this acquisition started, as against
    /// <see cref="OutputRangeClipping"/>, which is only ever about the stimulus in force right now.
    ///
    /// <para><b>A sweep is why this exists.</b> Every point configures its own stimulus, so a run
    /// whose loudest points overran the DAC leaves the live flag false the moment it moves on — and
    /// by the time the curve is on screen there is nothing anywhere to say part of it was measured
    /// through a clipped stimulus. Worse on the distortion sweeps than anywhere, since clipping adds
    /// harmonics and that is the quantity being plotted. Per acquisition and zeroed at the start of
    /// one, exactly like <see cref="DroppedRxChunks"/> and for the reason stated there: the only
    /// question it is ever asked is whether the measurement on screen can be trusted, and a running
    /// total since connect cannot answer it.</para>
    /// </summary>
    public bool OutputClippedDuringAcquisition => _outputClippedDuringAcquisition;

    /// <summary>
    /// The device state to stamp on a diagnostic record, read at the instant the record is analyzed
    /// rather than pushed on change — see <see cref="Tonebench.App.Diagnostics.DiagnosticContext"/>.
    /// Reads <see cref="IAnalyzerDevice.InputRangeDbv"/> off the device, not the
    /// <c>_inputRangeDbv</c> mirror, because autoranging steps the range inside the device and the
    /// mirror is exactly the value that goes stale when it does. Field reads only: this runs on the
    /// pipeline's DSP thread and must not take <see cref="_sessionLock"/>.
    /// </summary>
    public DiagnosticContext CaptureDiagnosticContext() => new(
        InputRangeDbv: _device.InputRangeDbv,
        OutputRangeDbv: _outputRangeDbv,
        AutoRangeEnabled: AutoRangeEnabled ?? false,
        Generator: _generatorConfig,
        DroppedRxChunks: _device.DroppedRxChunks,
        DroppedBlocks: _device.DroppedBlocks);

    /// <summary>
    /// Arms the device's stimulus tap and opens a recording for the acquisition that is starting, or
    /// does nothing at all when the recorder is disarmed.
    ///
    /// <para>The tap is attached here rather than when the device is built, and detached when the
    /// recording ends, because a non-null tap is what makes the transfer loop copy every chunk it
    /// builds: a recorder left attached would pay that cost on every run for a diagnostic that is
    /// normally off. The order matters — tap first, then <c>Begin</c>, then the stream — so that a
    /// generator-only stream already running starts being recorded at once rather than from the
    /// first chunk after the file was opened.</para>
    /// </summary>
    private void BeginStreamRecording(AcquisitionConfig config)
    {
        if (streamRecorder is not { Enabled: true })
            return;

        _device.StimulusTap = streamRecorder;
        streamRecorder.Begin(new StreamRecordingContext(
            SampleRateHz: config.SampleRateHz,

            // The device's own answer, not GeneratorSampleRateHz -- that property falls back to
            // _streamConfig, which this call runs ahead of. A null here is a device whose generator
            // shares the capture clock, which is every analyzer but a split.
            StimulusSampleRateHz: _device.GeneratorSampleRateHz ?? config.SampleRateHz,
            InputChannelCount: config.ChannelCount,
            Analyzer: _info?.ProductName ?? "(not connected)",
            SerialNumber: _info?.SerialNumber,
            FirmwareVersion: _info?.FirmwareVersion,
            DeviceKind: _kind.ToString(),
            InputRangeDbv: _device.InputRangeDbv,
            OutputRangeDbv: _outputRangeDbv,
            ExternalGainLeftDb: ExternalGainLeftDb,
            ExternalGainRightDb: ExternalGainRightDb,
            CaptureDelayMs: captureDelay.Ms,
            GeneratorLeft: DiagnosticRecorder.Describe(_generatorConfig.Left),
            GeneratorRight: DiagnosticRecorder.Describe(_generatorConfig.Right)));
    }

    /// <summary>
    /// Closes the recording, waits for its files to be complete, and detaches the tap. Awaited
    /// rather than fired off: the log line naming the file is what a user is asked to send, and it
    /// has to be in the file before the next acquisition's line.
    /// </summary>
    private async Task EndStreamRecordingAsync()
    {
        if (streamRecorder is null)
            return;

        await streamRecorder.EndAsync().ConfigureAwait(false);
        _device.StimulusTap = null;
    }

    /// <summary>The effective (already bin-snapped, if snapping is on) generator config — what the device is actually playing.</summary>
    public GeneratorConfig EffectiveGeneratorConfig => _generatorConfig;

    /// <summary>The acquisition config currently driving the device stream, or null when nothing has started one.</summary>
    public AcquisitionConfig? StreamConfig => _streamConfig;

    /// <summary>
    /// Whether the simulated DUT adds its own noise floor (the white floor plus the 1/f texture
    /// layered on it), or <c>null</c> when the device isn't the simulator. Deliberately reached by
    /// type test rather than through <see cref="IAnalyzerDevice"/>: real hardware has no such knob,
    /// so putting it on the interface would be a lie. See <see cref="SimulatedSignalProfile"/>.
    /// </summary>
    public bool? SimulatedNoiseEnabled => SimulatedHalf?.SignalProfile.NoiseEnabled;

    /// <summary>
    /// Whether the simulated DUT adds mains hum (a 50 Hz fundamental plus two harmonics 6 dB apart),
    /// or <c>null</c> when the device isn't the simulator. Independent of
    /// <see cref="SimulatedNoiseEnabled"/> — hum is a deterministic tone stack, not part of the
    /// noise path.
    /// </summary>
    public bool? SimulatedHumEnabled => SimulatedHalf?.SignalProfile.HumEnabled;

    /// <summary>
    /// Whether the simulated generator carries its fixed harmonic stack (H2 at −46 dBc, H3 at
    /// −60 dBc, 12 dB per order below that — 0.51 % THD on every linear DUT at any level), or
    /// <c>null</c> when the device isn't the simulator. This is the profile's own setting: the four
    /// nonlinear DUT models switch the stack off regardless (<see cref="SimulatedDevice"/>), so
    /// under one of those it reads true while contributing nothing.
    /// </summary>
    public bool? SimulatedHarmonicsEnabled => SimulatedHalf?.SignalProfile.HarmonicsEnabled;

    /// <summary>
    /// Which simulated device under test is patched in, or <c>null</c> when the device isn't the
    /// simulator. Unlike the noise and hum switches this one changes the DUT's <i>response</i>
    /// rather than what is added to it — see <see cref="SimulatedDutModel"/>.
    /// </summary>
    public SimulatedDutModel? SimulatedDut => SimulatedHalf?.SignalProfile.Dut;

    /// <summary>Whether the simulator couples its channels — see <see cref="SimulatedCrosstalk"/>; null when the device isn't the simulator.</summary>
    public bool? SimulatedCrosstalkEnabled => SimulatedHalf?.SignalProfile.CrosstalkEnabled;

    /// <summary>The simulated amplifier's output stage — see <see cref="SimulatedOutputStage"/>; null when the device isn't the simulator.</summary>
    public SimulatedOutputStageModel? SimulatedOutputStage => SimulatedHalf?.SignalProfile.OutputStage;

    /// <summary>What hangs on the simulated amplifier's output; null when the device isn't the simulator.</summary>
    public SimulatedLoad? SimulatedLoad => SimulatedHalf?.SignalProfile.Load;

    /// <summary>
    /// The simulator the knobs describe: the device itself, or whichever half of a split is one.
    ///
    /// <para><b>The capturing half first</b>, because every one of these knobs is about what the
    /// captured signal looks like — the DUT, its noise and hum, the crosstalk, the output stage and
    /// its load are all applied on the way <i>in</i>. The generator's harmonic stack is the
    /// exception, and <see cref="SimulatedHalves"/> is why that still works: a write goes to every
    /// simulated half, so on a two-simulator bench the stack reaches the one that plays.</para>
    /// </summary>
    private SimulatedDevice? SimulatedHalf => SimulatedHalves.FirstOrDefault();

    /// <summary>
    /// Every simulated device on this bench, capturing half first. One entry on a plain simulator,
    /// none on hardware, and on a split whatever of its two halves is simulated.
    /// </summary>
    private IEnumerable<SimulatedDevice> SimulatedHalves
    {
        get
        {
            if (_device is SimulatedDevice single)
            {
                yield return single;
                yield break;
            }

            if (_device is not SplitAnalyzerDevice split)
                yield break;

            if (split.Input is SimulatedDevice input)
                yield return input;
            if (split.Output is SimulatedDevice output)
                yield return output;
        }
    }

    /// <summary>
    /// Patches the simulated DUT's signal profile; <c>null</c> leaves a setting alone. Returns
    /// false when the device isn't the simulator, so the caller can answer with a 400 rather than
    /// silently accepting a setting that does nothing.
    /// </summary>
    public bool TrySetSimulatedProfile(
        bool? noiseEnabled, bool? humEnabled, bool? harmonicsEnabled = null, SimulatedDutModel? dut = null, bool? crosstalkEnabled = null,
        SimulatedOutputStageModel? outputStage = null, SimulatedLoad? load = null)
    {
        // Every simulated half, not just the one the knobs are read from. On a two-simulator split
        // the DUT and the noise belong to the capturing half while the generator's harmonic stack
        // belongs to the half that plays, so a write to one of them would silently do nothing to
        // whichever setting lives on the other.
        var halves = SimulatedHalves.ToArray();
        if (halves.Length == 0)
            return false;

        foreach (SimulatedDevice simulated in halves)
        {
            // Snapshot once: the property returns an immutable record, and re-reading it per field
            // could mix two different profiles into the one being written back.
            var profile = simulated.SignalProfile;
            simulated.SignalProfile = profile with
            {
                NoiseEnabled = noiseEnabled ?? profile.NoiseEnabled,
                HumEnabled = humEnabled ?? profile.HumEnabled,
                HarmonicsEnabled = harmonicsEnabled ?? profile.HarmonicsEnabled,
                Dut = dut ?? profile.Dut,
                CrosstalkEnabled = crosstalkEnabled ?? profile.CrosstalkEnabled,
                OutputStage = outputStage ?? profile.OutputStage,
                Load = load ?? profile.Load,
            };
        }

        return true;
    }

    /// <summary>
    /// Brings the device up, throwing whatever <see cref="IAnalyzerDevice.ConnectAsync"/> throws
    /// when there is nothing to connect to.
    ///
    /// Called once at startup by <see cref="DeviceLifecycleHostedService"/> and again, on demand,
    /// by <c>POST /api/device/connect</c>: a QA403 that isn't plugged in yet must not stop the app
    /// from starting (it used to take host startup down with it, so <c>run.bat</c> with no device
    /// attached died on a stack trace), so this has to be retryable rather than one-shot.
    ///
    /// Retrying after a failure is safe: <see cref="Hardware.Qa40xUsbDevice.ConnectAsync"/> assigns
    /// none of its own state and disposes the transport it opened unless every step succeeded.
    /// Retrying after *success* is a no-op — opening a second transport over a live one would claim
    /// the interface twice.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConnectCoreAsync(ct);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// <see cref="ConnectAsync"/> without the lock, for callers that already hold it — the same
    /// public-entry-point/Core split every other operation on this class uses, because
    /// <see cref="SemaphoreSlim"/> is not reentrant and <see cref="SwitchDeviceAsync"/> has to
    /// connect the device it just built while still holding the session.
    /// </summary>
    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        if (_info is not null)
            return;

        // Once per device, not once per attempt: every failed attempt would otherwise leave
        // another subscription behind, and a later disconnect would then log once per attempt.
        if (!_deviceLostHooked)
        {
            _device.DeviceLost += OnDeviceLost;
            _deviceLostHooked = true;
        }

        _info = await _device.ConnectAsync(ct);

        // Most sensitive input range (best resolution for a fresh measurement), and the *largest*
        // output range — maximum generator headroom, so a default tone level can't sit above DAC
        // full scale and arrive pre-clipped before the user has touched anything.
        _inputRangeDbv = Capabilities.InputRangesDbv[0];
        _outputRangeDbv = Capabilities.OutputRangesDbv[^1];

        // Push both to the device rather than only caching them here. The device has its own field
        // defaults, and leaving the two unsynced means status reports one full scale while the
        // device actually clips at another — invisible until the ranges started doing something.
        await _device.SetInputRangeAsync(_inputRangeDbv, ct);
        await _device.SetOutputRangeAsync(_outputRangeDbv, ct);

        // Same reasoning one line up, for the third thing the device has its own default for. A
        // device brought up while the session is in Manual would otherwise arrive autoranging, and
        // walk the range the user pinned back to wherever it liked.
        ApplyRangingModeToDevice();

        // A fresh handle: whatever was lost before belonged to the previous connection.
        _deviceLost = false;
        _connectionGeneration++;
        logger.LogInformation(
            "Connected to {Product} (serial {Serial}, fw {Firmware}, simulated={Simulated})",
            _info.ProductName, _info.SerialNumber, _info.FirmwareVersion, _info.IsSimulated);
    }

    /// <summary>Which analyzer the session is talking to, connected or not.</summary>
    public DeviceKind Kind => _kind;

    /// <summary>
    /// The lowest frequency the analyzer in use can actually excite, in Hz — 0 on a device with no
    /// floor. Read by the chirp runners, which clamp a sweep start to it rather than refusing one.
    /// </summary>
    public double MinStimulusFrequencyHz => _device.MinStimulusFrequencyHz;

    /// <summary>
    /// The highest sample rate the analyzer in use can <i>play</i> a stimulus at, in Hz, or null on
    /// one with no ceiling. The FFT screen's Generator panel reads it to stand down at a
    /// capture-only rate, so <see cref="RequireGeneratorRateUsable"/>'s refusal is what catches a
    /// mistake rather than what tells the user about a known limitation.
    ///
    /// <para><b>Null, never <see cref="double.PositiveInfinity"/>,</b> because this crosses the
    /// wire: <c>System.Text.Json</c> will not write a non-finite double and finds that out part way
    /// through serializing, which truncates the whole response — the same trap
    /// <c>ImpedanceStatusJsonTests</c> exists for. The default
    /// <see cref="IAnalyzerDevice.MaxGeneratorSampleRateHz"/> <i>is</i> infinity, so every analyzer
    /// but the QA403 and the simulator would hit it.</para>
    /// </summary>
    public double? MaxGeneratorSampleRateHz =>
        double.IsFinite(_device.MaxGeneratorSampleRateHz) ? _device.MaxGeneratorSampleRateHz : null;

    /// <summary>
    /// Refuses a rate the analyzer in use does not have, here rather than at the device.
    ///
    /// <para>The refusal has to be <b>synchronous with whoever asked for the measurement</b>: the
    /// stream is started on a pump task, so a rate the device rejects surfaces as an
    /// <c>ArgumentOutOfRangeException</c> on a task nobody awaits — the run simply never produces a
    /// point, with nothing to say why. Thrown where the caller can see it, it is an ordinary refusal
    /// the endpoints turn into a 409 carrying this message.</para>
    ///
    /// <para><b>This method used to be private, and its own remark that
    /// <see cref="StartAcquisitionCoreAsync"/> is "the one place every measurement passes through"
    /// was the bug</b> (corrected 2026-11-14). That is true of the <i>acquisition</i> and false of
    /// the <i>request</i>: a runner's <c>StartAsync</c> validates, spawns <c>RunAsync</c> on a task
    /// and returns, so by the time the acquisition starts the endpoint has already answered 200.
    /// Measured: <c>POST /api/frequency-response/start</c> at a span the analyzer could not reach
    /// answered <c>200 OK</c> with a plausible plan, and the run then died on its own task. So the
    /// eight runners that stream at a rate derived from their request call this in <c>StartAsync</c>,
    /// where the caller is still listening, and the call in <c>StartAcquisitionCoreAsync</c> stays as
    /// the backstop for everything else — the live FFT screen, which is genuinely synchronous there,
    /// and any future caller that forgets.</para>
    ///
    /// <para><c>ClockDriftRunner</c> is the one runner that does not call it and does not need to:
    /// it takes its rate from <see cref="Capabilities"/> rather than from a request, so it cannot
    /// name one the analyzer does not have.</para>
    ///
    /// <para>It only bites on an analyzer whose rates are not the QA403's, which today means a sound
    /// card: its rates are probed from the hardware, so a card that cannot do 192 kHz does not offer
    /// it and a span that implies 192 kHz is refused rather than half-measured.</para>
    /// </summary>
    public void RequireSupportedSampleRate(double sampleRateHz)
    {
        IReadOnlyList<double> supported = Capabilities.SampleRatesHz;
        foreach (double rate in supported)
        {
            if (Math.Abs(rate - sampleRateHz) < 1e-6)
                return;
        }

        throw new InvalidOperationException(
            $"This analyzer cannot run at {sampleRateHz / 1000.0:0.###} kHz. It supports {string.Join(", ", supported.Select(r => $"{r / 1000.0:0.###} kHz"))} — pick a frequency span that asks for one of those.");
    }

    /// <summary>
    /// Refuses a <i>voiced</i> generator above <see cref="IAnalyzerDevice.MaxGeneratorSampleRateHz"/>.
    /// Silence is always allowed, because Stop and every measurement switch disable both channels
    /// unconditionally and that must never fail — the same reasoning as the <c>_info is null</c>
    /// branch in <see cref="ConfigureGeneratorCoreAsync"/>.
    ///
    /// <para>This sits in <see cref="ConfigureGeneratorCoreAsync"/> for the reason the capture delay
    /// does: it is the one place every stimulus change passes through, so the live FFT screen and
    /// every point of every sweep are covered by one check rather than each screen remembering.</para>
    ///
    /// <para>It throws rather than clamping the rate or silently dropping the tones.
    /// <see cref="InvalidOperationException"/> is what <c>AcquisitionEndpoints</c> already maps to a
    /// 409 carrying the message, so the user is told; quietly playing a different stimulus than the
    /// one asked for, or quietly capturing with no stimulus at all, would both be reported as a
    /// successful measurement of something nobody requested.</para>
    /// </summary>
    private void RequireGeneratorRateUsable(GeneratorConfig config, double sampleRateHz, double analysisRateHz)
    {
        if (!config.Left.Enabled && !config.Right.Enabled)
            return;

        // A rate the generating half cannot play at all. Reachable only on a composite, where the
        // two ladders are deliberately not intersected -- a capture-only run at a rate the output
        // side has never heard of is legitimate, and it is asking it to *play* that is not.
        IReadOnlyList<double> playable = Capabilities.OutputSampleRatesHz;
        if (playable.Count > 0 && !playable.Any(rate => Math.Abs(rate - sampleRateHz) < 1e-6))
        {
            throw new InvalidOperationException(
                $"This bench's generator cannot run at {sampleRateHz / 1000.0:0.###} kHz. It accepts "
                    + $"{string.Join(", ", playable.Select(rate => $"{rate / 1000.0:0.###}"))} kHz. Choose one of "
                    + "those as the generator rate, or turn the generator off to capture without it.");
        }

        // Two rates have to be an integer ratio for a stimulus carrying its own period to survive:
        // that period has to be a whole number of frames on *both* sides, and a 44.1 kHz family rate
        // against a 48 kHz one provably cannot be -- a record of N frames at 192 kHz needs
        // N x 147/640 to be an integer, and 640 = 2^7 x 5 while 147 = 3 x 7^2 carries no factor 5,
        // so no power-of-two N ever is. Radix-2 forbids the only N that would, which makes this a
        // permanent property of a mixed-family pairing rather than a limitation to be lifted later.
        //
        // Judged per *stimulus* rather than per voiced generator, which is the correction of
        // 2026-11-14. This arm was unreachable until the 44.1 kHz family was probed -- every rate
        // the app offered was a power-of-two multiple of 48 kHz -- and it was gated on "either
        // channel is enabled", so the first thing anyone would try on a 44.1 kHz DAC, a 1 kHz tone,
        // would have been refused by a message whose own next sentence says tones are fine. A tone
        // is snapped to an *analysis* bin by SnapTones, so it is coherent in the record whatever
        // rate synthesized it; see PlaysAPeriodicStimulus for which kinds are immune and why.
        if ((PlaysAPeriodicStimulus(config.Left) || PlaysAPeriodicStimulus(config.Right))
            && !IsPeriodicRatePair(sampleRateHz, analysisRateHz))
        {
            throw new InvalidOperationException(PeriodicRatePairMessage(sampleRateHz, analysisRateHz));
        }

        double maxHz = _device.MaxGeneratorSampleRateHz;
        if (sampleRateHz <= maxHz + 1e-6)
            return;

        throw new InvalidOperationException(
            $"This analyzer's generator cannot be used at {sampleRateHz / 1000.0:0.###} kHz — it is an input-only "
                + $"rate. The generator works up to {maxHz / 1000.0:0.###} kHz. Measured on a QA403 at 384 kHz, the "
                + "output is about 12 dB low with roughly 44 % distortion and enough near-DC content to cycle the "
                + "device's own output protection relay, which a software stop does not clear. Turn the generator "
                + "off to capture at this rate, or drop to a lower rate to use it.");
    }

    /// <summary>
    /// Refuses a run whose stimulus carries its own period on a bench whose two rates are not an
    /// integer ratio — the same rule <see cref="RequireGeneratorRateUsable"/> applies, asked before
    /// the run rather than when its first stimulus is configured.
    ///
    /// <para><b>Why both.</b> That one is reached from <c>ConfigureGeneratorCoreAsync</c>, which a
    /// runner calls from <c>RunAsync</c> — on the task, after the endpoint has answered. Measured on
    /// the bench 2026-11-14 (Topping D50 III at 44.1 kHz into a QA403 at 192 kHz):
    /// <c>POST /api/frequency-response/start</c> answered <b>200 OK</b> with a plan, and the refusal
    /// arrived three seconds later as <c>phase: Failed</c>. The message was right and the moment was
    /// wrong — exactly what <see cref="RequireSupportedSampleRate"/>'s remarks describe, one check
    /// along. The UI never showed it, because the panels disable RUN on this pairing already; the
    /// API did.</para>
    ///
    /// <para>It needs no <see cref="GeneratorConfig"/>, unlike the fuller check: whether a run plays
    /// something periodic is a property of the <i>measurement</i>, which the caller knows before it
    /// has built a stimulus. The three runners whose stimulus has a period ask it; the stepped-sine
    /// family does not, because one tone at a time is coherent by bin snapping whatever the rates.</para>
    /// </summary>
    public void RequirePeriodicStimulusRatePair(double analysisRateHz)
    {
        double generatorRateHz = _device.GeneratorSampleRateHz ?? analysisRateHz;
        if (IsPeriodicRatePair(generatorRateHz, analysisRateHz))
            return;

        throw new InvalidOperationException(PeriodicRatePairMessage(generatorRateHz, analysisRateHz));
    }

    /// <summary>
    /// The one wording, so the two callers above cannot drift about what a mixed-family pairing
    /// costs — which is the fault this file's own history is full of.
    /// </summary>
    private static string PeriodicRatePairMessage(double generatorRateHz, double analysisRateHz) =>
        $"A generator at {generatorRateHz / 1000.0:0.###} kHz against a capture at "
            + $"{analysisRateHz / 1000.0:0.###} kHz is not an integer ratio, so a stimulus with a "
            + "period of its own cannot line up with the analysis record — a 44.1 kHz family rate "
            + "against a 48 kHz one is that case, at every FFT size. The chirp and periodic "
            + "noise need an integer ratio; tones, IMD, the multitone comb, square and triangle "
            + "waves and every stepped-sine sweep are unaffected. Measure the frequency response "
            + "with the stepped sine instead, or set a generator rate in the same family as the "
            + "capture rate.";

    /// <summary>
    /// Whether two sample rates are an integer ratio either way round, which is what a periodic
    /// stimulus needs to land on the same grid at both ends. Equal rates trivially are.
    /// </summary>
    internal static bool IsPeriodicRatePair(double aHz, double bHz)
    {
        if (!double.IsFinite(aHz) || !double.IsFinite(bHz) || aHz <= 0 || bHz <= 0)
            return false;

        double high = Math.Max(aHz, bHz);
        double low = Math.Min(aHz, bHz);
        double ratio = high / low;

        return Math.Abs(ratio - Math.Round(ratio)) < 1e-9;
    }

    /// <summary>
    /// Whether this channel plays a stimulus that carries a period of its own, which is the only
    /// thing the integer-ratio rule in <see cref="RequireGeneratorRateUsable"/> applies to.
    ///
    /// <para>The kind selects which settings object is in play and that object's own period field
    /// decides, so the chirp and periodic noise are caught while the continuous noise forms --
    /// <c>PeriodFrames</c> 0, the documented streaming case -- are not. A bare kind list would
    /// have to restate what those fields already say.</para>
    ///
    /// <para><b>The default is true on purpose.</b> A kind added to the enum later is taken to
    /// carry a period until somebody decides otherwise, so a new periodic stimulus is refused on a
    /// mixed-family pairing rather than silently putting a drifting ripple on every trace. The arms
    /// below are the kinds that are immune for a reason that can be stated: <c>Tones</c> and
    /// <c>Multitone</c> both reduce to summing the configured tones (see
    /// <c>Qa40xGeneratorWaveform</c>) and are snapped to <i>analysis</i> bins by <c>SnapTones</c>,
    /// so they are coherent in the record whatever rate synthesized them; <c>Square</c> and
    /// <c>Triangle</c> were never periodic over the analysis record even on one rate, their
    /// guarantee being that every harmonic lands on a multiple of the generator's own fs/M, which
    /// is a property of that clock alone and survives any capture rate.</para>
    /// </summary>
    private static bool PlaysAPeriodicStimulus(ChannelGeneratorConfig channel) =>
        channel.Enabled && channel.Kind switch
        {
            GeneratorKind.Off or GeneratorKind.Tones or GeneratorKind.Multitone
                or GeneratorKind.Square or GeneratorKind.Triangle => false,
            GeneratorKind.WhiteNoise or GeneratorKind.PinkNoise => (channel.Noise?.PeriodFrames ?? 0) > 0,
            GeneratorKind.EchoChirp => (channel.Chirp?.RepeatFrames ?? 0) > 0,
            _ => true,
        };

    /// <summary>
    /// One of the sound card's settings, or null when that is not the analyzer in use — the null
    /// convention the <c>Simulated*</c> fields established, so the UI can gate on the field rather
    /// than on a device-kind string.
    /// </summary>
    private string? SoundCardField(Func<Tonebench.Data.SoundCardConfig, string> select) =>
        UsesSoundCard ? select(_soundCard.Config) : null;

    /// <summary>
    /// Replaces the analyzer with one of the requested kind: real QA403 or simulator, chosen at
    /// runtime rather than by a command-line flag.
    ///
    /// <para><b>The old device is torn down exactly as it is at host shutdown</b>, and for exactly
    /// the same reasons — pipeline stopped, stream stopped, transfer pump <i>joined</i>, and only
    /// then the handle released. Skipping any of that leaves the QA403 streaming with nobody reading
    /// it, which is the state that makes it unreachable to every app until it is power-cycled. See
    /// <see cref="ShutdownAsync"/>, whose remarks are the long version.</para>
    ///
    /// <para>Refused while a measurement is running rather than interrupting one: every runner in the
    /// app holds the session across its whole sweep, and pulling the device out from under one would
    /// surface as an unexplained mid-run failure rather than as the deliberate act it was.</para>
    ///
    /// <para>The new device is connected on the spot, and a connect failure is <b>not</b> an error
    /// here: switching to Hardware with no QA403 attached leaves the session disconnected and the
    /// Connect button live, which is the same state the app starts in when nothing is plugged in.</para>
    /// </summary>
    /// <summary>
    /// Rebuilds the current analyzer from its settings as they now stand — what a sound card needs
    /// after its card, rate, channel pair or calibration changes.
    ///
    /// <para><see cref="SwitchDeviceAsync"/> cannot do this: it returns early when the kind has
    /// not changed, which is right for a dropdown and wrong here. And a settings change genuinely
    /// needs the device rebuilt rather than poked — <see cref="Capabilities"/> is documented as
    /// valid only after <c>ConnectAsync</c>, and a sound card derives both of its range rungs from
    /// the calibration.</para>
    /// </summary>
    public async Task ReopenAsync(CancellationToken ct)
    {
        if (!deviceFactory.CanSwitch)
            return;

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Acquisition is stopped rather than refused, unlike SwitchDeviceAsync. A reopen is
            // always a deliberate settings change on the analyzer already in use, a *measurement*
            // is already refused by MeasurementRunGuard before the caller gets here, and the live
            // FFT screen cannot carry on across a device being rebuilt anyway. Refusing instead made
            // the sound card's own calibration impossible: it starts acquisition to take its
            // reading and then has to reopen to pick the new full scale up, so it would have been
            // refused by the acquisition it started itself.
            await StopAcquisitionCoreAsync();

            DeviceKind kind = _kind;
            logger.LogInformation("Reopening analyzer {Kind} with its current settings", kind);

            await TearDownDeviceAsync();

            _device = deviceFactory.Create(kind);
            ApplyEffectiveExternalGain();

            try
            {
                await ConnectCoreAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Reopened {Kind} but could not connect ({Reason}) — press Connect once it is available.", kind, ex.Message);
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task SwitchDeviceAsync(DeviceKind kind, CancellationToken ct)
    {
        if (!deviceFactory.CanSwitch)
            throw new InvalidOperationException("This session is pinned to one device and cannot switch.");

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (kind == _kind)
                return;

            if (_isAcquiring)
                throw new InvalidOperationException("Stop the running measurement before switching devices.");

            logger.LogInformation("Switching analyzer: {From} -> {To}", _kind, kind);

            await TearDownDeviceAsync();

            _kind = kind;
            _device = deviceFactory.Create(kind);

            // Before the connect, so the first block the pipeline sees is already scaled by the new
            // analyzer's own front-end rather than the previous one's.
            ApplyEffectiveExternalGain();

            try
            {
                await ConnectCoreAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Same posture as startup: no device is an ordinary state, not a failed request.
                logger.LogWarning(
                    "Switched to {Kind} but could not connect ({Reason}) — press Connect once it is available.",
                    kind, ex.Message);
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Brings the current device all the way down and releases it: the teardown
    /// <see cref="ShutdownAsync"/> performs, plus the handle release the DI container used to do,
    /// plus the session state that belonged to that device rather than to this app.
    /// </summary>
    private async Task TearDownDeviceAsync()
    {
        _isAcquiring = false;
        _sessionRanger = null;
        await spectrumPipeline.StopAsync();
        await StopStreamIfRunningAsync();

        if (_deviceLostHooked)
        {
            _device.DeviceLost -= OnDeviceLost;
            _deviceLostHooked = false;
        }

        (_device as IDisposable)?.Dispose();

        _info = null;
        _deviceLost = false;
        _generatorConfig = GeneratorConfig.Off;
        _requestedGeneratorConfig = GeneratorConfig.Off;

        // Cleared with the rest: it seeds a range on *this* analyzer's ladder, and the one being
        // stood up in its place may not have the same rungs.
        _lastVoicedGeneratorConfig = null;
    }

    /// <summary>
    /// Releases the device. Runs when the container disposes this singleton — which is
    /// <b>after</b> <see cref="DeviceLifecycleHostedService"/> has stopped and therefore after
    /// <see cref="ShutdownAsync"/> has joined the pump, i.e. the exact point at which the container
    /// used to dispose <c>IAnalyzerDevice</c> itself. Moving the ownership here did not move that
    /// ordering, and it must not: releasing the handle with transfers still in flight is what leaves
    /// a QA403 needing a power cycle.
    /// </summary>
    public void Dispose() => (_device as IDisposable)?.Dispose();

    /// <summary>
    /// Guards every operation that has to reach the hardware. The app runs perfectly well with no
    /// device — the UI, the measurement database and every stored result are all still usable — so
    /// "not connected" is an ordinary state a request can arrive in, not an impossible one. Without
    /// this the failure surfaced from three levels down: <c>Qa40xUsbDevice</c>'s own "Device is not
    /// connected" on a register write (a 500 and a dev exception page), or worse, silently — a
    /// <c>POST /acquisition/start</c> answering 200 with <c>acquiring: false</c> while the stream
    /// task faulted on a background thread, which reads to the user as a Run button that does
    /// nothing. <see cref="InvalidOperationException"/> because that is what the endpoints already
    /// map to 409 with the message shown in the status bar.
    /// </summary>
    private void EnsureConnected()
    {
        if (_info is null)
            throw new InvalidOperationException("No device connected. Attach the analyzer and press Connect in the status bar.");
    }

    /// <summary>
    /// Turns "the device vanished while we were talking to it" into the same
    /// <see cref="InvalidOperationException"/> the endpoints already answer with 409. Without it the
    /// unplug surfaced as a raw <c>UsbException: Input/Output Error</c> out of a register write — a
    /// 500 and a stack trace for something the user did on purpose. <see cref="OnDeviceLost"/> has
    /// already run by this point (the device raises it before the exception propagates), so the
    /// session is disconnected and the message is the actionable half.
    /// </summary>
    private static InvalidOperationException DeviceLostError(Qa40xDeviceLostException inner) =>
        new("The QA40x was disconnected. Plug it back in and press Connect in the status bar.", inner);

    /// <summary>
    /// Status, preceded by a throttled liveness check — what <c>GET /api/device</c> serves.
    ///
    /// An unplug is only self-announcing while a stream is running (the transfer pump notices within
    /// one transfer). Idle, nothing touches USB at all, so the status bar went on reporting a
    /// connected QA403 that had been unplugged minutes earlier, and the discovery came instead as a
    /// failed register write behind the user's next click. Since the UI already polls this endpoint
    /// every 2 s, the check rides along with it rather than adding a background timer.
    /// </summary>
    public async Task<DeviceStatusResponse> GetStatusAsync(CancellationToken ct)
    {
        await CheckDeviceAliveAsync(ct).ConfigureAwait(false);
        return GetStatus();
    }

    private async Task CheckDeviceAliveAsync(CancellationToken ct)
    {
        if (_info is null || StreamRunning)
            return;

        long now = Environment.TickCount64;
        if (_lastPingTicks >= 0 && now - _lastPingTicks < LivenessPingIntervalMs)
            return;

        // Zero-timeout: if any other operation holds the session it is already talking to the device,
        // which answers the same question better than a ping would. Never block a status poll on it.
        if (!await _sessionLock.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            // Re-checked under the lock: both could have changed while waiting for it.
            if (_info is null || StreamRunning)
                return;

            _lastPingTicks = Environment.TickCount64;

            // The return value needs no handling: a false answer means the device already raised
            // DeviceLost, and OnDeviceLost has cleared _info by the time this returns — so the
            // GetStatus() that follows reports the disconnect on this very poll.
            await _device.PingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Liveness ping threw; leaving the session connected");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private bool StreamRunning => _streamTask is { IsCompleted: false };

    public DeviceStatusResponse GetStatus()
    {
        var left = ToResponse(_generatorConfig.Left);
        var right = ToResponse(_generatorConfig.Right);

        if (_info is not { } info)
            return new DeviceStatusResponse(
                false, "", "", "", false, 0, 0, false, left, right, SimulatedNoiseEnabled, SimulatedHumEnabled, SimulatedHarmonicsEnabled,
                SimulatedDut?.ToString(), _kind.ToString(),
                _deviceLost, AutoRangeEnabled, AutoRangeStepDownDelayMs, _generatorConfig.SnapToBin, _device.DroppedRxChunks, _device.DroppedBlocks,
                SimulatedCrosstalkEnabled: SimulatedCrosstalkEnabled,
                SimulatedOutputStage: SimulatedOutputStage?.ToString(), SimulatedLoad: SimulatedLoad?.ToString(),
                ExternalGainLeftDb: ExternalGainLeftDb, ExternalGainRightDb: ExternalGainRightDb,
                RangingMode: _rangingMode.ToString(), OutputRangeClipping: OutputRangeClipping,
                OutputClippedDuringAcquisition: OutputClippedDuringAcquisition,
                ExternalGainUnit: ExternalGainUnit,
                SplitInputKind: _kind == DeviceKind.Split ? _split.InputKind.ToString() : null,
                SplitOutputKind: _kind == DeviceKind.Split ? _split.OutputKind.ToString() : null);

        return new DeviceStatusResponse(
            true, info.ProductName, info.SerialNumber, info.FirmwareVersion, info.IsSimulated,
            _device.InputRangeDbv, _outputRangeDbv, Acquiring, left, right, SimulatedNoiseEnabled, SimulatedHumEnabled, SimulatedHarmonicsEnabled,
            SimulatedDut?.ToString(), _kind.ToString(),
            _deviceLost, AutoRangeEnabled, AutoRangeStepDownDelayMs, _generatorConfig.SnapToBin, _device.DroppedRxChunks, _device.DroppedBlocks,
            SimulatedCrosstalkEnabled: SimulatedCrosstalkEnabled,
            SimulatedOutputStage: SimulatedOutputStage?.ToString(), SimulatedLoad: SimulatedLoad?.ToString(),
            ExternalGainLeftDb: ExternalGainLeftDb, ExternalGainRightDb: ExternalGainRightDb,
            RangingMode: _rangingMode.ToString(), OutputRangeClipping: OutputRangeClipping,
            OutputClippedDuringAcquisition: OutputClippedDuringAcquisition,
            Telemetry: ToResponse(_device.Telemetry),
            SoundCardInputDevice: SoundCardField(c => c.InputDeviceName),
            SoundCardOutputDevice: SoundCardField(c => c.OutputDeviceName),
            SoundCardCalibrated: UsesSoundCard
                ? _soundCard.Calibration is { IsInputCalibrated: true, IsOutputCalibrated: true }
                : null,
            ExternalGainUnit: ExternalGainUnit,
            SplitInputKind: _kind == DeviceKind.Split ? _split.InputKind.ToString() : null,
            SplitOutputKind: _kind == DeviceKind.Split ? _split.OutputKind.ToString() : null);
    }

    /// <summary>
    /// Read straight off the device rather than cached here: it is a last reading taken on the
    /// liveness ping, and a second copy in this service could only ever be staler than that one.
    /// </summary>
    private static DeviceTelemetryResponse? ToResponse(DeviceTelemetry? telemetry)
        => telemetry is null
            ? null
            : new DeviceTelemetryResponse(
                telemetry.UsbVoltageV, telemetry.UsbCurrentMa, telemetry.TemperatureC);

    /// <summary>
    /// Runs on whichever thread noticed the disconnect — the stream's pump thread, or the request
    /// thread behind a liveness ping or a register write.
    ///
    /// The two flags are set inline so the very status response being built around this reports the
    /// disconnect. Clearing <see cref="_info"/> (rather than only setting
    /// <see cref="DeviceStatusResponse.DeviceLost"/>, which is all this used to do) is what makes the
    /// session honest about it: status flips to disconnected, every device operation is refused with
    /// a message instead of failing inside a register write, and Connect can bring the replugged
    /// device back — <see cref="Hardware.Qa40xUsbDevice"/> releases the dead handle on its way
    /// through here, so there is nothing left claiming the interface.
    ///
    /// Everything that has to *await* is deferred to <see cref="TearDownAfterLossAsync"/>: this can be
    /// called from inside the stream task's own finally, where awaiting that task would deadlock.
    /// </summary>
    private void OnDeviceLost(object? sender, EventArgs e)
    {
        _deviceLost = true;
        _info = null;
        logger.LogWarning("QA40x disconnected — session marked device-less; press Connect once it is plugged back in");

        int generation = _connectionGeneration;
        _ = Task.Run(() => TearDownAfterLossAsync(generation));
    }

    /// <summary>
    /// Releases what the lost device left running: the spectrum worker (started by
    /// <see cref="StartAcquisitionCoreAsync"/> and otherwise leaked, since a second
    /// <see cref="SpectrumPipeline.Start"/> overwrites its task without cancelling it) and the stream
    /// bookkeeping. The stream task itself needs no cancelling — it ended when the device did — but
    /// its fields have to be cleared or the next run would reuse a completed one.
    /// </summary>
    private async Task TearDownAfterLossAsync(int generation)
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Reconnected while this was queued: that session owns the pipeline now, leave it alone.
            if (generation != _connectionGeneration)
                return;

            _isAcquiring = false;
            await spectrumPipeline.StopAsync().ConfigureAwait(false);
            await StopStreamIfRunningAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Teardown after a device disconnect did not complete cleanly");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Also used by <see cref="MeasurementSaveService.ApplyAsync"/> to describe a measurement type's stored generator config in the same wire shape as live device status.</summary>
    internal static GeneratorChannelResponse ToResponse(ChannelGeneratorConfig config)
    {
        var tones = config.Tones.Select(t => new GeneratorToneResponse(t.FrequencyHz, t.AmplitudeDbv)).ToArray();

        // Fill unset per-kind blocks with the settings records' own defaults rather than sending
        // null: the UI seeds every one of its parameter forms from this response, and a null block
        // would leave whichever kind the user hasn't touched yet with no values to show.
        var multitone = config.Multitone ?? new MultitoneSettings();
        var noise = config.Noise ?? new WhiteNoiseSettings();
        var chirp = config.Chirp ?? new EchoChirpSettings();
        var wave = config.Wave ?? new PeriodicWaveSettings();

        return new GeneratorChannelResponse(
            config.Enabled,
            tones,
            config.Kind,
            new MultitoneResponse(multitone.TonesPerOctave, multitone.TotalRmsDbv, multitone.RolloffDbPerOctave),
            new WhiteNoiseResponse(noise.LevelDbv),
            new EchoChirpResponse(chirp.AmplitudeDbv, chirp.StartFrequencyHz, chirp.EndFrequencyHz, chirp.Smoothing),
            new PeriodicWaveResponse(wave.FrequencyHz, wave.LevelDbv));
    }

    /// <summary>
    /// Whether the device is autoranging its input. Null when the device has no autoranging of its
    /// own, so the UI can hide the checkbox — same null convention as
    /// <see cref="SimulatedNoiseEnabled"/>.
    ///
    /// <para>The device declares that through <see cref="IAnalyzerDevice.HasInputAutoRanging"/>
    /// rather than being recognised by type here. It used to read
    /// <c>_device is SimulatedDevice</c>, which answers a device-shaped question by naming one
    /// device — and a third analyzer with no ranger of its own falls on the wrong side of it.</para>
    /// </summary>
    public bool? AutoRangeEnabled => _device.HasInputAutoRanging ? _device.AutoRangeEnabled : null;

    /// <summary>
    /// Whether a measurement runner should take the input range over for the length of its run.
    /// Two conditions, and they are different questions that coincided until a third analyzer
    /// existed.
    ///
    /// <para>(1) <b>Manual means manual.</b> With the Auto control off the user pinned a range
    /// deliberately, and a measurement quietly reprogramming it would be the same class of surprise
    /// autoranging was before that control existed. Note null — a device with no ranger of its own,
    /// like the simulator — counts as rangeable here, because someone still has to choose.</para>
    ///
    /// <para>(2) <b>A ladder with one rung is not a ladder.</b> A sound card has exactly one input
    /// full scale, so there is nothing to predict, nothing to probe and nothing to ratchet. This is
    /// not merely wasted work: <see cref="FrequencyResponseRunner"/>'s output-voltage mode spends a
    /// whole extra chirp capture in its range probe to conclude that the only range is the only
    /// range, once per run.</para>
    ///
    /// <para>One property rather than the same pair of clauses at eight call sites, so the
    /// invariant has one place to be documented and one place to be tested — and so a ninth runner
    /// cannot be written against the old predicate.</para>
    /// </summary>
    public bool RunnerOwnsInputRange =>
        AutoRangeEnabled != false && Capabilities.InputRangesDbv.Count > 1;

    /// <summary>
    /// Switches who chooses the two analog ranges. Going back to <see cref="RangingMode.Auto"/>
    /// re-derives the output range from whatever the generator is currently playing, so the derived
    /// value is right again the moment the switch is thrown rather than at the next generator
    /// change — a pinned −12 dBV range left behind an 18 dBV tone would otherwise keep clipping
    /// under a status bar that had gone back to saying "Auto". The input range needs no equivalent:
    /// re-enabling the ranger is enough, since it steps to the right one from the next block.
    /// </summary>
    public async Task SetRangingModeAsync(RangingMode mode, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = _rangingMode;
            _rangingMode = mode;
            if (_info is null)
                return;

            ApplyRangingModeToDevice();

            if (mode == RangingMode.Manual)
            {
                if (previous == RangingMode.Auto)
                    await SeedManualOutputRangeAsync(ct);

                return;
            }

            double derived = OutputRangeSelector.Select(
                _generatorConfig, Capabilities.OutputRangesDbv, _lastSampleRateHz);
            if (derived != _outputRangeDbv)
                await SetOutputRangeCoreAsync(derived, ct);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// What the pinned output range starts at when Manual is first selected: the range Auto was
    /// using for the last stimulus that actually played.
    ///
    /// <para>Not simply "whatever Auto has right now", although that is the value the user sees and
    /// the one they mean. Stop disables both generator channels, and Auto then derives the
    /// <i>smallest</i> range, because a silent generator wants the quietest DAC state — correct for
    /// the noise-floor measurement that rule exists for, and useless to pin. Since Manual is almost
    /// always selected while stopped, the dropdown opened on -12 dBV every single time, and the
    /// first Run after it clipped against it.</para>
    ///
    /// <para>The input range needs no equivalent: nothing parks it on a silent generator, so it is
    /// already the range the last measurement ran on.</para>
    /// </summary>
    private async Task SeedManualOutputRangeAsync(CancellationToken ct)
    {
        // A generator that is playing is its own best answer, and already the range in force.
        if (_generatorConfig.Left.Enabled || _generatorConfig.Right.Enabled)
            return;

        // Nothing has ever played: keep what the connect chose (the widest range, maximum headroom),
        // rather than guessing a narrow one for a stimulus nobody has described yet.
        if (_lastVoicedGeneratorConfig is not { } voiced)
            return;

        double seeded = OutputRangeSelector.Select(voiced, Capabilities.OutputRangesDbv, _lastSampleRateHz);
        if (seeded != _outputRangeDbv)
            await SetOutputRangeCoreAsync(seeded, ct);
    }

    /// <summary>
    /// Whether the device may step the input range on its own. No endpoint of its own — the API
    /// expresses this through <see cref="SetRangingModeAsync"/>, because a second control for half
    /// the mode is a second way to reach a state the status bar then has no word for. Public all the
    /// same, like <see cref="SetAutoRangeStepDownEnabled"/>: every runner branches on
    /// <see cref="AutoRangeEnabled"/> to leave a pinned range alone, and their tests set it directly.
    /// </summary>
    public void SetAutoRangeEnabled(bool enabled) => _device.AutoRangeEnabled = enabled;

    /// <summary>
    /// The one place the mode reaches the device's own ranger. A plain property write, no await:
    /// <see cref="IAnalyzerDevice.AutoRangeEnabled"/> is a setting the device reads on its next
    /// block, not a register exchange, so this is safe to call mid-stream where a write on EP1
    /// would not be.
    /// </summary>
    private void ApplyRangingModeToDevice() => SetAutoRangeEnabled(_rangingMode == RangingMode.Auto);

    /// <summary>
    /// How long a peak must stay under-range before hardware autoranging steps to a more sensitive
    /// range, in milliseconds. Null with the same convention as <see cref="AutoRangeEnabled"/> — the
    /// simulator has no hardware ranger to configure.
    /// </summary>
    public double? AutoRangeStepDownDelayMs => _device.HasInputAutoRanging ? _device.AutoRangeStepDownDelayMs : null;

    public void SetAutoRangeStepDownDelayMs(double delayMs) => _device.AutoRangeStepDownDelayMs = delayMs;

    /// <summary>
    /// Whether hardware autoranging may step to a *more sensitive* range on its own. Null with the
    /// same convention as <see cref="AutoRangeEnabled"/>.
    ///
    /// <para>Deliberately not on <see cref="DeviceStatus"/> and not exposed over the API: this is
    /// not a user setting but a scoped suppression <see cref="ThdSweepRunner"/> holds for the length
    /// of a sweep, restoring whatever it found. A checkbox for it would be a second, slower way to
    /// express what the Auto checkbox already covers, and one a sweep would then have to fight.</para>
    /// </summary>
    public bool? AutoRangeStepDownEnabled => _device.HasInputAutoRanging ? _device.AutoRangeStepDownEnabled : null;

    public void SetAutoRangeStepDownEnabled(bool enabled) => _device.AutoRangeStepDownEnabled = enabled;

    public async Task SetInputRangeAsync(double dbv, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SetInputRangeCoreAsync(dbv, ct);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task SetInputRangeCoreAsync(double dbv, CancellationToken ct)
    {
        EnsureConnected();

        await _device.SetInputRangeAsync(dbv, ct);
        _inputRangeDbv = dbv;

        // Whoever just wrote the range — this session's own ranger, a runner, or the status bar —
        // the under-range clock is about the range that was in force, so it restarts here. A quiet
        // patch that straddles a range change must not count towards stepping down from the new one.
        _sessionRanger?.Reset();

        // A range change is a gain step in the analog path — same reasoning as the generator
        // change in ConfigureGeneratorAsync. (An *autorange* step originates inside the device and
        // so cannot be flushed from here; it announces itself in-band instead, on the first block
        // after the step — see PumpAsync and PcmBlock.StartsAfterDiscontinuity.)
        if (_isAcquiring)
            spectrumPipeline.Flush(StimulusLatencyFrames);
    }

    /// <summary>
    /// Frames of pre-change audio still in flight when a stimulus change returns — what every
    /// stimulus-driven <see cref="SpectrumPipeline.Flush"/> has to discard on top of emptying the
    /// buffers. Falls back to the last known sample rate when no stream is running, which only
    /// matters for a flush that is about to be followed by a restart anyway.
    /// </summary>
    private int StimulusLatencyFrames =>
        _device.StimulusLatencyFrames(_streamConfig?.SampleRateHz ?? _lastSampleRateHz);

    /// <summary>
    /// Frames of settle time to discard on top of <see cref="StimulusLatencyFrames"/> — the DUT's
    /// time, not the device's. The latency is audio the DAC had already committed when the change
    /// returned; this is how long the thing being measured needs after the new stimulus finally
    /// reaches it. A tube amplifier's output transformer and coupling caps ring for tens of
    /// milliseconds on a tone step, and with no settle time that ringing sits at the front of the
    /// first averaged record: measured against the simulator's tube-amp model, a −77 dBV shelf below
    /// the tone that reads as DC and vanishes once the record starts 300 ms after the step.
    /// Discarded as frames on the ring buffer rather than awaited, so it is exact and costs nothing
    /// when 0.
    /// </summary>
    private int CaptureDelayFrames =>
        captureDelay.FramesAt(_streamConfig?.SampleRateHz ?? _lastSampleRateHz);

    /// <summary>
    /// The one flush every stimulus change ends with: the in-flight audio, then the settle time.
    /// Only stimulus changes — an input-range step is a gain change in the ADC path that the DUT
    /// never sees, so <see cref="SetInputRangeCoreAsync"/> flushes the latency alone.
    /// </summary>
    private void FlushAfterStimulusChange()
    {
        spectrumPipeline.Flush(StimulusLatencyFrames + CaptureDelayFrames);
        LastAppliedCaptureDelayMs = captureDelay.Ms;
    }

    /// <summary>
    /// Re-applies the last generator request. Needed after an FFT-size or sample-rate change, because
    /// which frequency is coherent depends on both — without this, snapping silently goes stale and
    /// the tone drifts back off-bin the moment the analyzer is reconfigured.
    /// </summary>
    public async Task ReapplyGeneratorAsync(CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReapplyGeneratorCoreAsync(ct);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private Task ReapplyGeneratorCoreAsync(CancellationToken ct) =>
        ConfigureGeneratorCoreAsync(_requestedGeneratorConfig, ct);

    /// <summary>
    /// The one writer of <see cref="SpectrumPipelineOptions.Multitone"/>: the snapped comb of the
    /// first enabled Multitone channel, or null when nothing plays a comb. Called on every configure
    /// and re-snap, so it follows the FFT size and sample rate the way the comb itself does.
    /// </summary>
    private void SetMultitoneSpec(GeneratorConfig snapped)
    {
        ChannelGeneratorConfig? comb = snapped.Left is { Enabled: true, Kind: GeneratorKind.Multitone } ? snapped.Left
            : snapped.Right is { Enabled: true, Kind: GeneratorKind.Multitone } ? snapped.Right
            : null;

        var current = spectrumPipeline.Options;
        if (comb is null)
        {
            if (current.Multitone is not null)
                spectrumPipeline.Options = current with { Multitone = null };
            return;
        }

        double[] freqs = [.. comb.Tones.Select(t => t.FrequencyHz)];
        if (current.Multitone is { } existing && existing.ToneFreqsHz.SequenceEqual(freqs))
            return;

        spectrumPipeline.Options = current with { Multitone = new MultitoneSpec(freqs) };
    }

    /// <summary>
    /// Fills in what a square or triangle channel really plays: its frequency quantised to a whole
    /// even sample period, and the level of its fundamental, as a one-entry effective tone list.
    ///
    /// <para>Here rather than in the device for the reason the multitone comb is built in the
    /// endpoint: this is the one place that knows the sample rate the stimulus will be played at, and
    /// the quantisation depends on it — the same 10 kHz request is 10.105 kHz at 48 kHz and 9.6 kHz
    /// at 192 kHz. Doing it here also means device status, the scope trigger, the Gen tile and the
    /// gain reading all see the line the analyzer will measure without any of them knowing that a
    /// square is not a sine.</para>
    ///
    /// <para>The synthesizers read <see cref="ChannelGeneratorConfig.Wave"/> and ignore the tone
    /// list for these kinds, so this cannot become a second, disagreeing copy of the stimulus; what
    /// it is, is a derived <i>reading</i> of it. Note it rewrites <c>Wave.FrequencyHz</c> as well, so
    /// that the figure the device synthesizes from and the figure status reports are one number.</para>
    /// </summary>
    private GeneratorConfig ResolveWaves(GeneratorConfig config, double sampleRateHz)
    {
        if (sampleRateHz <= 0)
            return config;

        return config with { Left = Resolve(config.Left), Right = Resolve(config.Right) };

        ChannelGeneratorConfig Resolve(ChannelGeneratorConfig channel)
        {
            if (!PeriodicWave.IsWaveKind(channel.Kind) || channel.Wave is not { } wave)
                return channel;

            double effectiveHz = PeriodicWave.EffectiveFrequencyHz(wave.FrequencyHz, sampleRateHz);
            if (effectiveHz <= 0.0)
                return channel with { Tones = [] };

            // With the period, so the level reported is the line the analyzer will really read and
            // not the textbook one -- 0.9 dB apart at the top of the band. See FundamentalDbv.
            double fundamentalDbv = PeriodicWave.FundamentalDbv(
                channel.Kind, wave.LevelDbv, PeriodicWave.PeriodFrames(wave.FrequencyHz, sampleRateHz));

            return channel with
            {
                Wave = wave with { FrequencyHz = effectiveHz },
                Tones = [new GeneratorTone(effectiveHz, fundamentalDbv)],
            };
        }
    }

    /// <summary>
    /// Rewrites every tone frequency to the nearest coherent bin for the current FFT size and sample
    /// rate. No-op when <see cref="GeneratorConfig.SnapToBin"/> is off. Applies to the *effective*
    /// tone list, so a multitone comb gets snapped tone by tone as well.
    /// </summary>
    private GeneratorConfig SnapTones(GeneratorConfig config)
    {
        if (!config.SnapToBin)
            return config;

        int fftSize = spectrumPipeline.Options.FftSize;
        double sampleRateHz = _lastSampleRateHz;
        if (fftSize < 2 || sampleRateHz <= 0)
            return config;

        return config with { Left = SnapChannel(config.Left), Right = SnapChannel(config.Right) };

        ChannelGeneratorConfig SnapChannel(ChannelGeneratorConfig channel)
        {
            // Only the kinds whose tone list *is* the stimulus. Square and Triangle carry their
            // fundamental on that list too (see ResolveWaves), and it is already the real frequency
            // of a quantised period — moving it to the nearest coherent bin would make status report
            // a frequency the DAC is not playing, which is the one thing the effective list exists
            // to prevent. The other kinds have an empty list, so this is also a no-op for them.
            if (channel.Kind is not (GeneratorKind.Tones or GeneratorKind.Multitone))
                return channel;

            if (channel.Tones.Count == 0)
                return channel;

            var snapped = channel.Tones
                .Select(t => t with { FrequencyHz = CoherentFrequency.Nearest(t.FrequencyHz, sampleRateHz, fftSize).FrequencyHz })
                .ToArray();

            return channel with { Tones = snapped };
        }
    }

    public async Task SetOutputRangeAsync(double dbv, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SetOutputRangeCoreAsync(dbv, ct);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task SetOutputRangeCoreAsync(double dbv, CancellationToken ct)
    {
        EnsureConnected();

        await _device.SetOutputRangeAsync(dbv, ct);
        _outputRangeDbv = dbv;
        NoteOutputClipping();
    }

    /// <summary>
    /// Folds the stimulus in force into <see cref="OutputClippedDuringAcquisition"/>, and says so in
    /// the log the first time it happens in an acquisition.
    ///
    /// <para>Called from the two places that can create clipping — a stimulus change and a range
    /// change — rather than computed from a history, so there is one definition of "clipped" and it
    /// is <see cref="OutputRangeClipping"/>'s. The log line is the half that survives being sent in:
    /// a flag on a status response is gone with the process, and this fault is one a user reports
    /// after the fact as "the harmonics look wrong".</para>
    /// </summary>
    private void NoteOutputClipping()
    {
        if (_outputClippedDuringAcquisition || !OutputRangeClipping)
            return;

        _outputClippedDuringAcquisition = true;

        double peakV = Math.Max(
            OutputRangeSelector.RequiredPeakVolts(_generatorConfig.Left, GeneratorSampleRateHz),
            OutputRangeSelector.RequiredPeakVolts(_generatorConfig.Right, GeneratorSampleRateHz));

        logger.LogWarning(
            "Generator clipped: the stimulus needs a {PeakV:0.000} V peak, which is more than the "
            + "{RangeDbv:0.#} dBV output range can deliver ({Mode} ranging). What reaches the DUT is "
            + "clipped, so its distortion is the analyzer's.",
            peakV,
            _outputRangeDbv,
            _rangingMode);
    }

    /// <summary>
    /// <paramref name="streamConfigHint"/> is what to stream at if this call needs to bring the underlying
    /// USB stream up on its own — i.e. a channel just got enabled and no acquisition is currently running.
    /// Pass the UI's current (not-yet-started) Acquisition panel settings here so generator-only output
    /// uses the sample rate the user has actually selected, rather than an arbitrary default. Ignored
    /// while an acquisition is running: that call already owns the stream's configuration, and generator
    /// changes take effect on it automatically (<see cref="Hardware.Qa40xUsbDevice.ChunkPump.BuildTxChunk"/>
    /// reads the live generator config on every chunk).
    /// </summary>
    public async Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct, AcquisitionConfig? streamConfigHint = null)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConfigureGeneratorCoreAsync(config, ct, streamConfigHint);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Builds the periodic-noise table this config asks for, here on the caller's thread, so that
    /// the transfer thread finds it cached.
    ///
    /// <para><b>It is the crest-factor optimisation that makes this necessary.</b> The table used to
    /// be one inverse transform; it is now that plus up to sixteen clip-and-restore passes, which
    /// measures <b>~950 ms at a 524288-sample period</b> against ~30 ms before. The generator
    /// synthesizers call <see cref="PeriodicNoise.For"/> per frame and would pay that on the first
    /// frame after a stimulus change — on the transfer thread, against a TX queue holding 128 ms.
    /// That is a DAC starvation gap by arithmetic, and the impedance measurement is the one that
    /// would hit it, since unlike the response runners it never builds a reference of its own and
    /// so has nothing else that touches the table first.</para>
    ///
    /// <para>Never throws: a sequence that cannot be built is one the synthesizer will refuse in its
    /// own way, and a stimulus change is not the place to find out.</para>
    /// </summary>
    private static void WarmPeriodicNoise(GeneratorConfig config, double sampleRateHz)
    {
        if (!(sampleRateHz > 0.0)) return;

        foreach (var channel in new[] { config.Left, config.Right })
        {
            if (!channel.Enabled || channel.Kind != GeneratorKind.PinkNoise) continue;

            var noise = channel.Noise;
            if (noise is null || noise.PeriodFrames <= 1) continue;

            try
            {
                PeriodicNoise.For(noise.PeriodFrames, sampleRateHz, noise.CutoffHz, noise.Shape);
            }
            catch (ArgumentException)
            {
                // A period or a rate the sequence refuses. Left to the synthesizer, which is where
                // the decision about what to play with it belongs.
            }
        }
    }

    private async Task ConfigureGeneratorCoreAsync(GeneratorConfig config, CancellationToken ct, AcquisitionConfig? streamConfigHint = null)
    {
        _lastSampleRateHz = streamConfigHint?.SampleRateHz ?? _streamConfig?.SampleRateHz ?? _lastSampleRateHz;

        // The rate the *generator* runs at, which on everything but a composite is the rate being
        // captured. Taken once, here, because the five readers below are all generator quantities
        // and reading the analysis rate for any of them is wrong by the rate ratio -- see
        // GeneratorSampleRateHz.
        double generatorRateHz = _device.GeneratorSampleRateHz ?? _lastSampleRateHz;

        // Before anything is cached or snapped: a rate whose DAC does not work must refuse the
        // stimulus rather than play a mangled one. See RequireGeneratorRateUsable.
        RequireGeneratorRateUsable(config, generatorRateHz, _lastSampleRateHz);

        _requestedGeneratorConfig = config;

        if (config.Left.Enabled || config.Right.Enabled)
            _lastVoicedGeneratorConfig = config;

        // Before SnapTones, which leaves these kinds alone precisely because this has already put
        // the real frequency on their tone list.
        config = ResolveWaves(config, generatorRateHz);

        // SnapTones keeps the *analysis* rate, and the two lines being adjacent is the point: a
        // square's period is a count of the generator's frames, while a tone is snapped to an
        // analysis bin. That second one is why tones survive two clocks at all, and swapping either
        // for the other is silent -- one reports a frequency the DAC is not playing, the other
        // leaks a tone across its neighbours.
        config = SnapTones(config);
        SetMultitoneSpec(config);
        WarmPeriodicNoise(config, generatorRateHz);

        // Silencing a generator there is no device to silence is a success, not a 409: deviceControl's
        // stopRun (Stop, and every measurement switch) disables both channels unconditionally, and
        // with no device attached that has nothing to do. Asking for *output* without one is the
        // error case, and only that one is worth telling the user about.
        if (_info is null)
        {
            if (config.Left.Enabled || config.Right.Enabled)
                EnsureConnected();

            _generatorConfig = config;
            return;
        }

        // The output range is derived from what the generator is about to play — see
        // OutputRangeSelector. Ordering matters: program a *larger* range before the new config
        // reaches the DAC (otherwise the louder signal spends a chunk clipping against the old
        // smaller full scale), and a *smaller* one after (so the old louder signal isn't the thing
        // being squeezed into it).
        //
        // Under Manual ranging the range is the user's and this derivation is the whole thing being
        // switched off, so nextOutputRangeDbv is the range already programmed and both branches
        // below are no-ops. A level that overruns it is reported on the status (OutputRangeClipping)
        // rather than quietly corrected: correcting it would make "Manual" mean "manual until the
        // app disagrees".
        double nextOutputRangeDbv = _rangingMode == RangingMode.Auto
            ? OutputRangeSelector.Select(config, Capabilities.OutputRangesDbv, generatorRateHz)
            : _outputRangeDbv;
        bool widening = nextOutputRangeDbv > _outputRangeDbv;

        if (widening && nextOutputRangeDbv != _outputRangeDbv)
            await SetOutputRangeCoreAsync(nextOutputRangeDbv, ct);

        await _device.ConfigureGeneratorAsync(config, ct);
        _generatorConfig = config;
        NoteOutputClipping();

        if (!widening && nextOutputRangeDbv != _outputRangeDbv)
            await SetOutputRangeCoreAsync(nextOutputRangeDbv, ct);

        if (_isAcquiring)
        {
            // Every sample already buffered predates this stimulus, so an FFT record spanning the
            // change would show the step as a broad skirt around the tone rather than the real
            // noise floor. And so does everything still queued for the DAC at this instant, which is
            // why this passes the stimulus latency rather than flushing bare — plus the configured
            // settle time, for the DUT's own response to the step. See SpectrumPipeline.Flush and
            // CaptureDelayFrames.
            FlushAfterStimulusChange();

            // A recording's two files cannot say *why* the stimulus changed in the middle of them,
            // and a sweep changes it per point -- so the sidecar carries the configuration each
            // time, which is what makes a stale-stimulus bug visible as a disagreement between the
            // event and the samples after it.
            streamRecorder?.Note(
                $"stimulus now L: {DiagnosticRecorder.Describe(config.Left)}, R: {DiagnosticRecorder.Describe(config.Right)}");
            return;
        }

        bool anyChannelEnabled = config.Left.Enabled || config.Right.Enabled;
        if (anyChannelEnabled && streamConfigHint is { } hint)
            _ = await EnsureStreamRunningAsync(hint, ct);
        else if (!anyChannelEnabled)
            await StopStreamIfRunningAsync();
    }

    public async Task StartAcquisitionAsync(AcquisitionConfig config, CancellationToken ct)
    {
        if (config.SampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "SampleRateHz must be positive.");
        if (config.ChannelCount < 1 || config.ChannelCount > Capabilities.MaxChannelCount)
            throw new ArgumentOutOfRangeException(nameof(config), $"ChannelCount must be between 1 and {Capabilities.MaxChannelCount}.");
        if (config.BlockFrameCount < MinBlockFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                $"BlockFrameCount must be at least {MinBlockFrameCount} (the real QA40x's hardware queue depth, "
                    + "per docs/tonebench-stack-spec.md §2.1) — smaller values turn the acquisition loop into a "
                    + "near-tight spin at the simulated sample rate and can starve the app under load.");
        }

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartAcquisitionCoreAsync(config, ct);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task StartAcquisitionCoreAsync(AcquisitionConfig config, CancellationToken ct)
    {
        EnsureConnected();
        RequireSupportedSampleRate(config.SampleRateHz);

        // A config change (sample rate, channel count, block size) while already acquiring restarts
        // the stream in place rather than making the caller Stop then Start — the UI applies these
        // changes live, with no explicit stop/restart step of its own.
        double previousSampleRateHz = _streamConfig?.SampleRateHz ?? _lastSampleRateHz;
        if (_isAcquiring)
            await StopAcquisitionCoreAsync();

        // Reuses an already-running generator-only stream if its config matches, otherwise opens one
        // at this config. It does not wait for the open — see its remarks — which is why everything
        // below books discards against frames that have yet to arrive rather than against the clock.
        // Before the stream is brought up, so nothing it plays is missed -- see BeginStreamRecording.
        BeginStreamRecording(config);

        bool streamOpened = await EnsureStreamRunningAsync(config, ct);
        spectrumPipeline.Start(config.SampleRateHz, config.ChannelCount);

        // Built per acquisition rather than per device, so it is given this device's ladder and
        // starts its under-range clock with the run. Null on a device that ranges inside itself:
        // two rangers on one range is the hunting this was written to end, not twice the coverage.
        _sessionRanger = _device.HasInputAutoRanging
            ? null
            : new SessionInputRanger(Capabilities.InputRangesDbv) { StepDownDelayMs = _stepDownDelay.Ms };

        _isAcquiring = true;

        // Per acquisition, like the device's two drop counters: a stale "it clipped" from a run
        // twenty minutes ago reads exactly like one that just happened. Re-evaluated rather than
        // merely cleared, because the FFT screen's Run configures its generator *before* it
        // acquires, so a clipping stimulus is already in force by the time we reach here.
        _outputClippedDuringAcquisition = false;
        NoteOutputClipping();

        // **The device's own stream-start settling is not booked here any more, and must not be.**
        // It was, as a Flush at the end of this method, "so nothing above can overwrite it" -- and
        // nothing in this method did. What overwrote it was the *runner*, one level up: the first
        // tone it configures calls ConfigureGeneratorCoreAsync, whose own Flush replaces the
        // pending discard rather than extending it, so a 3 s settle became the 300 ms capture
        // delay and the card's re-clock transient landed in the measurement. Measured on a MOTU M4
        // in loopback, 100 % reproducible, and worst where it is least visible: a frequency-
        // response trace spread over 91 dB with coherence 0.17, against 0.51 dB and 1.0000 on the
        // next identical run.
        //
        // A discard that any later flush can shorten cannot express "these frames are unusable",
        // so the settle now lives in the device, which simply does not hand those frames out --
        // see SoundCardDevice.Pump. Booking it here as well would discard it twice.

        // The FFT screen's Run: the generator was configured while nothing was acquiring, so
        // ConfigureGeneratorCoreAsync had no window to flush and the settle time has been applied
        // to nothing. The stimulus reached the DUT no later than this stream started (a
        // generator-only stream brought up by that configure, or the one just started), so counting
        // the settle from here is conservative. No stimulus latency on top: nothing changed on the
        // wire, and Start() already emptied the window.
        if (_generatorConfig.Left.Enabled || _generatorConfig.Right.Enabled)
        {
            if (CaptureDelayFrames > 0)
                spectrumPipeline.Flush(CaptureDelayFrames);
            LastAppliedCaptureDelayMs = captureDelay.Ms;
        }

        // Which frequency is coherent depends on the sample rate, so a rate change has to re-snap the
        // generator or its tones silently drift off-bin at the new rate.
        if (config.SampleRateHz != previousSampleRateHz)
            await ReapplyGeneratorCoreAsync(ct);

        // The device's settle is frames it will not deliver, so a runner waiting for its first
        // record has to be told to wait that much longer -- otherwise a short-record, few-average
        // sweep declares the card wedged. Reported in milliseconds, like CaptureDelayMs, and for
        // the same reason.
        StreamSettleMs = streamOpened
            ? _device.StreamStartSettleFrames(config.SampleRateHz) * 1000.0 / config.SampleRateHz
            : 0.0;

        // **What this acquisition is actually measuring with, in one line.** SpectrumPipeline.Start
        // already logs the analysis side (record length, window, averaging, weighting) and every
        // sweep runner logs its own request -- but nothing logged the *stimulus* or the two
        // attenuators, and those are the settings most of this app's faults turn out to be about: a
        // range boundary that moved the noise floor, a generator at a level nobody meant, an
        // external gain left on from a fixture that is no longer wired up. The live FFT screen has
        // no runner to log for it, so a fault reported from that screen had no record of its
        // settings at all.
        //
        // Here rather than in ConfigureGeneratorCoreAsync, although that is the one place every
        // stimulus change passes through: a sweep passes through it once per point, which would be
        // hundreds of lines a run. An acquisition start is once per RUN, and by this point the
        // stimulus is already in _generatorConfig -- the FFT screen configures the generator before
        // acquiring, which is the same ordering the capture-delay flush above relies on.
        logger.LogInformation(
            "Acquisition starting at {SampleRateHz:0} Hz, {Channels} ch: stimulus L {Left}, R {Right}; "
                + "input range {InputDbv:0.#} dBV ({Ranging}), output range {OutputDbv:0.#} dBV, "
                + "external gain {GainLeft:0.##}/{GainRight:0.##} dB, capture delay {CaptureDelayMs:0} ms, "
                + "stream settle {SettleMs:0} ms",
            config.SampleRateHz, config.ChannelCount,
            DescribeStimulus(_generatorConfig.Left), DescribeStimulus(_generatorConfig.Right),
            InputRangeDbv,
            _device.HasInputAutoRanging ? (AutoRangeEnabled == true ? "auto" : "manual") : "fixed",
            OutputRangeDbv,
            _externalGain.LeftDb, _externalGain.RightDb,
            captureDelay.Ms, StreamSettleMs);
    }

    /// <summary>
    /// One channel's stimulus as a line of text, for the acquisition-start log.
    ///
    /// <para>Every kind names the figures that would make a measurement wrong if they were not what
    /// the user believed, and nothing else: a tone's frequency and level, a comb's density and
    /// total, a sweep's span and period. The tone list is capped because a multitone comb can be
    /// sixty tones and the useful part of it is the first few plus the count — the comb's own
    /// parameters above it say the rest.</para>
    /// </summary>
    private static string DescribeStimulus(ChannelGeneratorConfig channel)
    {
        if (!channel.Enabled || channel.Kind == GeneratorKind.Off)
            return "off";

        switch (channel.Kind)
        {
            case GeneratorKind.Multitone when channel.Multitone is { } comb:
                return $"multitone {comb.TonesPerOctave}/octave at {comb.TotalRmsDbv:0.##} dBV total, "
                    + $"rolloff {comb.RolloffDbPerOctave:0.##} dB/octave ({channel.Tones.Count} tones)";

            case GeneratorKind.WhiteNoise when channel.Noise is { } noise:
                return $"white noise at {noise.LevelDbv:0.##} dBV rms";

            case GeneratorKind.PinkNoise when channel.Noise is { } noise:
                return $"pink noise at {noise.LevelDbv:0.##} dBV rms";

            case GeneratorKind.Square or GeneratorKind.Triangle when channel.Wave is { } wave:
                // The played frequency, not the requested one (ResolveWaves has already rewritten
                // it), and the peak beside the level: those two differ by shape, and a log that
                // quoted only the rms could not say what the DUT was actually driven to.
                return $"{channel.Kind.ToString().ToLowerInvariant()} wave at {wave.FrequencyHz:0.####} Hz, "
                    + $"{wave.LevelDbv:0.##} dBV rms "
                    + $"({PeriodicWave.PeakVolts(channel.Kind, wave.LevelDbv):0.####} V peak)";

            case GeneratorKind.EchoChirp when channel.Chirp is { } chirp:
                return $"chirp {chirp.StartFrequencyHz:0.##}-{chirp.EndFrequencyHz:0.##} Hz at "
                    + $"{chirp.AmplitudeDbv:0.##} dBV, period {chirp.RepeatFrames} frames, smoothing {chirp.Smoothing}";

            default:
                if (channel.Tones.Count == 0)
                    return $"{channel.Kind} with no tones";

                const int named = 4;
                string tones = string.Join(
                    " + ",
                    channel.Tones.Take(named).Select(t => $"{t.FrequencyHz:0.####} Hz at {t.AmplitudeDbv:0.##} dBV"));

                return channel.Tones.Count > named
                    ? $"{tones} (+{channel.Tones.Count - named} more)"
                    : tones;
        }
    }

    /// <summary>
    /// How long the device will withhold samples at the start of this acquisition while its
    /// hardware settles — <see cref="IAnalyzerDevice.StreamStartSettleFrames"/> as a time. Zero for
    /// everything but a sound card that has just been re-clocked.
    ///
    /// <para>Every runner's per-capture timeout carries this the way it carries
    /// <see cref="CaptureDelayMs"/>: the frames are genuinely not coming, so a budget that does not
    /// allow for them reads a settling card as a stopped one. At 8192 points and one average the
    /// budget is 3.3 s and the settle is 3 s, so this is not a margin — it is the difference
    /// between a measurement and a TimeoutException.</para>
    /// </summary>
    public double StreamSettleMs { get; private set; }

    /// <summary>Starts (or restarts, if already running at a different config) the underlying device stream.</summary>
    /// <summary>
    /// Brings up a stream for this config, and reports whether it actually <b>opened</b> one —
    /// false when a running stream already matched and was reused.
    ///
    /// <para>The caller needs that distinction: a device's stream-start settle applies only to a
    /// real open. It is also exactly why STOP then RUN at the same rate is instant and clean while
    /// a rate change is neither.</para>
    ///
    /// <para>Note the stream is <i>started</i>, not yet open, when this returns: the pump is handed
    /// to the thread pool below and the device opens on that thread. Nothing here waits for it,
    /// which is safe because every flush books its discard against frames that have yet to arrive.
    /// </para>
    /// </summary>
    private async Task<bool> EnsureStreamRunningAsync(AcquisitionConfig config, CancellationToken ct)
    {
        lock (_streamLock)
        {
            if (_streamTask is { IsCompleted: false } && _streamConfig is { } current && current.Equals(config))
                return false;
        }

        await StopStreamIfRunningAsync();

        var cts = new CancellationTokenSource();

        // Capture the token by value into a local, not the field, before handing it to Task.Run: the
        // lambda doesn't actually run until the thread pool gets to it, and if StopStreamIfRunningAsync
        // races in first and nulls _streamCts, reading the field lazily inside the lambda throws a
        // NullReferenceException instead of observing an already-cancelled token like it should.
        var pumpCt = cts.Token;
        lock (_streamLock)
        {
            _streamCts = cts;
            _streamConfig = config;
            _streamTask = Task.Run(() => PumpAsync(config, pumpCt), CancellationToken.None);
        }

        return true;
    }

    /// <summary>
    /// Pumps <see cref="IAnalyzerDevice.StreamAsync"/> for as long as either acquisition or the generator
    /// needs the stream up. Every block is drained regardless — the device requires a continuous reader on
    /// EP2 to keep the DAC clocking TX at all — but is only forwarded to <see cref="spectrumPipeline"/>
    /// while <see cref="_isAcquiring"/> is set; otherwise it's just disposed (returns its rented buffer).
    /// </summary>
    private async Task PumpAsync(AcquisitionConfig config, CancellationToken ct)
    {
        try
        {
            await foreach (var block in _device.StreamAsync(config, ct).WithCancellation(ct))
            {
                if (_isAcquiring)
                {
                    // Autoranging steps the input range inside the device, and the quiesce that does
                    // it discards ~a queue depth of already-captured RX on top of the relay settle
                    // frames — a hole of order 110 ms that DroppedRxChunks does not count and the
                    // block sequence numbers do not show. Flushing here is the same reasoning as
                    // SetInputRangeCoreAsync's flush for a manual range change; the difference is
                    // that the news arrives on the block rather than from the caller.
                    //
                    // No stimulus-latency discard of its own: nothing changed on the way *out*, so
                    // there is no in-flight DAC audio to outrun. But it must not *cancel* one
                    // either, which a bare Flush() did — this news is late by a block, and a sweep
                    // runner that programmed the range and then its next tone has already booked a
                    // discard by the time it lands. See SpectrumPipeline.FlushAfterRangeStep and
                    // PcmBlock.StartsAfterDiscontinuity.
                    if (block.StartsAfterDiscontinuity)
                    {
                        // Information, not Debug, and logged here rather than at the step itself:
                        // this is the one event that makes an otherwise inexplicable one-record
                        // noise-floor jump attributable, and the pump thread is the wrong place to
                        // do file I/O. Repeated lines roughly a second apart mean the ranger is
                        // hunting at a threshold rather than tracking a real level change.
                        //
                        // **The flag says the stream broke, not why**, and only an autoranging
                        // device may be told it was the ranger. A sound card has one fixed gain and
                        // no ranger at all (HasInputAutoRanging false, SetInputRangeAsync only
                        // validates), so on that device this line named the single cause it could
                        // not be -- and a bench session spent on "why is it autoranging" before the
                        // real cause, a glitching ASIO duplex stream, was even looked at.
                        // SoundCardDevice.Pump names the actual cause on its own line.
                        if (_device.HasInputAutoRanging)
                        {
                            logger.LogInformation(
                                "Input range stepped to {RangeDbv} dBV by autoranging — flushing the "
                                    + "analysis window, since the step discarded the RX transfers in flight",
                                _device.InputRangeDbv);
                        }
                        else
                        {
                            logger.LogInformation(
                                "Discontinuity reported on the sample stream — flushing the analysis window, "
                                    + "since the record spanning it would read as a smeared tone on a lifted floor");
                        }

                        spectrumPipeline.FlushAfterRangeStep();
                    }

                    // The ranger for a device that has none of its own, fed the same way the
                    // hardware one is: block by block, off the peak the block carries. Before the
                    // enqueue, for the same reason the recorder is — the pipeline disposes it.
                    await MaybeStepInputRangeAsync(block, ct).ConfigureAwait(false);

                    // Before the enqueue, which takes ownership and disposes the block: the
                    // recorder copies what it needs and keeps nothing.
                    streamRecorder?.WriteInput(block);

                    spectrumPipeline.Enqueue(block); // takes ownership — disposes the block itself
                }
                else
                {
                    block.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Device stream faulted");
        }
    }

    /// <summary>
    /// Runs one block past <see cref="SessionInputRanger"/> and writes the range it asks for.
    /// Nothing happens at all on a device that ranges itself, with the ranges pinned to Manual, or
    /// on a ladder with one rung — and in the ordinary case the whole of this is a scan of the
    /// block and a comparison.
    ///
    /// <para>Three things about the write. It goes through <see cref="SetInputRangeCoreAsync"/>, so
    /// a range change made here flushes the analysis window exactly like one made by hand — the
    /// record spanning a gain step is not a measurement of anything. It takes the session lock with
    /// a <b>zero timeout</b>, like the liveness ping: this is the consumer thread, and a block not
    /// taken is a dropped block, so a range step that collides with a request simply waits for the
    /// next block. And it can only ever reach a device with no ranger of its own, which is why
    /// writing a register from here is safe — on a QA403 this is exactly the mid-stream EP1 write
    /// that wedges the device, and that is the one device this never runs on.</para>
    /// </summary>
    private async ValueTask MaybeStepInputRangeAsync(PcmBlock block, CancellationToken ct)
    {
        if (_sessionRanger is not { } ranger || _rangingMode != RangingMode.Auto)
            return;

        var options = spectrumPipeline.Options;
        double peakDbv = SessionInputRanger.BlockPeakDbv(
            block.Samples, block.ChannelCount, options.ProcessLeft, options.ProcessRight);

        ranger.StepDownDelayMs = _stepDownDelay.Ms;
        double blockDurationMs = block.SampleRateHz > 0.0 ? block.FrameCount * 1000.0 / block.SampleRateHz : 0.0;

        // Every runner sets SingleShot and ranges for itself; the live FFT screen's free-running
        // acquisition is the only consumer that does not. See the parameter's own remarks.
        if (ranger.Evaluate(_device.InputRangeDbv, peakDbv, blockDurationMs, stepDownAllowed: !options.SingleShot)
            is not { } targetDbv)
            return;

        if (!await _sessionLock.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            // Re-checked under the lock: a stop, a switch, a pinned range or someone else's write
            // could all have landed while this block was being scanned.
            if (_info is null || !_isAcquiring || _rangingMode != RangingMode.Auto
                || Math.Abs(_device.InputRangeDbv - targetDbv) < 1e-9)
            {
                return;
            }

            await SetInputRangeCoreAsync(targetDbv, ct).ConfigureAwait(false);

            // Information, like the hardware ranger's own step line, and for the same reason: it is
            // what makes a one-record noise-floor jump attributable afterwards. Repeated lines about
            // a second apart mean the ranger is hunting at a threshold rather than tracking a level.
            logger.LogInformation(
                "Input range stepped to {RangeDbv} dBV for a measured peak of {PeakDbv:0.00} dBV — "
                    + "this analyzer has no ranger of its own, so the session chose it",
                targetDbv, peakDbv);
        }
        catch (Exception ex)
        {
            // A range the device refuses, or one lost under us: say so once and stop ranging for
            // this acquisition rather than asking again on every block. The measurement continues
            // on the range it is on, which is the same place a device with no ranger starts from.
            _sessionRanger = null;
            logger.LogWarning(ex, "Input ranging stopped for this acquisition: the range write failed");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task StopStreamIfRunningAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_streamLock)
        {
            cts = _streamCts;
            task = _streamTask;
            _streamCts = null;
            _streamTask = null;
            _streamConfig = null;
        }

        if (cts is null)
            return;

        cts.Cancel();
        await _device.StopAsync();
        if (task is not null)
            await task;
        cts.Dispose();
    }

    public async Task StopAcquisitionAsync()
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAcquisitionCoreAsync();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task StopAcquisitionCoreAsync()
    {
        if (!_isAcquiring)
            return;

        _isAcquiring = false;
        _sessionRanger = null;
        await spectrumPipeline.StopAsync();
        await EndStreamRecordingAsync();

        // The generator may still need the stream even though acquisition is done with it.
        bool generatorActive = _generatorConfig.Left.Enabled || _generatorConfig.Right.Enabled;
        if (!generatorActive)
            await StopStreamIfRunningAsync();
    }

    /// <summary>
    /// Host-shutdown teardown. Deliberately *not* <see cref="StopAcquisitionAsync"/>, which was what
    /// the lifecycle hosted service used to call and which tears down nothing at all in the two
    /// states the app is usually left in: it returns immediately when acquisition was never started,
    /// and it deliberately keeps the stream up when the generator is enabled. Either way the transfer
    /// pump was still running when DI went on to dispose the device, so <c>TransferPump</c>'s
    /// <c>finally</c> — the only thing that writes <see cref="Hardware.Qa40xRegisters.StreamingStop"/>
    /// and collects the in-flight EP2 transfers — never ran before <c>libusb_close</c>. That leaves
    /// the device streaming with nobody reading it, which is what made the QA403 unreachable to every
    /// app until it was power-cycled.
    ///
    /// Unconditional and ordered: stop forwarding, stop the FFT worker, then bring the stream down
    /// and wait for the pump to actually join. Only after this returns is it safe for the DI
    /// container to dispose <see cref="IAnalyzerDevice"/> (release interface, close handle).
    /// </summary>
    public async Task ShutdownAsync()
    {
        // Logged step by step because the failure this guards against is a *hang*, not an exception:
        // whichever line has no matching "done" in the log is the one that blocked.
        logger.LogInformation("QA40x teardown: shutdown begin (acquiring={Acquiring}, stream={StreamRunning})",
            _isAcquiring, _streamTask is { IsCompleted: false });

        _isAcquiring = false;
        await spectrumPipeline.StopAsync();
        logger.LogInformation("QA40x teardown: spectrum pipeline stopped");

        // Before the stream comes down, so the file is closed and its header patched while the
        // writer still has time -- a recording abandoned by the shutdown watchdog is a file whose
        // length nothing ever wrote.
        await EndStreamRecordingAsync();

        await StopStreamIfRunningAsync();
        logger.LogInformation("QA40x teardown: device stream stopped and pump joined");
    }
}
