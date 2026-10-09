using Tonebench.Data;

namespace Tonebench.App.Services;

/// <summary>
/// Chooses the input range, block by block, for an analyzer that has no ranger of its own —
/// the simulator, and any other device whose <see cref="Device.IAnalyzerDevice.HasInputAutoRanging"/>
/// is false. Pure decision logic with one piece of state (the under-range clock), no I/O;
/// <c>DeviceSessionService.MaybeStepInputRangeAsync</c> is the only caller.
///
/// <para><b>This used to live in the browser, and that is the bug it exists to remove.</b>
/// <c>App.vue</c> watched the meters frames and posted <c>/api/device/input-range</c> once a target
/// had held for three frames. Every accepted range write flushes the analysis window
/// (<c>DeviceSessionService.SetInputRangeCoreAsync</c>), which clears the averagers — so the
/// decision being per-tab turned a second open window into an averaging treadmill. Measured on the
/// simulator 2026-10-09, 1 kHz at 7.5 dBV into a straight wire (peak 7.557 dBV, so the ladder's
/// answer is the 18 dBV rung): two attached clients wrote <b>18 and 12 alternately, about once a
/// second</b>, each one's write making the other's idea of the current range stale three frames
/// later. A 15-average FFT restarted at every third record and never finished; suppressing one
/// client's writes reached 15/15 at once, and so did pinning Ranging to Manual. Reported as
/// "with the simulator it restarts averaging after 3 measurements" — three being
/// <c>RANGE_STABILITY_FRAMES</c>, which is how the client's own constant ended up on screen.
/// One ranger in the session cannot disagree with itself however many windows are open.</para>
///
/// <para><b>It jumps to the target rather than stepping one rung</b>, unlike
/// <c>Qa40xAutoRanger</c>, and the difference is what each step costs. There, a step quiesces EP2
/// and punches a ~110 ms hole in the sample stream, so the ranger takes one rung per evaluation
/// and lets the next block confirm; here a step costs one flushed record and nothing else, so
/// arriving in one move is strictly cheaper than arriving in three. The target itself comes from
/// <see cref="InputRangeSelector"/> — the one ladder, the same 5 dB headroom as the hardware
/// ranger — so this class only decides <i>when</i>.</para>
///
/// <para>It cannot jump out of a range the signal is <i>already clipping</i>, and that is a
/// property of the measurement rather than of this class: a clipped block's peak is the clip
/// level, so the ladder is told the range it is on is just barely too small and moves one rung.
/// Measured against the simulator starting from 0 dBV with a 7.5 dBV tone — 0 → 6 → 12 → 18, three
/// steps in <b>44 ms</b>, all of them inside the first record and so costing an acquisition
/// nothing. A real device's own ranger walks up the same way for the same reason.</para>
/// </summary>
public sealed class SessionInputRanger(IReadOnlyList<double> inputRangesDbv)
{
    /// <summary>
    /// Extra margin, in dB, a peak must sit below the lower range's own step-up threshold before
    /// this steps down onto it. Same figure and same reasoning as
    /// <c>Qa40xAutoRanger.StepDownHysteresisDb</c>: without it the step-down and step-up
    /// comparisons are the same boundary, and a level sitting on it hunts for ever — which on this
    /// side of the app means one flushed record per hunt, i.e. averaging that never completes.
    /// </summary>
    public const double StepDownHysteresisDb = 2.0;

    /// <summary>
    /// How long the peak must stay under-range before stepping down, in milliseconds. Shared with
    /// the hardware ranger's own setting (Configuration ▸ step-down delay,
    /// <see cref="AutoRangeStepDownDelaySetting"/>), because it answers the same question — how
    /// long a quiet patch has to last before it counts as a quieter signal — and a bench that has
    /// tuned it for the QA403 means the same thing by it on the simulator.
    /// </summary>
    public double StepDownDelayMs { get; set; } = AutoRangeStepDownDelayConfig.Default.DelayMs;

    private double _underRangeElapsedMs;

    /// <summary>
    /// Forgets the under-range clock. Called when an acquisition starts and whenever the range is
    /// written from anywhere else, so a quiet patch that straddles someone else's range change
    /// does not count towards a step-down from the new one.
    /// </summary>
    public void Reset() => _underRangeElapsedMs = 0.0;

