namespace Qa403.Dsp;

/// <summary>
/// Group delay and phase delay from an unwrapped phase curve. Group delay is
/// <c>τg = −(1/360)·dφ/df</c>, phase delay <c>τφ = −φ/(360·f)</c>, φ in degrees, both in seconds.
///
/// <para><b>Group delay is a least-squares slope, not a bin difference.</b> A derivative amplifies
/// whatever noise the phase carries by <c>1/Δf</c>: 0.01° of phase noise on 0.37 Hz bins is 75 µs of
/// group delay per bin, on a DUT whose true midband group delay is a few microseconds. Over the
/// fractional-octave window the measurement already smooths its magnitude with, the slope of a
/// straight-line fit through every bin in the window is about <c>√(N/6)</c> less noisy than
/// differentiating and then averaging the same window — the latter only ever uses the window's two
/// end points. With no smoothing the estimate falls back to a central difference, which is honest
/// and noisy, and the manual says so.</para>
///
/// <para><b>The window is clamped to the analysed band, not to the array.</b>
/// <see cref="FractionalOctaveSmoother"/> can afford to reach outside the band because the
/// regulariser has already pinned out-of-band magnitudes near zero; out-of-band <i>unwrapped
/// phase</i> is random turns, and a regression that admits any of it produces wild slopes at both
/// band edges. Bins outside the band are written as 0, never NaN — these arrays go on the wire and
/// into JSON, where a NaN truncates the whole response.</para>
///
/// <para><b>Phase delay needs an anchor on a chirp and none on a stepped sweep.</b>
/// <see cref="FrequencyResponseEstimator.Complete"/> starts its unwrapper at bin 0 and walks through
/// the sub-band junk before it reaches the analysed band, so in-band phase carries an arbitrary
/// <c>k·360°</c>; for group delay that is invisible (a constant differentiates away) but phase delay
/// divides by <c>f</c> and reads the offset as <c>k/f</c> of delay. The caller names the bin whose
/// phase is brought into <c>(−180°, 180°]</c> — the bandwidth reference, i.e. the passband — and the
/// whole curve is shifted by that multiple of 360°. A stepped sweep's unwrapper starts at its first
/// tone, and nothing has removed any delay from it, so its phase already carries the physical turns:
/// anchoring it would <i>delete</i> real delay from any DUT slower than half a period at the anchor.
/// It passes no anchor. Either way an inverting DUT reads half a period, which is what the
/// definition says it should.</para>
/// </summary>
public static class GroupDelay
{
    /// <summary>
    /// Group delay per bin of a uniformly spaced phase curve, in seconds. Bins outside
    /// <c>[firstBin, lastBin]</c> are 0.
    /// </summary>
    /// <param name="phaseDeg">Unwrapped phase, bin <c>k</c> at <c>k · binHz</c>.</param>
    /// <param name="binHz">Bin spacing.</param>
    /// <param name="denominator">
    /// Octave denominator of the regression window, <see cref="FractionalOctaveSmoother"/>'s
    /// convention: 0 is a central difference over neighbouring bins.
    /// </param>
    /// <param name="firstBin">First bin of the analysed band (inclusive).</param>
    /// <param name="lastBin">Last bin of the analysed band (inclusive).</param>
    public static double[] FromUniformPhase(
        ReadOnlySpan<double> phaseDeg,
        double binHz,
        int denominator,
        int firstBin,
        int lastBin)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        ArgumentOutOfRangeException.ThrowIfNegative(denominator);
        ValidateBand(phaseDeg.Length, firstBin, lastBin);

        var result = new double[phaseDeg.Length];
        if (phaseDeg.Length == 0)
            return result;

        if (denominator == 0 || lastBin - firstBin < 2)
        {
            for (int k = firstBin; k <= lastBin; k++)
                result[k] = CentralDifferenceS(phaseDeg, binHz, k, firstBin, lastBin);
            return result;
        }

        // Prefix sums over the band of y and x·y, with x measured from the band's first bin so
        // the sums stay small: at 262144 bins from an origin at 0 the x·y sum reaches 1e14 and the
        // regression's cancellation eats the digits the answer needs. Σx and Σx² over a contiguous
        // integer range have closed forms and need no arrays.
        int bandLength = lastBin - firstBin + 1;
        var sumY = new double[bandLength + 1];
        var sumXY = new double[bandLength + 1];
        for (int i = 0; i < bandLength; i++)
        {
            double y = phaseDeg[firstBin + i];
            sumY[i + 1] = sumY[i] + y;
            sumXY[i + 1] = sumXY[i] + (i * y);
        }

        double halfWidthRatio = Math.Pow(2.0, 1.0 / (2.0 * denominator));

        for (int k = firstBin; k <= lastBin; k++)
        {
            // The same edge rule as the smoother (ceil the low edge, floor the high, so a band
            // narrower than a bin comes out empty rather than widening), but clamped to the band.
            int low = Math.Max(firstBin, (int)Math.Ceiling(k / halfWidthRatio));
            int high = Math.Min(lastBin, (int)Math.Floor(k * halfWidthRatio));

            if (high - low < 2)
            {
                result[k] = CentralDifferenceS(phaseDeg, binHz, k, firstBin, lastBin);
                continue;
            }

            int a = low - firstBin;
            int b = high - firstBin;
            double n = b - a + 1;
            double sx = (a + b) * n / 2.0;
            double sxx = SquaresUpTo(b) - SquaresUpTo(a - 1);
            double sy = sumY[b + 1] - sumY[a];
            double sxy = sumXY[b + 1] - sumXY[a];

            double slopeDegPerBin = (sxy - (sx * sy / n)) / (sxx - (sx * sx / n));
            result[k] = -slopeDegPerBin / (360.0 * binHz);
        }

