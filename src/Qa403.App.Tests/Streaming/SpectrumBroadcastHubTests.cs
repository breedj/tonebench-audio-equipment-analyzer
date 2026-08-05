using System.Diagnostics;
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
