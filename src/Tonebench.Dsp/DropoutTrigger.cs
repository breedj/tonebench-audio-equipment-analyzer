namespace Tonebench.Dsp;

/// <summary>
/// Fires when a qualifying edge has <i>not</i> happened for long enough: the trigger for a signal
/// that stops.
///
/// <para><b>What it is for.</b> Every other trigger here waits for something; this one waits for
/// nothing, which is what an intermittent fault actually looks like. An amplifier that mutes for a
/// tenth of a second, an oscillator that drops out, a clock that stalls, a connection that goes
/// open — none of them produces an event to trigger on, and with 100 % pre-trigger the record is
/// the signal right up to the moment it stopped.</para>
///
/// <para><b>It is also the <c>Spliced</c> flag made visible</b>, which is worth knowing before a
/// capture is read as a DUT fault: a dropped USB chunk or a DAC starvation gap is a stretch of
/// stream with no edges in it, so this trigger will fire on one. The record carries
/// <c>ScopeRecord.Spliced</c> precisely because a hole and a dead DUT look identical in a waveform,
/// and this is the kind most likely to catch one.</para>
///
/// <para><b>The timeout is <c>WidthLoS</c></b>, the same field the pulse-width trigger reads as its
/// shorter bound — one spec record for every kind, with the fields of the kind not selected simply
/// not read.</para>
/// </summary>
public sealed class DropoutTrigger : IScanTrigger
{
    private readonly TriggerSpec _spec;
    private readonly double _timeoutFrames;

    private bool _hasPrev;
    private double _prev;
    private long _prevIndex;
    private bool _armedRising;
    private bool _armedFalling;

    /// <summary>Where the clock is counting from: the last qualifying edge, or the first sample seen.</summary>
    private long? _countingFrom;

    private long _holdoffUntil;

    /// <param name="spec">
    /// <c>LevelV</c> and <c>HysteresisV</c> decide what counts as an edge, <c>Slope</c> which
    /// direction counts (<see cref="TriggerSlope.Either"/> meaning no crossing at all), and
    /// <c>WidthLoS</c> is how long an absence has to last.
    /// </param>
    /// <param name="sampleRateHz">What the timeout is converted against, once, here.</param>
    public DropoutTrigger(TriggerSpec spec, double sampleRateHz)
    {
        if (!double.IsFinite(spec.LevelV))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.LevelV, "The trigger level must be finite.");
        if (!(spec.HysteresisV >= 0.0) || !double.IsFinite(spec.HysteresisV))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.HysteresisV, "The hysteresis must be zero or positive and finite.");
        if (spec.HoldoffFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.HoldoffFrames, "The holdoff cannot be negative.");
        if (!(sampleRateHz > 0.0) || !double.IsFinite(sampleRateHz))
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), sampleRateHz, "The sample rate must be positive and finite.");
        if (!(spec.WidthLoS > 0.0) || !double.IsFinite(spec.WidthLoS))
        {
            throw new ArgumentOutOfRangeException(
                nameof(spec), spec.WidthLoS, "A dropout trigger needs a timeout above zero: how long an absence has to last.");
        }

        _spec = spec;
        _timeoutFrames = spec.WidthLoS * sampleRateHz;
    }

    public void Reset(bool keepHoldoff = false)
    {
        _hasPrev = false;
        _prev = 0.0;
        _prevIndex = 0;
        _armedRising = false;
        _armedFalling = false;
        _countingFrom = null;
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

            // The clock starts at the first sample this scanner sees rather than at the first edge,
            // so an input that is already dead when the run arms fires after one timeout instead of
            // waiting for ever for an edge that will never come -- which is the whole case.
            _countingFrom ??= index;

            if (_hasPrev)
            {
                bool rising = wantRising && _armedRising && _prev < level && value >= level;
                bool falling = wantFalling && _armedFalling && _prev > level && value <= level;

                if (rising || falling)
                {
                    _countingFrom = index;
                    if (rising)
                        _armedRising = false;
                    if (falling)
                        _armedFalling = false;
                }
                else if (index - _countingFrom.Value >= _timeoutFrames && index >= _holdoffUntil)
                {
                    // The event is an absence, so there is no crossing to interpolate: spelled as
                    // the previous sample plus a whole fraction, which is this sample's position
                    // and keeps the convention every other scanner reports in.
                    hit = new TriggerHit(_prevIndex, 1.0);
                    _holdoffUntil = index + _spec.HoldoffFrames;

                    // The clock restarts here rather than stopping, so a signal that stays dead
                    // goes on firing once a timeout -- which is what a bench instrument does, and
                    // what makes a dead input draw a trace at all rather than one and then nothing.
                    _countingFrom = index;

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
}
