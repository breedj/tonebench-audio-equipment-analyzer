namespace Tonebench.Dsp;

/// <summary>
/// Puts every record of an accumulation on one fractional sample grid.
///
/// <para><b>Why an accumulation needs this at all.</b> An oscilloscope's trigger is interpolated —
/// the engine reports where the crossing really was, to a fraction of a sample — but a record has
/// to start on a whole sample, so each capture ends up with its trigger a different fraction of a
/// sample from its own first frame. One record does not care. <see cref="RecordAccumulator"/> does:
/// its whole premise is that sample i of every record is the same point on the waveform, and a
/// residual offset uniform over half a sample attenuates a sinusoid by <c>sinc(f·Δt)</c> —
/// <b>0.9 dB at a quarter of the sample rate and 3.6 dB at 0.95 of Nyquist</b>. That reads as a
/// smaller waveform rather than as a misaligned one, which is the worst way for it to be wrong.</para>
///
/// <para>An envelope wants it for the mirror reason: misalignment <i>widens</i> the band by half a
/// sample of the waveform's own slope, which reads as jitter the DUT does not have.</para>
///
/// <para><b>It is the useful half of equivalent-time sampling and not the whole of it.</b> A bench
/// scope interleaves records taken at different fractional offsets to synthesise a sample rate
/// above its own; that works because its sampler is slower than its front end, which is not true
/// here — the band limit is the ADC's own decimation filter, applied before the samples exist. So
/// there is nothing between the samples that the samples do not already determine, and interleaving
/// would add no information. Aligning them, on the other hand, fixes a real loss. See
/// <c>FractionalDelayTests.ASincReconstructionFromOneRecord_AlreadyHasEverythingInterleavingCouldAdd</c>,
/// which measures the negative half rather than asserting it.</para>
/// </summary>
public sealed class RecordAligner
{
    /// <summary>
    /// The sub-sample trigger position every record is being put on, or null before the first one
    /// has set it.
    /// </summary>
    public double? Grid { get; private set; }

    /// <summary>Starts again, so the next record sets the grid. Called wherever the accumulator resets.</summary>
    public void Reset() => Grid = null;

    /// <summary>
    /// The channels, read onto the grid — or handed straight back where there is nothing to do.
    ///
    /// <para><b>The first record sets the grid and is used as it is</b>, so a single capture is
    /// never interpolated and the figures for one are readings of the samples themselves. Every
    /// record after it is read across by <see cref="FractionalDelay"/>: a windowed sinc rather than
    /// a straight line, for the reason the chart's own drawing documents.</para>
    ///
    /// <para>The shift is <c>thisRecord − theGrid</c>. A record whose trigger sits <i>earlier</i>
    /// within its own samples is read earlier, which moves its waveform later onto the grid — the
    /// sign a caller gets wrong once.</para>
    ///
    /// <para>Below <see cref="FractionalDelay.NegligibleShift"/> the record is handed back untouched
    /// and the grid is <b>not</b> re-set from it: taking the grid from whichever record happened to
    /// be close enough would let it walk a thousandth of a sample at a time.</para>
    /// </summary>
    /// <param name="channels">The record's samples, one array per channel.</param>
    /// <param name="triggerOffsetSamples">
    /// Where the trigger sits <b>within this record</b>, in fractional samples —
    /// <c>record.TriggerPosition − record.FirstIndex</c>.
    ///
    /// <para><b>Within the record, and not the fractional part of the absolute position</b>, which
    /// is a different number and was the first thing this got wrong. A record starts at
    /// <c>round(triggerPosition) − preTrigger</c>, so the trigger's offset inside it is
    /// <c>preTrigger + (triggerPosition − round(triggerPosition))</c> — and that bracket runs over
    /// <c>[−½, +½)</c>, where <c>frac(triggerPosition)</c> runs over <c>[0, 1)</c>. The two agree
    /// for a crossing in the first half of a sample and differ by a <i>whole sample</i> for one in
    /// the second half, so aligning on the fractional part shifted half the records a sample too
    /// far: measured on a 60 kHz tone at 192 kS/s, the average lost <b>4.2 dB</b> where leaving
    /// them alone would have cost 1.4 — the correction was adding about a sample of spread to the
    /// half-sample it was there to remove. The unit tests had passed because they were fed
    /// positions whose two readings happened to coincide; the loss showed up the first time a real
    /// capture was averaged at the top of the band.</para>
    /// </param>
    public double[][] Align(double[][] channels, double triggerOffsetSamples)
    {
        ArgumentNullException.ThrowIfNull(channels);

        if (!double.IsFinite(triggerOffsetSamples) || channels.Length == 0)
            return channels;

        if (Grid is not { } grid)
        {
            Grid = triggerOffsetSamples;
            return channels;
        }

        double shift = triggerOffsetSamples - grid;

        // Records of one geometry cannot differ by more than a sample: the offset is the
        // pre-trigger length plus a value in [-1/2, +1/2). Anything larger means the record length
        // or the pre-trigger moved under the accumulation, and shifting by it would be a
        // correction for a record this grid never described -- so it is left alone and the
        // accumulator's own shape check deals with it.
        if (Math.Abs(shift) > 1.0)
            return channels;

        if (!FractionalDelay.Matters(shift))
            return channels;

        var aligned = new double[channels.Length][];
        for (int c = 0; c < aligned.Length; c++)
        {
            aligned[c] = new double[channels[c].Length];
            FractionalDelay.Shift(channels[c], aligned[c], shift);
        }

        return aligned;
    }
}
