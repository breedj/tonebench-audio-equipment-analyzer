using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

/// <summary>
/// The seven golden tests from the Phase 2 spec. Non-negotiable: if a change breaks one of
/// these, the change is wrong, not the test. Every downstream dB value depends on Parseval
/// holding, so that one gates everything else.
/// </summary>
public class GoldenTests
{
    private const int FftSize = 1024;
    private const double SampleRateHz = 40960.0; // binSizeHz = 40 -> 1 kHz lands exactly on bin 25
    private const int FundamentalBin = 25; // 25 * 40 Hz = 1000 Hz

    private static readonly WindowType[] AllWindows =
    [
        WindowType.Rectangular,
        WindowType.Hann,
        WindowType.Hamming,
        WindowType.BlackmanHarris,
        WindowType.FlatTop,
    ];

    private static readonly IFftBackend[] AllBackends = [new FftSharpBackend(), new MathNetBackend()];

    // 1. 1 kHz coherent sine, 1.0 V peak -> -3.01 dBV (0.7071 Vrms), within 0.01 dB, for every window.
    [Fact]
    public void Golden_CoherentTone_1kHz_1VPeak_ReadsMinus3_01Dbv_AcrossEveryWindowAndBackend()
    {
        foreach (var backend in AllBackends)
        {
            foreach (var windowType in AllWindows)
            {
                var w = Window.Get(windowType, FftSize);
                var samples = TestSignals.CoherentTone(FftSize, (1.0, FundamentalBin));
                var windowed = new double[FftSize];
                Window.Apply(samples, w, windowed);

                var spectrum = new Complex[FftSize / 2 + 1];
                backend.Forward(windowed, spectrum);

                var destination = new double[spectrum.Length];
                var amplitude = AmplitudeSpectrum.Compute(spectrum, w.Sums, destination);

                Assert.True(
                    Math.Abs(-3.0103 - amplitude.DbvAt(FundamentalBin)) < 0.01,
                    $"{backend.GetType().Name}/{windowType}: expected -3.0103 dBV, got {amplitude.DbvAt(FundamentalBin):F4} dBV");
            }
        }
    }

    // 2. Parseval: Sum(PSD*df) over the full band equals time-domain variance, <0.1% error.
    [Fact]
    public void Golden_Parseval_PsdIntegratesToTimeDomainVariance()
    {
        var samples = TestSignals.ZeroMeanNoise(FftSize, seed: 2026);

        // Rectangular window: Parseval holds without any window-energy correction to verify against.
        var w = Window.Get(WindowType.Rectangular, FftSize);

        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(samples, spectrum);

        var destination = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, destination);
        double vTot = psd.IntegrateVrms();

        double variance = samples.Sum(x => x * x) / samples.Length;

