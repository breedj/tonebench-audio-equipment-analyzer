using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// A scripted stand-in for the device's HID endpoint: it records every report written and answers
/// whatever the test has scripted for the opcode of the most recent write. Keyed on the opcode
/// rather than on a queue, so a test that gets the command order wrong fails by saying which
/// command had no answer instead of silently reading the previous one's.
///
/// <para>It also keeps a small <b>relay model</b>, because the driver now reads the status word back
/// to confirm a state command took effect — a blind write is unreliable on this device. Writes it
/// accepts move the model; <see cref="DropNextWrites"/> makes it swallow some, which is how the
/// measured "first report after idle is dropped" behaviour is reproduced. A test that scripts
/// <see cref="Qa451Protocol.ReadTempsAndStatus"/> explicitly overrides the model, so the fault tests
/// still say exactly what the device reports.</para>
/// </summary>
internal sealed class FakeHidTransport : IHidTransport
{
    /// <summary>Opcodes that change the device's state, as opposed to asking it something.</summary>
    private static readonly byte[] StateOpcodes =
    [
        Qa451Protocol.Load8Ohm, Qa451Protocol.Load4Ohm, Qa451Protocol.LoadOff,
        Qa451Protocol.DutPowerOn, Qa451Protocol.DutPowerOff, Qa451Protocol.Reset,
    ];

    private LoadImpedance _modelledImpedance = LoadImpedance.Open;
    private bool _modelledPower;

    public List<byte[]> Written { get; } = [];

    public Dictionary<byte, uint> Replies { get; } = [];

    /// <summary>How many further writes to record but not apply — a dropped output report.</summary>
    public int DropNextWrites { get; set; }

    /// <summary>The (opcode, argument) pairs of the writes that change state, in order.</summary>
    public IReadOnlyList<(byte Opcode, byte Argument)> StateCommands =>
        [.. Written.Where(r => StateOpcodes.Contains(r[2])).Select(r => (r[2], r[3]))];

    public Exception? OpenFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    public Exception? ReadFailure { get; set; }

    public int ShortReadLength { get; set; }

    public bool IsOpen { get; private set; }

    public bool Disposed { get; private set; }

    public string? UsbSerial { get; set; } = "C39491461B001B00";

    private byte _lastOpcode;

    /// <summary>The opcodes written so far, in order — what most of these tests assert on.</summary>
    public IReadOnlyList<byte> Opcodes => Written.Select(report => report[2]).ToList();

    /// <summary>The (opcode, argument) pair of the n-th report written.</summary>
    public (byte Opcode, byte Argument) Command(int index) => (Written[index][2], Written[index][3]);

    public void Open()
    {
        if (OpenFailure is not null)
        {
            throw OpenFailure;
        }

        IsOpen = true;
    }

    public void Write(ReadOnlySpan<byte> report)
    {
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        Written.Add(report.ToArray());
        _lastOpcode = report[2];

        if (DropNextWrites > 0)
        {
            DropNextWrites--;
            return;
        }

        switch (report[2])
        {
            case Qa451Protocol.Load8Ohm: _modelledImpedance = LoadImpedance.EightOhm; break;
            case Qa451Protocol.Load4Ohm: _modelledImpedance = LoadImpedance.FourOhm; break;
            case Qa451Protocol.LoadOff: _modelledImpedance = LoadImpedance.Open; break;
            case Qa451Protocol.DutPowerOn: _modelledPower = true; break;
            case Qa451Protocol.DutPowerOff: _modelledPower = false; break;
            case Qa451Protocol.Reset:
                _modelledImpedance = LoadImpedance.Open;
                _modelledPower = false;
                break;
            default: break;
        }
    }

    public int Read(Span<byte> report, int timeoutMs)
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        if (!Replies.TryGetValue(_lastOpcode, out uint value))
        {
            // The relay model answers a status query a test has not scripted, so a test about
            // commands does not have to script the reads those commands now make.
            if (_lastOpcode == Qa451Protocol.ReadTempsAndStatus)
            {
                value = ModelledStatus();
            }
            else
            {
                throw new InvalidOperationException(
                    $"The test scripted no reply for command 0x{_lastOpcode:x2}.");
            }
        }

