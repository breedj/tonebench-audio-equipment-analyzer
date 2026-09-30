using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Device.SoundCard;

namespace Qa403.App.Tests.Services;

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

    private static (SoundCardCalibrationRunner Runner, SoundCardSetting Card, DeviceSessionService Session) Build(
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

        return (runner, card, session);
    }

    /// <summary>
    /// The whole safety of the loopback source: the known voltage comes from the output's own full
    /// scale, so without one the result would look measured and be invented.
    /// </summary>
    [Fact]
    public async Task Loopback_WhileTheOutputIsUncalibrated_IsRefused()
    {
        var (runner, _, session) = Build(outputFullScaleMv: null);
        await session.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MeasureInputViaLoopbackAsync(-20.0, StreamChannel.Left, CancellationToken.None));

        Assert.Contains("Calibrate the output first", ex.Message);
    }

    [Fact]
    public async Task Loopback_AtALevelTheDialogDoesNotOffer_IsRefused()
    {
        var (runner, _, session) = Build(outputFullScaleMv: 2000.0);
        await session.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => runner.MeasureInputViaLoopbackAsync(-7.0, StreamChannel.Left, CancellationToken.None));
    }

    [Fact]
    public async Task EveryStep_OnAnAnalyzerThatIsNotASoundCard_IsRefused()
    {
        var (runner, _, session) = Build(DeviceKind.Simulated, outputFullScaleMv: 2000.0);
        await session.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.StartOutputToneAsync(-6.0, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MeasureInputViaLoopbackAsync(-20.0, StreamChannel.Left, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MeasureInputAsync(500.0, VoltageEntryUnit.Rms, StreamChannel.Left, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyingAnOutputReading_BeforeAToneIsPlaying_IsRefused()
    {
        var (runner, _, session) = Build();
        await session.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ApplyOutputAsync(1000.0, VoltageEntryUnit.Rms, CancellationToken.None));

        Assert.Contains("Start the calibration tone", ex.Message);
    }

    [Fact]
    public void Nothing_IsRunningBeforeAStepStarts()
    {
        var (runner, _, _) = Build();

        Assert.False(runner.Running);
        Assert.Null(runner.PlayingDbFs);
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
