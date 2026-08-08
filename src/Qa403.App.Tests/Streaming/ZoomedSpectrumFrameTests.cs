using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Streaming;
using Qa403.Device;

namespace Qa403.App.Tests.Streaming;

/// <summary>
/// A zoomed-in client gets the full column budget spread over just its window, so it resolves
/// detail the shared overview frame cannot. Clients that are not zoomed (the usual case) keep
/// taking the single broadcast overview, so nothing extra is encoded for them.
/// </summary>
public class ZoomedSpectrumFrameTests
{
    // A realistic size on purpose. At small FFTs the decimator's "never narrower than one bin"
    // clamp consumes most of the column budget below a few hundred Hz, which flatters the overview
    // in exactly the range these tests measure and understates what zooming buys.
    private const int FftSize = 65536;
    private const double SampleRateHz = 48000.0;

    private static SpectrumPipeline NewPipeline(SpectrumBroadcastHub hub, int displayColumns = 400) =>
        new(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = FftSize,
                TargetFrameRateHz = 100,
                DisplayColumns = displayColumns,
                DisplayLoFreqHz = 10.0,
            },
        };

    private static void EnqueueTone(SpectrumPipeline pipeline, int sampleCount)
    {
        float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);
        for (int i = 0; i < sampleCount; i++)
            buffer[i] = (float)Math.Sin(2.0 * Math.PI * 1000.0 * i / SampleRateHz);

        pipeline.Enqueue(new PcmBlock(buffer, sampleCount, SampleRateHz, channelCount: 1, sequenceNumber: 0, timestampTicks: 0));
    }

    private static async Task<StreamFrameHeader> ReadSpectrumHeaderAsync(ChannelReader<byte[]> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            byte[] frame = await reader.ReadAsync(cts.Token);
            var header = StreamFrame.DecodeHeader(frame);
            if (header.MsgType == StreamMsgType.Spectrum)
                return header;
        }
    }

    [Fact]
    public async Task NoViewport_YieldsAnOverviewFrameSpanningTheWholeSpectrum()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (_, reader) = hub.Subscribe();

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize);

        var header = await ReadSpectrumHeaderAsync(reader);

        Assert.False(header.Flags.HasFlag(StreamFlags.Zoomed));
        // The first bin at or above DisplayLoFreqHz (10 Hz), quantized to the 5.86 Hz grid.
        Assert.InRange(header.X0, 10.0f, 16.0f);
        Assert.InRange(header.Dx, 23990.0f, 24010.0f);          // Nyquist
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task ZoomedViewport_YieldsAFrameCoveringOnlyThatWindow()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (id, reader) = hub.Subscribe();

        // 900-1100 Hz out of 10 Hz - 24 kHz is a ~39x reduction in log span, well past the 4x
        // threshold at which a private frame becomes worth encoding.
        hub.SetViewport(id, new SpectrumViewport(900.0, 1100.0));

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize);

        var header = await ReadSpectrumHeaderAsync(reader);

        Assert.True(header.Flags.HasFlag(StreamFlags.Zoomed));
        Assert.InRange(header.X0, 890.0, 900.0);
        Assert.InRange(header.Dx, 1100.0, 1110.0);

        // The whole budget now covers 200 Hz instead of 24 kHz. The window holds ~275 bins, fewer
        // than DisplayColumns, so the frame is capped at the bin count rather than padded out with
        // columns that would just repeat single bins.
        Assert.InRange(header.NPoints, 265u, 285u);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task ZoomedFrameResolvesManyMoreColumnsInsideTheWindowThanTheOverviewDoes()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub, displayColumns: 400);

        var (overviewId, overviewReader) = hub.Subscribe();
        var (zoomId, zoomReader) = hub.Subscribe();
        hub.SetViewport(zoomId, new SpectrumViewport(1000.0, 4000.0));

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize);

        var overview = await ReadSpectrumFrameAsync(overviewReader);
        var zoomed = await ReadSpectrumFrameAsync(zoomReader);

        Assert.False(StreamFrame.DecodeHeader(overview).Flags.HasFlag(StreamFlags.Zoomed));
        Assert.True(StreamFrame.DecodeHeader(zoomed).Flags.HasFlag(StreamFlags.Zoomed));

        int overviewInWindow = CountColumnsBetween(overview, 1000.0, 4000.0);
        int zoomedInWindow = CountColumnsBetween(zoomed, 1000.0, 4000.0);

        // 1-4 kHz is two octaves of a ~11-octave span, so the overview spends only ~1/5.5 of its
        // columns there while the zoomed frame spends all of them.
        Assert.True(
            zoomedInWindow > overviewInWindow * 3,
            $"zoomed frame should resolve far more inside the window: {zoomedInWindow} vs {overviewInWindow}");

        // And the client that never zoomed is unaffected.
        Assert.Equal(400u, StreamFrame.DecodeHeader(overview).NPoints);
        await pipeline.StopAsync();
    }

    private static async Task<byte[]> ReadSpectrumFrameAsync(ChannelReader<byte[]> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            byte[] frame = await reader.ReadAsync(cts.Token);
            if (StreamFrame.DecodeHeader(frame).MsgType == StreamMsgType.Spectrum)
                return frame;
        }
    }

    /// <summary>Counts decimated columns whose own centre frequency falls inside [lo, hi].</summary>
    private static int CountColumnsBetween(byte[] frame, double loHz, double hiHz)
    {
        var header = StreamFrame.DecodeHeader(frame);
        int count = 0;
        for (int i = 0; i < header.NPoints; i++)
        {
            float freqHz = BitConverter.ToSingle(frame, StreamFrame.HeaderLength + (i * 12));
            if (freqHz >= loHz && freqHz <= hiHz)
                count++;
        }

        return count;
    }
}
