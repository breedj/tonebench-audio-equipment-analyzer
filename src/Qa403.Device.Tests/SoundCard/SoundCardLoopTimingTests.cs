using Qa403.Device.SoundCard;

namespace Qa403.Device.Tests.SoundCard;

/// <summary>
/// The phase split behind a sound-card glitch line. Pure arithmetic over a stopwatch, so all of it
/// runs without a card — which is the point of it being its own type: the thing it answers for (a
/// 256.1 ms iteration with 0.4 ms of GC in it, measured over 50 loaded sweeps on 2026-10-04) cannot
/// be reproduced on demand, so the reporting has to be right before the next one happens.
///
/// <para>The segment assertions are deliberately loose — a sleep is a lower bound on a loaded
/// machine, never an equality — and what they pin is which segment carries the time, which is the
/// whole question the split exists to answer.</para>
/// </summary>
public class SoundCardLoopTimingTests
{
    /// <summary>One transfer at 192 kHz, the rate every stall here has been measured at.</summary>
    private const double TransferMs = 1024 * 1000.0 / 192_000.0;

    private const int StallMs = 60;

    [Fact]
    public void TheThreeSegments_TileTheWindowTheyDescribe()
    {
        var timing = new SoundCardLoopTiming(TransferMs);
        timing.ReadReturned(1);

        Thread.Sleep(20);
        timing.WorkDone();
        Thread.Sleep(20);
        timing.WriteDone();
        Thread.Sleep(20);
        timing.ReadReturned(2);

        SoundCardLoopWindow w = timing.Last;
        double sum = w.ReadWaitMs + w.WorkMs + w.WriteWaitMs;

        // Nothing may fall between the segments: a gap is somewhere a stall could hide, which is
        // the fault this type was written to end.
        Assert.Equal(w.TotalMs, sum, 2.0);
        Assert.Equal(2, w.Block);
    }

    [Fact]
    public void AStallInOurOwnWork_IsNotBlamedOnTheDriver()
    {
        var timing = new SoundCardLoopTiming(TransferMs);
        timing.ReadReturned(1);

        Thread.Sleep(StallMs);
        timing.WorkDone();
        timing.WriteDone();
        timing.ReadReturned(2);

        SoundCardLoopWindow w = timing.Last;
        Assert.True(w.WorkMs >= StallMs * 0.5, $"our work {w.WorkMs:0.0} ms");
        Assert.True(w.ReadWaitMs < StallMs * 0.5, $"read wait {w.ReadWaitMs:0.0} ms");
        Assert.True(w.WriteWaitMs < StallMs * 0.5, $"write wait {w.WriteWaitMs:0.0} ms");
    }

    [Fact]
    public void AStallWaitingForTheDriver_LandsOnReadWaitAndNotOnOurWork()
    {
        var timing = new SoundCardLoopTiming(TransferMs);
        timing.ReadReturned(1);

        timing.WorkDone();
        timing.WriteDone();
        Thread.Sleep(StallMs);
        timing.ReadReturned(2);

        SoundCardLoopWindow w = timing.Last;
        Assert.True(w.ReadWaitMs >= StallMs * 0.5, $"read wait {w.ReadWaitMs:0.0} ms");
        Assert.True(w.WorkMs < StallMs * 0.5, $"our work {w.WorkMs:0.0} ms");
        Assert.True(w.WriteWaitMs < StallMs * 0.5, $"write wait {w.WriteWaitMs:0.0} ms");
    }

    [Fact]
    public void AStallBlockedOnTheOutputRing_LandsOnWriteWait()
    {
        var timing = new SoundCardLoopTiming(TransferMs);
        timing.ReadReturned(1);

        timing.WorkDone();
        Thread.Sleep(StallMs);
        timing.WriteDone();
        timing.ReadReturned(2);

        SoundCardLoopWindow w = timing.Last;
        Assert.True(w.WriteWaitMs >= StallMs * 0.5, $"write wait {w.WriteWaitMs:0.0} ms");
        Assert.True(w.WorkMs < StallMs * 0.5, $"our work {w.WorkMs:0.0} ms");
    }

