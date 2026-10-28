using System.Numerics;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// <b>Average the complex Z over the smoothing band, then take the magnitude — never the other way
/// round.</b> The output-impedance measurement's rule, arriving on the screen it was found from.
///
/// <para>Both screens recover ohms from a measured complex ratio and then draw <c>|Z|</c>, and a
/// magnitude keeps the distance noise moved a bin in <i>any</i> direction — so every bin is biased
/// upward, and an RMS over a fractional-octave band of already-rectified values keeps the whole of
/// that bias. The two differ only in how much they amplify it on the way:
/// <c>Z = R·H/(1 − H)</c> is nearly linear for a DUT well below the reference resistor, where
/// <c>Z = R₁·R₂·(q − 1)/(R₂ − R₁·q)</c> is divergent. So this one is milder and not absent, and the
/// case where it bites is exactly the one a bench meets — a sub-ohm DUT on a 100 Ω reference, where
/// <c>|H| ≈ 0.001</c> and there is almost no signal per bin to begin with.</para>
///
/// <para>Measured end to end against the simulator's shorted-leads fixture (0.12 Ω + 0.6 µH on
/// 100 Ω) with its own noise and hum on, pink noise, 1/12 octave, eight records: at −30 dBV the
/// band median read <b>0.1340 Ω against a true 0.1313</b> with <b>72.6 % of columns high</b>, and
/// widening the band to 1/3 octave — four times the bins — moved the mean error only from +0.72 %
/// to +0.48 %. That is the signature: averaging cannot undo a rectification.</para>
///
/// <para>These tests put the noise in deliberately and assert the <i>difference between the two
/// orders</i> rather than a tolerance, because a tolerance loose enough for the old order to pass
/// is loose enough to hide it.</para>
/// </summary>
public class ImpedanceOrderingTests
{
    private const double BinHz = 1.0;
    private const double ReferenceOhms = 100.0;
    private const int CentreBin = 1000;

    private static ImpedanceSolveOptions Options() =>
        new() { ReferenceOhms = ReferenceOhms, BinHz = BinHz };

    /// <summary>The divider ratio a DUT of this impedance really produces: H = Z/(R + Z).</summary>
    private static Complex TrueRatio(Complex zout) => zout / (ReferenceOhms + zout);

    private static Complex[] NoisyRatio(Complex zout, double sigma, int n, int seed)
    {
        var h = TrueRatio(zout);
        var rng = new Random(seed);
        var ratio = new Complex[n];
        for (int k = 0; k < n; k++)
            ratio[k] = h + new Complex(Gaussian(rng) * sigma, Gaussian(rng) * sigma);

        return ratio;
    }

    /// <summary>What the broadband path used to do: solve every bin, then RMS the ohms.</summary>
    private static double SolveThenSmooth(Complex[] h, int denominator, FractionalOctaveSmoother.Band band)
    {
        var z = ImpedanceMath.Solve(h, Options());
        var ohms = new double[z.Length];
        for (int k = 0; k < z.Length; k++)
            ohms[k] = z[k].Magnitude;

        return FractionalOctaveSmoother.Smooth(ohms, BinHz, denominator, band)[CentreBin];
    }

    /// <summary>What it does now: solve every bin, average Z as a vector, and take the magnitude last.</summary>
    private static double SmoothThenMagnitude(Complex[] h, int denominator, FractionalOctaveSmoother.Band band)
    {
        var z = ImpedanceMath.Solve(h, Options());
        return FractionalOctaveSmoother.SmoothComplex(z, BinHz, denominator, band)[CentreBin].Magnitude;
    }

    /// <summary>
    /// The bench case: a sub-ohm DUT on a 100 Ω reference, read through noise.
    ///
    /// <para>Averaged over several draws because the two orders fail in different ways — the old
    /// one is <i>biased</i>, so every draw reads high and so does their average, while the new one
    /// merely <i>scatters</i> about the truth and only its average is meaningful.</para>
    /// </summary>
    [Fact]
    public void SmoothingTheOhms_ReadsHigh_WhereAveragingTheComplexZDoesNot()
    {
        var zdut = new Complex(0.12, 0.0);
        var band = new FractionalOctaveSmoother.Band(1, 4095);

        // 1/3 octave and two dozen draws, because the claim is about the two orders' *means* and
        // the new one scatters: a 1/12-octave band leaves each draw ~6 % wide, so twelve of them
        // bound the mean no better than the effect being measured.
        var olds = new List<double>();
        var nows = new List<double>();
        for (int seed = 1; seed <= 24; seed++)
        {
            // 0.0006 on a ratio whose own magnitude is 0.0012 - half of it, which is what a sub-ohm
            // DUT at a low drive really looks like per bin before the band averages anything.
            var h = NoisyRatio(zdut, sigma: 0.0006, n: 4096, seed: seed);
            olds.Add(SolveThenSmooth(h, 3, band));
            nows.Add(SmoothThenMagnitude(h, 3, band));
        }

        Assert.True(olds.Average() > 0.12 * 1.10,
            $"the old order should read high and averaged {olds.Average():0.0000} ohm");

        // Every single draw, not just the average: the bias is what makes that possible.
        Assert.True(olds.All(z => z > 0.12), $"the lowest old-order draw was {olds.Min():0.0000} ohm");

        Assert.True(Math.Abs(nows.Average() - 0.12) < 0.12 * 0.02,
            $"the new order averaged {nows.Average():0.0000} ohm against 0.12");

        // Scatter, not bias: the draws fall on both sides of the truth.
        Assert.Contains(nows, z => z < 0.12);
        Assert.Contains(nows, z => z > 0.12);
    }

