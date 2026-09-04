namespace Qa403.Dsp;

/// <summary>The peak of a response trace: the reference level every bandwidth figure is measured down from.</summary>
public readonly record struct ResponsePeak(int Bin, double FreqHz, double LevelDb);

/// <summary>
/// One bandwidth measurement. <see cref="LowerClamped"/>/<see cref="UpperClamped"/> mean the trace
/// never fell to the threshold on that side within the searched band, so the corresponding
/// frequency is the edge of the search rather than a measured corner — the difference between
/// "the response rolls off at 63 kHz" and "it was still flat where we stopped looking", which a
/// bare number cannot express.
/// </summary>
public readonly record struct BandwidthResult(
    double DropDb,
    double LowerHz,
    double UpperHz,
    double BandwidthHz,
    bool LowerClamped,
    bool UpperClamped);

/// <summary>
/// Finds the −1 dB / −3 dB corners of a frequency-response trace, plus its peak.
///
/// <para><b>First crossing outward from the peak, not nearest value anywhere.</b> The reference
/// WinForms app searched every bin below the peak for whichever came closest in level to
/// <c>peak · 10^(dB/20)</c> (<c>frmFrequencyResponse.cs:1262-1320</c>). On a rippled response that
/// picks whichever ripple happens to sit nearest the threshold, which can be an octave away from
/// the actual roll-off and jumps around between runs. Walking outward and stopping at the first
/// crossing is both stable and what a reader would point at on the plot.</para>
///
/// <para>The crossing is then interpolated <b>in log frequency</b> between the two bracketing bins,
/// because the axis is logarithmic and the roll-off is close to straight in that space. This also
/// removes the reference app's off-by-one, where the lower corner was reported as
/// <c>(i+1)·df</c> and the upper as <c>i·df</c>.</para>
///
/// <para>Run this on the <b>smoothed</b> trace. On raw bins a single noisy bin at −3.1 dB ends the
/// search early, and the reported bandwidth then tracks the noise floor rather than the DUT.</para>
/// </summary>
public static class BandwidthAnalyzer
{
    /// <summary>
    /// The highest bin of <paramref name="traceDb"/> within <c>[firstBin, lastBin]</c>. DC is
    /// excluded by <paramref name="firstBin"/> defaulting to 1 — it carries the DUT's offset, not
    /// its response, and is routinely the largest value in the array.
    /// </summary>
    public static ResponsePeak FindPeak(
        ReadOnlySpan<double> traceDb,
        double binHz,
        int firstBin = 1,
        int lastBin = int.MaxValue)
    {
        (int first, int last) = ClampRange(traceDb.Length, firstBin, lastBin);

        int peakBin = first;
        double peakDb = double.NegativeInfinity;
        for (int i = first; i <= last; i++)
        {
            if (traceDb[i] > peakDb)
            {
                peakDb = traceDb[i];
                peakBin = i;
            }
        }

        return new ResponsePeak(peakBin, peakBin * binHz, peakDb);
    }

    /// <summary>
    /// The band over which <paramref name="traceDb"/> stays within <paramref name="dropDb"/> of its
    /// peak. <paramref name="dropDb"/> is positive (pass 3 for the −3 dB bandwidth).
    /// </summary>
    public static BandwidthResult Find(
        ReadOnlySpan<double> traceDb,
        double binHz,
        double dropDb,
        int firstBin = 1,
        int lastBin = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropDb);

        (int first, int last) = ClampRange(traceDb.Length, firstBin, lastBin);

        var peak = FindPeak(traceDb, binHz, first, last);
        double threshold = peak.LevelDb - dropDb;

        double lowerHz = first * binHz;
        bool lowerClamped = true;
        for (int i = peak.Bin - 1; i >= first; i--)
        {
            if (traceDb[i] <= threshold)
            {
                lowerHz = InterpolateCrossing(i, traceDb[i], i + 1, traceDb[i + 1], threshold, binHz);
                lowerClamped = false;
                break;
            }
        }

        double upperHz = last * binHz;
        bool upperClamped = true;
        for (int i = peak.Bin + 1; i <= last; i++)
        {
            if (traceDb[i] <= threshold)
            {
                upperHz = InterpolateCrossing(i - 1, traceDb[i - 1], i, traceDb[i], threshold, binHz);
                upperClamped = false;
                break;
            }
        }

        return new BandwidthResult(
            dropDb,
            lowerHz,
            upperHz,
            Math.Max(0.0, upperHz - lowerHz),
            lowerClamped,
            upperClamped);
    }

    /// <summary>
    /// Where the line between two adjacent bins crosses <paramref name="threshold"/>, interpolated in
    /// log frequency. Falls back to the nearer bin's own frequency when the two levels are equal,
    /// which would otherwise divide by zero.
    /// </summary>
    private static double InterpolateCrossing(
        int binA,
        double levelA,
        int binB,
        double levelB,
        double threshold,
        double binHz)
    {
        double freqA = binA * binHz;
        double freqB = binB * binHz;

        // Bin 0 has no logarithm; the caller's firstBin keeps us off it in practice, but a caller
        // passing firstBin 0 should get a sane answer rather than a NaN.
        if (freqA <= 0.0)
            return freqB;

        double span = levelB - levelA;
        if (Math.Abs(span) < double.Epsilon)
            return freqA;

        double t = (threshold - levelA) / span;
        t = Math.Clamp(t, 0.0, 1.0);

        double logFreq = Math.Log10(freqA) + (t * (Math.Log10(freqB) - Math.Log10(freqA)));
        return Math.Pow(10.0, logFreq);
    }

    private static (int First, int Last) ClampRange(int length, int firstBin, int lastBin)
    {
        if (length < 2)
            throw new ArgumentException("A response trace needs at least two bins.", nameof(length));

        int first = Math.Clamp(firstBin, 0, length - 1);
        int last = Math.Clamp(lastBin, first, length - 1);
        return (first, last);
    }
}
