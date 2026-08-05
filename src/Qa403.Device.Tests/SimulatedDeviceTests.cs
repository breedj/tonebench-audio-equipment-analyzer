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
    public async Task Device_StartsWithGeneratorOff_NoDefaultDemoTone()
    {
        // Regression: the device's out-of-the-box state must agree with GeneratorConfig.Off (and
        // the UI's "Generator enabled" checkbox starting unchecked) — a hidden always-on demo tone
        // here previously meant the checkbox and the actual signal disagreed until the user
        // toggled it once. Deliberately does not touch SignalProfile/ConfigureGeneratorAsync.
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 4096);

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                double sumSquares = 0.0;
                foreach (var s in block.Samples)
                    sumSquares += (double)s * s;
                double rms = Math.Sqrt(sumSquares / block.SampleCount);

                // A legacy always-on 1 kHz/0 dBV demo tone would read Vrms ~= 1.0. Hum (-90 dBV)
                // and noise (-150 dBV/rtHz) alone land many orders of magnitude below that.
                Assert.True(rms < 0.01, $"Expected near-silence with the generator off, got Vrms={rms}");
            }

            break;
        }
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_DrivesSynthesizedSignalToConfiguredTone()
    {
        var device = new SimulatedDevice();
        await device.ConnectAsync(CancellationToken.None);
        device.SignalProfile = new SimulatedSignalProfile
        {
            HarmonicsEnabled = false, HumEnabled = false, NoiseEnabled = false, PinkNoiseEnabled = false,
        };

        const double freqHz = 3000.0;
        const double ampDbv = 0.0; // 0 dBV = 1 Vrms -> peak = sqrt(2)
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(freqHz, ampDbv)]), ChannelGeneratorConfig.Off), CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 64);
        double peakV = Math.Sqrt(2.0);

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                for (int i = 0; i < block.FrameCount; i++)
                {
                    double t = i / config.SampleRateHz;
                    double expected = peakV * Math.Cos(2.0 * Math.PI * freqHz * t);
                    Assert.Equal(expected, block.Samples[i], precision: 6);
                }
            }

            break; // one block is enough to confirm the frequency took effect
        }
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_SumsMultipleTones()
    {
        var device = new SimulatedDevice();
        await device.ConnectAsync(CancellationToken.None);
        device.SignalProfile = new SimulatedSignalProfile
        {
            HarmonicsEnabled = false, HumEnabled = false, NoiseEnabled = false, PinkNoiseEnabled = false,
        };

        GeneratorTone[] tones = [new(1000.0, 0.0), new(5000.0, -6.0)];
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, tones), ChannelGeneratorConfig.Off), CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 64);

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                for (int i = 0; i < block.FrameCount; i++)
                {
                    double t = i / config.SampleRateHz;
                    double expected = Math.Sqrt(2.0) * Math.Cos(2.0 * Math.PI * 1000.0 * t)
                        + (Math.Sqrt(2.0) * Math.Pow(10.0, -6.0 / 20.0) * Math.Cos(2.0 * Math.PI * 5000.0 * t));
                    Assert.Equal(expected, block.Samples[i], precision: 6);
                }
            }

            break;
        }
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_LeftAndRightRunIndependentTones()
    {
        // The split generator's whole point: each channel gets its own tone, not one signal
        // duplicated across channels (see SimulatedDevice's per-frame loop in StreamAsync).
        var device = new SimulatedDevice();
        await device.ConnectAsync(CancellationToken.None);
        device.SignalProfile = new SimulatedSignalProfile
        {
            HarmonicsEnabled = false, HumEnabled = false, NoiseEnabled = false, PinkNoiseEnabled = false,
        };

        const double leftFreqHz = 1000.0;
        const double rightFreqHz = 3000.0;
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(leftFreqHz, 0.0)]),
                new ChannelGeneratorConfig(true, [new GeneratorTone(rightFreqHz, 0.0)])),
            CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 64);
        double peakV = Math.Sqrt(2.0);

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                for (int frame = 0; frame < block.FrameCount; frame++)
                {
                    double t = frame / config.SampleRateHz;
                    double expectedLeft = peakV * Math.Cos(2.0 * Math.PI * leftFreqHz * t);
                    double expectedRight = peakV * Math.Cos(2.0 * Math.PI * rightFreqHz * t);
                    Assert.Equal(expectedLeft, block.Samples[frame * 2], precision: 6);
                    Assert.Equal(expectedRight, block.Samples[(frame * 2) + 1], precision: 6);
                }
            }

            break;
        }
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_DisabledProducesSilenceAtGeneratorFrequencies()
    {
        var device = new SimulatedDevice();
        await device.ConnectAsync(CancellationToken.None);
        device.SignalProfile = new SimulatedSignalProfile
        {
            HarmonicsEnabled = false, HumEnabled = false, NoiseEnabled = false, PinkNoiseEnabled = false,
        };

        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(false, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off), CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 64);

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                foreach (var sample in block.Samples)
                    Assert.Equal(0.0, sample, precision: 9);
            }

            break;
        }
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
