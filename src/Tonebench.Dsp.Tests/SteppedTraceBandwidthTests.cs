using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// <see cref="BandwidthAnalyzer"/>'s explicit-frequency overloads, which is what lets a stepped-sine
/// Bode plot — a handful of log-spaced points — be read for corners by the same code that reads a
/// full-resolution FFT trace.
/// </summary>
public class SteppedTraceBandwidthTests
{
    /// <summary>A uniform axis expressed the long way, for comparing the two overloads against each other.</summary>
    private static double[] UniformAxis(int length, double binHz)
    {
        var freqHz = new double[length];
        for (int i = 0; i < length; i++)
            freqHz[i] = i * binHz;

        return freqHz;
    }

    /// <summary>A single-pole response sampled on a log grid: flat, then −20 dB/decade past the corner.</summary>
    private static (double[] FreqHz, double[] MagnitudeDb) OnePoleHighCut(
        double cornerHz, double startHz, double endHz, int pointsPerOctave)
    {
        var freqHz = SweepPoints.LogPerOctave(startHz, endHz, pointsPerOctave);
        var magnitudeDb = new double[freqHz.Length];
        for (int i = 0; i < freqHz.Length; i++)
        {
            double ratio = freqHz[i] / cornerHz;
            magnitudeDb[i] = -10.0 * Math.Log10(1.0 + (ratio * ratio));
        }

        return (freqHz, magnitudeDb);
    }

    [Fact]
    public void OnAUniformGrid_TheTwoOverloadsAgreeExactly()
    {
        // The explicit axis is a generalisation, not a second algorithm: given the frequencies the
        // uniform overload would have computed, both must return the same numbers.
        const double BinHz = 1.46484375;

        var traceDb = new double[512];
        for (int i = 0; i < traceDb.Length; i++)
        {
            double freqHz = Math.Max(i * BinHz, 0.001);
            traceDb[i] = -10.0 * Math.Log10(1.0 + Math.Pow(freqHz / 200.0, 2.0));
        }

        var freqAxis = UniformAxis(traceDb.Length, BinHz);

        var uniformPeak = BandwidthAnalyzer.FindPeak(traceDb, BinHz);
        var explicitPeak = BandwidthAnalyzer.FindPeak(traceDb, freqAxis, firstIndex: 1);
        Assert.Equal(uniformPeak, explicitPeak);

        foreach (double dropDb in new[] { 1.0, 3.0 })
        {
            var uniform = BandwidthAnalyzer.Find(traceDb, BinHz, dropDb);
            var explicitAxis = BandwidthAnalyzer.Find(traceDb, freqAxis, dropDb, firstIndex: 1);
            Assert.Equal(uniform, explicitAxis);
        }
    }

    [Fact]
    public void OnALogGrid_TheThreeDbCornerLandsOnTheRealOne()
    {
        // A single pole is −3.01 dB exactly at its corner, so this is the one case whose answer is
        // known in closed form. 5 points per octave is the sweep panel's own default density; the
        // interpolation between two bracketing points is in log frequency, which is why a grid this
        // coarse still lands within a couple of percent.
        var (freqHz, magnitudeDb) = OnePoleHighCut(cornerHz: 20_000.0, startHz: 20.0, endHz: 200_000.0, pointsPerOctave: 5);

        var threeDb = BandwidthAnalyzer.Find(magnitudeDb, freqHz, dropDb: 3.0);

        Assert.False(threeDb.UpperClamped);
        Assert.InRange(threeDb.UpperHz, 19_400.0, 20_600.0);

        // Nothing rolls off at the bottom, so the lower corner is the edge of the search and says so
        // — the difference between "it rolls off at 20 Hz" and "it was still flat where we stopped".
        Assert.True(threeDb.LowerClamped);
        Assert.Equal(freqHz[0], threeDb.LowerHz, 6);
    }

    [Fact]
    public void OnALogGrid_TheOneDbCornerSitsBelowTheThreeDbOne()
    {
        var (freqHz, magnitudeDb) = OnePoleHighCut(cornerHz: 20_000.0, startHz: 20.0, endHz: 200_000.0, pointsPerOctave: 5);

        var oneDb = BandwidthAnalyzer.Find(magnitudeDb, freqHz, dropDb: 1.0);
        var threeDb = BandwidthAnalyzer.Find(magnitudeDb, freqHz, dropDb: 3.0);

        Assert.False(oneDb.UpperClamped);
        Assert.True(oneDb.UpperHz < threeDb.UpperHz);

        // A single pole is −1 dB at f_c/sqrt(10^0.1 − 1) ≈ 0.5088·f_c.
        Assert.InRange(oneDb.UpperHz, 9_900.0, 10_500.0);
    }

    [Fact]
    public void ThePeakIsReportedAtItsOwnFrequency_NotAtAnIndexTimesABinWidth()
    {
        // The bug this overload exists to avoid: on a log grid, index × spacing is meaningless, and
        // a peak reported that way lands wherever the arithmetic put it.
        var freqHz = new[] { 20.0, 100.0, 1000.0, 5000.0, 20000.0 };
        var magnitudeDb = new[] { -6.0, -1.0, 2.5, -1.0, -6.0 };

        var peak = BandwidthAnalyzer.FindPeak(magnitudeDb, freqHz);

        Assert.Equal(2, peak.Bin);
        Assert.Equal(1000.0, peak.FreqHz);
        Assert.Equal(2.5, peak.LevelDb);
    }

    [Fact]
    public void AMismatchedAxis_IsRejectedRatherThanReadPastTheEnd()
    {
        var traceDb = new double[8];
        var freqHz = new double[4];

        Assert.Throws<ArgumentException>(() => BandwidthAnalyzer.FindPeak(traceDb, freqHz));
        Assert.Throws<ArgumentException>(() => BandwidthAnalyzer.Find(traceDb, freqHz, dropDb: 3.0));
    }
}
