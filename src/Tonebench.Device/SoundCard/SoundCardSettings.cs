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
    ///
    /// <para>It shipped as 3000 while this remark said 2 s, and the 2 s is the figure the
    /// measurements support: the worst transient seen was 1.51 s, so 2000 covers it with a third
    /// again on top. The extra second was not free — it is spent on the first stream of every
    /// reopen, which is what <see cref="SoundCardConfig"/>'s calibration steps do between presses
    /// — and <c>ConfigMigrations/004</c> carries an existing bench over, but only from exactly
    /// 3000, so a figure anyone typed is left alone.</para>
    /// </summary>
    public const double DefaultMs = 2000.0;

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

    /// <summary>
    /// PortAudio's own name for the ASIO host API, which is what a stored card document holds.
    ///
    /// <para>A literal rather than a lookup because a document names its host API in text and is
    /// read long before anything enumerates: the factory decides whether a two-card bench is
    /// buildable while constructing it, where a native call is exactly what must not happen.</para>
    /// </summary>
    public const string AsioHostApiName = "ASIO";

    /// <summary>Whether a stored host API name is ASIO's.</summary>
    public static bool IsAsio(string? hostApiName) =>
        string.Equals(hostApiName, AsioHostApiName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why these two stored card selections cannot be the two halves of a split, or null.
    ///
    /// <para><b>The sibling of <c>SoundCardEnumerator.DuplexCombinationProblem</c>, and here rather
    /// than beside it for a reason that is not tidiness.</b> That one judges two live
    /// <c>SoundCardDeviceDescriptor</c>s — enumerated hardware — and the enumerator is internal to
    /// this assembly. This one judges two <i>stored</i> selections, which is a question about a
    /// document rather than about hardware, and it is asked from the App layer while a composite is
    /// being constructed: before anything has enumerated, and where a native call is exactly what
    /// must not happen. A settings record is where a rule about what a document can express
    /// belongs.</para>
    ///
    /// <para>The two refusals share a predicate and nothing else. There, one duplex stream cannot
    /// reach across two ASIO drivers. Here there are two streams and the limit is harder: <b>the
    /// ASIO SDK has no notion of more than one driver open per process at all</b>, and PortAudio's
    /// backend carries that as a singleton whose callbacks take no context pointer, so a callback
    /// could not say which driver called it. No threading arrangement reaches it — which is why a
    /// split gives each card a transfer thread of its own and still cannot have two ASIO
    /// cards.</para>
    ///
    /// <para>So the advice differs too, and that is the point of the separate message: the duplex
    /// one says move <i>both</i> cards to one other host API, where a split only needs <i>one</i>
    /// of them moved and the better converter keeps ASIO.</para>
    /// </summary>
    public static string? SplitCombinationProblem(
        string? inputHostApi, string inputDeviceName, string? outputHostApi, string outputDeviceName)
    {
        if (!IsAsio(inputHostApi) || !IsAsio(outputHostApi))
            return null;

        // One ASIO card used for both halves is one interface and one clock, which is a Sound card
        // bench rather than a split -- and two streams on one driver is refused just the same.
        if (string.Equals(inputDeviceName, outputDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{inputDeviceName}' is one interface, so it is a sound card rather than a split: "
                + "choose Sound card, which opens both of its directions in one stream.";
        }

        return $"A split cannot have two ASIO cards: ASIO allows one driver open per process, so "
            + $"'{inputDeviceName}' and '{outputDeviceName}' cannot both be opened whatever the "
            + "sample rate. Move one of them to WASAPI — which this analyzer opens in exclusive mode "
            + "and trusts — and the other keeps ASIO.";
    }


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

    /// <summary>True once a capture device has been chosen.</summary>
    public bool HasInput => !string.IsNullOrWhiteSpace(InputDeviceName);

    /// <summary>True once a playback device has been chosen.</summary>
    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputDeviceName);

    /// <summary>
    /// True once a card has been chosen on both sides — i.e. this bench can both generate and
    /// measure. Still the test every full measurement wants; it is no longer the test for whether
    /// a stream can be opened at all, which is <see cref="IsUsable"/>.
    /// </summary>
    public bool IsConfigured => HasInput && HasOutput;

    /// <summary>
    /// True once <i>either</i> direction has been chosen. A capture-only ADC (an E1DA Cosmos has
    /// only a USB-C out) and a playback-only USB DAC each satisfy this and not
    /// <see cref="IsConfigured"/>, and before 2026-11-11 both were refused at connect — the Cosmos
    /// case being the one <c>collapseToBestApi</c>'s own comment predicted, "would collapse the
    /// card into an unusable entry".
    /// </summary>
    public bool IsUsable => HasInput || HasOutput;

    /// <summary>
    /// Which directions the chosen devices can carry. Derived rather than stored: the two device
    /// names are what the user picked, and a stored direction could contradict them.
    /// </summary>
    public SoundCardStreamDirection Direction =>
        HasInput && HasOutput ? SoundCardStreamDirection.Duplex
        : HasInput ? SoundCardStreamDirection.InputOnly
        : SoundCardStreamDirection.OutputOnly;

    /// <summary>
    /// This bench with one direction masked off, so <see cref="Direction"/> derives the one that is
    /// left. What a composite analyzer needs of a card that is only half of it.
    ///
    /// <para><b>It blanks the two device-name fields rather than storing a direction</b>, because
    /// <see cref="Direction"/> is derived on purpose -- "the two device names are what the user
    /// picked, and a stored direction could contradict them". Masking keeps that invariant: a
    /// settings record still says exactly what it will open.</para>
    ///
    /// <para>Without it a fully-configured duplex card used as one half of a split would open
    /// <i>duplex</i> and claim an input the split is not using -- which on a card that is the split's
    /// generator means a capture nobody reads, and under WASAPI exclusive means an input endpoint
    /// taken away from whatever else wanted it.</para>
    ///
    /// <para>The host API, the channel offsets, the settle and the two front-end gains are all left
    /// alone: the masked direction's fields are inert, and a figure the user typed should survive
    /// being looked at.</para>
    /// </summary>
    public SoundCardSettings ForDirection(SoundCardStreamDirection direction) => direction switch
    {
        SoundCardStreamDirection.InputOnly => this with { OutputHostApi = string.Empty, OutputDeviceName = string.Empty },
        SoundCardStreamDirection.OutputOnly => this with { InputHostApi = string.Empty, InputDeviceName = string.Empty },
        _ => this,
    };
}
