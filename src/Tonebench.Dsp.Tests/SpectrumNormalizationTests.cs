using System.Numerics;
using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp.Tests;

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
    [InlineData(WindowType.Kaiser5)]
    [InlineData(WindowType.Kaiser7)]
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
        double excluded = psd.IntegrateVrmsExcluding([new BinRange(FundamentalBin, FundamentalBin)], psd.FullBand);

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

    // ---- golden: what a NOISE floor must read -------------------------------------------
    //
    // A coherent tone is normalised by S1 and reads the same under every window, which the
    // theory above already pins. Broadband noise is the other half of the convention and was
    // asserted nowhere until 2026-11-23: it must read PSD x ENBW_Hz, so the displayed floor
    // rises by exactly 10*log10(ENBW) as the window widens -- 1.76 dB from Rectangular to
    // Hann, 5.76 dB to Flat Top.
    //
    // Why this is a test and not a bench measurement: a real noise floor is never purely
    // broadband. Spurs, mains harmonics and clock artefacts are coherent, so they are
    // window-INDEPENDENT and dilute the step. Measured on a real QA403 the floor moved
    // anywhere from 62% to 90% of the ENBW step depending only on how much coherent content
    // the bench had that evening, which is useless as evidence either way. Synthetic white
    // noise has no coherent part by construction, so here the step must be the full ENBW and
    // any shortfall is the arithmetic.
    //
    // The errors this is shaped to catch, at Hann: applying sqrt(ENBW) instead of ENBW (an
    // amplitude-versus-power slip) reads 0.88 dB low; omitting the term entirely reads
    // 1.76 dB low; both grow with the window's width. See docs/qa40x-spectrum-scaling.md.

    private const int NoiseFftSize = 65536;
    private const double NoiseSampleRateHz = 48000.0;
    private const int NoiseSeed = 20261123;

    /// <summary>
    /// Mean of <c>VrmsAt(k)^2</c> over the interior bins, plus the realisation's own sample
    /// variance. Returns plain doubles because <see cref="AmplitudeSpectrum"/> is a ref struct
    /// and cannot leave the method inside a tuple.
    /// </summary>
    private static (double MeanBinVrms2, double VarianceV2) WhiteNoiseFloor(WindowType windowType, int seed)
    {
        var w = Window.Get(windowType, NoiseFftSize);
        var samples = TestSignals.GaussianWhiteNoise(NoiseFftSize, stdDev: 0.1, seed);

        // The realisation's own variance, not the nominal 0.1^2: the sample variance of 65536
        // draws scatters by ~0.55%, which would otherwise swamp the quantity being tested.
        double varianceV2 = 0.0;
        foreach (double s in samples)
            varianceV2 += s * s;
        varianceV2 /= NoiseFftSize;

        var windowed = new double[NoiseFftSize];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[NoiseFftSize / 2 + 1];
        new FftFlatBackend().Forward(windowed, spectrum);

        var destination = new double[spectrum.Length];
        var amplitude = AmplitudeSpectrum.Compute(spectrum, w.Sums, destination);

        // DC and Nyquist carry the one-sided factor of two that only interior bins earn, so
        // they are not comparable; start past the window's own DC guard for the same reason.
        int first = w.Sums.DcGuardBins;
        int last = spectrum.Length - 2;
        double sum = 0.0;
        for (int k = first; k <= last; k++)
        {
            double vrms = amplitude.VrmsAt(k);
            sum += vrms * vrms;
        }

        return (sum / (last - first + 1), varianceV2);
    }

    [Theory]
    [InlineData(WindowType.Rectangular)]
    [InlineData(WindowType.Hann)]
    [InlineData(WindowType.Hamming)]
    [InlineData(WindowType.BlackmanHarris)]
    [InlineData(WindowType.FlatTop)]
    [InlineData(WindowType.Kaiser5)]
    [InlineData(WindowType.Kaiser7)]
    public void AmplitudeSpectrum_WhiteNoise_ReadsThePsdTimesTheWindowsEnbw(WindowType windowType)
    {
        var w = Window.Get(windowType, NoiseFftSize);
        var (meanBinVrms2, varianceV2) = WhiteNoiseFloor(windowType, NoiseSeed);

        double psdV2PerHz = 2.0 * varianceV2 / NoiseSampleRateHz;      // one-sided
        double expectedV2 = psdV2PerHz * w.Sums.EnbwHz(NoiseSampleRateHz);

        double errorDb = 10.0 * Math.Log10(meanBinVrms2 / expectedV2);
        Assert.True(
            Math.Abs(errorDb) < 0.15,
            $"{windowType}: noise floor reads {errorDb:+0.000;-0.000} dB away from PSD x ENBW " +
            $"(ENBW {w.Sums.EnbwBins:0.0000} bins).");
    }

    [Theory]
    [InlineData(WindowType.Rectangular)]
    [InlineData(WindowType.Hamming)]
    [InlineData(WindowType.BlackmanHarris)]
    [InlineData(WindowType.FlatTop)]
    [InlineData(WindowType.Kaiser5)]
    [InlineData(WindowType.Kaiser7)]
    public void AmplitudeSpectrum_WhiteNoise_FloorStepsFromHannByTheEnbwRatio(WindowType windowType)
    {
        var hann = Window.Get(WindowType.Hann, NoiseFftSize);
        var other = Window.Get(windowType, NoiseFftSize);

        // Same seed for both, so the two floors share one noise realisation and the comparison
        // is paired -- the step is then far tighter than either floor's own scatter.
        var (hannFloor, _) = WhiteNoiseFloor(WindowType.Hann, NoiseSeed);
        var (otherFloor, _) = WhiteNoiseFloor(windowType, NoiseSeed);

        double measuredStepDb = 10.0 * Math.Log10(otherFloor / hannFloor);
        double enbwStepDb = 10.0 * Math.Log10(other.Sums.EnbwBins / hann.Sums.EnbwBins);

        Assert.True(
            Math.Abs(measuredStepDb - enbwStepDb) < 0.1,
            $"{windowType}: floor moved {measuredStepDb:0.000} dB from Hann, " +
            $"ENBW requires {enbwStepDb:0.000} dB.");
    }
}
