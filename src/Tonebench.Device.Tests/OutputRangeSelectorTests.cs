using Tonebench.Device;

namespace Tonebench.Device.Tests;

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
    public void RequiredPeakVolts_ContinuousPinkNoise_AllowsTheSameCrestFactorAsWhite()
    {
        // A *continuous* pink stream, i.e. PeriodFrames unset: shaping filters a Gaussian stream and
        // leaves it Gaussian, and PinkNoiseShaper renormalises it to the same RMS -- so the
        // peak-to-RMS ratio is white's, and so is the range. The periodic sequence below is the one
        // that is not covered by this.
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.PinkNoise,
            Noise = new WhiteNoiseSettings { LevelDbv = -20.0 },
        };

        double expected = Math.Pow(10.0, (-20.0 + OutputRangeSelector.NoiseCrestFactorDb) / 20.0);
        Assert.Equal(expected, OutputRangeSelector.RequiredPeakVolts(channel), precision: 10);
        // And with a rate to build a table with it is still the allowance, because there is no table
        // to build: a continuous stream has no period.
        Assert.Equal(expected, OutputRangeSelector.RequiredPeakVolts(channel, 48000.0), precision: 10);
    }

    /// <summary>
    /// A <b>periodic</b> pink sequence is asked what its own crest factor is, rather than allowed the
    /// Gaussian 12 dB.
    ///
    /// <para>It is deterministic, so its peak is a measured property of the table and not a
    /// probability — and it is not 12 dB. It was 11.37 to 14.49 dB when the phases were simply
    /// random, which under-read the peak and could park the DAC a rung too low; since the sequence
    /// has its crest factor optimised it measures <b>8.50 dB</b> at every period, rate and shape,
    /// which over-reads it by the same kind of margin in the other direction. Either way the
    /// allowance is the wrong number and the sequence is the right one.</para>
    /// </summary>
    [Fact]
    public void RequiredPeakVolts_PeriodicNoise_AsksTheSequenceForItsOwnCrestFactor()
    {
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.PinkNoise,
            Noise = new WhiteNoiseSettings
            {
                LevelDbv = -20.0,
                PeriodFrames = 65536,
                CutoffHz = PeriodicNoise.DefaultCutoffHz,
            },
        };

        double crestDb = PeriodicNoise.For(65536, 48000.0, PeriodicNoise.DefaultCutoffHz).CrestFactorDb;
        Assert.Equal(
            Math.Pow(10.0, (-20.0 + crestDb) / 20.0),
            OutputRangeSelector.RequiredPeakVolts(channel, 48000.0),
            precision: 10);

        // The figure this replaced, so the test fails if it goes back to the allowance. The sign of
        // the difference is not the point — only that the two are far enough apart to matter.
        Assert.True(Math.Abs(crestDb - OutputRangeSelector.NoiseCrestFactorDb) > 0.5, $"crest was {crestDb}");

        // No rate, no table: a caller that cannot know what it will be played at gets the allowance
        // rather than a wrong answer.
        Assert.Equal(
            Math.Pow(10.0, (-20.0 + OutputRangeSelector.NoiseCrestFactorDb) / 20.0),
            OutputRangeSelector.RequiredPeakVolts(channel),
            precision: 10);
    }

    /// <summary>
    /// And a table the sequence refuses to build never decides a range: a range chosen from the 12 dB
    /// guess may clip, where an exception here is an acquisition that will not start at all.
    /// </summary>
    [Fact]
    public void RequiredPeakVolts_PeriodicNoise_WithSettingsNoSequenceCanBeBuiltFrom_FallsBackToTheAllowance()
    {
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.PinkNoise,
            // Not a power of two, and a cut-off far outside the supported range.
            Noise = new WhiteNoiseSettings { LevelDbv = -20.0, PeriodFrames = 1000, CutoffHz = 1e9 },
        };

        Assert.Equal(
            Math.Pow(10.0, (-20.0 + OutputRangeSelector.NoiseCrestFactorDb) / 20.0),
            OutputRangeSelector.RequiredPeakVolts(channel, 48000.0),
            precision: 10);
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
