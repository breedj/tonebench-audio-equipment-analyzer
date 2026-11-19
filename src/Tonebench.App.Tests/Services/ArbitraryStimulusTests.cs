using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Endpoints;
using Tonebench.App.Json;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The arbitrary stimulus where it meets the session: what the app reports about a file it has
/// loaded, what it refuses, and the two things that must <b>not</b> happen — the samples reaching
/// <c>params_json</c>, and a missing file making it impossible to stop.
/// </summary>
public class ArbitraryStimulusTests : IDisposable
{
    private const double SampleRateHz = 48_000.0;

    private readonly List<string> _files = [];

    public void Dispose()
    {
        ArbitraryWaveformLoader.ClearCache();
        foreach (string path in _files)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A temp file that will not delete is the OS's business, not this test's.
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A 32-bit float mono WAV holding <paramref name="cycles"/> whole cycles of a sine.</summary>
    private string WriteSine(int frames, int sampleRateHz = 48_000, int cycles = 1, double amplitude = 0.5)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tonebench-arb-{Guid.NewGuid():N}.wav");
        _files.Add(path);

        var data = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2.0 * Math.PI * cycles * i / frames));
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), v);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)3);
        writer.Write((short)1);
        writer.Write(sampleRateHz);
        writer.Write(sampleRateHz * 4);
        writer.Write((short)4);
        writer.Write((short)32);
        writer.Write("data"u8.ToArray());
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    private static async Task<DeviceSessionService> ConnectedSessionAsync()
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(
            new SimulatedDevice(), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);
        return session;
    }

    private static ChannelGeneratorConfig Channel(string? path, bool enabled = true) =>
        new(enabled, [])
        {
            Kind = GeneratorKind.Arbitrary,
            Arbitrary = new ArbitrarySettings { Path = path, LevelDbv = -12.0 },
        };

    private static async Task<DeviceStatusResponse> PlayAsync(ChannelGeneratorConfig channel)
    {
        var session = await ConnectedSessionAsync();
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(channel, ChannelGeneratorConfig.Off) { SnapToBin = false },
            CancellationToken.None,
            new AcquisitionConfig(SampleRateHz, 2, 4096));
        return session.GetStatus();
    }

    [Fact]
    public async Task AFileIsLoadedWhenTheGeneratorIsConfigured_AndStatusSaysWhatItIs()
    {
        var status = await PlayAsync(Channel(WriteSine(frames: 480)));
        var arbitrary = status.GeneratorLeft.Arbitrary;

        Assert.Equal(480, arbitrary.Frames);
        Assert.Equal(480, arbitrary.SourceFrames);
        Assert.Equal(48_000.0, arbitrary.SourceSampleRateHz);
        Assert.Equal(480 / 48_000.0, arbitrary.DurationSeconds!.Value, precision: 9);
        Assert.Equal(100.0, arbitrary.RepeatHz!.Value, precision: 6);

        // A sine's crest factor, measured off the file rather than assumed.
        Assert.Equal(3.0103, arbitrary.CrestFactorDb!.Value, precision: 2);

        // And no tone list: a recorded waveform has no single line to name, and a figure invented
        // for one would be read as the fundamental by the scope trigger and the Gen tile alike.
        Assert.Empty(status.GeneratorLeft.Tones);
    }

    /// <summary>
    /// The analyzer's rate decides the loop, not the file's — so a 44.1 kHz file at 48 kHz is a
    /// longer loop of the same waveform, and a rate change rebuilds it rather than playing the old
    /// table at the wrong speed.
    /// </summary>
    [Fact]
    public async Task TheLoopIsBuiltForTheAnalyzersRate()
    {
        string path = WriteSine(frames: 441, sampleRateHz: 44_100);

        var status = await PlayAsync(Channel(path));

        Assert.Equal(480, status.GeneratorLeft.Arbitrary.Frames);
        Assert.Equal(441, status.GeneratorLeft.Arbitrary.SourceFrames);
        Assert.Equal(44_100.0, status.GeneratorLeft.Arbitrary.SourceSampleRateHz);
    }

    /// <summary>
    /// A file that has moved is refused <b>by name</b> rather than played as silence — a stimulus
    /// that is not playing has to say so, and the message is what the user acts on.
    /// </summary>
    [Fact]
    public async Task AMissingFileIsRefusedWithItsName()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tonebench-gone-{Guid.NewGuid():N}.wav");
        var session = await ConnectedSessionAsync();

        var ex = await Assert.ThrowsAsync<WaveFileException>(() => session.ConfigureGeneratorAsync(
            new GeneratorConfig(Channel(path), ChannelGeneratorConfig.Off),
            CancellationToken.None,
            new AcquisitionConfig(SampleRateHz, 2, 4096)));

        Assert.Contains(path, ex.Message);

        // An InvalidOperationException, which is what makes it a 409 carrying this message at every
        // call site rather than a 500 about itself.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    /// <summary>
    /// <b>And a silenced channel is never loaded.</b> Stop and every measurement switch disable
    /// both channels unconditionally and must never fail, so a file that has been moved would
    /// otherwise make it impossible to stop the analyzer at all.
    /// </summary>
    [Fact]
    public async Task ASilencedChannelWithAMissingFile_StillConfigures()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tonebench-gone-{Guid.NewGuid():N}.wav");

        var status = await PlayAsync(Channel(path, enabled: false));

        Assert.Equal(path, status.GeneratorLeft.Arbitrary.Path);
        Assert.Null(status.GeneratorLeft.Arbitrary.Frames);
    }

    /// <summary>
    /// <b>The samples must not reach <c>params_json</c>.</b> A ten-second loop at 192 kHz is nearly
    /// two million floats, and a measurement that carried one inside itself would be megabytes of
    /// JSON per save — and would quietly keep playing a year-old copy of a file that has since
    /// changed. The path is stored; the file is re-read.
    /// </summary>
    [Fact]
    public void TheTableIsNotSerialized()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);

        var file = WaveFileReader.Read(WriteSine(frames: 480));
        var table = ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Left, SampleRateHz);
        var config = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [])
            {
                Kind = GeneratorKind.Arbitrary,
                Arbitrary = new ArbitrarySettings { Path = "amp-step.wav", LevelDbv = -12.0, Table = table },
            },
            ChannelGeneratorConfig.Off);

        string json = JsonSerializer.Serialize(config, options);

        Assert.Contains("amp-step.wav", json);
        Assert.DoesNotContain("table", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(json.Length < 2000, $"the config serialized to {json.Length} characters");
    }

    /// <summary>
    /// The cache is keyed by the file's identity <i>and</i> the rate: the same ask comes back as
    /// the same table, and a different rate builds a new one rather than playing the old at the
    /// wrong speed.
    /// </summary>
    [Fact]
    public void TheCacheServesOneTablePerFileAndRate()
    {
        var settings = new ArbitrarySettings { Path = WriteSine(frames: 480) };

        var first = ArbitraryWaveformLoader.Load(settings, 48_000);
        var again = ArbitraryWaveformLoader.Load(settings, 48_000);
        var faster = ArbitraryWaveformLoader.Load(settings, 96_000);

        Assert.Same(first, again);
        Assert.NotSame(first, faster);
        Assert.Equal(960, faster.Frames);

        ArbitraryWaveformLoader.ClearCache();
        Assert.NotSame(first, ArbitraryWaveformLoader.Load(settings, 48_000));
    }

    /// <summary>
    /// A file edited in place is re-read, because the key carries its write time and length. A path
    /// alone would serve a stale table for the life of the process — which on a bench where the
    /// waveform is being iterated on is exactly the wrong answer.
    /// </summary>
    [Fact]
    public void AFileEditedInPlaceIsReadAgain()
    {
        string path = WriteSine(frames: 480);
        var settings = new ArbitrarySettings { Path = path };

        var before = ArbitraryWaveformLoader.Load(settings, 48_000);

        File.WriteAllBytes(path, File.ReadAllBytes(WriteSine(frames: 960)));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

        var after = ArbitraryWaveformLoader.Load(settings, 48_000);

        Assert.NotSame(before, after);
        Assert.Equal(960, after.Frames);
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void TryMapChannel_MapsTheArbitraryBlock()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "arbitrary",
            Arbitrary: new ArbitraryRequest(Path: @"C:\waves\step.wav", Channel: "right", LevelDbv: -3.0));

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out var config, out _));

        Assert.Equal(GeneratorKind.Arbitrary, config.Kind);
        Assert.Equal(@"C:\waves\step.wav", config.Arbitrary!.Path);
        Assert.Equal(ArbitraryChannel.Right, config.Arbitrary.Channel);
        Assert.Equal(-3.0, config.Arbitrary.LevelDbv);
        Assert.Null(config.Arbitrary.Table);
    }

    [Fact]
    public void TryMapChannel_RefusesAnArbitraryWithNoFile()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "Arbitrary",
            Arbitrary: new ArbitraryRequest(Path: "   ", Channel: null, LevelDbv: null));

        Assert.False(GeneratorEndpoints.TryMapChannel(request, "Right", out _, out var error));
        Assert.Contains(".wav", error);
        Assert.StartsWith("Right channel:", error);
    }

    /// <summary>And only for its own kind: an untouched block must not refuse a stimulus that never looks at it.</summary>
    [Fact]
    public void TryMapChannel_IgnoresAnUnusedArbitraryBlock()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: [new GeneratorToneRequest(1000.0, -6.0)], Kind: "Tones",
            Arbitrary: new ArbitraryRequest(Path: null, Channel: null, LevelDbv: null));

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out _, out _));
    }
}
