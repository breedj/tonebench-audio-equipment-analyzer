using System.Buffers;
using System.Runtime.CompilerServices;
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

    [Fact]
    public async Task ConfigureGeneratorAsync_EnablingAChannelStartsTheStreamIndependentlyOfAcquisitionAndStopsWhenDisabled()
    {
        // Regression test for the bug where enabling the generator did nothing until Start Acquisition
        // was pressed: DeviceSessionService must bring the device stream up on its own when a generator
        // channel is enabled, without flipping on Acquiring/the spectrum pipeline, and tear it back down
        // once every channel is off again (mirroring StopAcquisitionAsync's own teardown).
        var device = new RecordingDevice();
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var streamHint = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);
        var generatorConfig = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off);

        await session.ConfigureGeneratorAsync(generatorConfig, CancellationToken.None, streamHint);

        // EnsureStreamRunningAsync only schedules the pump loop via Task.Run; give it a moment to
        // actually start enumerating StreamAsync.
        for (int i = 0; i < 100 && device.StreamStartCount == 0; i++)
            await Task.Delay(10);

        Assert.Equal(1, device.StreamStartCount);
        Assert.False(session.Acquiring);

        await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);

        Assert.True(device.StopRequested);
        Assert.Equal(1, device.StreamEndCount);
    }

    [Fact]
    public async Task ShutdownAsync_StopsTheStreamEvenWhenTheGeneratorIsOnAndAcquisitionNeverStarted()
    {
        // The two states the app is normally left in, and the two StopAcquisitionAsync tears nothing
        // down in: it returns immediately when acquisition was never started, and it deliberately
        // keeps the stream up while the generator is enabled. Either way the pump was still running
        // when DI disposed the device, so TransferPump's finally — the only thing that writes
        // StreamingStop and collects in-flight EP2 transfers — never ran before libusb_close, and the
        // QA403 was left streaming and unreachable to every app until power-cycled.
        var device = new RecordingDevice();
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var streamHint = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None,
            streamHint);

        for (int i = 0; i < 100 && device.StreamStartCount == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, device.StreamStartCount);
        Assert.False(session.Acquiring);

        // What StopAcquisitionAsync would have done here: nothing at all.
        await session.StopAcquisitionAsync();
        Assert.Equal(0, device.StreamEndCount);

        await session.ShutdownAsync();

        Assert.True(device.StopRequested);
        Assert.Equal(1, device.StreamEndCount);
    }

    [Fact]
    public async Task ShutdownAsync_IsSafeWhenNothingWasEverStarted()
    {
        // Runs on every exit, including one where the user only ever looked at the UI.
        var device = new RecordingDevice();
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        await session.ShutdownAsync();

        Assert.Equal(0, device.StreamStartCount);
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_SnapsToneFrequenciesToTheNearestCoherentBin()
    {
        var device = new RecordingDevice();
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 131072 },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        // 1000 Hz at 192 kHz over 131072 points lands on bin 682.67 — about as far off-bin as it
        // gets, and the reason a requested "1000" showed leakage skirts around the fundamental.
        // Bin 683 is coprime to 2^17, so the snap lands there: 683 * 192000 / 131072 = 1000.4883 Hz.
        // That is the same frequency QuantAsylum's own app reports as "1.0004 kHz" for these settings.
        var hint = new AcquisitionConfig(SampleRateHz: 192_000, ChannelCount: 2, BlockFrameCount: 4096);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -6.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None,
            hint);

        Assert.Equal(1000.4883, session.GetStatus().GeneratorLeft.Tones[0].FreqHz, precision: 4);

        // And the status reports the effective frequency, so the UI shows what is really playing
        // rather than what was typed.
        Assert.True(session.GetStatus().GeneratorSnapToBin);
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_SnapToBinOff_LeavesTheRequestedFrequencyAlone()
    {
        var device = new RecordingDevice();
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 131072 },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var hint = new AcquisitionConfig(SampleRateHz: 192_000, ChannelCount: 2, BlockFrameCount: 4096);
        var config = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -6.0)]), ChannelGeneratorConfig.Off)
        {
            SnapToBin = false,
        };

        await session.ConfigureGeneratorAsync(config, CancellationToken.None, hint);

        Assert.Equal(1000.0, session.GetStatus().GeneratorLeft.Tones[0].FreqHz, precision: 6);
    }

    [Fact]
    public async Task ReapplyGeneratorAsync_ResnapsFromTheOriginalRequestNotThePreviouslySnappedValue()
    {
        // Coherence depends on the analysis length, so changing FFT size must re-snap — and it must
        // re-snap the frequency the user asked for. Re-snapping an already-snapped value would let
        // the tone walk a bin at a time every time the FFT size changed.
        var device = new RecordingDevice();
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 131072 },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var hint = new AcquisitionConfig(SampleRateHz: 192_000, ChannelCount: 2, BlockFrameCount: 4096);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -6.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None,
            hint);
        Assert.Equal(1000.4883, session.GetStatus().GeneratorLeft.Tones[0].FreqHz, precision: 4);

        // 16384 points at 192 kHz: bin 11.71875 Hz, 1000 Hz -> bin 85.33 -> 85 -> 996.09375 Hz.
        pipeline.Options = pipeline.Options with { FftSize = 16384 };
        await session.ReapplyGeneratorAsync(CancellationToken.None);

        Assert.Equal(996.09375, session.GetStatus().GeneratorLeft.Tones[0].FreqHz, precision: 5);
    }

    /// <summary>
    /// Records stream lifecycle calls so tests can observe whether/when <see cref="IAnalyzerDevice.StreamAsync"/>
    /// is actually running, independent of <see cref="DeviceSessionService.Acquiring"/>.
    /// </summary>
    private sealed class RecordingDevice : IAnalyzerDevice
    {
        public event EventHandler? DeviceLost { add { } remove { } }

        public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

        public int StreamStartCount { get; private set; }

        public int StreamEndCount { get; private set; }

        public bool StopRequested { get; private set; }

        public Task<DeviceInfo> ConnectAsync(CancellationToken ct) =>
            Task.FromResult(new DeviceInfo("QA403", "HW-0001", "1.0", IsSimulated: false));

        public double InputRangeDbv { get; private set; }

        public long DroppedRxChunks => 0;

        public bool AutoRangeEnabled { get; set; }

        public double AutoRangeStepDownDelayMs { get; set; }

        public Task SetInputRangeAsync(double dbv, CancellationToken ct)
        {
            InputRangeDbv = dbv;
            return Task.CompletedTask;
        }

        public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync()
        {
            StopRequested = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, [EnumeratorCancellation] CancellationToken ct)
        {
            StreamStartCount++;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    float[] buffer = ArrayPool<float>.Shared.Rent(config.BlockFrameCount * config.ChannelCount);
                    yield return new PcmBlock(
                        buffer, config.BlockFrameCount * config.ChannelCount, config.SampleRateHz, config.ChannelCount, 0, 0);
                    await Task.Delay(5, CancellationToken.None);
                }
            }
            finally
            {
                StreamEndCount++;
            }
        }
    }

    /// <summary>Stands in for real hardware: an <see cref="IAnalyzerDevice"/> that is not the simulator.</summary>
    private sealed class StubDevice : IAnalyzerDevice
    {
        public event EventHandler? DeviceLost { add { } remove { } }

        public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

        public Task<DeviceInfo> ConnectAsync(CancellationToken ct) =>
            Task.FromResult(new DeviceInfo("QA403", "HW-0001", "1.0", IsSimulated: false));

        public double InputRangeDbv { get; private set; }

        public long DroppedRxChunks => 0;

        public bool AutoRangeEnabled { get; set; }

        public double AutoRangeStepDownDelayMs { get; set; }

        public Task SetInputRangeAsync(double dbv, CancellationToken ct)
        {
            InputRangeDbv = dbv;
            return Task.CompletedTask;
        }

        public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => Task.CompletedTask;

        public IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, CancellationToken ct) =>
            AsyncEnumerable.Empty<PcmBlock>();

        public Task StopAsync() => Task.CompletedTask;
    }
}
