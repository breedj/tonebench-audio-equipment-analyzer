using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class MultitoneAnalyzerTests
{
    // 8192 points at 81 920 Hz: 10 Hz bins, so every tone and line below sits on an integer bin.
    private const int FftSize = 8192;
    private const double SampleRateHz = 81_920.0;
    private const int Nyquist = FftSize / 2;

    private static readonly int[] CombBins = [100, 200, 400, 800];

    private static PsdSpectrum Psd(WindowType windowType, double[] samples, double[] buffer)
    {
        var w = Window.Get(windowType, FftSize);
        var windowed = new double[FftSize];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(windowed, spectrum);
        return PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, buffer);
    }

    private static PsdSpectrum Psd(WindowType windowType, double[] buffer, params (double AmplitudeV, int Bin)[] components) =>
        Psd(windowType, TestSignals.CoherentTone(FftSize, components), buffer);

    private static double Dbc(double dbc) => Math.Pow(10.0, dbc / 20.0);

    private static (double AmplitudeV, int Bin)[] Comb(double amplitudeV) => [.. CombBins.Select(b => (amplitudeV, b))];

    [Theory]
    [InlineData(WindowType.Hann)]
    [InlineData(WindowType.BlackmanHarris)]
    public void ALineInOneGap_IsThatBandsHighestAtItsLevelReTheComb(WindowType windowType)
    {
        // Four equal 0.5 V tones at 1/2/4/8 kHz: comb RMS = 0.5/√2 · √4 = 0.707 V. A −60 dBr line at
        // 3 kHz (bin 300), in gap 1.
        const double tone = 0.5;
        double combVrms = tone / Math.Sqrt(2.0) * 2.0;
        double lineVpk = combVrms * Math.Sqrt(2.0) * Dbc(-60.0);
        var buffer = new double[Nyquist + 1];
        var psd = Psd(windowType, buffer, [.. Comb(tone), (lineVpk, 300)]);

        var result = MultitoneAnalyzer.Analyze(psd, CombBins, Nyquist);

        Assert.NotNull(result);
        Assert.Equal(4, result.Tones.Count);
        Assert.Equal(3, result.Bands.Count);
        Assert.Equal(combVrms, result.CombVrms, 3);
        Assert.All(result.Tones, t => Assert.Equal(20.0 * Math.Log10(tone / Math.Sqrt(2.0)), t.AmplitudeDbv, 1));

        var band = result.Bands[1];
        Assert.True(band.Resolved);
        Assert.Equal(300, band.HighestBin);
        Assert.Equal(3000.0, band.HighestFreqHz, 6);
        Assert.Equal(-60.0, band.HighestDbr, 1);
        // The gap's D+N is that one line, on a noise-free synthetic record.
        Assert.Equal(-60.0, band.DnDbr, 1);

        // The other gaps hold nothing; whatever their highest reads, it is far below the line.
        Assert.True(result.Bands[0].HighestDbr < -100.0, $"gap 0 read {result.Bands[0].HighestDbr:0.#} dBr");
        Assert.True(result.Bands[2].HighestDbr < -100.0, $"gap 2 read {result.Bands[2].HighestDbr:0.#} dBr");

        // The total is the one line too.
        Assert.Equal(-60.0, result.TdnDb, 1);
        Assert.Equal(3, result.ResolvedBands);
    }

    [Fact]
    public void TheTotal_IsThePowerSumOfTheGaps()
    {
        const double tone = 0.5;
        double combVrms = tone / Math.Sqrt(2.0) * 2.0;
        double lineVpk = combVrms * Math.Sqrt(2.0) * Dbc(-60.0);
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Hann, buffer, [.. Comb(tone), (lineVpk, 150), (lineVpk, 300), (lineVpk, 600)]);

        var result = MultitoneAnalyzer.Analyze(psd, CombBins, Nyquist);

        Assert.NotNull(result);
        Assert.Equal(-60.0 + (10.0 * Math.Log10(3.0)), result.TdnDb, 1);
        Assert.All(result.Bands, b => Assert.Equal(-60.0, b.DnDbr, 1));
    }

    [Fact]
    public void AGapWithNoise_ReadsTheNoiseAsDn()
    {
        // 0.001 V/√Hz-ish white noise: the gap integral is the noise power over its width, which the
        // analyzer must report as D+N rather than subtract away.
        const double tone = 0.5;
        var samples = TestSignals.CoherentTone(FftSize, Comb(tone));
        var noise = TestSignals.GaussianWhiteNoise(FftSize, stdDev: 0.01, seed: 7);
        for (int i = 0; i < samples.Length; i++)
            samples[i] += noise[i];
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Hann, samples, buffer);

        var result = MultitoneAnalyzer.Analyze(psd, CombBins, Nyquist);

        Assert.NotNull(result);
        // White noise of σ = 0.01 V has a two-sided PSD of σ²/fs; gap 2 spans bins 404..796,
        // i.e. 393 bins × 10 Hz. Expected power ≈ 2·σ²/fs · 3930 Hz (one-sided).
        double expectedPower = 2.0 * 0.01 * 0.01 / SampleRateHz * 3930.0;
        var band = result.Bands[2];
        Assert.InRange(band.DnVrms * band.DnVrms, expectedPower * 0.7, expectedPower * 1.3);
    }

    [Fact]
    public void TonesInOneBin_MergeIntoOneTone_AndTooCloseTonesLeaveTheGapUnresolved()
    {
        const double tone = 0.5;
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Hann, buffer, (tone, 100), (tone, 104), (tone, 400), (tone, 800));

        // 100 and 104 are 4 bins apart -- under MinToneSpacingBins -- and a duplicate of 100 is asked for.
        var result = MultitoneAnalyzer.Analyze(psd, [100, 100, 104, 400, 800], Nyquist);

        Assert.NotNull(result);
        Assert.Equal(4, result.Tones.Count);
        Assert.False(result.Bands[0].Resolved);
        Assert.True(result.Bands[1].Resolved);
        Assert.True(result.Bands[2].Resolved);
        Assert.Equal(2, result.ResolvedBands);
        Assert.Equal(1, result.UnresolvedBands);
    }

    [Fact]
    public void FewerThanTwoTonesInRange_IsNull()
    {
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Hann, buffer, (0.5, 100));

        Assert.Null(MultitoneAnalyzer.Analyze(psd, [100], Nyquist));
        Assert.Null(MultitoneAnalyzer.Analyze(psd, [100, Nyquist + 50], Nyquist));
    }

    [Fact]
    public void AGapAboveTheBandLimit_IsUnresolved()
    {
        const double tone = 0.5;
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Hann, buffer, Comb(tone));

        var result = MultitoneAnalyzer.Analyze(psd, CombBins, maxBinInclusive: 300);

        Assert.NotNull(result);
        Assert.True(result.Bands[0].Resolved);
        Assert.True(result.Bands[1].Resolved);   // clipped to bin 300, still has bins
        Assert.False(result.Bands[2].Resolved);  // entirely above the limit
    }
}
