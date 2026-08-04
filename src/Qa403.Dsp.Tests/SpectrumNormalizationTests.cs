using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class SpectrumNormalizationTests
{
    private const int FftSize = 1024;
    private const double SampleRateHz = 40960.0;
    private const int FundamentalBin = 25;

    private static Complex[] CoherentSpectrum(WindowType windowType, double amplitudeV = 1.0)
    {
        var w = Window.Get(windowType, FftSize);
        var samples = TestSignals.CoherentTone(FftSize, (amplitudeV, FundamentalBin));
        var windowed = new double[FftSize];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(windowed, spectrum);
        return spectrum;
    }

    [Theory]
    [InlineData(WindowType.Rectangular)]
    [InlineData(WindowType.Hann)]
    [InlineData(WindowType.Hamming)]
    [InlineData(WindowType.BlackmanHarris)]
    [InlineData(WindowType.FlatTop)]
    public void AmplitudeSpectrum_CoherentTone_PeakBinReadsExactVoltPeak(WindowType windowType)
    {
        var w = Window.Get(windowType, FftSize);
        var spectrum = CoherentSpectrum(windowType, amplitudeV: 1.0);
        var destination = new double[spectrum.Length];

        var amplitude = AmplitudeSpectrum.Compute(spectrum, w.Sums, destination);

        Assert.Equal(1.0, amplitude.PeakVoltsPerBin[FundamentalBin], 6);
        Assert.Equal(1.0 / Math.Sqrt(2.0), amplitude.VrmsAt(FundamentalBin), 6);
    }

    [Fact]
    public void AmplitudeSpectrum_Compute_RejectsMismatchedDestinationLength()
    {
        var w = Window.Get(WindowType.Hann, FftSize);
        var spectrum = CoherentSpectrum(WindowType.Hann);

        Assert.Throws<ArgumentException>(() => AmplitudeSpectrum.Compute(spectrum, w.Sums, new double[3]));
    }

    [Fact]
    public void PsdSpectrum_WeightsDcAndNyquistHalfOfInteriorBins()
    {
        const int n = 64;
        var samples = TestSignals.ZeroMeanNoise(n, seed: 42);
        var w = Window.Get(WindowType.Rectangular, n);

        var spectrum = new Complex[n / 2 + 1];
        new FftSharpBackend().Forward(samples, spectrum);

        var destination = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, 48000.0, w.Sums, destination);

        double dcScaleImpliedRatio = psd.ValuesV2PerHz[0] / (spectrum[0].Magnitude * spectrum[0].Magnitude);
        double interiorScaleImpliedRatio = psd.ValuesV2PerHz[1] / (spectrum[1].Magnitude * spectrum[1].Magnitude);

        Assert.Equal(interiorScaleImpliedRatio / 2.0, dcScaleImpliedRatio, 9);
    }

    [Fact]
    public void PsdSpectrum_IntegrateVrms_RecoversAmplitudeOfCoherentTone()
    {
        var w = Window.Get(WindowType.Hann, FftSize);
        var spectrum = CoherentSpectrum(WindowType.Hann);
        var destination = new double[spectrum.Length];

        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, destination);

        // Hann leaks a coherent tone's energy into bin-1/bin/bin+1; the full mainlobe must
        // integrate back to the exact input Vrms.
        double vrms = psd.IntegrateVrms(FundamentalBin - 3, FundamentalBin + 3);
        Assert.Equal(1.0 / Math.Sqrt(2.0), vrms, 6);
    }

    [Fact]
    public void PsdSpectrum_IntegrateVrmsExcluding_RemovesOnlyTheExcludedBins()
    {
        var w = Window.Get(WindowType.Rectangular, FftSize);
        var spectrum = CoherentSpectrum(WindowType.Rectangular);
        var destination = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, destination);

        double full = psd.IntegrateVrms();
        double excluded = psd.IntegrateVrmsExcluding([new BinRange(FundamentalBin, FundamentalBin)]);

        // Rectangular window puts 100% of a coherent tone's energy in exactly one bin, so
        // excluding it should leave (numerically) nothing.
        Assert.True(full > 0.5);
        Assert.True(excluded < 1e-6, $"expected near-zero residual, got {excluded}");
    }

    [Fact]
    public void PsdSpectrum_BinOfFrequency_MatchesBinSizeMath()
    {
        var w = Window.Get(WindowType.Rectangular, FftSize);
        var spectrum = CoherentSpectrum(WindowType.Rectangular);
        var destination = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, destination);

        Assert.Equal(FundamentalBin, psd.BinOfFrequency(1000.0));
        Assert.Equal(40.0, psd.BinSizeHz, 9);
    }
}
