namespace Tonebench.Device.SoundCard;

/// <summary>
/// One of PortAudio's host APIs — ASIO, WASAPI, WDM-KS, DirectSound, MME on Windows; ALSA, JACK,
/// PulseAudio on Linux; Core Audio on macOS. Which of them exist is a property of the native
/// library, not of this codebase: the list comes from <c>Pa_GetHostApiCount</c> at run time, so an
/// ASIO-enabled rebuild of the DLL makes ASIO appear here with no code change (see
/// <c>packaging/native/portaudio.md</c>).
/// </summary>
public sealed record SoundCardHostApi(int Index, string Name, SoundCardHostApiKind Kind);

/// <summary>
/// The host API kinds this app treats differently. Everything else is <see cref="Other"/> — the
/// point is not to enumerate PortAudio's list but to name the ones with a rule attached.
/// </summary>
public enum SoundCardHostApiKind
{
    Other = 0,

    /// <summary>The only one <i>on Windows</i> that presents a card as a single full-duplex device (see <see cref="CoreAudio"/> for the macOS counterpart), and the only one anywhere with sample-accurate channel selectors.</summary>
    Asio,

    /// <summary>Usable only in exclusive mode — shared mode is locked to the Windows mixer's rate and resamples silently.</summary>
    Wasapi,

    /// <summary>Kernel streaming: unmixed, but its device names carry no interface name, so two endpoints cannot be paired by name.</summary>
    WdmKs,

    /// <summary>Resamples in software and accepts any rate, so it can never be trusted for a measurement.</summary>
    DirectSound,

    /// <summary>As DirectSound: accepts any rate by resampling.</summary>
    Mme,

    /// <summary>Linux.</summary>
    Alsa,

    /// <summary>Linux.</summary>
    Jack,

    /// <summary>
    /// macOS, and the only host API there. Like <see cref="Asio"/> it presents a card <i>once</i>,
    /// full-duplex, rather than one endpoint per direction — so the pairing logic in
    /// <c>SoundCardEnumerator</c> takes the ASIO shape here.
    ///
    /// <para>Unlike ASIO it is <b>not</b> trusted: PortAudio's macOS backend resamples by default.
    /// <c>SoundCardEnumerator.Trust</c> carries the measurement and the way out of it. It still has
    /// to be <i>named</i> rather than left as <see cref="Other"/>, for two reasons that have
    /// nothing to do with trust — <c>PreferenceOf</c> would rank it last in a picker it is the only
    /// entry in, and the client's own <c>DEFAULT_HOST_API_KINDS</c> filter is a list of kinds, so
    /// an unnamed one is a dropdown with nothing in it.</para>
    /// </summary>
    CoreAudio,
}

/// <summary>One device as PortAudio reports it. Note a Windows device is usually one direction only — see <see cref="SoundCardEnumerator"/>.</summary>
public sealed record SoundCardDeviceDescriptor(
    int Index,
    string Name,
    int HostApiIndex,
    string HostApiName,
    SoundCardHostApiKind HostApiKind,
    int MaxInputChannels,
    int MaxOutputChannels,
    double DefaultLowInputLatencyS,
    double DefaultLowOutputLatencyS,
    double DefaultSampleRateHz)
{
    public bool HasInput => MaxInputChannels > 0;

    public bool HasOutput => MaxOutputChannels > 0;

    /// <summary>A single device carrying both directions — what ASIO gives, and what nothing else on Windows does.</summary>
    public bool IsFullDuplex => HasInput && HasOutput;
}

/// <summary>
/// Which directions a stream carries. <see cref="Duplex"/> is the one shape this app opened until
/// 2026-11-11, and it is still the right one whenever both directions are the same physical
/// interface — one device, therefore one clock, and the lockstep read/write loop that
/// <c>SoundCardDevice.Pump</c> is built around.
///
/// <para>The one-direction shapes exist for two devices that are <i>not</i> one interface: a
/// capture-only ADC (an E1DA Cosmos has only a USB-C out), a playback-only USB DAC, or a QA403
/// capturing while a sound card plays. Note what that costs and why it is still the right shape —
/// a single duplex stream spanning two devices would be read and written in lockstep against two
/// different clocks, so the output ring accumulates the difference at the ppm rate (~19 samples/s
/// at 100 ppm and 192 kHz, which eats a 180 ms ring in about half an hour) and the stream glitches
/// on a schedule no ring sizing can push out. Two independent one-direction streams each
/// free-run on their own device's clock and do not have that failure. The drift is still there in
/// the *measurement*, and that is a separate matter — see <c>SoundCardEnumerator.Pairing</c>.</para>
/// </summary>
public enum SoundCardStreamDirection
{
    /// <summary>Both directions on one device.</summary>
    Duplex = 0,

