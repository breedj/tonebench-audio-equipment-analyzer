using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Qa403.App.Streaming;

/// <summary>
/// One client's current zoom window, in Hz. A record class rather than a struct so the hub can
/// publish it with a single atomic reference assignment — it is written by that client's WebSocket
/// receive loop and read by the DSP worker thread.
/// </summary>
public sealed record SpectrumViewport(double LoFreqHz, double HiFreqHz);

/// <summary>
/// Fans one acquisition/DSP source out to N connected WebSocket clients. Each client gets its
/// own bounded <see cref="Channel{T}"/> with <see cref="BoundedChannelFullMode.DropOldest"/>:
/// <see cref="Broadcast"/> is a plain <c>TryWrite</c> per client, which never blocks and never
/// throws on a full channel — it silently evicts that client's oldest unread frame instead.
/// A slow or stalled client therefore can never stall the acquisition/DSP loop calling
/// <see cref="Broadcast"/>, and cannot affect any other connected client.
///
/// Most frames are identical for everyone and go out via <see cref="Broadcast"/>. Spectrum frames
/// are the exception: a client that has zoomed in reports its window through
/// <see cref="SetViewport"/>, and the pipeline then encodes a denser frame just for it and
/// delivers it with <see cref="Send"/>. See <see cref="StreamFlags.Zoomed"/>.
/// </summary>
public sealed class SpectrumBroadcastHub(int capacityPerClient = 8)
{
    private sealed class Client(Channel<byte[]> channel)
    {
        public Channel<byte[]> Channel { get; } = channel;

        /// <summary>Null means "not zoomed" — this client takes the shared overview frame.</summary>
        public volatile SpectrumViewport? Viewport;
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    public int ClientCount => _clients.Count;

    /// <summary>Registers a new client and returns its id (for <see cref="Unsubscribe"/>) and its frame reader.</summary>
    public (Guid Id, ChannelReader<byte[]> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacityPerClient)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var id = Guid.NewGuid();
        _clients[id] = new Client(channel);
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_clients.TryRemove(id, out var client))
            client.Channel.Writer.TryComplete();
    }

    /// <summary>
    /// Sets (or with <c>null</c>, clears) the client's zoom window. Unknown ids are ignored — the
    /// client may have disconnected between sending the message and it being processed.
    /// </summary>
    public void SetViewport(Guid id, SpectrumViewport? viewport)
    {
        if (_clients.TryGetValue(id, out var client))
            client.Viewport = viewport;
    }

    /// <summary>
    /// Snapshot of every connected client and the zoom window it last asked for. Used by the
    /// pipeline to decide which frames it has to encode this tick.
    /// </summary>
    public (Guid Id, SpectrumViewport? Viewport)[] ClientViewports()
    {
        var clients = _clients.ToArray();
        var result = new (Guid, SpectrumViewport?)[clients.Length];
        for (int i = 0; i < clients.Length; i++)
            result[i] = (clients[i].Key, clients[i].Value.Viewport);

        return result;
    }

    /// <summary>Sends <paramref name="frame"/> to one client. Same non-blocking, drop-oldest contract as <see cref="Broadcast"/>.</summary>
    public void Send(Guid id, byte[] frame)
    {
        if (_clients.TryGetValue(id, out var client))
            client.Channel.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Sends <paramref name="frame"/> to every currently subscribed client. Never blocks: a
    /// client at capacity has its oldest queued frame dropped to make room, per
    /// <see cref="BoundedChannelFullMode.DropOldest"/>.
    /// </summary>
    public void Broadcast(byte[] frame)
    {
        foreach (var (_, client) in _clients)
            client.Channel.Writer.TryWrite(frame);
    }
}