    [Fact]
    public void AWindowInsideItsOwnTransferPeriod_IsNotWorthAQueueReading()
    {
        // A second-long transfer period rather than the real 5.3 ms: what is under test is the
        // threshold, and a tight 5 ms bound would make the test itself a scheduling race.
        var timing = new SoundCardLoopTiming(1000.0);
        timing.ReadReturned(1);

        // No sleep at all: a window that cost microseconds cannot be the one to spend two native
        // calls on, and the common path has to pay nothing.
        Assert.False(timing.ReadReturned(2));
        Assert.False(timing.Last.HasQueueReading);
        Assert.Equal(string.Empty, timing.Last.DescribeQueue());
    }

    [Fact]
    public void AWindowThatRanLate_AsksForOne()
    {
        var timing = new SoundCardLoopTiming(TransferMs);
        timing.ReadReturned(1);

        Thread.Sleep((int)Math.Ceiling(TransferMs + SoundCardLoopTiming.LateIterationMs) + 10);

        Assert.True(timing.ReadReturned(2));
    }

    [Fact]
    public void TheQueueReading_JoinsTheWindowItWasTakenFor_AndTheWorstOneToo()
    {
        var timing = new SoundCardLoopTiming(TransferMs);
        timing.ReadReturned(1);

        Thread.Sleep(StallMs);
        Assert.True(timing.ReadReturned(2));
        timing.NoteQueue(12288, 30208);

        Assert.Equal(12288, timing.Last.FramesWaitingToRead);
        Assert.Equal(30208, timing.Last.FramesOfOutputRoom);

        // The worst window is the one a margin line prints, so it has to carry the reading or that
        // line describes a stall it cannot explain.
        Assert.Equal(2, timing.Worst.Block);
        Assert.Equal(12288, timing.Worst.FramesWaitingToRead);
        Assert.Contains("12288 frames were waiting to be read", timing.Worst.DescribeQueue());
        Assert.Contains("30208 frames of the output ring were free", timing.Worst.DescribeQueue());
    }

    [Fact]
    public void AFrameCountTheDriverWouldNotGive_PrintsAsUnknownRatherThanAsMinusOne()
    {
        var timing = new SoundCardLoopTiming(TransferMs);
        timing.ReadReturned(1);
        Thread.Sleep(StallMs);
        timing.ReadReturned(2);
        timing.NoteQueue(-1, 30208);

        Assert.Contains("an unknown number of frames were waiting", timing.Worst.DescribeQueue());
        Assert.Contains("30208 frames of the output ring were free", timing.Worst.DescribeQueue());
    }

    [Fact]
    public void TheStreamsSpinUp_IsNeverTheWorstWindow()
    {
        var timing = new SoundCardLoopTiming(TransferMs);

        // The first read waits hundreds of milliseconds for an ASIO driver's first buffer. Nothing
        // was at risk -- there is no audio to lose before block 0 -- and left in, it would stand as
        // the worst case for the life of the stream.
        Thread.Sleep(StallMs);
        timing.ReadReturned(0);

        Assert.Equal(0.0, timing.Worst.TotalMs);
        Assert.False(timing.Worst.HasQueueReading);

        timing.ReadReturned(1);
        Assert.Equal(1, timing.Worst.Block);
    }

    [Fact]
    public void TheWorstWindow_IsTheLongestOneAndKeepsItsOwnSplit()
    {
        var timing = new SoundCardLoopTiming(TransferMs);
        timing.ReadReturned(1);

        // Block 2: a long window, spent waiting for the driver.
        timing.WorkDone();
        timing.WriteDone();
        Thread.Sleep(StallMs);
        timing.ReadReturned(2);

        // Block 3: a short one, spent in our own work. It must not displace the worst, and its
        // split must not leak into it either.
        Thread.Sleep(5);
        timing.WorkDone();
        timing.WriteDone();
        timing.ReadReturned(3);

        Assert.Equal(2, timing.Worst.Block);
        Assert.True(timing.Worst.ReadWaitMs >= StallMs * 0.5, $"read wait {timing.Worst.ReadWaitMs:0.0} ms");
        Assert.True(timing.Worst.WorkMs < StallMs * 0.5, $"our work {timing.Worst.WorkMs:0.0} ms");
        Assert.Equal(3, timing.Last.Block);
    }
}
