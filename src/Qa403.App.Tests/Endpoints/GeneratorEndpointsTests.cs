using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Endpoints;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Endpoints;

public class GeneratorEndpointsTests
{
    [Fact]
    public void TryMapChannel_WithoutAKind_StillMeansAPlainToneList()
    {
        // The original request body shape ({ enabled, tones }) must keep working unchanged.
        var request = new GeneratorChannelRequest(Enabled: true, Tones: [new GeneratorToneRequest(1000.0, -6.0)]);

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out var config, out _));

        Assert.Equal(GeneratorKind.Tones, config.Kind);
        Assert.True(config.Enabled);
        Assert.Equal([new GeneratorTone(1000.0, -6.0)], config.Tones);
    }

    [Fact]
    public void TryMapChannel_Multitone_DerivesTheCombAndKeepsTheParameters()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true,
            Tones: null,
            Kind: "Multitone",
            Multitone: new MultitoneRequest(TonesPerOctave: 3, TotalRmsDbv: -30.0, RolloffDbPerOctave: 6.0));

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out var config, out _));

        Assert.Equal(GeneratorKind.Multitone, config.Kind);
        Assert.Equal(3, config.Multitone!.TonesPerOctave);
        Assert.Equal(-30.0, config.Multitone.TotalRmsDbv);
        Assert.Equal(6.0, config.Multitone.RolloffDbPerOctave);

        // Tones carries the derived comb so the device and the status response agree on what plays.
        Assert.Equal(MultitoneGenerator.Build(config.Multitone), config.Tones);
    }

    [Fact]
    public void TryMapChannel_OmittedFieldsFallBackToTheServerDefaults()
    {
        var request = new GeneratorChannelRequest(Enabled: true, Tones: null, Kind: "EchoChirp");

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Right", out var config, out _));

        var defaults = new EchoChirpSettings();
        Assert.Equal(defaults, config.Chirp);
    }

    [Fact]
    public void TryMapChannel_ParsesSmoothingCaseInsensitively()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "echochirp",
            Chirp: new EchoChirpRequest(AmplitudeDbv: -20.0, StartFreqHz: 20.0, EndFreqHz: 20_000.0, Smoothing: "oneover24"));

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out var config, out _));

        Assert.Equal(GeneratorKind.EchoChirp, config.Kind);
        Assert.Equal(SmoothingOctaves.OneOver24, config.Chirp!.Smoothing);
    }

    [Theory]
    [InlineData("Sawtooth", "Valid values")]
    [InlineData("", "Valid values")]
    public void TryMapChannel_RejectsAnUnknownKind(string kind, string expectedFragment)
    {
        var request = new GeneratorChannelRequest(Enabled: true, Tones: null, Kind: kind);

        Assert.False(GeneratorEndpoints.TryMapChannel(request, "Left", out _, out var error));
        Assert.Contains(expectedFragment, error);
        Assert.StartsWith("Left channel:", error);
    }

    [Fact]
    public void TryMapChannel_RejectsATonesPerOctaveBelowOne()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "Multitone",
            Multitone: new MultitoneRequest(TonesPerOctave: 0, TotalRmsDbv: null, RolloffDbPerOctave: null));

        Assert.False(GeneratorEndpoints.TryMapChannel(request, "Right", out _, out var error));
        Assert.Contains("TonesPerOctave", error);
    }

    [Theory]
    [InlineData(0.0, 20_000.0)]
    [InlineData(-5.0, 20_000.0)]
    [InlineData(1000.0, 1000.0)]
    [InlineData(1000.0, 500.0)]
    public void TryMapChannel_RejectsADegenerateChirpBand(double startFreqHz, double endFreqHz)
    {
        // An exponential sweep divides by ln(f2/f1), so these bands have no sweep at all.
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "EchoChirp",
            Chirp: new EchoChirpRequest(AmplitudeDbv: null, StartFreqHz: startFreqHz, EndFreqHz: endFreqHz, Smoothing: null));

        Assert.False(GeneratorEndpoints.TryMapChannel(request, "Left", out _, out var error));
        Assert.Contains("EchoChirp", error);
    }

    [Fact]
    public async Task GetStatus_ReportsEveryKindsSettingsBlock()
    {
        var session = new DeviceSessionService(
            new SimulatedDevice(),
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var noise = new WhiteNoiseSettings { LevelDbv = -33.0 };
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.WhiteNoise, Noise = noise },
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var status = session.GetStatus();

        Assert.Equal(GeneratorKind.WhiteNoise, status.GeneratorLeft.Kind);
        Assert.Equal(-33.0, status.GeneratorLeft.Noise.LevelDbv);

        // Blocks the channel has never used are still populated, with the server's defaults, so the
        // UI can seed all of its forms from one status read.
        Assert.Equal(new MultitoneSettings().TotalRmsDbv, status.GeneratorLeft.Multitone.TotalRmsDbv);
        Assert.Equal(new EchoChirpSettings().AmplitudeDbv, status.GeneratorRight.Chirp.AmplitudeDbv);
    }
}
