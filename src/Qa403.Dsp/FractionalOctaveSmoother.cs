namespace Qa403.Dsp;

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
/// <para>Cost is O(N) regardless of width, via a running power integral — a 1/3-octave band at
/// 20 kHz spans several thousand bins, so the naive form would be quadratic and unusable at the
/// 0.37 Hz resolution setting.</para>
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

        // Running integral of power, so any band sum is one subtraction. cumulative[i] is the sum of
        // magnitude[0..i-1] squared, hence the extra element.
        double[] cumulative = new double[n + 1];
        for (int i = 0; i < n; i++)
            cumulative[i + 1] = cumulative[i] + (magnitude[i] * magnitude[i]);

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

            double power = cumulative[high + 1] - cumulative[low];
            destination[k] = Math.Sqrt(power / (high - low + 1));
        }
    }

    /// <summary>Allocating convenience overload, for callers that are not reusing a buffer.</summary>
    public static double[] Smooth(ReadOnlySpan<double> magnitude, double binHz, int denominator)
    {
        double[] result = new double[magnitude.Length];
        Smooth(magnitude, result, binHz, denominator);
        return result;
    }
}
