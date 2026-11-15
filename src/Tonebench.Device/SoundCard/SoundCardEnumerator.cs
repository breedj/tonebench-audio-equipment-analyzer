namespace Tonebench.Device.SoundCard;

/// <summary>How confident we are that an input and an output device are the same physical interface.</summary>
public enum SoundCardPairing
{
    /// <summary>One device carrying both directions. Nothing to check — this is what ASIO gives.</summary>
    FullDuplexDevice,

    /// <summary>Two devices whose names name the same interface, e.g. <c>In 1-2 (MOTU M Series)</c> and <c>Out 1-2 (MOTU M Series)</c>.</summary>
    NamesMatch,

    /// <summary>
    /// Two devices whose names reveal <b>different</b> interfaces, e.g.
    /// <c>In 1-2 (MOTU M Series)</c> against <c>Luidsprekers (TOPPING USB DAC)</c>: two physical
    /// cards, and therefore two clocks.
    ///
    /// <para><b>This is the one verdict here that is refused rather than warned about, and it
    /// exists because <see cref="Unknown"/> cannot be.</b> A sound-card bench means one interface,
    /// so a bench naming two has to be turned away — but <c>Unknown</c> is the answer for every
    /// WDM-KS pairing, correct ones included, so refusing that would make a whole host API
    /// unusable. The two cases had to be told apart before either could be acted on: this one is
    /// <i>positive</i> evidence of two interfaces, where <c>Unknown</c> is the absence of
    /// evidence either way.</para>
    ///
    /// <para>A bench that really has two cards is not refused the measurement, only this spelling
    /// of it: two cards are what <c>DeviceKind.Split</c> is for, and it opens a stream per card so
    /// the two clocks never share a ring.</para>
    /// </summary>
    DifferentInterfaces,

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
    /// The rates a card is asked about, one at a time, in exclusive mode.
    ///
    /// <para><b>This is the app's list, not the card's</b>, and that distinction is worth keeping in
    /// view: a rate absent from a pairing's probe result was refused by the driver, but a rate absent
    /// from <i>here</i> was never asked for. It stopped at 192 kHz until 2026-11-13, which is how a
    /// Topping D50 III — a DAC that goes to 768 — came to report "48, 96 and 192 kHz" as though that
    /// were its own limit.</para>
    ///
    /// <para><b>Every rate is a power-of-two multiple of 48 kHz or of 44.1 kHz, and those two
    /// families do not mix.</b> A split runs its two halves at independent rates, and a stimulus
    /// carrying its own period needs that period to be a whole number of frames at both ends
    /// (<c>DeviceSessionService.IsPeriodicRatePair</c>). Within one family every pairing is a
    /// power-of-two ratio and so always is. Across them it provably never is: a record of N frames
    /// at 192 kHz needs N × 147/640 to be an integer, and 640 = 2⁷ × 5 while 147 = 3 × 7² carries no
    /// factor 5, so no power-of-two N ever is — and radix-2 forbids the only N that would. So a
    /// mixed-family pairing is permanently tone-only, which <c>PlaysAPeriodicStimulus</c> refuses
    /// per stimulus rather than per bench: tones, IMD, the multitone comb, square and triangle waves
    /// and every stepped-sine sweep are unaffected, and only the chirp and periodic noise are not.
    /// A third family would be a third set of ratios nothing here has reasoned about, which
    /// <c>EveryCandidateRate_BelongsToOneOfTheTwoFamilies</c> is there to force a decision on.</para>
    ///
    /// <para><b>Why 44.1 kHz is here at all</b>, given that cost (added 2026-11-14): it is what
    /// nearly all recorded music is, so it is the rate a DAC most needs measuring at, and the
    /// measurements a converter is actually judged by — THD+N and IMD at a level, linearity,
    /// dynamic range — are tone measurements that a mixed pairing runs unchanged. Frequency response
    /// moves to the stepped sine. Capturing a 44.1 kHz DAC at 192 kHz is also the better measurement
    /// than matching its rate would be: it is the only way to see the reconstruction images and
    /// shaped noise above 22 kHz, which is the half of a 44.1 kHz converter worth looking at. The
    /// dead band that creates is the point rather than a cost — <c>SplitDeviceDialog</c>'s own rate
    /// note already says so.</para>
    ///
    /// <para><b>What raising the ceiling did and did not cost.</b> Nothing downstream is sized in
    /// frames: <c>SoundCardDevice.TargetBufferedSeconds</c> asks PortAudio for its rings as a time,
    /// the DAC prime is a time, the re-clock settle is a time converted per rate, and the analysis
    /// hop is a fraction of a record. What does get tighter is the transfer loop — a 1024-frame
    /// transfer is 1.33 ms at 768 kHz against 5.3 ms at 192 — which is the same figure the QA403's
    /// 512-frame chunks already run at 384 kHz, so it is a precedent rather than a new risk.</para>
    ///
    /// <para><b>What it does not reach, and why that is the 44.1 kHz family's whole story.</b> The
    /// response screens — chirp, impulse response, impedance — derive their rate from the chosen
    /// <i>span</i> (<c>FrequencyResponseRequest.SampleRateHz</c> is the span doubled) and their
    /// transform length from that rate over <c>BaseBinHz</c>, which is 48000/32768. That is exactly
    /// what makes 48, 96 and 192 kHz land on 32768, 65536 and 131072 — and the 44.1 kHz family land
    /// on <b>30106, 60211 and 120422</b>, none of them a power of two, which
    /// <c>FftBackendValidation</c> throws on. So <c>SupportedSpansHz</c> has no 44.1 kHz entry and
    /// is not going to get one: a span there would fail a run with "Input length must be a power of
    /// two", naming nothing anybody chose. <b>Nothing needs one</b> — 44.1 kHz is what a DAC under
    /// test <i>plays</i>, and such a DAC is the generating half of a split captured at 192 kHz,
    /// which is the better measurement anyway. The <b>FFT screen</b> is unaffected throughout: it
    /// takes its transform length from the request and validates it as a power of two
    /// (<c>AcquisitionEndpoints</c>), with no reference to the rate at all.</para>
    ///
    /// <para>A card offering 768 kHz likewise widens the FFT screen's rate dropdown and not the
    /// chirp's span choices. <c>ImpedanceRequest.MaxCalibrationSpanHz</c> — which is what a stored
    /// calibration standard is captured at, chosen so one standard covers every measurement — is
    /// filled per analyzer from <c>DeviceSessionService.WidestAnalysisSpanHz</c> rather than being
    /// the flat 96 kHz it was until 2026-11-14, and that property intersects with the span list for
    /// this same reason.</para>
    /// </summary>
    public static readonly double[] CandidateRatesHz =
        [44_100.0, 48_000.0, 88_200.0, 96_000.0, 176_400.0, 192_000.0, 384_000.0, 768_000.0];

