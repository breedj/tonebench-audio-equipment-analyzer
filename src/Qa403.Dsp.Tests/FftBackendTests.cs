using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class FftBackendTests
{
    public static IEnumerable<object[]> Backends()
    {
        yield return [new FftSharpBackend()];
        yield return [new MathNetBackend()];
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Forward_RejectsNonPowerOfTwoLength(IFftBackend backend)
    {
        var timeIn = new double[3];
        var specOut = new Complex[3];
        Assert.Throws<ArgumentException>(() => backend.Forward(timeIn, specOut));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Forward_RejectsWrongSpecOutLength(IFftBackend backend)
    {
        var timeIn = new double[64];
        var specOut = new Complex[64]; // should be 33
        Assert.Throws<ArgumentException>(() => backend.Forward(timeIn, specOut));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Forward_ConstantSignal_EnergyIsAllInDcBin(IFftBackend backend)
    {
        const int n = 64;
        var samples = new double[n];
        Array.Fill(samples, 2.0);
        var spectrum = new Complex[n / 2 + 1];

        backend.Forward(samples, spectrum);

        Assert.Equal(n * 2.0, spectrum[0].Magnitude, 6);
        for (int k = 1; k < spectrum.Length; k++)
            Assert.Equal(0.0, spectrum[k].Magnitude, 6);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Forward_Impulse_ProducesFlatMagnitudeSpectrum(IFftBackend backend)
    {
        const int n = 64;
        var samples = new double[n];
        samples[0] = 1.0;
        var spectrum = new Complex[n / 2 + 1];

        backend.Forward(samples, spectrum);

        foreach (var bin in spectrum)
            Assert.Equal(1.0, bin.Magnitude, 6);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Forward_CoherentTone_PeakBinMatchesAnalyticAmplitude(IFftBackend backend)
    {
        const int n = 1024;
        const int bin = 25;
        const double amplitudeV = 1.0;

        var samples = TestSignals.CoherentTone(n, (amplitudeV, bin));
        var spectrum = new Complex[n / 2 + 1];

        backend.Forward(samples, spectrum);

        // Rectangular-window DFT of a coherent cosine: |X_k| == amplitude * N / 2.
        Assert.Equal(amplitudeV * n / 2.0, spectrum[bin].Magnitude, 6);

        for (int k = 0; k < spectrum.Length; k++)
        {
            if (k == bin)
                continue;

            Assert.True(spectrum[k].Magnitude < 1e-6, $"unexpected leakage at bin {k}: {spectrum[k].Magnitude}");
        }
    }

    [Fact]
    public void BothBackends_AgreeOnCoherentTone()
    {
        const int n = 4096;
        const int bin = 137;
        var samples = TestSignals.CoherentTone(n, (0.73, bin));

        var specA = new Complex[n / 2 + 1];
        var specB = new Complex[n / 2 + 1];
        new FftSharpBackend().Forward(samples, specA);
        new MathNetBackend().Forward(samples, specB);

        for (int k = 0; k < specA.Length; k++)
            Assert.Equal(specA[k].Magnitude, specB[k].Magnitude, 6);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Forward_ReusingBackendAcrossDifferentLengths_ProducesCorrectResults(IFftBackend backend)
    {
        foreach (int n in new[] { 64, 256, 64, 1024 })
        {
            var samples = TestSignals.CoherentTone(n, (1.0, n / 8));
            var spectrum = new Complex[n / 2 + 1];

            backend.Forward(samples, spectrum);

            Assert.Equal(n / 2.0, spectrum[n / 8].Magnitude, 6);
        }
    }
}