        double relativeError = Math.Abs(vTot * vTot - variance) / variance;
        Assert.True(relativeError < 0.001, $"relative error {relativeError:P4} exceeds 0.1%");
    }

    // 3. ENBW: Rectangular = 1.000 bins, Hann = 1.500 bins, within 1e-6.
    [Fact]
    public void Golden_Enbw_RectangularIsOneBin_HannIsOneAndAHalfBins()
    {
        var rect = Window.Get(WindowType.Rectangular, FftSize);
        var hann = Window.Get(WindowType.Hann, FftSize);

        Assert.Equal(1.000, rect.Sums.EnbwBins, 6);
        Assert.Equal(1.500, hann.Sums.EnbwBins, 6);
    }

    // 4. Fundamental + H2 at exactly 1.000% amplitude -> THD reads 1.000% +/- 0.001%.
    [Fact]
    public void Golden_FundamentalPlusOnePercentSecondHarmonic_ThdReadsOnePercent()
    {
        var w = Window.Get(WindowType.Hann, FftSize);
        var samples = TestSignals.CoherentTone(FftSize, (1.0, FundamentalBin), (0.01, FundamentalBin * 2));
        var windowed = new double[FftSize];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(windowed, spectrum);

        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, psdBuffer);

        var harmonics = new Harmonic[2];
        HarmonicAnalyzer.Analyze(psd, FundamentalBin, harmonicCount: 2, harmonics);

        double thd = SpectrumMetrics.Thd(harmonics);

        Assert.True(Math.Abs(0.01 - thd) < 0.00001, $"expected THD 1.000% +/- 0.001%, got {thd * 100:F4}%");
    }

    // 5. White noise, variance sigma^2 -> PSD flat at sigma^2/(fs/2) +/- 0.5 dB when averaged.
    [Fact]
    public void Golden_WhiteNoise_PsdIsFlatAtVarianceOverNyquistBandwidth()
    {
        const double sigma = 0.2;
        const int blocks = 64;

        var w = Window.Get(WindowType.Rectangular, FftSize);
        var backend = new FftSharpBackend();
        var spectrum = new Complex[FftSize / 2 + 1];
        var psdBuffer = new double[FftSize / 2 + 1];
        var accumulated = new double[FftSize / 2 + 1];

        for (int b = 0; b < blocks; b++)
        {
            var samples = TestSignals.GaussianWhiteNoise(FftSize, sigma, seed: 1000 + b);
            backend.Forward(samples, spectrum);
            var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, psdBuffer);
            for (int k = 0; k < accumulated.Length; k++)
                accumulated[k] += psd.ValuesV2PerHz[k];
        }

        // Average over blocks (reduces per-bin estimator variance) and then over interior bins
        // (white noise bins are independent, so this averages out the remaining scatter).
        double averagePsd = accumulated.Skip(4).SkipLast(4).Select(sum => sum / blocks).Average();
        double theoreticalPsd = sigma * sigma / (SampleRateHz / 2.0);

        double errorDb = 10.0 * Math.Log10(averagePsd / theoreticalPsd);
        Assert.True(Math.Abs(errorDb) < 0.5, $"PSD level error {errorDb:F3} dB exceeds 0.5 dB");
    }

    // 6. Non-coherent (off-bin) tone: flat-top amplitude error <0.02 dB; Hann <0.5 dB.
    [Theory]
    [InlineData(WindowType.FlatTop, 0.02)]
    [InlineData(WindowType.Hann, 0.5)]
    public void Golden_NonCoherentTone_AmplitudeErrorWithinWindowScallopingBound(WindowType windowType, double maxAbsErrorDb)
    {
        const double binSizeHz = SampleRateHz / FftSize; // 40 Hz
        double offBinFrequencyHz = (FundamentalBin + 0.25) * binSizeHz; // deliberately not on a bin center

        var w = Window.Get(windowType, FftSize);
        var samples = TestSignals.Tone(FftSize, amplitudeV: 1.0, offBinFrequencyHz, SampleRateHz);
        var windowed = new double[FftSize];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(windowed, spectrum);

        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, psdBuffer);
        int peak = HarmonicAnalyzer.FindPeakBin(psd.ValuesV2PerHz, FundamentalBin, searchSpanBins: 2);

        var destination = new double[spectrum.Length];
        var amplitude = AmplitudeSpectrum.Compute(spectrum, w.Sums, destination);

        double expectedDbv = 20.0 * Math.Log10(1.0 / Math.Sqrt(2.0));
        double errorDb = amplitude.DbvAt(peak) - expectedDbv;

        Assert.True(
            Math.Abs(errorDb) < maxAbsErrorDb,
            $"{windowType}: amplitude error {errorDb:F4} dB exceeds {maxAbsErrorDb} dB bound");
    }

    // 7. Decimation: a single-bin spike must survive decimation to 800 columns.
    [Fact]
    public void Golden_Decimation_SingleBinSpikeSurvivesTo800Columns()
    {
        const int n = 131073; // one-sided spectrum of a 256k-point real FFT
        var values = new double[n];
        Array.Fill(values, -140.0);
        values[77777] = -6.0;

        var destination = new MinMax[800];
        SpectrumDecimator.DecimateLinear(values, destination);

        Assert.Contains(destination, mm => mm.Max >= -6.0);

        var logDestination = new MinMax[800];
        SpectrumDecimator.DecimateLog(values, logDestination, startBin: 1);

        Assert.Contains(logDestination, mm => mm.Max >= -6.0);
    }
}
