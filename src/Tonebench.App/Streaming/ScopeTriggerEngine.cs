using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Streaming;

/// <summary>Where the engine is in the arm / trigger / fill / hold cycle.</summary>
internal enum ScopeEngineState
{
    /// <summary>Not armed. Roll mode sits here for ever, and so does a Single that has delivered.</summary>
    Idle,

    /// <summary>Armed and scanning for a qualifying event.</summary>
    Waiting,

    /// <summary>Triggered; waiting for the post-trigger samples to arrive before the record is whole.</summary>
    Filling,

    /// <summary>A record is ready to be taken. <see cref="ScopeRunMode.Single"/> stays here until re-armed.</summary>
    Held,
}

/// <summary>
/// Drives the oscilloscope's capture: feeds the stream into <see cref="ScopeCaptureBuffer"/>,
/// walks the trigger over it, and commits a record once the post-trigger samples have arrived.
///
/// <para>The whole reason this is a state machine fed block by block, rather than a search over a
/// record someone already captured, is that the two things a scope must have both need it.
/// <b>Pre-trigger</b> is samples from before the event, so the event has to be found as it passes.
/// And <b>holdoff</b>, pulse width, dropout and runt are state machines over the contiguous stream;
/// a search inside a finished window cannot express any of them.</para>
///
/// <para>Not thread-safe by design: it is driven from one thread, the consumer side of
/// <c>DeviceSessionService.PumpAsync</c>, which is where the blocks arrive. Only
/// <see cref="TakeRecord"/> and the state are read from elsewhere, and both are cheap snapshots.</para>
/// </summary>
internal sealed class ScopeTriggerEngine(ScopeCaptureBuffer buffer)
{
    private readonly ScopeCaptureBuffer _buffer = buffer;
    private readonly object _handoff = new();

    private ScopeCaptureSpec _spec;
    private IScanTrigger? _scanner;
    private ScopeEngineState _state = ScopeEngineState.Idle;
    private ScopeRecord? _record;

    private long _armedAt;
    private long _recordStart;
    private long _recordEnd;
    private double _triggerPosition;
    private bool _forced;

    private long _droppedAtArm;
    private long _droppedSeen;

    /// <summary>Scratch for the trigger scan, grown to the largest block seen.</summary>
    private double[] _scan = new double[4096];

    public ScopeEngineState State
    {
        get
        {
            lock (_handoff)
                return _state;
        }
    }

    /// <summary>The spec the engine is currently running, as last armed.</summary>
    public ScopeCaptureSpec Spec => _spec;

