using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp;

/// <summary>
/// A stored loopback calibration: the analyzer's own impulse response — its DAC, its ADC, its
/// anti-alias filters and whatever cable was patched output to input — kept so that a later
/// measurement can divide it out and report the DUT alone.
///
/// <para><b>Stored as a short impulse response, not as a spectrum.</b> A loopback IR is compact:
/// filter ringing either side of one impulse and nothing else. Zero-padding it re-derives
/// <c>H_ref</c> exactly on <i>any</i> transform length, so one calibration serves every resolution
/// setting. A stored spectrum would be welded to the FFT size and the chirp bounds it was taken
/// at.</para>
///
/// <para><b>The bulk delay is taken out and recorded separately.</b> <see cref="SpectrumFor"/> lays
/// the impulse down at index 0, so <c>H_ref</c> carries the analyzer's <i>response</i> without its
/// round-trip <i>delay</i>. Leaving the delay in would add a linear phase ramp to every corrected
/// measurement and make the reported latency figure wrong in a new and confusing way.</para>
///
/// <para><b>Sample rate is a hard key.</b> A calibration taken at another rate describes different
/// filters; resampling an impulse response is a separate problem and is deliberately not attempted
/// here. See <see cref="EnsureUsableAt"/>.</para>
/// </summary>
/// <param name="SampleRateHz">The rate this was captured at. Must match the run that uses it.</param>
/// <param name="Ir">The impulse response, <see cref="PeakOffset"/> samples in.</param>
/// <param name="PeakOffset">
/// Where the impulse sits inside <paramref name="Ir"/>. Non-zero on purpose: an anti-alias filter
/// rings <i>before</i> its impulse as well as after, and storing from the peak onwards would throw
/// that half away — which reads afterwards as a high-frequency error in every corrected measurement.
/// </param>
/// <param name="RemovedDelaySamples">
/// The rotation the estimator removed when this was captured, i.e. the analyzer's own round-trip
/// delay. Informational — it is not reapplied anywhere.
/// </param>
public sealed record LoopbackReference(
    double SampleRateHz,
    double[] Ir,
    int PeakOffset,
    double RemovedDelaySamples)
{
    /// <summary>Samples kept before the impulse by <see cref="Capture"/>.</summary>
    public const int DefaultPreSamples = 1024;

    /// <summary>Samples kept after it. The two together are the stored length.</summary>
    public const int DefaultPostSamples = 7168;

    /// <summary>
    /// Takes a calibration out of a finished loopback deconvolution, circularly, so an impulse that
    /// landed near index 0 keeps the ringing that wrapped to the far end of the record.
    /// </summary>
    public static LoopbackReference Capture(
        ImpulseResponseResult impulse,
        double sampleRateHz,
        int preSamples = DefaultPreSamples,
        int postSamples = DefaultPostSamples)
    {
        ArgumentNullException.ThrowIfNull(impulse);
        ArgumentOutOfRangeException.ThrowIfNegative(preSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(postSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        var source = impulse.Gated;
        if (preSamples + postSamples + 1 > source.Length)
            throw new ArgumentOutOfRangeException(nameof(postSamples), "A calibration cannot be longer than the record it came from.");

        var ir = new double[preSamples + postSamples + 1];
        for (int j = 0; j < ir.Length; j++)
        {
            int index = impulse.PeakIndex - preSamples + j;
            ir[j] = source[((index % source.Length) + source.Length) % source.Length];
        }

        return new LoopbackReference(sampleRateHz, ir, preSamples, impulse.AppliedRotationSamples);
    }

    /// <summary>
    /// <c>H_ref</c> on an <paramref name="fftSize"/>-point grid: the stored impulse response laid
    /// down with its peak at index 0 — circularly, so the pre-impulse ringing wraps to the end where
    /// a negative time belongs — and transformed. One-sided, <c>fftSize / 2 + 1</c> bins, matching
    /// what <see cref="FrequencyResponseEstimator.Add"/> expects.
    /// </summary>
    public Complex[] SpectrumFor(int fftSize, IFftBackend fft)
    {
        ArgumentNullException.ThrowIfNull(fft);
        if (fftSize < 4 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two of at least 4.", nameof(fftSize));
        if (Ir.Length > fftSize)
            throw new ArgumentOutOfRangeException(nameof(fftSize), "The calibration is longer than the transform it is being placed in.");

        var padded = new double[fftSize];
        for (int j = 0; j < Ir.Length; j++)
            padded[(((j - PeakOffset) % fftSize) + fftSize) % fftSize] = Ir[j];

        var spectrum = new Complex[(fftSize / 2) + 1];
        fft.Forward(padded, spectrum);
        return spectrum;
    }

    /// <summary>
    /// Throws unless this calibration describes the same analyzer configuration as the run about to
    /// use it. Refusing here beats correcting with the wrong filters, which produces a plausible
    /// trace nobody would question.
    /// </summary>
    public void EnsureUsableAt(double sampleRateHz)
    {
        if (Math.Abs(SampleRateHz - sampleRateHz) > 0.5)
        {
            throw new InvalidOperationException(
                $"This loopback reference was taken at {SampleRateHz:0} Hz and cannot be used at {sampleRateHz:0} Hz.");
        }
    }
}
