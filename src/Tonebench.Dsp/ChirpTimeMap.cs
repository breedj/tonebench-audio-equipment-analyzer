namespace Tonebench.Dsp;

/// <summary>
/// Where in a sweep each frequency is played: the exponential chirp's frequency-to-time map.
///
/// <para>Its own type because two things read it and they must read the same one.
/// <see cref="ChirpClockDrift"/> uses it to <i>remove</i> a clock's delay ramp, and
/// <see cref="ChirpDriftResidual"/> uses it as the basis function it <i>fits</i> to find what is
/// left. A second copy of <c>t(f)</c> in the estimator would make the two silently disagree about
/// the shape they are respectively taking off and looking for, and the symptom of that is the worst
/// available — a detector that reports a residual on a correctly corrected measurement, i.e. one
/// that tells the user their stored figure is stale when it is exact.</para>
///
/// <para>The map is <c>t(f) = T·ln(f/f₁)/ln(f₂/f₁)</c>, where <c>T</c> is the sweep's period. That
/// period is the analysis record, not an independent quantity: the chirp is built to be periodic
/// over exactly one record, which is the equality the whole frequency-response measurement rests
/// on.</para>
/// </summary>
public readonly struct ChirpTimeMap
{
    private readonly double _startFreqHz;
    private readonly double _logSpan;

    private ChirpTimeMap(double periodSeconds, double startFreqHz, double logSpan)
    {
        PeriodSeconds = periodSeconds;
        _startFreqHz = startFreqHz;
        _logSpan = logSpan;
    }

    /// <summary>The sweep's period, which is the analysis record's length.</summary>
    public double PeriodSeconds { get; }

    /// <summary>
    /// The map for a sweep, or null where the figures do not describe one — a non-positive period,
    /// a start at or below zero, or an end at or below the start.
    /// </summary>
    public static ChirpTimeMap? For(double periodSeconds, double startFreqHz, double endFreqHz)
    {
        if (!double.IsFinite(periodSeconds) || periodSeconds <= 0) return null;
        if (!double.IsFinite(startFreqHz) || startFreqHz <= 0) return null;
        if (!double.IsFinite(endFreqHz) || endFreqHz <= startFreqHz) return null;

        return new ChirpTimeMap(periodSeconds, startFreqHz, Math.Log(endFreqHz / startFreqHz));
    }

    /// <summary>The map for a record of <paramref name="fftSize"/> samples at that rate.</summary>
    public static ChirpTimeMap? For(double sampleRateHz, int fftSize, double startFreqHz, double endFreqHz) =>
        sampleRateHz > 0 && fftSize > 0
            ? For(fftSize / sampleRateHz, startFreqHz, endFreqHz)
            : null;

    /// <summary>How far into the sweep frequency <paramref name="freqHz"/> is played, in seconds.</summary>
    public double SweepTimeSeconds(double freqHz)
    {
        if (freqHz <= 0) return 0;

        // Clamped rather than extrapolated: below the sweep's start and above its end there is no
        // stimulus, so there is no arrival time either, and a linear extrapolation of a logarithm
        // would put an enormous correction on exactly the bins that carry only noise.
        double t = PeriodSeconds * Math.Log(freqHz / _startFreqHz) / _logSpan;
        return Math.Clamp(t, 0.0, PeriodSeconds);
    }

    /// <summary>
    /// The phase a clock ratio of ε = 1 would put on the response at this frequency, in radians —
    /// <c>2πf·t(f)</c>, the ramp's shape with its size divided out.
    ///
    /// <para>This is the one definition of that shape. The correction multiplies by
    /// <c>e^(−jε·this)</c> and the residual estimator fits <c>ε·this</c>, so a measurement corrected
    /// by exactly the right figure leaves a coefficient of zero by construction rather than by
    /// two derivations happening to agree.</para>
    /// </summary>
    public double RampRadiansPerEpsilon(double freqHz) => 2 * Math.PI * freqHz * SweepTimeSeconds(freqHz);
}