        report.Clear();
        report[0] = Qa451Protocol.ReportId;
        report[1] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(report.Slice(2, 4), value);
        return ShortReadLength > 0 ? ShortReadLength : Qa451Protocol.ReportLength;
    }

    public void Dispose() => Disposed = true;

    /// <summary>The status word this model's state would produce, room temperature and no faults.</summary>
    private uint ModelledStatus()
    {
        ushort status = Qa451Protocol.StatusAlwaysSet;
        if (_modelledPower)
        {
            status |= Qa451Protocol.StatusDutPowerOn;
        }

        status |= _modelledImpedance switch
        {
            LoadImpedance.FourOhm => Qa451Protocol.StatusLoad4Ohm,
            LoadImpedance.EightOhm => Qa451Protocol.StatusLoad8Ohm,
            _ => (ushort)0,
        };

        return ((uint)status << 16) | (20u << 8) | 21u;
    }
}

/// <summary>
/// The driver's sequencing and state, over <see cref="FakeHidTransport"/>. What is being pinned here
/// is everything that would otherwise only be checkable against hardware that switches relays into
/// an amplifier.
/// </summary>
public class Qa451LoadTests
{
    // No soft-start wait: a scripted transport has none to wait for, and the real 1.5 s belongs to
    // the hardware tests.
    private static Qa451Load Load(FakeHidTransport transport) =>
        new(NullLogger<Qa451Load>.Instance, () => transport, softStartSettleMs: 0);

    private static FakeHidTransport Connected(uint deviceId = Qa451Protocol.Qa451BId, int firmware = 39)
    {
        var transport = new FakeHidTransport();
        transport.Replies[Qa451Protocol.DeviceId] = deviceId;
        transport.Replies[Qa451Protocol.FirmwareVersion] = (uint)firmware;
        transport.Replies[Qa451Protocol.SerialNumber] = 123456;
        return transport;
    }

    private static uint TempsAndStatus(int left, int right, ushort status) =>
        ((uint)status << 16) | ((uint)(byte)(sbyte)left << 8) | (byte)(sbyte)right;

