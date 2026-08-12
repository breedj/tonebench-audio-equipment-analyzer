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
/// caller: it feeds each block's calibrated peak level through <see cref="Evaluate"/>, and on a
/// non-<see cref="RangeStepDecision.Hold"/> result performs the actual register-5 write and
/// discards samples for the settle window.
///
/// The headroom below is not a QA40x protocol fact — none of the reference trees implement
/// autoranging — but it is not an arbitrary tuning constant either: it is the previous WinForms
/// app's <c>QaLibrary.DetermineAttenuation</c> (reference/previous version/.../Libraries/QaLibrary.cs:272-283),
/// "add 5 dBV extra for a better THD measurement", already ported once for the simulator's
/// client-side ranger (<c>web/src/attenuation.ts</c>'s <c>determineAttenuation</c>). This is the
/// same 5 dB figure applied to the hardware ranger, so both rangers keep a signal at least 5 dB
/// below whichever range's nominal full scale it's sitting in. Unlike <c>determineAttenuation</c>,
/// which picks the target range directly from one measurement, this stays a deliberately simple
/// single-step-at-a-time state machine: step one range per evaluation, let the settle window and
/// the next block's evaluation decide whether another step is needed, rather than jumping straight
/// to a predicted "correct" range from one sample.
/// </summary>
internal sealed class Qa40xAutoRanger(IReadOnlyList<double> inputRangesDbv)
{
    /// <summary>
    /// Headroom, in dB, to keep a signal below a range's nominal full scale: step up once the peak
    /// exceeds <c>currentRangeDbv - HeadroomDb</c>, and step down once it would still sit at least
    /// this far below the next lower range's full scale. Same 5 dB figure as
    /// <c>QaLibrary.DetermineAttenuation</c> / <c>attenuation.ts</c>' <c>determineAttenuation</c>.
    /// </summary>
    public const double HeadroomDb = 5.0;

    /// <summary>Consecutive under-range blocks required before stepping down — hysteresis so one quiet block doesn't trigger a step (tuning constant, not a protocol fact). Same 3-block figure <c>App.vue</c>'s <c>RANGE_STABILITY_FRAMES</c> mirrors for the simulator.</summary>
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
    /// largest calibrated peak level (either channel), in dBV. <paramref name="peakDbv"/> must be
    /// the same peak-to-RMS-equivalent "Pk dBV" convention the app's meters and a generator's own
    /// dBV setpoint use (peak volts / sqrt(2), then 20*log10) — not a bare true-peak dBV, which
    /// would read ~3 dB hotter and eat into <see cref="HeadroomDb"/>. Compared against each
    /// candidate range's own nominal full scale (its entry in <see cref="inputRangesDbv"/>) rather
    /// than a fixed raw-code fraction, so the same headroom margin applies at every range.
    /// </summary>
    public RangeStepDecision Evaluate(double currentRangeDbv, double peakDbv)
    {
        int currentIndex = IndexOf(currentRangeDbv);

        if (peakDbv > currentRangeDbv - HeadroomDb)
        {
            _consecutiveUnderRangeBlocks = 0;
            return currentIndex < inputRangesDbv.Count - 1 ? RangeStepDecision.StepUp : RangeStepDecision.Hold;
        }

        if (currentIndex > 0 && peakDbv <= inputRangesDbv[currentIndex - 1] - HeadroomDb)
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
