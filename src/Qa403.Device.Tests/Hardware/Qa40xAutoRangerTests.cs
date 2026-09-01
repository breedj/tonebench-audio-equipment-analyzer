using Qa403.Device.Hardware;

namespace Qa403.Device.Tests.Hardware;

/// <summary>Pure state-machine tests, no transport/hardware involved.</summary>
public class Qa40xAutoRangerTests
{
    private static readonly double[] InputRangesDbv = [0, 6, 12, 18, 24, 30, 36, 42];

    // Chosen so 3 blocks (1200 ms) crosses the default 1000 ms StepDownDelayMs on the 3rd call,
    // matching the old 3-consecutive-block hysteresis this replaced — keeps the tests below reading
    // the same way while actually exercising the new elapsed-time accumulation.
    private const double BlockDurationMs = 400.0;

    [Fact]
    public void Evaluate_PeakAboveHeadroom_StepsUpOneRange()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);

        // Range 12's headroom boundary is 12 - 5 = 7 dBV; 8 dBV is just over it.
        var decision = ranger.Evaluate(currentRangeDbv: 12.0, peakDbv: 8.0, BlockDurationMs);

        Assert.Equal(RangeStepDecision.StepUp, decision);
        Assert.Equal(18.0, ranger.NextRangeDbv(12.0, decision));
    }

    [Fact]
    public void Evaluate_PeakAboveHeadroomAtTopRange_Holds()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);

        var decision = ranger.Evaluate(currentRangeDbv: 42.0, peakDbv: 100.0, BlockDurationMs);

        Assert.Equal(RangeStepDecision.Hold, decision);
        Assert.Equal(42.0, ranger.NextRangeDbv(42.0, decision));
    }

    [Fact]
    public void Evaluate_UnderRange_RequiresStepDownDelayBeforeSteppingDown()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        // Next range down is 6, whose headroom boundary is 6 - 5 = 1 dBV; well under that.
        const double lowPeakDbv = -10.0;

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs)); // 400 ms
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs)); // 800 ms
        var third = ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs); // 1200 ms >= 1000 ms default

        Assert.Equal(RangeStepDecision.StepDown, third);
        Assert.Equal(6.0, ranger.NextRangeDbv(12.0, third));
    }

    [Fact]
    public void Evaluate_UnderRangeAtBottomRange_NeverSteps()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        const double lowPeakDbv = -40.0;

        for (int i = 0; i < 5; i++)
            Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(0.0, lowPeakDbv, BlockDurationMs));
    }

    [Fact]
    public void Evaluate_ANormalLevelBlockResetsUnderRangeHysteresis()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        const double lowPeakDbv = -10.0;
        const double midPeakDbv = 4.0; // Between the 1 dBV step-down and 7 dBV step-up boundaries.

        ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs); // 400 ms
        ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs); // 800 ms
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, midPeakDbv, BlockDurationMs)); // resets the timer

        // Two more low blocks after the reset is only 800 ms elapsed, not yet the 1000 ms delay.
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs)); // 400 ms
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs)); // 800 ms
        Assert.Equal(RangeStepDecision.StepDown, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs)); // 1200 ms
    }

    [Fact]
    public void Reset_ClearsUnderRangeHysteresis()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        const double lowPeakDbv = -10.0;

        ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs);
        ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs);
        ranger.Reset();

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs));
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs));
    }

    [Fact]
    public void Evaluate_MidRangeSample_Holds()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        const double midPeakDbv = 4.0;

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, midPeakDbv, BlockDurationMs));
    }

    [Fact]
    public void StepDownDelayMs_DefaultsTo1000Ms()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);

        Assert.Equal(1000.0, ranger.StepDownDelayMs);
        Assert.Equal(1000.0, Qa40xAutoRanger.DefaultStepDownDelayMs);
    }

    [Fact]
    public void Evaluate_UnderRange_AccumulatesWallClockTimeRegardlessOfBlockCadence()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv) { StepDownDelayMs = 100.0 };
        const double lowPeakDbv = -10.0;

        // Many short blocks (e.g. a high sample rate's small chunk duration) still only step down
        // once their summed wall-clock time crosses StepDownDelayMs, not after a fixed block count.
        for (int i = 0; i < 9; i++)
            Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, blockDurationMs: 10.0));

        Assert.Equal(RangeStepDecision.StepDown, ranger.Evaluate(12.0, lowPeakDbv, blockDurationMs: 10.0));
    }

    [Fact]
    public void Evaluate_UnderRange_OneLongBlockCanCrossTheDelayImmediately()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv) { StepDownDelayMs = 100.0 };

        var decision = ranger.Evaluate(12.0, peakDbv: -10.0, blockDurationMs: 150.0);

        Assert.Equal(RangeStepDecision.StepDown, decision);
    }

    /// <summary>
    /// The regression test for the range hunt. With no level hysteresis the step-down threshold of
    /// range 6 and the step-up threshold of range 0 are the same number (0 - 5 = -5 dBV), so a peak
    /// parked between them stepped down, stepped straight back up, and repeated for as long as the
    /// signal was there — once per <see cref="Qa40xAutoRanger.StepDownDelayMs"/>. Every one of those
    /// steps quiesces EP2 and discards a queue depth of already-captured RX, so it was a ~110 ms
    /// hole punched in the sample stream every second, and each hole is one broadband smear on the
    /// display.
    /// </summary>
    [Fact]
    public void Evaluate_PeakInTheHysteresisBand_DoesNotOscillateBetweenTwoRanges()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);

        // Between range 0's step-up threshold (-5 dBV) and range 6's step-down threshold
        // (-5 - StepDownHysteresisDb): the level that used to hunt.
        const double peakDbv = -6.0;

        // Sitting on 6: never drops to 0, however long it stays under-range.
        for (int i = 0; i < 20; i++)
            Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(6.0, peakDbv, BlockDurationMs));

        // And sitting on 0 with the same signal: never climbs back to 6 either. Both directions
        // have to hold, or the deadband just moves the oscillation somewhere else.
        for (int i = 0; i < 20; i++)
            Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(0.0, peakDbv, BlockDurationMs));
    }

    /// <summary>
    /// The deadband must not swallow a genuine step-down: a peak below the hysteresis band still
    /// moves, so a real drop in level is still tracked.
    /// </summary>
    [Fact]
    public void Evaluate_PeakBelowTheHysteresisBand_StillStepsDown()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);

        // Just under range 6's step-down threshold of 0 - HeadroomDb - StepDownHysteresisDb.
        double peakDbv = -Qa40xAutoRanger.HeadroomDb - Qa40xAutoRanger.StepDownHysteresisDb - 0.1;

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(6.0, peakDbv, BlockDurationMs));   // 400 ms
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(6.0, peakDbv, BlockDurationMs));   // 800 ms
        var third = ranger.Evaluate(6.0, peakDbv, BlockDurationMs);                             // 1200 ms

        Assert.Equal(RangeStepDecision.StepDown, third);
        Assert.Equal(0.0, ranger.NextRangeDbv(6.0, third));
    }

    /// <summary>
    /// The two thresholds either side of one ladder step must not be the same number — that
    /// equality, not the timer, was the hunt. Stated against the constants so it survives a change
    /// to either.
    /// </summary>
    [Fact]
    public void StepDownThreshold_SitsBelowTheLowerRangesStepUpThreshold()
    {
        const double lowerRangeDbv = 0.0;

        double stepUpAtLowerRange = lowerRangeDbv - Qa40xAutoRanger.HeadroomDb;
        double stepDownOntoLowerRange = lowerRangeDbv - Qa40xAutoRanger.HeadroomDb - Qa40xAutoRanger.StepDownHysteresisDb;

        Assert.True(
            stepDownOntoLowerRange < stepUpAtLowerRange,
            $"step-down onto {lowerRangeDbv} dBV ({stepDownOntoLowerRange}) must be below that "
                + $"range's own step-up threshold ({stepUpAtLowerRange}), or the ranger hunts");
    }

    /// <summary>
    /// The half a caller can turn off is the step-*down* half, and only that half. A sweep that
    /// predicts each point's range needs its programmed range to survive — with step-down live,
    /// anything more than HeadroomDb under the range is walked back down one step per delay, which
    /// is the prediction being undone a second at a time — while still needing clip protection for
    /// a DUT that turns out louder than predicted.
    /// </summary>
    [Fact]
    public void Evaluate_WithStepDownDisabled_HoldsUnderRangeButStillStepsUpOnClip()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv) { StepDownEnabled = false };
        const double lowPeakDbv = -10.0; // Well under range 12's next step down (6 - 5 - 2 = -1 dBV).

        // Far longer than StepDownDelayMs: it is the flag, not the timer, holding this.
        for (int i = 0; i < 10; i++)
            Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs));

        // Same ranger, same instant: the clip backstop is untouched.
        var decision = ranger.Evaluate(currentRangeDbv: 12.0, peakDbv: 8.0, BlockDurationMs);

        Assert.Equal(RangeStepDecision.StepUp, decision);
        Assert.Equal(18.0, ranger.NextRangeDbv(12.0, decision));
    }

    /// <summary>
    /// Re-enabling must not inherit the under-range time that accumulated while it was off —
    /// otherwise the first block after a sweep ends steps down immediately, on a second of "quiet"
    /// that was never observed as such.
    /// </summary>
    [Fact]
    public void Evaluate_ReEnablingStepDown_StartsTheDelayAfresh()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv) { StepDownEnabled = false };
        const double lowPeakDbv = -10.0;

        for (int i = 0; i < 10; i++)
            ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs);

        ranger.StepDownEnabled = true;

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs)); // 400 ms
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs)); // 800 ms
        Assert.Equal(RangeStepDecision.StepDown, ranger.Evaluate(12.0, lowPeakDbv, BlockDurationMs)); // 1200 ms
    }

    [Fact]
    public void SettleFrameCount_DerivesFromTheDocumentedPlaceholderMs()
    {
        const double sampleRateHz = 192_000.0;

        int frames = Qa40xAutoRanger.SettleFrameCount(sampleRateHz);

        int expected = (int)Math.Ceiling(Qa40xAutoRanger.SettleTimeMsTodoPlaceholder / 1000.0 * sampleRateHz);
        Assert.Equal(expected, frames);
    }
}
