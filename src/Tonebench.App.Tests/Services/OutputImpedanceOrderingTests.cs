using System.Numerics;
using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// <b>Smooth the gain ratio, then solve — never solve and then smooth the ohms.</b>
///
/// <para>This is the one rule the broadband output-impedance path cannot be written without, and it
/// is invisible on the simulator: with no noise in the capture both orders agree, so every oracle
/// test in <see cref="OutputImpedanceRunnerTests"/> passed against the wrong one. It showed up on a
/// real amplifier instead — Joost's EL84 push-pull at 1 W on 2026-10-28, where the stepped sine read
/// 0.894 Ω and a pink-noise run drew <b>1.24 Ω over 200 Hz – 1 kHz and 1.67 Ω at the bottom</b>,
/// while re-solving that very capture from its own smoothed gain passes gave 0.87 and 1.02.</para>
///
/// <para>The mechanism is that <c>Z = R₁·R₂·(q − 1)/(R₂ − R₁·q)</c> recovers ohms from a ratio
/// sitting within a decibel of unity — some 12 Ω of Z per unit of q — and then a <i>magnitude</i>
/// is taken. Noise moves q in every direction; the magnitude keeps the distance whichever way it
/// went, so each bin is biased <i>up</i>, and no later average can take that back out. Averaging q
/// instead removes the noise while it is still additive and zero mean.</para>
///
/// <para>So these tests put noise in deliberately, which is what the simulator cannot do for this
/// measurement, and they assert the <i>difference between the two orders</i> rather than a
/// tolerance — a tolerance loose enough for the old order to pass is loose enough to hide it.</para>
/// </summary>
public class OutputImpedanceOrderingTests
{
    private const double BinHz = 1.0;
    private const double FirstLoadOhms = 8.0;
    private const double SecondLoadOhms = 4.0;
    private const int CentreBin = 1000;

    /// <summary>The ratio q = G₂/G₁ an amplifier of this output impedance really produces.</summary>
    private static Complex TrueRatio(Complex zout) =>
        (SecondLoadOhms * (FirstLoadOhms + zout)) / (FirstLoadOhms * (SecondLoadOhms + zout));

    private static Complex[] NoisyRatio(Complex zout, double sigma, int n, int seed)
    {
        var q = TrueRatio(zout);
        var rng = new Random(seed);
        var ratio = new Complex[n];
        for (int k = 0; k < n; k++)
            ratio[k] = q + new Complex(Gaussian(rng) * sigma, Gaussian(rng) * sigma);

        return ratio;
    }

    /// <summary>What the broadband path used to do: solve every bin, then average the ohms.</summary>
    private static double SolveThenSmooth(Complex[] ratio, int denominator, FractionalOctaveSmoother.Band band)
    {
        var ohms = new double[ratio.Length];
        for (int k = 0; k < ratio.Length; k++)
            ohms[k] = OutputImpedanceMath.Solve(ratio[k], FirstLoadOhms, SecondLoadOhms).Magnitude;

        return FractionalOctaveSmoother.Smooth(ohms, BinHz, denominator, band)[CentreBin];
    }

    /// <summary>What it does now: average the ratio, then solve once.</summary>
    private static double SmoothThenSolve(Complex[] ratio, int denominator, FractionalOctaveSmoother.Band band)
    {
        var smoothed = FractionalOctaveSmoother.SmoothComplex(ratio, BinHz, denominator, band);
        return OutputImpedanceMath.Solve(smoothed[CentreBin], FirstLoadOhms, SecondLoadOhms).Magnitude;
    }

