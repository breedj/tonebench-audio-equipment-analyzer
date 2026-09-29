using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class FftBackendTests
{
    public static IEnumerable<object[]> Backends()
    {
        yield return [new FftSharpBackend()];
        yield return [new MathNetBackend()];
        yield return [new FftFlatBackend()];
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

    /// <summary>
    /// Every backend against the one the app actually runs (<see cref="FftFlatBackend"/>), bin for
    /// bin and complex rather than by magnitude -- a backend whose imaginary sign is flipped agrees
    /// on every magnitude and is wrong about every phase, which is the whole of the frequency
    /// response, impedance and impulse measurements.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void EveryBackend_AgreesWithTheOneInProduction_OnACoherentTone(IFftBackend backend)
    {
        const int n = 4096;
        const int bin = 137;
        var samples = TestSignals.CoherentTone(n, (0.73, bin));

        var expected = new Complex[n / 2 + 1];
        var actual = new Complex[n / 2 + 1];
        new FftFlatBackend().Forward(samples, expected);
        backend.Forward(samples, actual);

        for (int k = 0; k < expected.Length; k++)
        {
            Assert.Equal(expected[k].Real, actual[k].Real, 6);
            Assert.Equal(expected[k].Imaginary, actual[k].Imaginary, 6);
        }
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

    [Theory]
    [MemberData(nameof(Backends))]
    public void Inverse_RoundTripsForward(IFftBackend backend)
    {
        const int n = 256;
        var samples = new double[n];
        var random = new Random(1234);
        for (int i = 0; i < n; i++)
            samples[i] = (random.NextDouble() * 2.0) - 1.0;

        var spectrum = new Complex[(n / 2) + 1];
        var restored = new double[n];

        backend.Forward(samples, spectrum);
        backend.Inverse(spectrum, restored);

        for (int i = 0; i < n; i++)
            Assert.Equal(samples[i], restored[i], 9);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Inverse_OfADelayedImpulseSpectrum_PutsThePeakAtTheDelay(IFftBackend backend)
    {
        // The property the frequency-response latency estimator depends on: a linear phase ramp in
        // the spectrum comes back as a single sample at that offset, not smeared across the record.
        const int n = 128;
        const int delay = 37;

        var spectrum = new Complex[(n / 2) + 1];
        for (int k = 0; k < spectrum.Length; k++)
            spectrum[k] = Complex.FromPolarCoordinates(1.0, -2.0 * Math.PI * k * delay / n);

        var impulse = new double[n];
        backend.Inverse(spectrum, impulse);

        int peak = 0;
        for (int i = 0; i < n; i++)
            if (Math.Abs(impulse[i]) > Math.Abs(impulse[peak]))
                peak = i;

        Assert.Equal(delay, peak);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Inverse_RejectsWrongSpecInLength(IFftBackend backend)
    {
        var spectrum = new Complex[64]; // should be 33
        var timeOut = new double[64];
        Assert.Throws<ArgumentException>(() => backend.Inverse(spectrum, timeOut));
    }
}
