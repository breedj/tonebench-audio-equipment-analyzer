using System.Buffers;

namespace Qa403.Dsp;

/// <summary>One spectral peak located by <see cref="PeakFinder.FindPeaks"/>.</summary>
public readonly record struct SpectralPeak(int Bin, double AmplitudeVrms, double AmplitudeDbv);

/// <summary>
/// Finds the strongest peaks in a spectrum that are NOT the fundamental or one of its harmonics —
/// mains hum, its own harmonics, and intermodulation/mixing products between multiple generator
/// tones (real ones: <c>SimulatedDevice</c>'s shared soft-clip is a genuine nonlinearity, so two
/// simultaneous tones produce real sum/difference-frequency products, not synthetic ones).
/// </summary>
public static class PeakFinder
{
    /// <summary>
    /// Greedily picks the largest remaining PSD bin not already excluded or claimed by an earlier
    /// pick, up to <paramref name="maxCount"/> times — strongest peak first. Each pick "claims" a
    /// ±<paramref name="skirtHalfWidthBins"/> window around itself (same width its level is
    /// integrated over) so a single physical spur's window-mainlobe spread across a few bins is
    /// never reported as several separate peaks. Returns the number written to
    /// <paramref name="destination"/> (which must be at least <paramref name="maxCount"/> long) —
    /// fewer than requested if the spectrum runs out of un-excluded, unclaimed bins first.
    /// </summary>
    public static int FindPeaks(
        PsdSpectrum psd,
        ReadOnlySpan<BinRange> excludedRanges,
        int maxCount,
        Span<SpectralPeak> destination,
        int minBin = 1,
        int skirtHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins)
    {
        if (maxCount < 0)
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        if (destination.Length < maxCount)
            throw new ArgumentException("Destination too short for requested peak count.", nameof(destination));

        var values = psd.ValuesV2PerHz;
        int n = values.Length;
        if (minBin < 0 || minBin >= n)
            throw new ArgumentOutOfRangeException(nameof(minBin));

        bool[] claimed = ArrayPool<bool>.Shared.Rent(n);
        try
        {
            Array.Clear(claimed, 0, n);
            foreach (var range in excludedRanges)
            {
                int lo = Math.Max(minBin, range.LoBinInclusive);
                int hi = Math.Min(n - 1, range.HiBinInclusive);
                for (int b = lo; b <= hi; b++)
                    claimed[b] = true;
            }

            int written = 0;
            for (int iter = 0; iter < maxCount; iter++)
            {
                int bestBin = -1;
                double bestValue = double.NegativeInfinity;
                for (int b = minBin; b < n; b++)
                {
                    if (claimed[b])
                        continue;
                    if (values[b] > bestValue)
                    {
                        bestValue = values[b];
                        bestBin = b;
                    }
                }

                if (bestBin < 0)
                    break;

                double vrms = psd.IntegrateVrms(bestBin - skirtHalfWidthBins, bestBin + skirtHalfWidthBins);
                double dbv = 20.0 * Math.Log10(vrms);
                destination[written++] = new SpectralPeak(bestBin, vrms, dbv);

                int claimLo = Math.Max(minBin, bestBin - skirtHalfWidthBins);
                int claimHi = Math.Min(n - 1, bestBin + skirtHalfWidthBins);
                for (int b = claimLo; b <= claimHi; b++)
                    claimed[b] = true;
            }

            return written;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(claimed);
        }
    }
}
