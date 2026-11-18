using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// Folding a record on its own period, and the two readings the overlay is for.
///
/// <para>Every expectation here is a figure the waveform was built with — an exact modulation
/// depth, an exact jitter, an exact period — because a spread or a jitter figure that is plausibly
/// wrong is this picture's whole failure mode.</para>
/// </summary>
public class WaveformEyeTests
{
    private const double Rate = 192_000.0;

    private static double[][] Fold(double[] samples, int foldFrames)
    {
        int folds = WaveformEye.FoldCount(samples.Length, foldFrames);
        var result = new double[folds][];
        for (int f = 0; f < folds; f++)
        {
            result[f] = new double[foldFrames];
            Array.Copy(samples, f * foldFrames, result[f], 0, foldFrames);
        }

        return result;
    }

    [Fact]
    public void FoldFrames_RoundsThePeriodToAWholeSample()
    {
        // 1 kHz at 192 kS/s is exactly 192 frames.
        Assert.Equal(192, WaveformEye.FoldFrames(1.0 / 1000.0, Rate));

        // 997 Hz is 192.577..., which rounds down to 193 rather than truncating to 192 -- the
        // difference is half a sample per fold, which walks the overlay sideways over a hundred of
        // them and reads as jitter that is not there.
        Assert.Equal(193, WaveformEye.FoldFrames(1.0 / 997.0, Rate));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FoldFrames_RefusesAPeriodThatIsNotOne(double periodS) =>
        Assert.Equal(0, WaveformEye.FoldFrames(periodS, Rate));

    [Fact]
    public void FoldFrames_RefusesAPeriodOfOneSample()
    {
        // Two samples is the shortest thing with a shape; one is a number.
        Assert.Equal(0, WaveformEye.FoldFrames(1.0 / Rate, Rate));
        Assert.Equal(2, WaveformEye.FoldFrames(2.0 / Rate, Rate));
    }

    [Fact]
    public void FoldCount_TakesWholeFoldsAndDropsTheRemainder()
    {
        // A part-fold drawn with the rest is a trace that stops in the middle of the picture, which
        // reads as a waveform that stopped rather than as a window that ran out.
        Assert.Equal(5, WaveformEye.FoldCount(1000, 192));
        Assert.Equal(5, WaveformEye.FoldCount(960, 192));
        Assert.Equal(2, WaveformEye.FoldCount(400, 192));
    }

    [Fact]
    public void FoldCount_RefusesFewerThanTwoFolds()
    {
        // One cycle on its own is the waveform view; the comparison between cycles is the point.
        Assert.Equal(0, WaveformEye.FoldCount(383, 192));
        Assert.Equal(0, WaveformEye.FoldCount(0, 192));
        Assert.Equal(0, WaveformEye.FoldCount(1000, 0));
    }

    [Fact]
    public void AnExactlyRepeatingTone_FoldsToOneLine()
    {
        // 1 kHz at 192 kS/s is a whole 192 samples a period, so every fold is the same samples and
        // the band has no width at all. This is the figure every other case is read against.
        int frames = 192 * 20;
        var samples = new double[frames];
        for (int i = 0; i < frames; i++)
            samples[i] = Math.Sin(2.0 * Math.PI * 1000.0 * i / Rate);

        var spread = WaveformEye.Measure(Fold(samples, 192), Rate);

        Assert.NotNull(spread);
        Assert.Equal(0.0, spread!.Value.MaxSpreadV, 12);
        Assert.Equal(0.0, spread.Value.MeanSpreadV, 12);
    }

    [Fact]
    public void AnAmplitudeModulatedTone_ReadsItsModulationDepthAtThePeaks()
    {
        // A 1 kHz tone of amplitude 1 whose amplitude falls linearly to 0.9 over twenty cycles. The
        // band's widest point is then 0.1 peak to peak, and it sits at the waveform's own peak --
        // which is the second half of what the reading says, and what tells amplitude modulation
        // from jitter.
        const int foldFrames = 192;
        const int folds = 20;
        var samples = new double[foldFrames * folds];
        for (int f = 0; f < folds; f++)
        {
            double amplitude = 1.0 - 0.1 * f / (folds - 1);
            for (int i = 0; i < foldFrames; i++)
                samples[f * foldFrames + i] = amplitude * Math.Sin(2.0 * Math.PI * i / foldFrames);
        }

        var spread = WaveformEye.Measure(Fold(samples, foldFrames), Rate);

        Assert.NotNull(spread);
        Assert.Equal(0.1, spread!.Value.MaxSpreadV, 9);
        // At the sine's own peak, a quarter of the way through the fold.
        Assert.Equal(48.0 / Rate, spread.Value.AtTimeS, 9);
        // The mean over the fold is the modulation depth times the mean |sin| -- taken over the
        // fold's own 192 samples, which is 0.063656 and not the continuous 2/pi of 0.063662. The
        // discrete figure is asserted because it is the one the code computes; a tolerance loose
        // enough to swallow the difference would be loose enough to hide a real error.
        double meanAbsSin = 0.0;
        for (int i = 0; i < foldFrames; i++)
            meanAbsSin += Math.Abs(Math.Sin(2.0 * Math.PI * i / foldFrames));
        meanAbsSin /= foldFrames;

        Assert.Equal(0.1 * meanAbsSin, spread.Value.MeanSpreadV, 12);
        Assert.Equal(0.1 * 2.0 / Math.PI, spread.Value.MeanSpreadV, 4);
    }

    [Fact]
    public void Measure_RefusesFoldsOfDifferentLengths()
    {
        // Position i in folds of unequal length is two different phases of the waveform, which is a
        // plausible wrong answer rather than an obvious one.
        Assert.Null(WaveformEye.Measure([new double[4], new double[5]], Rate));
    }

    [Fact]
    public void Measure_RefusesFewerThanTwoFolds()
    {
        Assert.Null(WaveformEye.Measure([new double[4]], Rate));
        Assert.Null(WaveformEye.Measure([], Rate));
    }

    [Fact]
    public void Measure_SkipsANonFiniteSampleRatherThanReadingItAsASpread()
    {
        double[][] folds = [[0.0, 1.0, 0.0], [0.0, double.NaN, 0.0]];

        var spread = WaveformEye.Measure(folds, Rate);

        Assert.NotNull(spread);
        Assert.Equal(0.0, spread!.Value.MaxSpreadV, 12);
    }

    [Fact]
    public void TheJitter_IsZeroForAToneThatRepeatsExactly()
    {
        int frames = 192 * 20;
        var samples = new double[frames];
        for (int i = 0; i < frames; i++)
            samples[i] = Math.Sin(2.0 * Math.PI * 1000.0 * i / Rate);

        double? jitter = WaveformEye.CrossingJitterS(Fold(samples, 192), Rate, 0.0);

        Assert.NotNull(jitter);
        Assert.Equal(0.0, jitter!.Value, 15);
    }

    [Fact]
    public void TheJitter_IsTheStandardDeviationOfTheCrossingsItMeasures()
    {
        // Two folds whose crossings are placed by hand, 1 and 3 samples in: the mean is 2 and the
        // population sigma is exactly one sample.
        double[][] folds =
        [
            [-1.0, 1.0, 1.0, 1.0, 1.0],
            [-1.0, -1.0, -1.0, 1.0, 1.0],
        ];

        double? jitter = WaveformEye.CrossingJitterS(folds, Rate, 0.0);

        Assert.NotNull(jitter);
        Assert.Equal(1.0 / Rate, jitter!.Value, 15);
    }

    [Fact]
    public void TheJitter_IsMeasuredBetweenSamplesRatherThanAtThem()
    {
        // Both folds cross between the same pair of samples -- at 0.25 and 0.75 of the way across.
        // An integer crossing index would read both as sample 1 and report no jitter at all, which
        // is the whole reason this is interpolated.
        double[][] folds =
        [
            [-0.25, 0.75, 1.0],
            [-0.75, 0.25, 1.0],
        ];

        double? jitter = WaveformEye.CrossingJitterS(folds, Rate, 0.0);

        Assert.NotNull(jitter);
        // Crossings at 0.25 and 0.75 of a sample: sigma is a quarter of a sample.
        Assert.Equal(0.25 / Rate, jitter!.Value, 15);
    }

    [Fact]
    public void TheJitter_IsAbsentWhereTooFewFoldsCrossTheLevel()
    {
        // A level above the waveform is crossed by nothing, and one crossing is a figure of
        // nothing: absent rather than zero, which would read as a perfectly stable signal.
        double[][] folds = [[-1.0, 1.0], [-1.0, 1.0]];

        Assert.Null(WaveformEye.CrossingJitterS(folds, Rate, 5.0));
        Assert.Null(WaveformEye.CrossingJitterS([[-1.0, 1.0]], Rate, 0.0));
    }

    [Fact]
    public void TheJitter_ReadsOnlyRisingCrossings()
    {
        // A falling crossing at the same level is a different point on the waveform, and mixing the
        // two would report half a period of "jitter" on a clean tone.
        double[][] folds = [[1.0, -1.0, 1.0], [1.0, -1.0, 1.0]];

        double? jitter = WaveformEye.CrossingJitterS(folds, Rate, 0.0);

        Assert.NotNull(jitter);
        Assert.Equal(0.0, jitter!.Value, 15);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void TheJitter_RefusesASampleRateThatIsNotOne(double rate) =>
        Assert.Null(WaveformEye.CrossingJitterS([[-1.0, 1.0], [-1.0, 1.0]], rate, 0.0));
}
