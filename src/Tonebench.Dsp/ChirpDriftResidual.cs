namespace Tonebench.Dsp;

/// <summary>
/// How much clock drift a deconvolved sweep still carries, as <see cref="ChirpDriftResidual.Fit"/>
/// measures it.
/// </summary>
/// <param name="ResidualPpm">
/// The ε left on the trace, in <see cref="ToneFrequency.DriftPpm"/>'s sign convention. On a run
/// corrected by a stored figure this is <c>ε_true − ε_stored</c>, i.e. <b>the error in the stored
/// figure</b>; on an uncorrected run it is ε itself.
/// </param>
/// <param name="SigmaPpm">
/// The fit's own standard error on that coefficient.
///
/// <para><b>Optimistic, deliberately reported anyway, and nothing decides on it.</b> It is the
/// textbook least-squares error, which assumes the bins are independent — and they are not: the
/// phase of neighbouring bins of one deconvolution is correlated, so with sixteen thousand of them
/// in a band this comes out far smaller than the estimate's real uncertainty. It is still the right
/// thing for this method to return, because it is what says the <i>fit</i> was determined at all
/// (a band of fifty usable bins reports a large one), and it is worth a log line. What it is not is
/// a threshold: see <see cref="FitRmsDeg"/> for the quantity that bounds the real error, and the
/// client's own rule for what is worth telling a reader.</para>
/// </param>
/// <param name="Bins">How many bins the fit used, after weighting dropped the rest.</param>
/// <param name="FitRmsDeg">
/// How far the three-term model missed the measured phase, weighted-RMS, in degrees. <b>This is the
/// honest confidence figure.</b> The model is "a constant, a bulk delay, and the sweep's own
/// logarithmic ramp"; everything the DUT does that is not one of those three lands here, so a
/// loopback reads a fraction of a degree and a loudspeaker reads tens. A residual much smaller than
/// this is a residual the DUT's own phase could have manufactured.
/// </param>
public sealed record ChirpDriftResidualFit(
    double ResidualPpm, double SigmaPpm, int Bins, double FitRmsDeg);

/// <summary>
/// Reads the clock drift a measurement still carries off the measurement itself, so a stored drift
/// figure can be told it has gone stale without the bench being re-wired into a loopback.
///
/// <para><b>Why this is possible at all.</b> <see cref="ChirpClockDrift"/> explains that two clocks
/// put a delay on a deconvolved sweep that grows with frequency, of exactly the shape
/// <c>θ(f) = ε·2πf·t(f)</c> where <c>t(f)</c> is the sweep's own frequency-to-time map. That shape
/// is <see cref="ChirpTimeMap.RampRadiansPerEpsilon"/>, and ε is the only unknown in it — so
/// regressing a measured phase onto it recovers ε. A run already corrected by a stored figure has
/// <c>ε_true − ε_stored</c> left, which is the staleness, straight out.</para>
///
/// <para><b>What else is in the phase, and how it is separated.</b> Three terms are fitted, not
/// one. A constant absorbs the phase anchor; a term linear in <c>f</c> absorbs any bulk delay,
/// which is both the DUT's own propagation delay and whatever rotation the estimator removed; and
/// the ramp term is the answer. The linear term is not optional — a bulk delay is the one thing
/// most likely to be confused with a ramp, and over a span of two or three decades <c>f·ln f</c> and
/// <c>f</c> are similar enough that leaving it out hands a DUT's delay straight to ε.</para>
///
/// <para><b>What cannot be separated, which is the limit of the method.</b> A DUT's own excess phase
/// is whatever it is, and part of it projects onto the ramp. The size of the problem is knowable:
/// one pole's whole phase swing is 90° — 1.6 radians — where on this project's bench a 104 ppm drift
/// over a 0.68 s record is <b>611°</b> at 20 kHz, so the ramp outweighs a filter corner by seven
/// times. That makes this reliable for the question it is asked ("is the stored figure tens of ppm
/// out?") and useless for the question it is not ("what is ε, to a tenth of a ppm?") — which is the
/// clock-drift test's job, with a known tone and no DUT in the answer.</para>
///
/// <para><b>So it is pointed at the cleanest trace available.</b> Where the right input is the
/// reference, that channel is fed the stimulus rather than the DUT's output, so its response
/// against the synthetic chirp holds the ramp and no DUT at all — and that is the panel's default.
/// See <c>ResponseCaptureSupport.EstimateDriftResidual</c>, which chooses, and which is also where
/// the weighting lives.</para>
///
/// <para><b>Weighted, and the weights matter more than the fit.</b> A bin the stimulus never
/// reached, or one in a DUT's stopband, carries a phase that is a property of the noise floor; the
/// estimator even <i>holds</i> the phase constant below its own magnitude floor rather than
/// unwrapping noise. Those bins must weigh nothing, or the fit is dominated by the quietest part of
/// the band.</para>
/// </summary>
public static class ChirpDriftResidual
{
    /// <summary>
    /// Fewer usable bins than this and there is no fit worth reporting. Generous rather than tight:
    /// the three-term model needs four, and this is "a band, rather than a corner of one".
    /// </summary>
    public const int MinBins = 64;

