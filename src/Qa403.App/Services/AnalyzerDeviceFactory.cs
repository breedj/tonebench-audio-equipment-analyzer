using Qa403.Device;
using Qa403.Device.Hardware;
using Qa403.Device.Simulation;

namespace Qa403.App.Services;

/// <summary>Which analyzer the session is talking to.</summary>
public enum DeviceKind
{
    /// <summary>A real QA403 over USB.</summary>
    Hardware = 0,

    /// <summary>The synthetic device — see <see cref="SimulatedDevice"/> and <see cref="SimulatedDutModel"/>.</summary>
    Simulated = 1,
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

    public AnalyzerDeviceFactory(ILoggerFactory loggerFactory, DeviceKind initialKind)
    {
        _loggerFactory = loggerFactory;
        InitialKind = initialKind;
    }

    private AnalyzerDeviceFactory(IAnalyzerDevice pinned)
    {
        _loggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _pinned = pinned;
        InitialKind = pinned is SimulatedDevice ? DeviceKind.Simulated : DeviceKind.Hardware;
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

    public virtual IAnalyzerDevice Create(DeviceKind kind) =>
        _pinned
        ?? kind switch
        {
            DeviceKind.Simulated => CreateSimulator(),
            _ => new Qa40xUsbDevice(_loggerFactory.CreateLogger<Qa40xUsbDevice>()),
        };

    /// <summary>
    /// The simulator as the app hands it to the user: noise floor, mains hum and the generator's
    /// fixed harmonic stack all <b>off</b>, so a straight wire reads as a straight wire and each
    /// impairment is something to switch on from the status bar rather than something to explain
    /// away. <see cref="SimulatedDevice"/>'s own defaults keep all three on — a large number of
    /// tests are written against that bench, and a device built directly still gets it.
    /// </summary>
    private static SimulatedDevice CreateSimulator()
    {
        var device = new SimulatedDevice();
        device.SignalProfile = device.SignalProfile with
        {
            NoiseEnabled = false,
            HumEnabled = false,
            HarmonicsEnabled = false,
        };
        return device;
    }
}
