using System.Numerics;

namespace Qa403.Dsp.Fft;

/// <summary>
/// Real-input forward FFT. The FFT library choice is not settled — nothing outside this
/// interface (and its implementations) may reference FftSharp or MathNet.Numerics types.
/// </summary>
public interface IFftBackend
{
    /// <summary>
    /// Computes the one-sided spectrum (DC..Nyquist, N/2+1 bins) of a power-of-two-length
    /// real input. <paramref name="specOut"/> must be exactly <c>timeIn.Length / 2 + 1</c>
    /// long. Bins are the raw (unnormalised) DFT sums — callers apply window and
    /// amplitude/PSD scaling separately.
    /// </summary>
    void Forward(ReadOnlySpan<double> timeIn, Span<Complex> specOut);
}
