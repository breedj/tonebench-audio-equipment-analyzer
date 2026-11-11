using System.Numerics;

namespace Tonebench.Dsp;

/// <summary>
/// Takes the delay a second clock puts on a deconvolved chirp back off again.
///
/// <para><b>What the clock does.</b> Two devices are two crystals, so the capture is a time-scaled
/// copy of what was played: sampling <c>δ</c> faster stretches the waveform over more samples, and
/// read back at the nominal rate it is <c>y(τ) = x(τ(1+ε))</c> with <c>ε ≈ −δ</c> — which is exactly
/// the quantity <see cref="ToneFrequency.DriftPpm"/> measures, since a played tone comes home at
/// <c>f(1+ε)</c>.</para>
///
/// <para><b>Why that is a delay ramp and not a delay.</b> An exponential chirp maps frequency to
/// time: the sweep reaches <c>f</c> at <c>t(f) = T·ln(f/f₁)/ln(f₂/f₁)</c>. Scaling time moves that
/// arrival to <c>t(f)/(1+ε)</c>, so against an unscaled reference every frequency is early (or late)
/// by <c>ε·t(f)</c> — nothing at the bottom of the sweep, the full <c>ε·T</c> at the top. That is a
/// delay that grows with frequency, which is precisely what group delay reports, and it is why the
/// error reads as a plausible DUT rather than as a fault.</para>
///
/// <para><b>Measured, on a MOTU M4 playing into a Sound BlasterX G6 at ε = −104 ppm:</b> 71 µs of
/// slip across a 0.683 s record, 511° of phase at 20 kHz, and a gated impulse smeared from 1 sample
/// half-width to 3 — against a one-clock control that repeated to 0.02° and 0.0012 dB. The
/// correction is worth as much as ε is known: measured twice five hours apart by two different
/// estimators, this bench's figure agreed to 0.33 ppm, so a ~100× reduction is available.</para>
///
/// <para><b>What it cannot do.</b> It inverts a <i>constant</i> ratio and nothing else — not jitter,
/// not a clock still settling (a QA403 re-locks over 15–30 s after a rate change), and not a signal
/// below the capture device's own floor. It is also specific to a chirp: the correction is built on
/// that time–frequency map, and a periodic-noise stimulus has none, which is the same reason noise
/// fails differently (its leakage goes incoherent and coherence falls, where the chirp quietly
/// reports a delay).</para>
/// </summary>
public sealed class ChirpClockDrift
{
    private readonly double _epsilon;
    private readonly double _periodSeconds;
    private readonly double _startFreqHz;
    private readonly double _logSpan;

    private ChirpClockDrift(double epsilon, double periodSeconds, double startFreqHz, double logSpan)
    {
        _epsilon = epsilon;
        _periodSeconds = periodSeconds;
        _startFreqHz = startFreqHz;
        _logSpan = logSpan;
    }

    /// <summary>Below this a correction is not worth making and is not made.</summary>
    public const double NegligiblePpm = 0.05;

    /// <summary>
    /// The correction for a sweep, or null when there is nothing to correct — no drift worth the
    /// name, or a chirp whose own parameters do not describe a sweep.
    ///
    /// <para><paramref name="driftPpm"/> is <see cref="ToneFrequency.DriftPpm"/>'s sign convention:
    /// negative when the captured tone reads below the played one.</para>
    /// </summary>
    public static ChirpClockDrift? For(
        double driftPpm, double sampleRateHz, int fftSize, double startFreqHz, double endFreqHz)
    {
        if (!double.IsFinite(driftPpm) || Math.Abs(driftPpm) < NegligiblePpm) return null;
        if (sampleRateHz <= 0 || fftSize <= 0) return null;
        if (startFreqHz <= 0 || endFreqHz <= startFreqHz) return null;

        return new ChirpClockDrift(
            driftPpm * 1e-6, fftSize / sampleRateHz, startFreqHz, Math.Log(endFreqHz / startFreqHz));
    }

    /// <summary>How far into the sweep frequency <paramref name="freqHz"/> is played, in seconds.</summary>
    public double SweepTimeSeconds(double freqHz)
    {
        if (freqHz <= 0) return 0;

        // Clamped rather than extrapolated: below the sweep's start and above its end there is no
        // stimulus, so there is no arrival time either, and a linear extrapolation of a logarithm
        // would put an enormous correction on exactly the bins that carry only noise.
        double t = _periodSeconds * Math.Log(freqHz / _startFreqHz) / _logSpan;
        return Math.Clamp(t, 0.0, _periodSeconds);
    }

    /// <summary>The delay this clock ratio puts on the response at that frequency, in seconds.</summary>
    public double DelaySeconds(double freqHz) => -_epsilon * SweepTimeSeconds(freqHz);

    /// <summary>
    /// Removes the ramp from a deconvolved transfer function, in place.
    ///
    /// <para>A delay <c>τ</c> multiplies the response by <c>e^(−j2πfτ)</c>, so taking one off is a
    /// multiply by <c>e^(+j2πfτ)</c>. Magnitudes are untouched by construction — the correction is a
    /// unit-modulus factor — which is what makes it safe to apply to a measurement whose magnitude
    /// was already trustworthy.</para>
    /// </summary>
    /// <param name="h">The transfer function, bin 0 upwards.</param>
    public void Correct(Span<Complex> h)
    {
        // The bin spacing is the reciprocal of the record, and the record is the chirp's own period
        // -- that equality is the premise the whole measurement rests on, so deriving it here rather
        // than taking it as an argument removes a way for the two to disagree.
        double binHz = 1.0 / _periodSeconds;

        for (int k = 0; k < h.Length; k++)
        {
            double f = k * binHz;
            double phase = 2 * Math.PI * f * DelaySeconds(f);
            h[k] *= new Complex(Math.Cos(phase), Math.Sin(phase));
        }
    }

    /// <summary>The slip across the whole record — the figure the plan predicts and the bench measures.</summary>
    public double SlipSeconds => Math.Abs(_epsilon) * _periodSeconds;
}
