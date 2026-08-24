namespace Qa403.Dsp;

/// <summary>
/// THD, THD+N, SNR, SINAD and band-limited noise floor, built on top of <see cref="Harmonic"/>
/// results (amplitude domain) and <see cref="PsdSpectrum"/> (power domain, for full-band
/// integration). THD+N notches the fundamental's bins out of the PSD sum rather than
/// subtracting V1² in the frequency domain — subtraction can go negative from rounding and
/// silently hides bugs; notching only ever removes bins that are actually there.
/// </summary>
public static class SpectrumMetrics
{
    /// <summary>
    /// Half-width of the fundamental's THD+N/SNR notch, in octaves — the notch spans
    /// [f0/2^n, f0·2^n]. Matches QuantAsylum's own PyQa40x (<c>compute_thdn_linear</c>'s
    /// <c>notch_octaves=0.5</c>).
    ///
    /// Deliberately octave-based rather than a fixed bin count. A tone's window skirts spread
    /// far past its main lobe when it doesn't land exactly on a bin, so the notch has to be much
    /// wider than the lobe used to *measure* it — qa40x-rs uses ±6 bins to measure and ±24 to
    /// exclude for the same reason. But a fixed bin count doesn't scale: ±24 bins is ±35 Hz at a
    /// 128k FFT and ±1125 Hz at a 4k one, where it would reach past H2 and notch real distortion
    /// out of THD+N. Half an octave is 0.71·f0 to 1.41·f0 at every FFT size, always clear of H2.
    /// </summary>
    public const double DefaultNotchOctaves = 0.5;

    public static double BinSizeHz(double sampleRateHz, int fftSize) => sampleRateHz / fftSize;

    public static int BinOfFrequency(double frequencyHz, double binSizeHz) =>
        (int)Math.Round(frequencyHz / binSizeHz);

    /// <summary>
    /// The bin range notched out around the fundamental for THD+N/SNR. Never narrower than
    /// <paramref name="minHalfWidthBins"/>, so a very low fundamental (where half an octave is
    /// only a bin or two) still has its whole lobe removed.
    /// </summary>
    public static BinRange FundamentalNotch(
        PsdSpectrum psd,
        Harmonic fundamental,
        double notchOctaves = DefaultNotchOctaves,
        int minHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins)
    {
        double centerHz = fundamental.Bin * psd.BinSizeHz;
        double factor = Math.Pow(2.0, Math.Max(0.0, notchOctaves));

        int lo = Math.Min(psd.BinOfFrequency(centerHz / factor), fundamental.Bin - minHalfWidthBins);
        int hi = Math.Max(psd.BinOfFrequency(centerHz * factor), fundamental.Bin + minHalfWidthBins);
        return new BinRange(lo, hi);
    }

    /// <summary>THD = sqrt(Σ V_h² for h=2..H) / V1. <paramref name="harmonics"/> must start with order 1 (the fundamental).</summary>
    public static double Thd(ReadOnlySpan<Harmonic> harmonics) => PartialThd(harmonics, fromOrder: 2);

    /// <summary>
    /// sqrt(Σ V_h² for h &gt;= <paramref name="fromOrder"/>) / V1 — <see cref="Thd"/> restricted to
    /// the harmonics at or above one order. <c>PartialThd(h, 2)</c> is THD itself.
    ///
    /// Exists for the "D6+" roll-up a THD-vs-frequency sweep plots: the individual 2nd..5th
    /// harmonics get their own traces, and everything from the 6th up is power-summed into one, so
    /// a chart doesn't carry eleven near-identical lines down in the grass. Power-summed rather
    /// than "the largest of them" because that is what makes the buckets add back up to THD:
    /// Thd² == PartialThd(2..5)² + PartialThd(6..)².
    ///
    /// Orders are read off <see cref="Harmonic.Order"/>, not from the span's indices, so a caller
    /// that trimmed the list still gets the right bucket.
    /// </summary>
    public static double PartialThd(ReadOnlySpan<Harmonic> harmonics, int fromOrder)
    {
        RequireFundamentalFirst(harmonics);
        if (fromOrder < 2)
            throw new ArgumentOutOfRangeException(nameof(fromOrder), fromOrder, "The fundamental is the reference, so a partial THD starts at order 2 or above.");

        double v1 = harmonics[0].AmplitudeVrms;
        double sumSquares = 0.0;
        for (int i = 1; i < harmonics.Length; i++)
        {
            if (harmonics[i].Order < fromOrder)
                continue;

            sumSquares += harmonics[i].AmplitudeVrms * harmonics[i].AmplitudeVrms;
        }

        return Math.Sqrt(sumSquares) / v1;
    }

