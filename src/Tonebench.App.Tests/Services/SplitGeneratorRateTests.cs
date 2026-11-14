using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Device.Split;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The generator's own sample rate, on a bench whose two halves do not share one.
///
/// <para>Every rate-dependent <i>generator</i> quantity has to read it rather than the analysis
/// rate, and the sharpest case is a square wave: its period is a whole, even count of the
/// generator's frames, so at a 1:4 ratio the frequency the app reports is wrong by up to fourfold
/// if the wrong rate is used. Tone snapping is the opposite and sits two lines away — it snaps to
/// <i>analysis</i> bins, which is the whole reason tones survive two clocks.</para>
/// </summary>
public class SplitGeneratorRateTests
{
    private static (DeviceSessionService Session, SpectrumPipeline Pipeline) Build(double generatorRateHz)
    {
        var device = new SplitAnalyzerDevice(
            new SimulatedDevice(),
            new SimulatedDevice(),
            new SplitDeviceSettings { GeneratorSampleRateHz = generatorRateHz },
            NullLogger<SplitAnalyzerDevice>.Instance);

        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(
            AnalyzerDeviceFactory.Pinned(device),
            pipeline,
            new CaptureDelaySetting(),
            NullLogger<DeviceSessionService>.Instance,
            split: new SplitDeviceSetting { Config = new SplitDeviceConfig("Simulated", "Simulated", generatorRateHz) });

        return (session, pipeline);
    }

    private static GeneratorConfig Square(double frequencyHz) => new(
        new ChannelGeneratorConfig(Enabled: true, Tones: [])
        {
            Kind = GeneratorKind.Square,
            Wave = new PeriodicWaveSettings { FrequencyHz = frequencyHz, LevelDbv = -10.0 },
        },
        ChannelGeneratorConfig.Off);