    /// <summary>
    /// The two rate families, each a base and its power-of-two multiples. Read by
    /// <see cref="FamilyBaseHz"/> and by the audit over <see cref="CandidateRatesHz"/>; the order
    /// is the order a mixed pairing is explained in, 48 kHz first because it is what every bench
    /// here measured at before 2026-11-14.
    /// </summary>
    public static readonly double[] RateFamilyBasesHz = [48_000.0, 44_100.0];

    /// <summary>
    /// Which family a rate belongs to, or null for a rate in neither — i.e. one no pairing rule
    /// here has reasoned about.
    ///
    /// <para>Two rates share a family exactly when a periodic stimulus survives between them, so
    /// this is the cheap form of the integer-ratio question for a caller that has a rate rather
    /// than a pair. Tolerance is absolute and tiny because every rate involved is an exact integer
    /// number of hertz.</para>
    /// </summary>
    public static double? FamilyBaseHz(double rateHz)
    {
        foreach (double baseHz in RateFamilyBasesHz)
        {
            double multiple = rateHz / baseHz;
            double rounded = Math.Round(multiple);

            // A power of two, and the same one back: 2^k for k >= 0, so 1, 2, 4, 8, ...
            if (rounded >= 1.0
                && Math.Abs(multiple - rounded) < 1e-9
                && ((long)rounded & ((long)rounded - 1)) == 0)
            {
                return baseHz;
            }
        }

        return null;
    }

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
        SoundCardDeviceDescriptor? input,
        SoundCardDeviceDescriptor? output,
        bool exclusive,
        int inputChannelCount = 2,
        int outputChannelCount = 2)
    {
        // A null side means "this stream does not carry that direction", so the rates probed are
        // that one device's. Both null is a caller that has not chosen anything, and answering
        // with the full candidate list would report a bench that cannot stream as able to stream
        // at every rate.
        if (input is null && output is null)
            throw new ArgumentException("A rate probe needs at least one device.", nameof(input));

        var direction = input is not null && output is not null ? SoundCardStreamDirection.Duplex
            : input is not null ? SoundCardStreamDirection.InputOnly
            : SoundCardStreamDirection.OutputOnly;

        var supported = new List<double>(CandidateRatesHz.Length);
        foreach (double rate in CandidateRatesHz)
        {
            // The absent side's index and channel count are never read -- AllocParameters returns
            // a null parameter block for it -- so the placeholders below reach no native call.
            var request = new SoundCardStreamRequest(
                input?.Index ?? 0, inputChannelCount,
                output?.Index ?? 0, outputChannelCount,
                rate, exclusive)
            {
                Direction = direction,
            };

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
        SoundCardDeviceDescriptor? input, int inputChannelOffset,
        SoundCardDeviceDescriptor? output, int outputChannelOffset)
    {
        // A null side is a direction this bench does not carry, so it has no channels to fall
        // short -- which is not the same as a device that has none, and that would be a real
        // complaint.
        string? inputProblem = input is not null && input.MaxInputChannels < inputChannelOffset + 2
            ? $"channels {PairLabel(inputChannelOffset)} need {inputChannelOffset + 2} input channels and '{input.Name}' has {input.MaxInputChannels}"
            : null;

        string? outputProblem = output is not null && output.MaxOutputChannels < outputChannelOffset + 2
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

    /// <summary>
    /// Why these two devices cannot form a duplex stream at all, or null when they can — asked
    /// <b>before</b> the rate probe, because a combination PortAudio refuses outright refuses every
    /// rate, and <see cref="SupportedRatesHz"/> reports that as an empty list indistinguishable
    /// from a card that genuinely does not do 48, 96 or 192 kHz.
    ///
    /// <para><b>ASIO is one host API with many drivers, and a duplex stream belongs to one
    /// driver.</b> A driver owns the whole interface and its callback, so PortAudio's ASIO backend
    /// returns <c>paBadIODeviceCombination</c> for any pair whose devices differ — there is nothing
    /// to negotiate and no rate at which it would work. Every other Windows host API presents each
    /// endpoint separately and can combine two of them.</para>
    ///
    /// <para>Reported from the bench 2026-11-12: a MOTU M4 input and a Topping D50 III output, both
    /// picked under ASIO, refused all three rates. The dialog said the pairing accepted none of
    /// them — true, and so wrong about the cause that the fix it then suggested ("put both cards on
    /// a host API the other also offers") had already been followed. Both <i>were</i> on one host
    /// API. Same host API is necessary and, for ASIO alone, not sufficient.</para>
    ///
    /// <para>This is a rule about the host API rather than a probe, so it costs no native call and
    /// is testable against descriptors. The general version — reading the <c>PaError</c> that
    /// <c>Pa_IsFormatSupported</c> already returns and which this code throws away for a bool —
    /// would also catch whatever WASAPI and WDM-KS refuse, and is the better shape the day a second
    /// case turns up; it is not built, because this is the one that bites.</para>
    /// </summary>
    public static string? DuplexCombinationProblem(
        SoundCardDeviceDescriptor? input, SoundCardDeviceDescriptor? output)
    {
        // A one-direction bench has no combination to refuse, and one device used both ways is the
        // case ASIO is built for.
        if (input is null || output is null || input.Index == output.Index)
            return null;

        if (input.HostApiKind != SoundCardHostApiKind.Asio || output.HostApiKind != SoundCardHostApiKind.Asio)
            return null;

        // The split's counterpart is SoundCardSettings.SplitCombinationProblem: same predicate,
        // harder limit, different advice. See its remarks for why it lives there.
        return $"ASIO cannot take its input from one driver and its output from another: a duplex "
            + $"stream belongs to a single ASIO driver, so '{input.Name}' and '{output.Name}' "
            + "cannot be combined whatever the sample rate. Pick both sides under one of the other "
            + "host APIs — WASAPI, which this analyzer opens in exclusive mode and trusts, offers "
            + "every card Windows can see.";
    }

    /// <summary>
    /// The suffix PortAudio's WASAPI backend appends to a <b>render loopback</b> endpoint.
    ///
    /// <para>Windows exposes one of these beside every playback endpoint, and PortAudio lists it as
    /// a device with input channels and no output channels — indistinguishable, by channel counts
    /// alone, from a real ADC. It is not one: what it captures is the digital stream Windows is
    /// <i>rendering to</i> that endpoint, taken after the mixer and before the converter, so there
    /// is no analog input anywhere in it.</para>
    ///
    /// <para>A literal in <c>pa_win_wasapi.c</c>, not a localised string, which is what makes this
    /// test sound.</para>
    /// </summary>
    public const string RenderLoopbackSuffix = " [Loopback]";

    /// <summary>
    /// Whether a device name is a render loopback endpoint — <see cref="RenderLoopbackSuffix"/>.
    ///
    /// <para><b>Why this is refused rather than merely discouraged.</b> There is no measurement it
    /// can make: it hears nothing the analyzer's generator plays unless this app is also rendering
    /// to that very endpoint, and then what it hears is its own output bit for bit, with neither
    /// converter in the path. Worse, it does not <i>fail</i> — WASAPI delivers loopback data only
    /// while something is rendering, so with the endpoint idle <c>Pa_ReadStream</c> simply never
    /// returns a buffer, and <c>SoundCardDevice.Pump</c> treats a timeout before the first block as
    /// a wait rather than a gap (which is right for a driver spinning up). So it waits for ever:
    /// measured 2026-11-13 on a TOPPING D50 III as the capturing half of a split, 8 s with the
    /// QA403 playing and the recorder armed reported <b>0 input frames</b> against 71 680 stimulus
    /// frames, no dropped chunks, no timeouts logged and the status bar reading Acquiring
    /// throughout.</para>
    /// </summary>
    public static bool IsRenderLoopback(string? deviceName) =>
        deviceName is not null
        && deviceName.EndsWith(RenderLoopbackSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the two devices look like one physical interface. Advisory in three of its four
    /// answers — see <see cref="SoundCardPairing.Unknown"/> for why, and
    /// <see cref="SoundCardPairing.DifferentInterfaces"/> for the one that is not.
    /// </summary>
    public static SoundCardPairing Pairing(SoundCardDeviceDescriptor input, SoundCardDeviceDescriptor output)
    {
        if (input.Index == output.Index && input.IsFullDuplex)
            return SoundCardPairing.FullDuplexDevice;

        string inStem = InterfaceStem(input.Name);
        string outStem = InterfaceStem(output.Name);

        // **Tested before the host-API check, and that order is the whole of what makes the verdict
        // usable.** Two names that each reveal an interface, and reveal different ones, are two
        // cards whichever host APIs they were enumerated under -- so answering Unknown merely
        // because the APIs differ would throw away the evidence in exactly the pairing a two-card
        // bench has. Both stems have to be non-empty: one of them empty is the WDM-KS case, where a
        // name carries no interface and there is nothing to compare.
        if (inStem.Length > 0 && outStem.Length > 0
            && !string.Equals(inStem, outStem, StringComparison.OrdinalIgnoreCase))
        {
            return SoundCardPairing.DifferentInterfaces;
        }

        // Same interface across two host APIs is left as Unknown rather than claimed as a match:
        // one card's WASAPI input against its own ASIO output is two driver instances of one
        // converter, which this code has never opened and has no measurement to say is sound.
        if (input.HostApiIndex != output.HostApiIndex)
            return SoundCardPairing.Unknown;

        return inStem.Length > 0 && string.Equals(inStem, outStem, StringComparison.OrdinalIgnoreCase)
            ? SoundCardPairing.NamesMatch
            : SoundCardPairing.Unknown;
    }

    /// <summary>
    /// Why these two devices cannot be one <c>DeviceKind.SoundCard</c> bench, or null — the
    /// narrowing of a sound card to a single physical interface.
    ///
    /// <para><b>Positive evidence only.</b> It fires on
    /// <see cref="SoundCardPairing.DifferentInterfaces"/> and on nothing else, so a WDM-KS pairing
    /// whose names say nothing (<see cref="SoundCardPairing.Unknown"/>) still opens exactly as it
    /// did. That asymmetry is deliberate: refusing a working bench is worse than letting an odd one
    /// through, and the odd one still gets the drift warning it always got.</para>
    ///
    /// <para>The message names Split rather than merely refusing, because the bench the user has
    /// built is a real one and this build does measure it — just under the other analyzer kind,
    /// which opens a stream per card so the two clocks never share a ring.</para>
    ///
    /// <para><b>Deliberately narrower than <c>ClockDriftCorrection.UsesTwoCards</c>, which asks the
    /// same question of a stored configuration and must not be reused here.</b> That one compares
    /// <see cref="SoundCardCatalog.CardOf"/>, which falls back to the <i>whole device name</i> where
    /// a name carries no interface — so under WDM-KS it compares <c>In 1-2 (In 1-2)</c> against
    /// <c>Out 1-2 (Out 1-2)</c> and calls one card two. That is the right trade where it lives: the
    /// cost of a false positive there is offering a drift correction for a bench that needs none.
    /// Here the cost is refusing a bench that works, on the one host API where refusing leaves
    /// nothing — so this requires both names to say which card they are, and answers nothing
    /// otherwise. Two rules because the two have different costs of being wrong, not because
    /// nobody noticed.</para>
    /// </summary>
    public static string? OneInterfaceProblem(
        SoundCardDeviceDescriptor? input, SoundCardDeviceDescriptor? output)
    {
        // One direction is one device and therefore one clock: there is no pairing to doubt, and
        // this is also every half of a split, whose settings are masked to a single direction.
        if (input is null || output is null)
            return null;

        if (Pairing(input, output) != SoundCardPairing.DifferentInterfaces)
            return null;

        return $"'{input.Name}' and '{output.Name}' are different interfaces, so they are two "
            + "clocks. A sound card bench is one interface; for two, choose Split as the analyzer "
            + "and give it a card on each half, which opens a stream per card.";
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
