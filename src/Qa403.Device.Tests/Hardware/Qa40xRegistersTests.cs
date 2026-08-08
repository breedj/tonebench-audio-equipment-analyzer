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

    [Fact]
    public void InputRangesDbv_MatchesDeviceCapabilities() =>
        Assert.Equal(DeviceCapabilities.Qa403.InputRangesDbv, Qa40xRegisters.InputRangesDbv);

    [Fact]
    public void OutputRangesDbv_MatchesDeviceCapabilities() =>
        Assert.Equal(DeviceCapabilities.Qa403.OutputRangesDbv, Qa40xRegisters.OutputRangesDbv);
}
