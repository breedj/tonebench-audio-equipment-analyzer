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
    public void DecimateLogWithCenters_MatchesDecimateLog_MinMaxUnchanged()
    {
        const int n = 65537;
        var values = new double[n];
        Array.Fill(values, -140.0);
        values[1000] = -3.0;

        var plain = new MinMax[800];
        SpectrumDecimator.DecimateLog(values, plain, startBin: 1);

        var withCenters = new MinMax[800];
        var centers = new double[800];
        SpectrumDecimator.DecimateLogWithCenters(values, withCenters, centers, startBin: 1);

        Assert.Equal(plain, withCenters);
    }

    [Fact]
    public void DecimateLogWithCenters_ReportsTrueBinEvenWhenColumnsOutnumberAvailableBins()
    {
        // Reproduces the regime that broke the client's continuous log-formula axis reconstruction:
        // a small FFT (here, the one-sided length for a 4096-point transform) decimated to far more
        // display columns than there are low-frequency bins to spread across. In that regime most
        // early columns collapse to single, adjacent bins — nothing like a smooth exponential curve
        // — so only the actual reported center can be trusted for a chart's frequency axis.
        const int specLen = 2049; // 4096-point real FFT -> N/2+1
        const int spikeBin = 171; // ~2000 Hz at 48 kHz / 4096
        var values = new double[specLen];
        Array.Fill(values, -140.0);
        values[spikeBin] = -3.0;

        var destination = new MinMax[1600];
        var centers = new double[1600];
        SpectrumDecimator.DecimateLogWithCenters(values, destination, centers, startBin: 1);

        int spikeColumn = Array.FindIndex(destination, mm => mm.Max >= -3.0);
        Assert.True(spikeColumn >= 0, "Spike did not survive decimation.");

        // The column's reported center bin must be near the true spike bin (within the column's
        // own width), not off by orders of magnitude the way a continuous log-interpolation would
        // be in this bin-scarce regime.
        Assert.InRange(centers[spikeColumn], spikeBin - 2.0, spikeBin + 2.0);
    }

    [Fact]
    public void DecimateLogWithCenters_CentersAreMonotonicallyNonDecreasing()
    {
        const int n = 65537;
        var values = new double[n];
        for (int i = 0; i < n; i++)
            values[i] = i;

        var destination = new MinMax[500];
        var centers = new double[500];
        SpectrumDecimator.DecimateLogWithCenters(values, destination, centers, startBin: 1);

        for (int c = 1; c < centers.Length; c++)
            Assert.True(centers[c] >= centers[c - 1], $"centers[{c}]={centers[c]} < centers[{c - 1}]={centers[c - 1]}");
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
