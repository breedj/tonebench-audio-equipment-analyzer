namespace Qa403.Dsp;

/// <summary>
/// Which two-tone intermodulation convention a measurement follows. The two differ in which
/// products count and what they are referenced to, and a figure quoted without its method is
/// meaningless — the same amplifier reads 10–20 dB apart between them.
/// </summary>
public enum IntermodulationMethod
{
    /// <summary>
    /// SMPTE RP120 / DIN 45403 modulation-type test: a large low-frequency tone f1 (60 Hz, or
    /// 250 Hz for DIN) and a small high-frequency carrier f2 (7 kHz / 8 kHz) at a 4:1 amplitude
    /// ratio. Nonlinearity modulates the carrier by the LF tone, so the products are the
    /// sidebands <c>f2 ± n·f1</c> (n = 1 is order 2, n = 2 is order 3, …). IMD is the RMS sum of
    /// the sidebands relative to the <b>carrier</b> f2 — by definition the small tone, which is why
    /// SMPTE figures run higher than CCIF ones for the same amplifier.
    /// </summary>
    Smpte,

    /// <summary>
    /// CCIF / ITU-R (IEC 60268-3 difference-frequency) test: two equal tones close together at
    /// the top of the band (19 + 20 kHz, or 18.5 + 19.5 kHz). The products are the difference
    /// frequencies <c>|m·f1 − n·f2|</c> with m + n the order: f2 − f1 (2nd), 2f1 − f2 and
    /// 2f2 − f1 (3rd), and so on; sum products (39 kHz and its neighbours) are out of band and
    /// excluded by definition. IMD is the RMS sum of the products relative to the <b>RMS sum of
    /// both tones</b>, <c>sqrt(V1² + V2²)</c> — the convention the QA403 tutorial computes by hand
    /// ("the RMS sum of the highest in-band products / the RMS sum of the two tones"). Other
    /// analyzers reference a single tone (+3 dB on the figure for equal tones) or the arithmetic
    /// sum V1 + V2 (−3 dB); the choice is a convention, not a correction, so do not "fix" it here.
    /// </summary>
    Ccif,
}

/// <summary>
/// One intermodulation product. <see cref="M"/> and <see cref="N"/> are the tone multipliers:
/// for <see cref="IntermodulationMethod.Ccif"/> the product is <c>|M·f1 − N·f2|</c>; for
/// <see cref="IntermodulationMethod.Smpte"/> it is <c>f2 + N·f1</c> with <c>M = +1</c> for the
/// upper sideband and <c>M = −1</c> for the lower one (so the label "f2 − 2·f1" can be rebuilt
/// from the numbers). <see cref="Dbc"/> is relative to the method's reference, not to either tone.
/// </summary>
public readonly record struct IntermodulationProduct(
    int Order, int M, int N, int Bin, double FrequencyHz, double AmplitudeVrms, double AmplitudeDbv, double Dbc);

