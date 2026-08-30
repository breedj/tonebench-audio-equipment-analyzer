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
    /// this far below the next lower range's full scale — plus
    /// <see cref="StepDownHysteresisDb"/>, without which the two comparisons are the same boundary.
    /// Same 5 dB figure as
    /// <c>QaLibrary.DetermineAttenuation</c> / <c>attenuation.ts</c>' <c>determineAttenuation</c>.
    /// </summary>
    public const double HeadroomDb = 5.0;

    /// <summary>
    /// Extra margin, in dB, a peak must sit below the *lower* range's own step-up threshold before
    /// this steps down onto it.
    ///
    /// <para>Without it the two thresholds are the same number and the ranger has no level
    /// hysteresis at all. On a 6 dB ladder with <see cref="HeadroomDb"/> = 5, range 6 stepped down
    /// when the peak was <c>&lt;= 0 - 5 = -5</c> dBV, and range 0 stepped straight back up when the
    /// peak was <c>&gt; 0 - 5 = -5</c> dBV — the same boundary in both directions. A signal sitting
    /// near it therefore oscillated indefinitely: the only thing separating the two decisions was
    /// <see cref="StepDownDelayMs"/>, so the pattern was one step down after a second of quiet
    /// followed immediately by a step back up. Observed on real hardware as clusters of register
    /// writes ~1.07 s apart — the delay plus one block — in the pump's own stall log.</para>
    ///
    /// <para>That is not a cosmetic problem. Every step quiesces EP2, and
    /// <c>TransferPump.QuiesceAndWriteRegister</c>'s <c>Drain()</c> discards every already-completed
    /// RX transfer — roughly one queue depth, ~64 ms at 192 kHz — on top of the settle frames the
    /// consumer drops. That is a ~110 ms hole punched in the sample stream once a second, and
    /// <c>SampleRingBuffer</c> concatenates straight across it. Hysteresis is what stops the hole
    /// from being self-inflicted; <c>PcmBlock.StartsAfterDiscontinuity</c> is what keeps the
    /// unavoidable ones off the display.</para>
    /// </summary>
    public const double StepDownHysteresisDb = 2.0;

    /// <summary>
    /// Default for <see cref="StepDownDelayMs"/> — how long a peak must stay under-range before
    /// stepping down, in milliseconds.
    /// </summary>
    public const double DefaultStepDownDelayMs = 1000.0;

    /// <summary>
    /// How long, in milliseconds, a peak must stay continuously under-range before
    /// <see cref="Evaluate"/> steps down — hysteresis so a brief dip doesn't trigger a step.
    /// Settable live (e.g. from a configuration dialog); a change takes effect on the next call to
    /// <see cref="Evaluate"/>.
    ///
    /// Time-based rather than a fixed block count: a fixed count of consecutive under-range blocks
    /// (this used to require 3) covers wildly different amounts of real time depending on sample
    /// rate and block size, and is too short to ride out an amplitude-modulated multitone signal —
    /// e.g. two tones close in frequency beat at their difference frequency, so <c>ScanPeakDbv</c>'s
    /// per-block peak rises and falls at that beat rate and can dip under the step-down threshold
    /// for several consecutive blocks purely from the beat, not from the signal actually having
    /// gotten quieter. A wall-clock delay filters that out regardless of block cadence.
    /// </summary>
    public double StepDownDelayMs { get; set; } = DefaultStepDownDelayMs;

    /// <summary>
    /// TODO: no relay settling time is documented anywhere in docs/protocol.md for register 5 —
    /// none of the three reference trees state one either (qa40x-rs-main's internal timing
    /// constant is deliberately not used here per the task instruction: if protocol.md doesn't
    /// state it, don't invent/borrow a plausible-sounding number). This is an unverified
    /// placeholder — confirm against real hardware and replace before relying on it.
    /// </summary>
    public const int SettleTimeMsTodoPlaceholder = 50;

    private double _underRangeElapsedMs;

    /// <summary>
    /// Decides what the input range should do next, given the current range and this block's
    /// largest calibrated peak level (either channel), in dBV. <paramref name="peakDbv"/> must be
    /// the same peak-to-RMS-equivalent "Pk dBV" convention the app's meters and a generator's own
    /// dBV setpoint use (peak volts / sqrt(2), then 20*log10) — not a bare true-peak dBV, which
    /// would read ~3 dB hotter and eat into <see cref="HeadroomDb"/>. Compared against each
    /// candidate range's own nominal full scale (its entry in <see cref="inputRangesDbv"/>) rather
    /// than a fixed raw-code fraction, so the same headroom margin applies at every range.
    /// <paramref name="blockDurationMs"/> is how much wall-clock time this block represents — the
    /// caller's chunk duration at its sample rate — accumulated against <see cref="StepDownDelayMs"/>
    /// for the under-range hysteresis; it plays no part in the immediate step-up path.
    /// </summary>
    public RangeStepDecision Evaluate(double currentRangeDbv, double peakDbv, double blockDurationMs)
    {
        int currentIndex = IndexOf(currentRangeDbv);

        if (peakDbv > currentRangeDbv - HeadroomDb)
        {
            _underRangeElapsedMs = 0;
            return currentIndex < inputRangesDbv.Count - 1 ? RangeStepDecision.StepUp : RangeStepDecision.Hold;
        }

        // StepDownHysteresisDb below the lower range's *own* step-up threshold, not level with it —
        // level with it is the same number in both directions, which is a ranger that hunts. See
        // StepDownHysteresisDb.
        if (currentIndex > 0 && peakDbv <= inputRangesDbv[currentIndex - 1] - HeadroomDb - StepDownHysteresisDb)
        {
            _underRangeElapsedMs += blockDurationMs;
            if (_underRangeElapsedMs >= StepDownDelayMs)
            {
                _underRangeElapsedMs = 0;
                return RangeStepDecision.StepDown;
            }

            return RangeStepDecision.Hold;
        }

        _underRangeElapsedMs = 0;
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

    /// <summary>Clears the under-range hysteresis timer — call after any explicit (non-autorange) range change.</summary>
    public void Reset() => _underRangeElapsedMs = 0;

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
