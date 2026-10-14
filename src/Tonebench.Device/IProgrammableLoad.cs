namespace Tonebench.Device;

/// <summary>
/// What a programmable load can present to a DUT. The numeric values are the ohms, and
/// <see cref="Open"/> is deliberately 0 rather than an absent value, because that is how both
/// QuantAsylum's own API and its hardware spell it — a QA451's "open" is a ~10 kΩ sense path, not a
/// disconnected terminal.
/// </summary>
public enum LoadImpedance
{
    /// <summary>No load engaged (~10 kΩ to the DUT on a QA451).</summary>
    Open = 0,

    /// <summary>4 Ω load engaged.</summary>
    FourOhm = 4,

    /// <summary>8 Ω load engaged.</summary>
    EightOhm = 8,
}

/// <summary>
/// Fault conditions a load reports about itself. Flags rather than an enum because a device may
/// report more than one at once, and because the raw bits are model-specific: see
/// <see cref="Hardware.Qa451Protocol.DecodeFaults"/>, where the same two bits mean different things
/// on a QA451 and a QA451B.
/// </summary>
[Flags]
public enum LoadFaults
{
    /// <summary>Nothing wrong.</summary>
    None = 0,

    /// <summary>A heatsink is over temperature. The load elements are protected in hardware.</summary>
    OverTemperature = 1 << 0,

    /// <summary>Over-current protection has tripped.</summary>
    OverCurrent = 1 << 1,
}

/// <summary>
/// Identity of a connected load, read once at connect. Separate from <see cref="LoadReading"/>
/// because none of it changes while the device is open.
/// </summary>
/// <param name="Model">Model name as the hardware itself reports it, e.g. "QA451B".</param>
/// <param name="FirmwareVersion">Firmware version the device reports.</param>
/// <param name="RequiredFirmwareVersion">
/// The lowest firmware the vendor application accepts for this model. Below it, that application
/// offers a reflash; this app only reports the mismatch — see
/// <see cref="Hardware.Qa451Load"/>'s remarks on why it never flashes anything.
/// </param>
/// <param name="SerialNumber">The product serial number the device reports, as a bare integer.</param>
/// <param name="UsbSerial">
/// The USB serial-number string from the device descriptor. A different thing from
/// <paramref name="SerialNumber"/>, and the one a host uses to tell two attached units apart.
/// </param>
public sealed record LoadInfo(
    string Model,
    int FirmwareVersion,
    int RequiredFirmwareVersion,
    uint SerialNumber,
    string? UsbSerial)
{
    /// <summary>Whether the firmware is at or above what this model is expected to run.</summary>
    public bool FirmwareIsSupported => FirmwareVersion >= RequiredFirmwareVersion;
}

/// <summary>
/// One poll of a load: its two heatsink temperatures, the DC current through its sense path, and
/// whatever it says about its own faults.
/// </summary>
/// <param name="LeftTemperatureC">Left-channel heatsink temperature. Whole degrees — the device reports a signed byte.</param>
/// <param name="RightTemperatureC">Right-channel heatsink temperature, same quantisation.</param>
/// <param name="CurrentA">
/// DC current in amps. The wire carries integer milliamps, but the measurement is far coarser than
/// that: every reading from this bench's QA451B was a multiple of about 48.5 mA, so a single sample
/// is worth roughly ±50 mA and anything quantitative should average several. See
/// <c>docs/qa451-protocol.md</c>.
/// </param>
/// <param name="Faults">Decoded faults, which needs the model to read the bits correctly.</param>
/// <param name="RequiresReplug">
/// True when a fault has latched in hardware and cannot be cleared by any command — the USB cable
/// has to be pulled. Carried here rather than inferred by callers, because which faults latch is
/// model-specific.
/// </param>
/// <param name="RawStatusBits">
/// The status field exactly as the device sent it, all nine bits. Kept because three of the nine are
/// still undocumented: a reading that behaves oddly is worth logging in full rather than reducing to
/// the flags this app happens to decode today.
/// </param>
/// <param name="EngagedImpedance">
/// The load the <i>hardware</i> says is engaged, read from the status word rather than remembered —
/// so a reading answers for the device and not for this app's bookkeeping. Null when the device
/// reports both load bits at once, which is not a state this app commands.
/// </param>
/// <param name="DutPowerEngaged">Whether the hardware says its high-side switch is closed.</param>
public sealed record LoadReading(
    int LeftTemperatureC,
    int RightTemperatureC,
    double CurrentA,
    LoadFaults Faults,
    bool RequiresReplug,
    ushort RawStatusBits,
    LoadImpedance? EngagedImpedance,
    bool DutPowerEngaged);

/// <summary>
/// A programmable load: a switchable resistive load across a DUT's output, a high-side switch for
/// the DUT's own supply, and a current measurement.
///
/// <para>Deliberately not part of <see cref="IAnalyzerDevice"/>. A load neither streams samples nor
/// generates a stimulus; it is a second instrument on the bench that an analyzer measurement may
/// want to set up before it runs.</para>
///
/// <para>Every method is async only so that the caller's thread is not blocked waiting on the
/// device's own gate. The underlying USB exchange is synchronous and short — a few milliseconds on a
/// QA451, with a one-second ceiling — so implementations hold their gate across it rather than
/// pushing the I/O onto the thread pool.</para>
/// </summary>
public interface IProgrammableLoad : IDisposable
{
    /// <summary>Identity read at connect, or null when not connected.</summary>
    LoadInfo? Info { get; }

    /// <summary>True between a successful <see cref="ConnectAsync"/> and a loss or dispose.</summary>
    bool IsConnected { get; }

    /// <summary>The load settings this device can actually present.</summary>
    IReadOnlyList<LoadImpedance> SupportedImpedances { get; }

    /// <summary>
    /// The load that is engaged, or null while that is not known yet.
    ///
    /// <para>On a QA451 this is the <i>hardware's</i> own answer: every
    /// <see cref="ReadAsync"/> decodes it from the status word
    /// (<see cref="Hardware.Qa451Protocol.DecodeRelayState"/>, measured on the bench), so a poll
    /// corrects this app's bookkeeping rather than the other way round. QuantAsylum's own
    /// application does not appear to know that is possible — its REST
    /// <c>GET /Settings/Impedance</c> answers from its button state.</para>
    ///
    /// <para>Null is therefore a narrow window: between a connect that deliberately did not reset
    /// and the first poll, nothing has established the state and this stays null rather than
    /// claiming the safe one.</para>
    /// </summary>
    LoadImpedance? Impedance { get; }

    /// <summary>Whether the DUT supply switch is closed, or null while that is not known. Same reasoning as <see cref="Impedance"/>.</summary>
    bool? DutPowerOn { get; }

    /// <summary>
    /// Opens the device, reads its identity, and puts it in a known state (no load, power off).
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Engages the given load, or opens it.</summary>
    Task SetImpedanceAsync(LoadImpedance impedance, CancellationToken cancellationToken = default);

    /// <summary>Switches the DUT's own supply through the load's high-side switch.</summary>
    Task SetDutPowerAsync(bool on, CancellationToken cancellationToken = default);

    /// <summary>Polls temperatures, current and status.</summary>
    Task<LoadReading> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// No load, DUT power off. Worth its own method because it is the state a bench should be left
    /// in, and because a load is one of the few things in this app whose state outlives the process
    /// — see <see cref="Hardware.Qa451Load.Dispose"/>.
    /// </summary>
    Task SetSafeStateAsync(CancellationToken cancellationToken = default);
}
