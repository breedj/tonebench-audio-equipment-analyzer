using Microsoft.Extensions.Logging;

namespace Tonebench.Device.Hardware;

/// <summary>
/// Picks the HID transport for the running OS. Both implementations use APIs that are already
/// present — see <see cref="IHidTransport"/> for why this app owns them rather than taking a
/// dependency.
/// </summary>
internal static class HidTransport
{
    /// <summary>Creates a transport for this platform.</summary>
    /// <exception cref="PlatformNotSupportedException">On an OS with neither hid.dll nor hidraw.</exception>
    public static IHidTransport Create(int vendorId, int productId)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsHidTransport(vendorId, productId);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxHidRawTransport(vendorId, productId);
        }

        throw new PlatformNotSupportedException(
            "A QA451 can be driven on Windows (hid.dll) and Linux (hidraw) only.");
    }
}

/// <summary>
/// Driver for the QuantAsylum QA451(B) programmable load. Framing and opcodes live in
/// <see cref="Qa451Protocol"/>, cited to <c>docs/qa451-protocol.md</c>; this class owns the
/// sequencing and the state, and nothing else.
///
/// <para>The device is strictly request/response — measured: it sends no unsolicited report, and its
/// control pipe answers nothing — so there is no pump, no queue and no drop counter here. One gate
/// serialises every exchange, because a reply carries no indication of which request it answers: two
/// callers interleaving a write and a read would silently swap each other's answers.</para>
///
/// <para><b>Nothing here ever flashes firmware.</b> The vendor application offers a reflash when the
/// firmware is below <see cref="Qa451Protocol.RequiredFirmwareFor"/>; this app reports the mismatch
/// and carries on, because QuantAsylum's own release notes say an unrequested reflash can brick the
/// unit. <see cref="Qa451Protocol.EnterBsl"/> is declared and never sent.</para>
/// </summary>
public sealed class Qa451Load : IProgrammableLoad
{
    private static readonly LoadImpedance[] Supported =
        [LoadImpedance.Open, LoadImpedance.FourOhm, LoadImpedance.EightOhm];

    /// <summary>How long the handle is left closed before it is reopened. See <see cref="ReopenCoreAsync"/>.</summary>
    private const int ReopenPauseMs = 500;

    private readonly ILogger<Qa451Load> _logger;
    private readonly Func<IHidTransport> _transportFactory;
    private readonly int _softStartSettleMs;

    /// <summary>
    /// How long a relay command is left alone before it is read back. Scaled with the soft-start
    /// setting so a test that asks for no waits gets none of either — see
    /// <see cref="Qa451Protocol.RelaySettleMs"/> for why the wait exists at all.
    /// </summary>
    private readonly int _relaySettleMs;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IHidTransport? _transport;
    private Qa451Model _model;
    private bool _disposed;

    /// <summary>
    /// When the DUT switch was last commanded, or <see cref="long.MinValue"/> if never. Read by
    /// <see cref="ReadAsync"/> to widen its timeout across the soft start — see
    /// <see cref="Qa451Protocol.PowerTransitionTimeoutMs"/>.
    /// </summary>
    private long _powerTransitionAtTicks = long.MinValue;

    /// <summary>Creates a driver that will talk to the first QA451 attached to this machine.</summary>
    public Qa451Load(ILogger<Qa451Load> logger)
        : this(
            logger,
            () => HidTransport.Create(Qa451Protocol.Qa451VendorId, Qa451Protocol.Qa451ProductId),
            Qa451Protocol.SoftStartSettleMs)
    {
    }

    /// <summary>
    /// Creates a driver over a given transport, for tests.
    /// </summary>
    /// <param name="softStartSettleMs">
    /// How long <see cref="SetDutPowerAsync"/> waits out the soft start. Only a test passes anything
    /// but the default: a scripted transport has no soft start to wait for, and real seconds spent
    /// sleeping in a unit test are seconds nobody gets back.
    /// </param>
    internal Qa451Load(
        ILogger<Qa451Load> logger,
        Func<IHidTransport> transportFactory,
        int softStartSettleMs = Qa451Protocol.SoftStartSettleMs)
    {
        _logger = logger;
        _transportFactory = transportFactory;
        _softStartSettleMs = softStartSettleMs;
        _relaySettleMs = softStartSettleMs == 0 ? 0 : Qa451Protocol.RelaySettleMs;
    }

    /// <inheritdoc />
    public LoadInfo? Info { get; private set; }

    /// <inheritdoc />
    public bool IsConnected => _transport is { IsOpen: true } && Info is not null;

