using Qa403.Device.Hardware;

namespace Qa403.Device.Tests.Hardware;

/// <summary>Pure state-machine tests, no transport/hardware involved.</summary>
public class Qa40xAutoRangerTests
{
    private static readonly double[] InputRangesDbv = [0, 6, 12, 18, 24, 30, 36, 42];

    [Fact]
    public void Evaluate_PeakAboveHeadroom_StepsUpOneRange()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);

        // Range 12's headroom boundary is 12 - 5 = 7 dBV; 8 dBV is just over it.
        var decision = ranger.Evaluate(currentRangeDbv: 12.0, peakDbv: 8.0);

        Assert.Equal(RangeStepDecision.StepUp, decision);
        Assert.Equal(18.0, ranger.NextRangeDbv(12.0, decision));
    }

    [Fact]
    public void Evaluate_PeakAboveHeadroomAtTopRange_Holds()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);

        var decision = ranger.Evaluate(currentRangeDbv: 42.0, peakDbv: 100.0);

        Assert.Equal(RangeStepDecision.Hold, decision);
        Assert.Equal(42.0, ranger.NextRangeDbv(42.0, decision));
    }

    [Fact]
    public void Evaluate_UnderRange_RequiresConsecutiveBlocksBeforeSteppingDown()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        // Next range down is 6, whose headroom boundary is 6 - 5 = 1 dBV; well under that.
        const double lowPeakDbv = -10.0;

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv));
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv));
        var third = ranger.Evaluate(12.0, lowPeakDbv);

        Assert.Equal(RangeStepDecision.StepDown, third);
        Assert.Equal(6.0, ranger.NextRangeDbv(12.0, third));
    }

    [Fact]
    public void Evaluate_UnderRangeAtBottomRange_NeverSteps()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        const double lowPeakDbv = -40.0;

        for (int i = 0; i < 5; i++)
            Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(0.0, lowPeakDbv));
    }

    [Fact]
    public void Evaluate_ANormalLevelBlockResetsUnderRangeHysteresis()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        const double lowPeakDbv = -10.0;
        const double midPeakDbv = 4.0; // Between the 1 dBV step-down and 7 dBV step-up boundaries.

        ranger.Evaluate(12.0, lowPeakDbv);
        ranger.Evaluate(12.0, lowPeakDbv);
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, midPeakDbv)); // resets the counter

        // Two more low blocks after the reset is only the 2nd consecutive, not yet the 3rd.
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv));
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv));
        Assert.Equal(RangeStepDecision.StepDown, ranger.Evaluate(12.0, lowPeakDbv));
    }

    [Fact]
    public void Reset_ClearsUnderRangeHysteresis()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        const double lowPeakDbv = -10.0;

        ranger.Evaluate(12.0, lowPeakDbv);
        ranger.Evaluate(12.0, lowPeakDbv);
        ranger.Reset();

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv));
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeakDbv));
    }

    [Fact]
    public void Evaluate_MidRangeSample_Holds()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        const double midPeakDbv = 4.0;

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, midPeakDbv));
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
