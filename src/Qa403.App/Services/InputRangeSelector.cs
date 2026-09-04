namespace Qa403.App.Services;

/// <summary>
/// Picks an input range for a level a measurement already knows about, rather than waiting for the
/// device's own ranger to discover it.
///
/// <para>Same ladder and same headroom as <c>Qa40xAutoRanger</c> and the browser's
/// <c>attenuation.ts</c>, which is what lets the three disagree only about <i>when</i> a range
/// changes, never about <i>which</i> one is right. All three descend from the previous WinForms
/// app's <c>QaLibrary.DetermineAttenuation</c> ("add 5 dBV extra for a better THD measurement").</para>
///
/// <para><b>The level is an RMS-referenced peak</b> — <c>20·log10(peak_volts / √2)</c>, the same
/// quantity <c>SpectrumMeters.PeakDbv</c> and the device ranger's own <c>ScanPeakDbv</c> produce.
/// The previous app fed its ladder a raw peak instead, which is 3.01 dB higher for the same
/// waveform. Mixing the two conventions would put this selector a whole range step away from the
/// hardware ranger and have them fight each other, so the app's convention wins over the reference's.
/// </para>
/// </summary>
public static class InputRangeSelector
{
    /// <summary>
    /// Headroom, in dB, kept between the expected peak and the chosen range's nominal full scale.
    /// Same 5 dB as <c>Qa40xAutoRanger.HeadroomDb</c>; they must not drift apart.
    /// </summary>
    public const double HeadroomDb = 5.0;

    /// <summary>
    /// The lowest (most sensitive) range that still leaves <see cref="HeadroomDb"/> above
    /// <paramref name="expectedPeakDbv"/>, or the largest range offered when nothing fits — a level
    /// past the top of the ladder is better measured clipped on the widest range than on the most
    /// sensitive one.
    /// </summary>
    public static double For(IReadOnlyList<double> rangesDbv, double expectedPeakDbv)
    {
        if (rangesDbv is null || rangesDbv.Count == 0)
            throw new ArgumentException("At least one input range is required.", nameof(rangesDbv));

        // A level that is NaN (no signal found at all) must not select the most sensitive range and
        // clip the moment something arrives; the widest is the safe answer.
        if (double.IsNaN(expectedPeakDbv))
            return rangesDbv.Max();

        double wanted = expectedPeakDbv + HeadroomDb;
        return rangesDbv.Where(r => r >= wanted).DefaultIfEmpty(rangesDbv.Max()).Min();
    }
}
