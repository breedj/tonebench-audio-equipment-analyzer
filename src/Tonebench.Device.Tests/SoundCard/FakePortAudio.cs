using Tonebench.Device.SoundCard;

namespace Tonebench.Device.Tests.SoundCard;

/// <summary>
/// A stand-in for the native library, so everything above the interop can be tested without a card.
///
/// <para>Its default topology is the one measured on Joost's machine (2026-09-30): a MOTU M4 split
/// into separate input and output devices on every host API, which is the shape the enumerator's
/// rules exist for. Nothing here has both directions unless a test asks for it.</para>
/// </summary>
internal sealed class FakePortAudio : IPortAudio
{
    private readonly List<SoundCardDeviceDescriptor> _devices = [];
    private readonly List<SoundCardHostApi> _apis = [];

    public string VersionText => "FakePortAudio";

    public IReadOnlyList<SoundCardHostApi> HostApis => _apis;

    public IReadOnlyList<SoundCardDeviceDescriptor> Devices => _devices;

    public int RefreshCount { get; private set; }

    /// <summary>Rates this fake claims to accept. Keyed by (device pair, exclusive) so a test can make shared mode meaner than exclusive, as WASAPI really is.</summary>
    public Func<SoundCardStreamRequest, bool> FormatSupported { get; set; } = _ => true;

    public void Refresh() => RefreshCount++;

    /// <summary>Devices whose driver this fake claims has a control panel. Empty by default: most host APIs have none.</summary>
    public HashSet<int> DevicesWithAControlPanel { get; } = [];

    public int ControlPanelsShown { get; private set; }

    public bool ShowControlPanel(int deviceIndex)
    {
        if (!DevicesWithAControlPanel.Contains(deviceIndex))
            return false;

        ControlPanelsShown++;
        return true;
    }

    public bool IsFormatSupported(SoundCardStreamRequest request) => FormatSupported(request);

    private readonly Lock _dispatcherGate = new();
    private StaDispatcher? _dispatcher;

    /// <summary>
    /// Created on demand so an enumerator-only test never starts a thread it does not need, and
    /// behind a lock for the reason <c>PortAudioNative.Shared</c> has one: two devices streaming at
    /// once reach this from two threads, and an unguarded <c>??=</c> hands each of them a
    /// dispatcher of its own. That reads as the two cards having separate transfer threads when
    /// they were meant to be sharing one — the exact thing a two-card test is asking about, so the
    /// race could only ever produce a wrong answer.
    /// </summary>
    public StaDispatcher Dispatcher
    {
        get
        {
            lock (_dispatcherGate)
            {
                return _dispatcher ??= new StaDispatcher("FakePortAudio");
            }
        }
    }

    /// <summary>The stream the next OpenStream hands out, so a test can arm it before the pump starts.</summary>
    public FakePortAudioStream NextStream { get; set; } = new();

    /// <summary>
    /// Supplies a stream per open instead of handing out the one <see cref="NextStream"/>, so two
    /// devices can run over one fake library. Null keeps the single-stream behaviour every other
    /// test here is written against.
    /// </summary>
    public Func<SoundCardStreamRequest, FakePortAudioStream>? StreamFactory { get; set; }

    /// <summary>
    /// The loop dispatcher handed to every open, in order, warm-ups included -- so a test can
    /// assert that two devices were not given the same transfer thread, which is the thing that
    /// makes the second one's pump queue behind the first and never run.
    /// </summary>
    public List<StaDispatcher?> LoopDispatchers { get; } = [];

    public SoundCardStreamRequest? OpenedWith { get; private set; }

    public int OpenedFramesPerBuffer { get; private set; }

    /// <summary>How many streams have been opened, warm-ups included.</summary>
    public int OpenCount { get; private set; }

