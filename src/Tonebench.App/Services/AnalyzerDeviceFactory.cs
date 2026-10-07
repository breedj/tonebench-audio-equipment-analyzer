using Tonebench.Device;
using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;
using Tonebench.Device.SoundCard;

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

    public AnalyzerDeviceFactory(
        ILoggerFactory loggerFactory,
        DeviceKind initialKind,
        SoundCardSetting? soundCard = null,
        Experimental384kSetting? experimental384k = null,
        AutoRangeStepDownDelaySetting? stepDownDelay = null)
    {
        _loggerFactory = loggerFactory;
        _soundCard = soundCard ?? new SoundCardSetting();
        _experimental384k = experimental384k ?? new Experimental384kSetting();
        _stepDownDelay = stepDownDelay ?? new AutoRangeStepDownDelaySetting();
        InitialKind = initialKind;
    }

    private AnalyzerDeviceFactory(IAnalyzerDevice pinned)
    {
        _loggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _soundCard = new SoundCardSetting();
        _experimental384k = new Experimental384kSetting();
        _stepDownDelay = new AutoRangeStepDownDelaySetting();
        _pinned = pinned;

        // A three-way test, not "simulator or else": a pinned sound card reported itself as
        // Hardware while the status bar showed a QA403 that was not there.
        InitialKind = pinned switch
        {
            SimulatedDevice => DeviceKind.Simulated,
            SoundCardDevice => DeviceKind.SoundCard,
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
            DeviceKind.Simulated => CreateSimulator(),

            // Built from the settings as they stand right now rather than from a captured copy: a
            // card chosen or calibrated since the last switch must be the one that opens, and the
            // session reconnects through this factory after every change for exactly that reason.
            DeviceKind.SoundCard => new SoundCardDevice(
                _soundCard.DeviceSettings,
                _soundCard.Calibration,
                _loggerFactory.CreateLogger<SoundCardDevice>()),

            // Allow384kHz is a Func, so unlike the sound card's settings it is NOT captured here:
            // the checkbox takes effect on this already-built device, and a device built before the
            // configuration document was read still reports the right rates. See
            // Qa40xUsbDevice.Allow384kHz.
            _ => new Qa40xUsbDevice(_loggerFactory.CreateLogger<Qa40xUsbDevice>())
            {
                Allow384kHz = () => _experimental384k.Enabled,
            },
        };

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
    private SimulatedDevice CreateSimulator()
    {
        var device = new SimulatedDevice { Allow384kHz = () => _experimental384k.Enabled };
        device.SignalProfile = device.SignalProfile with { HumEnabled = false };
        return device;
    }
}
