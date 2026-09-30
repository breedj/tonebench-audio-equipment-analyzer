using Qa403.App.Services;
using Qa403.Data;

namespace Qa403.App.Tests.Services;

/// <summary>
/// The one place that knows the document stores millivolts while the device layer works in peak
/// volts, and the validation that keeps a mis-entered reading out of a calibration.
/// </summary>
public class SoundCardSettingTests
{
    [Fact]
    public void Calibration_ConvertsMillivoltsToPeakVolts()
    {
        var setting = new SoundCardSetting
        {
            Config = SoundCardConfig.Default with
            {
                OutputFullScalePeakMv = 3342.0,
                InputFullScalePeakMvLeft = 9132.76,
                InputFullScalePeakMvRight = 9148.35,
            },
        };

        var calibration = setting.Calibration;

        Assert.Equal(3.342, calibration.OutputFullScalePeakV!.Value, 1e-9);
        Assert.Equal(9.13276, calibration.InputFullScalePeakVLeft!.Value, 1e-9);
        Assert.True(calibration.IsInputCalibrated);
        Assert.True(calibration.IsOutputCalibrated);
    }

    /// <summary>
    /// A null stays a null rather than becoming a number. That difference is the whole of how an
    /// uncalibrated card knows to report dBFS instead of claiming volts it has never measured.
    /// </summary>
    [Fact]
    public void Calibration_WithNothingMeasured_IsUncalibratedRatherThanZero()
    {
        var setting = new SoundCardSetting { Config = SoundCardConfig.Default };

        var calibration = setting.Calibration;

        Assert.Null(calibration.OutputFullScalePeakV);
        Assert.False(calibration.IsInputCalibrated);
        Assert.False(calibration.IsOutputCalibrated);
        Assert.Equal(0.0, calibration.InputRangeDbv, 1e-9);
    }

    [Fact]
    public void DeviceSettings_CarryTheCardAndItsWiring()
    {
        var setting = new SoundCardSetting
        {
            Config = SoundCardConfig.Default with
            {
                InputHostApi = "ASIO",
                InputDeviceName = "MOTU M Series",
                OutputHostApi = "ASIO",
                OutputDeviceName = "MOTU M Series",
                InputChannelOffset = 2,
                FrontEndGainLeftDb = -20.0,
            },
        };

        var device = setting.DeviceSettings;

        Assert.True(device.IsConfigured);
        Assert.Equal("MOTU M Series", device.InputDeviceName);
        Assert.Equal(2, device.InputChannelOffset);
        Assert.Equal(-20.0, device.FrontEndGainLeftDb);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-60.0)]
    [InlineData(60.0)]
    public void Config_WithAGainInsideTheBound_IsValid(double db) =>
        Assert.True((SoundCardConfig.Default with { FrontEndGainLeftDb = db }).IsValid);

    /// <summary>Past ±60 dB it is a typo rather than a fixture — the same bound the external gain uses.</summary>
    [Theory]
    [InlineData(61.0)]
    [InlineData(double.NaN)]
    public void Config_WithAGainOutsideTheBound_IsRejected(double db) =>
        Assert.False((SoundCardConfig.Default with { FrontEndGainLeftDb = db }).IsValid);

    /// <summary>A full scale of zero or a negative is a mis-entered voltmeter reading, and it would divide by zero downstream.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.PositiveInfinity)]
    public void Config_WithAnImpossibleFullScale_IsRejected(double mv) =>
        Assert.False((SoundCardConfig.Default with { OutputFullScalePeakMv = mv }).IsValid);

    [Fact]
    public void Config_WithNoCalibrationAtAll_IsStillValid() =>
        Assert.True(SoundCardConfig.Default.IsValid);

    [Fact]
    public void Config_WithAnUnknownGainUnit_IsRejected() =>
        Assert.False((SoundCardConfig.Default with { GainDisplayUnit = "Volts" }).IsValid);

    [Fact]
    public void Config_IsNotConfiguredUntilBothSidesAreChosen()
    {
        Assert.False(SoundCardConfig.Default.IsConfigured);
        Assert.False((SoundCardConfig.Default with { InputDeviceName = "Card" }).IsConfigured);
        Assert.True((SoundCardConfig.Default with { InputDeviceName = "Card", OutputDeviceName = "Card" }).IsConfigured);
    }
}
