using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The four stimulus kinds added for bench-scope work — sawtooth, pulse, tone burst and DC — where
/// they meet the session and the endpoint. The waveforms themselves are pinned in
/// <c>Tonebench.Device.Tests</c>; what is here is the seam: what the app <b>reports</b> about a
/// stimulus it has quantised, and what it refuses before anything reaches a DAC.
/// </summary>
public class NewStimulusKindsTests
{
    private const double SampleRateHz = 48_000.0;

    private static async Task<DeviceSessionService> ConnectedSessionAsync()
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(
            new SimulatedDevice(), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);
        return session;
    }

    private static GeneratorConfig Left(ChannelGeneratorConfig channel) =>
        new(channel, ChannelGeneratorConfig.Off) { SnapToBin = false };

    private static async Task<DeviceStatusResponse> PlayAsync(ChannelGeneratorConfig channel)
    {
        var session = await ConnectedSessionAsync();
        await session.ConfigureGeneratorAsync(
            Left(channel), CancellationToken.None, new AcquisitionConfig(SampleRateHz, 2, 4096));
        return session.GetStatus();
    }

    // ------------------------------------------------------------------- pulse

    /// <summary>
    /// A duty is rounded to a whole sample like the frequency is, and like the frequency it is
    /// <b>reported back</b> rather than silently applied — at a short period the duties available
    /// are coarse, and a panel echoing what was typed would be describing a pulse that is not
    /// playing. 700 Hz is 68 frames here, so 30 % rounds to 20 frames, i.e. 29.41 %.
    /// </summary>
    [Fact]
    public async Task APulsesDutyIsResolvedToWholeSamples_AndStatusSaysSo()
    {
        var status = await PlayAsync(new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.Pulse,
            Wave = new PeriodicWaveSettings { FrequencyHz = 700.0, LevelDbv = -12.0, DutyPercent = 30.0 },
        });

        Assert.Equal(SampleRateHz / 68, status.GeneratorLeft.Wave.FreqHz, precision: 6);
        Assert.Equal(100.0 * 20 / 68, status.GeneratorLeft.Wave.DutyPercent, precision: 6);
    }

    /// <summary>
    /// And the effective tone carries the fundamental a <i>narrow</i> pulse really has, which is
    /// far below the level that was typed: everything downstream reads that list for "the
    /// fundamental", so the square's figure there would name a line 10 dB above the one in the
    /// spectrum.
    /// </summary>
    [Fact]
    public async Task ANarrowPulsesEffectiveTone_IsBelowItsOwnLevel()
    {
        var status = await PlayAsync(new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.Pulse,
            Wave = new PeriodicWaveSettings { FrequencyHz = 1000.0, LevelDbv = -12.0, DutyPercent = 10.0 },
        });

        int period = PeriodicWave.PeriodFrames(1000.0, SampleRateHz);
        int width = PeriodicWave.WidthFrames(10.0, period);
        var tone = Assert.Single(status.GeneratorLeft.Tones);

        Assert.Equal(PeriodicWave.FundamentalDbv(GeneratorKind.Pulse, -12.0, period, width), tone.AmpDbv, precision: 9);

        double square = PeriodicWave.FundamentalDbv(GeneratorKind.Square, -12.0, period);
        Assert.True(tone.AmpDbv < square - 9.0, $"{tone.AmpDbv} is not well below the square's {square}");
    }

    // -------------------------------------------------------------- tone burst

    /// <summary>
    /// The burst's carrier goes through the shape family's quantiser, and the effective tone carries
    /// the carrier <i>line</i> a spectrum reads — the level times the duty — rather than the
    /// amplitude that was typed.
    /// </summary>
    [Fact]
    public async Task ABurstsCarrierIsQuantised_AndItsToneIsTheLineASpectrumReads()
    {
        var burst = new ToneBurstSettings
        {
            FrequencyHz = 700.0,
            LevelDbv = -12.0,
            CyclesOn = 5,
            CyclesOff = 15,
        };

        var status = await PlayAsync(new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.ToneBurst, Burst = burst });

        double expectedHz = SampleRateHz / 68;
        Assert.Equal(expectedHz, status.GeneratorLeft.Burst.FreqHz, precision: 6);

        var tone = Assert.Single(status.GeneratorLeft.Tones);
        Assert.Equal(expectedHz, tone.FreqHz, precision: 6);

        // 5 on of 20 is a quarter, i.e. 12.04 dB below the carrier's own level.
        Assert.Equal(-12.0 + 20.0 * Math.Log10(0.25), tone.AmpDbv, precision: 6);
    }

    // ---------------------------------------------------------------------- DC

    /// <summary>
    /// A DC level reaches the device as a constant, and the range it asks for is its own magnitude
    /// rather than an rms converted as if it were a sine.
    /// </summary>
    [Fact]
    public async Task ADcLevelPlaysAsAConstant()
    {
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.DcLevel,
            Dc = new DcLevelSettings { Volts = -0.75 },
        };

        var status = await PlayAsync(channel);
        Assert.Equal(-0.75, status.GeneratorLeft.Dc.Volts, precision: 9);
        Assert.Equal(0.75, OutputRangeSelector.RequiredPeakVolts(channel), precision: 9);
    }

    /// <summary>
    /// And it has no period to be wrong about, so it is not a periodic stimulus — which is what
    /// keeps the two-rate pairing check from refusing a DC level on a split analyzer.
    /// </summary>
    [Fact]
    public async Task ADcLevelIsNotAPeriodicStimulus()
    {
        var status = await PlayAsync(new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.DcLevel,
            Dc = new DcLevelSettings { Volts = 0.25 },
        });

        // Nothing to report as a tone: a constant has no line.
        Assert.Empty(status.GeneratorLeft.Tones);
    }

    /// <summary>
    /// <b>A measurement saved before the duty existed must open on the default, not on zero.</b>
    ///
    /// <para>The trap is in the serializer rather than in this feature: the app puts
    /// <c>AppJsonContext.Default</c> first in its resolver chain, and the source-generated resolver
    /// leaves an unset <c>init</c> property at the type's <c>default</c> instead of at the value its
    /// initializer gives it — where plain reflection-based deserialization of the same document
    /// answers 50. So every square and triangle in every existing project reads back as a 0 % duty,
    /// which the 1 % floor would turn into a sliver of a pulse the moment the kind was switched.
    /// Caught in the running app against a real stored measurement, not reasoned about.</para>
    ///
    /// <para>Note what this says about adding a field to any settings record here: the default in
    /// the initializer does <b>not</b> reach a document written before it.</para>
    /// </summary>
    [Fact]
    public void AStoredWaveWithNoDuty_ComesBackOnTheDefaultRatherThanZero()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, Tonebench.App.Json.AppJsonContext.Default);

        var stored = JsonSerializer.Deserialize<ChannelGeneratorConfig>(
            """{"enabled":true,"tones":[],"kind":"Square","wave":{"frequencyHz":1000,"levelDbv":-20}}""",
            options);

        // The document really does lose it, which is the half worth pinning: if this ever starts
        // answering 50 the rule below has become belt and braces rather than the fix.
        Assert.Equal(0.0, stored!.Wave!.DutyPercent);

        // And the boundary a stored document comes back across puts it right.
        var response = DeviceSessionService.ToResponse(stored);
        Assert.Equal(new PeriodicWaveSettings().DutyPercent, response.Wave.DutyPercent);

        // As does the width itself, for a caller holding the settings directly.
        Assert.Equal(32, PeriodicWave.WidthFrames(stored.Wave.DutyPercent, 64));
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void TryMapChannel_MapsEachNewBlock()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "pulse",
            Wave: new PeriodicWaveRequest(FreqHz: 250.0, LevelDbv: -9.0, DutyPercent: 20.0),
            Burst: new ToneBurstRequest(FreqHz: 400.0, LevelDbv: -3.0, CyclesOn: 2, CyclesOff: 8),
            Dc: new DcLevelRequest(Volts: 1.5));

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out var config, out _));

        Assert.Equal(GeneratorKind.Pulse, config.Kind);
        Assert.Equal(20.0, config.Wave!.DutyPercent);
        Assert.Equal(400.0, config.Burst!.FrequencyHz);
        Assert.Equal(2, config.Burst.CyclesOn);
        Assert.Equal(1.5, config.Dc!.Volts);
    }

    /// <summary>
    /// The duty is refused rather than clamped, unlike the frequency: the frequency's ceiling is
    /// fs/4 and the rate is not known at the endpoint, so clamping is the only honest answer there —
    /// while a duty outside 1..99 % needs no rate to be wrong.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(100.0)]
    [InlineData(-5.0)]
    public void TryMapChannel_RefusesADutyOutsideItsRange(double duty)
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "Pulse",
            Wave: new PeriodicWaveRequest(FreqHz: 1000.0, LevelDbv: -9.0, DutyPercent: duty));

        Assert.False(GeneratorEndpoints.TryMapChannel(request, "Right", out _, out var error));
        Assert.Contains("DutyPercent", error);
        Assert.StartsWith("Right channel:", error);
    }

    [Theory]
    [InlineData(0, 10, "CyclesOn")]
    [InlineData(5, -1, "CyclesOff")]
    public void TryMapChannel_RefusesACycleCountOutsideItsRange(int on, int off, string named)
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "ToneBurst",
            Burst: new ToneBurstRequest(FreqHz: 1000.0, LevelDbv: -9.0, CyclesOn: on, CyclesOff: off));

        Assert.False(GeneratorEndpoints.TryMapChannel(request, "Left", out _, out var error));
        Assert.Contains(named, error);
    }

    /// <summary>
    /// Zero cycles off is legal and is the continuous tone a reader compares the burst against —
    /// the one case in that range check that has to be allowed through.
    /// </summary>
    [Fact]
    public void TryMapChannel_AllowsABurstWithNothingOff()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: null, Kind: "ToneBurst",
            Burst: new ToneBurstRequest(FreqHz: 1000.0, LevelDbv: -9.0, CyclesOn: 3, CyclesOff: 0));

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out var config, out _));
        Assert.Equal(0, config.Burst!.CyclesOff);
    }

    /// <summary>
    /// And each block is validated only for the kind that reads it: an untouched default must never
    /// block a stimulus that never looks at it — the rule the impedance request's two settings
    /// groups already follow, and the one a fourth block makes easiest to forget.
    /// </summary>
    [Fact]
    public void TryMapChannel_IgnoresTheBlocksThisKindDoesNotRead()
    {
        var request = new GeneratorChannelRequest(
            Enabled: true, Tones: [new GeneratorToneRequest(1000.0, -6.0)], Kind: "Tones",
            Wave: new PeriodicWaveRequest(FreqHz: 0.0, LevelDbv: -9.0, DutyPercent: 500.0),
            Burst: new ToneBurstRequest(FreqHz: 0.0, LevelDbv: -9.0, CyclesOn: 0, CyclesOff: -3),
            Dc: new DcLevelRequest(Volts: double.NaN));

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out _, out var error));
        Assert.Equal("", error);
    }

    /// <summary>
    /// A level box can be left empty, and every new block falls back to its record's own default
    /// rather than to zero — the shape of the whole request: a client only sends the kind it
    /// selected.
    /// </summary>
    [Fact]
    public void TryMapChannel_FallsBackToTheSettingsDefaults()
    {
        var request = new GeneratorChannelRequest(Enabled: true, Tones: null, Kind: "Sawtooth");

        Assert.True(GeneratorEndpoints.TryMapChannel(request, "Left", out var config, out _));

        var wave = new PeriodicWaveSettings();
        var burst = new ToneBurstSettings();
        Assert.Equal(wave.FrequencyHz, config.Wave!.FrequencyHz);
        Assert.Equal(wave.DutyPercent, config.Wave.DutyPercent);
        Assert.Equal(burst.CyclesOn, config.Burst!.CyclesOn);
        Assert.Equal(0.0, config.Dc!.Volts);
    }
}
