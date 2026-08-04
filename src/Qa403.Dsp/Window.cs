using System.Collections.Concurrent;

namespace Qa403.Dsp;

/// <summary>
/// Periodic (DFT-even) window coefficients. Periodic, not symmetric, so that a coherently
/// sampled tone convolves cleanly onto integer bins — the symmetric (N-1 denominator) form
/// used for FIR design would shift S1/S2 off the values the golden tests pin down.
///
/// Coefficient arrays are cached per (type, length): building a window and summing it is
/// the only allocation, and every later call for the same (type, length) is a dictionary
/// lookup — no allocation on the hot path.
/// </summary>
public static class Window
{
    private static readonly ConcurrentDictionary<(WindowType Type, int Length), WindowCoefficients> Cache = new();

    public static WindowCoefficients Get(WindowType type, int length)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        return Cache.GetOrAdd((type, length), static key => Build(key.Type, key.Length));
    }

    /// <summary>Elementwise multiply <paramref name="samples"/> by the window into <paramref name="destination"/>. No allocation.</summary>
    public static void Apply(ReadOnlySpan<double> samples, WindowCoefficients window, Span<double> destination)
    {
        if (samples.Length != window.Length)
            throw new ArgumentException("Sample count must match window length.", nameof(samples));
        if (destination.Length != window.Length)
            throw new ArgumentException("Destination must match window length.", nameof(destination));

        var coefficients = window.Values;
        for (int n = 0; n < samples.Length; n++)
            destination[n] = samples[n] * coefficients[n];
    }

    /// <summary>
    /// Same as <see cref="Apply(ReadOnlySpan{double}, WindowCoefficients, Span{double})"/> but
    /// reads float samples (e.g. straight off a PcmBlock) — fuses the float-to-double widen
    /// with the window multiply instead of requiring a separate conversion pass.
    /// </summary>
    public static void Apply(ReadOnlySpan<float> samples, WindowCoefficients window, Span<double> destination)
    {
        if (samples.Length != window.Length)
            throw new ArgumentException("Sample count must match window length.", nameof(samples));
        if (destination.Length != window.Length)
            throw new ArgumentException("Destination must match window length.", nameof(destination));

        var coefficients = window.Values;
        for (int n = 0; n < samples.Length; n++)
            destination[n] = samples[n] * coefficients[n];
    }

    private static WindowCoefficients Build(WindowType type, int length)
    {
        var w = new double[length];
        switch (type)
        {
            case WindowType.Rectangular:
                Array.Fill(w, 1.0);
                break;

            case WindowType.Hann:
                FillCosineSum(w, 0.5, 0.5);
                break;

            case WindowType.Hamming:
                FillCosineSum(w, 0.54, 0.46);
                break;

            case WindowType.BlackmanHarris:
                FillCosineSum(w, 0.35875, 0.48829, 0.14128, 0.01168);
                break;

            case WindowType.FlatTop:
                FillCosineSum(w, 0.21557895, 0.41663158, 0.277263158, 0.083578947, 0.006947368);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        double s1 = 0.0;
        double s2 = 0.0;
        foreach (double v in w)
        {
            s1 += v;
            s2 += v * v;
        }

        return new WindowCoefficients(type, w, new WindowSums(s1, s2, length));
    }

    private static void FillCosineSum(double[] w, params double[] coefficients)
    {
        int n = w.Length;
        for (int i = 0; i < n; i++)
        {
            double sum = coefficients[0];
            double sign = -1.0;
            for (int k = 1; k < coefficients.Length; k++)
            {
                sum += sign * coefficients[k] * Math.Cos(2.0 * Math.PI * k * i / n);
                sign = -sign;
            }

            w[i] = sum;
        }
    }
}
