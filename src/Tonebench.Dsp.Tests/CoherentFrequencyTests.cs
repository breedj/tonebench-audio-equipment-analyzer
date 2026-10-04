namespace Tonebench.Dsp.Tests;

public class CoherentFrequencyTests
{
    [Fact]
    public void Nearest_ExactPowerOfTwoBinRequest_SnapsToNearestOddBin()
    {
        // N=1024 is a power of two, so every even bin shares a factor with N — only odd
        // bins are coprime to it. 1000 Hz at fs=40960/N=1024 wants bin 25, which is already odd.
        var result = CoherentFrequency.Nearest(1000.0, 40960.0, 1024);

        Assert.Equal(25, result.Bin);
        Assert.Equal(1000.0, result.FrequencyHz, 9);
        Assert.Equal(1, Gcd(result.Bin, 1024));
    }

    [Fact]
    public void Nearest_RequestLandingOnEvenBin_MovesToNearestOddBin()
    {
        // 1040 Hz at the same fs/N wants bin 26 (even, not coprime to 1024) -> nearest odd is 25 or 27.
        var result = CoherentFrequency.Nearest(1040.0, 40960.0, 1024);

        Assert.True(result.Bin == 25 || result.Bin == 27);
        Assert.Equal(1, Gcd(result.Bin, 1024));
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(1000.0)]
    [InlineData(9999.0)]
    [InlineData(19500.0)]
    public void Nearest_AlwaysReturnsBinCoprimeToN(double desiredHz)
    {
        const int n = 8192;
        var result = CoherentFrequency.Nearest(desiredHz, 192000.0, n);

        Assert.Equal(1, Gcd(result.Bin, n));
        Assert.InRange(result.Bin, 1, n - 1);
    }

    [Fact]
    public void Nearest_FrequencyMatchesBinTimesSampleRateOverN()
    {
        const double fs = 48000.0;
        const int n = 2048;
        var result = CoherentFrequency.Nearest(1234.5, fs, n);

        Assert.Equal(result.Bin * fs / n, result.FrequencyHz, 9);
    }

    [Theory]
    // 192 kHz / 32768 = 5.859 Hz bins. 10 Hz is bin 1.71, rounds to 2, and 2 is not coprime to a
    // power of two -- so the answer is bin 1 (5.86 Hz, 4.1 Hz away), not bin 3 (17.58 Hz, 7.6 Hz
    // away). Searching up-then-down returned bin 3 and started a 10 Hz THD sweep at 17.6 Hz.
    [InlineData(10.0, 192000.0, 32768, 1)]
    // Same request at the resolution the sweep actually uses now: bin 7, 10.25 Hz.
    [InlineData(10.0, 192000.0, 131072, 7)]
    // 20 Hz is bin 13.65 -> rounds to 14 (even); 13 is 0.65 away, 15 is 1.35 away.
    [InlineData(20.0, 192000.0, 131072, 13)]
    public void Nearest_EvenRoundedBin_TakesTheCoprimeNeighbourClosestInFrequency(
        double desiredHz, double sampleRateHz, int n, int expectedBin)
    {
        var result = CoherentFrequency.Nearest(desiredHz, sampleRateHz, n);

        Assert.Equal(expectedBin, result.Bin);
        Assert.Equal(1, Gcd(result.Bin, n));
    }

    [Fact]
    public void Nearest_NonPowerOfTwoN_StillFindsCoprimeBin()
    {
        // N need not be a power of two for this helper in general, even though the FFT
        // backends only accept power-of-two lengths.
        const int n = 999; // 3^3 * 37
        var result = CoherentFrequency.Nearest(500.0, 1000.0, n);

        Assert.Equal(1, Gcd(result.Bin, n));
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);

        return a;
    }
}
