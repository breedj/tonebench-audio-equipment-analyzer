using System.Numerics;
using FftFlat;

namespace Tonebench.Dsp.Fft;

/// <summary>
/// <see cref="IFftBackend"/> on top of the FftFlat library (Ooura's package ported to pure C#,
/// MIT, no dependencies). Keep FftFlat usage confined to this file.
///
/// <para>The reason it exists: FftFlat is the only one of the three with a genuine <b>real-input</b>
/// transform, and on this workload that is most of the difference. <see cref="MathNetBackend"/>
/// widens N doubles into an N-element <see cref="Complex"/> array, runs a full complex N-point
/// transform and copies N/2+1 bins back out — roughly twice the arithmetic and three times the
/// memory traffic of transforming the real signal in place. Measured against it in Release on the
/// forward transform: <b>10.5x at 4096 points, 5.7x at 65,536, 7.0x at 262,144 and 11.0x at
/// 1,048,576</b>, agreeing to 3.4e-16 relative to the peak bin at every size.</para>
///
/// <para>Two conventions line up for free and are the reason this is a drop-in.
/// <c>RealFourierTransform.Forward</c> inverts the sign of the imaginary parts specifically to
/// match Math.NET's, and its forward applies no scaling — which is exactly what
/// <see cref="IFftBackend.Forward"/> specifies (raw, unnormalised DFT sums).</para>
///
/// <para>Like the other two backends this holds per-instance scratch and is therefore <b>not
/// thread-safe</b>; FftFlat's own transform objects carry twiddle tables and say the same. Every
/// consumer here already owns its backend rather than sharing one.</para>
/// </summary>
public sealed class FftFlatBackend : IFftBackend
{
    private RealFourierTransform? _real;
    private double[] _timeScratch = [];

    // The inverse deliberately does not use FftFlat's own Inverse -- see HermitianInverse, which
    // exists because a library's *inverse* scaling and sign conventions are the easy thing to get
    // subtly wrong, while its unscaled forward is the one thing IFftBackend already pins down. So
    // this takes the same route the other two backends take, through FftFlat's complex forward.
    private FastFourierTransform? _complex;
    private Complex[] _complexScratch = [];

    public void Forward(ReadOnlySpan<double> timeIn, Span<Complex> specOut)
    {
        int specLength = FftBackendValidation.ValidateAndGetSpecLength(timeIn.Length, specOut.Length);
        EnsureReal(timeIn.Length);

        timeIn.CopyTo(_timeScratch);

        // Forward is in-place over N+2 doubles, reinterpreted as the N/2+1 one-sided bins: the two
        // trailing slots are where the Nyquist bin lands, and FftFlat documents them as ignored on
        // input. Cleared anyway, so a buffer reused across calls cannot carry a stale value into a
        // transform that only writes part of it.
        _timeScratch[timeIn.Length] = 0.0;
        _timeScratch[timeIn.Length + 1] = 0.0;

        _real!.Forward(_timeScratch)[..specLength].CopyTo(specOut);
    }

    public void Inverse(ReadOnlySpan<Complex> specIn, Span<double> timeOut)
    {
        EnsureComplex(timeOut.Length);

        HermitianInverse.Expand(specIn, _complexScratch, timeOut.Length);
        _complex!.Forward(_complexScratch.AsSpan());
        HermitianInverse.Finish(_complexScratch, timeOut);
    }

    private void EnsureReal(int length)
    {
        if (_real?.Length == length)
            return;

        _real = new RealFourierTransform(length);
        _timeScratch = new double[length + 2];
    }

    private void EnsureComplex(int length)
    {
        if (_complex?.Length == length)
            return;

        _complex = new FastFourierTransform(length);
        _complexScratch = new Complex[length];
    }
}
