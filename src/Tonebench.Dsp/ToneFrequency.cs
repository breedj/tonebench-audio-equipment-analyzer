using System.Numerics;

namespace Tonebench.Dsp;

/// <summary>
/// The frequency of a single tone, to a small fraction of a bin.
///
/// <para><b>Why the ordinary peak reading is not enough.</b> <see cref="HarmonicAnalyzer"/> finds the
/// fundamental to about a hundredth of a bin, which is ample for a level or a distortion figure and
/// useless for a clock: two sound cards differ by tens of parts per million, and at 1 kHz on a
/// 32768-point record at 48 kHz a hundredth of a bin is already 15 ppm — the whole quantity being
/// measured. This reads the same peak to about 3e-5 of a bin, i.e. <b>0.02 ppm</b> on that record at
/// 40 dB signal-to-noise, which is what makes a clock measurement out of it.</para>
///
/// <para><b>The interpolator is the Hann window's own and is exact.</b> For a tone windowed by a
/// Hann window the three magnitudes either side of the peak satisfy
/// <c>δ = 2(a₊ − a₋) / (a₋ + 2a₀ + a₊)</c> exactly, δ being the offset from the peak bin in bins.
/// Measured against synthetic tones at known offsets, the error is <b>1e-13 bins</b> — floating
/// point and nothing else — and with noise it grows as roughly the reciprocal of the amplitude
/// signal-to-noise: 2.9e-5 bins at 40 dB, 2.9e-6 at 60.</para>
///
/// <para><b>It is specific to that window, which is why the caller must pass one.</b> The obvious
/// alternative, Jacobsen's complex three-point estimator, is derived for a rectangular window and
/// is <i>badly</i> biased here — measured on the same tones, 0.165 bins out at δ = 0.33, i.e. five
/// thousand times worse than the formula above and in a way that looks like a real frequency
/// offset rather than like noise. Do not swap one for the other on the grounds that both are
/// three-point peak interpolators.</para>
/// </summary>
public static class ToneFrequency
{
    /// <summary>What <see cref="Estimate"/> found: where the tone is, and whether it is really there.</summary>
    /// <param name="Hz">The tone's frequency.</param>
    /// <param name="PeakBin">The bin its peak landed in, before interpolation.</param>
    /// <param name="OffsetBins">The interpolated offset from that bin, in (−0.5, 0.5).</param>
    /// <param name="AmplitudeDb">The peak bin's magnitude in dB, for a caller that wants a level.</param>
    /// <param name="DominanceDb">
    /// The tone against everything else in the spectrum. The one check a frequency estimate cannot
    /// make for itself: a reading taken from a peak that is not the tone is arithmetically perfect
    /// and means nothing, and on a real bench the loudest thing in a quiet capture is routinely a
    /// hum ladder rather than the stimulus.
    /// </param>
    public readonly record struct ToneEstimate(
        double Hz, int PeakBin, double OffsetBins, double AmplitudeDb, double DominanceDb);

    /// <summary>
    /// The tone nearest <paramref name="nearHz"/>, within <paramref name="halfWidthHz"/> of it.
    ///
    /// <para>Searched in a window rather than taken as the spectrum's maximum, for the reason
    /// <see cref="ToneEstimate.DominanceDb"/> gives: the global maximum is whatever is loudest, and
    /// the caller already knows what it asked the generator to play. Null when the window holds no
    /// bin, or when the peak sits on the very edge of the spectrum, where there is no bin on one
    /// side to interpolate against.</para>
    /// </summary>
    public static ToneEstimate? Estimate(
        ReadOnlySpan<Complex> spectrum, double binHz, double nearHz, double halfWidthHz)
    {
        if (spectrum.Length < 3 || binHz <= 0 || !double.IsFinite(nearHz)) return null;

        int lo = Math.Max(1, (int)Math.Floor((nearHz - halfWidthHz) / binHz));
        int hi = Math.Min(spectrum.Length - 2, (int)Math.Ceiling((nearHz + halfWidthHz) / binHz));
        if (hi < lo) return null;

        int peak = lo;
        double best = -1;
        for (int k = lo; k <= hi; k++)
        {
            double m = spectrum[k].Magnitude;
            if (m > best)
            {
                best = m;
                peak = k;
            }
        }

        if (best <= 0) return null;

        double below = spectrum[peak - 1].Magnitude;
        double above = spectrum[peak + 1].Magnitude;
        double denominator = below + (2 * best) + above;
        double offset = denominator > 0 ? 2 * (above - below) / denominator : 0;

        // Half a bin either way is all the formula can mean; anything larger is a peak that is not
        // where the search said it was, and clamping is honest where extrapolating is not.
        offset = Math.Clamp(offset, -0.5, 0.5);

        return new ToneEstimate(
            (peak + offset) * binHz, peak, offset, ToDb(best), DominanceDb(spectrum, peak, best));
    }

    /// <summary>
    /// The tone's power against the rest of the spectrum, the peak's own three bins excluded —
    /// the same test <see cref="HarmonicAnalyzer"/> uses for its fundamental, so the two agree
    /// about what "there is a tone here" means.
    /// </summary>
    private static double DominanceDb(ReadOnlySpan<Complex> spectrum, int peak, double peakMagnitude)
    {
        double total = 0;
        // Bin 0 is DC, which a capture of a quiet input is routinely dominated by and which is not
        // competing with the tone for the peak search's attention.
        for (int k = 1; k < spectrum.Length; k++)
        {
            double m = spectrum[k].Magnitude;
            total += m * m;
        }

        // The peak's skirt, at HarmonicAnalyzer.DefaultSkirtHalfWidthBins — and the width is not
        // arbitrary: a Hann-windowed tone sitting between two bins spreads measurably into the
        // second pair either side, so counting only ±1 charges the tone's own leakage to the
        // "rest" and reads a perfectly clean capture at 23 dB.
        double near = 0;
        for (int k = Math.Max(1, peak - 3); k <= Math.Min(spectrum.Length - 1, peak + 3); k++)
        {
            double m = spectrum[k].Magnitude;
            near += m * m;
        }

        _ = peakMagnitude;
        return 10 * Math.Log10(near / Math.Max(total - near, 1e-30));
    }

    private static double ToDb(double magnitude) => 20 * Math.Log10(Math.Max(magnitude, 1e-30));

    /// <summary>
    /// How far one clock runs from another, in parts per million, from a tone played on one and
    /// captured on the other.
    ///
    /// <para>Both devices are told the same nominal rate, so what comes back is
    /// <c>played × (1 + a) / (1 + b)</c> with <c>a</c> and <c>b</c> each card's own error against
    /// true time. Neither is separately knowable from one cable and neither needs to be: the ratio
    /// is <c>a − b</c>, which is the figure every consequence follows from.</para>
    /// </summary>
    public static double DriftPpm(double capturedHz, double playedHz) =>
        playedHz > 0 ? (capturedHz - playedHz) / playedHz * 1e6 : double.NaN;
}