    /// <summary>
    /// How far the three-term fit may miss the measured phase, in degrees rms, before the ppm it
    /// produces means nothing.
    ///
    /// <para>A drift ramp plus a bulk delay plus a constant <i>is</i> the phase where this
    /// measurement applies, so a genuine fit leaves a fraction of a degree — the synthetic cases
    /// in <c>ChirpDriftResidualTests</c> land at essentially zero. Measured on a real two-card
    /// bench the misses ran <b>14.8 to 42.3 degrees</b>, and every one of those readings was wrong
    /// by hundreds of ppm. Five is comfortably between the two and well clear of either, which is
    /// what a threshold wants to be: nothing in the measurements lies near it.</para>
    ///
    /// <para>It is a constant here rather than in the caller because it is a property of the model
    /// this class fits, and the figure it guards is the one this class returns.</para>
    /// </summary>
    public const double MaxFitRmsDeg = 5.0;

    /// <summary>
    /// Fits the ramp to a measured phase and returns the ε left on it, or null where there is
    /// nothing to fit — too few usable bins, or a design matrix a band that narrow cannot
    /// condition.
    /// </summary>
    /// <param name="phaseDeg">
    /// The unwrapped phase, bin 0 upwards, as <see cref="FrequencyResponseResult.PhaseDeg"/> gives
    /// it. <b>Unwrapped</b> is not negotiable: the ramp runs through many whole turns — hundreds of
    /// degrees at the top of the band on an ordinary two-card bench — so a phase folded into
    /// (−180°, 180°] has had the quantity being measured removed from it.
    /// </param>
    /// <param name="weight">
    /// Per bin, non-negative; zero drops the bin. Not normalised — only ratios matter.
    /// </param>
    /// <param name="binHz">The bin spacing, which is the reciprocal of the map's period.</param>
    /// <param name="map">The sweep's time map. See <see cref="ChirpTimeMap"/> for why it is shared.</param>
    /// <param name="firstBin">First bin to fit, inclusive — the bottom of the analysed band.</param>
    /// <param name="lastBin">Last bin to fit, inclusive.</param>
    public static ChirpDriftResidualFit? Fit(
        ReadOnlySpan<double> phaseDeg,
        ReadOnlySpan<double> weight,
        double binHz,
        ChirpTimeMap map,
        int firstBin,
        int lastBin)
    {
        if (binHz <= 0 || !double.IsFinite(binHz)) return null;

        int lo = Math.Max(1, firstBin);
        int hi = Math.Min(Math.Min(phaseDeg.Length, weight.Length) - 1, lastBin);
        if (hi - lo + 1 < MinBins) return null;

        // The three basis columns are scaled to unit maximum before the normal equations are
        // formed. Unscaled they span ten orders of magnitude -- a constant 1 against a ramp term
        // that reaches 1e5 radians per unit epsilon -- and a 3x3 normal matrix built from that is
        // numerically poor for no reason. The coefficient is divided back out at the end.
        double fScale = 0, rScale = 0;
        for (int k = lo; k <= hi; k++)
        {
            if (!(weight[k] > 0) || !double.IsFinite(phaseDeg[k])) continue;
            double f = k * binHz;
            fScale = Math.Max(fScale, f);
            rScale = Math.Max(rScale, Math.Abs(map.RampRadiansPerEpsilon(f)));
        }

        if (!(fScale > 0) || !(rScale > 0)) return null;

        // Symmetric 3x3 normal matrix and its right-hand side, basis [1, f, ramp].
        double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0;
        double b0 = 0, b1 = 0, b2 = 0;
        double totalWeight = 0;
        int bins = 0;

        for (int k = lo; k <= hi; k++)
        {
            double w = weight[k];
            if (!(w > 0) || !double.IsFinite(w) || !double.IsFinite(phaseDeg[k])) continue;

            double f = k * binHz;
            double g1 = f / fScale;
            double g2 = map.RampRadiansPerEpsilon(f) / rScale;
            double y = phaseDeg[k] * Math.PI / 180.0;

            a00 += w;
            a01 += w * g1;
            a02 += w * g2;
            a11 += w * g1 * g1;
            a12 += w * g1 * g2;
            a22 += w * g2 * g2;
            b0 += w * y;
            b1 += w * g1 * y;
            b2 += w * g2 * y;
            totalWeight += w;
            bins++;
        }

        if (bins < MinBins) return null;

        // Analytic inverse of the symmetric 3x3. Well conditioned once the columns are scaled, and
        // the only element of the inverse the standard error needs is the last diagonal one.
        double m0 = a11 * a22 - a12 * a12;
        double m1 = a01 * a22 - a12 * a02;
        double m2 = a01 * a12 - a11 * a02;
        double det = a00 * m0 - a01 * m1 + a02 * m2;
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-30) return null;

