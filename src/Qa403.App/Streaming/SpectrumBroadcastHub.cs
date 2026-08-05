using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Qa403.App.Streaming;

/// <summary>
/// Fans one acquisition/DSP source out to N connected WebSocket clients. Each client gets its
/// own bounded <see cref="Channel{T}"/> with <see cref="BoundedChannelFullMode.DropOldest"/>:
/// <see cref="Broadcast"/> is a plain <c>TryWrite</c> per client, which never blocks and never
/// throws on a full channel — it silently evicts that client's oldest unread frame instead.
/// A slow or stalled client therefore can never stall the acquisition/DSP loop calling
/// <see cref="Broadcast"/>, and cannot affect any other connected client.
/// </summary>
public sealed class SpectrumBroadcastHub(int capacityPerClient = 8)
{
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _clients = new();

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
        _clients[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_clients.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    /// <summary>
    /// Sends <paramref name="frame"/> to every currently subscribed client. Never blocks: a
    /// client at capacity has its oldest queued frame dropped to make room, per
    /// <see cref="BoundedChannelFullMode.DropOldest"/>.
    /// </summary>
    public void Broadcast(byte[] frame)
    {
        foreach (var (_, channel) in _clients)
            channel.Writer.TryWrite(frame);
    }
}
