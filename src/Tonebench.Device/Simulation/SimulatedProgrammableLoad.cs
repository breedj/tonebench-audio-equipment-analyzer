using Tonebench.Device.Hardware;

namespace Tonebench.Device.Simulation;

/// <summary>
/// <see cref="IProgrammableLoad"/> with no hardware behind it, so the load screens can be built and
/// driven with nothing on the bench — the same role <see cref="SimulatedDevice"/> plays for the
/// analyzer.
///
/// <para><b>It models the QA451's measured behaviour rather than an idealised load</b>, because the
/// quirks are the part a UI gets wrong: closing the DUT switch takes
/// <see cref="Qa451Protocol.SoftStartSettleMs"/> and <see cref="SetDutPowerAsync"/> really does wait
/// that long, and the current reading is quantised to <see cref="CurrentQuantumA"/> — ~48.5 mA,
/// measured on the bench unit, where the wire's units are milliamps. A screen built against a load
/// that answers instantly in exact milliamps is a screen that will look wrong the first time it
/// meets the real thing.</para>
///
/// <para><b>And it is the only way to exercise the fault paths at all.</b> Over-temperature needs a
/// heatsink at 60 °C and over-current needs more than 15 A, which also latches until the USB is
/// replugged — so those bits have never been seen set on real hardware here, and the hardware tests
/// deliberately do not provoke them (see <c>docs/qa451-protocol.md</c>). <see cref="InjectFault"/>
/// is how the fault handling gets tested, and the only honest way to see those screens.</para>
///
/// <para><b>Not to be confused with <see cref="SimulatedLoad"/></b>, which is the simulated
/// <i>amplifier's</i> load knob — what hangs on the output stage for the output-impedance
/// measurement. That one is a property of the simulated DUT; this is an instrument. The names are
/// close because the things are: resist the urge to shorten this one back.</para>
///
/// <para>Two things it deliberately does <i>not</i> model, both because they need a signal source
/// that a simulation does not have: heating (nothing dissipates in a load with no amplifier across
/// it, so the temperatures sit where they are put) and the refusal of load control while DC is
/// present on a load input. The second is worth knowing about rather than imitating — a fake that
/// refuses commands for reasons the user cannot see would be a poor teaching device.</para>
/// </summary>
public sealed class SimulatedProgrammableLoad : IProgrammableLoad
{
    /// <summary>
    /// The granularity of the real device's current reading, measured 2026-10-11: every value seen
    /// across a day of runs was a multiple of ~48.5 mA, whatever the milliamp wire units suggest.
    /// </summary>
    public const double CurrentQuantumA = 0.0485;

    private static readonly LoadImpedance[] Supported =
        [LoadImpedance.Open, LoadImpedance.FourOhm, LoadImpedance.EightOhm];

    private readonly int _softStartSettleMs;

    private LoadFaults _faults = LoadFaults.None;
    private bool _requiresReplug;
    private bool _disposed;

    /// <summary>Creates a simulated load.</summary>
    /// <param name="softStartSettleMs">
    /// How long <see cref="SetDutPowerAsync"/> takes to close the switch. Defaults to the figure
    /// measured on real hardware; a test that does not care about the wait passes 0.
    /// </param>
    public SimulatedProgrammableLoad(int softStartSettleMs = Qa451Protocol.SoftStartSettleMs)
    {
        _softStartSettleMs = softStartSettleMs;
    }

    /// <summary>
    /// Left-channel heatsink temperature to report. Settable so a screen can be shown a hot bench;
    /// nothing here heats up by itself, because nothing is dissipating.
    /// </summary>
    public int LeftTemperatureC { get; set; } = 22;

    /// <summary>
    /// Right-channel heatsink temperature. Deliberately <b>not</b> the same as the left one: two
    /// identical readings cannot show a left/right mix-up, and this app has been caught by a channel
    /// mapping before.
    /// </summary>
    public int RightTemperatureC { get; set; } = 23;

    /// <summary>
    /// What the DUT supply draws once the switch is closed, before quantisation. Reported as 0 while
    /// the switch is open, exactly as the hardware does.
    /// </summary>
    public double DutCurrentA { get; set; } = 0.5;

