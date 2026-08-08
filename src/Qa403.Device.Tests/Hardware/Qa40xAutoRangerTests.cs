using Qa403.Device.Hardware;

namespace Qa403.Device.Tests.Hardware;

/// <summary>Pure state-machine tests, no transport/hardware involved.</summary>
public class Qa40xAutoRangerTests
{
    private static readonly double[] InputRangesDbv = [0, 6, 12, 18, 24, 30, 36, 42];

    [Fact]
    public void Evaluate_ClippingSample_StepsUpOneRange()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);

        var decision = ranger.Evaluate(currentRangeDbv: 12.0, peakAbsRawSample: int.MaxValue);

        Assert.Equal(RangeStepDecision.StepUp, decision);
        Assert.Equal(18.0, ranger.NextRangeDbv(12.0, decision));
    }

    [Fact]
    public void Evaluate_ClippingAtTopRange_Holds()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);

        var decision = ranger.Evaluate(currentRangeDbv: 42.0, peakAbsRawSample: int.MaxValue);

        Assert.Equal(RangeStepDecision.Hold, decision);
        Assert.Equal(42.0, ranger.NextRangeDbv(42.0, decision));
    }

    [Fact]
    public void Evaluate_UnderRange_RequiresConsecutiveBlocksBeforeSteppingDown()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        int lowPeak = (int)(int.MaxValue * 0.01); // well under the 6% threshold

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeak));
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeak));
        var third = ranger.Evaluate(12.0, lowPeak);

        Assert.Equal(RangeStepDecision.StepDown, third);
        Assert.Equal(6.0, ranger.NextRangeDbv(12.0, third));
    }

    [Fact]
    public void Evaluate_UnderRangeAtBottomRange_NeverSteps()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        int lowPeak = (int)(int.MaxValue * 0.01);

        for (int i = 0; i < 5; i++)
            Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(0.0, lowPeak));
    }

    [Fact]
    public void Evaluate_ANormalLevelBlockResetsUnderRangeHysteresis()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        int lowPeak = (int)(int.MaxValue * 0.01);
        int midPeak = (int)(int.MaxValue * 0.5);

        ranger.Evaluate(12.0, lowPeak);
        ranger.Evaluate(12.0, lowPeak);
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, midPeak)); // resets the counter

        // Two more low blocks after the reset is only the 2nd consecutive, not yet the 3rd.
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeak));
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeak));
        Assert.Equal(RangeStepDecision.StepDown, ranger.Evaluate(12.0, lowPeak));
    }

    [Fact]
    public void Reset_ClearsUnderRangeHysteresis()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        int lowPeak = (int)(int.MaxValue * 0.01);

        ranger.Evaluate(12.0, lowPeak);
        ranger.Evaluate(12.0, lowPeak);
        ranger.Reset();

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeak));
        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, lowPeak));
    }

    [Fact]
    public void Evaluate_MidRangeSample_Holds()
    {
        var ranger = new Qa40xAutoRanger(InputRangesDbv);
        int midPeak = (int)(int.MaxValue * 0.5);

        Assert.Equal(RangeStepDecision.Hold, ranger.Evaluate(12.0, midPeak));
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
