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
    bool HasControlPanel);

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
    string? Problem = null);

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

            SoundCardDeviceDescriptor? input = enumerator.Find(inputHostApi, inputDevice);
            SoundCardDeviceDescriptor? output = enumerator.Find(outputHostApi, outputDevice);

            if (input is null || output is null)
            {
                return new SoundCardProbe(
                    [], SoundCardPairing.Unknown.ToString(), string.Empty, string.Empty,
                    input is null ? $"'{inputDevice}' is not there." : $"'{outputDevice}' is not there.");
            }

            int inputChannels = inputChannelOffset + 2;
            int outputChannels = outputChannelOffset + 2;

            if (SoundCardEnumerator.ChannelPairProblem(
                    input, inputChannelOffset, output, outputChannelOffset) is { } channelProblem)
            {
                return new SoundCardProbe(
                    [], SoundCardEnumerator.Pairing(input, output).ToString(),
                    SoundCardEnumerator.Trust(input.HostApiKind).ToString(),
                    SoundCardEnumerator.Trust(output.HostApiKind).ToString(),
                    channelProblem);
            }

            var rates = enumerator.SupportedRatesHz(
                input, output, exclusive: true, inputChannels, outputChannels);

            return new SoundCardProbe(
                rates,
                SoundCardEnumerator.Pairing(input, output).ToString(),
                SoundCardEnumerator.Trust(input.HostApiKind).ToString(),
                SoundCardEnumerator.Trust(output.HostApiKind).ToString());
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
    /// </summary>
    private static string InterfaceOf(SoundCardDeviceDescriptor d)
    {
        string stem = SoundCardEnumerator.InterfaceStem(d.Name);
        return stem.Length > 0 ? stem : d.Name;
    }

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
