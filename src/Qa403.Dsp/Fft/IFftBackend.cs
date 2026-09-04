using System.Numerics;

namespace Qa403.Dsp.Fft;

/// <summary>
/// Real-input forward FFT and its inverse. The FFT library choice is not settled — nothing outside
/// this interface (and its implementations) may reference FftSharp or MathNet.Numerics types.
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

    /// <summary>
    /// The exact inverse of <see cref="Forward"/>: reconstructs the real signal from a one-sided
    /// spectrum. <paramref name="specIn"/> must be <c>timeOut.Length / 2 + 1</c> long and
    /// <paramref name="timeOut"/> a power of two.
    ///
    /// <para>The input is treated as Hermitian — bins above Nyquist are implied by conjugate
    /// symmetry and any imaginary part in the DC and Nyquist bins is ignored, since a real signal
    /// cannot produce one. That is exactly the case this exists for: a transfer function estimated
    /// from two real signals, inverted to find where its impulse response peaks.</para>
    ///
    /// <para>Scaling is the counterpart of <see cref="Forward"/>'s raw sums, i.e. this divides by N,
    /// so <c>Inverse(Forward(x)) == x</c>. Pinned by <c>FftBackendTests</c>.</para>
    /// </summary>
    void Inverse(ReadOnlySpan<Complex> specIn, Span<double> timeOut);
}
