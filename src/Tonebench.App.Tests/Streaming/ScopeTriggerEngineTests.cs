using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The arm / trigger / fill / hold cycle. The two properties worth most here are the ones a search
/// inside an already-captured window could not have at all: the record carries samples from
/// <i>before</i> the event, and a record that spans a hole in the stream is thrown away rather than
/// drawn.
/// </summary>
public class ScopeTriggerEngineTests
{
    private const double SampleRateHz = 192_000.0;
    private const int BlockFrames = 4096;

    /// <summary>A square wave on both channels; the right channel is the left plus a constant.</summary>
    private static float[] Square(long firstIndex, int frames, int halfPeriod)
    {
        var block = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            long i = firstIndex + f;
            float v = (i / halfPeriod) % 2 == 0 ? -1f : 1f;
            block[f * 2] = v;
            block[f * 2 + 1] = v + 10f;
        }

        return block;
    }

    private static ScopeCaptureSpec Spec(
        int recordFrames = 2048,
        int preTriggerFrames = 1024,
        ScopeRunMode mode = ScopeRunMode.Single,
        long autoTimeoutFrames = 0,
        long discontinuityHoldoffFrames = 0) =>
        new(
            TriggerSpec.Edge(0.0),
            TriggerChannel: 0,
            RecordFrames: recordFrames,
            PreTriggerFrames: preTriggerFrames,
            Mode: mode,
            SampleRateHz: SampleRateHz,
            AutoTimeoutFrames: autoTimeoutFrames,
            DiscontinuityHoldoffFrames: discontinuityHoldoffFrames);

    private static (ScopeCaptureBuffer Buffer, ScopeTriggerEngine Engine) NewEngine()
    {
        var buffer = new ScopeCaptureBuffer(2, 8192);
        return (buffer, new ScopeTriggerEngine(buffer));
    }

    /// <summary>Feeds whole blocks of a square wave, returning every record the engine commits.</summary>
    private static List<ScopeRecord> Feed(
        ScopeTriggerEngine engine, int blocks, int halfPeriod = 512, long firstIndex = 0, long dropped = 0)
    {
        var records = new List<ScopeRecord>();
        for (int b = 0; b < blocks; b++)
        {
            long at = firstIndex + (long)b * BlockFrames;
            engine.Write(Square(at, BlockFrames, halfPeriod), default, startsAfterDiscontinuity: false, dropped);
            if (engine.TakeRecord() is { } record)
                records.Add(record);
        }

        return records;
    }

    [Fact]
    public void ARecordCarriesTheSamplesFromBeforeItsTrigger()
    {
        // The property the whole design exists for. The square is -1 before each rising edge and
        // +1 after, so a correctly placed record is negative throughout its pre-trigger half and
        // positive at the trigger.
        // 256 frames of pre-trigger against a 512-frame half period, so the whole pre-trigger
        // window sits inside the low half and any misplacement shows as a sign.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 2048, preTriggerFrames: 256), droppedTotal: 0);

        var records = Feed(engine, blocks: 3);

        var record = Assert.Single(records);
        Assert.Equal(2048, record.FrameCount);
        Assert.Equal(2, record.ChannelCount);

        Assert.Equal(-1.0, record.Channels[0][0], 6);
        Assert.Equal(-1.0, record.Channels[0][255], 6);
        Assert.Equal(1.0, record.Channels[0][256], 6);

        // t = 0 is the trigger, and everything before it is negative in time.
        Assert.True(record.TimeAt(0) < 0.0);
        Assert.Equal(-255.5 / SampleRateHz, record.TimeAt(0), 9);
    }

    [Fact]
    public void TheTriggerSitsBetweenTwoSamples_NotOnOne()
    {
        // A step from -1 to +1 crosses zero half way between the two samples that straddle it, and
        // that half sample is carried into the record rather than rounded away. So the frame the
        // record is anchored on is within half a sample of t = 0, and never further.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 2048, preTriggerFrames: 256), droppedTotal: 0);

        var record = Assert.Single(Feed(engine, blocks: 3));

        Assert.Equal(0.5 / SampleRateHz, record.TimeAt(256), 12);
        Assert.InRange(Math.Abs(record.TimeAt(256)), 0.0, 0.5 / SampleRateHz);
        Assert.Equal(255.5 / SampleRateHz, record.TriggerOffsetS, 9);
    }

    [Fact]
    public void AllPreTrigger_EndsTheRecordAtTheTrigger()
    {
        // 100 % pre-trigger: everything drawn happened before the event. Free here because the
        // buffer already holds the history; on a scope that captures forward from its trigger it
        // is not available at all.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 1024, preTriggerFrames: 1024), droppedTotal: 0);

        var record = Assert.Single(Feed(engine, blocks: 3));

        Assert.True(record.TimeAt(record.FrameCount - 1) <= 0.0);
        Assert.Equal(-1.0, record.Channels[0][^1], 6);
    }

    [Fact]
    public void NoPreTrigger_StartsTheRecordAtTheTrigger()
    {
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 1024, preTriggerFrames: 0), droppedTotal: 0);

        var record = Assert.Single(Feed(engine, blocks: 3));

        // Anchored on the first sample at or above the level; t = 0 is the interpolated crossing
        // half a sample before it.
        Assert.Equal(1.0, record.Channels[0][0], 6);
        Assert.Equal(0.5 / SampleRateHz, record.TimeAt(0), 12);
    }

    [Fact]
    public void Single_DeliversOnceAndHolds()
    {
        var (_, engine) = NewEngine();
        engine.Arm(Spec(mode: ScopeRunMode.Single), droppedTotal: 0);

        var records = Feed(engine, blocks: 8);

        Assert.Single(records);
        Assert.Equal(ScopeEngineState.Held, engine.State);
    }

    [Fact]
    public void Normal_KeepsDelivering()
    {
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 1024, preTriggerFrames: 512, mode: ScopeRunMode.Normal), droppedTotal: 0);

        var records = Feed(engine, blocks: 8);

        Assert.True(records.Count >= 4, $"expected a record most blocks, got {records.Count}");
        Assert.Equal(ScopeEngineState.Waiting, engine.State);
        foreach (var record in records)
            Assert.False(record.Forced);
    }

    [Fact]
    public void ARecordIsNeverHandedOverTwice()
    {
        // A consumer polling TakeRecord must not be able to draw one capture twice and count it as
        // two, which is what a statistics accumulator would do with it.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(), droppedTotal: 0);
        Feed(engine, blocks: 3);

        Assert.Null(engine.TakeRecord());
    }

    [Fact]
    public void Auto_TakesARecordWhenNothingQualifies_AndSaysSo()
    {
        // A level nothing reaches. Without the timeout the screen stays blank and a mis-set level
        // is indistinguishable from a dead input; with it, the flag is what stops the forced trace
        // passing for a triggered one.
        var (_, engine) = NewEngine();
        var spec = Spec(recordFrames: 2048, preTriggerFrames: 1024, mode: ScopeRunMode.Auto, autoTimeoutFrames: 4096)
            with { Trigger = TriggerSpec.Edge(5.0) };
        engine.Arm(spec, droppedTotal: 0);

        var records = Feed(engine, blocks: 6);

        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.True(r.Forced));
    }

    [Fact]
    public void Auto_PrefersARealTriggerOverItsTimeout()
    {
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 1024, preTriggerFrames: 512, mode: ScopeRunMode.Auto, autoTimeoutFrames: 4096), droppedTotal: 0);

        var records = Feed(engine, blocks: 6);

        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.False(r.Forced));
    }

    [Fact]
    public void ABreakWhileTheRecordIsFilling_ThrowsTheRecordAway()
    {
        // The two halves either side of an input-range step were never adjacent, so a record drawn
        // across them shows a step the DUT never made.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 4096, preTriggerFrames: 2048), droppedTotal: 0);

        // One block to trigger in, then the break lands before the record is whole.
        engine.Write(Square(0, BlockFrames, 512), default, startsAfterDiscontinuity: false, 0);
        Assert.Null(engine.TakeRecord());
        Assert.Equal(ScopeEngineState.Filling, engine.State);

        engine.Write(Square(BlockFrames, BlockFrames, 512), default, startsAfterDiscontinuity: true, 0);

        Assert.Null(engine.TakeRecord());
        Assert.Equal(ScopeEngineState.Waiting, engine.State);
    }

    [Fact]
    public void AfterABreak_NothingIsTriggeredUntilTheHoldoffHasPassed()
    {
        // The 24 dB attenuator release: ~1.05 s of samples reading exactly 24 dB low, of which
        // ONLY THE FIRST block carries the discontinuity flag. The flag bounds the splice; this
        // holdoff is what bounds the wrong levels after it.
        var (_, engine) = NewEngine();
        engine.Arm(
            Spec(recordFrames: 1024, preTriggerFrames: 512, mode: ScopeRunMode.Normal, discontinuityHoldoffFrames: 3 * BlockFrames),
            droppedTotal: 0);

        engine.Write(Square(0, BlockFrames, 512), default, startsAfterDiscontinuity: true, 0);
        var during = new List<ScopeRecord>();
        for (int b = 1; b <= 3; b++)
        {
            engine.Write(Square((long)b * BlockFrames, BlockFrames, 512), default, startsAfterDiscontinuity: false, 0);
            if (engine.TakeRecord() is { } r)
                during.Add(r);
        }

        Assert.Empty(during);

        var after = Feed(engine, blocks: 3, firstIndex: 4L * BlockFrames);
        Assert.NotEmpty(after);
    }

    [Fact]
    public void ADroppedBlockDuringTheRecord_MarksItSpliced()
    {
        // Counted by movement, never by the absolute value -- the device's counters are
        // per-acquisition and a stale total would read exactly like a fresh fault.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 4096, preTriggerFrames: 2048), droppedTotal: 7);

        engine.Write(Square(0, BlockFrames, 512), default, startsAfterDiscontinuity: false, 7);
        engine.Write(Square(BlockFrames, BlockFrames, 512), default, startsAfterDiscontinuity: false, 9);

        var record = engine.TakeRecord();
        Assert.NotNull(record);
        Assert.True(record!.Spliced);
    }

    [Fact]
    public void AnUndisturbedRecord_IsNotMarkedSpliced()
    {
        var (_, engine) = NewEngine();
        engine.Arm(Spec(), droppedTotal: 7);

        var record = Assert.Single(Feed(engine, blocks: 3, dropped: 7));

        Assert.False(record.Spliced);
        Assert.False(record.Forced);
    }

    [Fact]
    public void BothChannelsComeFromTheSameInstant()
    {
        // The right channel is the left plus ten, so any slip between them shows as that constant
        // changing -- the set rule carried all the way through to a committed record.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(), droppedTotal: 0);

        var record = Assert.Single(Feed(engine, blocks: 3));

        for (int i = 0; i < record.FrameCount; i++)
            Assert.Equal(10.0, record.Channels[1][i] - record.Channels[0][i], 5);
    }

    [Fact]
    public void TheSamplesAreQuantisedToWhatWillBeStored()
    {
        // What is drawn and measured must be what lands in capture.pcm, or re-opening a saved
        // result shows a different Vpp for no reason a user could see.
        var (buffer, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 1024, preTriggerFrames: 512), droppedTotal: 0);

        // A value with more precision than f32 can hold, on a signal that still triggers.
        var block = new float[BlockFrames * 2];
        for (int f = 0; f < BlockFrames; f++)
        {
            float v = (f / 512) % 2 == 0 ? -1f : 1f;
            block[f * 2] = v;
            block[f * 2 + 1] = v;
        }

        double[] scale = [1.0 / 3.0, 1.0 / 3.0];
        engine.Write(block, scale, startsAfterDiscontinuity: false, 0);
        engine.Write(block, scale, startsAfterDiscontinuity: false, 0);

        var record = engine.TakeRecord();
        Assert.NotNull(record);
        foreach (double v in record!.Channels[0])
            Assert.Equal((double)(float)v, v, 15);

        Assert.True(buffer.Newest > 0);
    }

    [Fact]
    public void Disarming_StopsCapturing()
    {
        var (_, engine) = NewEngine();
        engine.Arm(Spec(mode: ScopeRunMode.Normal), droppedTotal: 0);
        engine.Disarm();

        Assert.Equal(ScopeEngineState.Idle, engine.State);
        Assert.Empty(Feed(engine, blocks: 4));
    }

    [Fact]
    public void Roll_NeverArms()
    {
        var (_, engine) = NewEngine();
        engine.Arm(Spec(mode: ScopeRunMode.Roll), droppedTotal: 0);

        Assert.Equal(ScopeEngineState.Idle, engine.State);
        Assert.Empty(Feed(engine, blocks: 4));
    }

    [Fact]
    public void AnUnimplementedTriggerKind_IsRefusedAtArmingRatherThanIgnored()
    {
        var (_, engine) = NewEngine();
        var spec = Spec() with { Trigger = TriggerSpec.Edge(0.0) with { Kind = TriggerKind.Dropout } };

        Assert.Throws<NotSupportedException>(() => engine.Arm(spec, droppedTotal: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2049)]
    public void ANonsensePreTrigger_IsRefused(int preTriggerFrames)
    {
        var (_, engine) = NewEngine();

        if (preTriggerFrames is 0 or 1)
        {
            // Legal: a record may be entirely post-trigger.
            engine.Arm(Spec(recordFrames: 2048, preTriggerFrames: preTriggerFrames), droppedTotal: 0);
            return;
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => engine.Arm(Spec(recordFrames: 2048, preTriggerFrames: preTriggerFrames), droppedTotal: 0));
    }
}