        double c0 = (b0 * m0 - b1 * m1 + b2 * m2) / det;
        double c1 = (-b0 * m1 + b1 * (a00 * a22 - a02 * a02) - b2 * (a00 * a12 - a01 * a02)) / det;
        double c2 = (b0 * m2 - b1 * (a00 * a12 - a01 * a02) + b2 * (a00 * a11 - a01 * a01)) / det;
        if (!double.IsFinite(c0) || !double.IsFinite(c1) || !double.IsFinite(c2)) return null;

        double inv22 = (a00 * a11 - a01 * a01) / det;
        if (!double.IsFinite(inv22) || inv22 < 0) return null;

        // Residual, for both the reported miss and the standard error. A second pass rather than an
        // algebraic shortcut off the normal equations: that shortcut is a difference of two large
        // nearly equal sums and loses most of its digits on a good fit, which is exactly the case
        // whose number is being reported.
        double chi2 = 0;
        for (int k = lo; k <= hi; k++)
        {
            double w = weight[k];
            if (!(w > 0) || !double.IsFinite(w) || !double.IsFinite(phaseDeg[k])) continue;

            double f = k * binHz;
            double model = c0 + c1 * (f / fScale) + c2 * (map.RampRadiansPerEpsilon(f) / rScale);
            double e = phaseDeg[k] * Math.PI / 180.0 - model;
            chi2 += w * e * e;
        }

        int dof = bins - 3;
        double variance = dof > 0 ? chi2 / dof : 0.0;
        double sigmaScaled = Math.Sqrt(Math.Max(0.0, variance * inv22));

        double rmsRad = totalWeight > 0 ? Math.Sqrt(chi2 / totalWeight) : 0.0;

        return new ChirpDriftResidualFit(
            ResidualPpm: c2 / rScale * 1e6,
            SigmaPpm: sigmaScaled / rScale * 1e6,
            Bins: bins,
            FitRmsDeg: rmsRad * 180.0 / Math.PI);
    }
}
