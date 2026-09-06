using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class LoopbackReferenceTests
{
    private const int FftSize = 16384;
    private const double SampleRateHz = 48000.0;
    private const double StartHz = 20.0;
    private const double EndHz = 20000.0;

    private static double BinHz => SampleRateHz / FftSize;

    /// <summary>
    /// The reference has to describe the analyzer's <i>response</i> and not its <i>delay</i>, or
    /// every corrected measurement inherits a linear phase ramp that is nothing to do with the DUT.
    /// A calibration whose impulse is a single sample must therefore transform to unity at every
    /// bin, phase included — not merely to a flat magnitude.
    /// </summary>
    [Fact]
    public void SpectrumFor_OfALoneImpulse_IsUnityAtEveryBin()
    {
        var ir = new double[2048];
        ir[600] = 1.0;
        var reference = new LoopbackReference(SampleRateHz, ir, 600, 0.0);

        var spectrum = reference.SpectrumFor(FftSize, new MathNetBackend());

        foreach (var bin in spectrum)
        {
            Assert.Equal(1.0, bin.Real, 12);
            Assert.Equal(0.0, bin.Imaginary, 12);
        }
    }

    /// <summary>
    /// The stored impulse response keeps samples on both sides of its peak, because an anti-alias
    /// filter rings before its impulse as well as after. Those pre-samples describe negative time,
    /// so they have to land at the far end of the transform buffer — laying them down linearly would
    /// read the filter's pre-ringing as post-ringing and get its phase exactly wrong.
    /// </summary>
    [Fact]
    public void SpectrumFor_WrapsThePreImpulseSamplesToNegativeTime()
    {
        var ir = new double[16];
        ir[8] = 1.0;
        ir[7] = 0.5;
        var reference = new LoopbackReference(SampleRateHz, ir, 8, 0.0);

        var spectrum = reference.SpectrumFor(64, new MathNetBackend());
        var recovered = new double[64];
        new MathNetBackend().Inverse(spectrum, recovered);

        Assert.Equal(1.0, recovered[0], 12);
        Assert.Equal(0.5, recovered[63], 12);
    }

    [Fact]
    public void EnsureUsableAt_RefusesACalibrationTakenAtAnotherSampleRate()
    {
        var reference = new LoopbackReference(48000.0, new double[16], 8, 0.0);

        reference.EnsureUsableAt(48000.0);
        Assert.Throws<InvalidOperationException>(() => reference.EnsureUsableAt(192000.0));
    }

    /// <summary>
    /// A capture whose impulse landed near index 0 has half its ringing at the far end of the
    /// circular record. Read linearly, that half is replaced with whatever the record's tail holds.
    /// </summary>
    [Fact]
    public void Capture_ReadsTheRecordCircularly()
    {
        var gated = new double[FftSize];
        gated[0] = 1.0;
        gated[FftSize - 1] = 0.25;
        var impulse = new ImpulseResponseResult(gated, gated, 0, 0.0);

        var reference = LoopbackReference.Capture(impulse, SampleRateHz, preSamples: 4, postSamples: 4);

        Assert.Equal(1.0, reference.Ir[reference.PeakOffset], 12);
        Assert.Equal(0.25, reference.Ir[reference.PeakOffset - 1], 12);
    }

    /// <summary>
    /// The whole point of the feature, end to end and without a device. A stand-in analyzer chain
    /// rolls off at 3 kHz; the DUT rolls off at 500 Hz. Measured raw, the result is the product of
    /// the two and reads 3 dB down in the wrong place. Corrected against a loopback taken through
    /// the chain alone, it must be the DUT and only the DUT.
    ///
    /// <para>The correction is applied by multiplying <c>H_ref</c> into the <b>reference</b>
    /// spectrum rather than dividing it out of the response afterwards. That ordering is what lets
    /// the estimator's existing regulariser bound the near-zero bins of the corrected reference; a
    /// division after the fact would need a second regulariser of its own.</para>
    /// </summary>
    [Fact]
    public void CorrectingAgainstALoopback_LeavesTheDutAndNotTheAnalyzerChain()
    {
        const double chainCornerHz = 3000.0;
        const double dutCornerHz = 500.0;

        var stimulus = Chirp();
        var fft = new MathNetBackend();

        var reference = LoopbackReference.Capture(
            Deconvolve(stimulus, OnePoleLowPass(stimulus, chainCornerHz)).Impulse, SampleRateHz);

        var throughBoth = OnePoleLowPass(OnePoleLowPass(stimulus, dutCornerHz), chainCornerHz);

        var uncorrected = Deconvolve(stimulus, throughBoth).Response;

        var href = reference.SpectrumFor(FftSize, fft);
        var correctedReference = Spectrum(stimulus);
        for (int k = 0; k < correctedReference.Length; k++)
            correctedReference[k] *= href[k];

        var estimator = new FrequencyResponseEstimator(
            FftSize, fft, ImpulseGate.ForSweep(FftSize, StartHz, EndHz));
        estimator.Add(correctedReference, Spectrum(throughBoth));
        var corrected = estimator.Complete();

        bool sawTheChain = false;
        foreach (int bin in BandBins())
        {
            double freqHz = bin * BinHz;
            double dutOnlyDb = FrequencyResponseEstimator.ToDb(OnePoleMagnitude(freqHz, dutCornerHz));
            double bothDb = dutOnlyDb + FrequencyResponseEstimator.ToDb(OnePoleMagnitude(freqHz, chainCornerHz));

            Assert.Equal(dutOnlyDb, FrequencyResponseEstimator.ToDb(corrected.Magnitude[bin]), 0.05);

            if (Math.Abs(FrequencyResponseEstimator.ToDb(uncorrected.Magnitude[bin]) - bothDb) < 0.05
                && Math.Abs(bothDb - dutOnlyDb) > 1.0)
            {
                sawTheChain = true;
            }
        }

        // Guards the guard: if the stand-in chain did nothing measurable, the test above would pass
        // against a correction that also did nothing.
        Assert.True(sawTheChain, "the analyzer chain never moved the uncorrected trace, so nothing was proved");
    }

    private static (FrequencyResponseResult Response, ImpulseResponseResult Impulse) Deconvolve(
        double[] stimulus, double[] response)
    {
        var estimator = new FrequencyResponseEstimator(
            FftSize, new MathNetBackend(), ImpulseGate.ForSweep(FftSize, StartHz, EndHz));
        estimator.Add(Spectrum(stimulus), Spectrum(response));
        return (estimator.Complete(), estimator.CompleteImpulse());
    }

    private static double[] Chirp()
    {
        var period = new double[FftSize];
        ExponentialChirp.Fill(period, StartHz, EndHz, SampleRateHz, 1.0);
        return period;
    }

    private static IEnumerable<int> BandBins()
    {
        int first = (int)Math.Ceiling(StartHz * 2.0 / BinHz);
        int last = (int)Math.Floor(EndHz / 2.0 / BinHz);
        for (int bin = first; bin <= last; bin += 37)
            yield return bin;
    }

    private static Complex[] Spectrum(double[] samples)
    {
        var spectrum = new Complex[(FftSize / 2) + 1];
        new MathNetBackend().Forward(samples, spectrum);
        return spectrum;
    }

    /// <summary>Run to circular steady state — several periods in, the last one out.</summary>
    private static double[] OnePoleLowPass(double[] period, double cornerHz)
    {
        double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cornerHz / SampleRateHz);
        double state = 0.0;

        var output = new double[period.Length];
        for (int pass = 0; pass < 4; pass++)
            for (int i = 0; i < period.Length; i++)
            {
                state += alpha * (period[i] - state);
                output[i] = state;
            }

        return output;
    }

    private static double OnePoleMagnitude(double freqHz, double cornerHz)
    {
        double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cornerHz / SampleRateHz);
        double omega = 2.0 * Math.PI * freqHz / SampleRateHz;
        var denominator = 1.0 - ((1.0 - alpha) * Complex.FromPolarCoordinates(1.0, -omega));
        return alpha / denominator.Magnitude;
    }
}
