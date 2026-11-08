namespace Tonebench.Device.SoundCard;

/// <summary>How confident we are that an input and an output device are the same physical interface.</summary>
public enum SoundCardPairing
{
    /// <summary>One device carrying both directions. Nothing to check — this is what ASIO gives.</summary>
    FullDuplexDevice,

    /// <summary>Two devices whose names name the same interface, e.g. <c>In 1-2 (MOTU M Series)</c> and <c>Out 1-2 (MOTU M Series)</c>.</summary>
    NamesMatch,

    /// <summary>
    /// Two devices we cannot tie together. Warned about, never refused: WDM-KS names carry no
    /// interface at all (<c>In 1-2 (In 1-2)</c> against <c>Out 1-2 (Out 1-2)</c>), so blocking on
    /// names would make that host API unusable on hardware where it is the best choice.
    /// </summary>
    Unknown,
}

/// <summary>Whether a measurement taken through a host API can be believed.</summary>
public enum SoundCardHostApiTrust
{
    /// <summary>Unmixed and at the rate asked for.</summary>
    Trusted,

    /// <summary>Trustworthy only in exclusive mode, which this app always requests.</summary>
    RequiresExclusive,

    /// <summary>Resamples in software and accepts any rate, so the rate reported is not the rate the converter ran at.</summary>
    Resamples,
}

/// <summary>
/// Turns PortAudio's device table into the choices the sound-card analyzer offers, and holds the
/// three rules that came out of measuring a real machine (MOTU M4, 2026-09-30).
///
/// <para><b>1. There is no full-duplex device on Windows outside ASIO.</b> Of 57 devices
/// enumerated, not one had both inputs and outputs: the M4 appears as separate
/// <c>In 1-2 (MOTU M Series)</c> and <c>Out 1-2 (MOTU M Series)</c> on MME, DirectSound, WASAPI and
/// WDM-KS alike. So a pairing is an input device <i>plus</i> an output device, and the
/// "one device, full duplex only" rule survives as what it was actually protecting: one physical
/// interface, therefore one clock. A genuine duplex device is still preferred where one exists.</para>
///
/// <para><b>2. A channel pair is usually a device, not an offset.</b> The M4 exposes
/// <c>In 3-4</c> as its own device rather than as channels 3–4 of one. The offset path still
/// exists for a device that really does carry more than two channels — ASIO, and some WDM-KS
/// endpoints — which is why <see cref="SupportedRatesHz"/> takes channel counts rather than
/// assuming two.</para>
///
/// <para><b>3. Rates must be probed, per mode.</b> On WASAPI the answer differs between shared and
/// exclusive: measured on the M4, shared offered only 48 kHz (the Windows mixer's rate, with 44.1,
/// 96 and 192 kHz all refused) while exclusive opened all four at their true rate. Nothing here
/// trusts <see cref="SoundCardDeviceDescriptor.DefaultSampleRateHz"/>.</para>
/// </summary>
internal sealed class SoundCardEnumerator(IPortAudio portAudio)
{
    /// <summary>
    /// The rates the rest of the app is built around. Deliberately the intersection rather than
    /// everything a card offers: every FFT-size table, chirp period and panel default is written
    /// against these three, and a card that cannot do 192 kHz simply does not offer it.
    /// </summary>
    public static readonly double[] CandidateRatesHz = [48_000.0, 96_000.0, 192_000.0];

    public IReadOnlyList<SoundCardHostApi> HostApis => portAudio.HostApis;

    public IReadOnlyList<SoundCardDeviceDescriptor> Devices => portAudio.Devices;

    public void Refresh() => portAudio.Refresh();

    public IEnumerable<SoundCardDeviceDescriptor> InputDevices => portAudio.Devices.Where(d => d.HasInput);

    public IEnumerable<SoundCardDeviceDescriptor> OutputDevices => portAudio.Devices.Where(d => d.HasOutput);