    [Fact]
    public async Task Connect_ReadsTheIdentityThenResetsToAKnownState()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);

        await load.ConnectAsync();

        Assert.Equal(
            [Qa451Protocol.DeviceId, Qa451Protocol.FirmwareVersion, Qa451Protocol.SerialNumber, Qa451Protocol.Reset],
            transport.Opcodes);
        Assert.True(load.IsConnected);
        Assert.Equal("QA451B", load.Info!.Model);
        Assert.Equal(39, load.Info.FirmwareVersion);
        Assert.Equal(39, load.Info.RequiredFirmwareVersion);
        Assert.True(load.Info.FirmwareIsSupported);
        Assert.Equal(123456u, load.Info.SerialNumber);
        Assert.Equal("C39491461B001B00", load.Info.UsbSerial);
        Assert.Equal(LoadImpedance.Open, load.Impedance);
        Assert.False(load.DutPowerOn);
    }

    /// <summary>
    /// A QA451 cannot be asked what its relays are doing, so a connect that deliberately does not
    /// reset must report the state as unknown rather than assume the safe one. Assuming would be
    /// worse than useless: the device keeps an engaged load across the exit of whatever set it.
    /// </summary>
    [Fact]
    public async Task Connect_WithoutTheReset_LeavesTheStateUnknownRatherThanGuessing()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);

        await load.ConnectAsync(resetToKnownState: false);

        Assert.DoesNotContain(Qa451Protocol.Reset, transport.Opcodes);
        Assert.Null(load.Impedance);
        Assert.Null(load.DutPowerOn);
        Assert.True(load.IsConnected);
    }

    [Fact]
    public async Task Connect_ToAnUnrecognisedDevice_IsRefusedAndReleasesTheHandle()
    {
        FakeHidTransport transport = Connected(deviceId: 0x12345678);
        using Qa451Load load = Load(transport);

        await Assert.ThrowsAsync<Qa451ProtocolException>(() => load.ConnectAsync());

        Assert.True(transport.Disposed);
        Assert.False(load.IsConnected);
        Assert.Null(load.Info);
    }

    [Fact]
    public async Task Connect_WhenNothingIsAttached_SaysSo()
    {
        FakeHidTransport transport = Connected();
        transport.OpenFailure = new Qa451DeviceNotFoundException("nothing attached");
        using Qa451Load load = Load(transport);

        await Assert.ThrowsAsync<Qa451DeviceNotFoundException>(() => load.ConnectAsync());

        Assert.False(load.IsConnected);
    }

    [Fact]
    public async Task Connect_OnStaleFirmware_StillConnects()
    {
        // Nothing here flashes firmware: QuantAsylum's own notes say an unrequested reflash can
        // brick the unit, so a below-par version is reported and worked with.
        FakeHidTransport transport = Connected(firmware: 30);
        using Qa451Load load = Load(transport);

        await load.ConnectAsync();

        Assert.True(load.IsConnected);
        Assert.False(load.Info!.FirmwareIsSupported);
        Assert.DoesNotContain(Qa451Protocol.EnterBsl, transport.Opcodes);
    }

    [Theory]
    [InlineData(LoadImpedance.Open, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument)]
    [InlineData(LoadImpedance.FourOhm, Qa451Protocol.Load4Ohm, Qa451Protocol.Load4OhmArgument)]
    [InlineData(LoadImpedance.EightOhm, Qa451Protocol.Load8Ohm, Qa451Protocol.Load8OhmArgument)]
    public async Task SetImpedance_WritesTheGuardedPairAndRemembersIt(
        LoadImpedance impedance, byte opcode, byte argument)
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();

        await load.SetImpedanceAsync(impedance);

        // The last state command, not the next write: each command is now preceded by the LED kick
        // that tells the device the host is alive, and followed by the status read that confirms the
        // relays moved.
        Assert.Equal((opcode, argument), transport.StateCommands[^1]);
        Assert.Equal(impedance, load.Impedance);
    }

    [Fact]
    public async Task SetDutPower_WritesTheGuardedPairAndRemembersIt()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();

        await load.SetDutPowerAsync(on: true);
        await load.SetDutPowerAsync(on: false);

        // The state commands rather than adjacent writes: each one is now followed by the status
        // read that confirms it took effect.
        Assert.Equal(
            [
                (Qa451Protocol.Reset, (byte)0),
                (Qa451Protocol.DutPowerOn, Qa451Protocol.DutPowerOnArgument),
                (Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument),
            ],
            transport.StateCommands);
        Assert.False(load.DutPowerOn);
    }

    [Fact]
    public async Task Read_KicksTheLedThenReadsTemperaturesAndCurrent()
    {
        FakeHidTransport transport = Connected();
        transport.Replies[Qa451Protocol.ReadTempsAndStatus] = TempsAndStatus(left: 31, right: -4, status: 0);
        transport.Replies[Qa451Protocol.ReadCurrent] = 1234;
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();
        int before = transport.Written.Count;

        LoadReading reading = await load.ReadAsync();

        Assert.Equal(
            [Qa451Protocol.KickLed, Qa451Protocol.ReadTempsAndStatus, Qa451Protocol.ReadCurrent],
            transport.Opcodes.Skip(before));
        Assert.Equal(31, reading.LeftTemperatureC);
        Assert.Equal(-4, reading.RightTemperatureC);
        Assert.Equal(1.234, reading.CurrentA, 9);
        Assert.Equal(LoadFaults.None, reading.Faults);
        Assert.False(reading.RequiresReplug);
        Assert.Equal(LoadImpedance.Open, reading.EngagedImpedance);
        Assert.False(reading.DutPowerEngaged);
        Assert.Same(reading, load.LastReading);
    }

    /// <summary>
    /// The relay positions are in the status word (measured on the bench), so a poll is what makes
    /// <see cref="Qa451Load.Impedance"/> the hardware's answer rather than this app's bookkeeping —
    /// including after a connect that deliberately did not reset, where nothing else could resolve
    /// it.
    /// </summary>
    [Fact]
    public async Task Read_TakesTheRelayStateFromTheHardware_NotFromItsOwnBookkeeping()
    {
        FakeHidTransport transport = Connected();
        transport.Replies[Qa451Protocol.ReadTempsAndStatus] = TempsAndStatus(
            20, 20, status: Qa451Protocol.StatusAlwaysSet | Qa451Protocol.StatusLoad8Ohm | Qa451Protocol.StatusDutPowerOn);
        transport.Replies[Qa451Protocol.ReadCurrent] = 0;
        using Qa451Load load = Load(transport);
        await load.ConnectAsync(resetToKnownState: false);

        Assert.Null(load.Impedance);
        Assert.Null(load.DutPowerOn);

        LoadReading reading = await load.ReadAsync();

        Assert.Equal(LoadImpedance.EightOhm, reading.EngagedImpedance);
        Assert.True(reading.DutPowerEngaged);
        Assert.Equal(LoadImpedance.EightOhm, load.Impedance);
        Assert.True(load.DutPowerOn);
    }

    /// <summary>
    /// A poll believes the hardware over this app's bookkeeping. Here the device reports a load the
    /// app never commanded — which on a bench means someone pressed the vendor application's
    /// buttons, or a previous session left it engaged and the connect skipped its reset.
    /// </summary>
    [Fact]
    public async Task Read_WhenTheHardwareDisagreesWithWhatWasCommanded_BelievesTheHardware()
    {
        FakeHidTransport transport = Connected();
        transport.Replies[Qa451Protocol.ReadTempsAndStatus] = TempsAndStatus(
            20, 20, status: Qa451Protocol.StatusAlwaysSet | Qa451Protocol.StatusLoad8Ohm);
        transport.Replies[Qa451Protocol.ReadCurrent] = 0;
        using Qa451Load load = Load(transport);
        await load.ConnectAsync(resetToKnownState: false);

        await load.ReadAsync();

        Assert.Equal(LoadImpedance.EightOhm, load.Impedance);
    }

    /// <summary>
    /// Both load bits at once is not a state this app commands, so it is reported as unknown rather
    /// than resolved to whichever one looked more likely — and the remembered setting is left alone.
    /// </summary>
    [Fact]
    public async Task Read_WithBothLoadBitsSet_ReportsTheEngagedLoadAsUnknown()
    {
        FakeHidTransport transport = Connected();
        transport.Replies[Qa451Protocol.ReadTempsAndStatus] = TempsAndStatus(
            20, 20, status: Qa451Protocol.StatusLoad4Ohm | Qa451Protocol.StatusLoad8Ohm);
        transport.Replies[Qa451Protocol.ReadCurrent] = 0;
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();

        LoadReading reading = await load.ReadAsync();

        Assert.Null(reading.EngagedImpedance);

        // The remembered setting is left alone rather than overwritten with a guess: the reset at
        // connect is what put it at Open.
        Assert.Equal(LoadImpedance.Open, load.Impedance);
    }

    /// <summary>
    /// The decode has to follow the model the device reported, not a default — the same bit is an
    /// over-temperature on one model and a latched over-current on the other.
    /// </summary>
    [Fact]
    public async Task Read_OnAQa451B_ReadsBitThreeAsAnOverTemperatureThatDoesNotLatch()
    {
        FakeHidTransport transport = Connected(deviceId: Qa451Protocol.Qa451BId);
        transport.Replies[Qa451Protocol.ReadTempsAndStatus] = TempsAndStatus(95, 94, status: 0x0008);
        transport.Replies[Qa451Protocol.ReadCurrent] = 0;
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();

        LoadReading reading = await load.ReadAsync();

        Assert.Equal(LoadFaults.OverTemperature, reading.Faults);
        Assert.False(reading.RequiresReplug);
    }

    [Fact]
    public async Task Read_OnAQa451_ReadsBitThreeAsAnOverCurrentThatLatches()
    {
        FakeHidTransport transport = Connected(deviceId: Qa451Protocol.Qa451Id, firmware: 32);
        transport.Replies[Qa451Protocol.ReadTempsAndStatus] = TempsAndStatus(31, 30, status: 0x0008);
        transport.Replies[Qa451Protocol.ReadCurrent] = 0;
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();

        LoadReading reading = await load.ReadAsync();

        Assert.Equal(LoadFaults.OverCurrent, reading.Faults);
        Assert.True(reading.RequiresReplug);
    }

    [Fact]
    public async Task Read_CarriesTheWholeStatusWordForTheBitsNobodyUnderstandsYet()
    {
        FakeHidTransport transport = Connected();
        transport.Replies[Qa451Protocol.ReadTempsAndStatus] = TempsAndStatus(31, 30, status: 0x0141);
        transport.Replies[Qa451Protocol.ReadCurrent] = 0;
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();

        LoadReading reading = await load.ReadAsync();

        Assert.Equal(0x0141, reading.RawStatusBits);
    }

    /// <summary>
    /// A load that has tripped is reported, not hidden behind a refusal: nothing establishes that
    /// the firmware ignores commands while latched, and inventing that rule here would turn a fault
    /// into an exception about something else. Refusing is the app's decision to make.
    /// </summary>
    [Fact]
    public async Task AfterALatchedFault_CommandsAreStillSentAndTheFaultIsStillVisible()
    {
        FakeHidTransport transport = Connected();
        transport.Replies[Qa451Protocol.ReadTempsAndStatus] = TempsAndStatus(31, 30, status: 0x0010);
        transport.Replies[Qa451Protocol.ReadCurrent] = 0;
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();
        await load.ReadAsync();

        await load.SetImpedanceAsync(LoadImpedance.Open);

        Assert.Equal((Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument), transport.StateCommands[^1]);
        Assert.True(load.LastReading!.RequiresReplug);
    }

    /// <summary>
    /// A timeout is a hiccup on a device that is still there, following the QA403's rule — so it
    /// surfaces as a timeout and leaves the session up, rather than tearing it down.
    /// </summary>
    [Fact]
    public async Task Read_WhenNoReplyArrives_IsATimeoutAndTheDeviceStaysConnected()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();
        transport.ReadFailure = new TimeoutException("no report");

        await Assert.ThrowsAsync<TimeoutException>(() => load.ReadAsync());

        Assert.True(load.IsConnected);
    }

    [Fact]
    public async Task Read_WhenTheHandleFails_SaysTheDeviceIsGone()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();
        transport.ReadFailure = new Qa451DeviceLostException("unplugged");

        await Assert.ThrowsAsync<Qa451DeviceLostException>(() => load.ReadAsync());
    }

    [Fact]
    public async Task Read_WithATruncatedReply_IsAProtocolFailure()
    {
        FakeHidTransport transport = Connected();
        transport.Replies[Qa451Protocol.ReadTempsAndStatus] = 0;
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();
        transport.ShortReadLength = 8;

        await Assert.ThrowsAsync<Qa451ProtocolException>(() => load.ReadAsync());
    }

    /// <summary>
    /// The load is released before the supply is cut, which is the ordinary end of a measurement:
    /// cutting the supply first would leave an amplifier driving a load as its rails collapse.
    /// </summary>
    [Fact]
    public async Task SetSafeState_ReleasesTheLoadAndThenCutsTheDutSupply()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();
        await load.SetImpedanceAsync(LoadImpedance.FourOhm);
        await load.SetDutPowerAsync(on: true);
        int before = transport.StateCommands.Count;

        await load.SetSafeStateAsync();

        Assert.Equal(
            [
                (Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument),
                (Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument),
            ],
            transport.StateCommands.Skip(before));
        Assert.Equal(LoadImpedance.Open, load.Impedance);
        Assert.False(load.DutPowerOn);
    }

    /// <summary>
    /// Dispose closes the handle and touches nothing else. The hardware keeps an engaged load and a
    /// switched DUT supply across this process exiting, so putting the bench away has to be a
    /// deliberate call to <see cref="Qa451Load.SetSafeStateAsync"/> — this test is the record of
    /// that decision, not an accident of the implementation.
    /// </summary>
    [Fact]
    public async Task Dispose_ClosesTheHandleWithoutChangingTheHardwaresState()
    {
        FakeHidTransport transport = Connected();
        Qa451Load load = Load(transport);
        await load.ConnectAsync();
        await load.SetImpedanceAsync(LoadImpedance.EightOhm);
        int before = transport.Written.Count;

        load.Dispose();

        Assert.Equal(before, transport.Written.Count);
        Assert.True(transport.Disposed);
        Assert.False(load.IsConnected);
    }

    [Fact]
    public async Task AnyCommand_BeforeConnect_IsRefused()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => load.SetImpedanceAsync(LoadImpedance.FourOhm));
        await Assert.ThrowsAsync<InvalidOperationException>(() => load.SetDutPowerAsync(on: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => load.ReadAsync());
        Assert.Empty(transport.Written);
    }

    /// <summary>
    /// The measured behaviour this verification exists for: the first output report after an idle
    /// period is dropped, and a state command carries no acknowledgement, so the only way to know is
    /// to read the relay bits back. One retry is the whole fix.
    /// </summary>
    [Fact]
    public async Task ACommandTheDeviceDrops_IsRetriedUntilTheStatusAgrees()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();

        // Two, because the kick now goes out first: dropping one would only lose the kick and the
        // command itself would land.
        transport.DropNextWrites = 2;

        await load.SetImpedanceAsync(LoadImpedance.EightOhm);

        Assert.Equal(LoadImpedance.EightOhm, load.Impedance);

        Assert.Equal(
            [
                (Qa451Protocol.Load8Ohm, Qa451Protocol.Load8OhmArgument),
                (Qa451Protocol.Load8Ohm, Qa451Protocol.Load8OhmArgument),
            ],
            transport.StateCommands.Skip(1));
    }

    /// <summary>
    /// And when retrying does not help — DC on a load input makes the device refuse relay control
    /// outright, with no fault bit set — the app says so rather than reporting a load that is not
    /// engaged.
    /// </summary>
    [Fact]
    public async Task ACommandTheDeviceKeepsRefusing_IsReportedAndDoesNotChangeTheRememberedState()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();
        transport.DropNextWrites = 500; // enough that the reopen-and-retry cannot recover either

        Qa451CommandRefusedException ex = await Assert.ThrowsAsync<Qa451CommandRefusedException>(
            () => load.SetImpedanceAsync(LoadImpedance.FourOhm));

        Assert.Contains("DC", ex.Message, StringComparison.Ordinal);
        Assert.Equal(LoadImpedance.Open, load.Impedance);
    }

    [Fact]
    public async Task ADutPowerCommandTheDeviceDrops_IsAlsoRetried()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();
        transport.DropNextWrites = 1;

        await load.SetDutPowerAsync(on: true);

        Assert.True(load.DutPowerOn);
    }

    [Fact]
    public async Task Connect_Twice_IsRefused()
    {
        FakeHidTransport transport = Connected();
        using Qa451Load load = Load(transport);
        await load.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => load.ConnectAsync());
    }

    [Fact]
    public async Task EveryReportWritten_IsOneWholeFramedReport()
    {
        // No scripted status: the fake's relay model answers, so the commands below can verify
        // themselves the way they do against hardware.
        FakeHidTransport transport = Connected();
        transport.Replies[Qa451Protocol.ReadCurrent] = 0;
        using Qa451Load load = Load(transport);

        await load.ConnectAsync();
        await load.SetImpedanceAsync(LoadImpedance.EightOhm);
        await load.ReadAsync();

        Assert.All(transport.Written, report =>
        {
            Assert.Equal(Qa451Protocol.ReportLength, report.Length);
            Assert.Equal(Qa451Protocol.ReportId, report[0]);
            Assert.Equal(2, report[1]);
        });
    }
}
