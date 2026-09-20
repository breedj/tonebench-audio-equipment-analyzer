namespace Qa403.Dsp.Tests;

public class WeightingCurveTests
{
    [Theory]
    [InlineData(1000.0, 0.0, 0.01)]
    [InlineData(20.0, -50.5, 0.15)]
    [InlineData(100.0, -19.1, 0.1)]
    [InlineData(10000.0, -2.5, 0.1)]
    [InlineData(20000.0, -9.3, 0.1)]
    public void AWeighting_MatchesIec61672ReferencePoints(double freqHz, double expectedDb, double toleranceDb)
    {
        Assert.InRange(AWeighting.Instance.GainDb(freqHz), expectedDb - toleranceDb, expectedDb + toleranceDb);
    }

    [Theory]
    [InlineData(1000.0, 0.0, 0.01)]
    [InlineData(20.0, -6.2, 0.1)]
    [InlineData(100.0, -0.3, 0.1)]
    [InlineData(10000.0, -4.4, 0.1)]
    public void CWeighting_MatchesReferencePoints(double freqHz, double expectedDb, double toleranceDb)
    {
        Assert.InRange(CWeighting.Instance.GainDb(freqHz), expectedDb - toleranceDb, expectedDb + toleranceDb);
    }

    [Theory]
    [InlineData(1000.0, 0.0, 0.01)]
    [InlineData(31.5, -29.9, 0.15)]
    [InlineData(100.0, -19.8, 0.15)]
    [InlineData(2000.0, 5.6, 0.15)]
    [InlineData(6300.0, 12.2, 0.15)]
    [InlineData(10000.0, 8.1, 0.15)]
    [InlineData(20000.0, -22.2, 0.15)]
    public void ItuR468_MatchesTheStandardsTable(double freqHz, double expectedDb, double toleranceDb)
    {
        Assert.InRange(ItuR468Weighting.Instance.GainDb(freqHz), expectedDb - toleranceDb, expectedDb + toleranceDb);
    }

    [Fact]
    public void CcirArm_IsItuR468ReZeroedAt2kHz()
    {
        Assert.Equal(0.0, CcirArmWeighting.Instance.GainDb(2000.0), 9);
        Assert.InRange(CcirArmWeighting.Instance.GainDb(1000.0), -5.7, -5.5);
        Assert.Equal(
            ItuR468Weighting.Instance.GainDb(6300.0) - ItuR468Weighting.Instance.GainDb(2000.0),
            CcirArmWeighting.Instance.GainDb(6300.0), 9);
    }

    [Fact]
    public void RiaaPlayback_Reads19dBAt20HzAndMinus19dBAt20kHz()
    {
        Assert.Equal(0.0, RiaaWeighting.Playback.GainDb(1000.0), 9);
        Assert.InRange(RiaaWeighting.Playback.GainDb(20.0), 19.2, 19.4);
        Assert.InRange(RiaaWeighting.Playback.GainDb(20000.0), -19.7, -19.5);
    }

    [Fact]
    public void RiaaRecord_IsTheNegation()
    {
        foreach (double f in new[] { 20.0, 100.0, 1000.0, 5000.0, 20000.0 })
            Assert.Equal(-RiaaWeighting.Playback.GainDb(f), RiaaWeighting.Record.GainDb(f), 12);
    }

    [Fact]
    public void CurvesThatVanishAtDc_ReportNegativeInfinity_AndAZeroPowerGain()
    {
        Assert.True(double.IsNegativeInfinity(AWeighting.Instance.GainDb(0.0)));
        Assert.True(double.IsNegativeInfinity(CWeighting.Instance.GainDb(0.0)));
        Assert.True(double.IsNegativeInfinity(ItuR468Weighting.Instance.GainDb(0.0)));

        var gains = new double[3];
        WeightingTable.FillPowerGains(AWeighting.Instance, 1000.0, gains);
        Assert.Equal(0.0, gains[0]);
        Assert.Equal(1.0, gains[1], 6);
    }

    [Fact]
    public void StandardWeighting_For_ResolvesEveryMember()
    {
        foreach (WeightingStandard s in Enum.GetValues<WeightingStandard>())
            Assert.NotNull(StandardWeighting.For(s));
        Assert.Same(FlatWeighting.Instance, StandardWeighting.For(WeightingStandard.None));
    }

