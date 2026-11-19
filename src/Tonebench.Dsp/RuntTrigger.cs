namespace Tonebench.Dsp;

/// <summary>
/// Fires on a pulse that crosses one threshold and fails to reach the other: the trigger for an
/// edge that did not make it.
///
/// <para><b>What it is for.</b> A runt is a pulse that starts and gives up — a stage that cannot
/// quite swing to the rail on one cycle in a thousand, a supply that sags under a transient, an
/// output that clips asymmetrically only when something else is drawing current. On a scope it is
/// one short pulse among full-height ones, and neither a level nor a width can pick it out: a runt
/// is exactly as wide as a good pulse and crosses exactly the same low threshold.</para>
///
/// <para><b>Marginal at audio bandwidth, and offered anyway.</b> This kind earns its keep on logic
/// and switching waveforms, where "did the edge reach the rail" is the question. On an amplifier
/// the same fault usually reads better as distortion against level. It is here because the
/// machinery is the window trigger's and because an amber flag on one cycle in a thousand is
/// something no averaged measurement will ever show.</para>
///
/// <para><b>Rising means a pulse going up</b>: through <c>LevelLowV</c>, not reaching
/// <c>LevelV</c>, and back down through <c>LevelLowV</c> — fired on the way back, because that is
/// where "it failed to get there" becomes known. Falling is the mirror, through the upper
/// threshold and back. The two levels are sorted, as they are for the window trigger, so typing
/// them the other way round means the same pair.</para>
///
/// <para><b>No width qualifier.</b> A bench scope offers runt-plus-width; this does not, and the
/// <c>WidthLoS</c>/<c>WidthHiS</c> fields on the spec belong to the pulse-width kind alone. A runt
/// is picked out by its <i>height</i>, which is the whole point of the kind, and a width condition
/// on top of it would be a second thing to get wrong for a case nobody here has yet had.</para>
/// </summary>
public sealed class RuntTrigger : IScanTrigger
{
    /// <summary>One direction's state. Two of these, so <see cref="TriggerSlope.Either"/> is not a third code path.</summary>
    private struct Direction
    {
        /// <summary>True once the signal has sat far enough on the near side for a start to count.</summary>
        public bool Armed;

        /// <summary>True between the entry crossing and its return, while the far threshold is still unreached.</summary>
        public bool InRunt;

        /// <summary>Set when the far threshold is reached: a full pulse, not a runt, and no longer a candidate.</summary>
        public bool Disqualified;
    }

    private readonly TriggerSpec _spec;
    private readonly double _high;
    private readonly double _low;

    private bool _hasPrev;
    private double _prev;
    private long _prevIndex;
    private Direction _up;
    private Direction _down;
    private long _holdoffUntil;

    /// <param name="spec">
    /// <c>LevelV</c> and <c>LevelLowV</c> are the two thresholds in either order,
    /// <c>HysteresisV</c> the noise reject on the entry one, and <c>Slope</c> which direction of
    /// runt qualifies.
    /// </param>
    public RuntTrigger(TriggerSpec spec)
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
                nameof(spec), spec.LevelV, "A runt is a pulse that reaches one threshold and not the other, so the two must differ.");
        }

        _spec = spec;
        _high = Math.Max(spec.LevelV, spec.LevelLowV);
        _low = Math.Min(spec.LevelV, spec.LevelLowV);
    }

    public void Reset(bool keepHoldoff = false)
    {
        _hasPrev = false;
        _prev = 0.0;
        _prevIndex = 0;
        _up = default;
        _down = default;
        if (!keepHoldoff)
            _holdoffUntil = 0;
    }

    public bool Scan(ReadOnlySpan<double> samples, long firstAbsoluteIndex, out TriggerHit hit)
    {
        bool wantUp = _spec.Slope is TriggerSlope.Rising or TriggerSlope.Either;
        bool wantDown = _spec.Slope is TriggerSlope.Falling or TriggerSlope.Either;

        for (int i = 0; i < samples.Length; i++)
        {
            long index = firstAbsoluteIndex + i;
            double value = samples[i];

            if (_hasPrev)
            {
                // Both directions advanced over the sample before either hit is returned, for
                // PulseWidthTrigger's reason: a scan that returns early leaves the other direction
                // having never seen a crossing the resumed scan is already past.
                TriggerHit upHit = default;
                TriggerHit downHit = default;
                bool firedUp = wantUp && Step(ref _up, rising: true, index, value, out upHit);
                bool firedDown = wantDown && Step(ref _down, rising: false, index, value, out downHit);

                if (firedUp || firedDown)
                {
                    hit = firedUp ? upHit : downHit;
                    _holdoffUntil = index + _spec.HoldoffFrames;
                    _prev = value;
                    _prevIndex = index;
                    _hasPrev = true;
                    return true;
                }
            }

            if (wantUp)
                Arm(ref _up, rising: true, value);
            if (wantDown)
                Arm(ref _down, rising: false, value);

            _prev = value;
            _prevIndex = index;
            _hasPrev = true;
        }

        hit = default;
        return false;
    }

    /// <summary>
    /// Arms a direction once the signal has sat clear of its entry threshold, and clears a
    /// disqualification at the same time — a pulse that went all the way is over, and the next one
    /// starts a fresh candidate.
    ///
    /// <para>After the crossing test for the same sample, never before it: a single sample that
    /// both arms and fires turns the hysteresis band into no band at all.</para>
    /// </summary>
    private void Arm(ref Direction state, bool rising, double value)
    {
        double entry = rising ? _low : _high;
        double hysteresis = _spec.HysteresisV;

        if (rising ? value <= entry - hysteresis : value >= entry + hysteresis)
        {
            state.Armed = true;
            state.Disqualified = false;
        }
    }

    /// <summary>Advances one direction over one sample, answering true where this sample is the qualifying event.</summary>
    private bool Step(ref Direction state, bool rising, long index, double value, out TriggerHit hit)
    {
        double entry = rising ? _low : _high;
        double far = rising ? _high : _low;
        hit = default;

        bool entering = rising ? _prev < entry && value >= entry : _prev > entry && value <= entry;
        bool returning = rising ? _prev > entry && value <= entry : _prev < entry && value >= entry;
        bool reachedFar = rising ? value >= far : value <= far;

        if (!state.InRunt)
        {
            if (entering && state.Armed && !state.Disqualified)
            {
                state.InRunt = true;
                state.Armed = false;
            }

            return false;
        }

        if (reachedFar)
        {
            // A full pulse. Not a runt, and not a candidate again until the signal has sat clear of
            // the entry threshold -- which is what Arm's reset of Disqualified is for.
            state.InRunt = false;
            state.Disqualified = true;
            return false;
        }

        if (returning)
        {
            state.InRunt = false;

            if (index >= _holdoffUntil)
            {
                // On the way back through the entry threshold: that is where "it never got there"
                // becomes known, and it is the crossing a reader sees as the end of the runt.
                hit = new TriggerHit(_prevIndex, EdgeTrigger.CrossingFraction(_prev, value, entry));
                return true;
            }
        }

        return false;
    }
}