    /// <summary>
    /// The range to move to, or null to hold.
    /// </summary>
    /// <param name="currentRangeDbv">The range the device is on now.</param>
    /// <param name="peakDbv">
    /// This block's largest peak over the channels being measured, in the app's RMS-referenced
    /// convention (<c>20·log10(peak_volts / √2)</c>) and in the <i>analyzer's</i> own volts — a
    /// block is what the device delivered, before <c>SpectrumPipeline</c> divides the external
    /// input gain out of it, which is exactly what the ladder wants.
    /// </param>
    /// <param name="blockDurationMs">This block's own wall-clock length, for the under-range clock.</param>
    /// <param name="stepDownAllowed">
    /// False while a measurement runner owns the session. Every runner ranges for itself — a probe
    /// from the widest range, a prediction from the target, a per-point ratchet — and a reactive
    /// step down undoes that prediction mid-run. Step <i>up</i> stays live either way, as the clip
    /// backstop, which is the same split <c>Qa40xAutoRanger.StepDownEnabled</c> gives a sweep on
    /// real hardware.
    /// </param>
    public double? Evaluate(double currentRangeDbv, double peakDbv, double blockDurationMs, bool stepDownAllowed)
    {
        // One rung is not a ladder (a sound card), and a peak of NaN is a block with nothing in it.
        if (inputRangesDbv.Count <= 1 || double.IsNaN(peakDbv))
        {
            _underRangeElapsedMs = 0.0;
            return null;
        }

        double wanted = InputRangeSelector.For(inputRangesDbv, peakDbv);

        // Clipping, or close enough to be eating the headroom: immediately, with no hysteresis and
        // no delay. A record measured on too sensitive a range is wrong in a way no amount of
        // waiting improves.
        if (wanted > currentRangeDbv)
        {
            _underRangeElapsedMs = 0.0;
            return wanted;
        }

        if (!stepDownAllowed || wanted >= currentRangeDbv)
        {
            _underRangeElapsedMs = 0.0;
            return null;
        }

        // The hysteretic target: where the ladder would put a signal StepDownHysteresisDb louder
        // than this one. Stepping down onto *that* rather than onto `wanted` is what stops the new
        // range from immediately wanting to step back up.
        double hysteretic = InputRangeSelector.For(inputRangesDbv, peakDbv + StepDownHysteresisDb);
        if (hysteretic >= currentRangeDbv)
        {
            _underRangeElapsedMs = 0.0;
            return null;
        }

        _underRangeElapsedMs += blockDurationMs;
        if (_underRangeElapsedMs < StepDownDelayMs)
            return null;

        _underRangeElapsedMs = 0.0;
        return hysteretic;
    }

    /// <summary>
    /// The largest RMS-referenced peak in one block over the channels being measured, or NaN when
    /// the block carries none of them. Only the processed channels, which is the rule the browser's
    /// watcher had: one range serves both inputs, and a loud signal on a channel nobody is looking
    /// at would otherwise widen the range and spoil the one that is being measured. (The hardware
    /// ranger inside the device cannot make that distinction and takes both — it has no idea what
    /// the pipeline is processing.)
    /// </summary>
    public static double BlockPeakDbv(ReadOnlySpan<float> interleaved, int channelCount, bool left, bool right)
    {
        if (channelCount < 1)
            return double.NaN;

        float peak = 0.0f;
        bool any = false;
        for (int i = 0; i < interleaved.Length; i++)
        {
            int channel = i % channelCount;
            if (channel == 0 ? !left : channel == 1 ? !right : true)
                continue;

            any = true;
            float magnitude = Math.Abs(interleaved[i]);
            if (magnitude > peak)
                peak = magnitude;
        }

        if (!any)
            return double.NaN;

        // Zero volts is a legitimate block (a silenced generator into a perfect wire, and the
        // simulator with noise off), and log10(0) is −∞: the most sensitive range is the right
        // answer for silence, so say so with a number the ladder can take.
        return peak <= 0.0f ? double.NegativeInfinity : (20.0 * Math.Log10(peak / Math.Sqrt(2.0)));
    }
}
