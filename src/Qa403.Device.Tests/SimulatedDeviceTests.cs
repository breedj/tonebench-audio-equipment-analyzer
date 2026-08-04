using System.Diagnostics;
using Qa403.Device.Simulation;

namespace Qa403.Device.Tests;

public class SimulatedDeviceTests
{
    [Fact]
    public async Task ConnectAsync_ReturnsSimulatedDeviceInfo()
    {
        var device = new SimulatedDevice();

        DeviceInfo info = await device.ConnectAsync(CancellationToken.None);

        Assert.True(info.IsSimulated);
        Assert.False(string.IsNullOrWhiteSpace(info.SerialNumber));
    }

    [Fact]
    public void Capabilities_MatchesRealQa403Ranges()
    {
        var device = new SimulatedDevice();

        Assert.Equal(new double[] { 0, 6, 12, 18, 24, 30, 36, 42 }, device.Capabilities.InputRangesDbv);
        Assert.Equal(new double[] { -12, -2, 8, 18 }, device.Capabilities.OutputRangesDbv);
        Assert.Equal(2, device.Capabilities.MaxChannelCount);
    }

    [Fact]
    public async Task SetInputRangeAsync_RejectsValueNotInCapabilities()
    {
        var device = new SimulatedDevice();
        await device.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.SetInputRangeAsync(7.0, CancellationToken.None));
    }

    [Fact]
    public async Task SetOutputRangeAsync_AcceptsSupportedValue()
    {
        var device = new SimulatedDevice();
        await device.ConnectAsync(CancellationToken.None);

        await device.SetOutputRangeAsync(18.0, CancellationToken.None);
    }

    [Fact]
    public async Task StreamAsync_BeforeConnect_Throws()
    {
        var device = new SimulatedDevice();
        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 256);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var block in device.StreamAsync(config, CancellationToken.None))
                block.Dispose();
        });
    }

    [Fact]
    public async Task StreamAsync_YieldsBlocksWithExpectedShapeAndIncreasingSequenceNumbers()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 256);

        using var cts = new CancellationTokenSource();
        long previousSequence = -1;
        int blocksSeen = 0;

        await foreach (var block in device.StreamAsync(config, cts.Token))
        {
            using (block)
            {
                Assert.Equal(48_000, block.SampleRateHz);
                Assert.Equal(2, block.ChannelCount);
                Assert.Equal(256, block.FrameCount);
                Assert.Equal(512, block.SampleCount);
                Assert.True(block.SequenceNumber > previousSequence);
                previousSequence = block.SequenceNumber;

                // Both channels carry the same simulated signal (see SimulatedDevice remarks).
                Assert.Equal(block.Samples[0], block.Samples[1]);
            }

            blocksSeen++;
            if (blocksSeen >= 3)
            {
                cts.Cancel();
                break;
            }
        }

        Assert.Equal(3, blocksSeen);
    }

    [Fact]
    public async Task StreamAsync_StopAsync_EndsTheStream()
    {
        var device = new SimulatedDevice();
        await device.ConnectAsync(CancellationToken.None);
        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 256);

        var blocksSeen = 0;
        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            block.Dispose();
            blocksSeen++;
            if (blocksSeen == 1)
                await device.StopAsync();
        }

        Assert.True(blocksSeen >= 1);
    }

    [Fact]
    public async Task StreamAsync_EmitsAtRealTimeCadence_NotAsFastAsPossible()
    {
        var device = new SimulatedDevice();
        await device.ConnectAsync(CancellationToken.None);
        // 480 frames @ 48 kHz = 10 ms/block; a tight loop would blow through this in microseconds.
        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 480);

        using var cts = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        int blocksSeen = 0;

        await foreach (var block in device.StreamAsync(config, cts.Token))
        {
            block.Dispose();
            blocksSeen++;
            if (blocksSeen >= 5)
            {
                cts.Cancel();
                break;
            }
        }

        stopwatch.Stop();

        // 5 blocks * 10 ms = 50 ms expected; require at least half that to rule out a busy loop.
        Assert.True(stopwatch.ElapsedMilliseconds >= 25, $"expected real-time pacing (>=25 ms for 5 blocks), got {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void PcmBlock_Dispose_ReturnsBufferAndThrowsOnUseAfterDispose()
    {
        var buffer = new float[16];
        var block = new PcmBlock(buffer, sampleCount: 16, sampleRateHz: 48_000, channelCount: 1, sequenceNumber: 0, timestampTicks: 0);

        block.Dispose();
        block.Dispose(); // idempotent, must not throw

        Assert.Throws<ObjectDisposedException>(() => { _ = block.Samples; });
    }
}
