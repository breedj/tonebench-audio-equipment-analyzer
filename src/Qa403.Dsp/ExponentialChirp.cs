namespace Qa403.Dsp;

/// <summary>
/// The exponential ("Farina") sine sweep used as the frequency-response stimulus, generated as one
/// exact period.
///
/// <para><b>Why a period at all.</b> The QA403 streams continuously and has no capture trigger, so
/// an analyzer record starts at an arbitrary point in the stimulus. Rather than build synchronous
/// start machinery, the chirp is made exactly periodic with a period of <c>destination.Length</c>
/// frames and the analysis FFT is that same length. Any record then holds exactly one whole period,
/// circularly shifted by an unknown amount — and a circular shift does not change a magnitude
/// spectrum at all. So <c>|Y_k / X_k|</c> is correct no matter where the record landed, and only
/// phase carries the shift (which <see cref="FrequencyResponseEstimator"/> removes with the same
/// step that removes genuine DUT latency).</para>
///
/// <para>Because the record holds a whole number of periods, the correct analysis window is
/// <see cref="WindowType.Rectangular"/>: the DFT of a periodic signal over exactly one period is
/// exact, with no leakage and no window correction to apply. Applying a taper here would be
/// actively wrong — it would break the periodicity the whole scheme rests on.</para>
///
/// <para><b>The fade is part of the waveform, not a repair.</b> The sweep ends at a different phase
/// than it starts, so the wrap point is a step discontinuity that would splatter across the whole
/// spectrum. The raised-cosine fade at each end removes it. It is deliberately <i>not</i> corrected
/// for downstream: the reference spectrum is computed from this same function, so whatever the fade
/// does to the stimulus is divided out exactly. What it does cost is stimulus energy at the very
/// bottom and top of the sweep, which is what the coherence figure is there to expose.</para>
///
/// <para>This is the canonical copy. <c>Qa403.Device.Hardware.Qa40xGeneratorWaveform</c> and
/// <c>Qa403.Device.Simulation.SignalSynthesizer</c> hold their own re-derivations (that project has
/// no reference to this one, by design), and
/// <c>Qa403.App.Tests.Services.ChirpFormulaAgreementTests</c> pins all of them together. If they
/// ever drift, the deconvolution silently reports the drift as DUT response — so that test is not
/// optional.</para>
/// </summary>
public static class ExponentialChirp
{
    /// <summary>
    /// Raised-cosine fade at each end of the period, in seconds. Matches the generator's own
    /// <c>EchoChirpFadeSeconds</c>.
    /// </summary>
    public const double DefaultFadeSeconds = 0.005;

    /// <summary>
    /// Writes exactly one period of the sweep into <paramref name="destination"/>, whose length is
    /// the period in frames.
    /// </summary>
    /// <param name="destination">Receives one period; its length is the period, and must be positive.</param>
    /// <param name="startFreqHz">Sweep start, must be positive.</param>
    /// <param name="endFreqHz">Sweep end, must exceed <paramref name="startFreqHz"/>.</param>
    /// <param name="sampleRateHz">Frames per second, must be positive.</param>
    /// <param name="amplitudeVolts">Peak amplitude, i.e. the sine's peak and not its RMS.</param>
    /// <param name="fadeSeconds">Raised-cosine fade at each end of the period.</param>
    public static void Fill(
        Span<double> destination,
        double startFreqHz,
        double endFreqHz,
        double sampleRateHz,
        double amplitudeVolts,
        double fadeSeconds = DefaultFadeSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destination.Length, nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startFreqHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegative(fadeSeconds);
        if (endFreqHz <= startFreqHz)
            throw new ArgumentOutOfRangeException(nameof(endFreqHz), "End frequency must exceed the start frequency.");

        double periodSeconds = destination.Length / sampleRateHz;

        for (int n = 0; n < destination.Length; n++)
        {
            double tau = n / sampleRateHz;
            destination[n] = amplitudeVolts * Math.Sin(PhaseAt(tau, startFreqHz, endFreqHz, periodSeconds))
                * Fade(tau, periodSeconds, fadeSeconds);
        }
    }

    /// <summary>
    /// Instantaneous phase, radians, at <paramref name="tau"/> seconds into the period.
    ///
    /// <para>Integrating an instantaneous frequency that rises geometrically,
    /// <c>f(τ) = f₁·e^{kτ/T}</c> with <c>k = ln(f₂/f₁)</c>, gives
    /// <c>φ(τ) = 2π·f₁·T/k·(e^{kτ/T} − 1)</c> — so the sweep spends equal time per octave and its
    /// energy density falls at 3 dB/octave.</para>
    /// </summary>
    public static double PhaseAt(double tau, double startFreqHz, double endFreqHz, double periodSeconds)
    {
        double k = Math.Log(endFreqHz / startFreqHz);
        return 2.0 * Math.PI * startFreqHz * periodSeconds / k * (Math.Exp(k * tau / periodSeconds) - 1.0);
    }

    /// <summary>
    /// Raised-cosine taper, 0 at each end of the period rising to 1 <paramref name="fadeSeconds"/>
    /// in. A zero or over-long fade degrades gracefully rather than throwing, so a caller that picks
    /// a short period cannot produce a silent waveform.
    /// </summary>
    public static double Fade(double tau, double periodSeconds, double fadeSeconds)
    {
        if (fadeSeconds <= 0.0)
            return 1.0;

        double fromEdge = Math.Min(tau, periodSeconds - tau);
        if (fromEdge >= fadeSeconds)
            return 1.0;

        return 0.5 * (1.0 - Math.Cos(Math.PI * fromEdge / fadeSeconds));
    }

    /// <summary>Peak volts for a level in dBV (dB relative to 1 Vrms), the unit the generator config uses.</summary>
    public static double PeakVoltsFromDbv(double dbv) => Math.Sqrt(2.0) * Math.Pow(10.0, dbv / 20.0);
}
