namespace Qa403.Dsp;

/// <summary>
/// Locates the fundamental and harmonics 2..H by searching near each expected bin, then
/// sums PSD power over ±<c>skirtHalfWidthBins</c> around the located peak to capture jitter
/// skirts. Summing in the PSD domain (S2-normalised, Parseval-safe) rather than the
/// amplitude domain (S1) is deliberate: for a perfectly coherent tone under a non-rectangular
/// window, the window's own mainlobe spreads real energy into the neighbouring bins, and only
/// the PSD-domain sum recovers the exact input amplitude when those bins are included. An
/// S1-based sum would double-count that spread and inflate the reading.
/// </summary>
public static class HarmonicAnalyzer
{
    public const int DefaultPeakSearchSpanBins = 2;
    public const int DefaultSkirtHalfWidthBins = 3;

    /// <summary>
    /// Writes up to <paramref name="harmonicCount"/> harmonics (order 1 = fundamental) into
    /// <paramref name="destination"/> and returns how many were written. Stops early if a
    /// harmonic's expected bin would exceed Nyquist.
    /// </summary>
    public static int Analyze(
        PsdSpectrum psd,
        int fundamentalBin,
        int harmonicCount,
        Span<Harmonic> destination,
        int peakSearchSpanBins = DefaultPeakSearchSpanBins,
        int skirtHalfWidthBins = DefaultSkirtHalfWidthBins)
    {
        if (harmonicCount < 1)
            throw new ArgumentOutOfRangeException(nameof(harmonicCount));
        if (destination.Length < harmonicCount)
            throw new ArgumentException("Destination too short for requested harmonic count.", nameof(destination));

        int written = 0;
        double fundamentalVrms = 0.0;

        for (int order = 1; order <= harmonicCount; order++)
        {
            int expectedBin = fundamentalBin * order;
            if (expectedBin >= psd.ValuesV2PerHz.Length)
                break;

            int peak = FindPeakBin(psd.ValuesV2PerHz, expectedBin, peakSearchSpanBins);
            double vrms = psd.IntegrateVrms(peak - skirtHalfWidthBins, peak + skirtHalfWidthBins);
            double dbv = 20.0 * Math.Log10(vrms);

            if (order == 1)
                fundamentalVrms = vrms;

            double dbc = 20.0 * Math.Log10(vrms / fundamentalVrms);

            destination[written++] = new Harmonic(order, peak, vrms, dbv, dbc);
        }

        return written;
    }

    /// <summary>Finds the true peak within ±searchSpanBins of an expected bin (clock jitter can shift a tone by a bin or two).</summary>
    public static int FindPeakBin(ReadOnlySpan<double> psdValues, int expectedBin, int searchSpanBins)
    {
        int lo = Math.Max(0, expectedBin - searchSpanBins);
        int hi = Math.Min(psdValues.Length - 1, expectedBin + searchSpanBins);

        int peak = lo;
        double peakValue = psdValues[lo];
        for (int k = lo + 1; k <= hi; k++)
        {
            if (psdValues[k] > peakValue)
            {
                peakValue = psdValues[k];
                peak = k;
            }
        }

        return peak;
    }
}
