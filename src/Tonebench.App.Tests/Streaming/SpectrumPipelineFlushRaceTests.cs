using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// <see cref="SpectrumPipeline.Flush"/> against a tick that is already running.
///
/// The tick worker snapshots the ring buffer, then spends real time on a windowed FFT, harmonic
/// analysis and encoding before it commits. A Flush landing inside that gap used to be invisible to
/// it: the tick would go on to set the single-shot hold and overwrite the retained snapshot with a
/// record describing the *previous* stimulus. On a THD-vs-frequency sweep that meant every point
/// after the first reported the first point's spectrum, and the caller saw a completed capture
/// immediately instead of waiting for real post-change audio.
///
/// Found on hardware, not here — the end-to-end sweep tests all passed throughout, because they
/// asserted on the requested frequency list rather than on what the analyzer actually located.
/// </summary>
public class SpectrumPipelineFlushRaceTests
{
    /// <summary>Big enough that a tick's FFT takes long enough for a flush to land mid-flight.</summary>
    private const int FftSize = 131072;

    private const double SampleRateHz = 48_000.0;

    [Fact]
    public async Task Flush_LandingWhileATickIsInFlight_NeverLeavesThePipelineHeldOnPreFlushAudio()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = FftSize,
                TargetFrameRateHz = 60,
                DisplayColumns = 200,
                Averaging = AveragingMode.Off,
                SingleShot = true,
                ProcessLeft = true,
                ProcessRight = false,
            },
        };

        pipeline.Start(SampleRateHz, channelCount: 1);

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            // Keep flushing. Every tick therefore starts in one generation and commits in another,
            // which is exactly the situation a sweep creates once per point — so no capture may ever
            // be reported complete. Without the generation guard a tick commits anyway and this
            // observes `held`.
            var deadline = DateTime.UtcNow.AddSeconds(4);
            var flusher = Task.Run(async () =>
            {
                while (DateTime.UtcNow < deadline)
                {
                    pipeline.Flush(discardFrames: 0);
                    await Task.Delay(15);
                }
            });

            while (DateTime.UtcNow < deadline)
            {
                Assert.False(
                    pipeline.SingleShotComplete,
                    "a capture was reported complete even though a Flush invalidated every record that could have produced it");
                await Task.Delay(1);
            }

            await flusher;

            // And once the flushing stops, a capture must still be able to complete — the guard
            // must reject stale records, not wedge the pipeline permanently.
            var completeBy = DateTime.UtcNow.AddSeconds(30);
            while (!pipeline.SingleShotComplete)
            {
                Assert.True(DateTime.UtcNow < completeBy, "the pipeline never completed a capture after flushing stopped");
                await Task.Delay(20);
            }

            Assert.NotEmpty(pipeline.LatestSnapshots());
        }
        finally
        {
            streamCts.Cancel();
            await device.StopAsync();
            try
            {
                await drainTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            await pipeline.StopAsync();
        }
    }
}
