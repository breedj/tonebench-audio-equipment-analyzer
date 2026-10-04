namespace Tonebench.Dsp;

/// <summary>The standard two-tone pairs. <see cref="Custom"/> is any pair the user typed.</summary>
public enum IntermodulationPreset
{
    Custom,

    /// <summary>SMPTE RP120: 60 Hz + 7 kHz, 4:1.</summary>
    Smpte,

    /// <summary>DIN 45403: 250 Hz + 8 kHz, 4:1.</summary>
    Din,

    /// <summary>CCIF / ITU-R: 19 kHz + 20 kHz, 1:1.</summary>
    CcifItu,

    /// <summary>
    /// CCIF with the pair moved down to 18.5 + 19.5 kHz so the high odd-order products do not fall
    /// on the even-order ones (the tutorial's "some prefer" variant).
    /// </summary>
    Ccif18p5,
}

/// <summary>One preset's definition. <see cref="LowToHighRatio"/> is amplitude, so 4 means the LF tone is 12.04 dB above the HF one.</summary>
public sealed record IntermodulationPresetSpec(
    IntermodulationPreset Preset, double LowHz, double HighHz, double LowToHighRatio, IntermodulationMethod Method, int DefaultMaxOrder);

/// <summary>
/// The tone pairs of the standard IMD tests and the level arithmetic they share. Pure, so the
/// client's copy of the same table (<c>web/src/imd/presets.ts</c>) can be pinned to it by test.
/// </summary>
public static class IntermodulationPresets
{
    /// <summary>Highest order the SMPTE sideband count runs to by default (four sideband pairs). The QA40x's own default.</summary>
    public const int SmpteDefaultMaxOrder = 5;

    /// <summary>Highest order counted for CCIF by default: the 2nd-order difference tone and the two 3rd-order products. The QA40x's own default.</summary>
    public const int CcifDefaultMaxOrder = 3;

    public static IntermodulationPresetSpec Resolve(IntermodulationPreset preset) => preset switch
    {
        IntermodulationPreset.Smpte => new(preset, 60.0, 7_000.0, 4.0, IntermodulationMethod.Smpte, SmpteDefaultMaxOrder),
        IntermodulationPreset.Din => new(preset, 250.0, 8_000.0, 4.0, IntermodulationMethod.Smpte, SmpteDefaultMaxOrder),
        IntermodulationPreset.CcifItu => new(preset, 19_000.0, 20_000.0, 1.0, IntermodulationMethod.Ccif, CcifDefaultMaxOrder),
        IntermodulationPreset.Ccif18p5 => new(preset, 18_500.0, 19_500.0, 1.0, IntermodulationMethod.Ccif, CcifDefaultMaxOrder),
        IntermodulationPreset.Custom => throw new ArgumentException("Custom has no fixed tones; the caller supplies them.", nameof(preset)),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
    };

    public static int DefaultMaxOrder(IntermodulationMethod method) => method switch
    {
        IntermodulationMethod.Smpte => SmpteDefaultMaxOrder,
        IntermodulationMethod.Ccif => CcifDefaultMaxOrder,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    /// <summary>
    /// Splits a sine-equivalent level into the two tone levels. The pair's peak is
    /// <c>√2·(V1 + V2)</c>; making that equal to the peak of a sine at <paramref name="sineEquivalentDbv"/>
    /// with <c>V1 = r·V2</c> gives <c>V1 = r/(r+1)</c> and <c>V2 = 1/(r+1)</c> of the sine's RMS:
    /// 4:1 → −1.94 dB and −13.98 dB, 1:1 → −6.02 dB each. This is the tutorial's convention —
    /// each test is set up so that the two-tone peak equals the peak of the rated-power sine.
    /// </summary>
    public static (double LowDbv, double HighDbv) ToneLevelsDbv(double sineEquivalentDbv, double lowToHighRatio)
    {
        if (!(lowToHighRatio > 0.0) || !double.IsFinite(lowToHighRatio))
            throw new ArgumentOutOfRangeException(nameof(lowToHighRatio), lowToHighRatio, "The amplitude ratio must be positive.");

        double lowShare = lowToHighRatio / (lowToHighRatio + 1.0);
        double highShare = 1.0 / (lowToHighRatio + 1.0);
        return (sineEquivalentDbv + (20.0 * Math.Log10(lowShare)), sineEquivalentDbv + (20.0 * Math.Log10(highShare)));
    }

    /// <summary>The inverse of <see cref="ToneLevelsDbv"/>: the sine whose peak equals the pair's peak, <c>20·log10(V1 + V2)</c>.</summary>
    public static double SineEquivalentDbv(double lowDbv, double highDbv) =>
        20.0 * Math.Log10(Math.Pow(10.0, lowDbv / 20.0) + Math.Pow(10.0, highDbv / 20.0));
}
