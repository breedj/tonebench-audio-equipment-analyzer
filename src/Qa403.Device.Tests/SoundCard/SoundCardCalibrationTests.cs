using Qa403.Device.SoundCard;

namespace Qa403.Device.Tests.SoundCard;

/// <summary>
/// The sound card's sample-to-volts arithmetic. Pure, so all of it runs without a card.
/// </summary>
public class SoundCardCalibrationTests
{
    private const double Tolerance = 1e-9;

    private static SoundCardCalibration Calibrated(double outV = 2.0, double inLeftV = 1.5, double inRightV = 1.5) =>
        new()
        {
            OutputFullScalePeakV = outV,
            InputFullScalePeakVLeft = inLeftV,
            InputFullScalePeakVRight = inRightV,
        };

    [Fact]
    public void SampleToVolts_AtFullScale_ReturnsTheCalibratedFullScalePeak()
    {
        var cal = Calibrated(inLeftV: 1.5, inRightV: 2.5);

        Assert.Equal(1.5, cal.SampleToVolts(1.0f, 0), Tolerance);
        Assert.Equal(2.5, cal.SampleToVolts(1.0f, 1), Tolerance);
        Assert.Equal(-0.75, cal.SampleToVolts(-0.5f, 0), Tolerance);
    }

    [Fact]
    public void VoltsToSample_AtFullScale_ReturnsOne()
    {
        var cal = Calibrated(outV: 2.0);

        Assert.Equal(1.0f, cal.VoltsToSample(2.0), 1e-6);
        Assert.Equal(0.5f, cal.VoltsToSample(1.0), 1e-6);
    }

    /// <summary>
    /// The analogue of <c>Qa40xCalibration.VoltsToRaw</c>'s clamp. Without it a generator asked for
    /// more than the card can play would wrap rather than clip, and
    /// <c>OutputRangeSelector.Clips</c> would be warning about something that never happened.
    /// </summary>
    [Fact]
    public void VoltsToSample_AboveFullScale_ClipsRatherThanWrapping()
    {
        var cal = Calibrated(outV: 2.0);

        Assert.Equal(1.0f, cal.VoltsToSample(10.0), 1e-6);
        Assert.Equal(-1.0f, cal.VoltsToSample(-10.0), 1e-6);
    }

    [Fact]
    public void RoundTrip_ThroughBothDirections_PreservesTheLevel()
    {
        var cal = Calibrated(outV: 2.0, inLeftV: 2.0, inRightV: 2.0);

        foreach (double volts in new[] { 0.0, 0.25, 1.0, -1.75 })
            Assert.Equal(volts, cal.SampleToVolts(cal.VoltsToSample(volts), 0), 1e-6);
    }

    /// <summary>
    /// A tone played 3 dB below full scale and read as 1000 mV rms means full scale is that reading
    /// scaled back up: 1414.2 mV peak / 10^(−3/20) = 1997.6 mV peak.
    ///
    /// <para>Note the figures in ARTA's dialog are <i>not</i> a worked example of this and must not
    /// be used as one — its Estimated and Current columns are equal in both panels, which is an
    /// already-calibrated card displaying what it has stored. No output-level step maps its
    /// 1000 mV rms to its 3342 mV (that ratio is 7.47 dB, which is not on the dropdown).</para>
    /// </summary>
    [Fact]
    public void OutputFullScale_FromAVoltmeterReadingThreeDbDown_IsTheReadingScaledBackUp()
    {
        double measuredPeakV = VoltageEntry.MillivoltsToPeakVolts(1000.0, VoltageEntryUnit.Rms);

        double fullScaleV = SoundCardCalibration.OutputFullScaleFromMeasurement(measuredPeakV, playedDbFs: -3.0);

        Assert.Equal(1997.6, fullScaleV * 1000.0, 0.1);
    }

    /// <summary>
    /// Every level the dialog offers, against the same reading — each one 3, 6, 9, 12 and 20 dB of
    /// headroom above what the meter saw.
    /// </summary>
    [Theory]
    [InlineData(-3.0, 1997.6)]
    [InlineData(-6.0, 2821.7)]
    [InlineData(-9.0, 3985.8)]
    [InlineData(-12.0, 5630.1)]
    [InlineData(-20.0, 14142.1)]
    public void OutputFullScale_AtEveryOfferedLevel_ScalesByThatLevel(double playedDbFs, double expectedMv)
    {
        double measuredPeakV = VoltageEntry.MillivoltsToPeakVolts(1000.0, VoltageEntryUnit.Rms);

        double fullScaleV = SoundCardCalibration.OutputFullScaleFromMeasurement(measuredPeakV, playedDbFs);

        Assert.Equal(expectedMv, fullScaleV * 1000.0, 0.1);
    }

