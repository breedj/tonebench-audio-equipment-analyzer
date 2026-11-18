using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// Roll: no trigger at all, and the newest window is the record.
///
/// <para><b>The first case is the regression this class exists for.</b> The mode was in the enum,
/// offered in the picker and accepted by the runner, and the engine answered it by going
/// <see cref="ScopeEngineState.Idle"/> — where nothing scans and nothing fills — so a Roll run
/// committed no record ever and the screen stayed blank for as long as it was left running.</para>
///
/// <para>The rest are about the two things that make Roll different from a triggered run rather
/// than merely untriggered: the window is built <b>when it is asked for</b>, so a display nobody is
/// looking at costs nothing; and <b>t = 0 is its right-hand edge</b>, so the record runs backwards
/// from "now" rather than being placed against an event that never happened.</para>
/// </summary>
public class ScopeRollModeTests
{
    private const double SampleRateHz = 192_000.0;
    private const int BlockFrames = 4096;

    /// <summary>A ramp, so every sample says which absolute index it came from.</summary>
    private static float[] Ramp(long firstIndex, int frames)
    {
        var block = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            block[f * 2] = firstIndex + f;
            block[f * 2 + 1] = -(firstIndex + f);
        }

        return block;
    }

    private static ScopeCaptureSpec Spec(int recordFrames = 2048, long rollStepFrames = 512) =>
        new(
            TriggerSpec.Edge(0.0),
            TriggerChannel: 0,
            RecordFrames: recordFrames,
            // The whole record, which is what OscilloscopeRequest.PreTriggerFramesAt hands Roll:
            // every sample is older than "now".
            PreTriggerFrames: recordFrames,
            Mode: ScopeRunMode.Roll,
            SampleRateHz: SampleRateHz,
            RollStepFrames: rollStepFrames);

    private static (ScopeCaptureBuffer Buffer, ScopeTriggerEngine Engine) NewEngine()
    {
        var buffer = new ScopeCaptureBuffer(2, 8192);
        return (buffer, new ScopeTriggerEngine(buffer));
    }

    private static void Feed(ScopeTriggerEngine engine, int blocks, long firstIndex = 0)
    {
        for (int b = 0; b < blocks; b++)
        {
            long at = firstIndex + ((long)b * BlockFrames);
            engine.Write(Ramp(at, BlockFrames), default, startsAfterDiscontinuity: false, droppedTotal: 0);
        }
    }

    [Fact]
    public void ARollingRunHasAWindowToDraw()
    {
        var (_, engine) = NewEngine();
        engine.Arm(Spec(), droppedTotal: 0);

        Assert.Equal(ScopeEngineState.Rolling, engine.State);

        Feed(engine, blocks: 2);

        Assert.True(engine.RollWindowReady);
        var record = engine.NewestWindow();

        Assert.NotNull(record);
        Assert.Equal(2048, record!.FrameCount);
        Assert.True(record.Rolled);
    }

    [Fact]
    public void NothingIsCommitted_SoTheRecordCostsOnlyWhatIsAskedFor()
    {
        // The hand-off a triggered run uses stays empty: Roll's cost is one copy per read, on
        // whichever thread reads it, rather than one per n milliseconds on the acquisition thread
        // whether or not anybody is looking.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(), droppedTotal: 0);

        Feed(engine, blocks: 4);

        Assert.Null(engine.TakeRecord());
    }

    [Fact]
    public void TheWindowIsTheNewestSamples_WithTZeroAtItsRightHandEdge()
    {
        var (buffer, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 1024), droppedTotal: 0);

        Feed(engine, blocks: 2);
        long newest = buffer.Newest;

        var record = engine.NewestWindow();

        Assert.NotNull(record);
        // The ramp names its own index, so this is what says the window really is the newest 1024
        // frames rather than the oldest or some window in between.
        Assert.Equal(newest - 1024, record!.FirstIndex);
        Assert.Equal(newest - 1024, record.Channels[0][0], 6);
        Assert.Equal(newest - 1, record.Channels[0][1023], 6);

        // t = 0 at the right-hand edge: the whole record is to its left and the axis goes negative,
        // which is this app's convention wherever a time is measured from an event.
        Assert.Equal(1024 / SampleRateHz, record.TriggerOffsetS, 12);
        Assert.Equal(-1024 / SampleRateHz, record.Span.FromS, 12);
        Assert.True(record.Span.ToS <= 0.0);
    }

    [Fact]
    public void AWindowIsRefusedUntilAWholeRecordOfHistoryExists()
    {
        // Rather than served short: a record shorter than the one asked for would draw at the
        // wrong timebase, and the first moments of a run are exactly when somebody is watching.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 8192), droppedTotal: 0);

        Feed(engine, blocks: 1);
        Assert.False(engine.RollWindowReady);
        Assert.Null(engine.NewestWindow());

        Feed(engine, blocks: 1, firstIndex: BlockFrames);
        Feed(engine, blocks: 1, firstIndex: 2L * BlockFrames);
        Assert.True(engine.RollWindowReady);
        Assert.NotNull(engine.NewestWindow());
    }

    [Fact]
    public void ABreakShortensTheHistoryRatherThanSplicingTheWindow()
    {
        // A rolled record is never spliced by construction: the buffer refuses a window that would
        // cross a break, so a drop costs history rather than putting two non-adjacent pieces of
        // the stream side by side.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 4096), droppedTotal: 0);

        Feed(engine, blocks: 2);
        Assert.NotNull(engine.NewestWindow());

        engine.Write(Ramp(2L * BlockFrames, BlockFrames), default, startsAfterDiscontinuity: true, droppedTotal: 7);
        Assert.Null(engine.NewestWindow());

        Feed(engine, blocks: 2, firstIndex: 3L * BlockFrames);
        var record = engine.NewestWindow();
        Assert.NotNull(record);
        Assert.False(record!.Spliced);
    }

    [Fact]
    public void TheStepCounterTicksOnTheBuffersOwnClock()
    {
        // What a client watches to know there is a newer window, in place of the record counter a
        // triggered run advances. Arithmetic, so it costs nothing and allocates nothing.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(rollStepFrames: BlockFrames), droppedTotal: 0);

        Assert.Equal(0, engine.RollWindowsElapsed);

        Feed(engine, blocks: 1);
        Assert.Equal(1, engine.RollWindowsElapsed);

        Feed(engine, blocks: 2, firstIndex: BlockFrames);
        Assert.Equal(3, engine.RollWindowsElapsed);
    }

    [Fact]
    public void ATriggeredRunRollsNothing()
    {
        // The three Roll members answer for Roll and for nothing else, so a mode switch cannot
        // leave a triggered run serving windows beside its records.
        var (_, engine) = NewEngine();
        engine.Arm(Spec() with { Mode = ScopeRunMode.Normal, PreTriggerFrames = 1024 }, droppedTotal: 0);

        Feed(engine, blocks: 3);

        Assert.Equal(0, engine.RollWindowsElapsed);
        Assert.False(engine.RollWindowReady);
        Assert.Null(engine.NewestWindow());
    }

    [Fact]
    public void DisarmingStopsTheRoll()
    {
        var (_, engine) = NewEngine();
        engine.Arm(Spec(), droppedTotal: 0);
        Feed(engine, blocks: 2);
        Assert.NotNull(engine.NewestWindow());

        engine.Disarm();

        Assert.Equal(ScopeEngineState.Idle, engine.State);
        Assert.Null(engine.NewestWindow());
        Assert.False(engine.RollWindowReady);
        Assert.Equal(0, engine.RollWindowsElapsed);
    }

    [Fact]
    public void TheSamplesAreQuantisedLikeAnyOtherRecord()
    {
        // What is stored is what is measured. A rolling reading and the same capture saved have to
        // be readings of the same numbers, which is why this path quantises too.
        var (_, engine) = NewEngine();
        engine.Arm(Spec(recordFrames: 1024), droppedTotal: 0);

        var block = new float[BlockFrames * 2];
        for (int f = 0; f < BlockFrames; f++)
        {
            block[f * 2] = 0.1f;
            block[f * 2 + 1] = 0.1f;
        }

        engine.Write(block, default, startsAfterDiscontinuity: false, droppedTotal: 0);

        var record = engine.NewestWindow();

        Assert.NotNull(record);
        // 0.1 as a float is not 0.1 as a double, and the stored waveform is the float.
        Assert.Equal((double)0.1f, record!.Channels[0][0]);
    }
}
