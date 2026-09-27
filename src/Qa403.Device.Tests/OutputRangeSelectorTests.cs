using Qa403.Device;

namespace Qa403.Device.Tests;

public class OutputRangeSelectorTests
{
    private static readonly IReadOnlyList<double> Ranges = DeviceCapabilities.Qa403.OutputRangesDbv;

    private static GeneratorConfig SingleTone(double ampDbv) => new(
        new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, ampDbv)]), ChannelGeneratorConfig.Off);

    [Theory]
    // Full scales are -12/-2/8/18 dBV; with 2 dB of headroom a tone needs range >= amp + 2 dB.
    [InlineData(-30.0, -12.0)]
    [InlineData(-15.0, -12.0)]
    // -14 needs -12 exactly: the boundary case, and the one float rounding used to push a range up.
    [InlineData(-14.0, -12.0)]
    // -13 needs -11, which the -12 dBV full scale genuinely cannot supply.
    [InlineData(-13.0, -2.0)]
    [InlineData(-11.0, -2.0)]
    [InlineData(-6.0, -2.0)]
    [InlineData(0.0, 8.0)]
    [InlineData(6.0, 8.0)]
    [InlineData(12.0, 18.0)]
    public void Select_PicksTheLowestRangeWithEnoughHeadroom(double ampDbv, double expectedRangeDbv)
    {
        Assert.Equal(expectedRangeDbv, OutputRangeSelector.Select(SingleTone(ampDbv), Ranges));
    }

    [Fact]
    public void Select_AboveEveryRange_FallsBackToTheLargest()
    {
        // 24 dBV exceeds even the 18 dBV full scale. Clipping is unavoidable; the largest range at
        // least clips least.
        Assert.Equal(18.0, OutputRangeSelector.Select(SingleTone(24.0), Ranges));
    }

    [Fact]
    public void Select_SilentGenerator_PicksTheSmallestRange()
    {
        // Nothing playing means nothing to clip, and the smallest range is the quietest DAC state —
        // which is the whole point of deriving this rather than parking on 18 dBV.
        Assert.Equal(-12.0, OutputRangeSelector.Select(GeneratorConfig.Off, Ranges));
    }

    [Fact]
    public void Select_DisabledChannel_ContributesNothing()
    {
        var config = new GeneratorConfig(
            new ChannelGeneratorConfig(false, [new GeneratorTone(1000.0, 12.0)]),
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]));

        Assert.Equal(-12.0, OutputRangeSelector.Select(config, Ranges));
    }

    [Fact]
    public void Select_UsesTheLouderOfTheTwoChannels()
    {
        // One register serves both channels, so the quiet one must not be what sets the range.
        var config = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -40.0)]),
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 6.0)]));

        Assert.Equal(8.0, OutputRangeSelector.Select(config, Ranges));
    }

    [Fact]
    public void RequiredPeakVolts_SumsTonePeaksRatherThanCombiningThemInRms()
    {
        // Qa40xGeneratorWaveform.SumTones starts every tone as a cosine, so at t=0 they are all at
        // their own peak simultaneously — the worst case really is the arithmetic sum. An RMS
        // combination would under-size the range and clip the very first sample.
        var channel = new ChannelGeneratorConfig(
            true, [new GeneratorTone(1000.0, -6.0), new GeneratorTone(2000.0, -6.0), new GeneratorTone(3000.0, -6.0)]);

        double onePeakV = Math.Sqrt(2.0) * Math.Pow(10.0, -6.0 / 20.0);
        Assert.Equal(3.0 * onePeakV, OutputRangeSelector.RequiredPeakVolts(channel), precision: 10);
    }

    [Fact]
    public void RequiredPeakVolts_WhiteNoise_AllowsACrestFactorAboveItsRmsLevel()
    {
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.WhiteNoise,
            Noise = new WhiteNoiseSettings { LevelDbv = -20.0 },
        };

        // Gaussian noise has no bounded peak, so the level alone would clip constantly: allow
        // NoiseCrestFactorDb (12 dB ~ 4 sigma) above the configured RMS.
        double expected = Math.Pow(10.0, (-20.0 + OutputRangeSelector.NoiseCrestFactorDb) / 20.0);
        Assert.Equal(expected, OutputRangeSelector.RequiredPeakVolts(channel), precision: 10);
    }

    [Fact]
    public void RequiredPeakVolts_PinkNoise_AllowsTheSameCrestFactorAsWhite()
    {
        // Shaping filters a Gaussian stream and leaves it Gaussian, and PinkNoiseShaper renormalises
        // it to the same RMS -- so the peak-to-RMS ratio is white's, and so is the range.
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.PinkNoise,
            Noise = new WhiteNoiseSettings { LevelDbv = -20.0 },
        };

        double expected = Math.Pow(10.0, (-20.0 + OutputRangeSelector.NoiseCrestFactorDb) / 20.0);
        Assert.Equal(expected, OutputRangeSelector.RequiredPeakVolts(channel), precision: 10);
    }

    [Fact]
    public void RequiredPeakVolts_IgnoresTheToneListForNonToneKinds()
    {
        // Tones stays populated when the user switches kind, and sizing the range off a stimulus
        // that isn't playing would waste headroom (and DAC noise) on every noise measurement.
        var channel = new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 18.0)])
        {
            Kind = GeneratorKind.EchoChirp,
            Chirp = new EchoChirpSettings { AmplitudeDbv = -20.0 },
        };

        double expected = Math.Sqrt(2.0) * Math.Pow(10.0, -20.0 / 20.0);
        Assert.Equal(expected, OutputRangeSelector.RequiredPeakVolts(channel), precision: 10);
    }

    /// <summary>
    /// Clips is not "Select would have picked something bigger". Select keeps HeadroomDb in hand, so
    /// the band between a range's full scale and its full scale less the headroom is a range Select
    /// declines to choose and that also does not clip — and reporting that band as clipping to
    /// someone who pinned the range by hand would be crying wolf on most of their choices.
    /// </summary>
    [Fact]
    public void Clips_IsAboutFullScale_NotTheMarginSelectKeeps()
    {
        // The 8 dBV range's full scale is sqrt(2)*10^(8/20) = 3.552 V peak. A 6.5 dBV tone peaks at
        // 2.99 V: inside it, but inside it by less than the 2 dB Select insists on.
        var tone = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 6.5)]), ChannelGeneratorConfig.Off);

        Assert.Equal(18.0, OutputRangeSelector.Select(tone, DeviceCapabilities.Qa403.OutputRangesDbv));
        Assert.False(OutputRangeSelector.Clips(tone, 8.0));
        Assert.True(OutputRangeSelector.Clips(tone, -2.0));
    }

    /// <summary>
    /// A tone exactly at a range's full scale is the common case, not a corner one — the ranges are
    /// 10 dB apart and users pick round numbers — so it must not report clipping off a float
    /// rounding either way. What can still push it over is the per-channel calibration factor, which
    /// is what HeadroomDb covers and why Select would not have chosen this range.
    /// </summary>
    [Fact]
    public void Clips_IsFalseForALevelExactlyAtFullScale()
    {
        var tone = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 8.0)]), ChannelGeneratorConfig.Off);

        Assert.False(OutputRangeSelector.Clips(tone, 8.0));
    }

    [Fact]
    public void Clips_AnswersForTheLouderChannel()
    {
        // One register serves both outputs, so the quiet channel cannot make a clipped one safe.
        var config = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -40.0)]),
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 8.6)]));

        Assert.True(OutputRangeSelector.Clips(config, -12.0));
        Assert.False(OutputRangeSelector.Clips(config, 18.0));
    }
}
