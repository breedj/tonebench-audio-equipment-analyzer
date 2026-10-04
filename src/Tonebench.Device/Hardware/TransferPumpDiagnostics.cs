using System.Diagnostics;

namespace Tonebench.Device.Hardware;

/// <summary>
/// What the pump was doing when a TX-starvation event was recorded — enough to tell the causes apart
/// without a profiler attached.
/// </summary>
internal enum PumpStallCause
{
    /// <summary>No register write was involved: the loop itself ran long. The GC and thread-pool columns say why.</summary>
    LoopLate,

    /// <summary>The iteration contained a quiesce (drain EP2 → write a register → re-prime), which empties the TX queue by design.</summary>
    RegisterWrite,

    /// <summary>A bulk TX transfer moved fewer bytes than it was given. Those frames never reached the DAC at all.</summary>
    ShortWrite,

    /// <summary>A bulk RX transfer returned fewer bytes than it asked for. Those frames are a hole in the sample stream, not a DAC problem.</summary>
    ShortRead,
}

/// <summary>One recorded starvation event. A struct in a preallocated array — see <see cref="TransferPumpDiagnostics"/>.</summary>
internal readonly record struct PumpStall(
    long Iteration,
    PumpStallCause Cause,
    double IterationMs,
    double ReadWaitMs,
    double WriteWaitMs,
    double SubmitMs,
    double RegisterMs,
    double SlackMs,
    double GcPauseMs,
    int Gen0,
    int Gen1,
    int Gen2,
    int ThreadPoolThreads,
    long ThreadPoolPending,

    /// <summary>Bytes this event lost, if it lost any — TX bytes never sent for <see cref="PumpStallCause.ShortWrite"/>, RX bytes never delivered for <see cref="PumpStallCause.ShortRead"/>, zero otherwise. The cause says which.</summary>
    int MissingBytes);

/// <summary>
/// Measures whether the QA403's DAC is actually being kept fed, and attributes every shortfall.
///
/// <para><b>The metric.</b> The DAC clock never stops: every 1/fs seconds it emits a sample, and if
/// the host has not delivered one it emits silence — which is exactly the flat span a scope shows on
/// the generator output. So the only quantity that matters is
/// <c>slack = framesSubmitted/fs − elapsedSeconds</c>: seconds of audio handed to libusb minus
/// seconds of DAC time. It starts at one queue depth, and because samples that were never played are
/// still queued and play late, <b>every gap leaves a permanent downward step in it equal to the gap's
/// own length</b>. Reading a step off this number is reading the width of the flat span off the
/// scope. (Host-vs-device clock drift is tens of ppm, so it appears as a slow ramp, never a step.)</para>
///
/// <para>Per iteration the same thing is a lateness: the loop is paced by one chunk of RX, so
/// <c>iterationMs − chunkMs</c> is how much slack that iteration burned. A single long stall shows up
/// there; slow accumulation shows up in <see cref="MinSlackMs"/>. Both are kept, because the suspects
/// look different: a GC pause or a scheduling stall is one late iteration, whereas a quiesce is a
/// deliberate drain that spends its slack across many.</para>
///
/// <para><b>Cost.</b> Everything on the pump thread is a <see cref="Stopwatch.GetTimestamp"/>, an
/// arithmetic compare and — only on a recorded event — a few counter reads into a preallocated array.
/// No allocation, no logging, no lock: the detector must not become the stall. Formatting and logging
/// happen on the consumer side, which is off the pump thread by construction.</para>
/// </summary>
internal sealed class TransferPumpDiagnostics(double sampleRateHz, int queueDepth, int chunkFrames)
{
    /// <summary>Events held between drains. The consumer drains every assembled block, so this bounds a burst, not the run — see <see cref="Record"/>, which wraps rather than stopping.</summary>
    private const int Capacity = 128;

    /// <summary>An iteration is recorded once it burns this much slack beyond its own chunk period. Below a few ms it is transfer-completion jitter, not a threat to a 64 ms queue.</summary>
    private const double LateIterationMs = 4.0;

    private readonly PumpStall[] _stalls = new PumpStall[Capacity];

    /// <summary>Consumer-owned copy target, so <see cref="TryDrain"/> can hand out a stable span while the pump keeps writing.</summary>
    private readonly PumpStall[] _drainBuffer = new PumpStall[Capacity];
    private readonly double _chunkMs = chunkFrames * 1000.0 / sampleRateHz;
    private readonly Stopwatch _clock = new();

    /// <summary>Total events ever recorded. Pump-thread writes only, published with a release write.</summary>
    private long _written;