    /// <summary>
    /// The two entry units must describe the same voltage differently, not two different voltages:
    /// 1000 mV rms is 1414.2 mV peak, and a card calibrated against the wrong one is 3.01 dB out.
    /// </summary>
    [Fact]
    public void AnRmsEntryAndTheEquivalentPeakEntry_Agree()
    {
        double fromRms = SoundCardCalibration.OutputFullScaleFromMeasurement(
            VoltageEntry.MillivoltsToPeakVolts(1000.0, VoltageEntryUnit.Rms), -6.0);
        double fromPeak = SoundCardCalibration.OutputFullScaleFromMeasurement(
            VoltageEntry.MillivoltsToPeakVolts(1000.0 * Math.Sqrt(2.0), VoltageEntryUnit.Peak), -6.0);

        Assert.Equal(fromRms, fromPeak, 1e-9);
    }

    [Fact]
    public void InputFullScale_FromAKnownSource_ScalesItBackUpByTheLevelItWasMeasuredAt()
    {
        // 500 mV rms arriving 20 dB below full scale means full scale is ten times that.
        double knownPeakV = VoltageEntry.MillivoltsToPeakVolts(500.0, VoltageEntryUnit.Rms);

        double fullScaleV = SoundCardCalibration.InputFullScaleFromMeasurement(knownPeakV, measuredDbFs: -20.0);

        Assert.Equal(knownPeakV * 10.0, fullScaleV, 1e-9);
    }

    [Fact]
    public void AMeasurementAboveFullScale_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SoundCardCalibration.InputFullScaleFromMeasurement(1.0, measuredDbFs: 3.0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SoundCardCalibration.OutputFullScaleFromMeasurement(1.0, playedDbFs: 3.0));
    }

    [Fact]
    public void ANonPositiveMeasuredVoltage_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SoundCardCalibration.InputFullScaleFromMeasurement(0.0, -20.0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SoundCardCalibration.OutputFullScaleFromMeasurement(double.NaN, -20.0));
    }

    /// <summary>
    /// The whole point of the √2 default: uncalibrated, full scale is 0 dBV, so every level the app
    /// prints is the dBFS figure and nothing claims to be a voltage it has not measured.
    /// </summary>
    [Fact]
    public void Uncalibrated_PutsFullScaleAtZeroDbv_SoEveryLevelReadsAsDbfs()
    {
        var cal = SoundCardCalibration.Uncalibrated;

        Assert.False(cal.IsInputCalibrated);
        Assert.False(cal.IsOutputCalibrated);
        Assert.Equal(0.0, cal.InputRangeDbv, 1e-9);
        Assert.Equal(0.0, cal.OutputRangeDbv, 1e-9);

        // A tone at -20 dBFS reads -20 dBV.
        float halfScaleSample = (float)Math.Pow(10.0, -20.0 / 20.0);
        double dbv = SoundCardCalibration.PeakVoltsToDbv(cal.SampleToVolts(halfScaleSample, 0));
        Assert.Equal(-20.0, dbv, 1e-6);
    }

    /// <summary>
    /// The rungs are RMS-referenced peak dBV, the convention <c>InputRangeSelector</c>,
    /// <c>Qa40xAutoRanger</c> and <c>SpectrumMeters.PeakDbv</c> all share. A true-peak dBV here
    /// would sit 3.01 dB high and put every ranging decision one step out.
    /// </summary>
    [Fact]
    public void TheRungs_AreRmsReferencedPeakDbv()
    {
        // √2 V peak is 1 V rms, i.e. 0 dBV.
        Assert.Equal(0.0, SoundCardCalibration.PeakVoltsToDbv(Math.Sqrt(2.0)), 1e-9);
        Assert.Equal(6.0206, SoundCardCalibration.PeakVoltsToDbv(2.0 * Math.Sqrt(2.0)), 1e-4);
    }

    /// <summary>
    /// One number is used to predict clipping, so it has to describe whichever channel clips first.
    /// </summary>
    [Fact]
    public void InputRange_WithUnequalChannels_TakesTheOneThatClipsFirst()
    {
        var cal = Calibrated(inLeftV: 1.0, inRightV: 4.0);

        Assert.Equal(SoundCardCalibration.PeakVoltsToDbv(1.0), cal.InputRangeDbv, 1e-9);
    }

    [Fact]
    public void InputChannelDifference_IsTheGapBetweenTheTwoFullScales()
    {
        var cal = Calibrated(inLeftV: 1.0, inRightV: 2.0);

        Assert.Equal(6.0206, cal.InputChannelDifferenceDb!.Value, 1e-4);
    }

    [Fact]
    public void InputChannelDifference_WhileUncalibrated_IsAbsentRatherThanZero()
    {
        Assert.Null(SoundCardCalibration.Uncalibrated.InputChannelDifferenceDb);
    }

    /// <summary>
    /// A card whose output has been calibrated and whose input has not is a legitimate state — the
    /// two steps are independent, and the second does not have to trust the first.
    /// </summary>
    [Fact]
    public void TheTwoCalibrationSteps_AreReportedIndependently()
    {
        var outputOnly = new SoundCardCalibration { OutputFullScalePeakV = 2.0 };

        Assert.True(outputOnly.IsOutputCalibrated);
        Assert.False(outputOnly.IsInputCalibrated);
        Assert.Equal(SoundCardCalibration.UncalibratedFullScalePeakV, outputOnly.InputFullScalePeakV(0), Tolerance);
    }
}