    [Fact]
    public void Tabulated_InterpolatesLinearlyInDbOverLogFrequency()
    {
        var table = new TabulatedWeighting([new WeightingPoint(100.0, 0.0), new WeightingPoint(1000.0, 10.0)]);

        // Half way in log f is sqrt(100·1000) = 316.2 Hz, and the file's +10 is 10 dB of attenuation.
        Assert.Equal(-5.0, table.GainDb(Math.Sqrt(100.0 * 1000.0)), 9);
        Assert.Equal(-2.5, table.GainDb(Math.Pow(10.0, 2.25)), 9);
        Assert.Equal(0.0, table.GainDb(100.0), 12);
        Assert.Equal(-10.0, table.GainDb(1000.0), 12);
    }

    [Fact]
    public void Tabulated_HoldsTheEndValuesOutsideTheTable()
    {
        var table = new TabulatedWeighting([new WeightingPoint(1000.0, 3.0), new WeightingPoint(100.0, 1.0)]);

        Assert.Equal(-1.0, table.GainDb(10.0), 12);
        Assert.Equal(-1.0, table.GainDb(0.0), 12);
        Assert.Equal(-3.0, table.GainDb(50000.0), 12);
    }

    [Fact]
    public void Tabulated_NegatesTheFilesAttenuationSign()
    {
        // The tutorial's ITU-R 468 file: "6300, -12.2" is the curve's +12.2 dB peak.
        var table = new TabulatedWeighting([new WeightingPoint(1000.0, 0.0), new WeightingPoint(6300.0, -12.2), new WeightingPoint(20000.0, 22.2)]);

        Assert.Equal(12.2, table.GainDb(6300.0), 12);
        Assert.Equal(-22.2, table.GainDb(20000.0), 12);
    }

    [Fact]
    public void Tabulated_RejectsTooFewPointsAndBadFrequencies()
    {
        Assert.Throws<ArgumentException>(() => new TabulatedWeighting([new WeightingPoint(1000.0, 0.0)]));
        Assert.Throws<ArgumentException>(() => new TabulatedWeighting([new WeightingPoint(0.0, 0.0), new WeightingPoint(1000.0, 0.0)]));
        Assert.Throws<ArgumentException>(() => new TabulatedWeighting([new WeightingPoint(1000.0, 0.0), new WeightingPoint(1000.0, 1.0)]));
    }

    [Fact]
    public void Composite_SumsInDb()
    {
        var user = new TabulatedWeighting([new WeightingPoint(10.0, 2.0), new WeightingPoint(100000.0, 2.0)]);
        var composite = new CompositeWeighting(AWeighting.Instance, user);

        Assert.Equal(AWeighting.Instance.GainDb(1000.0) - 2.0, composite.GainDb(1000.0), 12);
        Assert.Equal(AWeighting.Instance.GainDb(50.0) - 2.0, composite.GainDb(50.0), 12);
    }
}

public class WeightingTableParserTests
{
    [Fact]
    public void Parses_TheQa40xFormat()
    {
        var points = WeightingTableParser.Parse("31.5, 29.9\r\n1000, 0\r\n6300, -12.2\r\n20000, 22.2\r\n");

        Assert.Equal(4, points.Count);
        Assert.Equal(new WeightingPoint(6300.0, -12.2), points[2]);
        Assert.Equal(new WeightingPoint(31.5, 29.9), points[0]);
    }

    [Fact]
    public void Skips_CommentsBlankLinesAndAHeader()
    {
        var points = WeightingTableParser.Parse("Frequency, Attenuation\n# a comment\n\n; another\n// and another\n100, 1\n\n1000, 2\n");

        Assert.Equal(2, points.Count);
        Assert.Equal(100.0, points[0].FreqHz);
        Assert.Equal(2.0, points[1].AttenuationDb);
    }

    [Fact]
    public void Accepts_WhitespaceAndTabSeparators()
    {
        var points = WeightingTableParser.Parse("100 1\n1000\t2\n");

        Assert.Equal(2, points.Count);
        Assert.Equal(1000.0, points[1].FreqHz);
    }

    [Fact]
    public void RejectsAMalformedLineNamingIt()
    {
        var ex = Assert.Throws<FormatException>(() => WeightingTableParser.Parse("100, 1\n1000, 2\noops, here\n"));

        Assert.Contains("Line 3", ex.Message);
        Assert.Contains("oops", ex.Message);
    }

    [Fact]
    public void RejectsFewerThanTwoPoints()
    {
        Assert.Throws<FormatException>(() => WeightingTableParser.Parse("1000, 0\n"));
        Assert.Throws<FormatException>(() => WeightingTableParser.Parse(""));
    }
}