    public static double ThdDb(ReadOnlySpan<Harmonic> harmonics) => 20.0 * Math.Log10(Thd(harmonics));

    /// <summary>
    /// THD+N = V(noise+distortion) / V1, where V(noise+distortion) is the PSD integrated across
    /// <paramref name="band"/> except the fundamental's ±skirtHalfWidthBins (the "notch").
    ///
    /// <paramref name="band"/> is the measurement bandwidth — see
    /// <see cref="PsdSpectrum.IntegrateVrmsExcluding"/> for why it is mandatory rather than
    /// defaulting to the whole spectrum.
    /// </summary>
    public static double ThdPlusN(PsdSpectrum psd, Harmonic fundamental, BinRange band, double notchOctaves = DefaultNotchOctaves)
    {
        Span<BinRange> notch = [FundamentalNotch(psd, fundamental, notchOctaves)];
        double vNoiseAndDistortion = psd.IntegrateVrmsExcluding(notch, band);
        return vNoiseAndDistortion / fundamental.AmplitudeVrms;
    }

    public static double ThdPlusNDb(PsdSpectrum psd, Harmonic fundamental, BinRange band, double notchOctaves = DefaultNotchOctaves) =>
        20.0 * Math.Log10(ThdPlusN(psd, fundamental, band, notchOctaves));

    /// <summary>SINAD = V1 / V(noise+distortion) = 1 / THD+N.</summary>
    public static double Sinad(PsdSpectrum psd, Harmonic fundamental, BinRange band, double notchOctaves = DefaultNotchOctaves) =>
        1.0 / ThdPlusN(psd, fundamental, band, notchOctaves);

    public static double SinadDb(PsdSpectrum psd, Harmonic fundamental, BinRange band, double notchOctaves = DefaultNotchOctaves) =>
        -ThdPlusNDb(psd, fundamental, band, notchOctaves);

    /// <summary>
    /// SNR = V1 / Vnoise, where Vnoise is the PSD integrated across <paramref name="band"/> except
    /// the fundamental AND harmonics 2..H (distortion is excluded from "noise", unlike THD+N).
    ///
    /// The fundamental gets the wide octave notch; the harmonics only get their own lobe removed.
    /// Widening the harmonic exclusions to match would start eating real noise bins for no gain —
    /// the point of excluding a harmonic here is just to keep its energy out of "noise".
    /// </summary>
    public static double Snr(
        PsdSpectrum psd,
        ReadOnlySpan<Harmonic> harmonics,
        BinRange band,
        double notchOctaves = DefaultNotchOctaves,
        int harmonicHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins)
    {
        RequireFundamentalFirst(harmonics);

        Span<BinRange> excluded = harmonics.Length <= 32 ? stackalloc BinRange[harmonics.Length] : new BinRange[harmonics.Length];
        excluded[0] = FundamentalNotch(psd, harmonics[0], notchOctaves, harmonicHalfWidthBins);
        for (int i = 1; i < harmonics.Length; i++)
            excluded[i] = new BinRange(harmonics[i].Bin - harmonicHalfWidthBins, harmonics[i].Bin + harmonicHalfWidthBins);

        double vNoise = psd.IntegrateVrmsExcluding(excluded, band);
        return harmonics[0].AmplitudeVrms / vNoise;
    }

    public static double SnrDb(
        PsdSpectrum psd,
        ReadOnlySpan<Harmonic> harmonics,
        BinRange band,
        double notchOctaves = DefaultNotchOctaves,
        int harmonicHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins) =>
        20.0 * Math.Log10(Snr(psd, harmonics, band, notchOctaves, harmonicHalfWidthBins));

    /// <summary>Band-limited noise floor: sqrt(Σ PSD·Δf) over [frequencyLoHz, frequencyHiHz].</summary>
    public static double BandNoiseFloorVrms(PsdSpectrum psd, double frequencyLoHz, double frequencyHiHz) =>
        psd.IntegrateVrms(frequencyLoHz, frequencyHiHz);

    private static void RequireFundamentalFirst(ReadOnlySpan<Harmonic> harmonics)
    {
        if (harmonics.Length < 1)
            throw new ArgumentException("At least the fundamental (order 1) is required.", nameof(harmonics));
        if (harmonics[0].Order != 1)
            throw new ArgumentException("harmonics[0] must be order 1 (the fundamental).", nameof(harmonics));
    }
}
