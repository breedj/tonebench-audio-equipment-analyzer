namespace Qa403.Dsp.Tests;

public class SpectrumDecimatorTests
{
    [Fact]
    public void DecimateLinear_SingleBinSpike_SurvivesTo800Columns()
    {
        const int n = 65537; // typical one-sided spectrum for a 128k-point real FFT
        var values = new double[n];
        Array.Fill(values, -140.0); // noise floor, dBV
        values[50000] = -3.0; // one huge spike

        var destination = new MinMax[800];
        SpectrumDecimator.DecimateLinear(values, destination);

        Assert.Contains(destination, mm => mm.Max >= -3.0);
    }

    [Fact]
    public void DecimateLog_SingleBinSpike_SurvivesTo800Columns()
    {
        const int n = 65537;
        var values = new double[n];
        Array.Fill(values, -140.0);
        values[1000] = -3.0; // spike in the low-frequency, finely-resolved region

        var destination = new MinMax[800];
        SpectrumDecimator.DecimateLog(values, destination, startBin: 1);

        Assert.Contains(destination, mm => mm.Max >= -3.0);
    }

    [Fact]
    public void Decimate_NeverProducesMeanValues()
    {
        // A mean-based implementation would report ~(-140*7 + -3)/8 ≈ -122.9, not -3, for a
        // column covering 8 bins with one spike. Min/max must preserve the extremes exactly.
        var values = new double[] { -140, -140, -140, -3, -140, -140, -140, -140 };
        var destination = new MinMax[1];

        SpectrumDecimator.DecimateLinear(values, destination);

        Assert.Equal(-140.0, destination[0].Min, 9);
        Assert.Equal(-3.0, destination[0].Max, 9);
    }

    [Fact]
    public void DecimateLinear_EveryBinIsAssignedToExactlyOneColumn_NoGaps()
    {
        const int n = 1000;
        var values = new double[n];
        for (int i = 0; i < n; i++)
            values[i] = i;

        var destination = new MinMax[37]; // deliberately awkward, doesn't divide evenly
        SpectrumDecimator.DecimateLinear(values, destination);

        // Column 0 must start at bin 0, last column must end at the last bin.
        Assert.Equal(0.0, destination[0].Min, 9);
        Assert.Equal((double)(n - 1), destination[^1].Max, 9);

        // Monotonic, non-overlapping coverage: each column's max <= next column's min doesn't
        // hold in general (columns can be single bins with equal values), but mins must be
        // non-decreasing since the data itself is monotonic increasing.
        for (int c = 1; c < destination.Length; c++)
            Assert.True(destination[c].Min >= destination[c - 1].Min);
    }

    [Fact]
    public void DecimateLog_RejectsStartBinZero()
    {
        var values = new double[100];
        var destination = new MinMax[10];

        Assert.Throws<ArgumentOutOfRangeException>(() => SpectrumDecimator.DecimateLog(values, destination, startBin: 0));
    }

    [Fact]
    public void Decimate_MinNeverExceedsMax()
    {
        var random = new Random(1234);
        var values = new double[10000];
        for (int i = 0; i < values.Length; i++)
            values[i] = random.NextDouble() * 200 - 160;

        var destination = new MinMax[123];
        SpectrumDecimator.DecimateLinear(values, destination);

        foreach (var mm in destination)
            Assert.True(mm.Min <= mm.Max);
    }
}
