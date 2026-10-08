using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Streaming;
using Tonebench.Device;

namespace Tonebench.App.Tests.Streaming;

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

    private static SpectrumPipeline NewPipeline(
        SpectrumBroadcastHub hub, int displayColumns = 400, bool processRight = false) =>
        new(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = FftSize,
                TargetFrameRateHz = 100,
                DisplayColumns = displayColumns,
                DisplayLoFreqHz = 10.0,
                // ProcessRight defaults to false, so a stereo test has to ask for it or the right
                // channel is never analysed and never retained.
                ProcessRight = processRight,
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

        // 900-1100 Hz out of 10 Hz - 24 kHz is a ~39x reduction in log span, far past the 1.5x
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

    /// <summary>
    /// A 2x zoom used to change nothing on the wire: the threshold was 4x, so the chart re-scaled
    /// while the columns stayed exactly as coarse, which reads as zoom simply not working on the
    /// min/max band (reported 2026-10-08). 1.5x is the threshold now.
    /// </summary>
    [Fact]
    public async Task AModestZoom_NowEarnsItsOwnDenserFrame()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (id, reader) = hub.Subscribe();

        // 10 Hz - 24 kHz is ln(24000/10) = 7.78 of log span; 150 Hz - 24 kHz is 5.08, a factor of
        // 1.53 - over the new threshold and nowhere near the old one.
        hub.SetViewport(id, new SpectrumViewport(150.0, 24000.0));

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize);

        var header = await ReadSpectrumHeaderAsync(reader);

        Assert.True(header.Flags.HasFlag(StreamFlags.Zoomed));
        Assert.InRange(header.X0, 140.0, 150.0);
        await pipeline.StopAsync();
    }

    /// <summary>
    /// The case the whole thing exists for. A stopped pipeline never ticks again, so before this
    /// the chart kept the last overview frame at the resolution it already had -- with the
    /// full-resolution trace sitting in the retained snapshot, unused.
    /// </summary>
    [Fact]
    public async Task StoppedPipeline_RebuildsTheRetainedTraceOverTheRequestedWindow()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (liveId, liveReader) = hub.Subscribe();

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize);
        await ReadSpectrumHeaderAsync(liveReader);
        await pipeline.StopAsync();

        // A fresh subscriber, so the only frames in its channel are the ones this re-send puts
        // there rather than anything left over from while it was running.
        var (zoomId, zoomReader) = hub.Subscribe();
        var window = new SpectrumViewport(900.0, 1100.0);
        hub.SetViewport(zoomId, window);
        pipeline.ResendRetainedTo(zoomId, window);

        var header = await ReadSpectrumHeaderAsync(zoomReader);

        Assert.True(header.Flags.HasFlag(StreamFlags.Zoomed));
        Assert.InRange(header.X0, 890.0, 900.0);
        Assert.InRange(header.Dx, 1100.0, 1110.0);
        Assert.InRange(header.NPoints, 265u, 285u);
    }

    /// <summary>Zooming back out has to restore the overview, not leave the zoomed frame up.</summary>
    [Fact]
    public async Task StoppedPipeline_WithNoWindow_RebuildsTheOverview()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (liveId, liveReader) = hub.Subscribe();

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize);
        await ReadSpectrumHeaderAsync(liveReader);
        await pipeline.StopAsync();

        var (id, reader) = hub.Subscribe();
        pipeline.ResendRetainedTo(id, viewport: null);

        var header = await ReadSpectrumHeaderAsync(reader);

        Assert.False(header.Flags.HasFlag(StreamFlags.Zoomed));
        Assert.InRange(header.X0, 10.0f, 16.0f);
        Assert.InRange(header.Dx, 23990.0f, 24010.0f);
    }

    /// <summary>
    /// Both channels or none: the client draws R only when its point count and zoom flag match
    /// L's, so re-sending L alone would take R off the chart until the next RUN.
    /// </summary>
    [Fact]
    public async Task StoppedPipeline_ResendsEveryRetainedChannel()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub, processRight: true);
        var (liveId, liveReader) = hub.Subscribe();

        pipeline.Start(SampleRateHz, channelCount: 2);
        EnqueueStereoTone(pipeline, FftSize);
        await ReadSpectrumHeaderAsync(liveReader);
        await pipeline.StopAsync();

        var (id, reader) = hub.Subscribe();
        var window = new SpectrumViewport(900.0, 1100.0);
        pipeline.ResendRetainedTo(id, window);

        var first = await ReadSpectrumHeaderAsync(reader);
        var second = await ReadSpectrumHeaderAsync(reader);

        Assert.Equal(
            [StreamChannel.Left, StreamChannel.Right],
            new[] { first.Channel, second.Channel }.OrderBy(c => c).ToArray());
        // And they agree, which is what the client's L/R guard requires.
        Assert.Equal(first.NPoints, second.NPoints);
        Assert.Equal(first.Flags.HasFlag(StreamFlags.Zoomed), second.Flags.HasFlag(StreamFlags.Zoomed));
    }

    /// <summary>
    /// While running, the next tick answers the window. A re-send here would put a second frame
    /// carrying a seq the client has already seen on the wire, which is how a tick comes to look
    /// torn.
    /// </summary>
    [Fact]
    public async Task RunningPipeline_IgnoresTheResend()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (liveId, liveReader) = hub.Subscribe();

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize);
        await ReadSpectrumHeaderAsync(liveReader);

        // No viewport is registered for this subscriber, so every frame a *tick* sends it is an
        // overview. A zoomed frame could therefore only have come from the re-send — which is the
        // thing that must not happen while the worker is running.
        var (idleId, idleReader) = hub.Subscribe();
        pipeline.ResendRetainedTo(idleId, new SpectrumViewport(900.0, 1100.0));

        await Task.Delay(150);
        await pipeline.StopAsync();

        while (idleReader.TryRead(out byte[]? frame))
        {
            var header = StreamFrame.DecodeHeader(frame);
            if (header.MsgType == StreamMsgType.Spectrum)
                Assert.False(header.Flags.HasFlag(StreamFlags.Zoomed));
        }
    }

    /// <summary>
    /// The floor Joost asked for: zoom far enough and every column is a single bin, so its min and
    /// max are the same value and the band has nothing left to draw -- the raw FFT.
    /// </summary>
    [Fact]
    public async Task ATightZoom_ReachesOneBinPerColumnAndTheBandCollapses()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (liveId, liveReader) = hub.Subscribe();

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize);
        await ReadSpectrumHeaderAsync(liveReader);
        await pipeline.StopAsync();

        // 0.73 Hz per bin at 65536/48 kHz, so a 10 Hz window holds about fourteen of them --
        // far fewer than the 400-column budget.
        var (id, reader) = hub.Subscribe();
        pipeline.ResendRetainedTo(id, new SpectrumViewport(1000.0, 1010.0));

        byte[] frame = await ReadSpectrumFrameAsync(reader);
        var header = StreamFrame.DecodeHeader(frame);

        Assert.InRange(header.NPoints, 5u, 30u);
        for (int i = 0; i < header.NPoints; i++)
        {
            (_, float min, float max) = ColumnAt(frame, i);
            Assert.Equal(max, min);
        }
    }

    private static void EnqueueStereoTone(SpectrumPipeline pipeline, int framesPerChannel)
    {
        int sampleCount = framesPerChannel * 2;
        float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);
        for (int i = 0; i < framesPerChannel; i++)
        {
            var sample = (float)Math.Sin(2.0 * Math.PI * 1000.0 * i / SampleRateHz);
            buffer[i * 2] = sample;
            buffer[(i * 2) + 1] = sample;
        }

        pipeline.Enqueue(new PcmBlock(buffer, sampleCount, SampleRateHz, channelCount: 2, sequenceNumber: 0, timestampTicks: 0));
    }

    private static (float FreqHz, float Min, float Max) ColumnAt(byte[] frame, int index)
    {
        int offset = StreamFrame.HeaderLength + (index * 12);
        return (
            BitConverter.ToSingle(frame, offset),
            BitConverter.ToSingle(frame, offset + 4),
            BitConverter.ToSingle(frame, offset + 8));
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
