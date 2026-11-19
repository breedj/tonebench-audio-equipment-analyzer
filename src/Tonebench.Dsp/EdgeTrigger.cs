namespace Tonebench.Dsp;

/// <summary>
/// The ordinary edge trigger: arm on the near side of the level, fire on the crossing, then hold
/// off. Resumable across blocks -- see <see cref="IScanTrigger"/> for why that is the point rather
/// than a detail.
///
/// <para><b>Arming is what hysteresis buys.</b> A rising trigger arms only once the signal has been
/// at or below <c>level - hysteresis</c>, and fires on the first sample at or above <c>level</c>;
/// falling is the mirror. With the hysteresis at zero this is a plain crossing test, which is what
/// the FFT screen's own zero-crossing search does. With it non-zero, a signal sitting on the level
/// no longer fires on every wiggle of its own noise -- which on a quiet DUT is the difference
/// between a stable trace and one that will not stand still.</para>
///
/// <para><b>The fraction is not a refinement.</b> One sample is 5.2 us at 192 kS/s, so a trace
/// aligned on the integer index alone jitters by up to that much from capture to capture and a
/// waveform near the top of the band visibly walks sideways. The crossing is linearly interpolated
/// between the two samples that straddle the level, which is exact for a straight segment and good
/// to well under a sample for anything band-limited enough to be on this grid at all.</para>
/// </summary>
public sealed class EdgeTrigger : IScanTrigger
{
    private readonly TriggerSpec _spec;

    private bool _hasPrev;
    private double _prev;
    private long _prevIndex;

    private bool _armedRising;
    private bool _armedFalling;

    /// <summary>Absolute index before which a hit is suppressed; 0 means nothing is owed.</summary>
    private long _holdoffUntil;

    public EdgeTrigger(TriggerSpec spec)
    {
        if (!double.IsFinite(spec.LevelV))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.LevelV, "The trigger level must be finite.");
        if (!(spec.HysteresisV >= 0.0) || !double.IsFinite(spec.HysteresisV))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.HysteresisV, "The hysteresis must be zero or positive and finite.");
        if (spec.HoldoffFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.HoldoffFrames, "The holdoff cannot be negative.");

        _spec = spec;
    }

    public void Reset(bool keepHoldoff = false)
    {
        _hasPrev = false;
        _prev = 0.0;
        _prevIndex = 0;
        _armedRising = false;
        _armedFalling = false;
        if (!keepHoldoff)
            _holdoffUntil = 0;
    }

    public bool Scan(ReadOnlySpan<double> samples, long firstAbsoluteIndex, out TriggerHit hit)
    {
        double level = _spec.LevelV;
        double hysteresis = _spec.HysteresisV;
        bool wantRising = _spec.Slope is TriggerSlope.Rising or TriggerSlope.Either;
        bool wantFalling = _spec.Slope is TriggerSlope.Falling or TriggerSlope.Either;

        for (int i = 0; i < samples.Length; i++)
        {
            long index = firstAbsoluteIndex + i;
            double value = samples[i];

            // The crossing test reads the arm state as it stood after the PREVIOUS sample, so it is
            // taken before this sample is allowed to arm anything. Swapping these two blocks lets a
            // single sample arm and fire at once, which turns the hysteresis band into no band.
            if (_hasPrev && index >= _holdoffUntil)
            {
                bool rising = wantRising && _armedRising && _prev < level && value >= level;
                bool falling = wantFalling && _armedFalling && _prev > level && value <= level;

                if (rising || falling)
                {
                    hit = new TriggerHit(_prevIndex, CrossingFraction(_prev, value, level));
                    _holdoffUntil = index + _spec.HoldoffFrames;
                    if (rising)
                        _armedRising = false;
                    if (falling)
                        _armedFalling = false;

                    // The sample that fired still becomes the previous one, so a resumed scan over
                    // the rest of this block does not re-examine the same pair.
                    _prev = value;
                    _prevIndex = index;
                    _hasPrev = true;
                    return true;
                }
            }

            if (value <= level - hysteresis)
                _armedRising = true;
            if (value >= level + hysteresis)
                _armedFalling = true;

            _prev = value;
            _prevIndex = index;
            _hasPrev = true;
        }

        hit = default;
        return false;
    }

    /// <summary>
    /// Where between two straddling samples the level is crossed, in [0, 1] from
    /// <paramref name="before"/> towards <paramref name="after"/>.
    ///
    /// <para>Two samples of equal value cannot be interpolated between, and that is reachable: a
    /// signal that sits exactly on the level, or a flat-topped clipped waveform whose top is the
    /// level. It answers 0 -- the near sample -- rather than dividing by zero.</para>
    /// </summary>
    public static double CrossingFraction(double before, double after, double level)
    {
        double span = after - before;
        if (span == 0.0 || !double.IsFinite(span))
            return 0.0;

        return Math.Clamp((level - before) / span, 0.0, 1.0);
    }
}