    private static GeneratorConfig Tone(double frequencyHz) => new(
        new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(frequencyHz, -10.0)]),
        ChannelGeneratorConfig.Off);

    [Theory]
    [InlineData(48_000.0, 48_000.0, true)]
    [InlineData(48_000.0, 192_000.0, true)]
    [InlineData(192_000.0, 48_000.0, true)]
    [InlineData(96_000.0, 384_000.0, true)]
    [InlineData(44_100.0, 192_000.0, false)]
    [InlineData(96_000.0, 192_000.1, false)]
    [InlineData(0.0, 48_000.0, false)]
    [InlineData(double.NaN, 48_000.0, false)]
    public void APeriodicStimulusNeedsAnIntegerRatio(double aHz, double bHz, bool expected) =>
        Assert.Equal(expected, DeviceSessionService.IsPeriodicRatePair(aHz, bHz));

    [Fact]
    public async Task AWavesPeriod_IsQuantisedOnTheGeneratorsRate_NotTheAnalysisRate()
    {
        // 10 kHz on a 48 kHz DAC has to land on a whole, even number of frames -- 4, i.e. 12 kHz --
        // where the same request against a 192 kHz capture would land on 20 frames, i.e. 9.6 kHz.
        // Reading the analysis rate here reports a frequency the DAC is not playing.
        (DeviceSessionService session, _) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(
            Square(10_000.0), CancellationToken.None,
            new AcquisitionConfig(192_000.0, 2, 4096));

        double played = Assert.Single(session.GetStatus().GeneratorLeft.Tones).FreqHz;
        Assert.Equal(PeriodicWave.EffectiveFrequencyHz(10_000.0, 48_000.0), played, precision: 6);
        Assert.NotEqual(PeriodicWave.EffectiveFrequencyHz(10_000.0, 192_000.0), played, precision: 6);
    }

    [Fact]
    public async Task ATone_IsSnappedToTheAnalysisBins_NotTheGeneratorsRate()
    {
        // The line right after the one above, and it wants the other rate. A synthesizer computing
        // from a frame index plays any frequency exactly, so the thing worth getting right is that
        // the tone lands on an analysis bin -- which is why tones are robust across two clocks.
        (DeviceSessionService session, SpectrumPipeline pipeline) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(
            Tone(1000.0), CancellationToken.None, new AcquisitionConfig(192_000.0, 2, 4096));

        double played = Assert.Single(session.GetStatus().GeneratorLeft.Tones).FreqHz;
        Assert.Equal(
            CoherentFrequency.Nearest(1000.0, 192_000.0, pipeline.Options.FftSize).FrequencyHz,
            played, precision: 6);
    }

    [Fact]
    public async Task ARateTheGeneratingHalfCannotPlay_IsRefused_AndNamesWhatItCan()
    {
        // Reachable only on a composite: the two rate ladders are deliberately not intersected, so
        // a capture-only run at a rate the output side has never heard of is legitimate and asking
        // it to *play* that rate is not.
        (DeviceSessionService session, _) = Build(generatorRateHz: 77_000.0);
        await session.ConnectAsync(CancellationToken.None);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ConfigureGeneratorAsync(
                Tone(1000.0), CancellationToken.None, new AcquisitionConfig(192_000.0, 2, 4096)));

        Assert.Contains("cannot run at 77 kHz", ex.Message);
        Assert.Contains("48", ex.Message);
    }

    [Fact]
    public async Task AChirp_OnABenchWhoseHalvesRunAtDifferentRates_IsAllowed()
    {
        // It was refused for a day, while the reference was still built at the analysis rate. Now
        // the stimulus is built at the rate it is played at and expressed on the analysis grid --
        // see TwoRateStimulusTests, which is where that is pinned -- so the session has no business
        // standing in the way of it.
        (DeviceSessionService session, _) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);

        var chirp = new GeneratorConfig(
            new ChannelGeneratorConfig(Enabled: true, Tones: []) { Kind = GeneratorKind.EchoChirp },
            ChannelGeneratorConfig.Off);

        await session.ConfigureGeneratorAsync(chirp, CancellationToken.None, new AcquisitionConfig(192_000.0, 2, 4096));

        Assert.Equal(GeneratorKind.EchoChirp, session.EffectiveGeneratorConfig.Left.Kind);
    }

    [Fact]
    public async Task AToneOrAWave_OnThatSameBench_IsNotRefused()
    {
        // The other half of the rule: a tone is snapped to an analysis bin and played exactly from a
        // frame index, and a wave is quantised onto the generator's own frames and reported at what
        // it really plays. Neither needs the two rates to match.
        (DeviceSessionService session, _) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(
            Tone(1000.0), CancellationToken.None, new AcquisitionConfig(192_000.0, 2, 4096));
        await session.ConfigureGeneratorAsync(
            Square(1000.0), CancellationToken.None, new AcquisitionConfig(192_000.0, 2, 4096));

        Assert.True(session.EffectiveGeneratorConfig.Left.Enabled);
    }

    [Fact]
    public async Task AChirp_OnABenchWithOneRate_IsFine()
    {
        (DeviceSessionService session, _) = Build(generatorRateHz: 0.0);
        await session.ConnectAsync(CancellationToken.None);

        var chirp = new GeneratorConfig(
            new ChannelGeneratorConfig(Enabled: true, Tones: []) { Kind = GeneratorKind.EchoChirp },
            ChannelGeneratorConfig.Off);

        await session.ConfigureGeneratorAsync(chirp, CancellationToken.None, new AcquisitionConfig(192_000.0, 2, 4096));

        Assert.Equal(GeneratorKind.EchoChirp, session.EffectiveGeneratorConfig.Left.Kind);
    }

    [Fact]
    public async Task SilencingTheGenerator_AtARateItCannotPlay_StillSucceeds()
    {
        // Stop and every measurement switch disable both channels unconditionally, and that must
        // never fail -- the same rule the 384 kHz refusal follows.
        (DeviceSessionService session, _) = Build(generatorRateHz: 77_000.0);
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(
            GeneratorConfig.Off, CancellationToken.None, new AcquisitionConfig(192_000.0, 2, 4096));

        Assert.False(session.EffectiveGeneratorConfig.Left.Enabled);
    }

    /// <summary>
    /// What a measurement <i>request</i> is given, which is not the same question.
    ///
    /// <para>Found on the bench: a 96 kHz chirp started after a 48 kHz tone had its band capped at
    /// 19.2 kHz and its reference built at 48 kHz, because "follow the capture rate" fell back to
    /// the last rate anything had streamed at. A runner asks before its own stream exists, so the
    /// only honest answer there is zero.</para>
    /// </summary>
    [Fact]
    public async Task TheRateARequestCarries_IsZeroWhenTheGeneratorFollowsTheCapture()
    {
        (DeviceSessionService session, _) = Build(generatorRateHz: 0.0);
        await session.ConnectAsync(CancellationToken.None);

        // Something ran at 48 kHz, which is what used to leak into the next run's request.
        await session.ConfigureGeneratorAsync(
            Tone(1000.0), CancellationToken.None, new AcquisitionConfig(48_000.0, 2, 4096));

        Assert.Equal(0.0, session.GeneratorRateOverrideHz);

        // And a split that really does drive its generator at its own rate still says so.
        (DeviceSessionService split, _) = Build(generatorRateHz: 48_000.0);
        await split.ConnectAsync(CancellationToken.None);
        Assert.Equal(48_000.0, split.GeneratorRateOverrideHz);
    }

    [Fact]
    public async Task TheReportedGeneratorRate_IsTheGeneratingHalves()
    {
        (DeviceSessionService session, _) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);
        await session.ConfigureGeneratorAsync(
            GeneratorConfig.Off, CancellationToken.None, new AcquisitionConfig(192_000.0, 2, 4096));

        Assert.Equal(48_000.0, session.GeneratorSampleRateHz);

        // And on a bench with one clock it is the analysis rate, so nothing else in the app has to
        // know which kind of bench it is on.
        (DeviceSessionService single, _) = Build(generatorRateHz: 0.0);
        await single.ConnectAsync(CancellationToken.None);
        await single.ConfigureGeneratorAsync(
            GeneratorConfig.Off, CancellationToken.None, new AcquisitionConfig(96_000.0, 2, 4096));

        Assert.Equal(96_000.0, single.GeneratorSampleRateHz);
    }

    /// <summary>
    /// One channel playing <paramref name="kind"/>. <b>Every</b> settings object is filled whatever
    /// the kind, which is what the FFT screen's staged config really looks like — a chirp setting
    /// left behind by a previous stimulus does not go away — and which is why
    /// <c>PlaysAPeriodicStimulus</c> has to read the kind to pick the object that is in play rather
    /// than asking whether any object carries a period.
    /// </summary>
    private static GeneratorConfig Stimulus(GeneratorKind kind, int noisePeriodFrames = 4096) => new(
        new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1_000.0, -10.0)])
        {
            Kind = kind,
            Multitone = new MultitoneSettings(),
            Noise = new WhiteNoiseSettings { PeriodFrames = noisePeriodFrames },
            Chirp = new EchoChirpSettings { RepeatFrames = 4_096 },
            Wave = new PeriodicWaveSettings { FrequencyHz = 1_000.0, LevelDbv = -10.0 },
        },
        ChannelGeneratorConfig.Off);

    /// <summary>A 48 kHz generator against a 44.1 kHz capture: 160/147, the one pairing that cannot.</summary>
    private const double MixedFamilyAnalysisRateHz = 44_100.0;

    /// <summary>
    /// A stimulus carrying its own period, across rate families. Refused, because its period cannot
    /// be a whole number of frames at both ends — see <c>RequireGeneratorRateUsable</c> for the
    /// arithmetic.
    /// </summary>
    [Theory]
    [InlineData(GeneratorKind.EchoChirp)]
    [InlineData(GeneratorKind.PinkNoise)]
    [InlineData(GeneratorKind.WhiteNoise)]
    public async Task APeriodicStimulus_AcrossANonIntegerRatePair_IsRefused(GeneratorKind kind)
    {
        (DeviceSessionService session, _) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ConfigureGeneratorAsync(
                Stimulus(kind), CancellationToken.None,
                new AcquisitionConfig(MixedFamilyAnalysisRateHz, 2, 4096)));

        Assert.Contains("not an integer ratio", ex.Message);

        // And it names the lever, because "cannot line up with the analysis record" on its own
        // leaves a reader with nothing to do about it.
        Assert.Contains("stepped sine", ex.Message);
    }

    /// <summary>
    /// The regression this correction exists for: a stimulus with no period of its own is coherent
    /// in the record whatever rate synthesized it, so the same mixed-family bench still plays it.
    ///
    /// <para>A tone is the case that mattered — it is the first thing anyone tries on a 44.1 kHz
    /// DAC, and until 2026-11-14 it was refused by a message saying tones were unaffected.</para>
    /// </summary>
    [Theory]
    [InlineData(GeneratorKind.Tones)]
    [InlineData(GeneratorKind.Multitone)]
    [InlineData(GeneratorKind.Square)]
    [InlineData(GeneratorKind.Triangle)]
    public async Task AStimulusWithNoPeriodOfItsOwn_AcrossANonIntegerRatePair_StillPlays(GeneratorKind kind)
    {
        (DeviceSessionService session, _) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(
            Stimulus(kind), CancellationToken.None,
            new AcquisitionConfig(MixedFamilyAnalysisRateHz, 2, 4096));

        Assert.True(session.GetStatus().GeneratorLeft.Enabled);
    }

    /// <summary>
    /// The continuous noise forms have no period at all (<c>PeriodFrames</c> 0, the documented
    /// streaming case), so they are decided by that field and not by their kind — which is the
    /// whole reason the test is structural rather than a list of kinds.
    /// </summary>
    [Fact]
    public async Task ContinuousNoise_AcrossANonIntegerRatePair_StillPlays()
    {
        (DeviceSessionService session, _) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(
            Stimulus(GeneratorKind.PinkNoise, noisePeriodFrames: 0), CancellationToken.None,
            new AcquisitionConfig(MixedFamilyAnalysisRateHz, 2, 4096));

        Assert.True(session.GetStatus().GeneratorLeft.Enabled);
    }

    /// <summary>
    /// And the guard must not fire on a bench that is fine. Every rate pairing the app offered
    /// before the 44.1 kHz family was probed is an integer ratio, so this is what would break on
    /// every existing bench if the condition were got wrong.
    /// </summary>
    [Fact]
    public async Task APeriodicStimulus_AcrossAnIntegerRatePair_StillPlays()
    {
        (DeviceSessionService session, _) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(
            Stimulus(GeneratorKind.EchoChirp), CancellationToken.None,
            new AcquisitionConfig(192_000.0, 2, 4096));

        Assert.True(session.GetStatus().GeneratorLeft.Enabled);
    }

    /// <summary>
    /// Silence is always allowed, whatever the rates — Stop and every measurement switch disable
    /// both channels unconditionally and that must never fail.
    /// </summary>
    [Fact]
    public async Task Silence_AcrossANonIntegerRatePair_IsAlwaysAllowed()
    {
        (DeviceSessionService session, _) = Build(generatorRateHz: 48_000.0);
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(
            GeneratorConfig.Off, CancellationToken.None,
            new AcquisitionConfig(MixedFamilyAnalysisRateHz, 2, 4096));

        Assert.False(session.GetStatus().GeneratorLeft.Enabled);
    }
}