/// <summary>
/// Result of <see cref="IntermodulationAnalyzer.Analyze"/>. A class rather than a ref struct
/// because it outlives the PSD it was measured from — it rides on the pipeline's meters record
/// and into a saved measurement.
/// </summary>
public sealed record IntermodulationResult(
    IntermodulationMethod Method,
    int MaxOrder,
    ToneMeasurement ToneLow,
    ToneMeasurement ToneHigh,
    double ToneLowFreqHz,
    double ToneHighFreqHz,
    double ReferenceVrms,
    double Imd,
    IReadOnlyList<(int Order, double Ratio)> PerOrder,
    IReadOnlyList<IntermodulationProduct> Products)
{
    public double ImdPercent => Imd * 100.0;

    public double ImdDb => 20.0 * Math.Log10(Math.Max(Imd, double.Epsilon));

    public double ToneLowDbv => Decibels(ToneLow.AmplitudeVrms);

    public double ToneHighDbv => Decibels(ToneHigh.AmplitudeVrms);

    public double ReferenceDbv => Decibels(ReferenceVrms);

    /// <summary>
    /// The level of a single sine whose peak equals the pair's peak: <c>V1 + V2</c> (RMS-scaled).
    /// This is what an amplifier's rating is quoted against, and the level every IMD test in the
    /// QA403 tutorial is set up from — "the same peak voltage as the sine wave that is the
    /// equivalent test power".
    /// </summary>
    public double SineEquivalentVrms => ToneLow.AmplitudeVrms + ToneHigh.AmplitudeVrms;

    public double SineEquivalentDbv => Decibels(SineEquivalentVrms);

    /// <summary>
    /// The pair's true RMS, <c>sqrt(V1² + V2²)</c> — what a voltmeter or a "RMS power" tile reads.
    /// For equal tones it sits 3.01 dB below <see cref="SineEquivalentVrms"/>; the tutorial notes
    /// the QA40x's reported RMS power is 52 % of the sine-equivalent power for exactly this reason.
    /// </summary>
    public double CombinedRmsVrms => Math.Sqrt((ToneLow.AmplitudeVrms * ToneLow.AmplitudeVrms) + (ToneHigh.AmplitudeVrms * ToneHigh.AmplitudeVrms));

    public double CombinedRmsDbv => Decibels(CombinedRmsVrms);

    private static double Decibels(double vrms) => 20.0 * Math.Log10(Math.Max(vrms, double.Epsilon));
}

/// <summary>
/// Two-tone intermodulation on a PSD. Tones and products are all measured by
/// <see cref="HarmonicAnalyzer.MeasureTone"/> — peak search, skirt sum, local noise floor
/// subtracted — so an IMD figure and a THD figure from the same record agree about every bin.
/// </summary>
public static class IntermodulationAnalyzer
{
    /// <summary>
    /// Fewest bins that may separate a tone from its nearest product (SMPTE: f1 itself; CCIF:
    /// f2 − f1). With a ±3-bin skirt on each, anything closer puts a product's skirt inside the
    /// carrier's own window mainlobe and reads the window as distortion — SMPTE's 60 Hz is 1.3 bins
    /// at a 4096-point FFT at 192 kHz. <see cref="Analyze"/> returns <see langword="null"/> below
    /// this instead of a number that means nothing.
    /// </summary>
    public const int MinToneSpacingBins = (2 * HarmonicAnalyzer.DefaultSkirtHalfWidthBins) + 2;

