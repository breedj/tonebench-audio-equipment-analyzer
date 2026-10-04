namespace Tonebench.Device.SoundCard;

/// <summary>
/// Where a sound card's full scale sits in volts, and the single point where its samples become
/// volts and volts become samples — the analogue of <see cref="Hardware.Qa40xCalibration"/>, and
/// bound by the same rule: calibration is applied exactly once, here, and <c>Tonebench.Dsp</c> never
/// sees a sample-domain number.
///
/// <para>There is no per-range page to read, because there is no range ladder: a sound card has one
/// fixed full scale on each side, and what the two calibration steps measure is where that full
/// scale sits. That is also what collapses <see cref="DeviceCapabilities.InputRangesDbv"/> and
/// <see cref="DeviceCapabilities.OutputRangesDbv"/> to one rung each, which is what lets
/// <c>InputRangeSelector</c> and <see cref="OutputRangeSelector"/> keep working with no special
/// case anywhere — given one rung to choose from, they return it.</para>
///
/// <para><b>The front-end is deliberately not in here.</b> Whatever divider or buffer sits between
/// the DUT and the card's input jack is described by the app's external input gain and undone at
/// the pipeline's door, exactly as it is for a QA403; this class is only ever about the analyzer
/// itself. Folding it in here instead would make <see cref="InputRangeDbv"/> a DUT-referred number,
/// and the three ranging seams that add the gain back would then add it twice.</para>
///
/// <para>Two steps rather than one, following ARTA, because they answer independent questions and
/// the second does not have to trust the first: the output full scale comes from a voltmeter on a
/// tone this app played, and the input full scale from a source of known voltage. A card whose
/// output is calibrated and whose input is not is a legitimate intermediate state, so the two
/// flags are separate.</para>
/// </summary>
public sealed record SoundCardCalibration
{
    /// <summary>
    /// What an uncalibrated card assumes its full scale is, in peak volts.
    ///
    /// <para>√2 V peak puts full scale at exactly <b>0 dBV</b>, so every number the app prints
    /// equals dBFS: a −20 dBFS tone reads −20 dBV and the single rung reads 0.0 dBV. 1.0 V peak
    /// would put full scale at −3.01 dBV and invite precisely the peak-versus-RMS confusion this
    /// codebase already fights about. Nothing here is a guess at a real card's voltage — the device
    /// reports itself uncalibrated, and the UI says the levels are dBFS rather than dBV.</para>
    /// </summary>
    public static readonly double UncalibratedFullScalePeakV = Math.Sqrt(2.0);

    /// <summary>An uncalibrated card: it streams, and every ratio it measures is already exact.</summary>
    public static SoundCardCalibration Uncalibrated { get; } = new();

    /// <summary>Peak volts at digital full scale on the output, or null before the output step has been run.</summary>
    public double? OutputFullScalePeakV { get; init; }

    /// <summary>Peak volts at digital full scale on the left input, or null before the input step has been run.</summary>
    public double? InputFullScalePeakVLeft { get; init; }

    /// <summary>Peak volts at digital full scale on the right input, or null before the input step has been run.</summary>
    public double? InputFullScalePeakVRight { get; init; }

    public bool IsOutputCalibrated => OutputFullScalePeakV is > 0.0;

    public bool IsInputCalibrated => InputFullScalePeakVLeft is > 0.0 && InputFullScalePeakVRight is > 0.0;

    /// <summary>Peak volts at full scale on the given input (0 = left), falling back to the dBFS-equivalent default.</summary>
    public double InputFullScalePeakV(int channel) =>
        (channel == 1 ? InputFullScalePeakVRight : InputFullScalePeakVLeft) ?? UncalibratedFullScalePeakV;

    /// <summary>Peak volts at full scale on the output, falling back to the dBFS-equivalent default.</summary>
    public double OutputFullScaleV => OutputFullScalePeakV ?? UncalibratedFullScalePeakV;

    /// <summary>
    /// A captured sample, in volts at the input jack. <paramref name="sample"/> is the card's
    /// float32 sample, nominally in [−1, +1].
    /// </summary>
    public double SampleToVolts(float sample, int channel) => sample * InputFullScalePeakV(channel);

    /// <summary>
    /// Volts to a sample the card can play, clipped at full scale. The clip is the analogue of
    /// <c>Qa40xCalibration.VoltsToRaw</c>'s, and it is what keeps
    /// <see cref="OutputRangeSelector.Clips"/> an honest warning rather than a decoration: a
    /// generator asked for more than full scale hard-clips, which is what a DAC does.
    /// </summary>
    public float VoltsToSample(double volts) =>
        (float)Math.Clamp(volts / OutputFullScaleV, -1.0, 1.0);

