using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Streaming;

public class SpectrumPipelineSnapshotTests
{
    [Fact]
    public void LatestSnapshots_BeforeAnyTick_IsEmpty()
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);

        Assert.Empty(pipeline.LatestSnapshots());
    }

    [Fact]
    public async Task LatestSnapshots_ReturnsBothChannelsFromTheSameTick()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]),
                new ChannelGeneratorConfig(true, [new GeneratorTone(2000.0, 0.0)])),
            CancellationToken.None);

        var (pipeline, streamCts, drainTask) = StartPipeline(device, channelCount: 2, out var hub);
        _ = hub;

        try
        {
            await WaitForSnapshotsAsync(pipeline, channelCount: 2);
            var snapshots = pipeline.LatestSnapshots();

            Assert.Equal(2, snapshots.Length);
            var left = Assert.Single(snapshots, s => s.Channel == StreamChannel.Left);
            var right = Assert.Single(snapshots, s => s.Channel == StreamChannel.Right);

            // ProcessTick processes channel 0 then channel 1 synchronously within one call, each
            // incrementing the shared _seq counter once -- consecutive seq values is the signature
            // of "retained together from one tick", not two independent updates drifting apart.
            Assert.True(Math.Abs((long)left.Seq - right.Seq) <= 1, $"left.Seq={left.Seq}, right.Seq={right.Seq}");
        }
        finally
        {
            await StopAsync(device, pipeline, streamCts, drainTask);
        }
    }

    [Fact]
    public async Task LatestSnapshots_SurvivesStopAsync()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var (pipeline, streamCts, drainTask) = StartPipeline(device, channelCount: 1, out _);

        await WaitForSnapshotsAsync(pipeline, channelCount: 1);
        await StopAsync(device, pipeline, streamCts, drainTask);

        Assert.Single(pipeline.LatestSnapshots());
    }

    [Fact]
    public async Task LatestSnapshots_ClearedByFlush()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var (pipeline, streamCts, drainTask) = StartPipeline(device, channelCount: 1, out _);

        try
        {
            await WaitForSnapshotsAsync(pipeline, channelCount: 1);
            Assert.NotEmpty(pipeline.LatestSnapshots());

            pipeline.Flush();

            Assert.Empty(pipeline.LatestSnapshots());
        }
        finally
        {
            await StopAsync(device, pipeline, streamCts, drainTask);
        }
    }

    [Fact]
    public async Task LatestSnapshots_MatchesTheBroadcastMeters()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var (pipeline, streamCts, drainTask) = StartPipeline(device, channelCount: 1, out var hub);
        var (_, reader) = hub.Subscribe();

        try
        {
            DecodedMeters? meters = null;
            StreamFrameHeader header = default;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (meters is null)
            {
                byte[] frame = await reader.ReadAsync(readCts.Token);
                header = StreamFrame.DecodeHeader(frame);
                if (header.MsgType == StreamMsgType.Meters)
                    meters = StreamFrame.DecodeMeters(frame);
            }

            // Immediately after decoding -- no delay before checking -- so the retained snapshot
            // is (almost certainly) still the one this exact frame came from. Asserting the seq
            // match explicitly, rather than only the values, turns a lost race into a clear
            // failure instead of a confusing value mismatch.
            var snapshot = pipeline.LatestSnapshots().Single(s => s.Channel == header.Channel);
            Assert.Equal(header.Seq, snapshot.Seq);

            var decoded = meters.Value;
            Assert.NotNull(snapshot.Meters);
            var snapshotMeters = snapshot.Meters;
            Assert.Equal(decoded.FundamentalFreqHz, snapshotMeters.FundamentalFreqHz);
            Assert.Equal(decoded.ThdPercent, snapshotMeters.ThdPercent);
            Assert.Equal(decoded.ThdNPercent, snapshotMeters.ThdNPercent);
            Assert.Equal(decoded.SnrDb, snapshotMeters.SnrDb);
            Assert.Equal(decoded.FundamentalDbv, snapshotMeters.FundamentalDbv);
            Assert.Equal(decoded.Harmonics.Length, snapshotMeters.Harmonics.Length);
        }
        finally
        {
            await StopAsync(device, pipeline, streamCts, drainTask);
        }
    }

    [Fact]
    public async Task LatestSnapshots_ReturnsACopy_NotTheLiveBuffer()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var (pipeline, streamCts, drainTask) = StartPipeline(device, channelCount: 1, out _);

        try
        {
            await WaitForSnapshotsAsync(pipeline, channelCount: 1);

            var first = pipeline.LatestSnapshots().Single();
            first.Dbv[0] = double.NaN;

            var second = pipeline.LatestSnapshots().Single();
            Assert.False(double.IsNaN(second.Dbv[0]));
        }
        finally
        {
            await StopAsync(device, pipeline, streamCts, drainTask);
        }
    }

    private static (SpectrumPipeline Pipeline, CancellationTokenSource StreamCts, Task DrainTask) StartPipeline(
        SimulatedDevice device, int channelCount, out SpectrumBroadcastHub hub, double sampleRateHz = 48000, int fftSize = 4096)
    {
        hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            // ProcessRight explicit here (its class default is now off) so these tests still cover
            // both channels regardless of the UI's left-only-by-default preference.
            Options = new SpectrumPipelineOptions { FftSize = fftSize, TargetFrameRateHz = 40, DisplayColumns = 200, ProcessRight = true },
        };
        pipeline.Start(sampleRateHz, channelCount);

        var config = new AcquisitionConfig(SampleRateHz: sampleRateHz, ChannelCount: channelCount, BlockFrameCount: 4096);
        var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        return (pipeline, streamCts, drainTask);
    }

    private static async Task StopAsync(SimulatedDevice device, SpectrumPipeline pipeline, CancellationTokenSource streamCts, Task drainTask)
    {
        streamCts.Cancel();
        try { await drainTask; } catch (OperationCanceledException) { }
        await device.StopAsync();
        await pipeline.StopAsync();
        streamCts.Dispose();
    }

    private static async Task WaitForSnapshotsAsync(SpectrumPipeline pipeline, int channelCount, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (pipeline.LatestSnapshots().Length < channelCount)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}
