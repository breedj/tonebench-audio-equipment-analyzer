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

    [Fact]
    public async Task ConnectAsync_PushesTheDefaultRangesToTheDeviceInsteadOfOnlyCachingThem()
    {
        // The status response and the device's own clip points must agree from the first frame:
        // caching a default here without sending it left the UI reporting one full scale while the
        // device clipped at another. Output defaults to the largest range so a default generator
        // level can't arrive pre-clipped.
        var device = new SimulatedDevice();
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);

        await session.ConnectAsync(CancellationToken.None);
        var status = session.GetStatus();

        Assert.Equal(device.Capabilities.InputRangesDbv[0], status.InputRangeDbv);
        Assert.Equal(device.Capabilities.OutputRangesDbv[^1], status.OutputRangeDbv);

        // Now prove the device actually received the 18 dBV output range rather than keeping its
        // own 8 dBV field default: a +12 dBV tone (peak 5.63 V) passes cleanly through an 18 dBV
        // DAC full scale but would be truncated at 3.55 V by an 8 dBV one. The input range is
        // opened right up first so the ADC's soft clip can't be what we end up measuring.
        await session.SetInputRangeAsync(device.Capabilities.InputRangesDbv[^1], CancellationToken.None);
        device.SignalProfile = new SimulatedSignalProfile
        {
            HarmonicsEnabled = false, HumEnabled = false, NoiseEnabled = false, PinkNoiseEnabled = false,
        };
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 12.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        double expectedPeakV = Math.Sqrt(2.0) * Math.Pow(10.0, 12.0 / 20.0);
        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 256);
        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                double peak = 0.0;
                for (int i = 0; i < block.SampleCount; i++)
                    peak = Math.Max(peak, Math.Abs(block.Samples[i]));

                Assert.Equal(expectedPeakV, peak, precision: 4);
            }

            break;
        }
    }

    [Fact]
    public async Task TrySetSimulatedProfile_TogglesNoiseAndHumAndShowsUpInStatus()
    {
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        // The simulated DUT ships with both on (SimulatedSignalProfile.NoiseEnabled/HumEnabled).
        Assert.True(session.SimulatedNoiseEnabled);
        Assert.True(session.SimulatedHumEnabled);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: false, humEnabled: false));

        Assert.False(session.SimulatedNoiseEnabled);
        Assert.False(session.SimulatedHumEnabled);
        Assert.False(session.GetStatus().SimulatedNoiseEnabled);
        Assert.False(session.GetStatus().SimulatedHumEnabled);
    }

    [Fact]
    public async Task TrySetSimulatedProfile_LeavesOmittedSettingsAlone()
    {
        // Patch semantics: the UI's two checkboxes each send only their own field, so toggling one
        // must not reset the other.
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: false, humEnabled: null));

        Assert.False(session.SimulatedNoiseEnabled);
        Assert.True(session.SimulatedHumEnabled);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: null, humEnabled: false));

        Assert.False(session.SimulatedNoiseEnabled);
        Assert.False(session.SimulatedHumEnabled);
    }

    [Fact]
    public async Task TrySetSimulatedProfile_ReportsFailureForANonSimulatedDevice()
    {
        // Real hardware has no such knob, so the endpoint must be able to answer with a 400 rather
        // than silently accepting a setting that does nothing. Status reports null for the same reason.
        var session = new DeviceSessionService(
            new StubDevice(),
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        Assert.False(session.TrySetSimulatedProfile(noiseEnabled: false, humEnabled: false));
        Assert.Null(session.SimulatedNoiseEnabled);
        Assert.Null(session.SimulatedHumEnabled);
        Assert.Null(session.GetStatus().SimulatedNoiseEnabled);
        Assert.Null(session.GetStatus().SimulatedHumEnabled);
    }

    /// <summary>Stands in for real hardware: an <see cref="IAnalyzerDevice"/> that is not the simulator.</summary>
    private sealed class StubDevice : IAnalyzerDevice
    {
        public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

        public Task<DeviceInfo> ConnectAsync(CancellationToken ct) =>
            Task.FromResult(new DeviceInfo("QA403", "HW-0001", "1.0", IsSimulated: false));

        public Task SetInputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => Task.CompletedTask;

        public IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, CancellationToken ct) =>
            AsyncEnumerable.Empty<PcmBlock>();

        public Task StopAsync() => Task.CompletedTask;
    }
}
