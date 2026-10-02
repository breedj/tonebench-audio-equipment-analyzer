using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Streaming;

namespace Qa403.App.Tests.Streaming;

public class SpectrumBroadcastHubTests
{
    [Fact]
    public void Broadcast_never_blocks_on_a_stalled_reader_and_does_not_affect_other_clients()
    {
        var hub = new SpectrumBroadcastHub(capacityPerClient: 4);
        var (_, stalledReader) = hub.Subscribe(); // deliberately never read from during the broadcast loop
        var (_, activeReader) = hub.Subscribe();

        const int frameCount = 5000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frameCount; i++)
            hub.Broadcast([(byte)(i % 256), (byte)((i >> 8) % 256)]);
        sw.Stop();

        // A blocking producer would stall for as long as the stalled reader never drains;
        // this must complete near-instantly regardless of client count or reader behavior.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Broadcast of {frameCount} frames took {sw.ElapsedMilliseconds} ms — producer appears blocked on a stalled reader.");

        // The active client still gets fanned-out data.
        var active = DrainAll(activeReader);
        Assert.NotEmpty(active);
        Assert.True(active.Count <= 4, "Active client queue exceeded its bounded capacity.");
        AssertIsSequenceNumber(active[^1], frameCount - 1);

        // The stalled client, now finally drained, must hold at most its bounded capacity —
        // proving drop-oldest capped memory instead of the channel growing to 5000 entries —
        // and its last entry must be the most recent frame broadcast, not a stale one.
        var stalled = DrainAll(stalledReader);
        Assert.True(stalled.Count <= 4, $"Stalled client accumulated {stalled.Count} frames; expected drop-oldest to cap it at 4.");
        Assert.NotEmpty(stalled);
        AssertIsSequenceNumber(stalled[^1], frameCount - 1);
    }

    [Fact]
    public void Unsubscribe_stops_further_delivery_and_completes_the_reader()
    {
        var hub = new SpectrumBroadcastHub();
        var (id, reader) = hub.Subscribe();

        hub.Broadcast([1]);
        hub.Unsubscribe(id);
        hub.Broadcast([2]);

        var received = DrainAll(reader);
        Assert.Single(received);
        Assert.Equal(1, received[0][0]);
        Assert.True(reader.Completion.IsCompleted);
    }

    /// <summary>
    /// The eviction the hub's whole contract is built on has to be *countable*. It was invisible to
    /// producer and consumer alike — <c>TryWrite</c>'s bool discarded at both call sites — and a
    /// transport that silently loses one of a tick's frames produces the same oscilloscope flicker
    /// as a server-side cause, with nothing to tell them apart. Per client as well as summed,
    /// because the hub exists so that one stalled client cannot affect another.
    /// </summary>
    [Fact]
    public void Evicted_frames_are_counted_per_client_and_in_total()
    {
        var hub = new SpectrumBroadcastHub(capacityPerClient: 4);
        var (_, stalledReader) = hub.Subscribe();
        var (_, activeReader) = hub.Subscribe();

        const int frameCount = 20;
        for (int i = 0; i < frameCount; i++)
        {
            hub.Broadcast([(byte)i]);

            // Kept drained, so this client never reaches capacity and never loses a frame.
            while (activeReader.TryRead(out _)) { }
        }

        var (max, total) = hub.DroppedFrames();

        // The stalled client keeps its last 4 and loses the rest; the drained one loses nothing. So
        // the max is that client's own count and the total is the same number — asserting both
        // against one stalled client is what pins "max is not just the total".
        Assert.Equal(frameCount - 4, max);
        Assert.Equal(frameCount - 4, total);

        // And its queue really is capped at capacity, i.e. the drops are evictions rather than
        // writes that never happened.
        Assert.Equal(4, DrainAll(stalledReader).Count);
    }

    /// <summary>
    /// <c>Send</c> writes to the same per-client queue, so its evictions have to be counted too —
    /// this is the call site a bool-returning check would be forgotten at next.
    /// </summary>
    [Fact]
    public void Send_evictions_are_counted_as_well_as_broadcast_ones()
    {
        var hub = new SpectrumBroadcastHub(capacityPerClient: 2);
        var (id, _) = hub.Subscribe();

        for (int i = 0; i < 5; i++)
            hub.Send(id, [(byte)i]);

        Assert.Equal(3, hub.DroppedFrames().Total);
    }

    /// <summary>
    /// Per acquisition, like the device's own two drop counters: a running total cannot answer the
    /// only question this number is ever asked, because a stale count from an earlier run reads
    /// exactly like one that has just happened.
    /// </summary>
    [Fact]
    public void ResetDroppedFrames_zeroes_every_clients_count()
    {
        var hub = new SpectrumBroadcastHub(capacityPerClient: 1);
        hub.Subscribe();
        hub.Subscribe();

        for (int i = 0; i < 4; i++)
            hub.Broadcast([(byte)i]);
        Assert.Equal(6, hub.DroppedFrames().Total);

        hub.ResetDroppedFrames();
        Assert.Equal((0L, 0L), hub.DroppedFrames());
    }

    /// <summary>
    /// Pins the one call site that makes the count per acquisition. Every runner starts its own
    /// acquisition and the FFT screen's Run is the same call, so this is the whole of "zeroed per
    /// measurement" — a reset wired anywhere else would leave some kind of run reporting the
    /// previous one's drops.
    /// </summary>
    [Fact]
    public async Task Starting_an_acquisition_zeroes_the_counts()
    {
        var hub = new SpectrumBroadcastHub(capacityPerClient: 1);
        hub.Subscribe();

        hub.Broadcast([1]);
        hub.Broadcast([2]);
        hub.Broadcast([3]);
        Assert.Equal(2, hub.DroppedFrames().Total);

        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 4096, DisplayColumns = 100 },
        };
        pipeline.Start(sampleRateHz: 48000.0, channelCount: 1);
        try
        {
            Assert.Equal(0, hub.DroppedFrames().Total);
        }
        finally
        {
            await pipeline.StopAsync();
        }
    }

    [Fact]
    public void Broadcast_with_no_subscribers_is_a_no_op()
    {
        var hub = new SpectrumBroadcastHub();
        var exception = Record.Exception(() => hub.Broadcast([1, 2, 3]));
        Assert.Null(exception);
    }

    private static List<byte[]> DrainAll(System.Threading.Channels.ChannelReader<byte[]> reader)
    {
        var result = new List<byte[]>();
        while (reader.TryRead(out var frame))
            result.Add(frame);
        return result;
    }

    private static void AssertIsSequenceNumber(byte[] frame, int expected)
    {
        int actual = frame[0] | (frame[1] << 8);
        Assert.Equal(expected % 65536, actual);
    }
}