    /// <summary>Total events ever handed to the consumer. Consumer-thread only.</summary>
    private long _drained;

    private long _iteration;
    private long _suppressed;

    private double _minSlackMs = double.MaxValue;
    private double _worstIterationMs;

    private TimeSpan _gcPauseAtStart;
    private int _gen0AtStart;
    private int _gen1AtStart;
    private int _gen2AtStart;

    /// <summary>Milliseconds of audio the queue is primed with — the whole budget any stall is spending.</summary>
    public double PrimedSlackMs { get; } = queueDepth * chunkFrames * 1000.0 / sampleRateHz;

    public long Iterations => _iteration;

    public long ShortWrites { get; private set; }

    public long ShortWriteBytes { get; private set; }

    /// <summary>RX transfers that came back short — see <see cref="RecordShortRead"/>.</summary>
    public long ShortReads { get; private set; }

    /// <summary>Bytes of captured audio never delivered because a read came back short.</summary>
    public long ShortReadBytes { get; private set; }

    /// <summary>Lowest slack seen. <c>PrimedSlackMs</c> minus this is the total DAC time lost over the run.</summary>
    public double MinSlackMs => _minSlackMs == double.MaxValue ? PrimedSlackMs : _minSlackMs;

    public double WorstIterationMs => _worstIterationMs;

    public long SuppressedStalls => _suppressed;

    public TimeSpan GcPauseDuringStream => GC.GetTotalPauseDuration() - _gcPauseAtStart;

    public int Gen0Collections => GC.CollectionCount(0) - _gen0AtStart;

    public int Gen1Collections => GC.CollectionCount(1) - _gen1AtStart;

    public int Gen2Collections => GC.CollectionCount(2) - _gen2AtStart;

    /// <summary>Starts the wall clock. Called immediately after the first prime, i.e. at the moment the queue is as full as it will ever be.</summary>
    public void Start()
    {
        _gcPauseAtStart = GC.GetTotalPauseDuration();
        _gen0AtStart = GC.CollectionCount(0);
        _gen1AtStart = GC.CollectionCount(1);
        _gen2AtStart = GC.CollectionCount(2);
        _clock.Start();
    }

    /// <summary>Audio submitted minus DAC time, in ms — see the type remarks. <paramref name="framesSubmitted"/> is the pump's running frame index.</summary>
    public double SlackMs(long framesSubmitted) =>
        ((framesSubmitted / sampleRateHz) - _clock.Elapsed.TotalSeconds) * 1000.0;

    /// <summary>
    /// Closes out one pump iteration. Every duration is a delta the loop already measured, so this
    /// does no timing of its own beyond reading the clock once for slack.
    /// </summary>
    public void EndIteration(
        long framesSubmitted,
        double iterationMs,
        double readWaitMs,
        double writeWaitMs,
        double submitMs,
        double registerMs,
        bool quiesced)
    {
        _iteration++;

        double slackMs = SlackMs(framesSubmitted);
        if (slackMs < _minSlackMs)
            _minSlackMs = slackMs;
        if (iterationMs > _worstIterationMs)
            _worstIterationMs = iterationMs;

        // The loop is paced by one chunk of RX, so anything beyond that is slack this iteration
        // burned. Sub-threshold overshoot is completion jitter and self-corrects on the next
        // iteration; if it ever stops self-correcting it still shows up in MinSlackMs.
        if (iterationMs - _chunkMs < LateIterationMs)
            return;

        Record(new PumpStall(
            _iteration,
            quiesced ? PumpStallCause.RegisterWrite : PumpStallCause.LoopLate,
            iterationMs,
            readWaitMs,
            writeWaitMs,
            submitMs,
            registerMs,
            slackMs,
            (GC.GetTotalPauseDuration() - _gcPauseAtStart).TotalMilliseconds,
            GC.CollectionCount(0) - _gen0AtStart,
            GC.CollectionCount(1) - _gen1AtStart,
            GC.CollectionCount(2) - _gen2AtStart,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            MissingBytes: 0));
    }