        return result;
    }

    /// <summary>
    /// Group delay per point of a phase curve on an arbitrary (log-spaced) frequency grid, in
    /// seconds: a central difference in frequency, one-sided at the ends. Fewer than two points
    /// have no slope and read 0.
    /// </summary>
    public static double[] FromPhaseOnGrid(ReadOnlySpan<double> phaseDeg, ReadOnlySpan<double> freqHz)
    {
        if (freqHz.Length != phaseDeg.Length)
            throw new ArgumentException("Phase and frequency must be the same length.", nameof(freqHz));

        int n = phaseDeg.Length;
        var result = new double[n];
        if (n < 2)
            return result;

        for (int i = 0; i < n; i++)
        {
            int lo = Math.Max(0, i - 1);
            int hi = Math.Min(n - 1, i + 1);
            double df = freqHz[hi] - freqHz[lo];
            if (df <= 0.0)
                throw new ArgumentException("Frequencies must be strictly increasing.", nameof(freqHz));

            result[i] = -(phaseDeg[hi] - phaseDeg[lo]) / (360.0 * df);
        }

        return result;
    }

    /// <summary>
    /// Phase delay per bin of a uniformly spaced phase curve, in seconds. Bins outside
    /// <c>[firstBin, lastBin]</c> are 0; <paramref name="firstBin"/> must be at least 1, since DC
    /// has no phase delay.
    /// </summary>
    /// <param name="anchorBin">
    /// The bin whose phase is brought into <c>(−180°, 180°]</c>, the whole curve shifted by the same
    /// multiple of 360° — see the class remarks. <c>null</c> leaves the turns as they are.
    /// </param>
    public static double[] PhaseDelayUniform(
        ReadOnlySpan<double> phaseDeg,
        double binHz,
        int firstBin,
        int lastBin,
        int? anchorBin)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        ValidateBand(phaseDeg.Length, firstBin, lastBin);
        if (phaseDeg.Length > 0 && firstBin < 1)
            throw new ArgumentOutOfRangeException(nameof(firstBin), "Phase delay is undefined at 0 Hz.");
        if (anchorBin is { } anchor && (anchor < 0 || anchor >= phaseDeg.Length))
            throw new ArgumentOutOfRangeException(nameof(anchorBin));

        var result = new double[phaseDeg.Length];
        if (phaseDeg.Length == 0)
            return result;

        double turnsDeg = anchorBin is { } a ? WholeTurnsDeg(phaseDeg[a]) : 0.0;
        for (int k = firstBin; k <= lastBin; k++)
            result[k] = -(phaseDeg[k] - turnsDeg) / (360.0 * k * binHz);

        return result;
    }

    /// <summary>
    /// Phase delay per point of a phase curve on an arbitrary frequency grid, in seconds. Every
    /// frequency must be positive.
    /// </summary>
    public static double[] PhaseDelayOnGrid(ReadOnlySpan<double> phaseDeg, ReadOnlySpan<double> freqHz, int? anchorIndex)
    {
        if (freqHz.Length != phaseDeg.Length)
            throw new ArgumentException("Phase and frequency must be the same length.", nameof(freqHz));
        if (anchorIndex is { } anchor && (anchor < 0 || anchor >= phaseDeg.Length))
            throw new ArgumentOutOfRangeException(nameof(anchorIndex));

        var result = new double[phaseDeg.Length];
        double turnsDeg = anchorIndex is { } a ? WholeTurnsDeg(phaseDeg[a]) : 0.0;
        for (int i = 0; i < phaseDeg.Length; i++)
        {
            if (freqHz[i] <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(freqHz), "Phase delay is undefined at or below 0 Hz.");

            result[i] = -(phaseDeg[i] - turnsDeg) / (360.0 * freqHz[i]);
        }

        return result;
    }

    /// <summary>The multiple of 360° that brings <paramref name="phaseDeg"/> into <c>(−180°, 180°]</c> when subtracted.</summary>
    private static double WholeTurnsDeg(double phaseDeg) => 360.0 * Math.Ceiling((phaseDeg - 180.0) / 360.0);

    /// <summary>Σ i² for i = 1..m (0 for m &lt; 1).</summary>
    private static double SquaresUpTo(int m) => m < 1 ? 0.0 : (double)m * (m + 1) * ((2.0 * m) + 1) / 6.0;

    private static double CentralDifferenceS(ReadOnlySpan<double> phaseDeg, double binHz, int k, int firstBin, int lastBin)
    {
        int lo = Math.Max(firstBin, k - 1);
        int hi = Math.Min(lastBin, k + 1);
        if (hi == lo)
            return 0.0;

        return -(phaseDeg[hi] - phaseDeg[lo]) / (360.0 * (hi - lo) * binHz);
    }

    private static void ValidateBand(int length, int firstBin, int lastBin)
    {
        if (length == 0)
            return;
        if (firstBin < 0 || firstBin >= length)
            throw new ArgumentOutOfRangeException(nameof(firstBin));
        if (lastBin < firstBin || lastBin >= length)
            throw new ArgumentOutOfRangeException(nameof(lastBin));
    }
}
