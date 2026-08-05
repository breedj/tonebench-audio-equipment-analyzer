namespace Qa403.Dsp;

/// <summary>Min/max over one decimated display column.</summary>
public readonly record struct MinMax(double Min, double Max);

/// <summary>
/// Decimates a spectrum for display by taking min/max per column — never the mean. A mean
/// flattens a single-bin spike into the noise floor around it and erases it; min/max always
/// preserves it in at least one of the two output values.
/// </summary>
public static class SpectrumDecimator
{
    /// <summary>Linearly spaced columns across the whole span.</summary>
    public static void DecimateLinear(ReadOnlySpan<double> values, Span<MinMax> destination)
    {
        Decimate(values, destination, default, startBin: 0, logSpaced: false);
    }

    /// <summary>
    /// Log-spaced columns from <paramref name="startBin"/> (inclusive, must be >= 1 — bin 0 is
    /// DC and has no logarithm) to the end of the span. Matches a logarithmic frequency axis.
    /// </summary>
    public static void DecimateLog(ReadOnlySpan<double> values, Span<MinMax> destination, int startBin = 1)
    {
        if (startBin < 1)
            throw new ArgumentOutOfRangeException(nameof(startBin), "Log spacing requires startBin >= 1 (bin 0 is DC).");

        Decimate(values, destination, default, startBin, logSpaced: true);
    }

    /// <summary>
    /// Same as <see cref="DecimateLog(ReadOnlySpan{double}, Span{MinMax}, int)"/>, but also
    /// writes each column's center bin (as a fractional bin index — multiply by the bin size to
    /// get Hz) into <paramref name="centerBins"/>, which must be exactly <c>destination.Length</c>
    /// long. Column width is not analytically predictable near the low-frequency end whenever
    /// there are few enough source bins that consecutive columns collapse to single, adjacent
    /// bins (small FFT sizes relative to the column count) — a caller that needs the true
    /// per-column frequency (e.g. to label a chart axis) must read it from here rather than
    /// re-deriving it from a continuous log-spacing formula, which silently diverges from what
    /// this method actually did in exactly that regime.
    /// </summary>
    public static void DecimateLogWithCenters(ReadOnlySpan<double> values, Span<MinMax> destination, Span<double> centerBins, int startBin = 1)
    {
        if (startBin < 1)
            throw new ArgumentOutOfRangeException(nameof(startBin), "Log spacing requires startBin >= 1 (bin 0 is DC).");
        if (centerBins.Length != destination.Length)
            throw new ArgumentException("centerBins must be exactly destination.Length long.", nameof(centerBins));

        Decimate(values, destination, centerBins, startBin, logSpaced: true);
    }

    private static void Decimate(ReadOnlySpan<double> values, Span<MinMax> destination, Span<double> centerBins, int startBin, bool logSpaced)
    {
        int n = values.Length;
        int cols = destination.Length;

        if (cols <= 0)
            throw new ArgumentOutOfRangeException(nameof(destination));
        if (startBin < 0 || startBin >= n)
            throw new ArgumentOutOfRangeException(nameof(startBin));

        // Column boundaries are computed cumulatively (each column's start is the previous
        // column's end) so every bin from startBin..n-1 is assigned to exactly one column —
        // no gaps, no overlaps, regardless of rounding.
        double logRatio = logSpaced ? Math.Log((double)n / startBin) : 0.0;
        int prevBoundary = startBin;

        for (int c = 0; c < cols; c++)
        {
            double frac = (double)(c + 1) / cols;
            int nextBoundary = logSpaced
                ? startBin + (int)Math.Round(startBin * (Math.Exp(logRatio * frac) - 1.0))
                : startBin + (int)((long)(c + 1) * (n - startBin) / cols);

            if (nextBoundary <= prevBoundary)
                nextBoundary = prevBoundary + 1;
            if (nextBoundary > n)
                nextBoundary = n;

            int lo = Math.Min(prevBoundary, n - 1);
            int hi = Math.Min(Math.Max(nextBoundary, lo + 1), n);

            double min = values[lo];
            double max = values[lo];
            for (int i = lo + 1; i < hi; i++)
            {
                double v = values[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            destination[c] = new MinMax(min, max);
            if (!centerBins.IsEmpty)
                centerBins[c] = (lo + hi - 1) / 2.0;
            prevBoundary = nextBoundary;
        }
    }
}
