using Tonebench.Device;
using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;
using Tonebench.Device.SoundCard;
using Tonebench.Device.Split;

namespace Tonebench.App.Services;

/// <summary>Which analyzer the session is talking to.</summary>
public enum DeviceKind
{
    /// <summary>A real QA403 over USB.</summary>
    Hardware = 0,

    /// <summary>The synthetic device — see <see cref="SimulatedDevice"/> and <see cref="SimulatedDutModel"/>.</summary>
    Simulated = 1,

    /// <summary>
    /// A sound card with an analog front-end, over PortAudio — see <see cref="SoundCardDevice"/>.
    /// Which card, and where its full scale sits, is <c>SoundCardConfig</c>.
    /// </summary>
    SoundCard = 2,

    /// <summary>
    /// Two devices as one analyzer: one supplies the DAC, the other the ADC — see
    /// <see cref="SplitAnalyzerDevice"/>. Which two, and the rate the generator runs at, is
    /// <c>SplitDeviceConfig</c>.
    ///
    /// <para>The benches it exists for are a USB DAC playing while a QA403 captures, and a QA403
    /// playing into a capture-only ADC such as an E1DA Cosmos. Two devices are two clocks, which the
    /// clock-drift correction and the two-clocks wording answer; it is not a quality step over
    /// either device alone and must not be presented as one.</para>
    /// </summary>
    Split = 3,
}

