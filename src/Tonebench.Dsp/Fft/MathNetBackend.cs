using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Tonebench.Dsp.Fft;

/// <summary>IFftBackend on top of MathNet.Numerics. Keep MathNet usage confined to this file.</summary>
public sealed class MathNetBackend : IFftBackend
{
    private Complex[] _scratch = [];

    public void Forward(ReadOnlySpan<double> timeIn, Span<Complex> specOut)
    {
        int specLength = FftBackendValidation.ValidateAndGetSpecLength(timeIn.Length, specOut.Length);

        EnsureScratch(timeIn.Length);

        for (int i = 0; i < timeIn.Length; i++)
            _scratch[i] = new Complex(timeIn[i], 0.0);

        // NoScaling: raw DFT sums, unnormalised forward transform — matches FftSharp's
        // convention so callers apply exactly one window/amplitude scaling downstream.
        Fourier.Forward(_scratch, FourierOptions.NoScaling);

        _scratch.AsSpan(0, specLength).CopyTo(specOut);
    }

    public void Inverse(ReadOnlySpan<Complex> specIn, Span<double> timeOut)
    {
        EnsureScratch(timeOut.Length);

        HermitianInverse.Expand(specIn, _scratch, timeOut.Length);
        Fourier.Forward(_scratch, FourierOptions.NoScaling);
        HermitianInverse.Finish(_scratch, timeOut);
    }

    private void EnsureScratch(int length)
    {
        if (_scratch.Length != length)
            _scratch = new Complex[length];
    }
}
