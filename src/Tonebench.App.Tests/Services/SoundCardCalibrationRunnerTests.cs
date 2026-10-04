using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Device.SoundCard;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The calibration steps' rules. The capture itself needs a card, so what is pinned here is the
/// refusals and the arithmetic — which is where a wrong answer would look like a right one.
/// </summary>
public class SoundCardCalibrationRunnerTests
{
    private sealed class AnyKindFactory(DeviceKind initialKind, SoundCardSetting soundCard)
        : AnalyzerDeviceFactory(NullLoggerFactory.Instance, initialKind, soundCard)
    {
        public override IAnalyzerDevice Create(DeviceKind kind) => new SimulatedDevice();
    }

    private static (SoundCardCalibrationRunner Runner, SoundCardSetting Card, DeviceSessionService Session,
        SpectrumPipeline Pipeline) Build(
        DeviceKind kind = DeviceKind.SoundCard, double? outputFullScaleMv = null)
    {
        var card = new SoundCardSetting
        {
            Config = SoundCardConfig.Default with
            {
                InputHostApi = "ASIO",
                InputDeviceName = "Card",
                OutputHostApi = "ASIO",
                OutputDeviceName = "Card",
                OutputFullScalePeakMv = outputFullScaleMv,
            },
        };

        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(
            new AnyKindFactory(kind, card),
            pipeline,
            new CaptureDelaySetting(),
            NullLogger<DeviceSessionService>.Instance,
            new ExternalGainSetting(),
            card);

        var runner = new SoundCardCalibrationRunner(
            session, pipeline, card, null!, NullLogger<SoundCardCalibrationRunner>.Instance);

        return (runner, card, session, pipeline);
    }

    /// <summary>
    /// The whole safety of the loopback source: the known voltage comes from the output's own full
    /// scale, so without one the result would look measured and be invented.
    /// </summary>
    [Fact]
    public async Task Loopback_WhileTheOutputIsUncalibrated_IsRefused()
    {
        var (runner, _, session, pipeline) = Build(outputFullScaleMv: null);
        await session.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MeasureInputViaLoopbackAsync(-20.0, CancellationToken.None));

