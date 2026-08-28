using System.Diagnostics;

namespace Qa403.Device.Hardware;

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
    int ShortWriteBytes);

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
    /// <summary>Events retained per stream. Past this only the counters advance — 128 is far more than a diagnosis needs, and it bounds both memory and log spam on a long run.</summary>
    private const int Capacity = 128;

    /// <summary>An iteration is recorded once it burns this much slack beyond its own chunk period. Below a few ms it is transfer-completion jitter, not a threat to a 64 ms queue.</summary>
    private const double LateIterationMs = 4.0;

    private readonly PumpStall[] _stalls = new PumpStall[Capacity];
    private readonly double _chunkMs = chunkFrames * 1000.0 / sampleRateHz;
    private readonly Stopwatch _clock = new();

    private int _recorded;
    private int _drained;
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
            ShortWriteBytes: 0));
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

    private void Record(in PumpStall stall)
    {
        if (_recorded >= Capacity)
        {
            _suppressed++;
            return;
        }

        _stalls[_recorded] = stall;

        // Published last, with a release write, so a consumer that sees the new count is guaranteed
        // to see the fully-written struct behind it. Only the pump thread ever writes here.
        Volatile.Write(ref _recorded, _recorded + 1);
    }

    /// <summary>
    /// Hands the consumer every event recorded since its last call. Called from the chunk loop in
    /// <c>StreamAsync</c>, which is off the pump thread — logging is I/O and must never happen
    /// between one transfer completing and the next being submitted.
    /// </summary>
    public bool TryDrain(out ReadOnlySpan<PumpStall> stalls)
    {
        int recorded = Volatile.Read(ref _recorded);
        if (recorded == _drained)
        {
            stalls = default;
            return false;
        }

        stalls = _stalls.AsSpan(_drained, recorded - _drained);
        _drained = recorded;
        return true;
    }
}