    /// <summary>
    /// The one input rung, in the RMS-referenced peak dBV this codebase uses everywhere
    /// (<c>20·log10(peak/√2)</c>). Read plainly, it is the level at which this card clips.
    ///
    /// <para>The <b>lower</b> of the two channels, because this is one number used to predict
    /// clipping and whichever channel clips first is the one that matters.</para>
    /// </summary>
    public double InputRangeDbv =>
        PeakVoltsToDbv(Math.Min(InputFullScalePeakV(0), InputFullScalePeakV(1)));

    /// <summary>The one output rung, same convention.</summary>
    public double OutputRangeDbv => PeakVoltsToDbv(OutputFullScaleV);

    /// <summary>
    /// Peak volts as RMS-referenced peak dBV. Mixing this convention with a true-peak dBV puts every
    /// ranging decision 3.01 dB out, which is why it is spelled once here.
    /// </summary>
    public static double PeakVoltsToDbv(double peakVolts) =>
        20.0 * Math.Log10(peakVolts / Math.Sqrt(2.0));

    /// <summary>
    /// The output step: a 400 Hz tone was played at <paramref name="playedDbFs"/> below full scale
    /// and read on a voltmeter as <paramref name="measuredVolts"/> peak. Full scale is therefore
    /// that reading scaled back up by the level it was played at.
    /// </summary>
    public static double OutputFullScaleFromMeasurement(double measuredPeakVolts, double playedDbFs)
    {
        RequirePositive(measuredPeakVolts, nameof(measuredPeakVolts));
        if (!double.IsFinite(playedDbFs) || playedDbFs > 0.0)
            throw new ArgumentOutOfRangeException(nameof(playedDbFs), playedDbFs, "The played level must be at or below full scale.");

        return measuredPeakVolts / Math.Pow(10.0, playedDbFs / 20.0);
    }

    /// <summary>
    /// The input step: a source of known voltage <paramref name="knownPeakVolts"/> was measured at
    /// <paramref name="measuredDbFs"/> below the card's full scale, so full scale is that source
    /// scaled back up.
    /// </summary>
    public static double InputFullScaleFromMeasurement(double knownPeakVolts, double measuredDbFs)
    {
        RequirePositive(knownPeakVolts, nameof(knownPeakVolts));
        if (!double.IsFinite(measuredDbFs) || measuredDbFs > 0.0)
            throw new ArgumentOutOfRangeException(nameof(measuredDbFs), measuredDbFs, "A level measured against full scale cannot exceed it.");

        return knownPeakVolts / Math.Pow(10.0, measuredDbFs / 20.0);
    }

    /// <summary>
    /// How far apart the two inputs' full scales are, in dB, or null while the input is
    /// uncalibrated. ARTA shows this beside the two readings, and it earns its place: a figure of a
    /// few tenths is two ordinary channels, while several dB is a wiring or channel-pair mistake
    /// that every later measurement would otherwise inherit.
    /// </summary>
    public double? InputChannelDifferenceDb =>
        IsInputCalibrated
            ? 20.0 * Math.Log10(InputFullScalePeakVRight!.Value / InputFullScalePeakVLeft!.Value)
            : null;

    private static void RequirePositive(double volts, string name)
    {
        if (!double.IsFinite(volts) || volts <= 0.0)
            throw new ArgumentOutOfRangeException(name, volts, "A measured voltage must be positive.");
    }
}

/// <summary>How a voltage was entered — ARTA offers both, and a card calibrated against the wrong one is 3.01 dB out.</summary>
public enum VoltageEntryUnit
{
    /// <summary>What a multimeter on AC volts reads.</summary>
    Rms,

    /// <summary>What a scope cursor on the peak reads.</summary>
    Peak,
}

public static class VoltageEntry
{
    /// <summary>The entered value as peak volts, whichever way it was measured. A sine is assumed — the calibration tone is one.</summary>
    public static double ToPeakVolts(double value, VoltageEntryUnit unit) =>
        unit == VoltageEntryUnit.Rms ? value * Math.Sqrt(2.0) : value;

    /// <summary>Millivolts as peak volts, which is the unit both calibration panels are entered in.</summary>
    public static double MillivoltsToPeakVolts(double millivolts, VoltageEntryUnit unit) =>
        ToPeakVolts(millivolts / 1000.0, unit);
}
