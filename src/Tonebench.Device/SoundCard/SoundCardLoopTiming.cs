using System.Diagnostics;
using System.Globalization;

namespace Tonebench.Device.SoundCard;

/// <summary>
/// One trip round the transfer loop, split into the three segments it can spend time in, plus what
/// the driver's two rings held when the stall was noticed.
///
/// <para>Frame counts are <c>-1</c> when they were not sampled — they are read only for a window
/// that ran late, because reading them costs two native calls and the common path must pay
/// nothing.</para>
/// </summary>
internal readonly record struct SoundCardLoopWindow(
    long Block,
    double TotalMs,
    double ReadWaitMs,
    double WorkMs,
    double WriteWaitMs,
    double GcMs,

    /// <summary>Frames the driver already had waiting when the late read finally returned. Far more than one transfer means the data was waiting for <i>us</i>.</summary>
    long FramesWaitingToRead,

    /// <summary>Free space in the output ring at that moment. Approaching the whole ring means the DAC had drained everything queued — the underflow's own near-miss measure.</summary>
    long FramesOfOutputRoom)
{
    /// <summary>A window that has not happened yet, with both frame counts unsampled rather than zero.</summary>
    public static SoundCardLoopWindow None { get; } = new(0, 0.0, 0.0, 0.0, 0.0, 0.0, -1, -1);

    public bool HasQueueReading => FramesWaitingToRead >= 0 || FramesOfOutputRoom >= 0;

    /// <summary>
    /// The queue reading as a log suffix, or an empty string when there is none. Built only when a
    /// line is actually written: formatting is not allowed to happen once per transfer.
    /// </summary>
    public string DescribeQueue() => HasQueueReading
        ? string.Format(
            CultureInfo.InvariantCulture,
            ", {0} frames were waiting to be read and {1} frames of the output ring were free",
            Format(FramesWaitingToRead), Format(FramesOfOutputRoom))
        : string.Empty;

    private static string Format(long frames) =>
        frames < 0 ? "an unknown number of" : frames.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Where a sound-card stall went, which is the one thing the single iteration figure could not say.
///
/// <para><b>Why a split at all.</b> A glitch line that reports only how long the iteration took
/// leaves three very different faults looking identical, and they have three different fixes: time
/// in <b>read wait</b> is the driver not delivering or this thread not being scheduled to collect;
/// time in <b>our work</b> is calibration, synthesis or a full consumer channel, which is ours to
/// fix in managed code; time in <b>write wait</b> is output-ring backpressure, which would be
/// strange while the DAC is simultaneously starving. Measured 2026-10-04 over 50 loaded sweeps, the
/// one dropped chunk came from a 256.1 ms iteration with 0.4 ms of GC in it — so the residual is not
/// a collection, and the next question is which segment held it. This is the sound-card counterpart
/// of <c>TransferPumpDiagnostics</c>'s <c>(read wait, write wait, submit, register+prime)</c>
/// breakdown, which answers exactly that question for the QA403.</para>
///
/// <para><b>Why the frame counts.</b> Thread CPU time was the obvious second number and it cannot
/// separate the two cases that matter: a thread blocked inside the driver's read and a thread that
/// was never scheduled both burn no CPU. The driver's own rings can — if a late read returns with
/// several transfers already waiting, the data had been sitting there and the host was late; if it
/// returns with nothing behind it, the driver had only just produced it.</para>
///
/// <para><b>Cost.</b> Two <see cref="Stopwatch"/> reads per segment and one
/// <see cref="GC.GetTotalPauseDuration"/> per window, into a preallocated struct. No allocation and
/// no formatting on the loop thread unless a line is written — the detector must not become the
/// stall, the same rule <c>TransferPumpDiagnostics</c> is written to.</para>
/// </summary>
internal sealed class SoundCardLoopTiming(double transferMs)
{
    /// <summary>
    /// How far past its own transfer period a window has to run before it is worth a queue reading.
    /// Below a few milliseconds it is ordinary completion jitter, not a threat to a 64 ms prime —
    /// the same threshold, and the same reasoning, as <c>TransferPumpDiagnostics.LateIterationMs</c>.
    /// </summary>
    internal const double LateIterationMs = 4.0;

    private readonly Stopwatch _loop = Stopwatch.StartNew();
    private readonly Stopwatch _segment = Stopwatch.StartNew();

    private TimeSpan _lastGcPause = GC.GetTotalPauseDuration();
    private double _workMs;
    private double _writeWaitMs;

    /// <summary>The window that has just closed. Both glitches of one stall quote it, so they describe the same window.</summary>
    public SoundCardLoopWindow Last { get; private set; } = SoundCardLoopWindow.None;

    /// <summary>
    /// The longest window of the stream, kept whether or not anything glitched — which is the
    /// point. A run with no glitch says only that no margin was <i>exceeded</i>; this says how much
    /// of it was used, so "the fix works" can be a measurement rather than an absence.
    /// </summary>
    public SoundCardLoopWindow Worst { get; private set; } = SoundCardLoopWindow.None;

    /// <summary>Our own work is done and the write is about to be made.</summary>
    public void WorkDone() => _workMs = NextSegmentMs();

    /// <summary>The write has returned, however it returned — called from a <c>finally</c>, or a timed-out write folds into the next read wait.</summary>
    public void WriteDone() => _writeWaitMs = NextSegmentMs();

    /// <summary>
    /// Closes the window at the far end of a read, which is where a stall is noticed: an input
    /// overflow by the read that has just returned late, an output underflow by the write a few
    /// hundred microseconds later.
    /// </summary>
    /// <param name="block">
    /// The sequence number this window produced. Zero is excluded from <see cref="Worst"/>: the
    /// first iterations are the stream spinning up, where a read waits hundreds of milliseconds for
    /// the driver's first buffer and would stand as the worst case for ever — and nothing was at
    /// risk then, there being no audio yet to lose.
    /// </param>
    /// <returns>Whether the window ran late enough to be worth a queue reading.</returns>
    public bool ReadReturned(long block)
    {
        double totalMs = _loop.Elapsed.TotalMilliseconds;
        _loop.Restart();
        double readWaitMs = NextSegmentMs();

        TimeSpan pause = GC.GetTotalPauseDuration();
        double gcMs = (pause - _lastGcPause).TotalMilliseconds;
        _lastGcPause = pause;

        Last = new SoundCardLoopWindow(block, totalMs, readWaitMs, _workMs, _writeWaitMs, gcMs, -1, -1);
        _workMs = 0.0;
        _writeWaitMs = 0.0;

        if (block > 0 && totalMs > Worst.TotalMs)
            Worst = Last;

        return totalMs > transferMs + LateIterationMs;
    }

    /// <summary>
    /// What the two rings held, sampled by the caller after <see cref="ReadReturned"/> said the
    /// window was late. Separate from that call because the timing must not reach into the stream:
    /// the native calls belong to the caller, which is also what keeps this testable without one.
    /// </summary>
    public void NoteQueue(long framesWaitingToRead, long framesOfOutputRoom)
    {
        Last = Last with
        {
            FramesWaitingToRead = framesWaitingToRead,
            FramesOfOutputRoom = framesOfOutputRoom,
        };

        // The worst window is usually the one being sampled, and it has to carry the reading or the
        // margin line reports a stall it cannot describe.
        if (Last.Block > 0 && Worst.Block == Last.Block)
            Worst = Last;
    }

    /// <summary>Closes one segment and opens the next, so the three of them tile the window with no gap to lose a stall in.</summary>
    private double NextSegmentMs()
    {
        double ms = _segment.Elapsed.TotalMilliseconds;
        _segment.Restart();
        return ms;
    }
}
