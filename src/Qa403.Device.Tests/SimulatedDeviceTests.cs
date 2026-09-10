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
    public async Task ConfigureGeneratorAsync_KindOff_ProducesSilenceEvenWhenEnabled()
    {
        var device = await ConnectBareDeviceAsync();
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]) { Kind = GeneratorKind.Off },
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        foreach (var sample in await FirstBlockSamplesAsync(device, channelCount: 1, blockFrameCount: 64))
            Assert.Equal(0.0, sample, precision: 9);
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_Multitone_SumsTheDerivedCombWithoutHarmonics()
    {
        var device = await ConnectBareDeviceAsync();
        var settings = new MultitoneSettings { TonesPerOctave = 2, TotalRmsDbv = -20.0 };
        var expectedTones = MultitoneGenerator.Build(settings);

        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                // Tones carries the derived comb, exactly as GeneratorEndpoints builds it.
                new ChannelGeneratorConfig(true, expectedTones) { Kind = GeneratorKind.Multitone, Multitone = settings },
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        const double sampleRateHz = 48_000.0;
        var samples = await FirstBlockSamplesAsync(device, channelCount: 1, blockFrameCount: 64, sampleRateHz);

        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / sampleRateHz;
            double expected = expectedTones.Sum(
                tone => Math.Sqrt(2.0) * Math.Pow(10.0, tone.AmplitudeDbv / 20.0) * Math.Cos(2.0 * Math.PI * tone.FrequencyHz * t));
            Assert.Equal(expected, samples[i], precision: 6);
        }
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_WhiteNoise_HasTheConfiguredBroadbandRms()
    {
        // LevelDbv is a broadband RMS, not a dBV/sqrt(Hz) density, so the block's own RMS is the
        // measurement — no bandwidth term involved.
        const double levelDbv = -20.0;
        var device = await ConnectBareDeviceAsync(randomSeed: 7);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.WhiteNoise, Noise = new WhiteNoiseSettings { LevelDbv = levelDbv } },
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var samples = await FirstBlockSamplesAsync(device, channelCount: 1, blockFrameCount: 16_384);

        double rms = Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Length);
        double measuredDbv = 20.0 * Math.Log10(rms);
        Assert.True(Math.Abs(measuredDbv - levelDbv) < 1.0, $"Expected ~{levelDbv} dBV, measured {measuredDbv} dBV");
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_WhiteNoise_DrawsLeftAndRightIndependently()
    {
        var device = await ConnectBareDeviceAsync(randomSeed: 3);
        var channel = new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.WhiteNoise, Noise = new WhiteNoiseSettings { LevelDbv = -20.0 } };
        await device.ConfigureGeneratorAsync(new GeneratorConfig(channel, channel), CancellationToken.None);

        var samples = await FirstBlockSamplesAsync(device, channelCount: 2, blockFrameCount: 256);

        int identicalFrames = 0;
        for (int frame = 0; frame < 256; frame++)
        {
            if (samples[frame * 2] == samples[(frame * 2) + 1])
                identicalFrames++;
        }

        Assert.Equal(0, identicalFrames);
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_PinkNoise_HasTheConfiguredBroadbandRms()
    {
        // The simulator has to present the same stimulus level the real device does, or an impedance
        // run verified against --simulate would be a different run on hardware. A longer block than
        // the white test's: 1/f noise puts most of its power at the low end, so a short one is a
        // draw of a slow random walk rather than an average of it.
        const double levelDbv = -20.0;
        var device = await ConnectBareDeviceAsync(randomSeed: 7);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.PinkNoise, Noise = new WhiteNoiseSettings { LevelDbv = levelDbv } },
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var samples = await FirstBlockSamplesAsync(device, channelCount: 1, blockFrameCount: 65_536);

        double rms = Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Length);
        double measuredDbv = 20.0 * Math.Log10(rms);
        Assert.True(Math.Abs(measuredDbv - levelDbv) < 1.0, $"Expected ~{levelDbv} dBV, measured {measuredDbv} dBV");
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_PinkNoise_PutsTheSameStimulusOnBothChannels()
    {
        // The mirror image of the white-noise test above, and deliberately so. White models two
        // independent generator channels; pink is a measurement stimulus, and the simulator's right
        // channel stands in for the reference tap of a fixture that a bench drives with the very
        // same signal. An impedance run against --simulate reads coherence near zero without this.
        var device = await ConnectBareDeviceAsync(randomSeed: 3);
        var channel = new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.PinkNoise, Noise = new WhiteNoiseSettings { LevelDbv = -20.0 } };
        await device.ConfigureGeneratorAsync(new GeneratorConfig(channel, channel), CancellationToken.None);

        var samples = await FirstBlockSamplesAsync(device, channelCount: 2, blockFrameCount: 256);

        for (int frame = 0; frame < 256; frame++)
            Assert.Equal(samples[frame * 2], samples[(frame * 2) + 1]);
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_PinkNoise_HasMorePowerBelowAKilohertzThanWhiteDoes()
    {
        // The reason this stimulus is pink rather than white: a divider measurement is noise-limited
        // at the bottom of the band. Measured as a ratio of the two kinds at the same level, so it
        // cannot pass by both being wrong the same way.
        var device = await ConnectBareDeviceAsync(randomSeed: 3);

        async Task<double> LowBandFractionAsync(GeneratorKind kind)
        {
            await device.ConfigureGeneratorAsync(
                new GeneratorConfig(
                    new ChannelGeneratorConfig(true, []) { Kind = kind, Noise = new WhiteNoiseSettings { LevelDbv = -20.0 } },
                    ChannelGeneratorConfig.Off),
                CancellationToken.None);

            var samples = await FirstBlockSamplesAsync(device, channelCount: 1, blockFrameCount: 32_768);

            // A one-pole low pass at about 1 kHz for 48 kHz sampling, which is all this needs: the
            // question is which kind puts more of its power below a kilohertz, not how much.
            const double alpha = 0.12;
            double state = 0.0;
            double lowPower = 0.0;
            double totalPower = 0.0;
            foreach (float sample in samples)
            {
                state += alpha * (sample - state);
                lowPower += state * state;
                totalPower += (double)sample * sample;
            }

            return lowPower / totalPower;
        }

        double pink = await LowBandFractionAsync(GeneratorKind.PinkNoise);
        double white = await LowBandFractionAsync(GeneratorKind.WhiteNoise);

        Assert.True(pink > 2.0 * white, $"Pink put {pink:0.000} of its power in the low band against white's {white:0.000}.");
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_EchoChirp_SweepsUpwardsWithinTheConfiguredBand()
    {
        const double sampleRateHz = 48_000.0;
        const double startFreqHz = 100.0;
        const double endFreqHz = 10_000.0;

        var device = await ConnectBareDeviceAsync();
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [])
                {
                    Kind = GeneratorKind.EchoChirp,
                    Chirp = new EchoChirpSettings { AmplitudeDbv = 0.0, StartFrequencyHz = startFreqHz, EndFrequencyHz = endFreqHz },
                },
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        // One 4096-sample block covers the first ~85 ms of the 1 s sweep, where the instantaneous
        // frequency is still near the start. Zero crossings give the local frequency without an FFT.
        var samples = await FirstBlockSamplesAsync(device, channelCount: 1, blockFrameCount: 4096, sampleRateHz);

        Assert.Contains(samples, s => Math.Abs(s) > 0.1); // not silent

        double measuredHz = ZeroCrossingRateHz(samples, sampleRateHz) / 2.0;
        Assert.InRange(measuredHz, startFreqHz, endFreqHz);

        // Sweeping up: the second half of the block must carry a higher local frequency than the first.
        int half = samples.Length / 2;
        double firstHalfHz = ZeroCrossingRateHz(samples[..half], sampleRateHz) / 2.0;
        double secondHalfHz = ZeroCrossingRateHz(samples[half..], sampleRateHz) / 2.0;
        Assert.True(secondHalfHz > firstHalfHz, $"Expected an upward sweep, got {firstHalfHz} Hz then {secondHalfHz} Hz");
    }

    [Fact]
    public async Task OutputRange_TruncatesAGeneratorLevelAboveDacFullScale()
    {
        // 0 dBV asked of a -12 dBV full-scale DAC is 12 dB over: the sine's peaks are cut flat at
        // the range's own peak voltage, with no knee (that's SoftClip's job, on the input side).
        const double outputRangeDbv = -12.0;
        const double sampleRateHz = 48_000.0;
        var device = await ConnectBareDeviceAsync();
        await device.SetOutputRangeAsync(outputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var samples = await FirstBlockSamplesAsync(device, channelCount: 1, blockFrameCount: 256, sampleRateHz);

        double clipPeakV = PeakVolts(outputRangeDbv);
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / sampleRateHz;
            double unclipped = Math.Sqrt(2.0) * Math.Cos(2.0 * Math.PI * 1000.0 * t);
            Assert.Equal(Math.Clamp(unclipped, -clipPeakV, clipPeakV), samples[i], precision: 6);
        }

        // And it really is clipping: the flat tops mean many samples sit exactly at the limit.
        Assert.True(samples.Count(s => Math.Abs(Math.Abs(s) - clipPeakV) < 1e-6) > 10);
    }

    [Fact]
    public async Task OutputRange_LeavesAGeneratorLevelWithinDacFullScaleUntouched()
    {
        const double sampleRateHz = 48_000.0;
        var device = await ConnectBareDeviceAsync();
        await device.SetOutputRangeAsync(18.0, CancellationToken.None); // full scale 7.94 Vrms
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var samples = await FirstBlockSamplesAsync(device, channelCount: 1, blockFrameCount: 256, sampleRateHz);

        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / sampleRateHz;
            Assert.Equal(Math.Sqrt(2.0) * Math.Cos(2.0 * Math.PI * 1000.0 * t), samples[i], precision: 6);
        }
    }

    [Fact]
    public async Task OutputRange_ChangedDuringAnAcquisitionTakesEffectOnTheNextBlock()
    {
        // Regression for the range fields being plain (non-volatile) doubles read inside the
        // per-block loop: without Volatile.Read the change could never reach the streaming thread.
        var device = await ConnectBareDeviceAsync();
        await device.SetOutputRangeAsync(18.0, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 256);
        double clippedPeakV = PeakVolts(-12.0);
        int blockIndex = 0;
        double peakBefore = 0.0;
        double peakAfter = 0.0;

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                double peak = 0.0;
                for (int i = 0; i < block.SampleCount; i++)
                    peak = Math.Max(peak, Math.Abs(block.Samples[i]));

                if (blockIndex == 0)
                    peakBefore = peak;
                else
                    peakAfter = peak;
            }

            if (blockIndex++ == 0)
                await device.SetOutputRangeAsync(-12.0, CancellationToken.None);
            else
                break;
        }

        Assert.Equal(Math.Sqrt(2.0), peakBefore, precision: 5);
        Assert.Equal(clippedPeakV, peakAfter, precision: 5);
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

    /// <summary>Peak volts of a sine at <paramref name="dbv"/> dBV — the same conversion the device uses for range full scales.</summary>
    private static double PeakVolts(double dbv) => Math.Sqrt(2.0) * Math.Pow(10.0, dbv / 20.0);

    /// <summary>A connected device whose simulated DUT adds nothing of its own — only the generator is audible.</summary>
    private static async Task<SimulatedDevice> ConnectBareDeviceAsync(int? randomSeed = null)
    {
        var device = new SimulatedDevice(randomSeed);
        await device.ConnectAsync(CancellationToken.None);
        device.SignalProfile = new SimulatedSignalProfile
        {
            HarmonicsEnabled = false, HumEnabled = false, NoiseEnabled = false, PinkNoiseEnabled = false,
        };

        return device;
    }

    private static async Task<float[]> FirstBlockSamplesAsync(
        SimulatedDevice device, int channelCount, int blockFrameCount, double sampleRateHz = 48_000.0)
    {
        var config = new AcquisitionConfig(sampleRateHz, channelCount, blockFrameCount);

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
                return block.Samples[..block.SampleCount].ToArray();
        }

        throw new InvalidOperationException("Stream ended without producing a block.");
    }

    /// <summary>Sign changes per second — twice the fundamental for a clean single-frequency waveform.</summary>
    private static double ZeroCrossingRateHz(ReadOnlySpan<float> samples, double sampleRateHz)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < 0.0f) != (samples[i] < 0.0f))
                crossings++;
        }

        return crossings * sampleRateHz / samples.Length;
    }
}
