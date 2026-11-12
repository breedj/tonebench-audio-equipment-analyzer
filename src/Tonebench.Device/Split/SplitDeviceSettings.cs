namespace Tonebench.Device.Split;

/// <summary>
/// How a composite analyzer is put together: the rate its generator runs at, and what to call each
/// half.
///
/// <para>The device-layer mirror of the <c>device.split</c> configuration document, kept in step by
/// hand for the reason <see cref="SoundCard.SoundCardSettings"/> is: <c>Tonebench.Device</c> does
/// not reference <c>Tonebench.Data</c>, so a device never reads a database.</para>
///
/// <para>Which two devices the halves <i>are</i> is deliberately not here. A
/// <see cref="SplitAnalyzerDevice"/> is handed two already-built <see cref="IAnalyzerDevice"/>s, so
/// it needs no notion of <c>DeviceKind</c> — that enum belongs to the App layer, and keeping it out
/// is what lets the composite be tested against two fakes.</para>
/// </summary>
public sealed record SplitDeviceSettings
{
    /// <summary>A one-rate split with placeholder labels — what a test wants and what a fresh document means.</summary>
    public static SplitDeviceSettings Default { get; } = new();

    /// <summary>
    /// The rate the generator side is streamed at, or <b>0</b> for "follow the analysis rate", which
    /// is what a fresh document holds and what makes a split behave exactly like a single device.
    ///
    /// <para>Independent rates are the point of a composite rather than a side effect of one. A DAC
    /// under test wants its own rate; ultrasonic residue wants a fast ADC and only a slow DAC; and
    /// the QA403's 384 kHz input mode is only a real measurement when the stimulus comes from
    /// something other than its own DAC, which at that rate emits garbage.</para>
    ///
    /// <para>It is validated where it is used, not here: a rate pair is refused when the generator
    /// side cannot play it, when it exceeds that side's
    /// <see cref="IAnalyzerDevice.MaxGeneratorSampleRateHz"/>, or when the two rates are not an
    /// integer ratio — see <c>DeviceSessionService.RequireGeneratorRateUsable</c>. A record that
    /// refused its own values could not be stored, and a stored figure a later hardware change
    /// invalidates has to be reportable rather than unloadable.</para>
    /// </summary>
    public double GeneratorSampleRateHz { get; init; }

    /// <summary>What to call the capturing half in a message or a log line, e.g. <c>QA403</c>.</summary>
    public string InputLabel { get; init; } = "input device";

    /// <summary>What to call the generating half, e.g. <c>Topping D50 III</c>.</summary>
    public string OutputLabel { get; init; } = "output device";
}
