namespace Tonebench.Device.SoundCard;

/// <summary>One device as the picker offers it, with the judgements the app has about its host API.</summary>
/// <param name="Trust">Whether a measurement through this host API can be believed — see <see cref="SoundCardHostApiTrust"/>.</param>
/// <param name="Preference">Sort key: the host APIs that reach the hardware first, the ones that resample last.</param>
public sealed record SoundCardOption(
    string HostApi,
    string Name,
    /// <summary>
    /// The physical interface this device belongs to, so the picker can offer a card first and its
    /// inputs and outputs second — which is what keeps both sides on one card, and therefore on one
    /// clock.
    ///
    /// <para>Under ASIO the device <i>is</i> the card, so this is its name. Under WASAPI,
    /// DirectSound and MME it is the interface buried in the device name —
    /// <c>In 1-2 (MOTU M Series)</c> is the MOTU. Under WDM-KS the name carries no interface at all
    /// (<c>In 1-2 (In 1-2)</c>), so each device stands alone and the grouping cannot help; that is
    /// honest rather than a gap, and the pairing warning still covers it.</para>
    /// </summary>
    string InterfaceName,
    string HostApiKind,
    int MaxInputChannels,
    int MaxOutputChannels,
    bool IsFullDuplex,
    string Trust,
    int Preference,
    /// <summary>
    /// Whether this device's driver has a settings panel of its own that the app can raise. ASIO
    /// only: buffer size, clock source and often the sample rate live there and PortAudio cannot
    /// set any of them. The other host APIs have nothing equivalent — Windows' own sound control
    /// panel configures the <i>mixer</i>, which exclusive mode bypasses, so offering it would point
    /// at settings that do not apply.
    /// </summary>
    bool HasControlPanel)
{
    /// <summary>
    /// The one line the log writes for this device, e.g. <c>Windows WASAPI 'Microfoon (2- E-MU
    /// Tracker Pre)' -> card '2- E-MU Tracker Pre' 2/0 ch</c>.
    ///
    /// <para>The card name is printed <b>only where it differs from the device name</b> — under
    /// ASIO and WDM-KS the device is its own card, and repeating it would double the length of
    /// every line that has nothing to say. Where it does appear it is the field that matters most:
    /// the picker collapses the list to one entry per card
    /// (<c>web/src/soundcard/cardList.ts</c>), so two host APIs that name one interface differently
    /// — or two interfaces that happen to name it the same — is the whole of what that rule can get
    /// wrong, and this is the only place it can be seen.</para>
    ///
    /// <para>Here rather than at the call site so it can be tested: a diagnostic nobody checks is
    /// how the gap this was written for arose in the first place.</para>
    /// </summary>
    public string Describe()
    {
        string card = string.Equals(InterfaceName, Name, StringComparison.Ordinal)
            ? string.Empty
            : $" -> card '{InterfaceName}'";

        return $"{HostApi} '{Name}'{card} {MaxInputChannels}/{MaxOutputChannels} ch"
            + (IsFullDuplex ? ", full duplex" : string.Empty);
    }
}

/// <summary>
/// One host API the native library carries, with what it found under it.
///
/// <para><b>Reported including the ones that found nothing</b>, which is the whole point of it. A
/// host API with no usable devices appears in neither <see cref="SoundCardInventory.Inputs"/> nor
/// <see cref="SoundCardInventory.Outputs"/>, so without this list "ASIO is not offered because this
/// build has none" and "ASIO is here and no driver answered" are the same screenshot and the same
/// log line — and only the first is ours to fix.</para>
/// </summary>
/// <param name="Kind">The <see cref="SoundCardHostApiKind"/> name, so the client can apply its own rules without parsing <paramref name="Name"/>.</param>
/// <param name="DeviceCount">
/// Every device PortAudio listed under this API, <b>including those reporting no channels at
/// all</b>. That case is the one worth naming: an ASIO driver which starts but cannot reach its
/// hardware — ASIO4ALL with nothing ticked in its own panel, or a card another application holds —
/// lands here as a device with 0 in and 0 out, where a driver that is simply not installed does not
/// appear at all. The two need different advice and looked identical until this existed.
/// </param>
/// <param name="InputCount">Devices with at least one input channel, i.e. those the picker can offer.</param>
/// <param name="OutputCount">Devices with at least one output channel.</param>
public sealed record SoundCardHostApiOption(
    string Name,
    string Kind,
    int DeviceCount,
    int InputCount,
    int OutputCount,
    string Trust,
    int Preference)
{
    /// <summary>
    /// The terse form the log writes, e.g. <c>ASIO 1 dev/0 in/0 out</c>. Here rather than at the
    /// call site so one description serves the log and anything else that has to quote it, and so
    /// it can be tested — a diagnostic nobody checks is how this gap arose in the first place.
    /// </summary>
    public string Describe() => $"{Name} {DeviceCount} dev/{InputCount} in/{OutputCount} out";
}

