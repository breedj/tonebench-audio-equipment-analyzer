namespace Qa403.Dsp.Tests;

public class BandwidthAnalyzerTests
{
    private const double BinHz = 1.0;

    [Fact]
    public void FindPeak_SkipsDc()
    {
        // DC carries the DUT's offset, not its response, and is routinely the largest value there.
        double[] trace = [100.0, 0.0, 5.0, 2.0];

        var peak = BandwidthAnalyzer.FindPeak(trace, BinHz);

        Assert.Equal(2, peak.Bin);
        Assert.Equal(5.0, peak.LevelDb);
        Assert.Equal(2.0, peak.FreqHz);
    }

    [Fact]
    public void Find_OnAnIdealBandpass_RecoversBothCorners()
    {
        // A flat plateau from bin 100 to 900 with 1 dB/bin skirts either side, so the -3 dB points
        // sit exactly 3 bins outside the plateau.
        var trace = new double[1024];
        for (int k = 1; k < trace.Length; k++)
        {
            if (k < 100)
                trace[k] = -(100 - k);
            else if (k > 900)
                trace[k] = -(k - 900);
            else
                trace[k] = 0.0;
        }

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0);

        Assert.Equal(97.0, result.LowerHz, 6);
        Assert.Equal(903.0, result.UpperHz, 6);
        Assert.Equal(806.0, result.BandwidthHz, 6);
        Assert.False(result.LowerClamped);
        Assert.False(result.UpperClamped);
    }

    [Fact]
    public void Find_InterpolatesBetweenBins()
    {
        // The threshold falls halfway between two bins in level; the reported frequency must land
        // between them rather than snapping to either. Interpolation is in log-f, so the answer is
        // the geometric mean of the two bin frequencies, not the arithmetic one.
        var trace = new double[64];
        for (int k = 1; k < trace.Length; k++)
            trace[k] = 0.0;
        trace[40] = -2.0;
        trace[41] = -4.0;
        for (int k = 42; k < trace.Length; k++)
            trace[k] = -20.0;

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0, firstBin: 1, lastBin: 63);

        Assert.Equal(Math.Sqrt(40.0 * 41.0), result.UpperHz, 6);
    }

    [Fact]
    public void Find_StopsAtTheFirstCrossing_NotTheNearestValueAnywhere()
    {
        // The reference app searched every bin for whichever came closest in level to the threshold.
        // Here a deep notch well past the roll-off sits at exactly -3 dB; that must not be mistaken
        // for the corner, which is at bin 20.
        var trace = new double[256];
        for (int k = 1; k < trace.Length; k++)
            trace[k] = k <= 20 ? 0.0 : -30.0;
        trace[200] = -3.0;

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0);

        Assert.InRange(result.UpperHz, 20.0, 21.0);
    }

    [Fact]
    public void Find_WhenTheResponseNeverFalls_ClampsAndSaysSo()
    {
        var trace = new double[128];
        for (int k = 1; k < trace.Length; k++)
            trace[k] = 0.0;

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0, firstBin: 1, lastBin: 127);

        Assert.True(result.LowerClamped);
        Assert.True(result.UpperClamped);
        Assert.Equal(1.0, result.LowerHz);
        Assert.Equal(127.0, result.UpperHz);
    }

    [Fact]
    public void Find_RespectsTheSearchRange()
    {
        // Outside the swept band the estimate is meaningless, so a caller restricts the search. The
        // roll-off at bin 10 must be invisible when the search starts at bin 40.
        var trace = new double[256];
        for (int k = 1; k < trace.Length; k++)
            trace[k] = k < 10 ? -40.0 : 0.0;
        for (int k = 150; k < trace.Length; k++)
            trace[k] = -40.0;

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0, firstBin: 40, lastBin: 200);

        Assert.True(result.LowerClamped);
        Assert.Equal(40.0, result.LowerHz);
        Assert.InRange(result.UpperHz, 149.0, 150.0);
    }

    [Fact]
    public void Find_OneDbIsNarrowerThanThreeDb()
    {
        var trace = new double[512];
        for (int k = 1; k < trace.Length; k++)
        {
            double octaves = Math.Log2(k / 256.0);
            // A smooth hump peaking at bin 256, steep enough that both -3 dB corners (about bins
            // 141 and 466) stay inside the array -- a corner that runs off the end clamps, and two
            // clamped corners would compare equal and prove nothing.
            trace[k] = -4.0 * octaves * octaves;
        }

        var oneDb = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 1.0);
        var threeDb = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0);

        Assert.True(oneDb.BandwidthHz < threeDb.BandwidthHz);
        Assert.True(oneDb.LowerHz > threeDb.LowerHz);
        Assert.True(oneDb.UpperHz < threeDb.UpperHz);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    public void Find_RejectsANonPositiveDrop(double dropDb)
    {
        var trace = new double[64];
        Assert.Throws<ArgumentOutOfRangeException>(() => BandwidthAnalyzer.Find(trace, BinHz, dropDb));
    }
}