    /// <summary>
    /// Measures both tones at their expected bins and every product of order 2..<paramref name="maxOrder"/>
    /// whose bin lies in (0, <paramref name="maxBinInclusive"/>] and clear of both tones. Products
    /// are formed from the <b>located</b> tone bins, in integer bins, so a tone that snapping or
    /// clock drift moved by a bin still has its products land exactly where they are. A product
    /// outside the band is omitted, not counted as zero, and an order with no products is absent
    /// from <see cref="IntermodulationResult.PerOrder"/>.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when either tone bin is outside the spectrum, when either tone has no
    /// measurable amplitude, or when the tones are closer than <see cref="MinToneSpacingBins"/>.
    /// </returns>
    public static IntermodulationResult? Analyze(
        PsdSpectrum psd,
        int toneLowBin,
        int toneHighBin,
        IntermodulationMethod method,
        int maxOrder,
        int maxBinInclusive,
        int peakSearchSpanBins = HarmonicAnalyzer.DefaultPeakSearchSpanBins,
        int skirtHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins,
        bool subtractNoiseFloor = true)
    {
        if (maxOrder < 2)
            throw new ArgumentOutOfRangeException(nameof(maxOrder), maxOrder, "Intermodulation starts at order 2.");

        int length = psd.ValuesV2PerHz.Length;
        if (toneLowBin <= 0 || toneHighBin <= 0 || toneLowBin >= length || toneHighBin >= length || toneLowBin >= toneHighBin)
            return null;

        var low = HarmonicAnalyzer.MeasureTone(psd, toneLowBin, peakSearchSpanBins, skirtHalfWidthBins, subtractNoiseFloor);
        var high = HarmonicAnalyzer.MeasureTone(psd, toneHighBin, peakSearchSpanBins, skirtHalfWidthBins, subtractNoiseFloor);
        if (low.AmplitudeVrms <= 0.0 || high.AmplitudeVrms <= 0.0 || low.Bin >= high.Bin)
            return null;

        int b1 = low.Bin;
        int b2 = high.Bin;
        if (Math.Min(b1, b2 - b1) < MinToneSpacingBins)
            return null;

        double reference = method switch
        {
            IntermodulationMethod.Smpte => high.AmplitudeVrms,
            IntermodulationMethod.Ccif => Math.Sqrt((low.AmplitudeVrms * low.AmplitudeVrms) + (high.AmplitudeVrms * high.AmplitudeVrms)),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
        };

        int highestBin = Math.Min(length - 1, maxBinInclusive);
        var products = new List<IntermodulationProduct>();
        var orderPower = new SortedDictionary<int, double>();
        double totalPower = 0.0;

        foreach (var (order, m, n, bin) in EnumerateProductBins(method, b1, b2, maxOrder))
        {
            if (bin <= 0 || bin > highestBin)
                continue;
            if (Math.Abs(bin - b1) <= skirtHalfWidthBins || Math.Abs(bin - b2) <= skirtHalfWidthBins)
                continue;
            if (products.Any(p => p.Bin == bin))
                continue;

            var tone = HarmonicAnalyzer.MeasureTone(psd, bin, peakSearchSpanBins, skirtHalfWidthBins, subtractNoiseFloor);
            double vrms = tone.AmplitudeVrms;
            double power = vrms * vrms;

            totalPower += power;
            orderPower[order] = orderPower.TryGetValue(order, out double so) ? so + power : power;

            products.Add(new IntermodulationProduct(
                order, m, n, tone.Bin, tone.Bin * psd.BinSizeHz, vrms,
                20.0 * Math.Log10(Math.Max(vrms, double.Epsilon)),
                20.0 * Math.Log10(Math.Max(vrms, double.Epsilon) / reference)));
        }

        var perOrder = orderPower.Select(kv => (kv.Key, Math.Sqrt(kv.Value) / reference)).ToArray();

        return new IntermodulationResult(
            method, maxOrder, low, high, b1 * psd.BinSizeHz, b2 * psd.BinSizeHz, reference,
            Math.Sqrt(totalPower) / reference, perOrder, products);
    }

    /// <summary>
    /// The product bins a method defines for tones at <paramref name="toneLowBin"/> and
    /// <paramref name="toneHighBin"/>, up to <paramref name="maxOrder"/>, in the (M, N) convention of
    /// <see cref="IntermodulationProduct"/>. Pure arithmetic: no range checking, negative or zero bins
    /// included, so the rule itself can be pinned by a test. Order is ascending, lower sideband /
    /// smaller M first within an order.
    /// </summary>
    public static IEnumerable<(int Order, int M, int N, int Bin)> EnumerateProductBins(
        IntermodulationMethod method, int toneLowBin, int toneHighBin, int maxOrder)
    {
        switch (method)
        {
            case IntermodulationMethod.Smpte:
                for (int n = 1; n <= maxOrder - 1; n++)
                {
                    yield return (n + 1, -1, n, toneHighBin - (n * toneLowBin));
                    yield return (n + 1, +1, n, toneHighBin + (n * toneLowBin));
                }
                break;

            case IntermodulationMethod.Ccif:
                for (int order = 2; order <= maxOrder; order++)
                {
                    for (int m = 1; m <= order - 1; m++)
                    {
                        int n = order - m;
                        yield return (order, m, n, Math.Abs((m * toneLowBin) - (n * toneHighBin)));
                    }
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, null);
        }
    }
}
