using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The square/triangle stimulus where it meets the session: what the quantised period does to the
/// frequency the app reports, and the fact that bin snapping must not touch it. The waveform itself
/// is pinned in <c>Tonebench.Device.Tests.PeriodicWaveTests</c>.
/// </summary>
public class PeriodicWaveStimulusTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>
    /// The pipeline comes back with the session because two of these tests need its FFT size: that
    /// is what bin snapping is relative to, and it is the thing a quantised wave must not be moved
    /// onto.
    /// </summary>
    private static async Task<(DeviceSessionService Session, SpectrumPipeline Pipeline)> ConnectedSessionAsync()
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(
            new SimulatedDevice(), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);
        return (session, pipeline);
    }

    private static GeneratorConfig Config(GeneratorKind kind, double freqHz, double levelDbv, bool snapToBin) =>
        new(
            new ChannelGeneratorConfig(true, [])
            {
                Kind = kind,
                Wave = new PeriodicWaveSettings { FrequencyHz = freqHz, LevelDbv = levelDbv },
            },
            ChannelGeneratorConfig.Off)
        {
            SnapToBin = snapToBin,
        };

    /// <summary>
    /// 700 Hz cannot be played at 48 kHz: the nearest whole even period is 68 frames, i.e.
    /// 705.88 Hz. The app has to say so — both on the wave block the device synthesizes from and on
    /// the effective tone list the UI reads — rather than echo back what was asked for.
    /// </summary>
    [Fact]
    public async Task ThePlayedFrequencyIsTheQuantisedOne_AndStatusSaysSo()
    {
        var (session, pipeline) = await ConnectedSessionAsync();
        await session.ConfigureGeneratorAsync(
            Config(GeneratorKind.Square, 700.0, -12.0, snapToBin: false),
            CancellationToken.None,
            new AcquisitionConfig(SampleRateHz, 2, 4096));

        var left = session.GetStatus().GeneratorLeft;
        double expectedHz = SampleRateHz / 68;

        Assert.Equal(expectedHz, left.Wave.FreqHz, precision: 6);
        Assert.Equal(expectedHz, Assert.Single(left.Tones).FreqHz, precision: 6);
    }

    /// <summary>
    /// The effective tone list carries the <i>fundamental's</i> level, not the waveform's total rms:
    /// 0.91 dB down for a square (plus the sampled series' own small correction, which is why this
    /// asserts against PeriodicWave rather than a constant), 0.06 dB for a triangle. Everything
    /// downstream reads that list for "the fundamental" — the scope trigger, the Gen tile, the gain
    /// reading — so a total rms there would report a line that is not in the spectrum.
    /// </summary>
    [Theory]
    [InlineData(GeneratorKind.Square)]
    [InlineData(GeneratorKind.Triangle)]
    public async Task TheEffectiveToneCarriesTheFundamentalsLevel(GeneratorKind kind)
    {
        var (session, pipeline) = await ConnectedSessionAsync();
        await session.ConfigureGeneratorAsync(
            Config(kind, 1000.0, -12.0, snapToBin: false),
            CancellationToken.None,
            new AcquisitionConfig(SampleRateHz, 2, 4096));

        var tone = Assert.Single(session.GetStatus().GeneratorLeft.Tones);
        int periodFrames = PeriodicWave.PeriodFrames(1000.0, SampleRateHz);

        Assert.Equal(PeriodicWave.FundamentalDbv(kind, -12.0, periodFrames), tone.AmpDbv, precision: 9);
        Assert.True(tone.AmpDbv < -12.0, "the fundamental holds less than the whole waveform");
    }

    /// <summary>
    /// Snapping rewrites a tone to the nearest coherent bin, and these kinds' tone list is a
    /// <i>reading</i> of a waveform the device has already committed to — so snapping it would make
    /// status report a frequency the DAC is not playing.
    /// </summary>
    [Fact]
    public async Task BinSnappingLeavesAQuantisedWaveAlone()
    {
        var (session, pipeline) = await ConnectedSessionAsync();
        await session.ConfigureGeneratorAsync(
            Config(GeneratorKind.Square, 700.0, -12.0, snapToBin: true),
            CancellationToken.None,
            new AcquisitionConfig(SampleRateHz, 2, 4096));

        double playedHz = SampleRateHz / 68;
        int fftSize = pipeline.Options.FftSize;

        // The test only means something if snapping would have moved it, so say that out loud.
        double snappedHz = CoherentFrequency.Nearest(playedHz, SampleRateHz, fftSize).FrequencyHz;
        Assert.NotEqual(playedHz, snappedHz, tolerance: 0.01);

        Assert.Equal(playedHz, Assert.Single(session.GetStatus().GeneratorLeft.Tones).FreqHz, precision: 6);
    }

    /// <summary>
    /// A tone list is still snapped, i.e. the gate above is on the kind and not a blanket switch-off.
    /// </summary>
    [Fact]
    public async Task BinSnappingStillAppliesToSineTones()
    {
        var (session, pipeline) = await ConnectedSessionAsync();
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(700.0, -12.0)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None,
            new AcquisitionConfig(SampleRateHz, 2, 4096));

        double expectedHz = CoherentFrequency
            .Nearest(700.0, SampleRateHz, pipeline.Options.FftSize)
            .FrequencyHz;
        Assert.NotEqual(700.0, expectedHz, tolerance: 0.01);
        Assert.Equal(expectedHz, Assert.Single(session.GetStatus().GeneratorLeft.Tones).FreqHz, precision: 6);
    }

    [Fact]
    public void TryMapChannel_MapsTheWaveBlock()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "square",
            Wave: new PeriodicWaveRequest(FreqHz: 250.0, LevelDbv: -9.0));

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out var config, out _));

        Assert.Equal(GeneratorKind.Square, config.Kind);
        Assert.Equal(250.0, config.Wave!.FrequencyHz);
        Assert.Equal(-9.0, config.Wave.LevelDbv);
    }

    [Fact]
    public void TryMapChannel_RejectsAWaveWithNoFrequency()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "Triangle",
            Wave: new PeriodicWaveRequest(FreqHz: 0.0, LevelDbv: -9.0));

        Assert.False(GeneratorEndpoints.TryMapChannel(request, "Right", out _, out var error));
        Assert.Contains("FreqHz", error);
        Assert.StartsWith("Right channel:", error);
    }

    /// <summary>
    /// And only for the kinds that read it: a frequency of zero left behind in an untouched wave
    /// block must not refuse a multitone run that never looks at it — the rule the impedance
    /// request's two settings groups already follow.
    /// </summary>
    [Fact]
    public void TryMapChannel_IgnoresAnUnusedWaveBlock()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: [new GeneratorToneRequest(1000.0, -6.0)], Kind: "Tones",
            Wave: new PeriodicWaveRequest(FreqHz: 0.0, LevelDbv: -9.0));

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out _, out _));
    }
}
