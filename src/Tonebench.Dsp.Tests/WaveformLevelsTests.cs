namespace Tonebench.Dsp.Tests;

/// <summary>
/// The vertical readings a scope takes. The two that did not exist in this codebase before are the
/// ones worth pinning: a reported <b>DC</b> level, and a <b>crest factor</b>.
/// </summary>
public class WaveformLevelsTests
{
    private static double[] Sine(int n, double cyclesInRecord, double ampPk, double offset = 0.0)
    {
        var x = new double[n];
        for (int i = 0; i < n; i++)
            x[i] = ampPk * Math.Sin(2.0 * Math.PI * cyclesInRecord * i / n) + offset;

        return x;
    }

    [Fact]
    public void ASineOnAnOffset_ReportsTheOffsetAsTheMean_AndKeepsItOutOfTheAcRms()
    {
        // 16 whole cycles, so the mean of the sine part is exactly zero and the offset is the whole
        // of the DC. This is the reading SpectrumRecordAnalyzer.ComputeTimeDomainLevels cannot give
        // at all: it subtracts the mean before it measures anything, which is correct for the
        // meters it feeds and is exactly what a scope must not do.
        var x = Sine(4096, cyclesInRecord: 16, ampPk: 2.0, offset: 0.75);

        var levels = WaveformMeasure.Measure(x);

        Assert.Equal(0.75, levels.MeanV, 9);
        Assert.Equal(2.75, levels.MaxV, 9);
        Assert.Equal(-1.25, levels.MinV, 9);
        Assert.Equal(4.0, levels.PeakToPeakV, 9);

        // AC RMS is the sine's own: 2 / sqrt(2). True RMS carries the offset as well.
        Assert.Equal(2.0 / Math.Sqrt(2.0), levels.AcRmsV, 9);
        Assert.Equal(Math.Sqrt(2.0 + 0.75 * 0.75), levels.RmsV, 9);

        // The two readings must differ by the DC, which is the whole point of reporting both.
        Assert.True(levels.RmsV > levels.AcRmsV);
    }

    [Fact]
    public void ASmallSignalOnAHugeOffset_StillMeasuresItsAcRms()
    {
        // The cancellation case, and the reason Measure takes two passes. The textbook one-pass
        // variance is sumOfSquares/n - mean^2; here both terms are about 1e16, where the spacing of
        // doubles is ~2, and the 0.5 that is the answer disappears entirely into the rounding. The
        // same class of fault as FractionalOctaveSmoother's stopband reading -600 dB.
        var x = Sine(8192, cyclesInRecord: 32, ampPk: 1.0, offset: 1e8);

        var levels = WaveformMeasure.Measure(x);

        Assert.Equal(1e8, levels.MeanV, 6);
        Assert.Equal(1.0 / Math.Sqrt(2.0), levels.AcRmsV, 8);

        // And the naive form really would have failed here -- asserted rather than asserted about,
        // so this test says why it exists rather than merely passing.
        double sumSquares = 0.0, sum = 0.0;
        foreach (double v in x)
        {
            sum += v;
            sumSquares += v * v;
        }

        double mean = sum / x.Length;
        double naive = Math.Sqrt(Math.Max(0.0, sumSquares / x.Length - mean * mean));
        Assert.True(
            Math.Abs(naive - levels.AcRmsV) > 0.01,
            $"the one-pass form gave {naive:0.######}, which is close enough to {levels.AcRmsV:0.######} that this test proves nothing");
    }

    [Fact]
    public void ASine_HasTheCrestFactorOfASine()
    {
        var x = Sine(4096, cyclesInRecord: 16, ampPk: 1.5);

        var levels = WaveformMeasure.Measure(x);

        Assert.NotNull(levels.CrestFactor);
        Assert.Equal(Math.Sqrt(2.0), levels.CrestFactor!.Value, 3);
    }

    [Fact]
    public void ASquare_HasACrestFactorOfOne()
    {
        var x = new double[4096];
        for (int i = 0; i < x.Length; i++)
            x[i] = (i / 64) % 2 == 0 ? 1.0 : -1.0;

        var levels = WaveformMeasure.Measure(x);

        Assert.NotNull(levels.CrestFactor);
        Assert.Equal(1.0, levels.CrestFactor!.Value, 9);
    }

    [Fact]
    public void ASineOnAnOffset_HasAnAcCrestFactorOfASine_WhereItsTrueCrestFactorIsMostlyTheOffset()
    {
        var x = Sine(4096, cyclesInRecord: 16, ampPk: 1.0, offset: 10.0);

        var levels = WaveformMeasure.Measure(x);

        Assert.Equal(Math.Sqrt(2.0), levels.AcCrestFactor!.Value, 3);
        Assert.True(levels.CrestFactor!.Value < 1.2, "the true crest factor of a small signal on a big offset is near 1");
    }

    [Fact]
    public void DigitalSilence_ReportsNoCrestFactorRatherThanInfinity()
    {
        // Not a nicety: a non-finite double on a status record truncates the whole JSON response,
        // 200 and all, which the client reads as the measurement having silently done nothing.
        var levels = WaveformMeasure.Measure(new double[256]);

        Assert.Equal(0.0, levels.RmsV, 12);
        Assert.Null(levels.CrestFactor);
        Assert.Null(levels.AcCrestFactor);
    }

    [Fact]
    public void AnEmptyRecord_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => WaveformMeasure.Measure(ReadOnlySpan<double>.Empty));
    }
}
