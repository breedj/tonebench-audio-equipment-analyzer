using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Services;

public class DeviceSessionServiceTests
{
    private static DeviceSessionService CreateService() => new(
        new SimulatedDevice(),
        new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
        NullLogger<DeviceSessionService>.Instance);

    [Fact]
    public async Task StartAcquisitionAsync_RejectsBlockFrameCountBelowHardwareQueueDepth()
    {
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 1);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.StartAcquisitionAsync(config, CancellationToken.None));
        Assert.Contains(DeviceSessionService.MinBlockFrameCount.ToString(), ex.Message);
        Assert.False(session.Acquiring);
    }

    [Fact]
    public async Task StartAcquisitionAsync_AcceptsBlockFrameCountAtTheFloor()
    {
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: DeviceSessionService.MinBlockFrameCount);

        await session.StartAcquisitionAsync(config, CancellationToken.None);
        Assert.True(session.Acquiring);

        await session.StopAcquisitionAsync();
        Assert.False(session.Acquiring);
    }
}
