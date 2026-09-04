using System.Numerics;

namespace Qa403.Dsp.Fft;

/// <summary>IFftBackend on top of the FftSharp library. Keep FftSharp usage confined to this file.</summary>
public sealed class FftSharpBackend : IFftBackend
{
    private double[] _scratch = [];
    private Complex[] _complexScratch = [];

    public void Forward(ReadOnlySpan<double> timeIn, Span<Complex> specOut)
    {
        FftBackendValidation.ValidateAndGetSpecLength(timeIn.Length, specOut.Length);

        if (_scratch.Length != timeIn.Length)
            _scratch = new double[timeIn.Length];

        timeIn.CopyTo(_scratch);

        Complex[] spectrum = FftSharp.FFT.ForwardReal(_scratch);
        spectrum.CopyTo(specOut);
    }

    public void Inverse(ReadOnlySpan<Complex> specIn, Span<double> timeOut)
    {
        if (_complexScratch.Length != timeOut.Length)
            _complexScratch = new Complex[timeOut.Length];

        HermitianInverse.Expand(specIn, _complexScratch, timeOut.Length);
        FftSharp.FFT.Forward(_complexScratch.AsSpan());
        HermitianInverse.Finish(_complexScratch, timeOut);
    }
}
