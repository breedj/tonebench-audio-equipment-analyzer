namespace Tonebench.Dsp;

/// <summary>
/// Fires when the signal <i>enters</i> or <i>leaves</i> a voltage band: the trigger for "tell me
/// when this stops sitting where it should".
///
/// <para><b>What it is for.</b> An edge trigger asks about one level, so it cannot express a
/// tolerance. A window can: set the band to the rail a bias point is supposed to sit in and trigger
/// on <i>leaving</i> it, and the capture is of the moment it misbehaved, with the whole record
/// before that moment sitting in the pre-trigger. The mirror -- trigger on <i>entering</i> -- is how
/// you catch a signal arriving, a mute releasing, or a protection circuit letting go.</para>
///
/// <para><b>The two levels are sorted rather than ordered by name.</b> <c>LevelV</c> and
/// <c>LevelLowV</c> are a pair of bounds, and a user who types them the other way round means the
/// same band. Refusing that would be a refusal about which box a number went in rather than about
/// the measurement.</para>
///
/// <para><b>A signal that jumps clean through the band in one sample is not an entry.</b> Each
/// sample is classified in or out and only a <i>transition</i> fires, so a step from below the band
/// to above it is outside-to-outside: nothing entered, and nothing is reported. That is the honest
/// answer -- at this sample rate a band narrower than one sample's worth of slew really cannot be
/// observed to have been entered -- and it is why a band set absurdly tight simply never fires
/// rather than firing on every edge.</para>
/// </summary>
public sealed class WindowTrigger : IScanTrigger
{
    private readonly TriggerSpec _spec;
    private readonly double _low;
    private readonly double _high;
    private readonly double _outerHysteresis;
    private readonly double _innerHysteresis;

    private bool _hasPrev;
    private double _prev;
    private long _prevIndex;
    private bool _wasInside;

    /// <summary>True once the signal has sat far enough outside the band for an entry to count.</summary>
    private bool _armedEnter;

    /// <summary>True once it has sat far enough inside for an exit to count.</summary>
    private bool _armedExit;

    private long _holdoffUntil;

    /// <param name="spec">
    /// <c>LevelV</c> and <c>LevelLowV</c> are the band's two bounds in either order;
    /// <see cref="TriggerSlope.Rising"/> fires on entering it, <see cref="TriggerSlope.Falling"/> on
    /// leaving, and <see cref="TriggerSlope.Either"/> on both.
    /// </param>
    public WindowTrigger(TriggerSpec spec)
    {
        if (!double.IsFinite(spec.LevelV))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.LevelV, "The trigger level must be finite.");
        if (!double.IsFinite(spec.LevelLowV))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.LevelLowV, "The second trigger level must be finite.");
        if (!(spec.HysteresisV >= 0.0) || !double.IsFinite(spec.HysteresisV))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.HysteresisV, "The hysteresis must be zero or positive and finite.");
        if (spec.HoldoffFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.HoldoffFrames, "The holdoff cannot be negative.");
        if (spec.LevelV == spec.LevelLowV)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spec), spec.LevelV, "The window's two levels must differ, or there is no band to be inside.");
        }

        _spec = spec;
        _low = Math.Min(spec.LevelV, spec.LevelLowV);
        _high = Math.Max(spec.LevelV, spec.LevelLowV);
        _outerHysteresis = spec.HysteresisV;

        // Inside the band the hysteresis is clamped to just under half its width. Unclamped, a
        // hysteresis wider than the band leaves "inside by the hysteresis" unreachable, and the exit
        // arm would never be set: the trigger would be perfectly configured and silently never fire,
        // which is the worst way for a control to be wrong. Clamped, a hysteresis that wide simply
        // stops contributing, and the band itself still decides.
        _innerHysteresis = Math.Min(spec.HysteresisV, (_high - _low) * 0.499);
    }

    public void Reset(bool keepHoldoff = false)
    {
        _hasPrev = false;
        _prev = 0.0;
        _prevIndex = 0;
        _wasInside = false;
        _armedEnter = false;
        _armedExit = false;
        if (!keepHoldoff)
            _holdoffUntil = 0;
    }

    public bool Scan(ReadOnlySpan<double> samples, long firstAbsoluteIndex, out TriggerHit hit)
    {
        bool wantEnter = _spec.Slope is TriggerSlope.Rising or TriggerSlope.Either;
        bool wantExit = _spec.Slope is TriggerSlope.Falling or TriggerSlope.Either;

        for (int i = 0; i < samples.Length; i++)
        {
            long index = firstAbsoluteIndex + i;
            double value = samples[i];
            bool inside = value >= _low && value <= _high;

            // Before this sample is allowed to arm anything, for EdgeTrigger's reason: a sample that
            // both arms and fires turns the hysteresis into no hysteresis.
            if (_hasPrev && index >= _holdoffUntil)
            {
                bool entering = wantEnter && _armedEnter && !_wasInside && inside;
                bool leaving = wantExit && _armedExit && _wasInside && !inside;

                if (entering || leaving)
                {
                    // Whichever bound was actually crossed -- an entry from below crosses the low
                    // one, an entry from above the high one -- so the trigger point is on the edge
                    // of the band the signal went through rather than on a nominal level.
                    double threshold = (entering ? _prev < _low : value < _low) ? _low : _high;

                    hit = new TriggerHit(_prevIndex, EdgeTrigger.CrossingFraction(_prev, value, threshold));
                    _holdoffUntil = index + _spec.HoldoffFrames;
                    if (entering)
                        _armedEnter = false;
                    if (leaving)
                        _armedExit = false;

                    _prev = value;
                    _prevIndex = index;
                    _wasInside = inside;
                    _hasPrev = true;
                    return true;
                }
            }

            if (value < _low - _outerHysteresis || value > _high + _outerHysteresis)
                _armedEnter = true;
            if (value >= _low + _innerHysteresis && value <= _high - _innerHysteresis)
                _armedExit = true;

            _prev = value;
            _prevIndex = index;
            _wasInside = inside;
            _hasPrev = true;
        }

        hit = default;
        return false;
    }
}
