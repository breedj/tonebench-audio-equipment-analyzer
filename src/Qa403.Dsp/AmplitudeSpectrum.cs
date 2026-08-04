using System.Numerics;

namespace Qa403.Dsp;

/// <summary>
/// Amplitude-domain normalisation: A_k = 2|X_k| / S1, peak volts per bin. Exact for a
/// coherently sampled tone landing on bin k, for any window (the window's DC gain S1
/// exactly compensates its own mainlobe scaling).
///
/// Deliberately a distinct type from <see cref="PsdSpectrum"/> — the two normalisations
/// (S1 vs S2) must never be mixed, and there is no flag to request "the wrong one".
/// A ref struct: it borrows the caller's destination buffer, never allocates its own.
/// </summary>
public readonly ref struct AmplitudeSpectrum
{
    private AmplitudeSpectrum(ReadOnlySpan<double> peakVoltsPerBin, WindowSums sums)
    {
        PeakVoltsPerBin = peakVoltsPerBin;
        Sums = sums;
    }

    /// <summary>Peak amplitude per bin, in volts: 2|X_k| / S1.</summary>
    public ReadOnlySpan<double> PeakVoltsPerBin { get; }

    public WindowSums Sums { get; }

    /// <summary>
    /// Computes peak-volts-per-bin into <paramref name="destination"/> (must be exactly
    /// <c>spectrum.Length</c> long) and returns a view over it. No allocation.
    /// </summary>
    public static AmplitudeSpectrum Compute(ReadOnlySpan<Complex> spectrum, WindowSums sums, Span<double> destination)
    {
        if (destination.Length != spectrum.Length)
            throw new ArgumentException("Destination must match spectrum length.", nameof(destination));

        for (int k = 0; k < spectrum.Length; k++)
            destination[k] = 2.0 * spectrum[k].Magnitude / sums.S1;

        return new AmplitudeSpectrum(destination, sums);
    }

    public double VrmsAt(int bin) => PeakVoltsPerBin[bin] / Math.Sqrt(2.0);

    public double DbvAt(int bin) => 20.0 * Math.Log10(VrmsAt(bin));
}
