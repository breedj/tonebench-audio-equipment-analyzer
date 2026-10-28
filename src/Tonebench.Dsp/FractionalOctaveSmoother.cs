using System.Numerics;

namespace Tonebench.Dsp;

/// <summary>
/// Fractional-octave smoothing of a magnitude trace: each output bin is the RMS of the input bins
/// falling inside a band of constant <i>relative</i> width centred on it.
///
/// <para><b>Power averaging, not dB averaging.</b> Averaging decibels is averaging logarithms, which
/// biases every result toward the nulls — a notch pulls the smoothed curve down far more than the
/// peak either side of it pulls it up. Averaging power and converting once at the end is the
/// physically meaningful operation, and it is what makes a smoothed response still integrate to the
/// same energy. Input and output are therefore <b>linear magnitudes</b>; convert to dB afterwards.
/// </para>
///
/// <para><b>The output grid stays the input's linear bin grid.</b> The reference implementations
/// resample onto a log-spaced grid at the same time, which conflates two independent things. Here
/// the trace keeps its <c>k · binHz</c> frequencies, so a smoothed and an unsmoothed trace remain
/// bin-for-bin comparable, <see cref="BandwidthAnalyzer"/> needs no separate grid, and the chart
/// can switch smoothing without re-deriving an x axis.</para>
///
/// <para><b>Summed in blocks, not from a running integral over the whole trace.</b> The obvious
/// implementation keeps one cumulative power array and takes a band as a single subtraction — O(N)
/// whatever the width, which matters because a 1/3-octave band at 20 kHz spans several thousand
/// bins and the naive form is quadratic at the 0.37 Hz resolution setting. It is also wrong on
/// exactly the traces this app produces. The integral is dominated by the passband, so recovering a
/// stopband band's power means subtracting two numbers of order 1e3 to get 1e-11, which is far
/// below the spacing of doubles that size: the answer is rounding, and rounding is often exactly
/// zero. Measured on the simulated Butterworth at a 96 kHz span, 1/24 octave: unsmoothed the trace
/// followed the filter to −138 dB at 40 kHz, smoothed it read <b>−600 dB</b> — the estimator's
/// zero floor — from 38 kHz up, which on the Bode plot is a cliff and reads as a broken DUT.
/// Summing per band from a block table touches only the bins inside the band, so the arithmetic
/// never mixes a passband bin into a stopband answer. Cost is O(N·(W/B + 2B)) with B = 128: a few
/// hundred operations per bin at the widest setting, once per pass.</para>
/// </summary>
public static class FractionalOctaveSmoother
{
    /// <summary>The <c>1/N</c> octave denominators the UI offers; 0 means no smoothing.</summary>
    public static readonly int[] SupportedDenominators = [0, 69, 48, 24, 12, 6, 3];

