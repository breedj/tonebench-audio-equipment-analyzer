using System.Numerics;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The clock measurement rests entirely on this estimator, and the figures below are what make it
/// usable: a hundredth of a bin — <see cref="HarmonicAnalyzer"/>'s accuracy, and ample for a level —
/// is already 15 ppm at 1 kHz on these records, which is the whole quantity.
/// </summary>
public class ToneFrequencyTests
{
    private const int N = 32768;
    private const double Fs = 48000.0;
    private const double BinHz = Fs / N;

    /// <summary>A Hann-windowed tone at an exact frequency, as the pipeline would hand it over.</summary>
    private static Complex[] Spectrum(double hz, double noiseRms = 0, int seed = 1)
    {
        var window = Window.Get(WindowType.Hann, N);

        var random = new Random(seed);
        var raw = new double[N];
        for (int n = 0; n < N; n++)
        {
            double noise = noiseRms > 0 ? NextGaussian(random) * noiseRms : 0;
            raw[n] = Math.Cos((2 * Math.PI * hz * n / Fs) + 0.7) + noise;
        }

        var samples = new double[N];
        Window.Apply(raw, window, samples);

        var backend = new FftFlatBackend();
        var spectrum = new Complex[(N / 2) + 1];
        backend.Forward(samples, spectrum);
        return spectrum;
    }

    private static double NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    [Theory]
    [InlineData(-0.45)]
    [InlineData(-0.25)]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.33)]
    [InlineData(0.45)]
    public void OnACleanTone_TheHannInterpolatorIsExact(double offsetBins)
    {
        double hz = (1365 + offsetBins) * BinHz;

        var estimate = ToneFrequency.Estimate(Spectrum(hz), BinHz, hz, 20 * BinHz);

        Assert.NotNull(estimate);
        // A thousandth of a bin would be a generous tolerance; this holds to a billionth, which is
        // the point — the formula is the window's own identity rather than a fit to it.
        Assert.Equal(hz, estimate!.Value.Hz, 1e-6);
    }

    [Theory]
    [InlineData(60, 0.2)]
    [InlineData(40, 2.0)]
    public void WithNoise_TheErrorStaysFarBelowTheQuantityBeingMeasured(double snrDb, double limitPpm)
    {
        // A sine of unit amplitude has an rms of 1/sqrt(2); the noise rms that sits snrDb below it.
        double hz = (1365 + 0.33) * BinHz;
        double noiseRms = Math.Pow(10, -snrDb / 20) / Math.Sqrt(2);

        var estimate = ToneFrequency.Estimate(Spectrum(hz, noiseRms), BinHz, hz, 20 * BinHz);

        Assert.NotNull(estimate);
        double ppm = Math.Abs(ToneFrequency.DriftPpm(estimate!.Value.Hz, hz));
        Assert.True(ppm < limitPpm, $"{snrDb:0} dB SNR read {ppm:0.000} ppm of error, over the {limitPpm} ppm allowed");
    }

    [Fact]
    public void TheSearchIsAroundTheToneAsked_NotTheLoudestThingInTheCapture()
    {
        // The hum ladder that cost a wrong answer by hand: eight times the tone's amplitude at
        // 50 Hz. Taking the spectrum's maximum measures the hum's frequency to great precision.
        double hz = (1365 + 0.3) * BinHz;
        var raw = new double[N];
        for (int n = 0; n < N; n++)
        {
            raw[n] = Math.Cos(2 * Math.PI * hz * n / Fs) + (8 * Math.Cos(2 * Math.PI * 50.0 * n / Fs));
        }

        var samples = new double[N];
        Window.Apply(raw, Window.Get(WindowType.Hann, N), samples);

        var spectrum = new Complex[(N / 2) + 1];
        new FftFlatBackend().Forward(samples, spectrum);

        var estimate = ToneFrequency.Estimate(spectrum, BinHz, hz, 20 * BinHz);

        Assert.NotNull(estimate);
        Assert.Equal(hz, estimate!.Value.Hz, 1e-3);
    }

    [Fact]
    public void ATonePresentButBuried_IsReportedAsSuch_RatherThanRefused()
    {
        // Dominance is a reading, not a gate: the estimate is still returned and the caller decides.
        // Measured by hand on this bench, a tone 36 dB under a hum ladder gave an epsilon that was
        // wrong by a factor of ten and looked entirely plausible.
        double hz = (1365 + 0.3) * BinHz;

        var loud = ToneFrequency.Estimate(Spectrum(hz), BinHz, hz, 20 * BinHz);
        var buried = ToneFrequency.Estimate(Spectrum(hz, noiseRms: 4.0), BinHz, hz, 20 * BinHz);

        Assert.NotNull(loud);
        Assert.NotNull(buried);
        Assert.True(loud!.Value.DominanceDb > 40, $"a clean tone read {loud.Value.DominanceDb:0.0} dB");
        Assert.True(buried!.Value.DominanceDb < 0, $"a buried tone read {buried.Value.DominanceDb:0.0} dB");
    }

    [Fact]
    public void DriftPpm_IsTheRatioOfTheTwoClocks_NotADifferenceInHertz()
    {
        // 104.37 ppm is this bench's own measured figure, a MOTU M4 against a Sound BlasterX G6.
        Assert.Equal(104.37, ToneFrequency.DriftPpm(1000.10437, 1000.0), 2);

        // The same fractional error at a different tone is the same ppm, which is the whole reason
        // the figure is expressed this way.
        Assert.Equal(104.37, ToneFrequency.DriftPpm(5000.52185, 5000.0), 2);
    }

    [Fact]
    public void AnEmptyOrEdgeSearch_AnswersNothingRatherThanGuessing()
    {
        var spectrum = Spectrum(1000.0);

        Assert.Null(ToneFrequency.Estimate(spectrum, BinHz, nearHz: 1e9, halfWidthHz: BinHz));
        Assert.Null(ToneFrequency.Estimate([], BinHz, 1000, 10));
    }
}
