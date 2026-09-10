namespace Qa403.Device.Tests;

/// <summary>
/// The 1/f shaper both generator implementations share: that it is calibrated, and that what it
/// produces really is pink.
///
/// <para>The slope is checked in <b>closed form</b> rather than by transforming a noise stream. A
/// spectrum estimated from noise is itself noisy, so an FFT-based version of this test would either
/// need an enormous record or a tolerance loose enough to pass against a filter that had stopped
/// working — and the filter's transfer function is known exactly, so there is no reason to guess at
/// it.</para>
/// </summary>
public class PinkNoiseShaperTests
{
    private const double SampleRateHz = 44_100.0;

    [Fact]
    public void RmsGain_IsTheRmsOfAShapedUnitWhiteStream()
    {
        // The point of the constant: it is derived from the coefficients rather than written down,
        // so this checks the derivation against the filter actually running.
        var shaper = new PinkNoiseShaper(SampleRateHz);
        var random = new Random(1);

        const int warmup = 20_000;
        const int samples = 400_000;
        double sumOfSquares = 0.0;

        for (int i = 0; i < warmup + samples; i++)
        {
            double y = shaper.Next(NextGaussian(random));
            if (i >= warmup)
                sumOfSquares += y * y;
        }

        double measured = Math.Sqrt(sumOfSquares / samples);
        Assert.Equal(PinkNoiseShaper.RmsGain, measured, tolerance: 0.05 * PinkNoiseShaper.RmsGain);
    }

    [Fact]
    public void NextStimulus_LeavesTheStreamAtTheRmsThatDroveIt()
    {
        // This is what lets LevelDbv mean the same broadband RMS for pink that it means for white,
        // which is in turn what lets OutputRangeSelector derive a range from the configured level.
        var shaper = new PinkNoiseShaper(SampleRateHz);
        var random = new Random(7);

        const double sigma = 0.1;
        const int warmup = 20_000;
        const int samples = 400_000;
        double sumOfSquares = 0.0;

        for (int i = 0; i < warmup + samples; i++)
        {
            double y = sigma * shaper.NextStimulus(NextGaussian(random));
            if (i >= warmup)
                sumOfSquares += y * y;
        }

        double measuredDb = 20.0 * Math.Log10(Math.Sqrt(sumOfSquares / samples) / sigma);
        Assert.True(Math.Abs(measuredDb) < 0.5, $"Expected ~0 dB against the driving RMS, measured {measuredDb:0.00} dB");
    }

    [Fact]
    public void PowerResponse_FallsThreeDbPerOctaveAcrossTheAudioBand()
    {
        // Seven octaves from 50 Hz, which is the span the filter is designed over at this rate.
        const double lowHz = 50.0;
        const int octaves = 7;

        double lowDb = 10.0 * Math.Log10(PinkNoiseShaper.PowerResponse(lowHz, SampleRateHz));
        double highDb = 10.0 * Math.Log10(PinkNoiseShaper.PowerResponse(lowHz * (1 << octaves), SampleRateHz));
        double slopeDbPerOctave = (lowDb - highDb) / octaves;

        Assert.Equal(3.01, slopeDbPerOctave, tolerance: 0.2);
    }

    [Fact]
    public void PowerResponse_HasNoOctaveFarFromTheAverageSlope()
    {
        // The economy filter approximates 1/f with three poles, so it ripples about the ideal line
        // by a few tenths of a dB per octave. That is fine for a stimulus and would not be for an
        // absolute reference; this pins the ripple so a coefficient typo, which would be worth many
        // dB, cannot hide inside the average the previous test measures.
        for (int octave = 0; octave < 7; octave++)
        {
            double freqHz = 50.0 * (1 << octave);
            double stepDb = 10.0 * Math.Log10(
                PinkNoiseShaper.PowerResponse(freqHz, SampleRateHz)
                    / PinkNoiseShaper.PowerResponse(2.0 * freqHz, SampleRateHz));

            Assert.True(
                Math.Abs(stepDb - 3.01) < 1.0,
                $"The octave above {freqHz:0} Hz falls {stepDb:0.00} dB, which is not within 1 dB of 3.01.");
        }
    }

    /// <summary>
    /// The DC blocker is where it says it is: a pink filter integrates its input, so what it produces
    /// wanders about a slowly moving offset, and playing that at a DUT is DC into the load, wasted
    /// DAC headroom, and a 41 dB DC bin that biases the estimator's whole top end low. See
    /// <see cref="PinkNoiseShaper.HighPassHz"/>.
    /// </summary>
    [Fact]
    public void NextStimulus_BlocksDcWithoutTouchingTheMeasuredBand()
    {
        var shaper = new PinkNoiseShaper(SampleRateHz);

        Assert.Equal(0.0, shaper.StimulusPowerResponse(0.0), 12);

        // Measured against the passband rather than against the bare filter directly: the two
        // responses are normalised by their own RMS gains, which differ by the power the blocker
        // removes, and that offset would otherwise be read as blocker gain.
        double BlockerDb(double freqHz)
        {
            double relative = 10.0 * Math.Log10(
                shaper.StimulusPowerResponse(freqHz) / PinkNoiseShaper.PowerResponse(freqHz, SampleRateHz));
            double passband = 10.0 * Math.Log10(
                shaper.StimulusPowerResponse(1000.0) / PinkNoiseShaper.PowerResponse(1000.0, SampleRateHz));
            return relative - passband;
        }

        // Down 3 dB at its own corner, by construction.
        Assert.Equal(-3.01, BlockerDb(PinkNoiseShaper.HighPassHz), tolerance: 0.05);

        // And out of the way by the bottom of any band this app analyses.
        double twentyHzDb = BlockerDb(20.0);
        Assert.True(twentyHzDb > -0.1, $"The blocker still costs {twentyHzDb:0.000} dB at 20 Hz.");
    }

    /// <summary>
    /// A stream of shaped samples has an offset the blocker has to remove, not an offset it can
    /// assume away: this is the property, measured, that the response test above predicts.
    /// </summary>
    [Fact]
    public void NextStimulus_LeavesNoOffsetWhereTheBareFilterDoes()
    {
        var bare = new PinkNoiseShaper(SampleRateHz);
        var blocked = new PinkNoiseShaper(SampleRateHz);

        const int warmup = 20_000;
        const int samples = 200_000;
        double bareSum = 0.0;
        double blockedSum = 0.0;
        double blockedSumOfSquares = 0.0;

        for (int i = 0; i < warmup + samples; i++)
        {
            double white = PinkNoiseShaper.WhiteAt(i);
            double x = bare.Next(white) / PinkNoiseShaper.RmsGain;
            double y = blocked.NextStimulus(white);

            if (i < warmup)
                continue;

            bareSum += x;
            blockedSum += y;
            blockedSumOfSquares += y * y;
        }

        double blockedRms = Math.Sqrt(blockedSumOfSquares / samples);
        double bareMean = Math.Abs(bareSum / samples);
        double blockedMean = Math.Abs(blockedSum / samples);

        Assert.True(blockedMean < 0.01 * blockedRms, $"The blocked stream still averages {blockedMean:0.0000}.");
        Assert.True(blockedMean < 0.2 * bareMean, $"Blocked mean {blockedMean:0.0000} against the bare filter's {bareMean:0.0000}.");
    }

    private static double NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
