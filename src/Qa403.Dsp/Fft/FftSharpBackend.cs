using System.Numerics;

namespace Qa403.Dsp.Fft;

/// <summary>IFftBackend on top of the FftSharp library. Keep FftSharp usage confined to this file.</summary>
public sealed class FftSharpBackend : IFftBackend
{
    private double[] _scratch = [];

    public void Forward(ReadOnlySpan<double> timeIn, Span<Complex> specOut)
    {
        FftBackendValidation.ValidateAndGetSpecLength(timeIn.Length, specOut.Length);

        if (_scratch.Length != timeIn.Length)
            _scratch = new double[timeIn.Length];

        timeIn.CopyTo(_scratch);

        Complex[] spectrum = FftSharp.FFT.ForwardReal(_scratch);
        spectrum.CopyTo(specOut);
    }
}
