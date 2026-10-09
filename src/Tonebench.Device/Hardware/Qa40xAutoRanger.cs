namespace Tonebench.Device.Hardware;

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
/// ranger an analyzer without one of its own gets (<c>SessionInputRanger</c>). This is the
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
    /// <c>QaLibrary.DetermineAttenuation</c> / <c>InputRangeSelector.HeadroomDb</c>.
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
    /// Whether <see cref="Evaluate"/> may return <see cref="RangeStepDecision.StepDown"/> at all.
    /// Clearing this leaves the step-*up* (clip protection) path fully live, which is the only
    /// combination that lets a caller program the input range itself and have it stay programmed:
    /// with both directions enabled, any explicit <c>SetInputRangeAsync</c> to a range more than
    /// <see cref="HeadroomDb"/> above the signal is walked straight back down one step per
    /// <see cref="StepDownDelayMs"/> (see <c>Qa40xUsbDevice.AutoRangeEnabled</c>'s historical note),
    /// and disabling autoranging outright gives up clip protection instead.
    ///
    /// <para>That combination is what a sweep wants. <c>ThdSweepRunner</c> predicts each point's
    /// level from the previous point's measurement and programs the range for it *between* points,
    /// where the change rides along with the generator change's flush and costs nothing; the ranger
    /// stays on purely as the backstop for a DUT that turns out louder than predicted. Reactive
    /// step-down during a sweep is not just redundant there, it is harmful — it undoes the
    /// prediction, and every step punches a ~110 ms hole in the sample stream mid-point.</para>
    /// </summary>
    public bool StepDownEnabled { get; set; } = true;

    /// <summary>
    /// Settle after a range change that leaves the 24 dB input attenuator where it was — i.e. any
    /// step within 0-18 dBV or within 24-42 dBV, and any step that *engages* the attenuator.
    ///
    /// <para>Still the unverified 50 ms it has always been: no relay settling time is documented in
    /// docs/protocol.md for register 5, and none of the three reference trees state one. What has
    /// now been measured is that it is not the number that mattered — see
    /// <see cref="AttenuatorReleaseSettleMs"/>.</para>
    /// </summary>
    public const int GainStepSettleMs = 50;

    /// <summary>
    /// Input ranges at this level and above engage the QA403's 24 dB input attenuator; below it the
    /// front end runs unattenuated. The ladder's own step is 6 dB, so this is the one boundary in it
    /// that is a relay rather than a gain setting — which is also why
    /// <c>Qa40xUsbDeviceHardwareTests</c>'s loopback theory measured the 24 dBV range as a 21 dB
    /// cliff rather than another 6 dB rung.
    /// </summary>
    public const double AttenuatedRangeFloorDbv = 24.0;

    /// <summary>
    /// Settle after the 24 dB attenuator is *released* (a change from 24-42 dBV down to 0-18).
    ///
    /// <para>The relay is asymmetric, and this is the half that costs. Measured on serial
    /// 1077527945 (fw 60) on 2026-10-08, loopback, 48 kHz, 4096-frame blocks: engaging it takes
    /// effect inside the first block, but releasing it lags about a second — on a 30 -> 0 dBV change
    /// the capture reads **exactly 24 dB low** (the attenuator still in circuit while
    /// <see cref="Qa40xCalibration"/> already scales for the new range) until 1.05 s after the
    /// register write, then jumps through one block carrying a 1.52 V transient and is correct from
    /// there on. It is wall time from the write, not from stream start: with 0 / 500 / 1000 / 2000 ms
    /// inserted between the two, the switch landed at 1055 ms / 1054 ms / before the first block /
    /// before the first block. A change within 0-18 dBV (12 -> 0) shows none of it.</para>
    ///
    /// <para>1500 rather than 1050 because the same change measured on a *running* stream put the
    /// switch between 1.13 and 1.23 s, and nothing here pins down what that spread depends on. The
    /// cost is paid only on this one transition, which an ordinary bench session at 0-18 dBV never
    /// makes.</para>
    /// </summary>
    public const int AttenuatorReleaseSettleMs = 1500;

    /// <summary>
    /// Whether moving from <paramref name="fromRangeDbv"/> to <paramref name="toRangeDbv"/> releases
    /// the 24 dB attenuator, which is the only range change that needs
    /// <see cref="AttenuatorReleaseSettleMs"/> rather than <see cref="GainStepSettleMs"/>.
    /// </summary>
    public static bool ReleasesAttenuator(double fromRangeDbv, double toRangeDbv) =>
        fromRangeDbv >= AttenuatedRangeFloorDbv && toRangeDbv < AttenuatedRangeFloorDbv;

    /// <summary>Settle time owed by a change from <paramref name="fromRangeDbv"/> to <paramref name="toRangeDbv"/>, in ms.</summary>
    public static int SettleMsFor(double fromRangeDbv, double toRangeDbv) =>
        ReleasesAttenuator(fromRangeDbv, toRangeDbv) ? AttenuatorReleaseSettleMs : GainStepSettleMs;

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
        if (StepDownEnabled && currentIndex > 0 && peakDbv <= inputRangesDbv[currentIndex - 1] - HeadroomDb - StepDownHysteresisDb)
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

    /// <summary>
    /// Number of frames to discard after a range step from <paramref name="fromRangeDbv"/> to
    /// <paramref name="toRangeDbv"/>, at a given sample rate. A time rather than a frame count for
    /// the reason the TX queue depth is one: the relay takes the time it takes whatever the stream
    /// is doing, so a fixed count would shrink the window as the sample rate rose.
    /// </summary>
    public static int SettleFrameCount(double sampleRateHz, double fromRangeDbv, double toRangeDbv) =>
        SettleFrameCountFor(sampleRateHz, SettleMsFor(fromRangeDbv, toRangeDbv));

    /// <summary>Frames in <paramref name="settleMs"/> at <paramref name="sampleRateHz"/>.</summary>
    public static int SettleFrameCountFor(double sampleRateHz, double settleMs) =>
        (int)Math.Ceiling(settleMs / 1000.0 * sampleRateHz);

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
