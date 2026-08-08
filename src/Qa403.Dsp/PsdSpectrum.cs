using System.Numerics;

namespace Qa403.Dsp;

/// <summary>A bin range, both ends inclusive, used to notch/exclude bins from a PSD integration.</summary>
public readonly record struct BinRange(int LoBinInclusive, int HiBinInclusive)
{
    public bool Contains(int bin) => bin >= LoBinInclusive && bin <= HiBinInclusive;
}

/// <summary>
/// Power-spectral-density normalisation: S_xx(f_k) = 2|X_k|² / (fs·S2), in V²/Hz. DC and
/// Nyquist have no mirror image so they carry half the weight of interior bins.
///
/// Deliberately a distinct type from <see cref="AmplitudeSpectrum"/> — see that type's
/// remarks. A ref struct: it borrows the caller's destination buffer, never allocates.
/// </summary>
public readonly ref struct PsdSpectrum
{
    private PsdSpectrum(ReadOnlySpan<double> valuesV2PerHz, double sampleRateHz, double binSizeHz, WindowSums sums)
    {
        ValuesV2PerHz = valuesV2PerHz;
        SampleRateHz = sampleRateHz;
        BinSizeHz = binSizeHz;
        Sums = sums;
    }

    public ReadOnlySpan<double> ValuesV2PerHz { get; }

    public double SampleRateHz { get; }

    public double BinSizeHz { get; }

    public WindowSums Sums { get; }

    /// <summary>
    /// Computes the one-sided PSD into <paramref name="destination"/> (must be exactly
    /// <c>spectrum.Length</c> long, i.e. N/2+1 for the N-point transform) and returns a
    /// view over it. No allocation.
    /// </summary>
    public static PsdSpectrum Compute(ReadOnlySpan<Complex> spectrum, double sampleRateHz, WindowSums sums, Span<double> destination)
    {
        if (destination.Length != spectrum.Length)
            throw new ArgumentException("Destination must match spectrum length.", nameof(destination));

        int nyquist = spectrum.Length - 1;
        for (int k = 0; k < spectrum.Length; k++)
        {
            double scale = k == 0 || k == nyquist ? 1.0 : 2.0;
            double magnitude = spectrum[k].Magnitude;
            destination[k] = scale * magnitude * magnitude / (sampleRateHz * sums.S2);
        }

        double binSizeHz = sampleRateHz / (2 * nyquist);
        return new PsdSpectrum(destination, sampleRateHz, binSizeHz, sums);
    }

    /// <summary>
    /// Wraps an already-computed PSD — e.g. the output of averaging several <see cref="Compute"/>
    /// results across ticks — without recomputing anything from a complex spectrum.
    /// <paramref name="valuesV2PerHz"/> is used as-is, in V²/Hz.
    /// </summary>
    public static PsdSpectrum FromValues(ReadOnlySpan<double> valuesV2PerHz, double sampleRateHz, double binSizeHz, WindowSums sums) =>
        new(valuesV2PerHz, sampleRateHz, binSizeHz, sums);

    public int BinOfFrequency(double frequencyHz) => (int)Math.Round(frequencyHz / BinSizeHz);

    /// <summary>
    /// The inclusive bin window covering [<paramref name="frequencyLoHz"/>, <paramref name="frequencyHiHz"/>],
    /// clamped to the spectrum. Bin 0 (DC) is never included unless the caller explicitly asks for
    /// 0 Hz: an analyzer's noise figures are defined over an audio band, and on a DC-coupled input
    /// the offset sitting in bin 0 is orders of magnitude larger than the noise being measured.
    /// </summary>
    public BinRange BandOf(double frequencyLoHz, double frequencyHiHz) =>
        new(
            Math.Max(frequencyLoHz > 0.0 ? 1 : 0, BinOfFrequency(frequencyLoHz)),
            Math.Min(ValuesV2PerHz.Length - 1, BinOfFrequency(frequencyHiHz)));

    /// <summary>The whole spectrum including DC — only for callers that genuinely want everything.</summary>
    public BinRange FullBand => new(0, ValuesV2PerHz.Length - 1);

    /// <summary>Parseval: sqrt(Σ PSD·Δf) over every bin in the spectrum.</summary>
    public double IntegrateVrms() => IntegrateVrms(0, ValuesV2PerHz.Length - 1);

    /// <summary>sqrt(Σ PSD·Δf) over [loBinInclusive, hiBinInclusive], clamped to the spectrum.</summary>
    public double IntegrateVrms(int loBinInclusive, int hiBinInclusive)
    {
        loBinInclusive = Math.Max(0, loBinInclusive);
        hiBinInclusive = Math.Min(ValuesV2PerHz.Length - 1, hiBinInclusive);

        double power = 0.0;
        for (int k = loBinInclusive; k <= hiBinInclusive; k++)
            power += ValuesV2PerHz[k] * BinSizeHz;

        return Math.Sqrt(Math.Max(0.0, power));
    }

    /// <summary>Band-limited noise floor: sqrt(Σ PSD·Δf) over [fLoHz, fHiHz].</summary>
    public double IntegrateVrms(double frequencyLoHz, double frequencyHiHz) =>
        IntegrateVrms(BinOfFrequency(frequencyLoHz), BinOfFrequency(frequencyHiHz));

    /// <summary>
    /// sqrt(Σ PSD·Δf) over every bin of <paramref name="band"/> NOT covered by
    /// <paramref name="excludedRanges"/> — the "notch" used by THD+N (excludes the fundamental)
    /// and SNR (excludes fundamental + harmonics).
    ///
    /// <paramref name="band"/> is the measurement bandwidth and is not optional by design. An
    /// unbounded version of this integration silently summed bin 0, and a DC-coupled input's
    /// few-mV offset then swamped the actual noise floor — on the bench that read THD+N ~33 dB
    /// worse than it really was (a 4.6 mV offset alone gives THD+N = -46.8 dB against a 1 V
    /// fundamental, versus -76.8 dB for the same trace with DC excluded).
    /// </summary>
    public double IntegrateVrmsExcluding(ReadOnlySpan<BinRange> excludedRanges, BinRange band)
    {
        int lo = Math.Max(0, band.LoBinInclusive);
        int hi = Math.Min(ValuesV2PerHz.Length - 1, band.HiBinInclusive);

        double power = 0.0;
        for (int k = lo; k <= hi; k++)
        {
            bool excluded = false;
            for (int r = 0; r < excludedRanges.Length; r++)
            {
                if (excludedRanges[r].Contains(k))
                {
                    excluded = true;
                    break;
                }
            }

            if (!excluded)
                power += ValuesV2PerHz[k] * BinSizeHz;
        }

        return Math.Sqrt(Math.Max(0.0, power));
    }
}
