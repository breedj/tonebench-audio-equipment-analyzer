using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class SpectrumMetricsTests
{
    private const int FftSize = 1024;
    private const double SampleRateHz = 40960.0;
    private const int FundamentalBin = 25;

    [Fact]
    public void BinSizeHz_IsSampleRateOverFftSize()
    {
        Assert.Equal(40.0, SpectrumMetrics.BinSizeHz(40960.0, 1024), 9);
    }

    [Fact]
    public void BinOfFrequency_RoundsToNearestBin()
    {
        Assert.Equal(25, SpectrumMetrics.BinOfFrequency(1000.0, 40.0));
        Assert.Equal(25, SpectrumMetrics.BinOfFrequency(1015.0, 40.0));
        Assert.Equal(26, SpectrumMetrics.BinOfFrequency(1025.0, 40.0));
    }

    private static Harmonic[] Analyze(
        WindowType windowType, int harmonicCount, out PsdSpectrum psd, params (double AmplitudeV, int Bin)[] components)
    {
        var w = Window.Get(windowType, FftSize);
        var samples = TestSignals.CoherentTone(FftSize, components);
        var windowed = new double[FftSize];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(windowed, spectrum);

        var psdBuffer = new double[spectrum.Length];
        psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, psdBuffer);

        var harmonics = new Harmonic[harmonicCount];
        int written = HarmonicAnalyzer.Analyze(psd, FundamentalBin, harmonicCount, harmonics);
        Array.Resize(ref harmonics, written);

        return harmonics;
    }

    [Fact]
    public void Thd_FundamentalPlusOnePercentSecondHarmonic_ReadsOnePercent()
    {
        var harmonics = Analyze(WindowType.Hann, 2, out _, (1.0, FundamentalBin), (0.01, FundamentalBin * 2));

        double thd = SpectrumMetrics.Thd(harmonics);

        Assert.Equal(0.01, thd, 4);
    }

    [Fact]
    public void Thd_ThrowsWhenFirstHarmonicIsNotOrderOne()
    {
        var bogus = new[] { new Harmonic(2, 50, 0.1, -20, -6) };
        Assert.Throws<ArgumentException>(() => SpectrumMetrics.Thd(bogus));
    }

    [Fact]
    public void ThdPlusN_PureToneNoNoise_IsNearZero()
    {
        var harmonics = Analyze(WindowType.Hann, 1, out var psd, (1.0, FundamentalBin));

        double thdN = SpectrumMetrics.ThdPlusN(psd, harmonics[0], psd.FullBand);

        Assert.True(thdN < 1e-5, $"expected near-zero THD+N for a pure coherent tone, got {thdN}");
    }

    [Fact]
    public void ThdPlusN_NotchesFundamentalRatherThanSubtracting()
    {
        // Add a tiny amount of broadband noise on top of the coherent tone. THD+N must equal
        // the noise-only residual once the fundamental's bins are excluded, not
        // sqrt(total^2 - V1^2) computed from the (possibly noisy) total.
        var w = Window.Get(WindowType.Rectangular, FftSize);
        var tone = TestSignals.CoherentTone(FftSize, (1.0, FundamentalBin));
        var noise = TestSignals.GaussianWhiteNoise(FftSize, stdDev: 0.001, seed: 7);
        var samples = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
            samples[i] = tone[i] + noise[i];

        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(samples, spectrum);
        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, psdBuffer);

        var harmonics = new Harmonic[1];
        HarmonicAnalyzer.Analyze(psd, FundamentalBin, 1, harmonics);

        double thdN = SpectrumMetrics.ThdPlusN(psd, harmonics[0], psd.FullBand);
        var notch = SpectrumMetrics.FundamentalNotch(psd, harmonics[0]);
        double manualNoiseOnly = psd.IntegrateVrmsExcluding([notch], psd.FullBand) / harmonics[0].AmplitudeVrms;

        Assert.Equal(manualNoiseOnly, thdN, 9);
        Assert.True(thdN > 0.0);
    }

    [Fact]
    public void Sinad_IsReciprocalOfThdPlusN()
    {
        var harmonics = Analyze(WindowType.Hann, 1, out var psd, (1.0, FundamentalBin));

        double thdN = SpectrumMetrics.ThdPlusN(psd, harmonics[0], psd.FullBand);
        double sinad = SpectrumMetrics.Sinad(psd, harmonics[0], psd.FullBand);

        if (thdN > 0)
            Assert.Equal(1.0 / thdN, sinad, 3);
    }

    [Fact]
    public void Snr_ExcludesHarmonicsFromNoiseFloor()
    {
        // A "clean" tone plus a deliberately huge 2nd harmonic: SNR (which excludes harmonics)
        // should stay high even though THD+N (which does not) would read poorly.
        var harmonics = Analyze(WindowType.Hann, 2, out var psd, (1.0, FundamentalBin), (0.5, FundamentalBin * 2));

        double snr = SpectrumMetrics.Snr(psd, harmonics, psd.FullBand);

        Assert.True(snr > 100, $"expected SNR to ignore the harmonic and stay high, got {snr}");
    }

    [Fact]
    public void BandNoiseFloorVrms_IntegratesOnlyRequestedBand()
    {
        var w = Window.Get(WindowType.Rectangular, FftSize);
        var samples = TestSignals.GaussianWhiteNoise(FftSize, stdDev: 0.01, seed: 99);
        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(samples, spectrum);
        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, psdBuffer);

        double fullBand = psd.IntegrateVrms();
        double halfBand = SpectrumMetrics.BandNoiseFloorVrms(psd, 0.0, SampleRateHz / 4.0);

        Assert.True(halfBand < fullBand);
        Assert.True(halfBand > 0.0);
    }
}
