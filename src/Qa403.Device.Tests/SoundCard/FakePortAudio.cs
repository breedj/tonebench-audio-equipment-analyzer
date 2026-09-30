using Qa403.Device.SoundCard;

namespace Qa403.Device.Tests.SoundCard;

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

    public bool IsFormatSupported(SoundCardStreamRequest request) => FormatSupported(request);

    private StaDispatcher? _dispatcher;

    /// <summary>Created on demand so an enumerator-only test never starts a thread it does not need.</summary>
    public StaDispatcher Dispatcher => _dispatcher ??= new StaDispatcher("FakePortAudio");

    /// <summary>The stream the next OpenStream hands out, so a test can arm it before the pump starts.</summary>
    public FakePortAudioStream NextStream { get; set; } = new();

    public SoundCardStreamRequest? OpenedWith { get; private set; }

    public int OpenedFramesPerBuffer { get; private set; }

    public IPortAudioStream OpenStream(SoundCardStreamRequest request, int framesPerBuffer)
    {
        OpenedWith = request;
        OpenedFramesPerBuffer = framesPerBuffer;
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
