namespace Tonebench.Dsp;

/// <summary>
/// Fires at a fixed point of the <i>generator's</i> own repeat, not on anything measured — the one
/// trigger here that no bench oscilloscope has, and the only one that works on a stimulus with no
/// edge to find.
///
/// <para><b>Why this analyzer can do it.</b> It composes the stimulus, so the stimulus's phase is
/// known exactly rather than inferred from the signal coming back. That is the same fact the chirp
/// measurement rests on: a sweep made to repeat over exactly one analysis record needs no trigger,
/// because every record holds one whole sweep whatever point it starts at. Here it is turned into a
/// trigger — fire every <c>PeriodFrames</c>, and successive records are the same stretch of the
/// stimulus every time.</para>
///
/// <para><b>What it buys is zero jitter, on signals the other kinds cannot hold at all.</b> A chirp,
/// a periodic-noise sequence and a multitone comb have no feature an edge trigger can lock to, so
/// they draw as a wash that redraws differently every capture. Triggered on the stimulus they stand
/// perfectly still, which is what makes a transient visible in one and what makes averaging them
/// worth anything at all (<see cref="RecordAccumulator"/>'s whole premise is that sample i of every
/// record is the same point on the waveform). On a square or a triangle it is still worth having:
/// the trigger point is exact rather than interpolated, so there is no sub-sample residue left to
/// align away.</para>
///
/// <para><b>What it is NOT is absolute phase, and that has to be said plainly.</b> The index this
/// fires on is the <i>input</i> clock's, and between the generator writing a frame and the ADC
/// returning it there is the DAC queue, the DUT and the analog path — a delay this app does not
/// measure. So t = 0 is a consistent point of the stimulus offset by an unknown constant, not the
/// stimulus's own zero crossing. Consistent is the whole of what a trace needs; absolute would need
/// a round-trip measurement, which is the frequency-response measurement's job and not this
/// one's.</para>
///
/// <para><b>It is anchored on the absolute frame clock rather than on the first sample it sees</b>,
/// which is deliberate and is what makes a run phase-locked across re-arms and across a break in
/// the stream: the device goes on generating from its own frame index whatever the scanner does, so
/// <c>index mod period</c> is the same point of the stimulus for the life of the acquisition.
/// Anchoring on the first scanned sample would re-phase the trace every time the engine reset —
/// after a dropped block, after an input-range step — which on a waveform reads as the DUT having
/// moved.</para>
/// </summary>
public sealed class StimulusTrigger : IScanTrigger
{
    private readonly long _periodFrames;
    private readonly int _holdoffFrames;
    private long _holdoffUntil;

    /// <param name="periodFrames">
    /// How many frames the stimulus repeats in. A whole number of frames and not a time, because
    /// the whole point is that the repeat is exact in the sampled domain — the chirp's own rule,
    /// and the reason every periodic stimulus here is built to a frame count.
    /// </param>
    /// <param name="holdoffFrames">How many frames after a hit to refuse another, as every scanner takes it.</param>
    public StimulusTrigger(long periodFrames, int holdoffFrames = 0)
    {
        if (periodFrames < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(periodFrames), periodFrames, "The stimulus repeat must be at least one frame.");
        }

        if (holdoffFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(holdoffFrames), holdoffFrames, "The holdoff cannot be negative.");

        _periodFrames = periodFrames;
        _holdoffFrames = holdoffFrames;
    }

    /// <summary>
    /// Nothing to forget.
    ///
    /// <para>The phase is the absolute frame clock's, so a reset — a dropped block, a range step —
    /// leaves it exactly where it was, which is the point: the generator did not stop counting
    /// either. The holdoff is kept for the same reason, being a count on the same clock, which is
    /// why <paramref name="keepHoldoff"/> is accepted and ignored: this scanner has behaved that
    /// way since it was written, and the flag exists so the other five can.</para>
    /// </summary>
    public void Reset(bool keepHoldoff = false)
    {
    }

    public bool Scan(ReadOnlySpan<double> samples, long firstAbsoluteIndex, out TriggerHit hit)
    {
        hit = default;
        if (samples.IsEmpty)
            return false;

        long start = Math.Max(firstAbsoluteIndex, _holdoffUntil);
        long end = firstAbsoluteIndex + samples.Length;
        if (start >= end)
            return false;

        // The first index at or after `start` that sits on the stimulus's own grid. Modulo of a
        // negative index is negative in C#, so it is nudged back onto [0, period) -- the index is
        // non-negative in practice and this costs one comparison for not having to think about it.
        long into = start % _periodFrames;
        if (into < 0)
            into += _periodFrames;

        long at = into == 0 ? start : start + (_periodFrames - into);
        if (at >= end)
            return false;

        // Exact: the event is a frame index rather than a crossing between two samples, so there is
        // nothing to interpolate and the fraction is a true zero rather than a rounded one.
        hit = new TriggerHit(at, 0.0);

        // At least one frame past the hit even with no holdoff asked for, so the same index can
        // never fire twice however a caller resumes. The engine resumes two samples on, so this is
        // belt and braces -- but a scanner whose answer depends on where it is resumed from is a
        // scanner that will eventually be resumed somewhere else.
        _holdoffUntil = at + Math.Max(1, _holdoffFrames);
        return true;
    }
}
