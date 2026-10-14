using System.Buffers.Binary;

namespace Tonebench.Device.Hardware;

/// <summary>Which of the two load models is attached. The hardware answers this itself (<see cref="Qa451Protocol.DeviceId"/>).</summary>
internal enum Qa451Model
{
    /// <summary>Original QA451.</summary>
    Qa451,

    /// <summary>QA451B — same product with better current-sense accuracy, and <b>different status bits</b>.</summary>
    Qa451B,
}

/// <summary>
/// Framing, opcodes and decoding for the QA451(B) programmable load. Every constant here is cited
/// to <c>docs/qa451-protocol.md</c>, which records how each one was established — measured from the
/// attached device with a read-only HID probe, or read out of QuantAsylum's shipped application
/// (<c>QA451.exe</c> 1.45, <c>Hardware</c> class) with ILSpy. Nothing is inferred, per CLAUDE.md
/// "Never invent USB protocol details".
///
/// <para>Pure and static so that all of it is testable without a device: the framing, the command
/// pairs and the status decode are where the mistakes would be, and none of them need USB.</para>
/// </summary>
internal static class Qa451Protocol
{
    /// <summary>USB VID — QuantAsylum's usual one, shared with the QA403 (measured).</summary>
    public const int Qa451VendorId = 0x16c0;

    /// <summary>
    /// USB PID (measured: the attached unit reports <c>BusReportedDeviceDesc = QA451</c>). Adjacent
    /// to the QA403's 0x4e39 and nothing like it in transport: this is a plain HID device on
    /// Windows' own HidUsb driver, not a libusb bulk device, so it cannot share
    /// <see cref="Qa40xTransport"/>.
    /// </summary>
    public const int Qa451ProductId = 0x4e35;

    /// <summary>
    /// The one valid HID report id, and it is mandatory on every transfer in both directions
    /// (measured: ids 0 and 1 are refused with ERROR_INVALID_PARAMETER).
    /// </summary>
    public const byte ReportId = 0x3f;

    /// <summary>Report length in both directions, including the leading id byte (measured).</summary>
    public const int ReportLength = 64;

    /// <summary>
    /// Largest payload that fits: 64 bytes less the report id and the length byte. The vendor
    /// application enforces the same limit. Every command this app sends is two bytes.
    /// </summary>
    public const int MaxPayloadLength = 62;

    /// <summary>Per-exchange ceiling, matching the vendor application's read/write timeouts.</summary>
    public const int ExchangeTimeoutMs = 1000;

    /// <summary>
    /// How long the soft start holds 10 Ω in circuit when the DUT switch closes (datasheet).
    /// </summary>
    public const int SoftStartMs = 1000;

    /// <summary>
    /// How long to leave the device alone after a load command, before reading the relays back.
    ///
    /// <para><b>A command that arrives while a relay is committing aborts it.</b> Measured
    /// 2026-10-13: engaging 8 Ω and then sending the LED kick straight away left the load open on
    /// every one of twelve tries, while the identical sequence <i>without</i> that kick engaged at
    /// 2 ms and held for the whole 1.2 s watched. The same thing happens on the DUT switch, where a
    /// host that keeps querying through the soft start can make the device abandon the close
    /// (see <see cref="SoftStartSettleMs"/>) — so this is one behaviour, not two.</para>
    ///
    /// <para>300 ms because that is the gap the relay hardware test has always left between a
    /// command and its read-back, and that test has never seen a relay fail to engage. Held inside
    /// <see cref="Qa451Load"/>'s gate, so nothing else — a status poll above all — can land inside
    /// the window.</para>
    /// </summary>
    public const int RelaySettleMs = 300;

    /// <summary>
    /// How long to leave the device alone after commanding the DUT switch closed, before expecting
    /// it to answer or measuring anything.
    ///
    /// <para>Measured 2026-10-10 (5 V through 47 Ω): the request issued immediately after the
    /// power-on went unanswered for 1005 ms; the next one answered with the switch engaged but
    /// 0.000 A still flowing, and current appeared by 1243 ms. So a close completes in about 1.2 s,
    /// which is the datasheet's 1 s soft start plus the firmware's own sequencing.</para>
    ///
    /// <para>It is a <i>wait</i> and not merely a timeout because the window is fragile: in a run
    /// that kept querying through it with no pause, the switch read engaged at 10 ms and had
    /// released itself by 120 ms — the device evidently abandoned the close. Left alone for the
    /// second, the switch then stays closed <b>with no keepalive at all</b> (verified: 5 s of status
    /// queries and no LED kicks, still engaged and still passing current). So the kick is not a
    /// watchdog, which was this file's first reading of it.</para>
    /// </summary>
    public const int SoftStartSettleMs = 1500;

