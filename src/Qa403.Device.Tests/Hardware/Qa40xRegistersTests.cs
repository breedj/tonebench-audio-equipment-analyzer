using Qa403.Device.Hardware;

namespace Qa403.Device.Tests.Hardware;

/// <summary>Encoding tables must match docs/protocol.md exactly — see Qa40xRegisters.cs for citations.</summary>
public class Qa40xRegistersTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 1)]
    [InlineData(12, 2)]
    [InlineData(18, 3)]
    [InlineData(24, 4)]
    [InlineData(30, 5)]
    [InlineData(36, 6)]
    [InlineData(42, 7)]
    public void InputRangeDbvToRegister_MatchesForm1csSwitch(double dbv, byte expected) =>
        Assert.Equal(expected, Qa40xRegisters.InputRangeDbvToRegister(dbv));

    [Theory]
    [InlineData(-12, 0)]
    [InlineData(-2, 1)]
    [InlineData(8, 2)]
    [InlineData(18, 3)]
    public void OutputRangeDbvToRegister_MatchesForm1csSwitch(double dbv, byte expected) =>
        Assert.Equal(expected, Qa40xRegisters.OutputRangeDbvToRegister(dbv));

    [Theory]
    [InlineData(48_000, 0u)]
    [InlineData(96_000, 1u)]
    [InlineData(192_000, 2u)]
    [InlineData(384_000, 3u)]
    public void SampleRateHzToRegister_MatchesPyQa40xTable(double sampleRateHz, uint expected) =>
        Assert.Equal(expected, Qa40xRegisters.SampleRateHzToRegister(sampleRateHz));

    [Fact]
    public void InputRangeDbvToRegister_RejectsUnsupportedValue() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Qa40xRegisters.InputRangeDbvToRegister(7.0));

    [Fact]
    public void OutputRangeDbvToRegister_RejectsUnsupportedValue() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Qa40xRegisters.OutputRangeDbvToRegister(0.0));

    [Fact]
    public void SampleRateHzToRegister_RejectsUnsupportedValue() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Qa40xRegisters.SampleRateHzToRegister(44_100));

    /// <summary>
    /// The raw values behind a real QA403 status bar reading "USB Voltage: 4.864V | USB Current:
    /// 0.830A | 25.0C" — the vendor screenshot this readout was built from. Pinned as a set rather
    /// than as three scalings because the units differ per register (mV, mA, deci-°C) and getting
    /// one of them wrong produces a number that still looks plausible on screen. There is no
    /// isolated-rail figure: see <see cref="Qa40xRegisters.TelemetryIsoCurrent"/>.
    /// </summary>
    [Fact]
    public void DecodeTelemetry_MatchesTheVendorStatusBarReading()
    {
        var telemetry = Qa40xRegisters.DecodeTelemetry(
            usbVoltageRaw: 4864, usbCurrentRaw: 830, temperatureRaw: 250);

        Assert.Equal(4.864, telemetry.UsbVoltageV, 6);
        Assert.Equal(830.0, telemetry.UsbCurrentMa, 6);
        Assert.Equal(25.0, telemetry.TemperatureC, 6);
    }

    /// <summary>
    /// Temperature is the one register whose raw value is not already in the unit it is displayed
    /// in, so a decode that forgot the divide-by-ten still reads as a believable rack temperature
    /// for the first digit and as nonsense afterwards. A non-round tenth is what catches it.
    /// </summary>
    [Fact]
    public void DecodeTelemetry_ReadsTemperatureInDeciDegrees() =>
        Assert.Equal(31.7, Qa40xRegisters.DecodeTelemetry(0, 0, temperatureRaw: 317).TemperatureC, 6);
}