    /// <summary>
    /// A wider band buys the old order almost nothing, which is what says the error is a bias.
    /// Four times the bins should halve a scatter twice over.
    /// </summary>
    [Theory]
    [InlineData(48)]
    [InlineData(12)]
    [InlineData(3)]
    public void TheOldOrdersError_IsABias_ThatAWiderBandCannotRemove(int denominator)
    {
        var zdut = new Complex(0.12, 0.0);
        var band = new FractionalOctaveSmoother.Band(1, 4095);
        var h = NoisyRatio(zdut, sigma: 0.0006, n: 4096, seed: 4242);

        double old = SolveThenSmooth(h, denominator, band);
        double now = SmoothThenMagnitude(h, denominator, band);

        Assert.True(old > 0.12 * 1.05,
            $"1/{denominator} octave: the old order read {old:0.0000} ohm, so the bias has gone somewhere");
        Assert.True(now < old,
            $"1/{denominator} octave: averaging the complex Z read {now:0.0000}, no better than {old:0.0000}");
    }

    /// <summary>
    /// A DUT well clear of the noise is unaffected either way, which is why this was never visible
    /// on the 8 Ω fixture the accuracy table is measured against: at 36 dB more signal per bin the
    /// two orders agree to a hundredth of a percent.
    /// </summary>
    [Fact]
    public void AWellConditionedDut_ReadsTheSameUnderBothOrders()
    {
        var band = new FractionalOctaveSmoother.Band(1, 4095);
        var h = NoisyRatio(new Complex(8.0, 0.0), sigma: 0.0006, n: 4096, seed: 7);

        double old = SolveThenSmooth(h, 12, band);
        double now = SmoothThenMagnitude(h, 12, band);

        Assert.True(Math.Abs(old - now) < 8.0 * 0.0005,
            $"old {old:0.0000}, new {now:0.0000} - the two should agree on a loud DUT");
        Assert.True(Math.Abs(now - 8.0) < 8.0 * 0.002, $"read {now:0.0000} ohm against 8");
    }

    /// <summary>
    /// With no noise at all the two orders agree, which is the honest statement of why neither the
    /// simulator's quiet fixtures nor the manual's accuracy table could have caught this.
    /// </summary>
    [Fact]
    public void WithoutNoise_BothOrdersAgree()
    {
        var band = new FractionalOctaveSmoother.Band(1, 4095);
        var h = NoisyRatio(new Complex(0.12, 0.0), sigma: 0.0, n: 4096, seed: 1);

        Assert.Equal(SolveThenSmooth(h, 12, band), SmoothThenMagnitude(h, 12, band), 9);
    }

    /// <summary>
    /// A reactive DUT keeps its angle through the averaging: Z is smoothed as a vector, so the
    /// phase the impedance screen's whole second axis is about survives it.
    /// </summary>
    [Fact]
    public void AReactiveDut_KeepsItsAngle_ThroughTheSmoothing()
    {
        var zdut = Complex.FromPolarCoordinates(6.0, 45.0 * Math.PI / 180.0);
        var band = new FractionalOctaveSmoother.Band(1, 4095);
        var h = NoisyRatio(zdut, sigma: 0.0006, n: 4096, seed: 99);

        var z = ImpedanceMath.Solve(h, Options());
        var smoothed = FractionalOctaveSmoother.SmoothComplex(z, BinHz, 12, band)[CentreBin];

        Assert.True(Math.Abs(smoothed.Magnitude - 6.0) < 6.0 * 0.02,
            $"read {smoothed.Magnitude:0.000} ohm against 6.000");
        Assert.True(Math.Abs((smoothed.Phase * 180.0 / Math.PI) - 45.0) < 1.0,
            $"read {smoothed.Phase * 180.0 / Math.PI:0.0} degrees against 45");
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
