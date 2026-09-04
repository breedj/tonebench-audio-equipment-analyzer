namespace Qa403.Dsp.Tests;

public class FractionalOctaveSmootherTests
{
    private const double BinHz = 1.46484375;

    [Fact]
    public void Smooth_WithDenominatorZero_PassesThrough()
    {
        double[] magnitude = [1.0, 2.0, 0.5, 8.0, 3.0];

        var smoothed = FractionalOctaveSmoother.Smooth(magnitude, BinHz, denominator: 0);

        Assert.Equal(magnitude, smoothed);
    }

    [Fact]
    public void Smooth_OfAFlatTrace_StaysFlat()
    {
        var magnitude = new double[4096];
        Array.Fill(magnitude, 0.25);

        var smoothed = FractionalOctaveSmoother.Smooth(magnitude, BinHz, denominator: 3);

        for (int k = 1; k < magnitude.Length; k++)
            Assert.Equal(0.25, smoothed[k], 12);
    }

    [Fact]
    public void Smooth_PreservesTotalPower_RatherThanPullingDownIntoTheNulls()
    {
        // The reason this averages power and not decibels. A trace alternating between two levels
        // must smooth to their RMS; averaging dB would land it at their geometric mean, which is
        // 1.4 dB lower here and biases every smoothed response pessimistically.
        var magnitude = new double[8192];
        for (int k = 0; k < magnitude.Length; k++)
            magnitude[k] = (k % 2 == 0) ? 1.0 : 0.5;

        var smoothed = FractionalOctaveSmoother.Smooth(magnitude, BinHz, denominator: 6);

        double expected = Math.Sqrt(((1.0 * 1.0) + (0.5 * 0.5)) / 2.0);
        for (int k = 2000; k < magnitude.Length; k += 500)
            Assert.Equal(expected, smoothed[k], 2);
    }

    [Fact]
    public void Smooth_SpreadsASpikeOverTheBandAndKeepsItsEnergy()
    {
        var magnitude = new double[8192];
        const int spike = 4000;
        magnitude[spike] = 1.0;

        var smoothed = FractionalOctaveSmoother.Smooth(magnitude, BinHz, denominator: 12);

        // 1/12 octave at bin 4000 spans bin 4000/2^(1/24) .. 4000*2^(1/24), about 234 bins.
        int low = (int)Math.Ceiling(spike / Math.Pow(2.0, 1.0 / 24.0));
        int high = (int)Math.Floor(spike * Math.Pow(2.0, 1.0 / 24.0));
        int width = high - low + 1;

        Assert.Equal(Math.Sqrt(1.0 / width), smoothed[spike], 9);

        // The band's width in *bins* grows with centre frequency, so the outermost bins that still
        // see the spike average it over a slightly narrower band and read slightly higher. What
        // matters is that the spread stops exactly where the band arithmetic says it should.
        Assert.True(smoothed[low] > 0.0);
        Assert.True(smoothed[high] > 0.0);
        Assert.Equal(0.0, smoothed[low - 1], 12);
        Assert.Equal(0.0, smoothed[high + 1], 12);
    }

    [Fact]
    public void Smooth_WhereTheBandIsNarrowerThanABin_KeepsFullResolution()
    {
        // At 1/24 octave a band is narrower than one bin below about bin 34, which is most of the
        // bottom decade at these resolutions. Blanking it would erase the low end of every trace.
        var magnitude = new double[512];
        for (int k = 0; k < magnitude.Length; k++)
            magnitude[k] = k;

        var smoothed = FractionalOctaveSmoother.Smooth(magnitude, BinHz, denominator: 24);

        for (int k = 1; k <= 10; k++)
            Assert.Equal(magnitude[k], smoothed[k], 12);
    }

    [Fact]
    public void Smooth_WiderDenominator_SmoothsMore()
    {
        var random = new Random(11);
        var magnitude = new double[8192];
        for (int k = 0; k < magnitude.Length; k++)
            magnitude[k] = 1.0 + ((random.NextDouble() - 0.5) * 0.4);

        double Ripple(int denominator)
        {
            var smoothed = FractionalOctaveSmoother.Smooth(magnitude, BinHz, denominator);
            double min = double.MaxValue, max = double.MinValue;
            for (int k = 2000; k < magnitude.Length; k++)
            {
                min = Math.Min(min, smoothed[k]);
                max = Math.Max(max, smoothed[k]);
            }
            return max - min;
        }

        double none = Ripple(0);
        double narrow = Ripple(48);
        double wide = Ripple(3);

        Assert.True(narrow < none, $"1/48 octave should smooth: {narrow} vs {none}");
        Assert.True(wide < narrow, $"1/3 octave should smooth more than 1/48: {wide} vs {narrow}");
    }

    [Fact]
    public void Smooth_LeavesDcAlone()
    {
        var magnitude = new double[256];
        magnitude[0] = 42.0;

        var smoothed = FractionalOctaveSmoother.Smooth(magnitude, BinHz, denominator: 3);

        Assert.Equal(42.0, smoothed[0]);
    }

    [Fact]
    public void Smooth_InPlace_IsRejectedByLengthMismatchOnly()
    {
        var magnitude = new double[64];
        Assert.Throws<ArgumentException>(
            () => FractionalOctaveSmoother.Smooth(magnitude, new double[32], BinHz, 3));
    }

    [Fact]
    public void SupportedDenominators_MatchTheUisSmoothingChoices() =>
        Assert.Equal([0, 69, 48, 24, 12, 6, 3], FractionalOctaveSmoother.SupportedDenominators);
}
