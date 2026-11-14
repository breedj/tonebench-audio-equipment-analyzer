using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// A stepped point may only be believed once the audio it was taken for has actually arrived.
///
/// <para>Reported from the bench on 2026-11-14: a 53-point stepped response on a split analyzer
/// (a Topping D50 III generating at 44.1 kHz into a QA403 capturing at 96 kHz) drew a -140 dBV
/// notch at 2085.9375 Hz, with every point either side flat at -20 dBV. The recording of that run
/// shows the DAC playing that tone and the analyzer capturing it at full level, so neither was at
/// fault - what the recording also shows is that the point occupied <b>185 ms</b> of the run where
/// its neighbours took ~490, and the configured capture delay alone is 300 ms. The record the
/// point was handed therefore still held the previous tone, and reading the new tone's bin out of
/// it returned the noise floor.</para>
///
/// <para>These tests pin the rule rather than the race. Which interleaving let a wait be satisfied
/// by a pre-change window has not been established - the pipeline has one worker and sequential
/// ticks, and every flush clears the completion flags before it returns - so what is guarded here
/// is the invariant the handshake had been assuming all along.</para>
/// </summary>
public class CaptureHonestyTests
{
    /// <summary>
    /// The discard is the point of it: a window that is full says nothing on its own, because the
    /// frames that filled it may be the ones the flush booked to throw away.
    /// </summary>
    [Theory]
    [InlineData(32_896, 28_800, 4_096, true)]    // the bench's own point, measured properly
    [InlineData(32_895, 28_800, 4_096, false)]   // one frame short of it
    [InlineData(8_192, 28_800, 4_096, false)]    // two records and no discard: the reported fault
    [InlineData(4_096, 0, 4_096, true)]          // no settle configured, so one record is enough
    [InlineData(4_095, 0, 4_096, false)]
    [InlineData(0, 0, 4_096, false)]             // nothing delivered at all
    public void ACapture_HeldItsOwnStimulus_OnlyOnceTheDiscardAndAWholeRecordHaveArrived(
        long delivered, long discard, int fftSize, bool honest) =>
        Assert.Equal(honest, SteppedCaptureSupport.CaptureHeldItsOwnStimulus(delivered, discard, fftSize));

    /// <summary>
    /// A full window is not the test, and this is the case that says so: the 2085.9375 Hz point had
    /// a full window - that is why it produced a reading at all - built entirely from audio the
    /// flush had booked to be thrown away.
    /// </summary>
    [Fact]
    public void AFullWindowOfDiscardedAudio_IsNotAnHonestCapture()
    {
        const int FftSize = 4096;
        const int Discard = 28_800;

        Assert.False(SteppedCaptureSupport.CaptureHeldItsOwnStimulus(FftSize, Discard, FftSize));
        Assert.True(SteppedCaptureSupport.CaptureHeldItsOwnStimulus(Discard + FftSize, Discard, FftSize));
    }

    /// <summary>
    /// The counter the rule is asked of: every frame handed to the pipeline since the last flush,
    /// <b>including</b> the ones a booked discard threw away. Counting only what reached a ring
    /// buffer would make the discard invisible, which is the one thing being waited for.
    /// </summary>
    [Fact]
    public void FramesSinceFlush_CountsDeliveredFrames_DiscardedOnesIncluded()
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 1024 },
        };

        pipeline.Start(sampleRateHz: 48_000, channelCount: 2);
        Assert.Equal(0, pipeline.FramesSinceFlush);

        pipeline.Enqueue(Block(512));
        pipeline.Enqueue(Block(512));
        Assert.Equal(1024, pipeline.FramesSinceFlush);

        // Everything from here is swallowed by the ring buffers, and must still be counted: the
        // whole question is how much audio has gone past since the stimulus changed.
        pipeline.Flush(discardFrames: 4096);
        Assert.Equal(0, pipeline.FramesSinceFlush);

        pipeline.Enqueue(Block(512));
        Assert.Equal(512, pipeline.FramesSinceFlush);
        Assert.False(SteppedCaptureSupport.CaptureHeldItsOwnStimulus(pipeline.FramesSinceFlush, 4096, 1024));

        for (int i = 0; i < 9; i++)
            pipeline.Enqueue(Block(512));

        Assert.Equal(5120, pipeline.FramesSinceFlush);
        Assert.True(SteppedCaptureSupport.CaptureHeldItsOwnStimulus(pipeline.FramesSinceFlush, 4096, 1024));
    }

    /// <summary>
    /// The regression that would cost the most and show the least: a check that fires on an honest
    /// capture takes every point of every stepped sweep twice, on hardware and in the simulator
    /// alike, and changes not one number on screen. So a complete run has to produce no retake.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryRun_RetakesNothing()
    {
        var log = new ListLogger<SteppedResponseRunner>();

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 4096, TargetFrameRateHz = 100, DisplayColumns = 200 },
        };

        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with
        {
            HarmonicsEnabled = false,
            NoiseEnabled = false,
            HumEnabled = false,
        };

        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var runner = new SteppedResponseRunner(session, pipeline, hub, log);
        await runner.StartAsync(
            new SteppedResponseRequest(
                1000.0, 4000.0, 2, ThdSweepLevelMode.GeneratorVoltage, -20.0, ThdSweepLevelUnit.Dbv, 8.0,
                GenerateLeft: true, GenerateRight: true, FftSize: 16384, Window: WindowType.Hann,
                AverageCount: 1, SampleRateHz: 48_000.0, AdaptiveResolution: true, RightChannelIsReference: true),
            CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (runner.Running)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The run did not finish (phase {runner.Status.Phase}).");

            await Task.Delay(25);
        }

        Assert.Equal(FrequencyResponsePhase.Finished, runner.Status.Phase);
        Assert.DoesNotContain(log.Lines, line => line.Contains("retaking the point", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, line => line.Contains("came back short again", StringComparison.Ordinal));

        await session.ShutdownAsync();
    }

    private static PcmBlock Block(int frames)
    {
        float[] samples = new float[frames * 2];
        return new PcmBlock(samples, samples.Length, sampleRateHz: 48_000, channelCount: 2, sequenceNumber: 0, timestampTicks: 0);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
                Lines.Add(formatter(state, exception));
        }
    }
}
