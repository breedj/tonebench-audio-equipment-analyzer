using System.Numerics;
using Qa403.Dsp;

namespace Qa403.Dsp.Tests;

/// <summary>
/// The unwrap rule shared by <see cref="FrequencyResponseEstimator"/> (across FFT bins) and the
/// stepped-sine response (across sweep points). Extracted so the two cannot come to disagree about
/// a DUT that inverts.
/// </summary>
public class PhaseUnwrapperTests
{
    /// <summary>The loop that used to live inline in <see cref="FrequencyResponseEstimator"/>, kept here as the reference behaviour.</summary>
    private static double[] UnwrapTheOldWay(IReadOnlyList<double> rawDeg)
    {
        var result = new double[rawDeg.Count];
        double previousDeg = 0.0;
        double turns = 0.0;

        for (int k = 0; k < rawDeg.Count; k++)
        {
            double raw = rawDeg[k];
            if (k > 0)
            {
                double step = raw + turns - previousDeg;
                if (step > 180.0)
                    turns -= 360.0 * Math.Ceiling((step - 180.0) / 360.0);
                else if (step < -180.0)
                    turns += 360.0 * Math.Ceiling((-step - 180.0) / 360.0);
            }

            result[k] = raw + turns;
            previousDeg = result[k];
        }

        return result;
    }

    [Fact]
    public void ItMatchesTheInlineLoopItReplaced()
    {
        // A steadily accumulating phase, wrapped to (−180, 180] the way an angle off a complex
        // number arrives — several turns' worth, so the branch that adds more than one turn at a
        // time is exercised too.
        var raw = new double[400];
        for (int i = 0; i < raw.Length; i++)
        {
            double continuous = -1.7 * i * i / 10.0;
            raw[i] = Complex.FromPolarCoordinates(1.0, continuous * Math.PI / 180.0).Phase * (180.0 / Math.PI);
        }

        var expected = UnwrapTheOldWay(raw);

        var unwrapper = new PhaseUnwrapper();
        for (int i = 0; i < raw.Length; i++)
            Assert.Equal(expected[i], unwrapper.Next(raw[i]), 9);

        var bulk = new double[raw.Length];
        PhaseUnwrapper.Unwrap(raw, bulk);
        Assert.Equal(expected, bulk);
    }

    [Fact]
    public void AContinuousRampComesBackContinuous()
    {
        // The point of unwrapping: a response that accumulates several turns reads as a curve rather
        // than a sawtooth that resets every 360°.
        var unwrapper = new PhaseUnwrapper();

        double previous = double.NaN;
        for (int i = 0; i < 200; i++)
        {
            double continuousDeg = -13.0 * i;
            double wrappedDeg = Complex.FromPolarCoordinates(1.0, continuousDeg * Math.PI / 180.0).Phase * (180.0 / Math.PI);

            double unwrapped = unwrapper.Next(wrappedDeg);
            Assert.Equal(continuousDeg, unwrapped, 8);

            if (!double.IsNaN(previous))
                Assert.InRange(Math.Abs(unwrapped - previous), 0.0, 180.0);

            previous = unwrapped;
        }
    }

    [Fact]
    public void TheFirstSampleIsTakenAsGiven()
    {
        // Nothing precedes it, so there is no branch to keep it near — the curve starts wherever the
        // first angle is.
        Assert.Equal(-170.0, new PhaseUnwrapper().Next(-170.0));
        Assert.Equal(170.0, new PhaseUnwrapper().Next(170.0));
    }

    [Fact]
    public void ResetStartsAFreshCurve()
    {
        // 170° followed by −170° is a 20° step across the wrap, not a 340° jump back.
        var unwrapper = new PhaseUnwrapper();
        unwrapper.Next(170.0);
        Assert.Equal(190.0, unwrapper.Next(-170.0), 9);

        unwrapper.Reset();
        Assert.Equal(-170.0, unwrapper.Next(-170.0));
    }

    [Fact]
    public void NextFromRadians_IsNextInDegrees()
    {
        var fromRadians = new PhaseUnwrapper();
        var fromDegrees = new PhaseUnwrapper();

        foreach (double rad in new[] { 0.5, 2.9, -3.0, 1.2, -2.8 })
            Assert.Equal(fromDegrees.Next(rad * 180.0 / Math.PI), fromRadians.NextFromRadians(rad), 9);
    }

    [Fact]
    public void ADestinationShorterThanTheSource_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => PhaseUnwrapper.Unwrap(new double[4], new double[2]));
    }
}