    /// <summary>Capture only. Nothing is written, so the blocking read is the only clock.</summary>
    InputOnly = 1,

    /// <summary>Playback only. Nothing is read, so the blocking write is the only clock, and the stream yields no blocks.</summary>
    OutputOnly = 2,
}

/// <summary>What to open: one input device, one output device, a rate, and whether to demand exclusive access.</summary>
public sealed record SoundCardStreamRequest(
    int InputDevice,
    int InputChannelCount,
    int OutputDevice,
    int OutputChannelCount,
    double SampleRateHz,
    bool Exclusive)
{
    /// <summary>Suggested latency, in seconds; null lets the device's own default-low figure stand.</summary>
    public double? SuggestedLatencyS { get; init; }

    /// <summary>
    /// Which directions to open. Defaults to <see cref="SoundCardStreamDirection.Duplex"/>, so
    /// every caller that predates one-direction streams keeps the shape it asked for.
    /// </summary>
    public SoundCardStreamDirection Direction { get; init; } = SoundCardStreamDirection.Duplex;

    /// <summary>True when this stream carries capture. The device and channel count are ignored when it does not.</summary>
    public bool HasInput => Direction != SoundCardStreamDirection.OutputOnly;

    /// <summary>True when this stream carries playback. The device and channel count are ignored when it does not.</summary>
    public bool HasOutput => Direction != SoundCardStreamDirection.InputOnly;
}

/// <summary>
/// The native library behind an interface, so everything above <see cref="PortAudioInterop"/> can be
/// tested without a sound card — the same reason <c>ILoopbackReferenceStore</c> is an interface.
/// </summary>
internal interface IPortAudio : IDisposable
{
    string VersionText { get; }

    IReadOnlyList<SoundCardHostApi> HostApis { get; }

    IReadOnlyList<SoundCardDeviceDescriptor> Devices { get; }

    /// <summary>
    /// The thread every native call is made on. Exposed because a stream's transfer loop has to run
    /// there too — both for the apartment ASIO needs and because a dedicated thread is what the
    /// DAC-gap rule asks for — and because a fake needs to supply one so the loop is testable
    /// without a card.
    /// </summary>
    StaDispatcher Dispatcher { get; }

    /// <summary>Re-reads the device list. PortAudio caches it at initialise, so a card plugged in since then is invisible until this runs.</summary>
    void Refresh();

    /// <summary>Whether the device pair will accept this rate. For WASAPI the answer differs between shared and exclusive, which is why the request carries the flag.</summary>
    bool IsFormatSupported(SoundCardStreamRequest request);

    /// <summary>Opens a duplex stream in PortAudio's blocking read/write mode.</summary>
    IPortAudioStream OpenStream(SoundCardStreamRequest request, int framesPerBuffer);

    /// <summary>
    /// Shows an ASIO driver's own control panel and <b>blocks until the user closes it</b> — it is
    /// a modal dialog owned by the driver, and PortAudio offers no way to show it otherwise.
    ///
    /// <para>The block is on the STA dispatcher thread, which is also where a running stream's
    /// transfer loop lives, so the caller must refuse while acquiring. Returns false, rather than
    /// throwing, for a device whose host API has no such panel.</para>
    /// </summary>
    bool ShowControlPanel(int deviceIndex);
}

/// <summary>An open duplex stream. Read before write each iteration — the documented way not to deadlock one.</summary>
internal interface IPortAudioStream : IDisposable
{
    double InputLatencyS { get; }

    double OutputLatencyS { get; }

    double SampleRateHz { get; }

    void Start();

    void Stop();

    /// <summary>Fills <paramref name="buffer"/> with interleaved frames. True when the driver reported an input overflow — samples are missing, and the transfer still happened.</summary>
    bool Read(float[] buffer, int frames);

    /// <summary>Plays interleaved frames. True when the driver reported an output underflow — the DAC ran dry.</summary>
    bool Write(float[] buffer, int frames);

    /// <summary>
    /// Frames the driver already has waiting for the next <see cref="Read"/>, and free space in the
    /// output ring — <c>-1</c> for either when the driver will not say.
    ///
    /// <para>Diagnostics only, and read only after an iteration has already run late: several
    /// transfers waiting on a late read means the data had been sitting there and the host was late,
    /// where nothing behind it means the driver had only just produced it. The two cases look
    /// identical in every other number this loop keeps, including thread CPU time, because a thread
    /// blocked inside the driver's read and a thread that was never scheduled both burn none.</para>
    /// </summary>
    long FramesAvailableToRead();

    /// <inheritdoc cref="FramesAvailableToRead"/>
    long FramesAvailableToWrite();
}
