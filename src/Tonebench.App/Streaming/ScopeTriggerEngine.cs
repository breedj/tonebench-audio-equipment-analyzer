using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Streaming;

/// <summary>Where the engine is in the arm / trigger / fill / hold cycle.</summary>
public enum ScopeEngineState
{
    /// <summary>Not armed, and nothing coming. A Single that has delivered sits here.</summary>
    Idle,

    /// <summary>Armed and scanning for a qualifying event.</summary>
    Waiting,

    /// <summary>Triggered; waiting for the post-trigger samples to arrive before the record is whole.</summary>
    Filling,

    /// <summary>A record is ready to be taken. <see cref="ScopeRunMode.Single"/> stays here until re-armed.</summary>
    Held,

    /// <summary>
    /// <see cref="ScopeRunMode.Roll"/>: no trigger at all, and the newest window is the record.
    ///
    /// <para>Its own state rather than <see cref="Idle"/>, which is what it used to be: idle means
    /// nothing is coming, and a Roll run that sat there committed no record ever — the mode was
    /// offered in the picker, accepted by the runner and then produced a screen that stayed blank
    /// for ever. Nothing scans and nothing fills here; see <c>NewestWindow</c>.</para>
    /// </summary>
    Rolling,
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
public sealed class ScopeTriggerEngine(ScopeCaptureBuffer buffer)
{
    private readonly ScopeCaptureBuffer _buffer = buffer;
    private readonly object _handoff = new();

    private ScopeCaptureSpec _spec;
    private IScanTrigger? _scanner;

    /// <summary>
    /// A retune waiting to be taken up, built on the caller's thread and applied on the stream's.
    /// See <see cref="Retune"/>.
    /// </summary>
    private (ScopeCaptureSpec Spec, IScanTrigger Scanner)? _pending;
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
        Validate(spec);
        var scanner = BuildScanner(spec);

        _droppedAtArm = droppedTotal;
        _droppedSeen = droppedTotal;

        lock (_handoff)
            _record = null;

        Take(spec, scanner);

        // The stimulus settle, booked here and deliberately NOT in Take -- so it applies to an arm
        // and not to a retune. An arm follows a generator configuration, where the audio in flight
        // is still the old stimulus and the DUT has not settled on the new one; a retune changes no
        // stimulus at all (the endpoint refuses a generator on one), so booking it there would make
        // every knob turn cost a few hundred milliseconds of stream for nothing.
        //
        // Booked on the buffer rather than kept as a deadline here, which is the point: it raises
        // ContiguousFrom, so those samples are excluded from the trigger scan AND from any record's
        // pre-trigger history. A deadline on the scan alone would still let the first record reach
        // back into the old stimulus for its pre-trigger window.
        if (spec.SettleFrames > 0)
            _buffer.BookDiscard(spec.SettleFrames);

        // A retune queued before this arm is deliberately NOT dropped, and that is not an
        // oversight. OscilloscopeRunner.StartAsync sets its phase and spawns the run task, so the
        // runner is Running -- and therefore retunable -- a moment before the task gets here: a
        // control turned in that moment reaches Retune first and would be lost silently, which the
        // user meets as a knob that did nothing. A retune can only ever arrive after its own run's
        // start, so a pending one is always the newer intent; one left over from a run that has
        // ended is cleared by Disarm, which every run's finally calls.
    }

    /// <summary>
    /// Changes the settings of a capture already running, without stopping it.
    ///
    /// <para><b>Why this exists.</b> A trigger level is set by dragging it, so a level that only
    /// took effect on a restart would be a level nobody could find -- and the same is true of every
    /// other control on a bench instrument, where nothing needs a stop and a start to take effect.
    /// The one setting that genuinely cannot be applied to a capture already running is the sample
    /// rate, which re-clocks the hardware; <see cref="Services.OscilloscopeRunner.Retune"/> refuses
    /// that by name rather than appearing to honour it.</para>
    ///
    /// <para>The spec is validated and its scanner built <b>here</b>, on the caller's thread, so a
    /// setting this engine cannot honour is an error on the request that asked for it rather than
    /// an exception thrown on the stream. It is <b>applied</b> on the stream's own thread, at the
    /// top of the next <see cref="Write"/>, because that is the only thread allowed to touch the
    /// scan state -- so a retune lands within one block, a few tens of milliseconds at any
    /// supported rate.</para>
    ///
    /// <para>What it abandons is deliberate. A fill in progress goes back to waiting: the record it
    /// was filling was triggered on settings the user has just replaced. A record already committed
    /// and not yet taken is <i>kept</i> -- it is the last thing the screen saw, and blanking the
    /// trace because a knob moved is the opposite of what the knob is for.</para>
    ///
    /// <para>A retune that lands just before <see cref="Arm"/> survives it rather than being
    /// dropped; see the note there for why that window is reachable at all.</para>
    /// </summary>
    public void Retune(ScopeCaptureSpec spec)
    {
        Validate(spec);
        var scanner = BuildScanner(spec);

        lock (_handoff)
            _pending = (spec, scanner);
    }

    /// <summary>Stops capturing. A record already committed stays takeable.</summary>
    public void Disarm()
    {
        _scanner = null;
        lock (_handoff)
        {
            // Or a retune that arrived as the run was ending would re-arm a disarmed engine.
            _pending = null;

            if (_state is ScopeEngineState.Waiting or ScopeEngineState.Filling or ScopeEngineState.Rolling)
                _state = ScopeEngineState.Idle;
        }
    }

