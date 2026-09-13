namespace Qa403.Dsp.Tests;

public class FrequencyNormalizedDistortionTests
{
    /// <summary>A response drawn from a function of frequency over a log grid.</summary>
    private static FundamentalResponse ResponseOf(Func<double, double> vrmsAt, double fromHz = 20.0, double toHz = 96000.0, int stepsPerOctave = 3)
    {
        var response = new FundamentalResponse();
        foreach (double hz in SweepPoints.LogPerOctave(fromHz, toHz, stepsPerOctave))
            response.Add(hz, vrmsAt(hz));
        return response;
    }

    private static Harmonic[] HarmonicsOf(double fundamentalVrms, params (int Order, double Vrms)[] upper)
    {
        var list = new List<Harmonic> { new(1, 100, fundamentalVrms, 20.0 * Math.Log10(fundamentalVrms), 0.0) };
        foreach (var (order, vrms) in upper)
            list.Add(new Harmonic(order, 100 * order, vrms, 20.0 * Math.Log10(vrms), 20.0 * Math.Log10(vrms / fundamentalVrms)));
        return [.. list];
    }

    [Fact]
    public void Compute_AgainstAFlatResponse_EqualsTheConventionalFigures()
    {
        // With the fundamental the same everywhere, dividing by A1(n·f) is dividing by A1(f):
        // the normalized figures must reproduce SpectrumMetrics exactly.
        var response = ResponseOf(_ => 1.0);
        var harmonics = HarmonicsOf(1.0, (2, 0.01), (3, 0.005), (4, 0.002), (5, 0.001), (6, 0.0005), (7, 0.0003));

        var n = FrequencyNormalizedDistortion.Compute(1000.0, harmonics, response);

        Assert.Equal(SpectrumMetrics.Thd(harmonics), n.Thd!.Value, 12);
        Assert.Equal(0.01, n.HarmonicRatio[0]!.Value, 12);
        Assert.Equal(0.005, n.HarmonicRatio[1]!.Value, 12);
        Assert.Equal(0.002, n.HarmonicRatio[2]!.Value, 12);
        Assert.Equal(0.001, n.HarmonicRatio[3]!.Value, 12);
        Assert.Equal(SpectrumMetrics.PartialThd(harmonics, 6), n.D6Plus!.Value, 12);
    }

    [Theory]
    [InlineData(1000.0)]
    [InlineData(1234.5)]   // off the response grid: pins the interpolation law, not just the lookup
    public void Compute_AgainstAOneOverFResponse_ScalesEachHarmonicByItsOrder(double freqHz)
    {
        // A 1/f fundamental is a straight line in dB against log f, so an interpolation that is
        // linear in exactly those coordinates reproduces it between grid points with no error at
        // all. Each harmonic is placed at r_n · A1(f), i.e. a conventional D_n of r_n; normalized
        // against A1(n·f) = A1(f)/n that reads n · r_n, so every order is scaled by its own order.
        var response = ResponseOf(hz => 1000.0 / hz);
        double a1 = 1000.0 / freqHz;
        var harmonics = HarmonicsOf(a1, (2, 0.01 * a1), (3, 0.01 * a1), (5, 0.01 * a1), (6, 0.01 * a1));

        var n = FrequencyNormalizedDistortion.Compute(freqHz, harmonics, response);

        Assert.Equal(0.02, n.HarmonicRatio[0]!.Value, 9);
        Assert.Equal(0.03, n.HarmonicRatio[1]!.Value, 9);
        Assert.Null(n.HarmonicRatio[2]);
        Assert.Equal(0.05, n.HarmonicRatio[3]!.Value, 9);
        Assert.Equal(0.06, n.D6Plus!.Value, 9);
        Assert.Equal(Math.Sqrt((0.02 * 0.02) + (0.03 * 0.03) + (0.05 * 0.05) + (0.06 * 0.06)), n.Thd!.Value, 9);
    }