    /// <inheritdoc />
    public IReadOnlyList<LoadImpedance> SupportedImpedances => Supported;

    /// <inheritdoc />
    public LoadImpedance? Impedance { get; private set; }

    /// <inheritdoc />
    public bool? DutPowerOn { get; private set; }

    /// <summary>
    /// The last reading taken, or null if none has been. Exposed because a latched fault is
    /// information the <i>app</i> has to act on: this driver deliberately does not refuse commands
    /// when one is set, since nothing establishes that the firmware ignores them and inventing that
    /// rule here would hide the fault behind an exception about something else.
    /// </summary>
    public LoadReading? LastReading { get; private set; }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(resetToKnownState: true, cancellationToken);

    /// <summary>
    /// Opens the device and reads its identity.
    ///
    /// <para><paramref name="resetToKnownState"/> sends <see cref="Qa451Protocol.Reset"/>, which is
    /// the only way to know what the relays are doing: the device cannot be asked, and it keeps its
    /// load engaged and its DUT supply switched after the process that set them exits — the vendor
    /// application's own connect does exactly this reset for that reason. Pass false only to inspect
    /// a device without disturbing it, and expect <see cref="Impedance"/> and
    /// <see cref="DutPowerOn"/> to stay null, which is the honest answer rather than a guess.</para>
    /// </summary>
    public async Task ConnectAsync(bool resetToKnownState, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_transport is { IsOpen: true })
            {
                throw new InvalidOperationException("This QA451 is already connected.");
            }