    /// <summary>
    /// A stream for a <i>warm-up</i> open, which the device makes and throws away whenever the
    /// sample rate has changed since the last one (see <c>SoundCardDevice.ReclockIfNeeded</c>).
    ///
    /// <para>Kept separate from <see cref="NextStream"/> so a test arming the stream it means to
    /// inspect still gets that stream, and so a warm-up's own start/stop/dispose do not land in the
    /// calls a test asserts on.</para>
    /// </summary>
    public FakePortAudioStream? WarmUpStream { get; private set; }

    /// <summary>
    /// The loop dispatcher the most recent open was given, warm-ups included, so a test can assert
    /// two devices were not handed the same one. The warm-up opens first and the stream that runs
    /// second, so after a stream has started this is that stream's.
    /// </summary>
    public StaDispatcher? OpenedLoopDispatcher { get; private set; }

    public IPortAudioStream OpenStream(
        SoundCardStreamRequest request, int framesPerBuffer, StaDispatcher? loopDispatcher = null)
    {
        OpenedWith = request;
        OpenedFramesPerBuffer = framesPerBuffer;
        OpenedLoopDispatcher = loopDispatcher;
        LoopDispatchers.Add(loopDispatcher);

        // Two devices over one fake library, which is how two cards really run: there is one
        // PortAudioNative for the process, so a test about two of them cannot give each its own
        // fake without testing something else. A factory sidesteps NextStream/WarmUpStream
        // entirely -- both are single-valued, and the warm-up branch below counts opens across the
        // whole fake, so the second device would be handed the first one's stream.
        if (StreamFactory is not null)
        {
            OpenCount++;
            FakePortAudioStream made = StreamFactory(request);
            made.SampleRateHz = request.SampleRateHz;
            return made;
        }

        // The first open of a rate is the device's throwaway; the next one is the stream that runs.
        if (OpenCount++ == 0 && WarmUpStream is null)
        {
            WarmUpStream = new FakePortAudioStream { SampleRateHz = request.SampleRateHz };
            return WarmUpStream;
        }

        NextStream.SampleRateHz = request.SampleRateHz;
        return NextStream;
    }

    public void Dispose() => _dispatcher?.Dispose();

    public SoundCardHostApi AddHostApi(string name, SoundCardHostApiKind kind)
    {
        var api = new SoundCardHostApi(_apis.Count, name, kind);
        _apis.Add(api);
        return api;
    }

    public SoundCardDeviceDescriptor AddDevice(SoundCardHostApi api, string name, int inputs, int outputs)
    {
        var device = new SoundCardDeviceDescriptor(
            Index: _devices.Count,
            Name: name,
            HostApiIndex: api.Index,
            HostApiName: api.Name,
            HostApiKind: api.Kind,
            MaxInputChannels: inputs,
            MaxOutputChannels: outputs,
            DefaultLowInputLatencyS: 0.01,
            DefaultLowOutputLatencyS: 0.01,
            DefaultSampleRateHz: 48_000.0);
        _devices.Add(device);
        return device;
    }

    /// <summary>The measured Windows topology: one interface, split per host API, never full duplex.</summary>
    public static FakePortAudio WithASplitInterface()
    {
        var fake = new FakePortAudio();
        foreach ((string name, SoundCardHostApiKind kind) in new[]
        {
            ("Windows WASAPI", SoundCardHostApiKind.Wasapi),
            ("Windows WDM-KS", SoundCardHostApiKind.WdmKs),
        })
        {
            var api = fake.AddHostApi(name, kind);
            if (kind == SoundCardHostApiKind.WdmKs)
            {
                // WDM-KS strips the interface name -- this is verbatim what it reports.
                fake.AddDevice(api, "In 1-2 (In 1-2)", inputs: 2, outputs: 0);
                fake.AddDevice(api, "Out 1-2 (Out 1-2)", inputs: 0, outputs: 2);
            }
            else
            {
                fake.AddDevice(api, "In 1-2 (MOTU M Series)", inputs: 2, outputs: 0);
                fake.AddDevice(api, "In 3-4 (MOTU M Series)", inputs: 2, outputs: 0);
                fake.AddDevice(api, "Out 1-2 (MOTU M Series)", inputs: 0, outputs: 2);
            }
        }

        return fake;
    }
}
