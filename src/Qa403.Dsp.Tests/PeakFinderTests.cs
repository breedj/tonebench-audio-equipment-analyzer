namespace Qa403.Dsp.Tests;

public class PeakFinderTests
{
    private static PsdSpectrum MakePsd(double[] values, double sampleRateHz = 48000.0)
    {
        double binSizeHz = sampleRateHz / (2 * (values.Length - 1));
        return PsdSpectrum.FromValues(values, sampleRateHz, binSizeHz, new WindowSums(S1: values.Length, S2: values.Length, Length: values.Length));
    }

    [Fact]
    public void FindPeaks_ReturnsStrongestFirst()
    {
        var values = new double[1000];
        Array.Fill(values, 1e-12); // quiet floor
        values[100] = 1e-6; // -60ish dBV
        values[500] = 1e-4; // strongest
        values[900] = 1e-8; // weakest of the three

        var psd = MakePsd(values);
        var destination = new SpectralPeak[3];
        int written = PeakFinder.FindPeaks(psd, [], maxCount: 3, destination);

        Assert.Equal(3, written);
        Assert.Equal(500, destination[0].Bin);
        Assert.Equal(100, destination[1].Bin);
        Assert.Equal(900, destination[2].Bin);
        Assert.True(destination[0].AmplitudeDbv > destination[1].AmplitudeDbv);
        Assert.True(destination[1].AmplitudeDbv > destination[2].AmplitudeDbv);
    }

    [Fact]
    public void FindPeaks_ExcludesGivenBinRanges()
    {
        var values = new double[1000];
        Array.Fill(values, 1e-12);
        values[500] = 1e-3; // would be the strongest peak, but excluded (e.g. the fundamental)
        values[200] = 1e-6; // the real target once the fundamental is excluded

        var psd = MakePsd(values);
        var destination = new SpectralPeak[1];
        int written = PeakFinder.FindPeaks(psd, [new BinRange(495, 505)], maxCount: 1, destination);

        Assert.Equal(1, written);
        Assert.Equal(200, destination[0].Bin);
    }

    [Fact]
    public void FindPeaks_DoesNotReportTheSameSpurTwice()
    {
        // A single physical spur's window mainlobe spreads across a few adjacent bins — without
        // suppression this would be reported as multiple separate "peaks" at the same spur.
        var values = new double[1000];
        Array.Fill(values, 1e-12);
        values[300] = 1e-4;
        values[301] = 9e-5; // mainlobe neighbor, must not count as a second peak
        values[299] = 8e-5;
        values[700] = 1e-6; // a genuinely separate, weaker spur

        var psd = MakePsd(values);
        var destination = new SpectralPeak[2];
        int written = PeakFinder.FindPeaks(psd, [], maxCount: 2, destination, skirtHalfWidthBins: 3);

        Assert.Equal(2, written);
        Assert.Equal(300, destination[0].Bin);
        Assert.Equal(700, destination[1].Bin);
    }

    [Fact]
    public void FindPeaks_RespectsMinBin()
    {
        var values = new double[1000];
        Array.Fill(values, 1e-12);
        values[5] = 1e-3; // below minBin, must be ignored (e.g. DC/near-DC leakage)
        values[300] = 1e-6;

        var psd = MakePsd(values);
        var destination = new SpectralPeak[1];
        int written = PeakFinder.FindPeaks(psd, [], maxCount: 1, destination, minBin: 10);

        Assert.Equal(1, written);
        Assert.Equal(300, destination[0].Bin);
    }

    [Fact]
    public void FindPeaks_ReturnsFewerThanRequestedWhenBinsRunOut()
    {
        // Every bin excluded except a 3-bin notch (49..51) — exactly the suppression window
        // around the one real peak at bin 50, so after that single pick nothing remains.
        var values = new double[100];
        Array.Fill(values, 1e-12);
        values[50] = 1e-3;

        var psd = MakePsd(values);
        var destination = new SpectralPeak[5];
        int written = PeakFinder.FindPeaks(psd, [new BinRange(1, 48), new BinRange(52, 99)], maxCount: 5, destination, skirtHalfWidthBins: 1);

        Assert.Equal(1, written);
        Assert.Equal(50, destination[0].Bin);
    }

    [Fact]
    public void FindPeaks_ZeroMaxCountWritesNothing()
    {
        var values = new double[100];
        Array.Fill(values, 1e-12);
        var psd = MakePsd(values);

        int written = PeakFinder.FindPeaks(psd, [], maxCount: 0, []);

        Assert.Equal(0, written);
    }
}
