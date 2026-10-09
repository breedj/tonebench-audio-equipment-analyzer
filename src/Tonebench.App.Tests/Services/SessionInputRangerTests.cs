using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The decision half of the session's input ranger — the ladder is
/// <see cref="InputRangeSelector"/>'s and is tested there; what is pinned here is <i>when</i> it
/// moves, which is the half that went wrong when this lived in the browser.
/// </summary>
public class SessionInputRangerTests
{
    private static readonly double[] Ladder = [0.0, 6.0, 12.0, 18.0, 24.0, 30.0, 36.0, 42.0];

    private static SessionInputRanger Ranger(double stepDownDelayMs = 1000.0) =>
        new(Ladder) { StepDownDelayMs = stepDownDelayMs };

    /// <summary>
    /// Clipping is answered at once and in one move: on a 6 dB ladder a signal that arrives 20 dB
    /// above the range needs four rungs, and taking them one block at a time would flush the
    /// analysis window four times for one event.
    /// </summary>
    [Fact]
    public void APeakAboveTheRange_StepsStraightToTheRangeThatHoldsIt()
    {
        Assert.Equal(18.0, Ranger().Evaluate(currentRangeDbv: 0.0, peakDbv: 7.5, blockDurationMs: 21.3, stepDownAllowed: true));
    }

    /// <summary>The level the ladder already agrees with is no decision at all.</summary>
    [Fact]
    public void APeakTheRangeAlreadyFits_HoldsIt()
    {
        Assert.Null(Ranger().Evaluate(currentRangeDbv: 18.0, peakDbv: 7.5, blockDurationMs: 21.3, stepDownAllowed: true));
    }

    /// <summary>
    /// Stepping down waits out the delay — a dip is not a quieter signal — and then goes in one
    /// move, like the step up.
    /// </summary>
    [Fact]
    public void APeakWellUnderTheRange_StepsDownOnlyOnceTheDelayHasElapsed()
    {
        var ranger = Ranger(stepDownDelayMs: 100.0);

        Assert.Null(ranger.Evaluate(18.0, peakDbv: -20.0, blockDurationMs: 40.0, stepDownAllowed: true));
        Assert.Null(ranger.Evaluate(18.0, peakDbv: -20.0, blockDurationMs: 40.0, stepDownAllowed: true));
        Assert.Equal(0.0, ranger.Evaluate(18.0, peakDbv: -20.0, blockDurationMs: 40.0, stepDownAllowed: true));
    }

    /// <summary>A moment of quiet inside an otherwise loud signal restarts the clock rather than accumulating.</summary>
    [Fact]
    public void ADipShorterThanTheDelay_DoesNotStepDown()
    {
        var ranger = Ranger(stepDownDelayMs: 100.0);

        Assert.Null(ranger.Evaluate(18.0, peakDbv: -20.0, blockDurationMs: 40.0, stepDownAllowed: true));
        Assert.Null(ranger.Evaluate(18.0, peakDbv: 7.5, blockDurationMs: 40.0, stepDownAllowed: true));
        Assert.Null(ranger.Evaluate(18.0, peakDbv: -20.0, blockDurationMs: 40.0, stepDownAllowed: true));
        Assert.Null(ranger.Evaluate(18.0, peakDbv: -20.0, blockDurationMs: 40.0, stepDownAllowed: true));
    }

    /// <summary>
    /// The one that matters. A level sitting just inside a rung's step-up threshold must not be
    /// walked down onto the rung below, because the moment it is there the ladder wants it back —
    /// and here each move is a flushed record, so a hunting ranger is an FFT whose averaging never
    /// completes. 1.0 dBV wants the 6 dBV rung outright (1 + 5 = 6), so without the hysteresis
    /// margin this steps down from 12 and then straight back up.
    /// </summary>
    [Fact]
    public void ALevelSittingOnARungBoundary_DoesNotHunt()
    {
        var ranger = Ranger(stepDownDelayMs: 0.0);
        const double onTheBoundaryDbv = 1.0;

        double? down = ranger.Evaluate(12.0, onTheBoundaryDbv, blockDurationMs: 40.0, stepDownAllowed: true);
        Assert.Null(down);

        // And the other direction is a hold too, which is what makes the pair stable rather than
        // merely slow: whichever of the two rungs it is on, it stays there.
        Assert.Null(ranger.Evaluate(6.0, onTheBoundaryDbv, blockDurationMs: 40.0, stepDownAllowed: true));
    }

