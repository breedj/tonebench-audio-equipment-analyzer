using System.Buffers;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Streaming;

public class SpectrumPipelineTests
{
    [Fact]
    public async Task Meters_OtherPeaks_SurfacesMainsHum()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off), CancellationToken.None);

        var meters = await RunPipelineAndAwaitMetersAsync(device);

        // The default profile's hum fundamental (50 Hz, -90 dBV) is easily the loudest thing in
        // the spectrum besides the 1 kHz tone and its harmonics, which are excluded — it must
        // show up near the top of OtherPeaks.
        Assert.Contains(meters.OtherPeaks, p => Math.Abs(p.FreqHz - 50.0) < 5.0);
    }

    [Fact]
    public async Task Meters_OtherPeaks_SurfacesTheSecondGeneratorToneAndIntermodulation()
    {
        // Two simultaneous tones through SimulatedDevice's shared soft-clip produce real
        // intermodulation, and the second tone itself is not a harmonic of whichever tone the
        // pipeline auto-detects as "the fundamental" — both should land in OtherPeaks.
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        // Just past the clip threshold (not deeply overdriven) -> a few real IMD products without
        // so many clipping artifacts that they'd crowd hum out of a small top-N list.
        await device.SetInputRangeAsync(6.0, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(2000.0, 0.0), new GeneratorTone(2200.0, 0.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var meters = await RunPipelineAndAwaitMetersAsync(device);

        // Both tones are equal-amplitude, so which one the global peak search picks as "the
        // fundamental" isn't guaranteed — assert on whichever of {2000, 2200} it did NOT pick.
        double otherToneHz = Math.Abs(meters.FundamentalFreqHz - 2000.0) < Math.Abs(meters.FundamentalFreqHz - 2200.0)
            ? 2200.0
            : 2000.0;
        Assert.Contains(meters.OtherPeaks, p => Math.Abs(p.FreqHz - otherToneHz) < 10.0);

        // Real intermodulation from the shared soft-clip: 2*f1-f2 and 2*f2-f1 (the classic
        // close-in third-order products for two tones this close together).
        Assert.Contains(meters.OtherPeaks, p => Math.Abs(p.FreqHz - 1800.0) < 10.0);
        Assert.Contains(meters.OtherPeaks, p => Math.Abs(p.FreqHz - 2400.0) < 10.0);
    }

    private static async Task<DecodedMeters> RunPipelineAndAwaitMetersAsync(
        SimulatedDevice device, double sampleRateHz = 48000, int fftSize = 65536)
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = fftSize, TargetFrameRateHz = 40, DisplayColumns = 200 },
        };

        var (_, reader) = hub.Subscribe();
        pipeline.Start(sampleRateHz, channelCount: 1);

        var config = new AcquisitionConfig(SampleRateHz: sampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            byte[]? metersFrame = null;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (metersFrame is null)
            {
                byte[] frame = await reader.ReadAsync(readCts.Token);
                if (StreamFrame.DecodeHeader(frame).MsgType == StreamMsgType.Meters)
                    metersFrame = frame;
            }

            return StreamFrame.DecodeMeters(metersFrame);
        }
        finally
        {
            streamCts.Cancel();
            try { await drainTask; } catch (OperationCanceledException) { }
            await device.StopAsync();
            await pipeline.StopAsync();
        }
    }

    [Fact]
    public async Task ProcessRight_False_SuppressesRightChannelFrames()
    {
        // The device always samples both channels (real ADC hardware can't do otherwise) — this
        // proves the L/R checkboxes only gate DSP/broadcast, per SpectrumPipelineOptions.ProcessRight.
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 4096, TargetFrameRateHz = 40, DisplayColumns = 200, ProcessRight = false },
        };

        var (_, reader) = hub.Subscribe();
        pipeline.Start(sampleRateHz: 48000, channelCount: 2);

        var config = new AcquisitionConfig(SampleRateHz: 48000, ChannelCount: 2, BlockFrameCount: 4096);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            int metersFramesSeen = 0;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (metersFramesSeen < 3)
            {
                byte[] frame = await reader.ReadAsync(readCts.Token);
                var header = StreamFrame.DecodeHeader(frame);
                Assert.Equal(StreamChannel.Left, header.Channel);
                if (header.MsgType == StreamMsgType.Meters)
                    metersFramesSeen++;
            }
        }
        finally
        {
            streamCts.Cancel();
            try { await drainTask; } catch (OperationCanceledException) { }
            await device.StopAsync();
            await pipeline.StopAsync();
        }
    }

    [Fact]
    public async Task SingleShot_HoldsAfterReachingAverageCount()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]), ChannelGeneratorConfig.Off), CancellationToken.None);

        const int averageCount = 3;
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = 4096, TargetFrameRateHz = 40, DisplayColumns = 200,
                Averaging = AveragingMode.Linear, AverageCount = averageCount, SingleShot = true,
            },
        };

        var (_, reader) = hub.Subscribe();
        pipeline.Start(sampleRateHz: 48000, channelCount: 1);

        var config = new AcquisitionConfig(SampleRateHz: 48000, ChannelCount: 1, BlockFrameCount: 4096);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            // The averages-taken count must climb 1, 2, ... up to (and never past) averageCount,
            // and only the meters frame that reaches averageCount carries Held — a client (e.g. the
            // "Single" button's auto-stop) must be able to tell "done" apart from "still climbing".
            int lastAveragesTaken = 0;
            uint lastSeq = 0;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (lastAveragesTaken < averageCount)
            {
                byte[] frame = await reader.ReadAsync(readCts.Token);
                var header = StreamFrame.DecodeHeader(frame);
                lastSeq = header.Seq;
                if (header.MsgType != StreamMsgType.Meters)
                    continue;

                var meters = StreamFrame.DecodeMeters(frame);
                Assert.InRange((int)meters.AveragesTaken, lastAveragesTaken + 1, averageCount);
                lastAveragesTaken = (int)meters.AveragesTaken;

                bool expectHeld = lastAveragesTaken == averageCount;
                Assert.Equal(expectHeld, header.Flags.HasFlag(StreamFlags.Held));
            }

            // Held: every frame seen after this point must belong to the tick that just completed
            // (same seq) — a genuinely new tick's spectrum/meters/scope frame would carry a higher
            // seq, and none should arrive even though samples keep streaming in.
            using var holdCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                while (true)
                {
                    byte[] frame = await reader.ReadAsync(holdCts.Token);
                    Assert.Equal(lastSeq, StreamFrame.DecodeHeader(frame).Seq);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: the hold window elapsed with nothing but same-tick leftovers, if any.
            }
        }
        finally
        {
            streamCts.Cancel();
            try { await drainTask; } catch (OperationCanceledException) { }
            await device.StopAsync();
            await pipeline.StopAsync();
        }
    }

    [Fact]
    public async Task Pipeline_broadcasts_spectrum_and_meters_frames_for_a_known_tone()
    {
        const double sampleRateHz = 48000;
        const int fftSize = 4096;
        var tone = CoherentFrequency.Nearest(1000.0, sampleRateHz, fftSize);
        const double peakV = 1.0; // 1.0 V peak, Hann -> -3.01 dBV, per this app's golden-test convention.

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = fftSize, TargetFrameRateHz = 40, DisplayColumns = 200 },
        };

        var (_, reader) = hub.Subscribe();
        pipeline.Start(sampleRateHz, channelCount: 1);
        try
        {
            // Fill the ring buffer up front so the worker's very first tick already has a full window.
            const int blockFrames = 512;
            long totalFrames = 0;
            while (totalFrames < fftSize * 2)
            {
                float[] buffer = ArrayPool<float>.Shared.Rent(blockFrames);
                for (int i = 0; i < blockFrames; i++)
                {
                    double t = (totalFrames + i) / sampleRateHz;
                    buffer[i] = (float)(peakV * Math.Cos(2.0 * Math.PI * tone.FrequencyHz * t));
                }

                pipeline.Enqueue(new PcmBlock(buffer, blockFrames, sampleRateHz, channelCount: 1, sequenceNumber: totalFrames, timestampTicks: 0));
                totalFrames += blockFrames;
            }

            byte[]? spectrumFrame = null;
            byte[]? metersFrame = null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (spectrumFrame is null || metersFrame is null)
            {
                byte[] frame = await reader.ReadAsync(cts.Token);
                var header = StreamFrame.DecodeHeader(frame);
                if (header.MsgType == StreamMsgType.Spectrum)
                    spectrumFrame ??= frame;
                else if (header.MsgType == StreamMsgType.Meters)
                    metersFrame ??= frame;
            }

            var spectrumHeader = StreamFrame.DecodeHeader(spectrumFrame);
            Assert.True(spectrumHeader.Flags.HasFlag(StreamFlags.Decimated));
            Assert.True(spectrumHeader.Flags.HasFlag(StreamFlags.LogSpaced));
            Assert.Equal(200u, spectrumHeader.NPoints);

            var meters = StreamFrame.DecodeMeters(metersFrame);
            Assert.InRange(meters.FundamentalFreqHz, (float)tone.FrequencyHz - 5f, (float)tone.FrequencyHz + 5f);
            Assert.InRange(meters.FundamentalDbv, -3.5f, -2.5f);
            Assert.NotEmpty(meters.Harmonics);

            // The spectrum frame carries its own dBV values and used to be checked by nothing at
            // all — which is how it shipped 3.01 dB high (10*log10 of peak-volts-squared, missing
            // the /2 that makes it rms). Pin it to the same golden-test convention the meters frame
            // above is pinned to: 1.0 V peak reads -3.01 dBV, on the chart as well as in the meters.
            var columns = StreamFrame.DecodeSpectrumColumns(spectrumFrame);
            var peakColumn = columns.MaxBy(c => c.Value.Max);
            Assert.InRange(peakColumn.FreqHz, (float)tone.FrequencyHz - 20f, (float)tone.FrequencyHz + 20f);
            Assert.InRange(peakColumn.Value.Max, -3.5, -2.5);
        }
        finally
        {
            await pipeline.StopAsync();
        }
    }
}
