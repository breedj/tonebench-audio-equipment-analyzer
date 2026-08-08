namespace Qa403.Device.Hardware;

/// <summary>What <see cref="Qa40xAutoRanger.Evaluate"/> decided the input range should do next.</summary>
internal enum RangeStepDecision
{
    /// <summary>Stay on the current input range.</summary>
    Hold,

    /// <summary>Clipping (or near it): move to the next-higher (less sensitive) input range.</summary>
    StepUp,

    /// <summary>Sustained under-range: move to the next-lower (more sensitive) input range for better resolution.</summary>
    StepDown,
}

/// <summary>
/// Pure decision logic for QA403 input autoranging — no I/O. <c>Qa40xUsbDevice</c> is the only
/// caller: it feeds each block's peak raw sample through <see cref="Evaluate"/>, and on a
/// non-<see cref="RangeStepDecision.Hold"/> result performs the actual register-5 write and
/// discards samples for the settle window.
///
/// Autoranging behavior itself (when to step, how much hysteresis) is not a QA40x protocol fact —
/// none of the reference trees implement it, so every threshold below is an engineering judgment
/// call, not a cited wire-protocol value, and is documented as such. This is a deliberately simple
/// single-step-at-a-time state machine: step one range per evaluation, let the settle window and
/// the next block's evaluation decide whether another step is needed, rather than trying to jump
/// straight to a predicted "correct" range from one sample.
/// </summary>
internal sealed class Qa40xAutoRanger(IReadOnlyList<double> inputRangesDbv)
{
    /// <summary>Raw peak at/above this fraction of int32 full scale is treated as clipping (tuning constant, not a protocol fact).</summary>
    public const double ClipThresholdFraction = 0.99;

    /// <summary>Raw peak below this fraction of int32 full scale (~ -24 dBFS) is treated as under-range (tuning constant, not a protocol fact).</summary>
    public const double UnderRangeThresholdFraction = 0.06;

    /// <summary>Consecutive under-range blocks required before stepping down — hysteresis so one quiet block doesn't trigger a step (tuning constant, not a protocol fact).</summary>
    public const int UnderRangeConsecutiveBlocksRequired = 3;

    /// <summary>
    /// TODO: no relay settling time is documented anywhere in docs/protocol.md for register 5 —
    /// none of the three reference trees state one either (qa40x-rs-main's internal timing
    /// constant is deliberately not used here per the task instruction: if protocol.md doesn't
    /// state it, don't invent/borrow a plausible-sounding number). This is an unverified
    /// placeholder — confirm against real hardware and replace before relying on it.
    /// </summary>
    public const int SettleTimeMsTodoPlaceholder = 50;

    private int _consecutiveUnderRangeBlocks;

    /// <summary>
    /// Decides what the input range should do next, given the current range and this block's
    /// largest absolute raw ADC sample (any channel). Clipping is checked in raw-code space
    /// because the ADC's code range is fixed regardless of which input range is selected — only
    /// the analog front-end's voltage-to-full-scale mapping changes with the range.
    /// </summary>
    public RangeStepDecision Evaluate(double currentRangeDbv, int peakAbsRawSample)
    {
        double fraction = peakAbsRawSample / (double)int.MaxValue;
        int currentIndex = IndexOf(currentRangeDbv);

        if (fraction >= ClipThresholdFraction)
        {
            _consecutiveUnderRangeBlocks = 0;
            return currentIndex < inputRangesDbv.Count - 1 ? RangeStepDecision.StepUp : RangeStepDecision.Hold;
        }

        if (fraction < UnderRangeThresholdFraction && currentIndex > 0)
        {
            _consecutiveUnderRangeBlocks++;
            if (_consecutiveUnderRangeBlocks >= UnderRangeConsecutiveBlocksRequired)
            {
                _consecutiveUnderRangeBlocks = 0;
                return RangeStepDecision.StepDown;
            }

            return RangeStepDecision.Hold;
        }

        _consecutiveUnderRangeBlocks = 0;
        return RangeStepDecision.Hold;
    }

    /// <summary>The range to move to for a given decision — one step in <see cref="inputRangesDbv"/>, clamped at either end.</summary>
    public double NextRangeDbv(double currentRangeDbv, RangeStepDecision decision)
    {
        int index = IndexOf(currentRangeDbv);
        int nextIndex = decision switch
        {
            RangeStepDecision.StepUp => Math.Min(index + 1, inputRangesDbv.Count - 1),
            RangeStepDecision.StepDown => Math.Max(index - 1, 0),
            _ => index,
        };

        return inputRangesDbv[nextIndex];
    }

    /// <summary>Number of frames to discard after a range step, for a given sample rate — derived from <see cref="SettleTimeMsTodoPlaceholder"/>.</summary>
    public static int SettleFrameCount(double sampleRateHz) =>
        (int)Math.Ceiling(SettleTimeMsTodoPlaceholder / 1000.0 * sampleRateHz);

    /// <summary>Clears the under-range hysteresis counter — call after any explicit (non-autorange) range change.</summary>
    public void Reset() => _consecutiveUnderRangeBlocks = 0;

    private int IndexOf(double rangeDbv)
    {
        for (int i = 0; i < inputRangesDbv.Count; i++)
        {
            if (inputRangesDbv[i] == rangeDbv)
                return i;
        }

        throw new ArgumentOutOfRangeException(nameof(rangeDbv), rangeDbv, "Not a supported input range.");
    }
}