    /// <summary>
    /// Ceiling for an exchange issued just after a DUT-power change, where the device goes silent
    /// while it sequences the soft start.
    ///
    /// <para>Measured 2026-10-10 against a 5 V supply current-limited to 200 mA: a read issued
    /// immediately after <see cref="DutPowerOn"/> went unanswered for **just over 3 s** — through
    /// the ordinary 1000 ms ceiling and through a 3000 ms one — and then answered normally. The
    /// handle survives it: the device is neither wedged nor reset, which a diagnostic established by
    /// polling the same handle and then opening a second one
    /// (<c>Qa451DutPowerDiagnosticHardwareTests</c>).</para>
    ///
    /// <para>The silence in that measurement ended with the switch reported <b>open</b> — the device
    /// declined to close it into a supply that was current-limiting rather than holding its voltage
    /// up, and set no fault bit doing so. So this ceiling covers the device's own
    /// attempt-and-give-up, which is longer than the <see cref="SoftStartMs"/> a clean close takes;
    /// with nothing wired to S+/S− a close is immediate and the next poll answers at once.</para>
    ///
    /// <para>Hence 6 s rather than 3: the one measurement available sat right on 3 s, and a ceiling
    /// that tight turns a device that is merely slow into a reported failure. A dead device costs
    /// this once, and only in the window after a power command.</para>
    /// </summary>
    public const int PowerTransitionTimeoutMs = 6000;

    /// <summary>Cadence at which the vendor application polls and kicks the LED. See <see cref="KickLed"/>.</summary>
    public const int PollIntervalMs = 1000;

    // ---- opcodes. Payload is always [opcode, argument]; the argument is a fixed per-command
    // constant rather than a parameter. It looks like a guard so that a stray report cannot throw a
    // relay, but the firmware is closed and that is a guess: send these exact pairs and nothing else.

    /// <summary>
    /// Kicks the front-panel LED. The vendor application sends this once a second alongside its
    /// poll, and this app does the same.
    ///
    /// <para><b>The kick is not what holds the DUT switch</b>: measured 2026-10-10, the switch stayed
    /// closed and passing current through 5 s of status queries with no kicks at all. But do not
    /// read that as "nothing holds it", which is this file's earlier and broader claim — the user's
    /// manual says the device opens its relays and disables load power when the application "is
    /// closed or stops responding". Both fit if the firmware watches for <i>any</i> traffic rather
    /// than for this command, and that run was querying throughout. What feeds that watchdog, and
    /// how long it waits, is untested.</para>
    /// </summary>
    public const byte KickLed = 0x00;

    /// <summary>Reads both heatsink temperatures and the status word. See <see cref="DecodeTempsAndStatus"/>.</summary>
    public const byte ReadTempsAndStatus = 0x01;

    /// <summary>Engages the 8 Ω load.</summary>
    public const byte Load8Ohm = 0x02;

    /// <summary>Engages the 4 Ω load.</summary>
    public const byte Load4Ohm = 0x03;

    /// <summary>Releases both loads — the "open" state.</summary>
    public const byte LoadOff = 0x04;

    /// <summary>Closes the high-side switch feeding the DUT's supply, through the soft-start.</summary>
    public const byte DutPowerOn = 0x0c;

    /// <summary>Opens the high-side switch.</summary>
    public const byte DutPowerOff = 0x0d;

    /// <summary>
    /// Resets the device. The vendor application sends this at every connect, which is the only
    /// thing that establishes a known relay state — see <see cref="Qa451Load.ConnectAsync()"/>.
    /// Exactly what it resets (relays only, or the fault latches too) is not known.
    /// </summary>
    public const byte Reset = 0x1c;

    /// <summary>Reads the DC current. Reply is integer milliamps; see <see cref="CurrentAmpsFromRaw"/>.</summary>
    public const byte ReadCurrent = 0x40;

