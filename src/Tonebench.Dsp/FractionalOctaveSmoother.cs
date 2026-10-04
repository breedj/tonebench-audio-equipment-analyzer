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
        int denominator)
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
        int blockCount = ((n - 1) / BlockBins) + 1;
        double[] blockPower = new double[blockCount];
        for (int i = 0; i < n; i++)
        {
            power[i] = magnitude[i] * magnitude[i];
            blockPower[i / BlockBins] += power[i];
        }

        // Half-width as a frequency ratio: a 1/N-octave band spans 2^(1/N), so each edge sits
        // 2^(1/2N) away from the centre.
        double halfWidthRatio = Math.Pow(2.0, 1.0 / (2.0 * denominator));

        // DC has no centre frequency to build a relative band around, and the chart skips it anyway.
        destination[0] = magnitude[0];

        for (int k = 1; k < n; k++)
        {
            // The band edges in bin units. Rounding inward (ceil the low edge, floor the high) is
            // what makes a band narrower than one bin come out empty rather than silently widening
            // to two — that case is handled explicitly below.
            int low = (int)Math.Ceiling(k / halfWidthRatio);
            int high = (int)Math.Floor(k * halfWidthRatio);

            low = Math.Max(low, 1);
            high = Math.Min(high, n - 1);

            // Below roughly denominator·1.44 bins the band is narrower than the bin spacing, which
            // is most of the bottom decade at 1/24 octave. Falling back to the bin itself keeps the
            // low end of the trace at full resolution instead of blanking it.
            if (high < low)
            {
                destination[k] = magnitude[k];
                continue;
            }

            double bandPower = BandPower(power, blockPower, low, high);
            destination[k] = Math.Sqrt(bandPower / (high - low + 1));
        }
    }

    /// <summary>
    /// Bins per block in the table above. The per-bin cost is <c>W/B + 2B</c> for a band of
    /// <c>W</c> bins, so this trades the wide bands (thousands of bins at 1/3 octave) against the
    /// narrow ones, which pay the two partial ends whatever their width.
    /// </summary>
    private const int BlockBins = 128;

    /// <summary>
    /// The summed power over <c>[low, high]</c>, from those bins and no others: whole blocks
    /// straight off the table, the partial block at each end bin by bin. A band narrower than the
    /// blocks it touches is summed directly.
    /// </summary>
    private static double BandPower(double[] binPower, double[] blockPower, int low, int high)
    {
        // The first bin of the first block the band covers completely, and one past the last bin of
        // the last one. When they cross, no block is fully inside the band.
        int firstWhole = ((low + BlockBins - 1) / BlockBins) * BlockBins;
        int lastWholeEnd = ((high + 1) / BlockBins) * BlockBins;

        double power = 0.0;

        if (firstWhole >= lastWholeEnd)
        {
            for (int i = low; i <= high; i++)
                power += binPower[i];

            return power;
        }

        for (int i = low; i < firstWhole; i++)
            power += binPower[i];

        for (int b = firstWhole / BlockBins; b < lastWholeEnd / BlockBins; b++)
            power += blockPower[b];

        for (int i = lastWholeEnd; i <= high; i++)
            power += binPower[i];

        return power;
    }

    /// <summary>Allocating convenience overload, for callers that are not reusing a buffer.</summary>
    public static double[] Smooth(ReadOnlySpan<double> magnitude, double binHz, int denominator)
    {
        double[] result = new double[magnitude.Length];
        Smooth(magnitude, result, binHz, denominator);
        return result;
    }
}