            IHidTransport transport = _transportFactory();
            try
            {
                transport.Open();

                _model = Qa451Protocol.ModelFromId(Exchange(transport, Qa451Protocol.DeviceId));
                int firmware = (int)Exchange(transport, Qa451Protocol.FirmwareVersion);
                uint serial = Exchange(transport, Qa451Protocol.SerialNumber);

                var info = new LoadInfo(
                    Qa451Protocol.ModelName(_model),
                    firmware,
                    Qa451Protocol.RequiredFirmwareFor(_model),
                    serial,
                    transport.UsbSerial);

                if (resetToKnownState)
                {
                    Send(transport, Qa451Protocol.Reset);
                    Impedance = LoadImpedance.Open;
                    DutPowerOn = false;
                }

                _transport = transport;
                Info = info;

                _logger.LogInformation(
                    "QA451 load connected: {Model}, firmware {Firmware} (requires {Required}), serial {Serial}, "
                        + "USB serial {UsbSerial}, state {State}",
                    info.Model,
                    info.FirmwareVersion,
                    info.RequiredFirmwareVersion,
                    info.SerialNumber,
                    info.UsbSerial ?? "unknown",
                    resetToKnownState ? "reset to no load, power off" : "left as found");

                if (!info.FirmwareIsSupported)
                {
                    _logger.LogWarning(
                        "QA451 firmware is {Firmware}, below the {Required} QuantAsylum's own application "
                            + "expects for a {Model}. Nothing here will flash it; use the vendor application "
                            + "if a update is really wanted.",
                        info.FirmwareVersion,
                        info.RequiredFirmwareVersion,
                        info.Model);
                }
            }
            catch
            {
                transport.Dispose();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetImpedanceAsync(LoadImpedance impedance, CancellationToken cancellationToken = default)
    {
        // Validated before the gate, so a bad argument is refused without waiting for the device.
        _ = Qa451Protocol.ImpedanceCommand(impedance);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WithReopenOnRefusalAsync(
                transport => SetImpedanceCoreAsync(transport, impedance, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetDutPowerAsync(bool on, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WithReopenOnRefusalAsync(
                transport => SetDutPowerCoreAsync(transport, on, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<LoadReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IHidTransport transport = RequireConnected();

            // The LED kick rides along with the poll, on the cadence the vendor application uses.
            // Whether the firmware treats it as a watchdog is unknown (docs/qa451-protocol.md), and
            // this is the cheap side of that bet.
            Send(transport, Qa451Protocol.KickLed);

            // A poll landing in the soft start waits longer, because the device stops answering
            // while it sequences one: measured at the ordinary ceiling, which is also the soft
            // start's own duration, so the two were racing.
            int timeoutMs = Environment.TickCount64 - _powerTransitionAtTicks < 2 * Qa451Protocol.SoftStartMs
                ? Qa451Protocol.PowerTransitionTimeoutMs
                : Qa451Protocol.ExchangeTimeoutMs;

            (int left, int right, ushort status) = Qa451Protocol.DecodeTempsAndStatus(
                Exchange(transport, Qa451Protocol.ReadTempsAndStatus, timeoutMs: timeoutMs));
            double amps = Qa451Protocol.CurrentAmpsFromRaw(
                Exchange(transport, Qa451Protocol.ReadCurrent, timeoutMs: timeoutMs));
            (LoadFaults faults, bool requiresReplug) = Qa451Protocol.DecodeFaults(_model, status);
            (LoadImpedance? engaged, bool dutPower) = Qa451Protocol.DecodeRelayState(status);

            var reading = new LoadReading(
                left, right, amps, faults, requiresReplug, status, engaged, dutPower);
            LoadFaults previous = LastReading?.Faults ?? LoadFaults.None;
            LoadImpedance? commanded = Impedance;
            bool? commandedPower = DutPowerOn;
            LastReading = reading;

            // The hardware's own answer wins over this app's bookkeeping: the status word carries
            // the relay positions, so a poll is what makes Impedance true rather than merely
            // remembered — and what resolves it at all after a connect that did not reset.
            if (engaged is not null)
            {
                Impedance = engaged;
            }

            DutPowerOn = dutPower;

            // A relay that did not take is worth saying out loud. Note a poll issued inside a
            // relay's settle can report the previous state, so this is "they disagree", not
            // necessarily "the command failed".
            if (commanded is not null && engaged is not null && commanded != engaged)
            {
                _logger.LogWarning(
                    "QA451 reports {Reported} engaged where {Commanded} was commanded (status bits 0x{Status:x3})",
                    Describe(engaged.Value),
                    Describe(commanded.Value),
                    status);
            }

            // The DUT switch gets the same treatment, and it is not hypothetical: measured
            // 2026-10-10, the device accepted a power-on command, went quiet for three seconds and
            // came back with the switch open and no fault bit set, having declined to close it into
            // a supply that was current-limiting. Without this reconciliation the driver would have
            // gone on claiming the supply was switched.
            if (commandedPower is not null && commandedPower != dutPower)
            {
                _logger.LogWarning(
                    "QA451 reports the DUT supply {Reported} where {Commanded} was commanded "
                        + "(status bits 0x{Status:x3}). The device can decline to close the switch — "
                        + "check that the supply holds its voltage rather than current-limiting.",
                    dutPower ? "on" : "off",
                    commandedPower.Value ? "on" : "off",
                    status);
            }

            if (faults != previous && faults != LoadFaults.None)
            {
                _logger.LogWarning(
                    "QA451 reports {Faults} (status bits 0x{Status:x3}, heatsinks {Left} / {Right} °C, "
                        + "{Current:F3} A){Replug}",
                    faults,
                    status,
                    left,
                    right,
                    amps,
                    requiresReplug ? ". This latches in hardware: the USB cable has to be replugged." : string.Empty);
            }

            return reading;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetSafeStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Load first, then the supply: releasing the load while the DUT is still powered is the
            // ordinary end of a measurement, where cutting the supply first would leave an
            // amplifier's output driving a load as its rails collapse.
            await WithReopenOnRefusalAsync(
                transport => SetImpedanceCoreAsync(transport, LoadImpedance.Open, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await WithReopenOnRefusalAsync(
                transport => SetDutPowerCoreAsync(transport, on: false, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("QA451 put in its safe state: no load, DUT power off");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs a state command and, if the device refuses it, <b>reopens the handle and tries once
    /// more</b>.
    ///
    /// <para>Measured 2026-10-13, and this is the only recovery that works. After a quiet period the
    /// device stops applying state commands: the writes are accepted and silently ignored while
    /// <i>reads keep answering normally</i>, which points at the output pipe rather than the
    /// firmware. Retrying does not help, nor settling longer, nor reading first, nor sending the
    /// device's own Reset — but commands issued straight after a fresh open all take effect. That
    /// matches the manual's "if the QA451 application is closed or stops responding ... the QA451
    /// will automatically open the load relays", and it is why <see cref="LoadSessionService"/>'s
    /// keepalive exists: this path is the recovery, not the plan.</para>
    /// </summary>
    private async Task WithReopenOnRefusalAsync(Func<IHidTransport, Task> command, CancellationToken ct)
    {
        try
        {
            await command(RequireConnected()).ConfigureAwait(false);
            return;
        }
        catch (Qa451CommandRefusedException ex)
        {
            _logger.LogWarning(
                "QA451 refused a command ({Message}). Reopening the device and trying once more.",
                ex.Message);
        }

        await ReopenCoreAsync(ct).ConfigureAwait(false);
        await command(RequireConnected()).ConfigureAwait(false);
    }

    /// <summary>Closes and reopens the device, re-reading its identity. Caller holds the gate.</summary>
    private async Task ReopenCoreAsync(CancellationToken ct)
    {
        _transport?.Dispose();
        _transport = null;

        // A gap between closing and reopening, because closing and immediately reopening does not
        // recover the device while switching the load kind — which disposes this driver and builds a
        // new one, with a round trip in between — does. The difference between those two paths is
        // the pause, so the pause is what is reproduced here.
        if (_relaySettleMs > 0)
        {
            await Task.Delay(ReopenPauseMs, ct).ConfigureAwait(false);
        }

        IHidTransport transport = _transportFactory();
        try
        {
            transport.Open();
            _model = Qa451Protocol.ModelFromId(Exchange(transport, Qa451Protocol.DeviceId));
            int firmware = (int)Exchange(transport, Qa451Protocol.FirmwareVersion);
            uint serial = Exchange(transport, Qa451Protocol.SerialNumber);
            Send(transport, Qa451Protocol.Reset);

            if (_relaySettleMs > 0)
            {
                await Task.Delay(_relaySettleMs, ct).ConfigureAwait(false);
            }

            _transport = transport;
            Info = new LoadInfo(
                Qa451Protocol.ModelName(_model),
                firmware,
                Qa451Protocol.RequiredFirmwareFor(_model),
                serial,
                transport.UsbSerial);
            Impedance = LoadImpedance.Open;
            DutPowerOn = false;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>Sets the load and confirms the relays moved. Caller holds the gate.</summary>
    private async Task SetImpedanceCoreAsync(
        IHidTransport transport, LoadImpedance impedance, CancellationToken ct)
    {
        (byte opcode, byte argument) = Qa451Protocol.ImpedanceCommand(impedance);

        await ApplyAndVerifyAsync(
            transport,
            opcode,
            argument,
            status => Qa451Protocol.DecodeRelayState(status).Impedance == impedance,
            $"load {Describe(impedance)}",
            settleMs: _relaySettleMs,
            ct).ConfigureAwait(false);

        Impedance = impedance;
        _logger.LogInformation("QA451 load set to {Impedance}", Describe(impedance));
    }

    /// <summary>Switches the DUT supply and confirms it. Caller holds the gate.</summary>
    private async Task SetDutPowerCoreAsync(IHidTransport transport, bool on, CancellationToken ct)
    {
        (byte opcode, byte argument) = Qa451Protocol.DutPowerCommand(on);
        _powerTransitionAtTicks = Environment.TickCount64;

        // Closing the switch takes about 1.2 s and the device may not answer while it sequences the
        // soft start, so the settle is waited out *inside the gate* before anything is read back.
        // Measured 2026-10-10: a host that keeps querying through that window can make the device
        // abandon the close — the switch read engaged at 10 ms and had released itself by 120 ms —
        // whereas leaving it alone for the second gets a switch that then stays closed with no
        // keepalive at all. So this returns when the DUT supply is actually up, and no other caller
        // can interleave a poll into the window.
        await ApplyAndVerifyAsync(
            transport,
            opcode,
            argument,
            status => Qa451Protocol.DecodeRelayState(status).DutPowerOn == on,
            $"DUT power {(on ? "on" : "off")}",
            settleMs: on ? _softStartSettleMs : _relaySettleMs,
            ct).ConfigureAwait(false);

        DutPowerOn = on;
        _powerTransitionAtTicks = Environment.TickCount64;
        _logger.LogInformation("QA451 DUT power switched {State}", on ? "on" : "off");
    }

    /// <summary>
    /// Writes a state command and reads the status word back to confirm it took effect, retrying
    /// once.
    ///
    /// <para>Necessary because these commands carry <b>no acknowledgement</b> — the device answers
    /// nothing — and two measured behaviours make a blind write unreliable. The first output report
    /// after an idle period is dropped (2026-10-13: after 30 s quiet, the first load command left
    /// the load open and an immediate repeat worked), which the retry fixes outright. And DC on a
    /// load input makes the device refuse relay control entirely with no fault bit set, which it
    /// cannot fix — hence <see cref="Qa451CommandRefusedException"/> rather than a silent
    /// disagreement between what was asked for and what the next poll reports.</para>
    /// </summary>
    private async Task ApplyAndVerifyAsync(
        IHidTransport transport,
        byte opcode,
        byte argument,
        Func<ushort, bool> applied,
        string what,
        int settleMs,
        CancellationToken ct)
    {
        // Four, measured rather than chosen: after 30 s of no traffic the first two load commands
        // Two is plenty now that the kick below is in the right place: with it, a command takes
        // effect first time even after 90 s of silence. The retry is only there for a genuinely
        // dropped report, and it costs nothing unless one happens.
        const int Attempts = 2;

        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            // The kick goes immediately BEFORE the command, and the order is the whole of it.
            //
            // The kick is the device's host-liveness signal, not a cosmetic LED: after a quiet
            // period it will not move a relay until something tells it the host is alive, and a
            // bare status read does not count — measured 2026-10-13: after 60-90 s of silence a
            // command was refused however often it was retried, however long it settled, after a
            // read, after the device's own Reset and after the handle was closed and reopened. With
            // a kick in front of it, the same command takes effect first time. Sent the other way
            // round it is worse than useless: a kick arriving just after a command *cancels* it
            // (twelve tries, twelve failures), which is the same window in which querying through
            // the DUT soft start makes the device abandon the close.
            Send(transport, Qa451Protocol.KickLed);
            Send(transport, opcode, argument);

            if (settleMs > 0)
            {
                await Task.Delay(settleMs, ct).ConfigureAwait(false);
            }

            int timeoutMs = settleMs > 0 ? Qa451Protocol.PowerTransitionTimeoutMs : Qa451Protocol.ExchangeTimeoutMs;
            (_, _, ushort status) = Qa451Protocol.DecodeTempsAndStatus(
                Exchange(transport, Qa451Protocol.ReadTempsAndStatus, timeoutMs: timeoutMs));

            if (applied(status))
            {
                return;
            }

            _logger.LogWarning(
                "QA451 did not apply {What} on attempt {Attempt} of {Attempts} (status bits 0x{Status:x3})",
                what,
                attempt,
                Attempts,
                status);
        }

        throw new Qa451CommandRefusedException(
            $"The QA451 did not apply {what} after {Attempts} attempts. A DUT with DC on its output "
                + "makes the device refuse relay control until the DC is removed; otherwise check that "
                + "nothing has tripped and that the unit is still responding.");
    }

    private IHidTransport RequireConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transport is not { IsOpen: true })
        {
            throw new InvalidOperationException("No QA451 is connected.");
        }

        return _transport;
    }

    /// <summary>
    /// Writes one command and reads its reply. Synchronous and called with the gate held: the
    /// exchange is a few milliseconds, and splitting it across an await is what would let a second
    /// caller's reply arrive in the middle of this one.
    /// </summary>
    private static uint Exchange(IHidTransport transport, byte opcode, byte argument = 0, int? timeoutMs = null)
    {
        Span<byte> request = stackalloc byte[Qa451Protocol.ReportLength];
        Qa451Protocol.BuildRequest(request, opcode, argument);
        transport.Write(request);

        Span<byte> reply = stackalloc byte[Qa451Protocol.ReportLength];
        int received = transport.Read(reply, timeoutMs ?? Qa451Protocol.ExchangeTimeoutMs);
        if (received != Qa451Protocol.ReportLength)
        {
            throw new Qa451ProtocolException(
                $"QA451 answered command 0x{opcode:x2} with {received} bytes, expected {Qa451Protocol.ReportLength}.");
        }

        return Qa451Protocol.ParseScalarReply(reply);
    }

    /// <summary>Writes one command that the device does not answer.</summary>
    private static void Send(IHidTransport transport, byte opcode, byte argument = 0)
    {
        Span<byte> request = stackalloc byte[Qa451Protocol.ReportLength];
        Qa451Protocol.BuildRequest(request, opcode, argument);
        transport.Write(request);
    }

    private static string Describe(LoadImpedance impedance) =>
        impedance == LoadImpedance.Open ? "open" : $"{(int)impedance} ohms";

    /// <summary>
    /// Closes the handle. What the <i>hardware</i> then does is the device's own business, and the
    /// user's manual says it protects itself: "if the QA451 application is closed or stops
    /// responding, or if the QA451 is disconnected from the USB, then the QA451 will automatically
    /// open the load relays and disable the load power".
    ///
    /// <para>This file previously claimed the opposite — that an engaged load stays engaged across
    /// the process exiting — reasoned from the vendor application's code, where closing the form
    /// only closes the HID stream. That was reading the host and calling it the device. Not yet
    /// confirmed on the bench either way, so <see cref="SetSafeStateAsync"/> stays the documented
    /// way to put a load away: it is explicit, it is immediate, and it does not depend on firmware
    /// behaviour this app has only read about.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport?.Dispose();
        _transport = null;
        Info = null;
        _gate.Dispose();
    }
}
