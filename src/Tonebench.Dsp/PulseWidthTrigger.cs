namespace Tonebench.Dsp;

/// <summary>
/// Fires on a pulse whose <i>width</i> qualifies: the glitch trigger, and the one advanced trigger
/// that earns its keep at audio frequencies.
///
/// <para><b>What it is for.</b> An edge trigger fires on every crossing, so a fault that happens
/// once in a thousand cycles is one trace in a thousand and is never on screen when you look. A
/// width condition selects the fault itself: a crossover glitch is a pulse far narrower than the
/// half-cycle around it, a protection circuit firing is one far wider, and both are invisible to a
/// trigger that cannot tell them from the signal. With the whole record sitting before the trigger
/// point (100 % pre-trigger costs nothing here), the capture shows what led up to it.</para>
///
/// <para><b>A pulse is level crossings, not polarity.</b> For <see cref="TriggerSlope.Rising"/> a
/// pulse is the stretch <i>above</i> the level — up through it and back down; for
/// <see cref="TriggerSlope.Falling"/> it is the stretch below. <see cref="TriggerSlope.Either"/>
/// watches both, which on a square wave means every half-cycle is a pulse: that is what a bench
/// scope does with it and it is only useful with a width condition narrow enough to exclude the
/// signal.</para>
///
/// <para><b>Under Either both polarities are advanced over every sample before any hit is
/// returned</b>, which is not an optimisation to be tidied away: a positive pulse's trailing
/// crossing <i>is</i> the negative pulse's leading one, so returning as soon as the first polarity
/// fires would leave the second having never seen the pair its own pulse starts on -- and the scan
/// resumes past it, so that pulse would be lost. A square wave whose halves both qualify would then
/// report every other one.</para>
///
/// <para><b>When it fires depends on which bound is set, and that is a decision rather than an
/// inconsistency.</b> With an <i>upper</i> bound ("narrower than T", the glitch case) the width is
/// not known until the pulse ends, so it fires on the <b>trailing</b> crossing. With a lower bound
/// alone ("wider than T") it fires <b>as soon as the width passes T</b>, mid-pulse — because
/// waiting for the end would mean a pulse that never ends never fires, and a level that has stuck
/// is exactly the fault that condition is set for.</para>
/// </summary>
public sealed class PulseWidthTrigger : IScanTrigger
{
    /// <summary>One polarity's state. Two of these, so <see cref="TriggerSlope.Either"/> is not a third code path.</summary>
    private struct Polarity
    {
        /// <summary>True once the signal has sat far enough on the near side for a leading crossing to count.</summary>
        public bool Armed;

        /// <summary>True between a leading crossing and its trailing one.</summary>
        public bool InPulse;

        /// <summary>Where the leading crossing was, in fractional absolute samples.</summary>
        public double Start;

        /// <summary>Set once a lower-bound-only condition has fired mid-pulse, so the trailing edge does not fire again.</summary>
        public bool Fired;
    }

    private readonly TriggerSpec _spec;
    private readonly double _loFrames;
    private readonly double _hiFrames;
    private readonly bool _upperBounded;

    private bool _hasPrev;
    private double _prev;
    private long _prevIndex;
    private Polarity _positive;
    private Polarity _negative;
    private long _holdoffUntil;

