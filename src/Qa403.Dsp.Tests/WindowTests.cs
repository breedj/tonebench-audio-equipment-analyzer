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
    [InlineData(WindowType.Kaiser5)]
    [InlineData(WindowType.Kaiser7)]
    public void OtherWindows_ProduceFiniteNonDegenerateSums(WindowType type)
    {
        var w = Window.Get(type, Length);

        Assert.True(w.Sums.S1 > 0);
        Assert.True(w.Sums.S2 > 0);
        Assert.True(w.Sums.EnbwBins >= 1.0);
    }

    /// <summary>
    /// The Kaiser parameter convention, pinned by the one number that distinguishes the two
    /// readings of "Kaiser 5" / "Kaiser 7" — see <see cref="Window"/>. ARTA names α and uses
    /// β = πα; numpy, scipy and MATLAB take β directly, and the bare-β reading would give
    /// 1.359 and 1.569 bins here. A change that swapped the convention would otherwise pass every
    /// other test in this file and silently rewrite the ENBW stored with every trace.
    /// </summary>
    [Theory]
    [InlineData(WindowType.Kaiser5, 2.283048)]
    [InlineData(WindowType.Kaiser7, 2.684814)]
    public void Kaiser_HasTheEnbwOfTheAlphaTimesPiConvention(WindowType type, double expectedEnbwBins)
    {
        // ENBW is a shape property: independent of length, so a mismatch is the parameter and not
        // the record size. Checked at two lengths for exactly that reason.
        foreach (int length in new[] { 1024, 8192 })
            Assert.Equal(expectedEnbwBins, Window.Get(type, length).Sums.EnbwBins, 5);
    }

    /// <summary>
    /// A Kaiser is built from a Bessel series rather than a cosine sum, and
    /// <see cref="Window"/> evaluates only half of it and mirrors the rest. Three properties make
    /// that legitimate, and all three are checked here: the window peaks at 1 in the middle, it is
    /// symmetric about N/2, and sample 0 carries the 1/I₀(β) floor of the DFT-even form.
    ///
    /// <para>The symmetry loop is also what pins <i>periodic</i> rather than symmetric: it asserts
    /// w[N−1] == w[1], where the symmetric (N−1 denominator) form would instead put w[N−1] back at
    /// w[0] — three orders of magnitude lower.</para>
    /// </summary>
    [Theory]
    [InlineData(WindowType.Kaiser5, 1.48489e-06)]
    [InlineData(WindowType.Kaiser7, 3.28891e-09)]
    public void Kaiser_IsSymmetricAboutTheMidpoint_AndStartsAtTheReciprocalOfI0(WindowType type, double expectedFirst)
    {
        var w = Window.Get(type, Length);

        // As a ratio: the absolute values are ~1e-6 and ~1e-9, where a decimal-places assertion
        // would pass against almost anything.
        Assert.Equal(1.0, w.Values[0] / expectedFirst, 4);
        Assert.Equal(1.0, w.Values[Length / 2], 12);
        Assert.True(w.Values[0] < w.Values[1]);

        for (int n = 1; n < Length / 2; n++)
            Assert.Equal(w.Values[n], w.Values[Length - n], 15);
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
