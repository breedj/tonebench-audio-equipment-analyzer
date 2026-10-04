using System.Numerics;

namespace Tonebench.Dsp.Fft;

/// <summary>
/// The one-sided inverse transform, expressed purely in terms of an in-place <b>complex forward</b>
/// DFT so both backends can share it.
///
/// <para>Neither library is asked for an inverse of its own, deliberately: their scaling and sign
/// conventions for the inverse differ and are easy to get subtly wrong, whereas the unscaled
/// forward transform is the one thing <see cref="IFftBackend.Forward"/> already pins down (and that
/// <c>FftBackendTests</c> already cross-checks between the two libraries). The identity used is
/// <c>IFFT(X) = conj(FFT(conj(X))) / N</c>, which holds for any correct unscaled forward.</para>
/// </summary>
internal static class HermitianInverse
{
    /// <summary>
    /// Expands a one-sided spectrum into the full conjugate-symmetric N-point spectrum, already
    /// conjugated, ready to be handed to a complex forward transform.
    /// </summary>
    public static int Expand(ReadOnlySpan<Complex> specIn, Span<Complex> full, int timeOutLength)
    {
        int specLength = FftBackendValidation.ValidateAndGetSpecLength(timeOutLength, specIn.Length);
        int n = timeOutLength;

        // conj(X) for the half we were given. DC and Nyquist are forced real: a real signal cannot
        // put an imaginary part there, and an estimator's rounding noise otherwise leaks a tiny
        // imaginary component into every output sample.
        full[0] = new Complex(specIn[0].Real, 0.0);
        for (int k = 1; k < specLength - 1; k++)
            full[k] = Complex.Conjugate(specIn[k]);
        full[specLength - 1] = new Complex(specIn[specLength - 1].Real, 0.0);

        // Mirror: conj(X)[N-k] == X[k], since the implied bin above Nyquist is conj(X[k]).
        for (int k = 1; k < specLength - 1; k++)
            full[n - k] = specIn[k];

        return n;
    }

    /// <summary>
    /// Finishes the identity: takes the real part of the conjugated forward transform and scales
    /// by 1/N. (The conjugation is a no-op on the real part, so it is folded away here.)
    /// </summary>
    public static void Finish(ReadOnlySpan<Complex> transformed, Span<double> timeOut)
    {
        double scale = 1.0 / timeOut.Length;
        for (int i = 0; i < timeOut.Length; i++)
            timeOut[i] = transformed[i].Real * scale;
    }
}