    /// <summary>
    /// Smooths <paramref name="magnitude"/> into <paramref name="destination"/> (same length; may be
    /// the same span). <paramref name="denominator"/> 0 copies through unchanged.
    /// </summary>
    /// <param name="magnitude">Linear magnitude per bin, bin <c>k</c> at <c>k · binHz</c>.</param>
    /// <param name="destination">Receives the smoothed linear magnitudes.</param>
    /// <param name="binHz">Bin spacing.</param>
    /// <param name="denominator">Octave denominator: 3 is 1/3-octave (widest), 69 the narrowest, 0 none.</param>
    public static void Smooth(
        ReadOnlySpan<double> magnitude,
        Span<double> destination,
        double binHz,
        int denominator,
        Band? band = null)
    {
        if (destination.Length != magnitude.Length)
            throw new ArgumentException("Destination must be the same length as the input.", nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(denominator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);

        if (denominator == 0 || magnitude.Length < 3)
        {
            magnitude.CopyTo(destination);
            return;
        }

        int n = magnitude.Length;

        // Per-bin power once, then per block of bins. Deliberately not a running total across the
        // whole trace -- see the remarks above for what that costs on a trace with a deep stopband.
        double[] power = new double[n];
        for (int i = 0; i < n; i++)
            power[i] = magnitude[i] * magnitude[i];

        double[] blockPower = BlockTotals(power);

        double halfWidthRatio = HalfWidthRatio(denominator);
        var limit = (band ?? Band.Whole(n)).ClampedTo(n);

        // DC has no centre frequency to build a relative band around, and the chart skips it anyway.
        destination[0] = magnitude[0];

        for (int k = 1; k < n; k++)
        {
            if (!TryBandBins(k, limit, halfWidthRatio, out int low, out int high))
            {
                destination[k] = magnitude[k];
                continue;
            }

            double bandPower = BandSum(power, blockPower, low, high);
            destination[k] = Math.Sqrt(bandPower / (high - low + 1));
        }
    }

    /// <summary>
    /// Half-width as a frequency ratio: a 1/N-octave band spans <c>2^(1/N)</c>, so each edge sits
    /// <c>2^(1/2N)</c> away from the centre.
    /// </summary>
    private static double HalfWidthRatio(int denominator) => Math.Pow(2.0, 1.0 / (2.0 * denominator));

    /// <summary>
    /// The bins a 1/N-octave band centred on bin <paramref name="k"/> covers, or false when the band
    /// is narrower than one bin.
    ///
    /// <para>Rounding inward (ceil the low edge, floor the high) is what makes a band narrower than
    /// one bin come out empty rather than silently widening to two. That happens below roughly
    /// <c>denominator · 1.44</c> bins, which is most of the bottom decade at 1/24 octave, and every
    /// caller answers it the same way: fall back to the bin itself, so the low end of the trace keeps
    /// its full resolution instead of blanking.</para>
    /// </summary>
    private static bool TryBandBins(int k, Band band, double halfWidthRatio, out int low, out int high)
    {
        low = Math.Max((int)Math.Ceiling(k / halfWidthRatio), band.FirstBin);
        high = Math.Min((int)Math.Floor(k * halfWidthRatio), band.LastBin);
        return high >= low;
    }

    /// <summary>
    /// The bins a smoothing band may reach into: the <b>analysed</b> band, not the whole spectrum.
    ///
    /// <para><b>Why a band at all.</b> A 1/3-octave window centred on the top analysed bin reaches
    /// 12 % past it, and on a swept measurement there is nothing up there: the chirp stops at
    /// 0.98 of Nyquist and the analysed band at 0.95, so the top of the window is filled with bins
    /// whose ratio is noise over nothing. For a frequency response that reads as a lifted top
    /// octave; for an <i>impedance</i> it is far worse, because <c>Z = R·H/(1−H)</c> of a noise
    /// ratio that wanders near 1 is clamped at <see cref="Tonebench.Dsp"/>'s own ceiling, and an RMS
    /// that includes one such bin is that bin. Reported 2026-10-20 as a spike at the end of the
    /// trace at 1/3 octave that is "less high" at 1/12 — which is the arithmetic exactly: 1/12
    /// reaches 0.978 of Nyquist and stops just short of the dead band, 1/3 reaches past it.</para>
    ///
    /// <para><see cref="GroupDelay"/> has clamped its own regression window to this band from the
    /// start, for the same reason in the phase: out-of-band unwrapped phase is random turns.</para>
    /// </summary>
    /// <param name="FirstBin">The lowest bin a band may include; never 0, which has no centre frequency.</param>
    /// <param name="LastBin">The highest.</param>
    public readonly record struct Band(int FirstBin, int LastBin)
    {
        /// <summary>The whole trace, for a caller with no analysed band of its own.</summary>
        public static Band Whole(int binCount) => new(1, binCount - 1);

        /// <summary>Clamped into a trace of <paramref name="binCount"/> bins, and never inverted.</summary>
        public Band ClampedTo(int binCount)
        {
            int first = Math.Clamp(FirstBin, 1, Math.Max(1, binCount - 1));
            int last = Math.Clamp(LastBin, first, Math.Max(1, binCount - 1));
            return new Band(first, last);
        }
    }

    /// <summary>
    /// Bins per block in the table above. The per-bin cost is <c>W/B + 2B</c> for a band of
    /// <c>W</c> bins, so this trades the wide bands (thousands of bins at 1/3 octave) against the
    /// narrow ones, which pay the two partial ends whatever their width.
    /// </summary>
    private const int BlockBins = 128;

    /// <summary>
    /// The sum over <c>[low, high]</c>, from those bins and no others: whole blocks straight off the
    /// table, the partial block at each end bin by bin. A band narrower than the blocks it touches is
    /// summed directly.
    /// </summary>
    private static double BandSum(double[] binValue, double[] blockValue, int low, int high)
    {
        // The first bin of the first block the band covers completely, and one past the last bin of
        // the last one. When they cross, no block is fully inside the band.
        int firstWhole = ((low + BlockBins - 1) / BlockBins) * BlockBins;
        int lastWholeEnd = ((high + 1) / BlockBins) * BlockBins;

        double sum = 0.0;

        if (firstWhole >= lastWholeEnd)
        {
            for (int i = low; i <= high; i++)
                sum += binValue[i];

            return sum;
        }

        for (int i = low; i < firstWhole; i++)
            sum += binValue[i];

        for (int b = firstWhole / BlockBins; b < lastWholeEnd / BlockBins; b++)
            sum += blockValue[b];

        for (int i = lastWholeEnd; i <= high; i++)
            sum += binValue[i];

        return sum;
    }

    /// <summary>The block totals of a per-bin series, which is the other half of what <see cref="BandSum"/> reads.</summary>
    private static double[] BlockTotals(double[] bins)
    {
        var blocks = new double[((bins.Length - 1) / BlockBins) + 1];
        for (int i = 0; i < bins.Length; i++)
            blocks[i / BlockBins] += bins[i];

        return blocks;
    }

    /// <summary>Allocating convenience overload, for callers that are not reusing a buffer.</summary>
    public static double[] Smooth(ReadOnlySpan<double> magnitude, double binHz, int denominator, Band? band = null)
    {
        double[] result = new double[magnitude.Length];
        Smooth(magnitude, result, binHz, denominator, band);
        return result;
    }

    /// <summary>
    /// The angle of <paramref name="values"/> smoothed over the same bands the magnitude is, in
    /// degrees in <c>(−180, 180]</c>.
    ///
    /// <para><b>A vector mean, not an average of angles.</b> Each output bin is
    /// <c>arg(Σ z)</c> over its band: the band's phasors are added and the argument of the sum
    /// taken. Averaging the angles themselves is wrong twice over. It cannot cross the wrap — +179°
    /// and −179° are 2° apart and average to <b>0°</b>, the opposite direction — and it gives a bin
    /// carrying no signal the same vote as one carrying all of it, when a bin with nothing in it has
    /// no angle at all, only noise. Adding the phasors answers both: the wrap is not represented, and
    /// the weighting by magnitude falls out of the arithmetic rather than being bolted on.</para>
    ///
    /// <para><b>Why this is worth doing at all.</b> The magnitude column of a stored trace is an RMS
    /// over a fractional-octave band and the phase column was the <i>centre bin</i> of that band —
    /// one bin of the ten or more the band spans, raw. On a measurement whose phase is well
    /// determined the two agree; on one where it is not, the magnitude is smoothed into a readable
    /// curve while the phase beside it is drawn as the full-resolution noise it always was, which
    /// reads as a broken measurement rather than a noisy one. Reported 2026-10-20 against a sub-ohm
    /// impedance fixture, where the chirp's own coherence falls to 0.07 at some frequencies.</para>
    ///
    /// <para>A band whose phasors <b>cancel completely</b> keeps the bin's own angle: the resultant
    /// is then a rounding error, and its direction says nothing. The test is the resultant against
    /// the band's own total length (<c>|Σ z| ≤ Σ|z| · 1e-12</c>), not against zero — equal and
    /// opposite phasors do not sum to exactly zero in floating point, and the residue of a band of
    /// 1024 of them pointed at <b>90°</b>, a direction not one of its bins had. Deliberately a
    /// <i>numerical</i> floor rather than a quality one: a noisy band also has a short resultant, and
    /// there the mean is the best estimate available and worth having.</para>
    /// </summary>
    /// <param name="values">The complex curve, bin <c>k</c> at <c>k · binHz</c>.</param>
    public static double[] SmoothPhaseDeg(
        ReadOnlySpan<Complex> values, double binHz, int denominator, Band? band = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(denominator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);

        int n = values.Length;
        var result = new double[n];
        for (int k = 0; k < n; k++)
            result[k] = values[k].Phase * 180.0 / Math.PI;

        if (denominator == 0 || n < 3) return result;

        var realBins = new double[n];
        var imagBins = new double[n];
        var lengthBins = new double[n];
        for (int k = 0; k < n; k++)
        {
            realBins[k] = values[k].Real;
            imagBins[k] = values[k].Imaginary;
            lengthBins[k] = values[k].Magnitude;
        }

        var realBlocks = BlockTotals(realBins);
        var imagBlocks = BlockTotals(imagBins);
        var lengthBlocks = BlockTotals(lengthBins);

        double halfWidthRatio = HalfWidthRatio(denominator);
        var limit = (band ?? Band.Whole(n)).ClampedTo(n);

        for (int k = 1; k < n; k++)
        {
            if (!TryBandBins(k, limit, halfWidthRatio, out int low, out int high)) continue;

            double sumReal = BandSum(realBins, realBlocks, low, high);
            double sumImaginary = BandSum(imagBins, imagBlocks, low, high);
            double resultant = Math.Sqrt((sumReal * sumReal) + (sumImaginary * sumImaginary));

            if (resultant <= BandSum(lengthBins, lengthBlocks, low, high) * CancelledResultant) continue;

            result[k] = Math.Atan2(sumImaginary, sumReal) * 180.0 / Math.PI;
        }

        return result;
    }

    /// <summary>
    /// How short a band's resultant may be, against the total length of the phasors that made it,
    /// before it is read as complete cancellation rather than as a direction.
    /// </summary>
    private const double CancelledResultant = 1e-12;

    /// <summary>
    /// The complex <b>arithmetic mean</b> over the same 1/N-octave bands.
    ///
    /// <para><b>A mean, where every other smoother here takes an RMS or a magnitude-weighted
    /// average — and that difference is the whole reason this exists.</b> It is for a quantity
    /// something downstream will put through a <i>nonlinear</i> map, where the averaging has to
    /// happen first or it cannot help at all. Noise on a measured complex quantity is additive and
    /// zero mean, so summing it as a vector cancels it; once that quantity has been mapped to
    /// something a magnitude is then taken of, the noise has been <i>rectified</i> — it pushed the
    /// value away from the truth in every direction and the magnitude kept the distance — and no
    /// amount of later averaging can bring it back, because there is nothing left to cancel
    /// against. Smooth first, map afterwards.</para>
    ///
    /// <para>The impedance screen is what uses it: <c>Z = R·H/(1 − H)</c> is solved per bin and the
    /// complex Z averaged here, so the magnitude is taken last. On a sub-ohm DUT read at a low
    /// drive that took the band median from <b>+2.1 % to −0.1 %</b> and the share of columns
    /// reading high from 73 % to 53 % — 50 % being none at all.</para>
    ///
    /// <para>It was <i>found</i> on the output-impedance measurement, which amplified the same
    /// fault twelvefold: <c>Z = R₁·R₂·(q − 1)/(R₂ − R₁·q)</c> recovers ohms from a gain ratio
    /// sitting within a decibel of one, and solving per bin read <b>1.24 Ω for a 0.88 Ω
    /// amplifier</b> on a real bench (2026-10-28). That measurement is the stepped sine only now,
    /// so this has one caller — but the <i>reason</i> is general, and any future code that takes a
    /// magnitude of something it averaged wants this rather than <see cref="Smooth"/>.</para>
    ///
    /// <para>A band narrower than one bin leaves that bin alone, and a
    /// <paramref name="denominator"/> of 0 means no smoothing at all, both as the other smoothers
    /// do it.</para>
    /// </summary>
    public static Complex[] SmoothComplex(
        ReadOnlySpan<Complex> values, double binHz, int denominator, Band? band = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(denominator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);

        int n = values.Length;
        var result = values.ToArray();
        if (denominator == 0 || n < 3) return result;

        var realBins = new double[n];
        var imagBins = new double[n];
        for (int k = 0; k < n; k++)
        {
            realBins[k] = values[k].Real;
            imagBins[k] = values[k].Imaginary;
        }

        var realBlocks = BlockTotals(realBins);
        var imagBlocks = BlockTotals(imagBins);

        double halfWidthRatio = HalfWidthRatio(denominator);
        var limit = (band ?? Band.Whole(n)).ClampedTo(n);

        for (int k = 1; k < n; k++)
        {
            if (!TryBandBins(k, limit, halfWidthRatio, out int low, out int high)) continue;

            double count = high - low + 1;
            result[k] = new Complex(
                BandSum(realBins, realBlocks, low, high) / count,
                BandSum(imagBins, imagBlocks, low, high) / count);
        }

        return result;
    }

    /// <summary>
    /// An <b>unwrapped</b> phase smoothed over the same bands, weighted by magnitude, in degrees —
    /// the frequency response's counterpart to <see cref="SmoothPhaseDeg"/>.
    ///
    /// <para><b>A weighted mean of the angles, which is exactly what the phasor sum must not be used
    /// for here.</b> A frequency-response phase is unwrapped: it carries the whole turns a delayed
    /// DUT runs through, the bandwidth reference anchors them away, and the client puts a removed
    /// delay back by adding to them. Taking the argument of a phasor sum would discard every one of
    /// those turns and hand back a value in <c>(−180, 180]</c>, so a DUT reading −1440° would come
    /// back reading 0°. Weighting by magnitude buys the other half of what the phasor sum gives for
    /// free: a notch inside the band, where the phase swings through half a turn over a few bins
    /// that carry almost nothing, no longer drags the column it sits in.</para>
    ///
    /// <para>A band carrying no magnitude at all falls back to the bin's own phase, for the reason
    /// the phasor version falls back: there is nothing to weight with.</para>
    /// </summary>
    public static double[] SmoothUnwrappedPhaseDeg(
        ReadOnlySpan<double> phaseDeg,
        ReadOnlySpan<double> magnitude,
        double binHz,
        int denominator,
        Band? band = null)
    {
        if (magnitude.Length != phaseDeg.Length)
            throw new ArgumentException("The magnitude must be the same length as the phase.", nameof(magnitude));
        ArgumentOutOfRangeException.ThrowIfNegative(denominator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);

        int n = phaseDeg.Length;
        var result = phaseDeg.ToArray();
        if (denominator == 0 || n < 3) return result;

        var weightBins = magnitude.ToArray();
        var productBins = new double[n];
        for (int k = 0; k < n; k++)
            productBins[k] = weightBins[k] * phaseDeg[k];

        var weightBlocks = BlockTotals(weightBins);
        var productBlocks = BlockTotals(productBins);

        double halfWidthRatio = HalfWidthRatio(denominator);
        var limit = (band ?? Band.Whole(n)).ClampedTo(n);

        for (int k = 1; k < n; k++)
        {
            if (!TryBandBins(k, limit, halfWidthRatio, out int low, out int high)) continue;

            double sumWeight = BandSum(weightBins, weightBlocks, low, high);
            if (!(sumWeight > 0.0)) continue;

            result[k] = BandSum(productBins, productBlocks, low, high) / sumWeight;
        }

        return result;
    }
}