    /// <summary>
    /// Arms for the next capture. Sizes the buffer to hold the record plus room to spare, so a
    /// trigger found near the write head still has its pre-trigger history behind it.
    /// </summary>
    public void Arm(ScopeCaptureSpec spec, long droppedTotal)
    {
        if (spec.RecordFrames < 2)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.RecordFrames, "A record needs at least two frames.");
        if (spec.PreTriggerFrames < 0 || spec.PreTriggerFrames > spec.RecordFrames)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.PreTriggerFrames, "The pre-trigger cannot be negative or longer than the record.");
        if (!(spec.SampleRateHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.SampleRateHz, "The sample rate must be positive.");

        _spec = spec;

        // Twice the record, so a hit landing anywhere in the newest block still has a whole
        // pre-trigger window behind it that has not been overwritten while the rest was filling.
        _buffer.EnsureCapacity(checked(spec.RecordFrames * 2));

        _scanner = spec.Trigger.Kind switch
        {
            TriggerKind.Edge => new EdgeTrigger(spec.Trigger),
            _ => throw new NotSupportedException($"The {spec.Trigger.Kind} trigger is not implemented yet."),
        };

        _droppedAtArm = droppedTotal;
        _droppedSeen = droppedTotal;
        _armedAt = _buffer.Newest;
        _forced = false;

        lock (_handoff)
        {
            _record = null;
            _state = spec.Mode == ScopeRunMode.Roll ? ScopeEngineState.Idle : ScopeEngineState.Waiting;
        }
    }

    /// <summary>Stops capturing. A record already committed stays takeable.</summary>
    public void Disarm()
    {
        _scanner = null;
        lock (_handoff)
        {
            if (_state is ScopeEngineState.Waiting or ScopeEngineState.Filling)
                _state = ScopeEngineState.Idle;
        }
    }

    /// <summary>
    /// The committed record, handed over once. Null until one is ready — and null again afterwards,
    /// so a consumer polling this cannot draw the same capture twice and call it a new one.
    /// </summary>
    public ScopeRecord? TakeRecord()
    {
        lock (_handoff)
        {
            var record = _record;
            _record = null;
            return record;
        }
    }

    /// <summary>
    /// Writes one block and advances the capture over it. The single call
    /// <c>DeviceSessionService.PumpAsync</c> makes, placed beside the stream recorder's and before
    /// the pipeline takes ownership of the block.
    /// </summary>
    /// <param name="droppedTotal">
    /// The device's dropped-chunk and dropped-block counters added together. Their <i>movement</i>
    /// across a record is what marks it spliced; the absolute value is never read.
    /// </param>
    public void Write(ReadOnlySpan<float> interleaved, ReadOnlySpan<double> scale, bool startsAfterDiscontinuity, long droppedTotal)
    {
        long from = _buffer.Newest;
        _buffer.Write(interleaved, scale, startsAfterDiscontinuity);
        long to = _buffer.Newest;

        if (startsAfterDiscontinuity)
        {
            // The flag marks the splice. It does not bound the wrong levels that follow one: after
            // a 24 dB attenuator release ~10 blocks read 24 dB low and only the first is flagged.
            _buffer.BookDiscard(_spec.DiscontinuityHoldoffFrames);
            _scanner?.Reset();
        }

        if (droppedTotal != _droppedSeen)
            _droppedSeen = droppedTotal;

        Advance(from, to);
    }

    private void Advance(long from, long to)
    {
        if (_state == ScopeEngineState.Waiting)
            ScanForTrigger(from, to);

        if (_state == ScopeEngineState.Filling)
            FillIfReady();
    }

    private void ScanForTrigger(long from, long to)
    {
        var scanner = _scanner;
        if (scanner is null)
            return;

        // Never scan samples the buffer would refuse to serve: they are either overwritten or on
        // the far side of a break, and a trigger found in them could not be drawn anyway.
        long floor = Math.Max(_buffer.ContiguousFrom, _buffer.Oldest);
        long start = Math.Max(from, floor);

        while (start < to)
        {
            int count = (int)Math.Min(to - start, _scan.Length);
            if (count > _scan.Length)
                _scan = new double[count];

            if (!_buffer.CopyRange(_spec.TriggerChannel, start, _scan.AsSpan(0, count)))
                return;

            if (scanner.Scan(_scan.AsSpan(0, count), start, out var hit))
            {
                if (AcceptTrigger(hit.Position))
                    return;

                // The hit was real but its pre-trigger history is not there yet -- too close to the
                // start of the stream, or to a break. Keep scanning rather than disarming: the next
                // crossing will have the history behind it.
                start = hit.Index + 2;
                continue;
            }

            start += count;
        }

        ForceIfTimedOut(to);
    }

    private bool AcceptTrigger(double position)
    {
        // Rounded, not floored-plus-one: a crossing that lands exactly on a sample would otherwise
        // anchor the record one frame late. Which sample anchors it only decides which samples are
        // in the record -- t = 0 is drawn from the fractional position, which is carried whole.
        long triggerSample = (long)Math.Round(position, MidpointRounding.AwayFromZero);
        long recordStart = triggerSample - _spec.PreTriggerFrames;
        if (recordStart < Math.Max(_buffer.ContiguousFrom, _buffer.Oldest))
            return false;

        _recordStart = recordStart;
        _recordEnd = recordStart + _spec.RecordFrames;
        _triggerPosition = position;
        _forced = false;
        _droppedAtArm = _droppedSeen;

        lock (_handoff)
            _state = ScopeEngineState.Filling;

        FillIfReady();
        return true;
    }

    private void ForceIfTimedOut(long now)
    {
        if (_spec.Mode != ScopeRunMode.Auto || _spec.AutoTimeoutFrames <= 0)
            return;
        if (now - _armedAt < _spec.AutoTimeoutFrames)
            return;

        // Place the record so it ends at the newest sample: everything is already there, so this
        // commits immediately rather than waiting for a post-trigger window that nothing is
        // counting down to.
        long recordStart = now - _spec.RecordFrames;
        if (recordStart < Math.Max(_buffer.ContiguousFrom, _buffer.Oldest))
            return;

        _recordStart = recordStart;
        _recordEnd = recordStart + _spec.RecordFrames;
        _triggerPosition = recordStart + _spec.PreTriggerFrames;
        _forced = true;
        _droppedAtArm = _droppedSeen;

        lock (_handoff)
            _state = ScopeEngineState.Filling;

        FillIfReady();
    }

    private void FillIfReady()
    {
        if (_buffer.Newest < _recordEnd)
            return;

        // A break that landed inside the record while it was filling takes the record with it:
        // its two halves were never adjacent.
        if (_buffer.ContiguousFrom > _recordStart)
        {
            ReArm();
            return;
        }

        var channels = new double[_buffer.ChannelCount][];
        for (int c = 0; c < channels.Length; c++)
        {
            var samples = new double[_spec.RecordFrames];
            if (!_buffer.CopyRange(c, _recordStart, samples))
            {
                ReArm();
                return;
            }

            // What is stored is what is measured. Quantising here rather than at save time is what
            // keeps a reading on screen equal to the same reading after a reload.
            PcmCodec.QuantizeInPlace(samples);
            channels[c] = samples;
        }

        var record = new ScopeRecord(
            channels,
            _spec.SampleRateHz,
            _recordStart,
            _triggerPosition,
            _buffer.Epoch,
            Spliced: _droppedSeen != _droppedAtArm,
            Forced: _forced);

        lock (_handoff)
        {
            _record = record;
            _state = _spec.Mode == ScopeRunMode.Single ? ScopeEngineState.Held : ScopeEngineState.Waiting;
        }

        if (_spec.Mode != ScopeRunMode.Single)
            ReArmScanner();
    }

    private void ReArm()
    {
        ReArmScanner();
        lock (_handoff)
            _state = ScopeEngineState.Waiting;
    }

    private void ReArmScanner()
    {
        _scanner?.Reset();
        _armedAt = _buffer.Newest;
        _droppedAtArm = _droppedSeen;
        _forced = false;
    }
}
