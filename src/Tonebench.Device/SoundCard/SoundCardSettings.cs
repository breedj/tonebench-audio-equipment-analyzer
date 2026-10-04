namespace Tonebench.Device.SoundCard;

/// <summary>
/// How long a sound card is given to settle after a stream is opened.
/// </summary>
public static class SoundCardStreamSettle
{
    /// <summary>
    /// 2 s, measured rather than guessed. On a MOTU M4 over ASIO (2026-10-01), every stream opened
    /// at a new rate showed the same transient: the first records read THD 45–90 % with a spurious
    /// ~7577 Hz component, then dropped to under 0.1 % and settled at 0.0004 %. The <i>time</i> was
    /// consistent where the record count was not — 0.75 s at 48 kHz, 1.24 s at 96 kHz and 1.51 s at
    /// 192 kHz, i.e. 5 records at one rate and 23 at another — which is why this is expressed in
    /// milliseconds and converted per rate, the same lesson as the QA403's queue depth.
    /// </summary>
    public const double DefaultMs = 3000.0;

    /// <summary>Past this it is a typo rather than a card: ten seconds of discard is longer than most measurements.</summary>
    public const double MaxMs = 10_000.0;
}

/// <summary>
/// Which card the analyzer is, and how it is wired. The calibration that turns its samples into
/// volts is <see cref="SoundCardCalibration"/>, kept beside this rather than inside it because the
/// two are written by different things — this by the device picker, that by the two calibration
/// steps.
///
/// <para>Devices are named, never indexed. PortAudio's indices renumber whenever anything is
/// plugged in or a driver updates, and an index that silently resolves to a different card is a
/// calibration quietly applied to the wrong hardware.</para>
/// </summary>
public sealed record SoundCardSettings
{
    /// <summary>Nothing chosen yet — what a fresh install has.</summary>
    public static SoundCardSettings Empty { get; } = new();

    public string InputHostApi { get; init; } = string.Empty;

    public string InputDeviceName { get; init; } = string.Empty;

    public string OutputHostApi { get; init; } = string.Empty;

    public string OutputDeviceName { get; init; } = string.Empty;

    /// <summary>
    /// Index of the first of the two input channels the analyzer uses. ARTA's "Input channels 3/4"
    /// is offset 2.
    ///
    /// <para>On Windows outside ASIO a channel pair is usually a <i>device</i> rather than an
    /// offset — a MOTU M4 exposes <c>In 3-4</c> as its own WASAPI device — so this stays 0 there.
    /// Under ASIO the whole card is one device and the offset is the only way to reach the other
    /// pair.</para>
    /// </summary>
    public int InputChannelOffset { get; init; }

    /// <summary>Index of the first of the two output channels. See <see cref="InputChannelOffset"/>.</summary>
    public int OutputChannelOffset { get; init; }

    /// <summary>
    /// How long to throw away after a stream is opened, in milliseconds, before anything is
    /// analysed. See <c>SoundCardDevice.StreamStartSettleFrames</c> for what it is covering.
    /// </summary>
    public double StreamStartSettleMs { get; init; } = SoundCardStreamSettle.DefaultMs;

    /// <summary>
    /// The front-end gain in front of the left input, in dB — a 10:1 divider is −20. Stored here so
    /// a sound card's bench wiring is remembered independently of the QA403's; the session copies
    /// whichever is effective into the app's one external-gain setting, which applies it at the
    /// pipeline's door.
    /// </summary>
    public double FrontEndGainLeftDb { get; init; }

    /// <summary>The front-end gain in front of the right input, in dB.</summary>
    public double FrontEndGainRightDb { get; init; }

    /// <summary>Whether the bar shows the two gains in dB or as a plain ratio. Display only.</summary>
    public string GainDisplayUnit { get; init; } = "Db";

    /// <summary>
    /// The lowest frequency worth asking this card for, in Hz. Sound cards are AC-coupled at both
    /// ends, so below their own corner a sweep reports the card's high-pass as DUT response. The
    /// default is deliberately conservative; a DC-coupled front-end fixes only the input side,
    /// because the card's output coupling still limits the stimulus.
    /// </summary>
    public double MinStimulusFrequencyHz { get; init; } = 20.0;

    /// <summary>True once a card has been chosen on both sides.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(InputDeviceName) && !string.IsNullOrWhiteSpace(OutputDeviceName);
}
