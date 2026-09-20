using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

/// <summary>
/// Band RMS, noise density and the weighted noise integrals. The weighted figures are produced the
/// way the pipeline produces them: a copy of the PSD multiplied per bin by the curve's power gain,
/// handed to the unchanged SpectrumMetrics integrals.
/// </summary>
public class NoiseFigureMetricsTests
{
    private const int FftSize = 4096;
    private const double SampleRateHz = 40960.0; // 10 Hz bins
    private const int FundamentalBin = 100;     // 1 kHz

    private static PsdSpectrum Psd(double[] samples, WindowType windowType, double[] psdBuffer)
    {
        var w = Window.Get(windowType, FftSize);
        var windowed = new double[FftSize];
        Window.Apply(samples, w, windowed);
        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(windowed, spectrum);
        return PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, psdBuffer);
    }

    private static double[] Weighted(PsdSpectrum psd, IWeightingCurve curve)
    {
        var gains = new double[psd.ValuesV2PerHz.Length];
        WeightingTable.FillPowerGains(curve, psd.BinSizeHz, gains);
        var weighted = new double[gains.Length];
        for (int k = 0; k < gains.Length; k++)
            weighted[k] = psd.ValuesV2PerHz[k] * gains[k];
        return weighted;
    }

    [Fact]
    public void BandRms_OfAnInBandTone_EqualsItsRmsByParseval()
    {
        var samples = TestSignals.CoherentTone(FftSize, (1.0, FundamentalBin));
        var psd = Psd(samples, WindowType.Hann, new double[FftSize / 2 + 1]);

        double bandRms = SpectrumMetrics.BandRmsVrms(psd, psd.BandOf(20.0, 20000.0));

        Assert.Equal(1.0 / Math.Sqrt(2.0), bandRms, 4);
    }

    [Fact]
    public void BandRms_ExcludesWhatIsOutsideTheBand()
    {
        // A 1 kHz tone at 1 V and a 15 kHz tone at 0.5 V: a 20 Hz-10 kHz band sees only the first.
        var samples = TestSignals.CoherentTone(FftSize, (1.0, FundamentalBin), (0.5, 1500));
        var psd = Psd(samples, WindowType.Hann, new double[FftSize / 2 + 1]);

        Assert.Equal(1.0 / Math.Sqrt(2.0), SpectrumMetrics.BandRmsVrms(psd, psd.BandOf(20.0, 10000.0)), 4);
        Assert.Equal(Math.Sqrt(1.0 * 1.0 / 2.0 + 0.5 * 0.5 / 2.0), SpectrumMetrics.BandRmsVrms(psd, psd.BandOf(20.0, 20000.0)), 4);
    }

    [Fact]
    public void NoiseDensity_OfWhiteNoise_ReadsTwoSigmaSquaredOverFs()
    {
        // White noise of variance σ² has a one-sided density of 2σ²/fs. Averaging several records
        // brings the chi-square spread of each bin down to where a 0.3 dB check is meaningful.
        const double sigma = 0.01;
        double expected = 2.0 * sigma * sigma / SampleRateHz;
        int specLen = FftSize / 2 + 1;
        var mean = new double[specLen];
        const int records = 32;
        for (int r = 0; r < records; r++)
        {
            var psd = Psd(TestSignals.GaussianWhiteNoise(FftSize, sigma, seed: 100 + r), WindowType.Hann, new double[specLen]);
            for (int k = 0; k < specLen; k++)
                mean[k] += psd.ValuesV2PerHz[k] / records;
        }
        var averaged = PsdSpectrum.FromValues(mean, SampleRateHz, SampleRateHz / FftSize, Window.Get(WindowType.Hann, FftSize).Sums);

        double density = SpectrumMetrics.NoiseDensityV2PerHz(averaged, averaged.BandOf(20.0, 20000.0));

        double errorDb = 10.0 * Math.Log10(density / expected);
        Assert.InRange(errorDb, -0.3, 0.3);
    }

    [Fact]
    public void NoiseDensity_IsNotBiasedByTheNotch()
    {
        // The same white record, with and without a fundamental notch (a huge one: three octaves).
        // A density defined as power over the band's width would read low by the notched fraction;
        // the mean over the included bins does not.
        const double sigma = 0.01;
        int specLen = FftSize / 2 + 1;
        var mean = new double[specLen];
        const int records = 32;
        for (int r = 0; r < records; r++)
        {
            var psd = Psd(TestSignals.GaussianWhiteNoise(FftSize, sigma, seed: 200 + r), WindowType.Hann, new double[specLen]);
            for (int k = 0; k < specLen; k++)
                mean[k] += psd.ValuesV2PerHz[k] / records;
        }
        var averaged = PsdSpectrum.FromValues(mean, SampleRateHz, SampleRateHz / FftSize, Window.Get(WindowType.Hann, FftSize).Sums);
        var band = averaged.BandOf(20.0, 20000.0);

        double plain = SpectrumMetrics.NoiseDensityV2PerHz(averaged, band);
        var fakeFundamental = new Harmonic(1, FundamentalBin, 1.0, 0.0, 0.0);
        double notched = SpectrumMetrics.NoiseDensityV2PerHz(averaged, [fakeFundamental], band, notchOctaves: 3.0);

        Assert.InRange(10.0 * Math.Log10(notched / plain), -0.1, 0.1);
    }

    [Fact]
    public void WeightedThdPlusN_OfWhiteNoiseUnderA_DropsByTheCurvesBandMean()
    {
        // A tone plus white noise: THD+N through an A-weighted PSD must drop by exactly the band
        // mean of the curve's power gain (white noise weights every bin equally), not by the
        // curve's value at any one frequency. Sanity: the textbook figure for 20 Hz-20 kHz white
        // noise is about -2 dB.
        const double sigma = 0.001;
        int specLen = FftSize / 2 + 1;
        var tone = TestSignals.CoherentTone(FftSize, (1.0, FundamentalBin));
        var mean = new double[specLen];
        const int records = 16;
        for (int r = 0; r < records; r++)
        {
            var noise = TestSignals.GaussianWhiteNoise(FftSize, sigma, seed: 300 + r);
            var samples = new double[FftSize];
            for (int i = 0; i < FftSize; i++)
                samples[i] = tone[i] + noise[i];
            var psd = Psd(samples, WindowType.Hann, new double[specLen]);
            for (int k = 0; k < specLen; k++)
                mean[k] += psd.ValuesV2PerHz[k] / records;
        }
        var sums = Window.Get(WindowType.Hann, FftSize).Sums;
        var averaged = PsdSpectrum.FromValues(mean, SampleRateHz, SampleRateHz / FftSize, sums);
        var band = averaged.BandOf(20.0, 20000.0);

        var harmonics = new Harmonic[1];
        HarmonicAnalyzer.Analyze(averaged, FundamentalBin, 1, harmonics);
        double unweighted = SpectrumMetrics.ThdPlusNDb(averaged, harmonics[0], band);

        var weightedValues = Weighted(averaged, AWeighting.Instance);
        var weighted = PsdSpectrum.FromValues(weightedValues, SampleRateHz, averaged.BinSizeHz, sums);
        double weightedDb = SpectrumMetrics.ThdPlusNDb(weighted, harmonics[0], band);

        // Expected drop: the mean power gain over the bins the integral keeps (band minus the notch).
        var gains = new double[specLen];
        WeightingTable.FillPowerGains(AWeighting.Instance, averaged.BinSizeHz, gains);
        var notch = SpectrumMetrics.FundamentalNotch(averaged, harmonics[0]);
        double gainSum = 0.0;
        int count = 0;
        for (int k = band.LoBinInclusive; k <= band.HiBinInclusive; k++)
        {
            if (notch.Contains(k)) continue;
            gainSum += gains[k];
            count++;
        }
        double expectedDropDb = 10.0 * Math.Log10(gainSum / count);

        Assert.InRange(expectedDropDb, -3.0, -1.5);
        // Finite averaging leaves each bin with some chi-square spread, so the weighted sum is the
        // band mean only to within a few hundredths of a dB.
        Assert.InRange(weightedDb - unweighted, expectedDropDb - 0.1, expectedDropDb + 0.1);
    }

    [Fact]
    public void WeightedIntegral_LeavesA1kHzToneUntouched()
    {
        // Every standard curve is 0 dB at 1 kHz, so a weighted band RMS of a pure 1 kHz tone is the
        // tone's own RMS — the weighting must not move the reference.
        var samples = TestSignals.CoherentTone(FftSize, (1.0, FundamentalBin));
        var psd = Psd(samples, WindowType.Hann, new double[FftSize / 2 + 1]);
        var band = psd.BandOf(20.0, 20000.0);

        foreach (var standard in new[] { WeightingStandard.A, WeightingStandard.C, WeightingStandard.ItuR468, WeightingStandard.RiaaPlayback, WeightingStandard.RiaaRecord })
        {
            var weighted = PsdSpectrum.FromValues(Weighted(psd, StandardWeighting.For(standard)), SampleRateHz, psd.BinSizeHz, psd.Sums);
            Assert.Equal(1.0 / Math.Sqrt(2.0), SpectrumMetrics.BandRmsVrms(weighted, band), 3);
        }
    }
}
