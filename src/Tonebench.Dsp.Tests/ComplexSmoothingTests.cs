using System.Numerics;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// <see cref="FractionalOctaveSmoother.SmoothComplex"/>: the arithmetic mean over a band, for a
/// quantity something downstream will treat nonlinearly.
///
/// <para>What separates it from every other smoother here is that it is a <b>mean</b> and not an
/// RMS or a magnitude-weighted average, so noise that moved a bin in any direction cancels instead
/// of accumulating. The first two tests pin exactly that, because an RMS passes almost everything
/// else one could think to assert.</para>
/// </summary>
public class ComplexSmoothingTests
{
    private const double BinHz = 1.0;

    private static Complex[] Constant(int n, Complex value) =>
        Enumerable.Repeat(value, n).ToArray();

    /// <summary>
    /// The one property an RMS cannot have: phasors that cancel must average to nothing.
    ///
    /// <para>Alternating +1 and -1 has a mean of zero and an RMS of one, so this fails outright
    /// against a smoother that squares before it averages - which is what the magnitude path does
    /// and must keep doing, since a spectrum's level really is an RMS over its band.</para>
    /// </summary>
    [Fact]
    public void SmoothComplex_OverBinsThatCancel_IsAMeanAndNotAnRms()
    {
        int n = 4096;
        var values = new Complex[n];
        for (int k = 0; k < n; k++)
            values[k] = new Complex((k % 2 == 0) ? 1.0 : -1.0, 0.0);

        var smoothed = FractionalOctaveSmoother.SmoothComplex(values, BinHz, 3);

        // A 1/3-octave band at bin 1000 spans hundreds of bins, so the two signs very nearly match.
        Assert.True(smoothed[1000].Magnitude < 0.02, $"read {smoothed[1000].Magnitude}, so it is not a mean");

        var rms = FractionalOctaveSmoother.Smooth(values.Select(v => v.Magnitude).ToArray(), BinHz, 3);
        Assert.True(rms[1000] > 0.9, $"the magnitude smoother should still read ~1 here, not {rms[1000]}");
    }

    /// <summary>
    /// Zero-mean noise on a constant value averages away, and what is left is the value - which is
    /// the whole reason a caller smooths before it maps rather than after.
    /// </summary>
    [Fact]
    public void SmoothComplex_RemovesZeroMeanNoise()
    {
        int n = 4096;
        var truth = new Complex(0.9, -0.2);
        var rng = new Random(20261028);
        var values = new Complex[n];
        for (int k = 0; k < n; k++)
            values[k] = truth + new Complex(Gaussian(rng) * 0.3, Gaussian(rng) * 0.3);

        var smoothed = FractionalOctaveSmoother.SmoothComplex(values, BinHz, 3);

        // Asserted against the band's own noise rather than at an absolute tolerance: the claim is
        // that averaging removes it, and the 1/3-octave band at bin 1000 covers 232 bins, so what
        // is left should be roughly sqrt(232) smaller. An RMS over the same bins would converge on
        // the noise instead of on the value, which is the whole distinction being drawn here.
        double rawRms = Math.Sqrt(Enumerable.Range(891, 232)
            .Average(k => Math.Pow((values[k] - truth).Magnitude, 2.0)));
        double left = (smoothed[1000] - truth).Magnitude;

        Assert.True(left < rawRms / 8.0, $"the band's noise is {rawRms:0.0000} and {left:0.0000} is left");
        Assert.True(left < 0.05, $"read {smoothed[1000]} against a truth of {truth}");
    }

    [Fact]
    public void SmoothComplex_WithDenominatorZero_LeavesEveryBinAlone()
    {
        var values = Constant(512, new Complex(2.0, 1.0));
        values[100] = new Complex(-5.0, 7.0);

        var smoothed = FractionalOctaveSmoother.SmoothComplex(values, BinHz, 0);

        Assert.Equal(values, smoothed);
    }

    /// <summary>
    /// A band keeps the smoother inside it, exactly as the magnitude and phase smoothers are kept:
    /// a column at the edge must not be dragged toward whatever lies outside the analysed range.
    /// </summary>
    [Fact]
    public void SmoothComplex_DoesNotReachPastTheAnalysedBand()
    {
        int n = 4096;
        var values = Constant(n, Complex.One);
        for (int k = 2000; k < n; k++)
            values[k] = new Complex(1000.0, 0.0);

        var band = new FractionalOctaveSmoother.Band(1, 1999);
        var smoothed = FractionalOctaveSmoother.SmoothComplex(values, BinHz, 3, band);

        // Bin 1900's own 1/3-octave band runs well past 2000 and would otherwise carry the wall in.
        Assert.True(Math.Abs(smoothed[1900].Real - 1.0) < 1e-9,
            $"bin 1900 read {smoothed[1900].Real}, so the band was not respected");
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