    /// <summary>Reads the model id. See <see cref="ModelFromId"/>.</summary>
    public const byte DeviceId = 0xfc;

    /// <summary>Reads the product serial number as a bare uint32.</summary>
    public const byte SerialNumber = 0xfd;

    /// <summary>Reads the firmware version.</summary>
    public const byte FirmwareVersion = 0xfe;

    /// <summary>
    /// Enters the MSP430 bootstrap loader; the device then re-enumerates as
    /// <see cref="BslVendorId"/>/<see cref="BslProductId"/>. Declared so that nobody has to go
    /// looking for it, and <b>never sent by this app</b> — QuantAsylum's own release notes say an
    /// unrequested reflash can brick the unit.
    /// </summary>
    public const byte EnterBsl = 0xff;

    /// <summary>Guard argument for <see cref="Load8Ohm"/>.</summary>
    public const byte Load8OhmArgument = 0x22;

    /// <summary>Guard argument for <see cref="Load4Ohm"/>.</summary>
    public const byte Load4OhmArgument = 0x33;

    /// <summary>Guard argument for <see cref="LoadOff"/>.</summary>
    public const byte LoadOffArgument = 0x44;

    /// <summary>Guard argument for <see cref="DutPowerOn"/>.</summary>
    public const byte DutPowerOnArgument = 0x53;

    /// <summary>Guard argument for <see cref="DutPowerOff"/>.</summary>
    public const byte DutPowerOffArgument = 0x48;

    /// <summary>Model id answered by a QA451.</summary>
    public const uint Qa451Id = 0xcafe0451;

    /// <summary>Model id answered by a QA451B.</summary>
    public const uint Qa451BId = 0xdead0451;

    /// <summary>Lowest firmware the vendor application accepts on a QA451.</summary>
    public const int Qa451RequiredFirmware = 32;

    /// <summary>Lowest firmware the vendor application accepts on a QA451B.</summary>
    public const int Qa451BRequiredFirmware = 39;

    /// <summary>TI's VID, which the device shows while in its bootstrap loader.</summary>
    public const int BslVendorId = 0x2047;

    /// <summary>PID the device shows while in its bootstrap loader.</summary>
    public const int BslProductId = 0x0200;

    /// <summary>Status bit 3. Over-current (OCP0) on a QA451, over-temperature on a QA451B.</summary>
    private const ushort StatusBit3 = 0x0008;

    /// <summary>Status bit 4. Over-current (OCP1) on a QA451, over-current on a QA451B.</summary>
    private const ushort StatusBit4 = 0x0010;

    /// <summary>
    /// Status bit 0: the DUT high-side switch is closed. Measured on the bench 2026-10-10 — see
    /// <see cref="DecodeRelayState"/>, which is where the whole relay-state reading is explained.
    /// </summary>
    public const ushort StatusDutPowerOn = 0x0001;

    /// <summary>Status bit 1: the 4 Ω load is engaged (measured).</summary>
    public const ushort StatusLoad4Ohm = 0x0002;

    /// <summary>Status bit 2: the 8 Ω load is engaged (measured).</summary>
    public const ushort StatusLoad8Ohm = 0x0004;

    /// <summary>
    /// Status bit 6, set on every reading taken from a healthy idle QA451B and unchanged by any
    /// command. Named for what it is useful for — telling a reader that a non-zero status word is
    /// normal — and not for what it means, which is still unknown.
    /// </summary>
    public const ushort StatusAlwaysSet = 0x0040;

    /// <summary>
    /// Fills <paramref name="report"/> with one request. The buffer must be exactly
    /// <see cref="ReportLength"/>, and it is cleared first: the padding past the payload goes out on
    /// the wire, so leaving a previous command's bytes in it would send stale data the firmware has
    /// never been shown.
    /// </summary>
    public static void BuildRequest(Span<byte> report, byte opcode, byte argument)
    {
        if (report.Length != ReportLength)
        {
            throw new ArgumentException($"A QA451 report is exactly {ReportLength} bytes.", nameof(report));
        }

        report.Clear();
        report[0] = ReportId;
        report[1] = 2; // payload length: every command this app sends is [opcode, argument]
        report[2] = opcode;
        report[3] = argument;
    }