    [Fact]
    public void Compute_HarmonicAboveTheMeasuredResponse_IsNullAndLeftOutOfTheSums()
    {
        // Response measured to 5 kHz only: H2 of 2 kHz (4 kHz) has a reference, H3 (6 kHz) does not.
        var response = ResponseOf(_ => 1.0, 20.0, 5000.0);
        var harmonics = HarmonicsOf(1.0, (2, 0.01), (3, 0.01), (6, 0.01));

        var n = FrequencyNormalizedDistortion.Compute(2000.0, harmonics, response);

        Assert.Equal(0.01, n.HarmonicRatio[0]!.Value, 12);
        Assert.Null(n.HarmonicRatio[1]);
        Assert.Null(n.D6Plus);
        Assert.Equal(0.01, n.Thd!.Value, 12);
    }

    [Fact]
    public void Compute_SecondHarmonicWithoutAReference_ReportsNoTotal()
    {
        var response = ResponseOf(_ => 1.0, 20.0, 3000.0);
        var harmonics = HarmonicsOf(1.0, (2, 0.01), (3, 0.01));

        var n = FrequencyNormalizedDistortion.Compute(2000.0, harmonics, response);

        Assert.Null(n.Thd);
        Assert.All(n.HarmonicRatio, r => Assert.Null(r));
        Assert.Null(n.D6Plus);
    }

    [Fact]
    public void Compute_AgainstAnEmptyResponse_IsAllNull()
    {
        var n = FrequencyNormalizedDistortion.Compute(1000.0, HarmonicsOf(1.0, (2, 0.01)), new FundamentalResponse());

        Assert.Null(n.Thd);
        Assert.Equal(NormalizedDistortion.IndividualOrders, n.HarmonicRatio.Count);
        Assert.All(n.HarmonicRatio, r => Assert.Null(r));
    }

    [Fact]
    public void FundamentalResponse_AFundamentalThatWasNeverLocated_IsNotStoredAndCannotProduceInfinity()
    {
        // A point whose fundamental was never found reports 0 V (or -inf dBV). Stored, it would
        // make every ratio taken against it infinite -- which System.Text.Json refuses exactly as
        // it refuses NaN, truncating the status response mid-array.
        var response = new FundamentalResponse();
        response.Add(1000.0, 1.0);
        response.Add(2000.0, 0.0);
        response.Add(3000.0, double.NaN);
        response.Add(4000.0, double.NegativeInfinity);

        Assert.Equal(1, response.Count);
        Assert.Null(response.VrmsAt(2000.0));

        var n = FrequencyNormalizedDistortion.Compute(1000.0, HarmonicsOf(1.0, (2, 0.01)), response);
        Assert.Null(n.HarmonicRatio[0]);
        Assert.Null(n.Thd);
    }

    [Fact]
    public void FundamentalResponse_ReadsExactlyAtGridPointsAndNullOutsideThem()
    {
        var response = new FundamentalResponse();
        response.Add(100.0, 2.0);
        response.Add(1000.0, 1.0);
        response.Add(10000.0, 0.5);

        Assert.Equal(2.0, response.VrmsAt(100.0)!.Value, 12);
        Assert.Equal(1.0, response.VrmsAt(1000.0)!.Value, 12);
        Assert.Equal(0.5, response.VrmsAt(10000.0)!.Value, 12);
        Assert.Null(response.VrmsAt(99.9));
        Assert.Null(response.VrmsAt(10000.1));

        // Halfway in log f between 100 and 1000 (316.2 Hz) is halfway in dB between 6.02 and 0 dB.
        Assert.Equal(Math.Sqrt(2.0), response.VrmsAt(Math.Sqrt(100.0 * 1000.0))!.Value, 9);
    }

    [Fact]
    public void FundamentalResponse_AddOutOfOrder_KeepsTheGridSorted()
    {
        var response = new FundamentalResponse();
        response.Add(1000.0, 1.0);
        response.Add(100.0, 1.0);
        response.Add(500.0, 1.0);

        Assert.Equal(3, response.Count);
        Assert.Equal(1.0, response.VrmsAt(250.0)!.Value, 12);
        Assert.Equal(1.0, response.VrmsAt(750.0)!.Value, 12);
    }
}