    /// <summary>
    /// The regression, stated as the bench saw it: a 0.9 Ω amplifier read through noise.
    ///
    /// <para><b>Averaged over several draws, because the two orders fail in different ways and that
    /// difference is the point.</b> The old order is <i>biased</i> — every draw reads high, so the
    /// average of them does too — while the new order merely <i>scatters</i> about the truth, so a
    /// single draw of it can land either side and only the average is meaningful. A test pinned to
    /// one seed would be asserting the size of that scatter, which is not the claim.</para>
    ///
    /// <para>The sigma is small in its own terms — 5 % on a ratio of order one — and it is the
    /// 12 Ω-per-unit amplification that makes it matter. Note how little the old order is helped by
    /// the smoothing it was relying on.</para>
    /// </summary>
    [Fact]
    public void SolvingPerBin_ReadsHigh_WhereSmoothingTheRatioFirstDoesNot()
    {
        var zout = new Complex(0.9, 0.0);
        var band = new FractionalOctaveSmoother.Band(1, 4095);

        var olds = new List<double>();
        var nows = new List<double>();
        for (int seed = 1; seed <= 12; seed++)
        {
            var ratio = NoisyRatio(zout, sigma: 0.05, n: 4096, seed: seed);
            olds.Add(SolveThenSmooth(ratio, 3, band));
            nows.Add(SmoothThenSolve(ratio, 3, band));
        }

        Assert.True(olds.Average() > 0.9 * 1.15,
            $"the old order should read high and averaged {olds.Average():0.0000} ohm");

        // Every single draw of it, not just the average: the bias is what makes that possible.
        Assert.True(olds.All(z => z > 0.9), $"the lowest old-order draw was {olds.Min():0.0000} ohm");

        Assert.True(Math.Abs(nows.Average() - 0.9) < 0.9 * 0.02,
            $"the new order averaged {nows.Average():0.0000} ohm against 0.9");

        // Scatter, not bias: the draws fall on both sides of the truth.
        Assert.Contains(nows, z => z < 0.9);
        Assert.Contains(nows, z => z > 0.9);
    }

    /// <summary>
    /// More averaging does not rescue the old order, which is what makes this a bias rather than
    /// noise — the narrower band is noisier and the wider one is no more correct.
    /// </summary>
    [Theory]
    [InlineData(48)]
    [InlineData(12)]
    [InlineData(3)]
    public void TheOldOrdersError_IsABias_ThatAveragingCannotRemove(int denominator)
    {
        var zout = new Complex(0.9, 0.0);
        var ratio = NoisyRatio(zout, sigma: 0.05, n: 4096, seed: 7);
        var band = new FractionalOctaveSmoother.Band(1, 4095);

        double old = SolveThenSmooth(ratio, denominator, band);
        double now = SmoothThenSolve(ratio, denominator, band);

        Assert.True(old > 0.9 * 1.10,
            $"1/{denominator} octave: the old order read {old:0.0000} ohm, so the bias has gone somewhere");
        Assert.True(now < old,
            $"1/{denominator} octave: smoothing the ratio read {now:0.0000}, no better than {old:0.0000}");
    }

    /// <summary>
    /// With no noise at all the two orders agree — which is the honest statement of why the
    /// simulator could never have caught this, and why the figures in the manual's accuracy table
    /// had to be re-measured rather than reasoned about.
    /// </summary>
    [Fact]
    public void WithoutNoise_BothOrdersAgree_WhichIsWhyTheSimulatorCouldNotCatchIt()
    {
        var zout = new Complex(0.9, 0.0);
        var ratio = NoisyRatio(zout, sigma: 0.0, n: 4096, seed: 1);
        var band = new FractionalOctaveSmoother.Band(1, 4095);

        Assert.Equal(SolveThenSmooth(ratio, 3, band), SmoothThenSolve(ratio, 3, band), 9);
    }

    /// <summary>
    /// A reactive Zout keeps its angle through the averaging: the ratio is smoothed as a vector, so
    /// the phase the reference channel exists to recover survives it.
    /// </summary>
    [Fact]
    public void AReactiveZout_KeepsItsAngle_ThroughTheSmoothing()
    {
        var zout = new Complex(2.0, 1.9);
        var ratio = NoisyRatio(zout, sigma: 0.02, n: 4096, seed: 99);
        var band = new FractionalOctaveSmoother.Band(1, 4095);

        var smoothed = FractionalOctaveSmoother.SmoothComplex(ratio, BinHz, 3, band);
        var z = OutputImpedanceMath.Solve(smoothed[CentreBin], FirstLoadOhms, SecondLoadOhms);

        Assert.True(Math.Abs(z.Magnitude - zout.Magnitude) < 0.05 * zout.Magnitude,
            $"read {z.Magnitude:0.000} ohm against {zout.Magnitude:0.000}");
        Assert.True(Math.Abs((z.Phase - zout.Phase) * 180.0 / Math.PI) < 2.0,
            $"read {z.Phase * 180.0 / Math.PI:0.0} degrees against {zout.Phase * 180.0 / Math.PI:0.0}");
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
