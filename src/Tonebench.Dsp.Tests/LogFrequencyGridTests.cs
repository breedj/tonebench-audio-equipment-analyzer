using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The log-spaced display grid both decay surfaces are built on, and the reduction of an FFT's
/// linear bins onto it.
/// </summary>
public class LogFrequencyGridTests
{
    [Fact]
    public void Columns_AreGeometricallySpacedInsideTheBand()
    {
        float[] columns = LogFrequencyGrid.Columns(20, 20000, 30);

        Assert.Equal(30, columns.Length);
        Assert.InRange(columns[0], 20, 20000);
        Assert.InRange(columns[^1], 20, 20000);

        double ratio = columns[1] / columns[0];
        for (int c = 1; c < columns.Length; c++)
            Assert.Equal(ratio, columns[c] / columns[c - 1], 4);
    }

    /// <summary>
    /// The max, not a mean: an average flattens a single-bin resonance into the floor around it,
    /// and a resonance is the one thing a decay surface is looked at for.
    /// </summary>
    [Fact]
    public void ReduceMax_WhereAColumnHoldsSeveralBins_KeepsTheLoudestOfThem()
    {
        var bins = new double[200];
        for (int k = 0; k < bins.Length; k++) bins[k] = 0.001;
        bins[150] = 1.0;

        var columns = new float[20];
        LogFrequencyGrid.ReduceMax(bins, binHz: 10, lowHz: 20, highHz: 1990, columns);

        Assert.Equal(1.0, columns.Max(), 6);
    }

    /// <summary>
    /// The one invariant both branches share: a spectrum with the same magnitude in every bin
    /// reads that magnitude at every column, whether the column holds bins of its own or is
    /// interpolated between two of them.
    /// </summary>
    [Fact]
    public void ReduceMax_OverAFlatSpectrum_IsFlat()
    {
        var bins = new double[513];
        Array.Fill(bins, 0.25);

        // 187.5 Hz bins against 400 columns over 20 Hz - 1 kHz: five bins, so most columns hold
        // none and every one of them is interpolated.
        var columns = new float[400];
        LogFrequencyGrid.ReduceMax(bins, binHz: 192000.0 / 1024, lowHz: 20, highHz: 1000, columns);

        Assert.All(columns, value => Assert.Equal(0.25, value, 5));
    }

    /// <summary>
    /// The regression this grid exists in its own file for. A cumulative spectral decay has one
    /// linear FFT and <c>sampleRate / fftLength</c> between its bins — 187.5 Hz at 1024 points and
    /// 192 kHz, i.e. five bins across 20 Hz – 1 kHz against 400 columns — so a column that holds no
    /// bin used to take the nearest one and eighty columns in a row repeated a single value. The
    /// staircase that drew was reported as a broken chart beside a burst decay, which is smooth
    /// because its wavelet is evaluated at every column.
    /// </summary>
    [Fact]
    public void ReduceMax_WithFarFewerBinsThanColumns_DrawsNoStaircase()
    {
        const double binHz = 192000.0 / 1024;
        var bins = new double[513];
        for (int k = 0; k < bins.Length; k++) bins[k] = Math.Pow(10, -k / 4.0);

        var columns = new float[400];
        LogFrequencyGrid.ReduceMax(bins, binHz, lowHz: 20, highHz: 1000, columns);

        int repeats = 0;
        for (int c = 1; c < columns.Length; c++)
            if (columns[c] == columns[c - 1])
                repeats++;

        Assert.True(repeats < 20, $"{repeats} of 399 neighbouring columns repeat their neighbour's value.");
    }

    /// <summary>
    /// No extremum is invented: an interpolated column lies between the two bins either side of it,
    /// which is the whole licence for smoothing the staircase away.
    /// </summary>
    [Fact]
    public void ReduceMax_NeverAnswersOutsideTheBinsAroundIt()
    {
        const double binHz = 100;
        var bins = new double[] { 0.5, 1.0, 0.1, 0.9, 0.2, 0.7 };

        var columns = new float[200];
        LogFrequencyGrid.ReduceMax(bins, binHz, lowHz: 110, highHz: 490, columns);

        Assert.All(columns, value => Assert.InRange(value, bins.Min(), bins.Max()));
    }

    /// <summary>Straight in dB, because the surface is read in dB and that is the one reading under
    /// which the drawn segment is actually straight.</summary>
    [Fact]
    public void ReduceMax_BetweenTwoBins_IsStraightInDecibels()
    {
        const double binHz = 1000;
        // Bin 1 is 0 dB and bin 2 is -40 dB, so a column between them should read -40 x however far
        // along it sits. Every column of this band falls inside that gap.
        var bins = new double[] { 1.0, 1.0, 0.01, 1.0 };

        var columns = new float[16];
        LogFrequencyGrid.ReduceMax(bins, binHz, lowHz: 1100, highHz: 1900, columns);

        float[] centres = LogFrequencyGrid.Columns(1100, 1900, 16);
        for (int c = 0; c < columns.Length; c++)
        {
            double fraction = centres[c] / binHz - 1;
            Assert.Equal(-40 * fraction, 20 * Math.Log10(columns[c]), 3);
        }
    }

    [Fact]
    public void ReduceMax_AcrossAnEmptyBin_StaysFinite()
    {
        const double binHz = 1000;
        var bins = new double[] { 1.0, 0.0, 1.0, 1.0 };

        var columns = new float[50];
        LogFrequencyGrid.ReduceMax(bins, binHz, lowHz: 1100, highHz: 1900, columns);

        Assert.All(columns, value => Assert.True(float.IsFinite(value), $"{value} is not finite."));
    }
}
