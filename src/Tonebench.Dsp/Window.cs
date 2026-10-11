using System.Collections.Concurrent;

namespace Tonebench.Dsp;

/// <summary>
/// Periodic (DFT-even) window coefficients. Periodic, not symmetric, so that a coherently
/// sampled tone convolves cleanly onto integer bins — the symmetric (N-1 denominator) form
/// used for FIR design would shift S1/S2 off the values the golden tests pin down.
///
/// Coefficient arrays are cached per (type, length): building a window and summing it is
/// the only allocation, and every later call for the same (type, length) is a dictionary
/// lookup — no allocation on the hot path.
///
/// <para><b>The Kaiser windows are parameterised the way ARTA names them: the number is α, and
/// β = πα.</b> So <see cref="WindowType.Kaiser5"/> is β = 5π ≈ 15.708 and
/// <see cref="WindowType.Kaiser7"/> is β = 7π ≈ 21.991 — Harris's 1978 convention, not the bare β
/// that numpy, scipy and MATLAB take. The distinction is not cosmetic: read as β, "Kaiser 5" would
/// have a 1.359-bin ENBW and −37 dB sidelobes — *narrower* in the skirts than Hann, and the
/// opposite of the low-leakage window the name is asked for. The πα reading gives 2.283 bins at
/// −120 dB and 2.685 at −171 dB, which is what ARTA recommends these two for ("for measuring
/// harmonic and intermodulation distortions use the Kaiser5 … to get 24-bit resolution use
/// Kaiser7").</para>
///
/// <para>Which reading is right is settled by ARTA's own Table 2.2: its Kaiser7 ENBW of 2.6849
/// bins is what β = 7π produces, to four decimals. Its Kaiser5 entry of 2.2183 matches nothing —
/// β = 5π gives 2.2830, and 2.2183 would need α = 4.708 — so that one is an error in their table,
/// not evidence of a second convention. Pinned by
/// <c>WindowTests.Kaiser_HasTheEnbwOfTheAlphaTimesPiConvention</c>, which exists so that a later
/// "fix" to the bare-β reading fails loudly instead of quietly changing every stored ENBW.</para>
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

            case WindowType.Kaiser5:
                FillKaiser(w, alpha: 5.0);
                break;

            case WindowType.Kaiser7:
                FillKaiser(w, alpha: 7.0);
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

    /// <summary>
    /// Kaiser-Bessel, periodic like every other window here: the shape argument runs
    /// <c>2n/N − 1</c> over n = 0..N−1, not the symmetric <c>2n/(N−1) − 1</c> ARTA's Table 2.1
    /// writes. The two agree to about one part in N, but the DFT-even form is the convention the
    /// golden tests pin, so it is the one used.
    ///
    /// <para>Only half the array is evaluated: the argument enters as its square, so
    /// <c>w[N−n] == w[n]</c> for every n ≥ 1 and the window is its own mirror about N/2. Worth
    /// doing here where it is not for the cosine sums, because <see cref="BesselI0"/> is a
    /// ~60-term series per sample rather than a handful of cosines, and the build happens on the
    /// pipeline worker inside a tick, the first time a record length is seen. Measured in Release
    /// at 1,048,576 points: 71 ms for Kaiser7 and 62 ms for Kaiser5 against 22 ms for
    /// Blackman-Harris — once per (type, length), and against a record that is 5.5 s long.</para>
    /// </summary>
    private static void FillKaiser(double[] w, double alpha)
    {
        int n = w.Length;
        double beta = Math.PI * alpha;
        double normalisation = BesselI0(beta);

        for (int i = 0; i <= n / 2; i++)
        {
            double r = (2.0 * i / n) - 1.0;
            // Clamped: r is exactly ±1 at the ends in exact arithmetic, and a rounding overshoot
            // would hand Math.Sqrt a negative and produce NaN coefficients.
            double shape = Math.Sqrt(Math.Max(0.0, 1.0 - (r * r)));
            double value = BesselI0(beta * shape) / normalisation;

            w[i] = value;
            if (i > 0)
                w[n - i] = value;
        }
    }

    /// <summary>
    /// Modified Bessel function of the first kind, order 0, by its own power series
    /// <c>Σ ((x/2)^k / k!)²</c> — each term is the previous one times <c>(x/2)² / k²</c>, so it
    /// costs one multiply and one divide and converges to machine precision in about sixty terms
    /// at the largest argument used here (β = 7π).
    ///
    /// <para>Written out rather than taken from MathNet.Numerics' <c>SpecialFunctions</c>:
    /// nothing outside <c>Tonebench.Dsp/Fft</c> may name that package (see CLAUDE.md), and the usual
    /// Abramowitz &amp; Stegun 9.8.2 polynomial is only good to ~2e-7 relative, which is coarse
    /// against a window whose whole point is a −170 dB sidelobe floor.</para>
    /// </summary>
    /// <remarks>
    /// Internal rather than private because <see cref="Resampler"/> needs the same series for its
    /// Kaiser-windowed FIR, and the reasons above for not taking one from a package apply there
    /// too. Shared rather than copied: two power series that must agree is one too many.
    /// </remarks>
    internal static double BesselI0(double x)
    {
        double halfSquared = x * x / 4.0;
        double term = 1.0;
        double sum = 1.0;

        for (int k = 1; k < 256; k++)
        {
            term *= halfSquared / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17)
                break;
        }

        return sum;
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
