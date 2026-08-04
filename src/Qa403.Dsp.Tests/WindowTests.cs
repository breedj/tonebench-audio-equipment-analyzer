namespace Qa403.Dsp.Tests;

public class WindowTests
{
    private const int Length = 1024;

    [Fact]
    public void Rectangular_SumsMatchAnalyticFormula()
    {
        var w = Window.Get(WindowType.Rectangular, Length);

        Assert.Equal(Length, w.Sums.S1, 9);
        Assert.Equal(Length, w.Sums.S2, 9);
    }

    [Fact]
    public void Hann_SumsMatchAnalyticFormula()
    {
        var w = Window.Get(WindowType.Hann, Length);

        Assert.Equal(Length / 2.0, w.Sums.S1, 6);
        Assert.Equal(Length * 3.0 / 8.0, w.Sums.S2, 6);
    }

    [Theory]
    [InlineData(WindowType.Hamming)]
    [InlineData(WindowType.BlackmanHarris)]
    [InlineData(WindowType.FlatTop)]
    public void OtherWindows_ProduceFiniteNonDegenerateSums(WindowType type)
    {
        var w = Window.Get(type, Length);

        Assert.True(w.Sums.S1 > 0);
        Assert.True(w.Sums.S2 > 0);
        Assert.True(w.Sums.EnbwBins >= 1.0);
    }

    [Fact]
    public void Get_RejectsNonPositiveLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Window.Get(WindowType.Hann, 0));
    }

    [Fact]
    public void Get_ReturnsSameCachedInstanceForSameTypeAndLength()
    {
        var first = Window.Get(WindowType.Hann, Length);
        var second = Window.Get(WindowType.Hann, Length);

        Assert.Same(first, second);
    }

    [Fact]
    public void Get_ReturnsDistinctInstancesForDifferentLengths()
    {
        var a = Window.Get(WindowType.Hann, 256);
        var b = Window.Get(WindowType.Hann, 512);

        Assert.NotSame(a, b);
        Assert.NotEqual(a.Sums.S1, b.Sums.S1);
    }

    [Fact]
    public void Apply_MultipliesSamplesByCoefficients()
    {
        var w = Window.Get(WindowType.Hann, 8);
        var samples = new double[8];
        Array.Fill(samples, 1.0);
        var destination = new double[8];

        Window.Apply(samples, w, destination);

        for (int n = 0; n < 8; n++)
            Assert.Equal(w.Values[n], destination[n], 12);
    }

    [Fact]
    public void Apply_FloatOverload_MatchesDoubleOverload()
    {
        var w = Window.Get(WindowType.BlackmanHarris, 16);
        var doubleSamples = new double[16];
        var floatSamples = new float[16];
        for (int n = 0; n < 16; n++)
        {
            doubleSamples[n] = Math.Sin(n);
            floatSamples[n] = (float)doubleSamples[n];
        }

        var expected = new double[16];
        var actual = new double[16];
        Window.Apply(doubleSamples, w, expected);
        Window.Apply(floatSamples, w, actual);

        for (int n = 0; n < 16; n++)
            Assert.Equal(expected[n], actual[n], 5);
    }
}