    /// <summary>
    /// Reads the uint32 a reply carries. Every command that answers at all answers with a four-byte
    /// little-endian payload.
    /// </summary>
    /// <exception cref="Qa451ProtocolException">
    /// If the report is not the expected length, does not carry <see cref="ReportId"/>, or declares
    /// a payload too short to hold the value. The report id is checked rather than assumed: Windows
    /// and Linux hidraw both include it for a device with numbered reports, so a failure here means
    /// that assumption has broken on some platform and wants investigating, not papering over.
    /// </exception>
    public static uint ParseScalarReply(ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportLength)
        {
            throw new Qa451ProtocolException($"Expected a {ReportLength}-byte reply, got {report.Length}.");
        }

        if (report[0] != ReportId)
        {
            throw new Qa451ProtocolException(
                $"Reply carries report id 0x{report[0]:x2}, expected 0x{ReportId:x2}. "
                    + "Either this is not a QA451 reply, or the platform's HID layer has stripped the "
                    + "report id byte (see docs/qa451-protocol.md).");
        }

        int payloadLength = report[1];
        if (payloadLength < sizeof(uint))
        {
            throw new Qa451ProtocolException(
                $"Reply declares a {payloadLength}-byte payload, too short for a uint32.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(2, sizeof(uint)));
    }

    /// <summary>
    /// Splits the <see cref="ReadTempsAndStatus"/> reply: the right-channel temperature in bits
    /// 0..7 and the left in bits 8..15, both <b>signed</b> bytes in whole degrees, and nine status
    /// bits from bit 16.
    ///
    /// <para>All nine status bits are kept. The vendor application masks nine and then casts to
    /// <c>byte</c>, so bit 8 never reaches its own UI; only two bits are understood here either way,
    /// and the rest are worth logging rather than discarding.</para>
    /// </summary>
    public static (int LeftTemperatureC, int RightTemperatureC, ushort Status) DecodeTempsAndStatus(uint raw)
    {
        int left = (sbyte)((raw >> 8) & 0xff);
        int right = (sbyte)(raw & 0xff);
        ushort status = (ushort)((raw >> 16) & 0x1ff);
        return (left, right, status);
    }

    /// <summary>
    /// Converts a <see cref="ReadCurrent"/> reply (integer milliamps) to amps.
    ///
    /// <para><b>The reply is signed.</b> Read as a <c>uint</c> it was right for every current this
    /// bench had ever drawn and absurd for the one it had not: with nothing on the DUT supply the
    /// device answered <c>0xFFFFFFD2</c> — −46 mA, the sense amplifier's own offset, inside the
    /// ~48.5 mA quantum that is this reading's whole resolution — and the status bar read
    /// <b>4294967.25 A</b>. (That figure is the tell, and it is exact: 2³² − 46 milliamps.) A small
    /// negative reading is reported as measured rather than clamped to zero: it is what the
    /// hardware said, it is within one quantum of nothing, and a floor would quietly hide a sense
    /// resistor wired backwards — which reads as a steady, *large* negative current.</para>
    /// </summary>
    public static double CurrentAmpsFromRaw(uint raw) => unchecked((int)raw) / 1000.0;

    /// <summary>
    /// Decodes the two known status bits, which <b>mean different things on the two models</b> — the
    /// reason <see cref="Qa451Load"/> reads the device id before it reads anything else.
    ///
    /// <para>A QA451 reports two over-current trips (OCP0 on bit 3, OCP1 on bit 4) and no
    /// temperature flag. A QA451B reports over-temperature on bit 3 and over-current on bit 4.
    /// Reading one model's bits with the other's table turns a hot heatsink into a phantom
    /// over-current, or hides a real one.</para>
    ///
    /// <para><paramref name="requiresReplug"/> is true for the faults that latch in hardware: every
    /// over-current trip, on either model. There is no clear-fault command, and the vendor
    /// application's own answer is to stop polling and print "Replug". An over-temperature on a
    /// QA451B does not latch — it clears when the heatsink cools.</para>
    /// </summary>
    public static (LoadFaults Faults, bool requiresReplug) DecodeFaults(Qa451Model model, ushort status)
    {
        LoadFaults faults = LoadFaults.None;
        bool latched = false;

        switch (model)
        {
            case Qa451Model.Qa451:
                if ((status & StatusBit3) != 0 || (status & StatusBit4) != 0)
                {
                    faults |= LoadFaults.OverCurrent;
                    latched = true;
                }

                break;

            case Qa451Model.Qa451B:
                if ((status & StatusBit3) != 0)
                {
                    faults |= LoadFaults.OverTemperature;
                }

                if ((status & StatusBit4) != 0)
                {
                    faults |= LoadFaults.OverCurrent;
                    latched = true;
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown load model.");
        }

        return (faults, latched);
    }

    /// <summary>
    /// Reads the relay and DUT-switch state out of the status word — <b>so the hardware can be
    /// asked what it is doing</b>, which is the one thing about this device that its own vendor
    /// application appears not to know: that application tracks its buttons instead, which is why
    /// its REST <c>GET /Settings/Impedance</c> answers from the UI rather than from the device.
    ///
    /// <para>Measured 2026-10-10 on a QA451B with bare terminals, one command per step: engaging
    /// 8 Ω set bit 2 and nothing else, 4 Ω set bit 1 and cleared bit 2, open cleared both, and the
    /// DUT switch moved bit 0 independently of either load bit. The bits are therefore independent
    /// flags and not a code — confirmed by driving a load and the supply switch at the same time,
    /// which sets both bits at once.</para>
    ///
    /// <para>Both load bits set at once is not a state this app commands, so it is reported as
    /// <c>null</c> — unknown — rather than resolved to a guess. A poll taken inside a relay's own
    /// settle time can still report the previous state; that is a race with the hardware, not a
    /// disagreement worth resolving here.</para>
    /// </summary>
    public static (LoadImpedance? Impedance, bool DutPowerOn) DecodeRelayState(ushort status)
    {
        bool fourOhm = (status & StatusLoad4Ohm) != 0;
        bool eightOhm = (status & StatusLoad8Ohm) != 0;

        LoadImpedance? impedance = (fourOhm, eightOhm) switch
        {
            (false, false) => LoadImpedance.Open,
            (true, false) => LoadImpedance.FourOhm,
            (false, true) => LoadImpedance.EightOhm,
            (true, true) => null,
        };

        return (impedance, (status & StatusDutPowerOn) != 0);
    }

    /// <summary>The opcode/argument pair that selects a load setting.</summary>
    public static (byte Opcode, byte Argument) ImpedanceCommand(LoadImpedance impedance) => impedance switch
    {
        LoadImpedance.Open => (LoadOff, LoadOffArgument),
        LoadImpedance.FourOhm => (Load4Ohm, Load4OhmArgument),
        LoadImpedance.EightOhm => (Load8Ohm, Load8OhmArgument),
        _ => throw new ArgumentOutOfRangeException(nameof(impedance), impedance, "A QA451 presents open, 4 or 8 ohms."),
    };

    /// <summary>The opcode/argument pair that switches the DUT supply.</summary>
    public static (byte Opcode, byte Argument) DutPowerCommand(bool on) =>
        on ? (DutPowerOn, DutPowerOnArgument) : (DutPowerOff, DutPowerOffArgument);

    /// <summary>
    /// Maps a <see cref="DeviceId"/> reply to a model, or throws. An unrecognised id is refused
    /// rather than defaulted: the status bits are model-specific, so guessing the model would
    /// silently misreport faults on whatever this turned out to be.
    /// </summary>
    public static Qa451Model ModelFromId(uint id) => id switch
    {
        Qa451Id => Qa451Model.Qa451,
        Qa451BId => Qa451Model.Qa451B,
        _ => throw new Qa451ProtocolException(
            $"Attached load answered device id 0x{id:x8}, which is neither a QA451 (0x{Qa451Id:x8}) "
                + $"nor a QA451B (0x{Qa451BId:x8})."),
    };

    /// <summary>The lowest firmware the vendor application accepts for a model.</summary>
    public static int RequiredFirmwareFor(Qa451Model model) => model switch
    {
        Qa451Model.Qa451 => Qa451RequiredFirmware,
        Qa451Model.Qa451B => Qa451BRequiredFirmware,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown load model."),
    };

    /// <summary>The model name as the hardware reports itself, for display and logging.</summary>
    public static string ModelName(Qa451Model model) => model switch
    {
        Qa451Model.Qa451 => "QA451",
        Qa451Model.Qa451B => "QA451B",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown load model."),
    };
}
