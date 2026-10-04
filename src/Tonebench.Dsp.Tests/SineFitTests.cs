namespace Tonebench.Dsp.Tests;

/// <summary>
/// Golden tests for the four-parameter sine fit behind the THD residual view. What the residual
/// shows is only as good as what the fit removes: a fundamental that is not cancelled to well
/// below the DUT's distortion draws the fit's own error as a THD residual.
/// </summary>
public class SineFitTests
{
    private const double SampleRateHz = 48_000.0;

    private static double[] Tone(int n, double freqHz, double ampPk, double phaseRad = 0.3, double offset = 0.0, double h2Fraction = 0.0)
    {
        var x = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / SampleRateHz;
            x[i] = ampPk * Math.Cos(2.0 * Math.PI * freqHz * t + phaseRad)
                + h2Fraction * ampPk * Math.Cos(2.0 * Math.PI * 2.0 * freqHz * t + 1.1)
                + offset;
        }

        return x;
    }

    private static double Db(double ratio) => 20.0 * Math.Log10(ratio);

    [Fact]
    public void ACoherentTone_IsCancelledToTheArithmeticFloor()
    {
        const int n = 4096;
        double freqHz = 85 * SampleRateHz / n; // bin 85
        var x = Tone(n, freqHz, 1.0, phaseRad: 0.7, offset: 0.05);

        var fit = SineFit.Fit(x, SampleRateHz, freqHz);

        Assert.Equal(freqHz, fit.FreqHz, 6);
        Assert.Equal(1.0, fit.AmplitudeVpk, 9);
        Assert.Equal(0.7, fit.PhaseRad, 9);
        Assert.Equal(0.05, fit.OffsetV, 9);
        double residualDb = Db(SineFit.ResidualRms(x, fit, SampleRateHz) / fit.Vrms);
        Assert.True(residualDb < -120.0, $"residual {residualDb:0.#} dB re the fundamental");
    }

    [Fact]
    public void OnePercentSecondHarmonic_LeavesExactlyOnePercentResidual()
    {
        const int n = 4096;
        double freqHz = 85 * SampleRateHz / n;
        var x = Tone(n, freqHz, 1.0, h2Fraction: 0.01);

        var fit = SineFit.Fit(x, SampleRateHz, freqHz);

        // Residual RMS over fundamental RMS is the H2 fraction, i.e. 1.000 % THD.
        double ratio = SineFit.ResidualRms(x, fit, SampleRateHz) / fit.Vrms;
        Assert.InRange(ratio, 0.00995, 0.01005);
        Assert.Equal(1.0, fit.AmplitudeVpk, 4);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(-0.5)]
    [InlineData(0.9)]
    public void AnOffGridTone_IsRefinedToItsTrueFrequency_AndCancelled(double binsOff)
    {
        const int n = 4096;
        double binHz = SampleRateHz / n;
        double freqHz = (85 + binsOff) * binHz;
        var x = Tone(n, freqHz, 0.5, phaseRad: -1.2);

        // Started from the bin an FFT peak search would report.
        var fit = SineFit.Fit(x, SampleRateHz, 85 * binHz);

        Assert.InRange(Math.Abs(fit.FreqHz - freqHz) / freqHz, 0.0, 1e-7);
        double residualDb = Db(SineFit.ResidualRms(x, fit, SampleRateHz) / fit.Vrms);
        Assert.True(residualDb < -100.0, $"residual {residualDb:0.#} dB re the fundamental, {fit.Iterations} iterations");
    }

    [Fact]
    public void AnUnrefinedSubtraction_IsWhatTheRefinementExistsToAvoid()
    {
        // Half a bin off, taken at the bin: π radians of drift across the record, and a "residual"
        // the size of the fundamental. This is the failure the four-parameter fit is for.
        const int n = 4096;
        double binHz = SampleRateHz / n;
        var x = Tone(n, 85.5 * binHz, 0.5);

        var unrefined = SineFit.Fit(x, SampleRateHz, 85 * binHz, maxIterations: 0);
        var refined = SineFit.Fit(x, SampleRateHz, 85 * binHz);

        double unrefinedDb = Db(SineFit.ResidualRms(x, unrefined, SampleRateHz) / (0.5 / Math.Sqrt(2.0)));
        double refinedDb = Db(SineFit.ResidualRms(x, refined, SampleRateHz) / refined.Vrms);
        Assert.True(unrefinedDb > -10.0, $"unrefined residual {unrefinedDb:0.#} dB -- expected the fit to fail without refinement");
        Assert.True(refinedDb < -100.0, $"refined residual {refinedDb:0.#} dB");
    }

    [Fact]
    public void ALowTone_OverFewPeriods_StillConverges()
    {
        // 20 Hz at 48 kHz over 4096 samples is 1.7 periods -- the shortest record the scope path
        // would hand the fit. The offset and the cosine are far from orthogonal here.
        const int n = 4096;
        var x = Tone(n, 20.0, 1.0, phaseRad: 0.4, offset: 0.1);

        var fit = SineFit.Fit(x, SampleRateHz, 20.5);

        Assert.Equal(20.0, fit.FreqHz, 5);
        Assert.Equal(1.0, fit.AmplitudeVpk, 6);
        Assert.Equal(0.1, fit.OffsetV, 6);
    }

    [Fact]
    public void Subtract_TakesATailOfTheRecordWithoutRefitting()
    {
        const int n = 8192;
        double freqHz = 170.3 * SampleRateHz / n;
        var x = Tone(n, freqHz, 1.0, h2Fraction: 0.02);
        var fit = SineFit.Fit(x, SampleRateHz, 170 * SampleRateHz / n);

        var tail = new float[2048];
        SineFit.Subtract(x, fit, SampleRateHz, n - tail.Length, tail);

        // What is left is the 2 % second harmonic, 0.02 V peak.
        double peak = tail.Max(v => Math.Abs(v));
        Assert.InRange(peak, 0.0199, 0.0201);
        Assert.Throws<ArgumentOutOfRangeException>(() => SineFit.Subtract(x, fit, SampleRateHz, n - 100, new float[2048]));
    }

    [Fact]
    public void RejectsWhatItCannotFit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SineFit.Fit(new double[3], SampleRateHz, 1000.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SineFit.Fit(new double[64], SampleRateHz, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SineFit.Fit(new double[64], SampleRateHz, SampleRateHz / 2.0));
    }
}