/// <summary>What the picker needs to draw itself.</summary>
/// <param name="Available">
/// False when the native library could not be loaded or started at all — a machine with no audio
/// stack, or a publish that lost <c>portaudio.dll</c>. The dialog says so rather than showing an
/// empty list that looks like "no cards".
/// </param>
public sealed record SoundCardInventory(
    bool Available,
    string Version,
    /// <summary>
    /// Every host API the native library carries, those that found nothing included — see
    /// <see cref="SoundCardHostApiOption"/> for why that is not merely informational.
    /// </summary>
    IReadOnlyList<SoundCardHostApiOption> HostApis,
    IReadOnlyList<SoundCardOption> Inputs,
    IReadOnlyList<SoundCardOption> Outputs,
    string? Problem = null)
{
    /// <summary>
    /// Every device once, ordered as the picker offers them: <see cref="Inputs"/> and
    /// <see cref="Outputs"/> are two views of one device table, and a full-duplex device is in
    /// both.
    ///
    /// <para>Deduplicated on host API <i>and</i> name, because the same name under two host APIs is
    /// two different devices — and because adding the two lists' lengths is exactly the fault the
    /// enumeration log line had until 2026-11-07, where this bench's one MOTU under ASIO was
    /// counted as two.</para>
    ///
    /// <para><b>A method rather than a property</b>, although it computes nothing a caller could
    /// not: this record is the body of <c>GET /api/soundcard/devices</c>, and
    /// <c>System.Text.Json</c> serialises a get-only property — which would send the whole device
    /// table a third time.</para>
    /// </summary>
    public IReadOnlyList<SoundCardOption> DistinctDevices() =>
        [.. Inputs
            .Concat(Outputs)
            .DistinctBy(o => (o.HostApi, o.Name))
            .OrderBy(o => o.Preference)
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)];
}

/// <summary>
/// The result of asking whether a particular pairing will work, and at which rates.
/// </summary>
/// <param name="Pairing">
/// <c>FullDuplexDevice</c>, <c>NamesMatch</c> or <c>Unknown</c>. Advisory: two endpoints of one
/// interface share a clock, two separate cards do not and their drift is reported as DUT response —
/// but WDM-KS names carry no interface at all, so this can only warn.
/// </param>
public sealed record SoundCardProbe(
    IReadOnlyList<double> SampleRatesHz,
    string Pairing,
    string InputTrust,
    string OutputTrust,
    string? Problem = null);