        Assert.Contains("Calibrate the output first", ex.Message);
    }

    [Fact]
    public async Task Loopback_AtALevelTheDialogDoesNotOffer_IsRefused()
    {
        var (runner, _, session, pipeline) = Build(outputFullScaleMv: 2000.0);
        await session.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => runner.MeasureInputViaLoopbackAsync(-7.0, CancellationToken.None));
    }

    [Fact]
    public async Task EveryStep_OnAnAnalyzerThatIsNotASoundCard_IsRefused()
    {
        var (runner, _, session, pipeline) = Build(DeviceKind.Simulated, outputFullScaleMv: 2000.0);
        await session.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.StartOutputToneAsync(-6.0, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MeasureInputViaLoopbackAsync(-20.0, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MeasureInputAsync(500.0, VoltageEntryUnit.Rms, StreamChannel.Left, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyingAnOutputReading_BeforeAToneIsPlaying_IsRefused()
    {
        var (runner, _, session, pipeline) = Build();
        await session.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ApplyOutputAsync(1000.0, VoltageEntryUnit.Rms, CancellationToken.None));

        Assert.Contains("Start the calibration tone", ex.Message);
    }

    [Fact]
    public void Nothing_IsRunningBeforeAStepStarts()
    {
        var (runner, _, _, _) = Build();

        Assert.False(runner.Running);
        Assert.Null(runner.PlayingDbFs);
    }

    /// <summary>
    /// A step analyses at its <i>own</i> record length, and hands the screen's back afterwards.
    ///
    /// <para>This is what made the input step fail intermittently: these steps read one peak, and
    /// how long that takes is one record — so inheriting the measurement screen's FFT size made a
    /// calibration's latency a property of a setting on an unrelated screen. The pipeline's own
    /// default is 65536 points, 1.37 s at 48 kHz, and a bench left at 131072 pays 2.73 s, against a
    /// budget that then had to cover a 3 s re-clock settle as well. See
    /// <c>SoundCardCalibrationRunner.CalibrationFftSize</c> and <c>ReadBudget</c>.</para>
    ///
    /// <para>Both halves matter: a step that pinned the size and did not put it back would leave an
    /// FFT screen measuring at 16384 points with its own dropdown saying something else.</para>
    /// </summary>
    [Fact]
    public async Task AStep_AnalysesAtItsOwnRecordLength_AndHandsTheScreensBack()
    {
        var (runner, _, session, pipeline) = Build();
        await session.ConnectAsync(CancellationToken.None);

        pipeline.Options = pipeline.Options with { FftSize = 131072, ProcessRight = false };

        await runner.StartOutputToneAsync(-6.0, CancellationToken.None);
        Assert.Equal(SoundCardCalibrationRunner.CalibrationFftSize, pipeline.Options.FftSize);

        // Both channels, so the dialog's two level bars both move -- the pipeline's default is
        // left only, which is the FFT screen's.
        Assert.True(pipeline.Options.ProcessRight);

        await runner.CancelAsync();

        Assert.Equal(131072, pipeline.Options.FftSize);
        Assert.False(pipeline.Options.ProcessRight);
    }

    /// <summary>
    /// The external-source step tidies up after a step that <b>threw</b>, which it did not.
    ///
    /// <para>It had no teardown at all — no options restore, no <c>SilenceAsync</c> — so a step
    /// refused for a clipped or a silent source left the screen's <c>ProcessRight</c> switched on,
    /// left this runner's FFT size in place of the screen's, and left the acquisition it started
    /// running. The last is the one that bites: a session left acquiring keeps the browser's own
    /// ranging watcher live, which then posts an input range into the next step — and refuses the
    /// card's control panel for the rest of the session. The loopback step always had this; this
    /// one is now the same shape.</para>
    ///
    /// <para>Driven through the failure path on purpose: with the generator silenced (which this
    /// step does, since the source is external) the simulator presents its own noise floor, which
    /// is below the silence guard, so the step refuses — exactly the case that leaked.</para>
    /// </summary>
    [Fact]
    public async Task TheExternalSourceStep_ThatIsRefused_StillPutsEverythingBack()
    {
        var (runner, _, session, pipeline) = Build(outputFullScaleMv: 2000.0);
        await session.ConnectAsync(CancellationToken.None);

        pipeline.Options = pipeline.Options with { FftSize = 32768, ProcessRight = false };
        SpectrumPipelineOptions before = pipeline.Options;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MeasureInputAsync(1000.0, VoltageEntryUnit.Rms, StreamChannel.Left, CancellationToken.None));

        Assert.Equal(before.FftSize, pipeline.Options.FftSize);
        Assert.Equal(before.ProcessRight, pipeline.Options.ProcessRight);
        Assert.False(session.Acquiring);
        Assert.False(runner.Running);
    }

    /// <summary>
    /// The arithmetic the loopback source rests on: what the card plays is its output full scale
    /// scaled by the level, and the input's full scale is that voltage scaled back up by however far
    /// below full scale it arrived. Worked here rather than only inside the runner, because a sign
    /// or a direction wrong here is a calibration that is confidently off by the level played.
    /// </summary>
    [Theory]
    // Played 20 dB down and arrived 20 dB down: no loss, so the input's full scale equals the output's.
    [InlineData(-20.0, -20.0, 2000.0)]
    // Arrived 6 dB *quieter* than it left, so the input is the less sensitive side: it takes twice
    // the voltage to reach its full scale, not half. (Getting this backwards is exactly the error
    // this case exists to catch — it did catch it while being written.)
    [InlineData(-20.0, -26.0, 3990.5)]
    // The same 6 dB of loss at a different playback level must give the same answer: the figure
    // describes the input, not the level it was measured at.
    [InlineData(-6.0, -12.0, 3990.5)]
    public void LoopbackArithmetic_ScalesTheKnownVoltageByTheLossMeasured(
        double playedDbFs, double measuredDbFs, double expectedInputFullScaleMv)
    {
        const double outputFullScaleV = 2.0;

        double knownPeakV = outputFullScaleV * Math.Pow(10.0, playedDbFs / 20.0);
        double inputFullScaleV = SoundCardCalibration.InputFullScaleFromMeasurement(knownPeakV, measuredDbFs);

        Assert.Equal(expectedInputFullScaleMv, inputFullScaleV * 1000.0, 0.5);
    }

    /// <summary>
    /// A perfect loopback on a card whose two sides are equal reads the output's own full scale
    /// back, whatever level it was played at — the property that makes this step self-consistent.
    /// </summary>
    [Theory]
    [InlineData(-3.0)]
    [InlineData(-6.0)]
    [InlineData(-20.0)]
    public void LoopbackArithmetic_ThroughALosslessCable_RecoversTheOutputsFullScale(double playedDbFs)
    {
        const double outputFullScaleV = 3.342;

        double knownPeakV = outputFullScaleV * Math.Pow(10.0, playedDbFs / 20.0);

        // A lossless cable into an input of the same full scale arrives at exactly the level played.
        double inputFullScaleV = SoundCardCalibration.InputFullScaleFromMeasurement(knownPeakV, playedDbFs);

        Assert.Equal(outputFullScaleV, inputFullScaleV, 1e-9);
    }
}