    /// <summary>
    /// While a runner owns the session the range is the runner's: it probed or predicted it, and a
    /// reactive step down undoes that. Clip protection stays live, exactly as
    /// <c>Qa40xAutoRanger.StepDownEnabled</c> leaves it during a sweep on real hardware.
    /// </summary>
    [Fact]
    public void WithStepDownDisallowed_OnlyTheClipBackstopFires()
    {
        var ranger = Ranger(stepDownDelayMs: 0.0);

        Assert.Null(ranger.Evaluate(42.0, peakDbv: -40.0, blockDurationMs: 40.0, stepDownAllowed: false));
        Assert.Equal(18.0, ranger.Evaluate(0.0, peakDbv: 7.5, blockDurationMs: 40.0, stepDownAllowed: false));
    }

    /// <summary>A sound card's single full scale is not a ladder; there is nothing to decide.</summary>
    [Fact]
    public void WithOneRung_ThereIsNothingToChoose()
    {
        var ranger = new SessionInputRanger([16.5]) { StepDownDelayMs = 0.0 };

        Assert.Null(ranger.Evaluate(16.5, peakDbv: 40.0, blockDurationMs: 40.0, stepDownAllowed: true));
        Assert.Null(ranger.Evaluate(16.5, peakDbv: -40.0, blockDurationMs: 40.0, stepDownAllowed: true));
    }

    /// <summary>
    /// The peak is RMS-referenced — <c>20·log10(peak/√2)</c>, the convention every other ranging
    /// seam in the app uses. A bare true peak reads 3.01 dB hotter and would put this a whole rung
    /// away from <see cref="InputRangeSelector"/>, which is the one thing the shared ladder exists
    /// to prevent.
    /// </summary>
    [Fact]
    public void BlockPeakDbv_IsTheRmsReferencedPeak()
    {
        float[] block = [1.0f, 0.0f, -0.5f, 0.0f];

        double peak = SessionInputRanger.BlockPeakDbv(block, channelCount: 2, left: true, right: true);

        Assert.Equal(20.0 * Math.Log10(1.0 / Math.Sqrt(2.0)), peak, 6);
    }

    /// <summary>
    /// Only the channels being measured. One range serves both inputs, so a loud signal on a
    /// channel nobody is looking at would otherwise widen the range and cost the measured channel
    /// real resolution.
    /// </summary>
    [Fact]
    public void BlockPeakDbv_IgnoresAChannelThePipelineIsNotProcessing()
    {
        float[] block = [0.1f, 10.0f, 0.1f, 10.0f];

        double left = SessionInputRanger.BlockPeakDbv(block, channelCount: 2, left: true, right: false);
        double both = SessionInputRanger.BlockPeakDbv(block, channelCount: 2, left: true, right: true);

        Assert.Equal(20.0 * Math.Log10(0.1 / Math.Sqrt(2.0)), left, 6);
        Assert.Equal(20.0 * Math.Log10(10.0 / Math.Sqrt(2.0)), both, 6);
    }

    /// <summary>
    /// Silence is a level, not a missing one: the most sensitive range is the right answer for it,
    /// and −∞ is what says so to a ladder that would read NaN as "no signal found, park wide".
    /// </summary>
    [Fact]
    public void BlockPeakDbv_OfASilentBlock_IsNegativeInfinityRatherThanNaN()
    {
        float[] block = [0.0f, 0.0f, 0.0f, 0.0f];

        double peak = SessionInputRanger.BlockPeakDbv(block, channelCount: 2, left: true, right: true);

        Assert.Equal(double.NegativeInfinity, peak);
        Assert.Equal(0.0, InputRangeSelector.For(Ladder, peak));
    }

    /// <summary>With neither channel processed there is no peak to speak for, and NaN is what holds the range.</summary>
    [Fact]
    public void BlockPeakDbv_WithNoChannelProcessed_IsNaN_AndHolds()
    {
        float[] block = [1.0f, 1.0f];

        double peak = SessionInputRanger.BlockPeakDbv(block, channelCount: 2, left: false, right: false);

        Assert.True(double.IsNaN(peak));
        Assert.Null(Ranger(stepDownDelayMs: 0.0).Evaluate(42.0, peak, blockDurationMs: 40.0, stepDownAllowed: true));
    }
}