    /// <param name="spec">
    /// <c>LevelV</c> is the threshold, <c>HysteresisV</c> the noise-reject band, and
    /// <c>WidthLoS</c>..<c>WidthHiS</c> the qualifying width in seconds. A non-finite
    /// <c>WidthHiS</c> means unbounded above.
    /// </param>
    /// <param name="sampleRateHz">
    /// What the two widths are converted against, once, here. A width in seconds is the only way to
    /// state one that means the same thing at every sample rate — the lesson the chirp's period and
    /// the transfer queue's depth each had to learn — and a width in frames is the only way to
    /// compare it against sample indices.
    /// </param>
    public PulseWidthTrigger(TriggerSpec spec, double sampleRateHz)
    {
        if (!double.IsFinite(spec.LevelV))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.LevelV, "The trigger level must be finite.");
        if (!(spec.HysteresisV >= 0.0) || !double.IsFinite(spec.HysteresisV))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.HysteresisV, "The hysteresis must be zero or positive and finite.");
        if (spec.HoldoffFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.HoldoffFrames, "The holdoff cannot be negative.");
        if (!(sampleRateHz > 0.0) || !double.IsFinite(sampleRateHz))
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), sampleRateHz, "The sample rate must be positive and finite.");
        if (!(spec.WidthLoS >= 0.0))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.WidthLoS, "The low end of the width cannot be negative.");
        if (double.IsFinite(spec.WidthHiS) && spec.WidthHiS < spec.WidthLoS)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spec), spec.WidthHiS, "The high end of the width cannot be below the low end.");
        }

        _spec = spec;
        _loFrames = spec.WidthLoS * sampleRateHz;
        _upperBounded = double.IsFinite(spec.WidthHiS);
        _hiFrames = _upperBounded ? spec.WidthHiS * sampleRateHz : double.PositiveInfinity;
    }

    public void Reset(bool keepHoldoff = false)
    {
        _hasPrev = false;
        _prev = 0.0;
        _prevIndex = 0;
        _positive = default;
        _negative = default;
        if (!keepHoldoff)
            _holdoffUntil = 0;
    }

    public bool Scan(ReadOnlySpan<double> samples, long firstAbsoluteIndex, out TriggerHit hit)
    {
        bool wantPositive = _spec.Slope is TriggerSlope.Rising or TriggerSlope.Either;
        bool wantNegative = _spec.Slope is TriggerSlope.Falling or TriggerSlope.Either;

        for (int i = 0; i < samples.Length; i++)
        {
            long index = firstAbsoluteIndex + i;
            double value = samples[i];

            if (_hasPrev)
            {
                // Both polarities are advanced over this sample BEFORE either hit is returned, and
                // that ordering is the whole of why Either works. A positive pulse's trailing
                // crossing is the negative pulse's leading one, so returning the moment the first
                // polarity fires leaves the second having never seen the pair that starts its own
                // pulse -- and since the scan resumes past it, that pulse is lost. Stepping both
                // first costs one comparison and makes Either report every qualifying pulse of
                // either polarity. The two can never fire on the same sample, one firing on a
                // trailing crossing where the other is only starting, so preferring the positive
                // hit is a tie-break that is not reached.
                // Declared up front because the short-circuit leaves an out parameter unassigned
                // where the polarity is not wanted, which the compiler is right to refuse.
                TriggerHit positiveHit = default;
                TriggerHit negativeHit = default;
                bool firedPositive = wantPositive && Step(ref _positive, positive: true, index, value, out positiveHit);
                bool firedNegative = wantNegative && Step(ref _negative, positive: false, index, value, out negativeHit);

                if (firedPositive)
                {
                    hit = positiveHit;
                    return Fire(index, value, ref hit);
                }

                if (firedNegative)
                {
                    hit = negativeHit;
                    return Fire(index, value, ref hit);
                }
            }

            if (wantPositive)
                Arm(ref _positive, positive: true, value);
            if (wantNegative)
                Arm(ref _negative, positive: false, value);

            _prev = value;
            _prevIndex = index;
            _hasPrev = true;
        }

        hit = default;
        return false;
    }

    /// <summary>
    /// Arms a polarity once the signal has sat on its own near side of the hysteresis band.
    ///
    /// <para>After the crossing test for the same sample, never before it — <see cref="EdgeTrigger"/>
    /// documents why: a single sample that both arms and fires turns the hysteresis band into no
    /// band at all.</para>
    /// </summary>
    private void Arm(ref Polarity state, bool positive, double value)
    {
        double level = _spec.LevelV;
        double hysteresis = _spec.HysteresisV;

        if (positive ? value <= level - hysteresis : value >= level + hysteresis)
            state.Armed = true;
    }

    /// <summary>
    /// Advances one polarity over one sample, answering true where this sample is the qualifying
    /// event.
    /// </summary>
    private bool Step(ref Polarity state, bool positive, long index, double value, out TriggerHit hit)
    {
        double level = _spec.LevelV;
        hit = default;

        bool leading = positive ? _prev < level && value >= level : _prev > level && value <= level;
        bool trailing = positive ? _prev > level && value <= level : _prev < level && value >= level;

        if (!state.InPulse)
        {
            if (leading && state.Armed)
            {
                state.InPulse = true;
                state.Fired = false;
                state.Start = _prevIndex + EdgeTrigger.CrossingFraction(_prev, value, level);
                state.Armed = false;
            }

            return false;
        }

        if (trailing)
        {
            double fraction = EdgeTrigger.CrossingFraction(_prev, value, level);
            double end = _prevIndex + fraction;
            double width = end - state.Start;
            bool qualifies = !state.Fired && width >= _loFrames && width <= _hiFrames;

            state.InPulse = false;
            state.Fired = false;

            if (qualifies && index >= _holdoffUntil)
            {
                // At the trailing crossing: that is where the width became known, and it is where a
                // bench scope puts the trigger point for a narrower-than condition. Reported as
                // EdgeTrigger reports one -- the sample before, plus a fraction -- so a caller that
                // resumes a scan from the hit resumes from the same place for either kind.
                hit = new TriggerHit(_prevIndex, fraction);
                return true;
            }

            return false;
        }

        // Mid-pulse, and only where nothing bounds it above: a pulse that never ends would never
        // fire if this waited for its trailing edge, and a level that has stuck is exactly the
        // fault a wider-than condition is set for.
        if (!_upperBounded && !state.Fired && _loFrames > 0.0 && index - state.Start >= _loFrames && index >= _holdoffUntil)
        {
            state.Fired = true;

            // No crossing to interpolate: the event is the width passing a bound, which happens in
            // the middle of a stretch that is not crossing anything. Spelled as the previous sample
            // plus a whole fraction rather than as this sample plus none, which is the same position
            // and keeps the convention above.
            hit = new TriggerHit(_prevIndex, 1.0);
            return true;
        }

        return false;
    }

    /// <summary>Books the holdoff and carries the sample that fired into the previous slot, so a resumed scan does not re-examine the pair.</summary>
    private bool Fire(long index, double value, ref TriggerHit hit)
    {
        _holdoffUntil = index + _spec.HoldoffFrames;
        _prev = value;
        _prevIndex = index;
        _hasPrev = true;
        return true;
    }
}