    private static void Validate(ScopeCaptureSpec spec)
    {
        if (spec.RecordFrames < 2)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.RecordFrames, "A record needs at least two frames.");
        if (spec.PreTriggerFrames < 0 || spec.PreTriggerFrames > spec.RecordFrames)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.PreTriggerFrames, "The pre-trigger cannot be negative or longer than the record.");
        if (!(spec.SampleRateHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.SampleRateHz, "The sample rate must be positive.");
    }

    /// <summary>
    /// The scanner for a kind.
    ///
    /// <para>The width-based kinds are handed <c>spec.SampleRateHz</c>, which is the one place that
    /// conversion happens: a width is set in seconds -- the only unit that means the same thing at
    /// every sample rate -- and compared against sample indices, so something has to convert it, and
    /// doing it here means no scanner carries a rate it could get out of step with.</para>
    /// </summary>
    private static IScanTrigger BuildScanner(ScopeCaptureSpec spec) => spec.Trigger.Kind switch
    {
        TriggerKind.Edge => new EdgeTrigger(spec.Trigger),
        TriggerKind.PulseWidth => new PulseWidthTrigger(spec.Trigger, spec.SampleRateHz),
        TriggerKind.Window => new WindowTrigger(spec.Trigger),
        _ => throw new NotSupportedException($"The {spec.Trigger.Kind} trigger is not implemented yet."),
    };

    /// <summary>Puts a validated spec and its scanner into service: armed, and scanning from here on.</summary>
    private void Take(ScopeCaptureSpec spec, IScanTrigger scanner)
    {
        _spec = spec;

        // Twice the record, so a hit landing anywhere in the newest block still has a whole
        // pre-trigger window behind it that has not been overwritten while the rest was filling.
        _buffer.EnsureCapacity(checked(spec.RecordFrames * 2));

        _scanner = scanner;
        _armedAt = _buffer.Newest;
        _forced = false;

        lock (_handoff)
            _state = spec.Mode == ScopeRunMode.Roll ? ScopeEngineState.Rolling : ScopeEngineState.Waiting;
    }

    /// <summary>Takes up a queued retune. Called at the top of <see cref="Write"/> and nowhere else.</summary>
    private void ApplyPending()
    {
        (ScopeCaptureSpec Spec, IScanTrigger Scanner) pending;
        lock (_handoff)
        {
            if (_pending is not { } next)
                return;

            _pending = null;
            pending = next;
        }

        _droppedAtArm = _droppedSeen;
        Take(pending.Spec, pending.Scanner);
    }

    /// <summary>
    /// How many roll steps have gone by since the run was armed — 0 in every other mode.
    ///
    /// <para><b>Arithmetic on the buffer's own clock, so it costs nothing and allocates nothing.</b>
    /// It is what a client watches to know there is a newer window to fetch, in place of the record
    /// counter a triggered run advances. The step is a <i>time</i> converted once by the runner, so
    /// the counter ticks at the same rate whatever the sample rate and whatever the record
    /// length.</para>
    /// </summary>
    public long RollWindowsElapsed
    {
        get
        {
            lock (_handoff)
            {
                if (_state != ScopeEngineState.Rolling || _spec.RollStepFrames <= 0)
                    return 0;
                return Math.Max(0, (_buffer.Newest - _armedAt) / _spec.RollStepFrames);
            }
        }
    }

    /// <summary>
    /// Whether a whole record's worth of unbroken history is on hand to roll — asked without
    /// copying any of it, so the status poll can answer it several times a second.
    /// </summary>
    public bool RollWindowReady
    {
        get
        {
            lock (_handoff)
            {
                if (_state != ScopeEngineState.Rolling)
                    return false;
            }

            return _buffer.AvailableFrames >= _spec.RecordFrames;
        }
    }

    /// <summary>
    /// The newest window, built here and now — Roll's record.
    ///
    /// <para><b>Built on demand rather than committed on a schedule, and that is the whole design
    /// of Roll.</b> A rolling scope has no event to anchor a record to, so <i>any</i> window is as
    /// good as the one before it and the only question is when somebody looks. Committing one every
    /// n milliseconds would put a fresh <c>double[RecordFrames]</c> per channel on the acquisition
    /// thread at that rate whether or not anything was reading it — 3 MB a go at the one-second
    /// extreme, which is a large-object allocation and the documented cause of the sound card's
    /// dropped chunks. Building it when it is asked for moves that cost onto the request thread and
    /// makes it proportional to how often somebody is looking, which for a scrolling display is the
    /// client's own status poll.</para>
    ///
    /// <para>Null until a whole record of unbroken history exists. <b>Never spliced</b>, by
    /// construction rather than by a check: a window that would cross a break is refused by the
    /// buffer, so a drop shortens the history available instead of putting two non-adjacent pieces
    /// of the stream side by side.</para>
    /// </summary>
    public ScopeRecord? NewestWindow()
    {
        lock (_handoff)
        {
            if (_state != ScopeEngineState.Rolling)
                return null;
        }

        int frames = _spec.RecordFrames;
        if (frames < 2)
            return null;

        long newest = _buffer.Newest;
        long start = newest - frames;
        if (start < 0 || _buffer.AvailableFrames < frames)
            return null;

        var channels = new double[_buffer.ChannelCount][];
        for (int c = 0; c < channels.Length; c++)
        {
            var samples = new double[frames];
            if (!_buffer.CopyRange(c, start, samples))
                return null;

            // What is stored is what is measured -- the committed path's own rule, and it has to
            // hold here too or a rolling reading and the same capture saved would differ.
            PcmCodec.QuantizeInPlace(samples);
            channels[c] = samples;
        }

        return new ScopeRecord(
            channels,
            _spec.SampleRateHz,
            start,
            // The right-hand edge: t = 0 is "now" and the record runs backwards from it.
            newest,
            _buffer.Epoch,
            Spliced: false,
            Forced: false,
            Rolled: true);
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
        ApplyPending();

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