/// <summary>
/// Makes analyzer devices on demand, so which one the app is using is a runtime choice rather than a
/// command-line one.
///
/// <para><b>Why this exists at all.</b> <c>IAnalyzerDevice</c> used to be a DI singleton picked by
/// <c>--simulate</c> at startup, which meant comparing a measurement against the simulator — or
/// demonstrating anything with no QA403 to hand — took a restart. Nothing else in the app depended
/// on that: <see cref="DeviceSessionService"/> is the only consumer of the interface, so making the
/// device swappable is a change to one field and its teardown, not to the shape of the app.</para>
///
/// <para><b>The container no longer owns the device's lifetime.</b> A singleton it cannot replace is
/// also a singleton it must not dispose out from under a swap, so <see cref="DeviceSessionService"/>
/// creates and disposes every device itself — see its <c>IDisposable</c> implementation, which runs
/// at exactly the point the container used to dispose the device.</para>
/// </summary>
/// <remarks>
/// Not sealed, and <see cref="Create"/> is virtual, for one reason: a test must never construct a
/// <see cref="Qa40xUsbDevice"/>. Doing so on a machine that happens to have a QA403 attached would
/// claim the interface out from under whatever else is using it and turn an ordinary unit test into
/// a hardware-dependent one — the thing <c>[Trait("Category","Hardware")]</c> exists to keep
/// separate. Overriding this is how the switching tests get two fake analyzers.
/// </remarks>
public class AnalyzerDeviceFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyzerDevice? _pinned;
    private readonly SoundCardSetting _soundCard;
    private readonly Experimental384kSetting _experimental384k;
    private readonly AutoRangeStepDownDelaySetting _stepDownDelay;
    private readonly SplitDeviceSetting _split;

    public AnalyzerDeviceFactory(
        ILoggerFactory loggerFactory,
        DeviceKind initialKind,
        SoundCardSetting? soundCard = null,
        Experimental384kSetting? experimental384k = null,
        AutoRangeStepDownDelaySetting? stepDownDelay = null,
        SplitDeviceSetting? split = null)
    {
        _loggerFactory = loggerFactory;
        _soundCard = soundCard ?? new SoundCardSetting();
        _experimental384k = experimental384k ?? new Experimental384kSetting();
        _stepDownDelay = stepDownDelay ?? new AutoRangeStepDownDelaySetting();
        _split = split ?? new SplitDeviceSetting();
        InitialKind = initialKind;
    }

    private AnalyzerDeviceFactory(IAnalyzerDevice pinned)
    {
        _loggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _soundCard = new SoundCardSetting();
        _experimental384k = new Experimental384kSetting();
        _stepDownDelay = new AutoRangeStepDownDelaySetting();
        _split = new SplitDeviceSetting();
        _pinned = pinned;

        // A four-way test, not "simulator or else": a pinned sound card reported itself as
        // Hardware while the status bar showed a QA403 that was not there, and a composite would do
        // the same — it is neither a SimulatedDevice nor a SoundCardDevice, so it would fall through
        // to Hardware and a two-device bench would be described as one QA403.
        InitialKind = pinned switch
        {
            SimulatedDevice => DeviceKind.Simulated,
            SoundCardDevice => DeviceKind.SoundCard,
            SplitAnalyzerDevice => DeviceKind.Split,
            _ => DeviceKind.Hardware,
        };
    }

    /// <summary>Which kind the app starts on — <c>--simulate</c>'s only remaining job.</summary>
    public DeviceKind InitialKind { get; }

    /// <summary>
    /// False for a factory built around one already-constructed device. Tests hand
    /// <see cref="DeviceSessionService"/> a device directly rather than a kind to build, and a
    /// session built that way has nothing to switch to — see <see cref="Pinned"/>.
    /// </summary>
    public bool CanSwitch => _pinned is null;

    /// <summary>
    /// A factory that always yields the device it was given. For tests and for
    /// <see cref="DeviceSessionService"/>'s device-taking constructor; never registered in DI.
    /// </summary>
    public static AnalyzerDeviceFactory Pinned(IAnalyzerDevice device) => new(device);

    /// <summary>
    /// The device for <paramref name="kind"/>, with the settings that have to be <i>written</i> into
    /// it applied — see <see cref="ApplyStoredSettings"/>.
    /// </summary>
    public virtual IAnalyzerDevice Create(DeviceKind kind) =>
        // A pinned device belongs to its test, which may have set the very fields below; the
        // factory's job there is to hand back what it was given, unaltered.
        _pinned ?? ApplyStoredSettings(Build(kind));

    /// <summary>
    /// The settings a device cannot read for itself.
    ///
    /// <para><c>Allow384kHz</c> is a <c>Func</c> and needs nothing here. The step-down delay is a
    /// plain field on the device's ranger, so it has to be pushed — and pushed on every device, not
    /// just the QA403: the property is "ignored but harmlessly stored" on an analyzer with no ranger
    /// (<c>IAnalyzerDevice.AutoRangeStepDownDelayMs</c>), and a device that is handed it anyway
    /// cannot forget it if one ever grows one.</para>
    /// </summary>
    private IAnalyzerDevice ApplyStoredSettings(IAnalyzerDevice device)
    {
        device.AutoRangeStepDownDelayMs = _stepDownDelay.Ms;
        return device;
    }

    private IAnalyzerDevice Build(DeviceKind kind) =>
        kind switch
        {
            DeviceKind.Simulated => CreateSimulator(StoredAllow384kHz),

            // Built from the settings as they stand right now rather than from a captured copy: a
            // card chosen or calibrated since the last switch must be the one that opens, and the
            // session reconnects through this factory after every change for exactly that reason.
            DeviceKind.SoundCard => CreateSoundCard(_soundCard.DeviceSettings, ownStreamThread: false),

            DeviceKind.Split => BuildSplit(),

            _ => CreateQa40x(StoredAllow384kHz),
        };

    /// <summary>
    /// The experimental 384 kHz opt-in, as the configuration document holds it.
    ///
    /// <para>A <c>Func</c> and not a captured value, so unlike the sound card's settings it is NOT
    /// read here: the checkbox takes effect on an already-built device, and a device built before
    /// the configuration database was read still reports the right rates. See
    /// <c>Qa40xUsbDevice.Allow384kHz</c>.</para>
    /// </summary>
    private Func<bool> StoredAllow384kHz => () => _experimental384k.Enabled;

    /// <summary>
    /// A QA40x, with the rate gate the caller decides.
    ///
    /// <para>One constructor for both callers so the gate is the only thing that differs between a
    /// standalone analyzer and a split's half — the duplicate it replaced is how one of them comes
    /// to be built differently from the other without anyone deciding it should.</para>
    /// </summary>
    private IAnalyzerDevice CreateQa40x(Func<bool> allow384kHz) =>
        new Qa40xUsbDevice(_loggerFactory.CreateLogger<Qa40xUsbDevice>())
        {
            Allow384kHz = allow384kHz,
        };

    /// <summary>
    /// A composite analyzer from the two kinds <c>device.split</c> names.
    ///
    /// <para><b>A sound-card half is built for one direction only.</b>
    /// <see cref="SoundCardSettings.ForDirection"/> blanks the other direction's device name, so
    /// <c>Direction</c> derives <c>InputOnly</c> or <c>OutputOnly</c> — without it a
    /// fully-configured duplex card used as one half opens <i>duplex</i> and claims an input the
    /// split is not using, which under WASAPI exclusive takes an endpoint away from whatever else
    /// wanted it.</para>
    ///
    /// <para>The pairing cannot be unbuildable here: <see cref="SplitDeviceSetting"/> falls back to
    /// its default rather than holding one this build cannot make, because this runs inside the
    /// session's field initializer where a refusal would be a crash at startup rather than a
    /// message on the status bar.</para>
    /// </summary>
    private IAnalyzerDevice BuildSplit()
    {
        // **Only a card against another card needs a transfer thread of its own, and giving one to
        // every card half would be the wrong default.** A transfer loop occupies the thread it is
        // posted to for its stream's whole life, so two cards sharing the library's one thread
        // means the second pump never runs. One card against a QA40x or the simulator is a single
        // PortAudio stream, which is the arrangement every measurement this app has taken was
        // measured with, and it keeps it.
        bool twoCards = _split.InputKind == DeviceKind.SoundCard && _split.OutputKind == DeviceKind.SoundCard;

        IAnalyzerDevice input = BuildHalf(_split.InputKind, SoundCardStreamDirection.InputOnly, twoCards);
        IAnalyzerDevice output = BuildHalf(_split.OutputKind, SoundCardStreamDirection.OutputOnly, twoCards);

        SplitDeviceSettings settings = _split.DeviceSettings(
            LabelFor(_split.InputKind, asInput: true),
            LabelFor(_split.OutputKind, asInput: false));

        // Decided here because this is the only layer that can: the composite holds two
        // IAnalyzerDevices and cannot see which host API a card was picked under, and judging it
        // needs no native call, which matters because this runs in the session's field initializer.
        if (twoCards)
        {
            settings = settings with
            {
                CombinationProblem = SoundCardSettings.SplitCombinationProblem(
                    _soundCard.Config.InputHostApi,
                    _soundCard.Config.InputDeviceName,
                    _soundCard.Config.OutputHostApi,
                    _soundCard.Config.OutputDeviceName),
            };
        }

        return new SplitAnalyzerDevice(
            input, output, settings, _loggerFactory.CreateLogger<SplitAnalyzerDevice>());
    }

    /// <summary>
    /// One half of a composite analyzer.
    ///
    /// <para><b>A QA40x or simulated half always offers 384 kHz</b>, whatever the experimental
    /// opt-in says, and that is safe by construction rather than by permission. The restriction
    /// exists to protect the QA403's <i>output</i> protection relay, which that rate's DAC drives
    /// into near-DC mangling — and what protects it is not this gate but
    /// <see cref="Qa40xModel.MaxGeneratorSampleRateHz"/> (192 kHz, hardcoded) together with
    /// <c>DeviceSessionService.RequireGeneratorRateUsable</c>. On a split the generator is the
    /// <em>other</em> device: <see cref="SplitAnalyzerDevice.Capabilities"/> takes its output ladder
    /// from the output half's <c>OutputSampleRatesHz</c> filtered by that half's own
    /// <c>MaxGeneratorSampleRateHz</c>, so a capture-side 384 kHz cannot raise it by one rung.
    /// </para>
    ///
    /// <para>And it is the one bench the rate exists for — a QA403 capturing to about 180 kHz while
    /// something else plays — so meeting it behind a checkbox in a dialog a split no longer opens
    /// was a setting standing between the user and the reason they paired two devices. A standalone
    /// QA403 keeps the opt-in, where the rate really should not arrive unasked: its own DAC reads
    /// 12 dB low with about 44 % distortion there. <c>ConfigRepository</c>'s
    /// <c>Experimental384kConfig</c> records that measurement.</para>
    /// </summary>
    private IAnalyzerDevice BuildHalf(DeviceKind kind, SoundCardStreamDirection direction, bool ownStreamThread) =>
        ApplyStoredSettings(kind switch
        {
            DeviceKind.SoundCard => CreateSoundCard(
                _soundCard.DeviceSettings.ForDirection(direction), ownStreamThread),
            DeviceKind.Simulated => CreateSimulator(SplitAllows384kHz),
            _ => CreateQa40x(SplitAllows384kHz),
        });

    /// <summary>Always, on a split — see <see cref="BuildHalf"/> for why that is safe.</summary>
    private static Func<bool> SplitAllows384kHz => static () => true;

    /// <summary>
    /// A sound card from the settings given, which for one half of a split are masked to one
    /// direction.
    ///
    /// <para><b>Virtual for the reason <see cref="Create"/> is</b>, and the same reason spelled a
    /// second time: a <c>SoundCardDevice</c>'s constructor reaches <c>PortAudioNative.Shared</c>,
    /// which initialises a native audio stack on a thread of its own, so a unit test must not build
    /// one. Overriding this is how the composite's tests check <i>which</i> settings each half was
    /// built from — the direction masking being a rule that is invisible on a card chosen for only
    /// one direction and wrong on a fully-configured duplex one.</para>
    /// </summary>
    protected virtual IAnalyzerDevice CreateSoundCard(SoundCardSettings settings, bool ownStreamThread = false) =>
        new SoundCardDevice(
            settings, _soundCard.Calibration, _loggerFactory.CreateLogger<SoundCardDevice>(), ownStreamThread);

    /// <summary>
    /// What to call a half in a log line or a connect failure, before anything has connected and so
    /// before a <see cref="DeviceInfo"/> exists. A card is named by the device the user picked,
    /// which is the only one of the three a bench can have two of.
    /// </summary>
    private string LabelFor(DeviceKind kind, bool asInput) => kind switch
    {
        DeviceKind.SoundCard => asInput
            ? FirstNonEmpty(_soundCard.Config.InputDeviceName, "the input card")
            : FirstNonEmpty(_soundCard.Config.OutputDeviceName, "the output card"),
        DeviceKind.Simulated => "the simulator",
        _ => "the QA40x",
    };

    private static string FirstNonEmpty(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>
    /// The simulator as the app hands it to the user: noise floor and the generator's fixed harmonic
    /// stack <b>on</b>, mains hum <b>off</b>.
    ///
    /// <para><b>Noise was off until 2026-10-06</b>, on the reasoning that a straight wire should read
    /// as a straight wire and each impairment should be something to switch on rather than something
    /// to explain away. The reasoning is sound and the default was still wrong, because a noise-free
    /// bench is not a clean one — it is an <i>impossible</i> one, and several analyses here are built
    /// on the assumption that no bin is ever exactly zero. A noise-free simulation drives a stopband
    /// bin to the sample format's own rounding, which <c>FrequencyResponseEstimator.ToDb</c> floors
    /// at −600 dB with a coherence of 1.0 (no noise means no disagreement between averages, so
    /// coherence cannot catch it) and a phase that is whatever <c>atan2(0, 0)</c> and the removed
    /// delay made of it. Measured on the Bessel over a 96 kHz span, that unwrapped to −1 311 194° by
    /// 95 kHz and put three thousand critical-phase markers on a Nichols chart — see
    /// <c>response/complexPlane.ts</c>'s <c>MIN_MEASURED_RELATIVE_DB</c>, which exists to clean up
    /// after exactly this. Every one of those floors is a guard against an artefact of the default,
    /// not against anything a bench can produce.</para>
    ///
    /// <para>Hum stays off: 50 Hz and two harmonics are a <i>fault</i> to demonstrate rather than a
    /// property of a working bench, and they sit in the middle of the band where they obscure a real
    /// measurement. The harmonic stack stays on (since 2026-09-21, at Joost's request): without it
    /// the FFT screen's THD reads 0.000 % on every linear model, which looks like a broken
    /// measurement rather than a clean one, and the nonlinear models switch the stack off for
    /// themselves anyway (<see cref="SimulatedDut.SuppliesOwnDistortion"/>).</para>
    ///
    /// <para><see cref="SimulatedDevice"/>'s own defaults keep all three on — a large number of tests
    /// are written against that bench, and a device built directly still gets it.</para>
    /// </summary>
    private SimulatedDevice CreateSimulator(Func<bool> allow384kHz)
    {
        var device = new SimulatedDevice { Allow384kHz = allow384kHz };
        device.SignalProfile = device.SignalProfile with { HumEnabled = false };
        return device;
    }
}
