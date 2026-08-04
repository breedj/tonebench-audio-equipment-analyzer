namespace Qa403.Dsp.Tests;

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
