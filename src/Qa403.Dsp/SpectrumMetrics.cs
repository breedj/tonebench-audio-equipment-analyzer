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
    public static double BinSizeHz(double sampleRateHz, int fftSize) => sampleRateHz / fftSize;

    public static int BinOfFrequency(double frequencyHz, double binSizeHz) =>
        (int)Math.Round(frequencyHz / binSizeHz);

    /// <summary>THD = sqrt(Σ V_h² for h=2..H) / V1. <paramref name="harmonics"/> must start with order 1 (the fundamental).</summary>
    public static double Thd(ReadOnlySpan<Harmonic> harmonics)
    {
        RequireFundamentalFirst(harmonics);

        double v1 = harmonics[0].AmplitudeVrms;
        double sumSquares = 0.0;
        for (int i = 1; i < harmonics.Length; i++)
            sumSquares += harmonics[i].AmplitudeVrms * harmonics[i].AmplitudeVrms;

        return Math.Sqrt(sumSquares) / v1;
    }

    public static double ThdDb(ReadOnlySpan<Harmonic> harmonics) => 20.0 * Math.Log10(Thd(harmonics));

    /// <summary>
    /// THD+N = V(noise+distortion) / V1, where V(noise+distortion) is the PSD integrated over
    /// every bin except the fundamental's ±skirtHalfWidthBins (the "notch").
    /// </summary>
    public static double ThdPlusN(PsdSpectrum psd, Harmonic fundamental, int skirtHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins)
    {
        Span<BinRange> notch = [new BinRange(fundamental.Bin - skirtHalfWidthBins, fundamental.Bin + skirtHalfWidthBins)];
        double vNoiseAndDistortion = psd.IntegrateVrmsExcluding(notch);
        return vNoiseAndDistortion / fundamental.AmplitudeVrms;
    }

    public static double ThdPlusNDb(PsdSpectrum psd, Harmonic fundamental, int skirtHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins) =>
        20.0 * Math.Log10(ThdPlusN(psd, fundamental, skirtHalfWidthBins));

    /// <summary>SINAD = V1 / V(noise+distortion) = 1 / THD+N.</summary>
    public static double Sinad(PsdSpectrum psd, Harmonic fundamental, int skirtHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins) =>
        1.0 / ThdPlusN(psd, fundamental, skirtHalfWidthBins);

    public static double SinadDb(PsdSpectrum psd, Harmonic fundamental, int skirtHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins) =>
        -ThdPlusNDb(psd, fundamental, skirtHalfWidthBins);

    /// <summary>
    /// SNR = V1 / Vnoise, where Vnoise is the PSD integrated over every bin except the
    /// fundamental AND harmonics 2..H (distortion is excluded from "noise", unlike THD+N).
    /// </summary>
    public static double Snr(PsdSpectrum psd, ReadOnlySpan<Harmonic> harmonics, int skirtHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins)
    {
        RequireFundamentalFirst(harmonics);

        Span<BinRange> excluded = harmonics.Length <= 32 ? stackalloc BinRange[harmonics.Length] : new BinRange[harmonics.Length];
        for (int i = 0; i < harmonics.Length; i++)
            excluded[i] = new BinRange(harmonics[i].Bin - skirtHalfWidthBins, harmonics[i].Bin + skirtHalfWidthBins);

        double vNoise = psd.IntegrateVrmsExcluding(excluded);
        return harmonics[0].AmplitudeVrms / vNoise;
    }

    public static double SnrDb(PsdSpectrum psd, ReadOnlySpan<Harmonic> harmonics, int skirtHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins) =>
        20.0 * Math.Log10(Snr(psd, harmonics, skirtHalfWidthBins));

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