    /// <inheritdoc />
    public LoadInfo? Info { get; private set; }

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<LoadImpedance> SupportedImpedances => Supported;

    /// <inheritdoc />
    public LoadImpedance? Impedance { get; private set; }

    /// <inheritdoc />
    public bool? DutPowerOn { get; private set; }

    /// <summary>The last reading handed out, or null if none has been.</summary>
    public LoadReading? LastReading { get; private set; }

    /// <summary>
    /// Makes the load report a fault, which no bench test can. Over-current latches on the real
    /// hardware and is only cleared by unplugging it, so <paramref name="requiresReplug"/> follows
    /// the fault rather than being free: pass <see cref="LoadFaults.None"/> to clear a
    /// non-latching one.
    /// </summary>
    public void InjectFault(LoadFaults faults, bool requiresReplug = false)
    {
        _faults = faults;
        _requiresReplug = faults != LoadFaults.None && requiresReplug;
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Info = new LoadInfo(
            Model: "Simulated load",
            FirmwareVersion: 1,
            RequiredFirmwareVersion: 1,
            SerialNumber: 0,
            UsbSerial: null);
        IsConnected = true;

        // Connecting resets the hardware to a known state, so the simulation does the same — a
        // screen that comes up showing "unknown" against a simulated load would be showing a state
        // the real device only reports after a connect that deliberately skipped its reset.
        Impedance = LoadImpedance.Open;
        DutPowerOn = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetImpedanceAsync(LoadImpedance impedance, CancellationToken cancellationToken = default)
    {
        if (!Supported.Contains(impedance))
        {
            throw new ArgumentOutOfRangeException(
                nameof(impedance), impedance, "A QA451 presents open, 4 or 8 ohms.");
        }

        RequireConnected();
        Impedance = impedance;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SetDutPowerAsync(bool on, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        DutPowerOn = on;

        // The real soft start, modelled: a close takes about a second and a half and the device is
        // unresponsive for most of it. A caller that works against this will work against hardware.
        if (on && _softStartSettleMs > 0)
        {
            await Task.Delay(_softStartSettleMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<LoadReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();

        bool powered = DutPowerOn == true;
        double amps = powered ? Quantise(DutCurrentA) : 0.0;

        var reading = new LoadReading(
            LeftTemperatureC,
            RightTemperatureC,
            amps,
            _faults,
            _requiresReplug,
            RawStatusBits: StatusBits(powered),
            EngagedImpedance: Impedance,
            DutPowerEngaged: powered);

        LastReading = reading;
        return Task.FromResult(reading);
    }

    /// <inheritdoc />
    public Task SetSafeStateAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        Impedance = LoadImpedance.Open;
        DutPowerOn = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The status word the hardware would report for this state, so anything that reads the raw bits
    /// — a log line, a diagnostic, the issue report — sees the same shape it would on a bench.
    /// </summary>
    private ushort StatusBits(bool powered)
    {
        ushort status = Qa451Protocol.StatusAlwaysSet;

        if (powered)
        {
            status |= Qa451Protocol.StatusDutPowerOn;
        }

        status |= Impedance switch
        {
            LoadImpedance.FourOhm => Qa451Protocol.StatusLoad4Ohm,
            LoadImpedance.EightOhm => Qa451Protocol.StatusLoad8Ohm,
            _ => 0,
        };

        // The two fault bits mean different things per model; this reports them as a QA451B does,
        // which is the unit this app was developed against.
        if (_faults.HasFlag(LoadFaults.OverTemperature))
        {
            status |= 0x0008;
        }

        if (_faults.HasFlag(LoadFaults.OverCurrent))
        {
            status |= 0x0010;
        }

        return status;
    }

    private static double Quantise(double amps) =>
        Math.Round(amps / CurrentQuantumA) * CurrentQuantumA;

    private void RequireConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected)
        {
            throw new InvalidOperationException("No load is connected.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        IsConnected = false;
        Info = null;
    }
}
