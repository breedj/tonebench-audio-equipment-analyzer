using System.Buffers.Binary;
using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// The framing, command pairs and decoding of the QA451 load, pinned against
/// <c>docs/qa451-protocol.md</c>. All of it is reachable without a device, which is the point of
/// keeping <see cref="Qa451Protocol"/> pure.
/// </summary>
public class Qa451ProtocolTests
{
    private static byte[] Report() => new byte[Qa451Protocol.ReportLength];

    private static byte[] ScalarReply(uint value, byte reportId = Qa451Protocol.ReportId, byte payloadLength = 4)
    {
        byte[] reply = Report();
        reply[0] = reportId;
        reply[1] = payloadLength;
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(2, 4), value);
        return reply;
    }

    [Fact]
    public void BuildRequest_FramesTheReportIdTheLengthAndThePayload()
    {
        byte[] report = Report();

        Qa451Protocol.BuildRequest(report, Qa451Protocol.Load4Ohm, Qa451Protocol.Load4OhmArgument);

        Assert.Equal(Qa451Protocol.ReportId, report[0]);
        Assert.Equal(2, report[1]);
        Assert.Equal(Qa451Protocol.Load4Ohm, report[2]);
        Assert.Equal(Qa451Protocol.Load4OhmArgument, report[3]);
        Assert.All(report[4..], b => Assert.Equal(0, b));
    }

    /// <summary>
    /// The padding past the payload goes out on the wire, so a reused buffer must be cleared — a
    /// previous command's bytes would otherwise be sent as data the firmware has never been shown.
    /// </summary>
    [Fact]
    public void BuildRequest_ClearsWhateverWasInTheBuffer()
    {
        byte[] report = Report();
        Array.Fill(report, (byte)0xaa);

        Qa451Protocol.BuildRequest(report, Qa451Protocol.KickLed, 0);

        Assert.Equal(Qa451Protocol.ReportId, report[0]);
        Assert.Equal(2, report[1]);
        Assert.All(report[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildRequest_RefusesABufferThatIsNotOneWholeReport()
    {
        Assert.Throws<ArgumentException>(() => Qa451Protocol.BuildRequest(new byte[8], 0x01, 0));
    }

    [Fact]
    public void ParseScalarReply_ReadsTheLittleEndianUint32FromThePayload()
    {
        Assert.Equal(0xcafe0451u, Qa451Protocol.ParseScalarReply(ScalarReply(0xcafe0451u)));
    }

    [Fact]
    public void ParseScalarReply_WithTheWrongReportId_IsRefused()
    {
        var ex = Assert.Throws<Qa451ProtocolException>(
            () => Qa451Protocol.ParseScalarReply(ScalarReply(1, reportId: 0x00)));

        Assert.Contains("report id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseScalarReply_WithAPayloadTooShortForTheValue_IsRefused()
    {
        Assert.Throws<Qa451ProtocolException>(() => Qa451Protocol.ParseScalarReply(ScalarReply(1, payloadLength: 3)));
    }

    [Fact]
    public void ParseScalarReply_WithAShortReport_IsRefused()
    {
        Assert.Throws<Qa451ProtocolException>(() => Qa451Protocol.ParseScalarReply(new byte[16]));
    }

    /// <summary>Both temperatures are signed bytes, and a cold bench really does report negatives.</summary>
    [Fact]
    public void DecodeTempsAndStatus_SplitsTheTwoSignedTemperatures()
    {
        uint raw = ((uint)unchecked((byte)(sbyte)-5) << 8) | 42u;

        (int left, int right, _) = Qa451Protocol.DecodeTempsAndStatus(raw);

        Assert.Equal(-5, left);
        Assert.Equal(42, right);
    }

    /// <summary>
    /// The regression this test exists for: the vendor application masks nine status bits and then
    /// casts to <c>byte</c>, so bit 8 never reaches its own UI. Only two bits are understood here,
    /// which is exactly why the rest have to survive the decode.
    /// </summary>
    [Fact]
    public void DecodeTempsAndStatus_KeepsAllNineStatusBits()
    {
        uint raw = 0x1ffu << 16;

        (_, _, ushort status) = Qa451Protocol.DecodeTempsAndStatus(raw);

        Assert.Equal(0x1ff, status);
        Assert.NotEqual(0, status & 0x100);
    }

    [Fact]
    public void DecodeTempsAndStatus_IgnoresBitsAboveTheStatusField()
    {
        (_, _, ushort status) = Qa451Protocol.DecodeTempsAndStatus(0xffff_0000u);

        Assert.Equal(0x1ff, status);
    }

    /// <summary>
    /// Bit 3 is the one that differs: an over-temperature on a QA451B, an over-current trip on a
    /// QA451. Reading one model's bits with the other's table turns a hot heatsink into a phantom
    /// over-current — which is why the driver reads the device id before anything else.
    /// </summary>
    [Fact]
    public void DecodeFaults_ReadsBitThreeDifferentlyOnTheTwoModels()
    {
        (LoadFaults onB, bool replugOnB) = Qa451Protocol.DecodeFaults(Qa451Model.Qa451B, 0x0008);
        (LoadFaults onPlain, bool replugOnPlain) = Qa451Protocol.DecodeFaults(Qa451Model.Qa451, 0x0008);

        Assert.Equal(LoadFaults.OverTemperature, onB);
        Assert.False(replugOnB);

        Assert.Equal(LoadFaults.OverCurrent, onPlain);
        Assert.True(replugOnPlain);
    }

    // The model is an internal enum, so the theory carries a bool: xUnit needs public test methods,
    // and a public method cannot take a less accessible parameter type.
    [Theory]
    [InlineData(false, (ushort)0x0008)]
    [InlineData(false, (ushort)0x0010)]
    [InlineData(true, (ushort)0x0010)]
    public void DecodeFaults_EveryOverCurrentTripLatches(bool isBModel, ushort status)
    {
        Qa451Model model = isBModel ? Qa451Model.Qa451B : Qa451Model.Qa451;

        (LoadFaults faults, bool requiresReplug) = Qa451Protocol.DecodeFaults(model, status);

        Assert.True(faults.HasFlag(LoadFaults.OverCurrent));
        Assert.True(requiresReplug);
    }

    [Fact]
    public void DecodeFaults_OnAQuietDevice_ReportsNothing()
    {
        (LoadFaults faults, bool requiresReplug) = Qa451Protocol.DecodeFaults(Qa451Model.Qa451B, 0x0000);

        Assert.Equal(LoadFaults.None, faults);
        Assert.False(requiresReplug);
    }

    [Fact]
    public void DecodeFaults_OnAQa451B_ReportsBothFaultsAtOnce()
    {
        (LoadFaults faults, bool requiresReplug) = Qa451Protocol.DecodeFaults(Qa451Model.Qa451B, 0x0018);

        Assert.Equal(LoadFaults.OverTemperature | LoadFaults.OverCurrent, faults);
        Assert.True(requiresReplug);
    }

    /// <summary>
    /// The four status words measured on the bench, one per command, with bare terminals
    /// (docs/qa451-protocol.md). This is the table the relay-state reading rests on.
    /// </summary>
    [Theory]
    [InlineData((ushort)0x040, LoadImpedance.Open, false)]      // after a reset
    [InlineData((ushort)0x044, LoadImpedance.EightOhm, false)]  // 8 ohm engaged
    [InlineData((ushort)0x042, LoadImpedance.FourOhm, false)]   // 4 ohm engaged
    [InlineData((ushort)0x041, LoadImpedance.Open, true)]       // DUT supply switched, no load
    [InlineData((ushort)0x045, LoadImpedance.EightOhm, true)]   // both at once: independent flags
    public void DecodeRelayState_MatchesWhatTheBenchMeasured(
        ushort status, LoadImpedance expected, bool dutPowerOn)
    {
        (LoadImpedance? impedance, bool power) = Qa451Protocol.DecodeRelayState(status);

        Assert.Equal(expected, impedance);
        Assert.Equal(dutPowerOn, power);
    }

    /// <summary>
    /// Both load bits at once is not a state this app commands, so it is unknown rather than a
    /// guess at which relay matters more.
    /// </summary>
    [Fact]
    public void DecodeRelayState_WithBothLoadBitsSet_IsUnknown()
    {
        (LoadImpedance? impedance, _) = Qa451Protocol.DecodeRelayState(
            Qa451Protocol.StatusLoad4Ohm | Qa451Protocol.StatusLoad8Ohm);

        Assert.Null(impedance);
    }

    /// <summary>
    /// The relay bits and the fault bits must not be read into each other: an over-temperature on a
    /// QA451B is bit 3, one bit above the 8 Ω relay's bit 2.
    /// </summary>
    [Fact]
    public void DecodeRelayState_IgnoresTheFaultBits()
    {
        (LoadImpedance? impedance, bool power) = Qa451Protocol.DecodeRelayState(0x0018);

        Assert.Equal(LoadImpedance.Open, impedance);
        Assert.False(power);
    }

    [Fact]
    public void CurrentAmpsFromRaw_IsIntegerMilliamps()
    {
        Assert.Equal(1.234, Qa451Protocol.CurrentAmpsFromRaw(1234), 9);
        Assert.Equal(0.0, Qa451Protocol.CurrentAmpsFromRaw(0), 9);
    }

    /// <summary>
    /// The reply is signed, and reading it as a <c>uint</c> is a bug with exactly one symptom: a
    /// bench drawing no current at all reads four million amps. 0xFFFFFFD2 is the figure the real
    /// QA451B answered with nothing on its DUT supply — the sense offset, −46 mA.
    /// </summary>
    [Fact]
    public void CurrentAmpsFromRaw_ReadsASmallNegativeReadingAsNegative()
    {
        Assert.Equal(-0.046, Qa451Protocol.CurrentAmpsFromRaw(0xFFFFFFD2), 9);
        Assert.Equal(-0.001, Qa451Protocol.CurrentAmpsFromRaw(0xFFFFFFFF), 9);
    }

    [Theory]
    [InlineData(LoadImpedance.Open, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument)]
    [InlineData(LoadImpedance.FourOhm, Qa451Protocol.Load4Ohm, Qa451Protocol.Load4OhmArgument)]
    [InlineData(LoadImpedance.EightOhm, Qa451Protocol.Load8Ohm, Qa451Protocol.Load8OhmArgument)]
    public void ImpedanceCommand_IsTheGuardedPairForThatLoad(LoadImpedance impedance, byte opcode, byte argument)
    {
        Assert.Equal((opcode, argument), Qa451Protocol.ImpedanceCommand(impedance));
    }

    [Fact]
    public void ImpedanceCommand_RefusesALoadTheHardwareDoesNotHave()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Qa451Protocol.ImpedanceCommand((LoadImpedance)16));
    }

    [Fact]
    public void DutPowerCommand_IsTheGuardedPairForThatState()
    {
        Assert.Equal((Qa451Protocol.DutPowerOn, Qa451Protocol.DutPowerOnArgument), Qa451Protocol.DutPowerCommand(on: true));
        Assert.Equal((Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument), Qa451Protocol.DutPowerCommand(on: false));
    }

    [Fact]
    public void ModelFromId_RecognisesBothModels()
    {
        Assert.Equal(Qa451Model.Qa451, Qa451Protocol.ModelFromId(Qa451Protocol.Qa451Id));
        Assert.Equal(Qa451Model.Qa451B, Qa451Protocol.ModelFromId(Qa451Protocol.Qa451BId));
    }

    /// <summary>
    /// An unknown id is refused rather than defaulted: the status bits are model-specific, so
    /// guessing would silently misreport faults on whatever this turned out to be.
    /// </summary>
    [Fact]
    public void ModelFromId_OnAnUnknownId_IsRefusedAndSaysWhatItSaw()
    {
        var ex = Assert.Throws<Qa451ProtocolException>(() => Qa451Protocol.ModelFromId(0x12345678));

        Assert.Contains("0x12345678", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequiredFirmware_IsPerModel()
    {
        Assert.Equal(32, Qa451Protocol.RequiredFirmwareFor(Qa451Model.Qa451));
        Assert.Equal(39, Qa451Protocol.RequiredFirmwareFor(Qa451Model.Qa451B));
    }

    /// <summary>
    /// The two ids are adjacent and the devices are not: a QA451 is a HID device on the in-box
    /// driver, where the QA403 is a libusb bulk device. Pinned so a copy-paste of the analyzer's
    /// PID into this file fails here rather than against hardware.
    /// </summary>
    [Fact]
    public void TheUsbIdsAreTheOnesMeasuredOnTheBench()
    {
        Assert.Equal(0x16c0, Qa451Protocol.Qa451VendorId);
        Assert.Equal(0x4e35, Qa451Protocol.Qa451ProductId);
        Assert.Equal(0x3f, Qa451Protocol.ReportId);
        Assert.Equal(64, Qa451Protocol.ReportLength);
    }
}
