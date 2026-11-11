namespace Tonebench.Dsp;

/// <summary>
/// How much of a deconvolved transfer function is signal, estimated from the roughness of its own
/// phase — so a channel that is carrying nothing can be told from one that is, <b>from a single
/// record</b>.
///
/// <para><b>Why it has to be phase roughness and not any of the obvious things.</b> Coherence is the
/// usual answer and is unavailable here: it is identically 1 for one record
/// (<see cref="FrequencyResponseResult.Coherence"/> says so), and one record is this app's default.
/// Level is unavailable too, or rather is unusable: a reference tap may legitimately be through a
/// divider, a DUT may legitimately have 60 dB of gain, and an uncalibrated sound card has no volts
/// scale at all, so no absolute or relative level threshold survives contact with a real bench.
/// And the deconvolution itself gives nothing away — it reproduces the captured spectrum bin for
/// bin wherever the stimulus has energy, so there is no residual to measure.</para>
///
/// <para><b>What does work is that a transfer function is smooth and noise is not.</b> Any real
/// path — a cable, a delay, a filter however steep — has a phase that moves by nearly the same
/// amount from each bin to the next, so its <i>second</i> difference is essentially zero. Measured:
/// a pure delay through an eight-pole filter sitting at 0.94 of Nyquist, which is about as much
/// phase as a converter can put on a band, gives a second-difference RMS small enough to read as
/// <b>160 dB</b> of signal-to-noise. Noise added to that signal shows up as bin-to-bin jitter
/// directly: for a per-bin signal-to-noise ratio <c>r</c> the phase error is about <c>1/r</c>
/// radians, and the RMS of a second difference of independent errors is <c>√6</c> times that. So
/// <c>r ≈ √6 / σ₂</c>, and the whole estimate is one pass over an array that already exists.</para>
///
/// <para><b>Measured against known SNRs</b> (a delayed signal plus complex noise, 16 000 bins):
/// 60 dB reads 62.9, 40 reads 42.8, 30 reads 32.7, 20 reads 22.9, 10 reads 12.6, 0 reads 2.1 and
/// −20 reads −0.3. So it is good to two or three decibels over the whole range that matters and
/// saturates near zero rather than running away, which is the right behaviour at the end where the
/// only question being asked is "is anything there at all".</para>
///
/// <para><b>It bounds the noise, which is not the same as validating the signal.</b> A channel
/// carrying a loud, clean signal that is not the stimulus — an unrelated source, or heavy crosstalk
/// from the other channel — reads a high SNR here and is still the wrong thing to divide by. This
/// answers "is this channel carrying a transfer function"; it cannot answer "is it carrying
/// <i>mine</i>".</para>
/// </summary>
public static class TransferFunctionSnr
{
    /// <summary>
    /// Fewer bins than this and the estimate is itself noise. A second difference needs three bins;
    /// this is "a band, rather than a corner of one", and matches
    /// <see cref="ChirpDriftResidual.MinBins"/> for the same reason.
    /// </summary>
    public const int MinBins = 64;

    /// <summary>
    /// The per-bin signal-to-noise ratio of a deconvolved transfer function, in dB, or null where
    /// too few bins are usable to say.
    /// </summary>
    /// <param name="phaseDeg">
    /// The unwrapped phase, bin 0 upwards. <b>Unwrapped</b> rather than folded: a wrap is a 360°
    /// step, and a second difference would read every one of them as enormous roughness.
    /// </param>
    /// <param name="weight">
    /// Per bin, non-negative; zero drops the bin. <b>A bin whose phase was held rather than measured
    /// must weigh nothing</b> — <see cref="FrequencyResponseEstimator.PhaseFloorRelative"/> holds
    /// the phase constant below the trace's own magnitude floor, and a constant has a second
    /// difference of exactly zero, so a run of held bins reads as a perfect channel.
    /// </param>
    /// <param name="firstBin">First bin to measure, inclusive.</param>
    /// <param name="lastBin">Last bin to measure, inclusive.</param>
    public static double? FromPhaseRoughness(
        ReadOnlySpan<double> phaseDeg, ReadOnlySpan<double> weight, int firstBin, int lastBin)
    {
        int lo = Math.Max(0, firstBin);
        int hi = Math.Min(Math.Min(phaseDeg.Length, weight.Length) - 1, lastBin);
        if (hi - lo + 1 < MinBins) return null;

        double sum = 0;
        int used = 0;

        // Only where all three bins of the difference are themselves measurements, so a held or
        // dropped bin cannot contribute a spurious zero *or* a spurious step at its edges.
        for (int k = lo + 1; k < hi; k++)
        {
            if (!(weight[k - 1] > 0) || !(weight[k] > 0) || !(weight[k + 1] > 0)) continue;
            if (!double.IsFinite(phaseDeg[k - 1]) || !double.IsFinite(phaseDeg[k]) || !double.IsFinite(phaseDeg[k + 1]))
                continue;

            double d2 = phaseDeg[k + 1] - 2 * phaseDeg[k] + phaseDeg[k - 1];
            sum += d2 * d2;
            used++;
        }

        if (used < MinBins) return null;

        double sigma2Rad = Math.Sqrt(sum / used) * Math.PI / 180.0;

        // Exactly smooth: a synthetic trace, or one whose phase is held across the whole band
        // despite the weights. Reported as the cap rather than as infinity, which would be a figure
        // nothing downstream could compare or serialise.
        if (!(sigma2Rad > 0)) return MaxSnrDb;

        double snr = Math.Sqrt(6.0) / sigma2Rad;
        return Math.Clamp(20.0 * Math.Log10(snr), MinSnrDb, MaxSnrDb);
    }

    /// <summary>
    /// The floor the estimate saturates at. Below about 0 dB the phase is uniformly distributed and
    /// the measurement stops distinguishing degrees of hopeless, so a more negative figure would be
    /// a precision this cannot deliver.
    /// </summary>
    public const double MinSnrDb = -10.0;

    /// <summary>
    /// And the ceiling. A noise-free synthetic trace reads 200 dB, which is a statement about
    /// double precision rather than about a bench.
    /// </summary>
    public const double MaxSnrDb = 200.0;
}