/// <summary>
/// The public face of the sound-card layer: everything above it stays out of the interop, which is
/// <c>internal</c> on purpose. Every call goes through <see cref="PortAudioNative.Shared"/>, so it
/// lands on the one STA thread ASIO requires.
/// </summary>
public static class SoundCardCatalog
{
    /// <summary>
    /// Lists what the machine has. Never throws: a missing or broken native library comes back as
    /// <see cref="SoundCardInventory.Available"/> false with the reason, because the picker has to
    /// be able to say "PortAudio would not start" rather than "no cards found".
    /// </summary>
    public static SoundCardInventory Enumerate(bool refresh = false)
    {
        try
        {
            PortAudioNative pa = PortAudioNative.Shared;
            if (refresh)
                pa.Refresh();

            var options = pa.Devices.Select(Describe).ToList();

            return new SoundCardInventory(
                Available: true,
                Version: pa.VersionText,
                HostApis: new SoundCardEnumerator(pa).HostApiOptions(),
                Inputs: [.. options.Where(o => o.MaxInputChannels > 0).OrderBy(o => o.Preference).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)],
                Outputs: [.. options.Where(o => o.MaxOutputChannels > 0).OrderBy(o => o.Preference).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)]);
        }
        catch (Exception ex)
        {
            return new SoundCardInventory(
                Available: false, Version: string.Empty,
                HostApis: [], Inputs: [], Outputs: [], Problem: ex.Message);
        }
    }

    /// <summary>
    /// Which of 48/96/192 kHz this pairing accepts, and how confident we are that the two are one
    /// interface. WASAPI is probed in <b>exclusive</b> mode, which is the only mode this app opens:
    /// in shared mode a card offers only the Windows mixer's rate and resamples to it.
    /// </summary>
    public static SoundCardProbe Probe(
        string inputHostApi, string inputDevice, int inputChannelOffset,
        string outputHostApi, string outputDevice, int outputChannelOffset)
    {
        try
        {
            PortAudioNative pa = PortAudioNative.Shared;
            var enumerator = new SoundCardEnumerator(pa);

            // **An empty name is a direction this bench does not carry, not a device that is
            // missing.** A capture-only ADC and a playback-only DAC are each half of this, and
            // before the one-direction streams of 2026-11-11 the probe answered both with
            // "'' is not there." -- which left the picker unable to save a bench the device layer
            // could open perfectly well. A name that was given and cannot be found is still a
            // fault, and still says so.
            bool wantsInput = !string.IsNullOrWhiteSpace(inputDevice);
            bool wantsOutput = !string.IsNullOrWhiteSpace(outputDevice);

            if (!wantsInput && !wantsOutput)
            {
                return new SoundCardProbe(
                    [], SoundCardPairing.Unknown.ToString(), string.Empty, string.Empty,
                    "Choose an input, an output, or both.");
            }

            SoundCardDeviceDescriptor? input = wantsInput ? enumerator.Find(inputHostApi, inputDevice) : null;
            SoundCardDeviceDescriptor? output = wantsOutput ? enumerator.Find(outputHostApi, outputDevice) : null;

            if ((wantsInput && input is null) || (wantsOutput && output is null))
            {
                return new SoundCardProbe(
                    [], SoundCardPairing.Unknown.ToString(), string.Empty, string.Empty,
                    input is null ? $"'{inputDevice}' is not there." : $"'{outputDevice}' is not there.");
            }

            int inputChannels = inputChannelOffset + 2;
            int outputChannels = outputChannelOffset + 2;

            // Only a bench holding both directions has a pairing to doubt; one device is one clock,
            // and the clock question then belongs to whatever supplies the other half. Trust is
            // per side, so an absent one reports nothing rather than borrowing its partner's.
            string pairing = input is not null && output is not null
                ? SoundCardEnumerator.Pairing(input, output).ToString()
                : SoundCardPairing.FullDuplexDevice.ToString();
            string inputTrust = input is null ? string.Empty : SoundCardEnumerator.Trust(input.HostApiKind).ToString();
            string outputTrust = output is null ? string.Empty : SoundCardEnumerator.Trust(output.HostApiKind).ToString();

            if (SoundCardEnumerator.ChannelPairProblem(
                    input, inputChannelOffset, output, outputChannelOffset) is { } channelProblem)
            {
                return new SoundCardProbe([], pairing, inputTrust, outputTrust, channelProblem);
            }

            var rates = enumerator.SupportedRatesHz(
                input, output, exclusive: true, inputChannels, outputChannels);

            return new SoundCardProbe(rates, pairing, inputTrust, outputTrust);
        }
        catch (Exception ex)
        {
            return new SoundCardProbe([], SoundCardPairing.Unknown.ToString(), string.Empty, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Raises an ASIO driver's own control panel for the named device and <b>blocks until the user
    /// closes it</b>, because that is the only way the driver offers it.
    ///
    /// <para>Two consequences the caller has to respect. The wait happens on the one STA thread
    /// every PortAudio call is made on — the thread a running stream's transfer loop also lives on
    /// — so this must not be called while acquiring, or the panel would queue behind a loop that
    /// never ends. And while it is open, every other sound-card call queues behind it.</para>
    ///
    /// <para>Returns false when that device has no panel, which is every host API but ASIO.</para>
    /// </summary>
    public static bool ShowControlPanel(string hostApi, string deviceName)
    {
        PortAudioNative pa = PortAudioNative.Shared;
        SoundCardDeviceDescriptor device = new SoundCardEnumerator(pa).Find(hostApi, deviceName)
            ?? throw new SoundCardNotFoundException(
                $"'{deviceName}' ({hostApi}) is not there any more. Refresh the list and pick it again.");

        return device.HostApiKind == SoundCardHostApiKind.Asio && pa.ShowControlPanel(device.Index);
    }

    /// <summary>
    /// The card a device belongs to: the interface buried in its name where there is one, and the
    /// device's own name where there is not — which is ASIO, whose device is the card, and WDM-KS,
    /// whose names name nothing.
    ///
    /// <para>Public, and taking a name rather than a descriptor, because it is also how the app
    /// decides whether a configured bench is one card or two without enumerating PortAudio — see
    /// <c>ClockDriftCorrection.UsesTwoCards</c>, where getting it from the stored device names
    /// alone is the whole point. One rule rather than two, or the picker's card list and the
    /// clock-drift correction could disagree about what a card is.</para>
    /// </summary>
    public static string CardOf(string deviceName)
    {
        string stem = SoundCardEnumerator.InterfaceStem(deviceName);
        return stem.Length > 0 ? stem : deviceName;
    }

    private static string InterfaceOf(SoundCardDeviceDescriptor d) => CardOf(d.Name);

    private static SoundCardOption Describe(SoundCardDeviceDescriptor d) => new(
        HostApi: d.HostApiName,
        Name: d.Name,
        InterfaceName: InterfaceOf(d),
        HostApiKind: d.HostApiKind.ToString(),
        MaxInputChannels: d.MaxInputChannels,
        MaxOutputChannels: d.MaxOutputChannels,
        IsFullDuplex: d.IsFullDuplex,
        Trust: SoundCardEnumerator.Trust(d.HostApiKind).ToString(),
        Preference: SoundCardEnumerator.PreferenceOf(d.HostApiKind),
        HasControlPanel: d.HostApiKind == SoundCardHostApiKind.Asio);
}
