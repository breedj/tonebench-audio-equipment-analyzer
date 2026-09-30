namespace Qa403.Device.SoundCard;

/// <summary>
/// One of PortAudio's host APIs — ASIO, WASAPI, WDM-KS, DirectSound, MME on Windows; ALSA, JACK,
/// PulseAudio on Linux. Which of them exist is a property of the native library, not of this
/// codebase: the list comes from <c>Pa_GetHostApiCount</c> at run time, so an ASIO-enabled rebuild
/// of the DLL makes ASIO appear here with no code change (see <c>packaging/native/portaudio.md</c>).
/// </summary>
public sealed record SoundCardHostApi(int Index, string Name, SoundCardHostApiKind Kind);

/// <summary>
/// The host API kinds this app treats differently. Everything else is <see cref="Other"/> — the
/// point is not to enumerate PortAudio's list but to name the ones with a rule attached.
/// </summary>
public enum SoundCardHostApiKind
{
    Other = 0,

    /// <summary>The only one that presents a card as a single full-duplex device, and the only one with sample-accurate channel selectors.</summary>
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
}
