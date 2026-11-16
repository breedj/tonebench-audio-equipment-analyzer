namespace Tonebench.Dsp;

/// <summary>
/// Choosing a trigger level from the signal rather than from the user: the "50 %" button, and the
/// noise-reject band that goes with it.
/// </summary>
public static class TriggerAutoLevel
{
    /// <summary>
    /// The default hysteresis, as a fraction of the record's peak-to-peak. Small enough to reject
    /// the noise on an ordinary bench signal and far short of the amplitude, so an auto-levelled
    /// trigger still arms on anything that genuinely crosses.
    /// </summary>
    public const double DefaultHysteresisFraction = 0.02;

    /// <summary>
    /// Half way between the extremes of the record -- the level a scope's 50 % button picks.
    ///
    /// <para>Deliberately the midpoint of max and min rather than the <i>mean</i>. On an
    /// asymmetric waveform, a rectified one or anything with a duty cycle away from half, the mean
    /// sits nowhere near the middle of the swing and a trigger placed there fires on one edge of
    /// each cycle and not the other; the midpoint crosses both.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The record is empty.</exception>
    public static double FiftyPercent(ReadOnlySpan<double> samples)
    {
        var levels = WaveformMeasure.Measure(samples);
        return (levels.MaxV + levels.MinV) / 2.0;
    }

    /// <summary>
    /// A hysteresis band sized to the record: <paramref name="fraction"/> of its peak-to-peak.
    /// Zero for a record with no swing at all, which is the one case where any band would suppress
    /// every crossing there could ever be.
    /// </summary>
    /// <exception cref="ArgumentException">The record is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The fraction is negative or not finite.</exception>
    public static double HysteresisFor(ReadOnlySpan<double> samples, double fraction = DefaultHysteresisFraction)
    {
        if (!(fraction >= 0.0) || !double.IsFinite(fraction))
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "The fraction must be zero or positive and finite.");

        var levels = WaveformMeasure.Measure(samples);
        return levels.PeakToPeakV > 0.0 ? levels.PeakToPeakV * fraction : 0.0;
    }
}