    /// <summary>
    /// A bulk TX transfer that moved fewer bytes than it was handed. LibUsbDotNet reports a partial
    /// transfer as a length, not an error (a timeout is explicitly not fatal — see
    /// <see cref="Qa40xTransport.WriteDataAsync"/>), so discarding that value hides frames that never
    /// reached the DAC behind a completely healthy-looking loop.
    /// </summary>
    public void RecordShortWrite(long framesSubmitted, int expectedBytes, int actualBytes)
    {
        ShortWrites++;
        ShortWriteBytes += expectedBytes - actualBytes;

        Record(new PumpStall(
            _iteration,
            PumpStallCause.ShortWrite,
            IterationMs: 0,
            ReadWaitMs: 0,
            WriteWaitMs: 0,
            SubmitMs: 0,
            RegisterMs: 0,
            SlackMs(framesSubmitted),
            (GC.GetTotalPauseDuration() - _gcPauseAtStart).TotalMilliseconds,
            GC.CollectionCount(0) - _gen0AtStart,
            GC.CollectionCount(1) - _gen1AtStart,
            GC.CollectionCount(2) - _gen2AtStart,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            expectedBytes - actualBytes));
    }

    /// <summary>
    /// A bulk RX transfer that came back with fewer bytes than were asked for.
    ///
    /// <para>The mirror of <see cref="RecordShortWrite"/>, and it was the asymmetry that hid it: the
    /// TX side has always checked its returned length, while the RX side passed
    /// <c>transferLength</c> straight through to the assembler, which appends exactly what it is
    /// given. So a short read is a hole in the sample stream that nothing counts —
    /// <see cref="Qa40xUsbDevice.DroppedRxChunks"/> counts only chunks the consumer was too slow to
    /// take, and the block sequence numbers stay contiguous across it. Per
    /// <c>SampleRingBuffer</c>'s contract that hole splices the next FFT record, lifting the
    /// broadband floor for exactly one record.</para>
    ///
    /// <para>Counted rather than acted on. A short read is not known to occur in normal operation on
    /// this device, so the first thing needed is to find out whether it does at all; treating one as
    /// a discontinuity and flushing on it would, if they turn out to be routine, freeze the display
    /// instead of showing a glitch.</para>
    /// </summary>
    public void RecordShortRead(long framesSubmitted, int expectedBytes, int actualBytes)
    {
        ShortReads++;
        ShortReadBytes += expectedBytes - actualBytes;

        Record(new PumpStall(
            _iteration,
            PumpStallCause.ShortRead,
            IterationMs: 0,
            ReadWaitMs: 0,
            WriteWaitMs: 0,
            SubmitMs: 0,
            RegisterMs: 0,
            SlackMs(framesSubmitted),
            (GC.GetTotalPauseDuration() - _gcPauseAtStart).TotalMilliseconds,
            GC.CollectionCount(0) - _gen0AtStart,
            GC.CollectionCount(1) - _gen1AtStart,
            GC.CollectionCount(2) - _gen2AtStart,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            expectedBytes - actualBytes));
    }

    /// <summary>
    /// Appends one event. A ring, deliberately: this used to stop recording entirely once
    /// <see cref="Capacity"/> events had been seen in a stream, so a long run went blind after its
    /// first 128 — measured on a 12-minute run that logged its last starvation event 2 minutes in
    /// and then reported "237 further events not logged", which is precisely the window an
    /// intermittent artifact lands in. Recent events are the ones worth keeping; old ones are
    /// dropped and counted instead.
    /// </summary>
    private void Record(in PumpStall stall)
    {
        long written = _written;
        _stalls[(int)(written % Capacity)] = stall;

        // Published last, with a release write, so a consumer that sees the new count is guaranteed
        // to see the fully-written struct behind it. Only the pump thread ever writes here.
        Volatile.Write(ref _written, written + 1);
    }

    /// <summary>
    /// Hands the consumer every event recorded since its last call. Called from the chunk loop in
    /// <c>StreamAsync</c>, which is off the pump thread — logging is I/O and must never happen
    /// between one transfer completing and the next being submitted.
    /// </summary>
    public bool TryDrain(out ReadOnlySpan<PumpStall> stalls)
    {
        long written = Volatile.Read(ref _written);
        if (written == _drained)
        {
            stalls = default;
            return false;
        }

        // Whatever the ring has already overwritten since the last drain is gone; count it rather
        // than pretend. The consumer drains once per assembled block (~21 ms at 192 kHz), so losing
        // anything here needs 128 events inside one block — a storm, and the count says so.
        long from = Math.Max(_drained, written - Capacity);
        _suppressed += from - _drained;

        // Copied out rather than handed over as a span into the live ring: the pump keeps writing
        // while the consumer formats these. A very long stall burst can still tear the oldest entry
        // of a drain, which for a diagnostic is a fair trade against the blind spot above.
        int count = (int)(written - from);
        for (int i = 0; i < count; i++)
            _drainBuffer[i] = _stalls[(int)((from + i) % Capacity)];

        _drained = written;
        stalls = _drainBuffer.AsSpan(0, count);
        return true;
    }
}
