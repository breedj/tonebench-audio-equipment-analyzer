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
/// <para>The crossing is then interpolated <b>in log frequency</b> between the two bracketing
/// samples, because the axis is logarithmic and the roll-off is close to straight in that space.
/// This also removes the reference app's off-by-one, where the lower corner was reported as
/// <c>(i+1)·df</c> and the upper as <c>i·df</c>. Interpolating in log frequency is also what lets
/// the same code serve a log-spaced stepped sweep unchanged — see the frequency-array overloads.</para>
///
/// <para>Run this on the <b>smoothed</b> trace. On raw bins a single noisy bin at −3.1 dB ends the
/// search early, and the reported bandwidth then tracks the noise floor rather than the DUT.
/// (A stepped sine has nothing to smooth: every sample is already an averaged measurement of one
/// tone rather than one noisy bin.)</para>
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        return FindPeakCore(traceDb, FrequencyAxis.Uniform(binHz), firstBin, lastBin);
    }

    /// <summary>
    /// The highest sample of <paramref name="traceDb"/>, where <paramref name="freqHz"/> gives each
    /// sample's own frequency rather than a constant bin spacing — a stepped sweep's log-spaced
    /// grid.
    ///
    /// <para><paramref name="firstIndex"/> defaults to 0, not 1: there is no DC sample to skip here.
    /// Every entry is a tone that was actually played.</para>
    /// </summary>
    public static ResponsePeak FindPeak(
        ReadOnlySpan<double> traceDb,
        ReadOnlySpan<double> freqHz,
        int firstIndex = 0,
        int lastIndex = int.MaxValue)
    {
        RequireMatchingAxis(traceDb, freqHz);
        return FindPeakCore(traceDb, FrequencyAxis.Explicit(freqHz), firstIndex, lastIndex);
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

        return FindCore(traceDb, FrequencyAxis.Uniform(binHz), dropDb, firstBin, lastBin);
    }

    /// <summary>
    /// As <see cref="Find(ReadOnlySpan{double}, double, double, int, int)"/>, for a trace whose
    /// samples carry their own frequencies instead of sitting on a uniform grid.
    /// </summary>
    public static BandwidthResult Find(
        ReadOnlySpan<double> traceDb,
        ReadOnlySpan<double> freqHz,
        double dropDb,
        int firstIndex = 0,
        int lastIndex = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropDb);
        RequireMatchingAxis(traceDb, freqHz);

        return FindCore(traceDb, FrequencyAxis.Explicit(freqHz), dropDb, firstIndex, lastIndex);
    }

    private static ResponsePeak FindPeakCore(
        ReadOnlySpan<double> traceDb,
        scoped FrequencyAxis axis,
        int firstIndex,
        int lastIndex)
    {
        (int first, int last) = ClampRange(traceDb.Length, firstIndex, lastIndex);

        int peakIndex = first;
        double peakDb = double.NegativeInfinity;
        for (int i = first; i <= last; i++)
        {
            if (traceDb[i] > peakDb)
            {
                peakDb = traceDb[i];
                peakIndex = i;
            }
        }

        return new ResponsePeak(peakIndex, axis.At(peakIndex), peakDb);
    }

    private static BandwidthResult FindCore(
        ReadOnlySpan<double> traceDb,
        scoped FrequencyAxis axis,
        double dropDb,
        int firstIndex,
        int lastIndex)
    {
        (int first, int last) = ClampRange(traceDb.Length, firstIndex, lastIndex);

        var peak = FindPeakCore(traceDb, axis, first, last);
        double threshold = peak.LevelDb - dropDb;

        double lowerHz = axis.At(first);
        bool lowerClamped = true;
        for (int i = peak.Bin - 1; i >= first; i--)
        {
            if (traceDb[i] <= threshold)
            {
                lowerHz = InterpolateCrossing(axis, i, traceDb[i], i + 1, traceDb[i + 1], threshold);
                lowerClamped = false;
                break;
            }
        }

        double upperHz = axis.At(last);
        bool upperClamped = true;
        for (int i = peak.Bin + 1; i <= last; i++)
        {
            if (traceDb[i] <= threshold)
            {
                upperHz = InterpolateCrossing(axis, i - 1, traceDb[i - 1], i, traceDb[i], threshold);
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
    /// Where the line between two adjacent samples crosses <paramref name="threshold"/>,
    /// interpolated in log frequency. Falls back to the nearer sample's own frequency when the two
    /// levels are equal, which would otherwise divide by zero.
    /// </summary>
    private static double InterpolateCrossing(
        scoped FrequencyAxis axis,
        int indexA,
        double levelA,
        int indexB,
        double levelB,
        double threshold)
    {
        double freqA = axis.At(indexA);
        double freqB = axis.At(indexB);

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

    private static void RequireMatchingAxis(ReadOnlySpan<double> traceDb, ReadOnlySpan<double> freqHz)
    {
        if (freqHz.Length != traceDb.Length)
        {
            throw new ArgumentException(
                $"The frequency axis has {freqHz.Length} entries but the trace has {traceDb.Length}.",
                nameof(freqHz));
        }
    }

    /// <summary>
    /// Where a sample sits in frequency: either a constant bin spacing (an FFT trace) or one
    /// frequency per sample (a stepped sweep's log-spaced grid).
    ///
    /// <para>The whole of this analyzer only ever asks "what frequency is sample i", so making that
    /// the one thing the two cases disagree about is what lets both share the search and the
    /// interpolation verbatim. A <c>ref struct</c> so the explicit case carries the caller's span
    /// rather than a copy — a full-resolution FFT trace would otherwise cost a 262144-element array
    /// per call to express something the uniform case already knows from one number.</para>
    /// </summary>
    private readonly ref struct FrequencyAxis
    {
        private readonly double _binHz;
        private readonly ReadOnlySpan<double> _freqHz;

        private FrequencyAxis(double binHz, ReadOnlySpan<double> freqHz)
        {
            _binHz = binHz;
            _freqHz = freqHz;
        }

        public static FrequencyAxis Uniform(double binHz) => new(binHz, default);

        public static FrequencyAxis Explicit(ReadOnlySpan<double> freqHz) => new(0.0, freqHz);

        public double At(int index) => _freqHz.IsEmpty ? index * _binHz : _freqHz[index];
    }
}