    /// <summary>
    /// Every host API the native library carries, each with what it found, ordered as the picker
    /// would offer them.
    ///
    /// <para><b>Including the ones that found nothing</b>, which is the whole point — see
    /// <see cref="SoundCardHostApiOption"/>. The counts are taken over the device table rather than
    /// from PortAudio's own per-API <c>deviceCount</c>: the two agree, and counting here is what
    /// also yields the per-direction tallies that say whether anything under an API is usable.</para>
    /// </summary>
    public IReadOnlyList<SoundCardHostApiOption> HostApiOptions()
    {
        var options = new List<SoundCardHostApiOption>(portAudio.HostApis.Count);
        foreach (SoundCardHostApi api in portAudio.HostApis)
        {
            int count = 0;
            int inputs = 0;
            int outputs = 0;
            foreach (SoundCardDeviceDescriptor d in portAudio.Devices)
            {
                if (d.HostApiIndex != api.Index)
                    continue;

                count++;
                if (d.HasInput)
                    inputs++;
                if (d.HasOutput)
                    outputs++;
            }

            options.Add(new SoundCardHostApiOption(
                Name: api.Name,
                Kind: api.Kind.ToString(),
                DeviceCount: count,
                InputCount: inputs,
                OutputCount: outputs,
                Trust: Trust(api.Kind).ToString(),
                Preference: PreferenceOf(api.Kind)));
        }

        return [.. options
            .OrderBy(o => o.Preference)
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Which of <see cref="CandidateRatesHz"/> this pair actually accepts, probed one at a time.
    /// </summary>
    public IReadOnlyList<double> SupportedRatesHz(
        SoundCardDeviceDescriptor input,
        SoundCardDeviceDescriptor output,
        bool exclusive,
        int inputChannelCount = 2,
        int outputChannelCount = 2)
    {
        var supported = new List<double>(CandidateRatesHz.Length);
        foreach (double rate in CandidateRatesHz)
        {
            var request = new SoundCardStreamRequest(
                input.Index, inputChannelCount, output.Index, outputChannelCount, rate, exclusive);
            if (portAudio.IsFormatSupported(request))
                supported.Add(rate);
        }

        return supported;
    }

    /// <summary>
    /// Finds a device by host-API name and device name rather than by index. PortAudio's indices
    /// renumber whenever anything is plugged in or a driver updates, and an index that silently
    /// resolves to a different card is a calibration quietly applied to the wrong hardware.
    /// </summary>
    public SoundCardDeviceDescriptor? Find(string hostApiName, string deviceName) =>
        portAudio.Devices.FirstOrDefault(d =>
            string.Equals(d.HostApiName, hostApiName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>How a channel pair is written in the UI: an offset of 2 is "3/4".</summary>
    public static string PairLabel(int channelOffset) => $"{channelOffset + 1}/{channelOffset + 2}";

    /// <summary>
    /// Why a channel pair cannot be used on these two devices, or null when it can.
    ///
    /// <para>It names the pair, the count it needs and the count the device has, because the
    /// control that would fix it is usually <b>not on screen</b>: the dialog shows a channel-pair
    /// dropdown only where there is more than one pair to choose, so a stale offset carried onto a
    /// two-channel card produces an error about channels beside no channel control at all. The
    /// message it replaced — "That channel pair is past what the card has." — named neither the
    /// pair nor either count, and read as the card being at fault.</para>
    ///
    /// <para>Both sides are reported when both are short, rather than the first one found: a pair
    /// is chosen per direction, and fixing the input only to be told about the output is two trips
    /// through the same dead end.</para>
    /// </summary>
    public static string? ChannelPairProblem(
        SoundCardDeviceDescriptor input, int inputChannelOffset,
        SoundCardDeviceDescriptor output, int outputChannelOffset)
    {
        string? inputProblem = input.MaxInputChannels < inputChannelOffset + 2
            ? $"channels {PairLabel(inputChannelOffset)} need {inputChannelOffset + 2} input channels and '{input.Name}' has {input.MaxInputChannels}"
            : null;

        string? outputProblem = output.MaxOutputChannels < outputChannelOffset + 2
            ? $"channels {PairLabel(outputChannelOffset)} need {outputChannelOffset + 2} output channels and '{output.Name}' has {output.MaxOutputChannels}"
            : null;

        return (inputProblem, outputProblem) switch
        {
            (null, null) => null,
            (not null, null) => Capitalise($"{inputProblem}."),
            (null, not null) => Capitalise($"{outputProblem}."),
            _ => Capitalise($"{inputProblem}, and {outputProblem}."),
        };
    }

    private static string Capitalise(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Whether the two devices look like one physical interface. Advisory — see <see cref="SoundCardPairing.Unknown"/>.</summary>
    public static SoundCardPairing Pairing(SoundCardDeviceDescriptor input, SoundCardDeviceDescriptor output)
    {
        if (input.Index == output.Index && input.IsFullDuplex)
            return SoundCardPairing.FullDuplexDevice;

        if (input.HostApiIndex != output.HostApiIndex)
            return SoundCardPairing.Unknown;

        string inStem = InterfaceStem(input.Name);
        string outStem = InterfaceStem(output.Name);

        return inStem.Length > 0 && string.Equals(inStem, outStem, StringComparison.OrdinalIgnoreCase)
            ? SoundCardPairing.NamesMatch
            : SoundCardPairing.Unknown;
    }

    /// <summary>
    /// The interface name PortAudio buries in a device name: the text between the <b>first</b>
    /// <c>(</c> and the <b>last</b> <c>)</c>, which is where every Windows host API puts it —
    /// <c>In 1-2 (MOTU M Series)</c>. Empty when there is none, or when what is there merely
    /// repeats the direction, which is the WDM-KS case (<c>In 1-2 (In 1-2)</c>) and the whole
    /// reason pairing is advisory.
    ///
    /// <para>First-to-last rather than the innermost pair, because real device names nest:
    /// <c>Speaker (Realtek(R) Audio)</c> and <c>Microfoonmatrix (Intel® Smart Sound Technologie
    /// voor digitale microfoons)</c> are both on this bench. Taking the last <c>(</c> reads the
    /// trademark as the interface and pairs a Realtek against anything else carrying an (R).</para>
    /// </summary>
    public static string InterfaceStem(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return string.Empty;

        int close = deviceName.LastIndexOf(')');
        int open = close > 0 ? deviceName.IndexOf('(') : -1;
        if (open < 0 || open > close)
            return string.Empty;

        string inside = deviceName[(open + 1)..close].Trim();
        string outside = deviceName[..open].Trim();

        // "In 1-2 (In 1-2)" names no interface, it repeats the endpoint. Treated as unknown rather
        // than as a stem, or every WDM-KS input would "match" only itself and nothing else.
        return string.Equals(inside, outside, StringComparison.OrdinalIgnoreCase) ? string.Empty : inside;
    }

    /// <summary>
    /// Whether a measurement through this host API can be believed. DirectSound and MME resample in
    /// software and will accept any rate — measured, both "supported" 48, 96 and 192 kHz on a card
    /// whose WASAPI shared mode admitted to only 48.
    ///
    /// <para><b>Core Audio is <see cref="SoundCardHostApiTrust.Resamples"/> and that is not a
    /// placeholder.</b> PortAudio's macOS backend defaults to <c>paMacCorePlayNice</c> (0x00),
    /// whose own header comment reads "do as much sample rate conversion as possible and as little
    /// mucking with the device as possible" — so with no host-API-specific stream info supplied it
    /// inserts an <c>AudioConverter</c> rather than setting the device's nominal rate, which is
    /// behaviourally what DirectSound and MME do. The app asks for
    /// <c>SoundCardStreamRequest.Exclusive</c> unconditionally, but that is materialised only for
    /// WASAPI (<c>PortAudioNative.BuildParameters</c>), so on Core Audio it currently buys nothing
    /// and <see cref="SoundCardHostApiTrust.RequiresExclusive"/> would be a claim this code does
    /// not honour.</para>
    ///
    /// <para>The fix, for whoever can run it: build a <c>paMacCoreStreamInfo</c> on the
    /// <c>Exclusive</c> path — <c>{ unsigned long size; PaHostApiTypeId hostApiType; unsigned long
    /// version; unsigned long flags; }</c>, version <c>0x01</c>, filled by hand (there is no
    /// setup helper) — with <c>paMacCore_ChangeDeviceParameters</c> (0x01) and
    /// <c>paMacCore_FailIfConversionRequired</c> (0x02), the second being documented as meaningful
    /// only in combination with the first. Then this becomes
    /// <see cref="SoundCardHostApiTrust.RequiresExclusive"/> honestly. It is deliberately not
    /// written here: those figures come from a 2006-era third-party copy of <c>pa_mac_core.h</c>
    /// rather than from the header of the library the machine will actually load, and every
    /// <c>unsigned long</c> there is 8 bytes on LP64 where the WASAPI struct's equivalents are 4 —
    /// the trap the "---- scalars ----" note in <c>PortAudioInterop</c> exists for. Nobody here has
    /// a Mac to check it against, and the practical answer meanwhile is to set the card's rate in
    /// Audio MIDI Setup to the rate being measured, which is what the dialog's warning says.</para>
    /// </summary>
    public static SoundCardHostApiTrust Trust(SoundCardHostApiKind kind) => kind switch
    {
        SoundCardHostApiKind.Asio or SoundCardHostApiKind.WdmKs
            or SoundCardHostApiKind.Alsa or SoundCardHostApiKind.Jack => SoundCardHostApiTrust.Trusted,
        SoundCardHostApiKind.Wasapi => SoundCardHostApiTrust.RequiresExclusive,
        SoundCardHostApiKind.DirectSound or SoundCardHostApiKind.Mme => SoundCardHostApiTrust.Resamples,
        _ => SoundCardHostApiTrust.Resamples,
    };

    /// <summary>
    /// The order the picker offers host APIs in: the ones that reach the hardware first, the ones
    /// that resample last. ASIO leads because it is also the only one that presents a card as a
    /// single full-duplex device.
    ///
    /// <para>Core Audio shares rank 0 with ASIO rather than being slotted in beside it, and the
    /// tie is unreachable: ASIO exists only on Windows and Core Audio only on macOS, so no device
    /// table can ever hold both. Giving it a rank of its own would mean choosing, with nothing to
    /// choose on.</para>
    /// </summary>
    public static int PreferenceOf(SoundCardHostApiKind kind) => kind switch
    {
        SoundCardHostApiKind.Asio or SoundCardHostApiKind.CoreAudio => 0,
        SoundCardHostApiKind.Jack => 1,
        SoundCardHostApiKind.Alsa => 2,
        SoundCardHostApiKind.WdmKs => 3,
        SoundCardHostApiKind.Wasapi => 4,
        SoundCardHostApiKind.DirectSound => 5,
        SoundCardHostApiKind.Mme => 6,
        _ => 7,
    };
}
