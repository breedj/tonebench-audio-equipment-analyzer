using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Streaming;

/// <summary>
/// <see cref="SpectrumPipelineOptions.AverageHopFraction"/> — averaging paced by fresh signal
/// instead of by the display tick.
///
/// Averaging used to run on the <see cref="SpectrumPipelineOptions.TargetFrameRateHz"/> timer, which
/// has no relationship to the record length, so how much an "average" actually added was an accident
/// of the FFT size: at a large size consecutive averages re-analyzed almost the same samples, and a
/// THD sweep paid real time per point for a variance reduction it never got.
/// </summary>
public class AverageHopFractionTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>Small enough that several records fit inside a short test, large enough to still be a real FFT.</summary>
    private const int FftSize = 8192;

    private static SpectrumPipeline BuildPipeline(double hopFraction, int averageCount) =>
        new(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = FftSize,
                TargetFrameRateHz = 60,
                DisplayColumns = 200,
                Averaging = averageCount > 1 ? AveragingMode.Linear : AveragingMode.Off,
                AverageCount = averageCount,
                AverageHopFraction = hopFraction,
                SingleShot = true,
                ProcessLeft = true,
                ProcessRight = false,
            },
        };

    /// <summary>Runs a single-shot capture and reports how long it took to complete.</summary>
    private static async Task<TimeSpan> TimeSingleShotAsync(SpectrumPipeline pipeline)
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        pipeline.Start(SampleRateHz, channelCount: 1);

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 1024);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            pipeline.Flush();
            var started = System.Diagnostics.Stopwatch.StartNew();

            while (!pipeline.SingleShotComplete)
            {
                if (started.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("The capture never completed.");
                await Task.Delay(5);
            }

            return started.Elapsed;
        }
        finally
        {
            await streamCts.CancelAsync();
            try
            {
                await drainTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            await pipeline.StopAsync();
            await device.StopAsync();
        }
    }

    [Fact]
    public async Task SingleAverage_IsUnaffectedByTheHopFraction()
    {
        // One average has nothing to overlap with, so the gate must never delay it — the first
        // analysis of an epoch is already a complete post-flush record.
        var pipeline = BuildPipeline(hopFraction: 1.0, averageCount: 1);

        var elapsed = await TimeSingleShotAsync(pipeline);

        // One record is 171 ms at 8192/48 kHz; generous upper bound to keep this off the CI clock.
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"a single average took {elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task EightAverages_TakeLongerWithAHopThanWithout_BecauseTheyNowCarryRealSignal()
    {
        // The point of the change. Without a hop the eight averages are whatever the tick timer
        // happened to deliver; with one they are spaced by real signal, which necessarily takes
        // longer — that extra time is the variance reduction the old behaviour was billing for
        // without providing.
        var legacy = await TimeSingleShotAsync(BuildPipeline(hopFraction: 0.0, averageCount: 8));
        var welch = await TimeSingleShotAsync(BuildPipeline(hopFraction: 0.5, averageCount: 8));

        Assert.True(
            welch > legacy,
            $"hopped averaging ({welch.TotalMilliseconds:0} ms) should outlast tick-paced averaging ({legacy.TotalMilliseconds:0} ms)");
    }

    [Fact]
    public async Task EightAverages_AtFiftyPercentHop_SpanRoughlyFourAndAHalfRecords()
    {
        // n averages at hop h cost (1 + (n-1)*h) records: 4.5 here. Asserted as a band rather than a
        // figure because the acquisition block size quantises when each average can land.
        var recordSeconds = FftSize / SampleRateHz;
        var elapsed = await TimeSingleShotAsync(BuildPipeline(hopFraction: 0.5, averageCount: 8));

        double records = elapsed.TotalSeconds / recordSeconds;
        Assert.InRange(records, 3.5, 7.0);
    }
}
