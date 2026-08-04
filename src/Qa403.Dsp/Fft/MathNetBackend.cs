using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Qa403.Dsp.Fft;

/// <summary>IFftBackend on top of MathNet.Numerics. Keep MathNet usage confined to this file.</summary>
public sealed class MathNetBackend : IFftBackend
{
    private Complex[] _scratch = [];

    public void Forward(ReadOnlySpan<double> timeIn, Span<Complex> specOut)
    {
        int specLength = FftBackendValidation.ValidateAndGetSpecLength(timeIn.Length, specOut.Length);

        if (_scratch.Length != timeIn.Length)
            _scratch = new Complex[timeIn.Length];

        for (int i = 0; i < timeIn.Length; i++)
            _scratch[i] = new Complex(timeIn[i], 0.0);

        // NoScaling: raw DFT sums, unnormalised forward transform — matches FftSharp's
        // convention so callers apply exactly one window/amplitude scaling downstream.
        Fourier.Forward(_scratch, FourierOptions.NoScaling);

        _scratch.AsSpan(0, specLength).CopyTo(specOut);
    }
}
