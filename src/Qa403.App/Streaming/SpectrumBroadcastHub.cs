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
///
/// Every eviction is counted per client (<see cref="DroppedFrames"/>), because a dropped frame is
/// not a skipped update: the frames of one tick are a *set* the client reassembles by seq, so
/// losing one of a tick's two scope frames produces exactly the symptom a fit-latency bug produced
/// on 2026-09-28 — an oscilloscope alternating between "no fundamental to remove" and an empty
/// plot. Without a counter the two are indistinguishable, which is the failure mode CLAUDE.md's
/// rule about the two RX drop counters is about. See <see cref="DefaultCapacityPerClient"/>.
/// </summary>
public sealed class SpectrumBroadcastHub(int capacityPerClient = SpectrumBroadcastHub.DefaultCapacityPerClient)
{
    /// <summary>
    /// Frames one client may have queued before its oldest is evicted. Sized in <i>ticks</i> rather
    /// than as a round number of frames: the frames of one tick are a set the client reassembles by
    /// seq (<c>useSpectrumStream</c> pairs the two scope frames of a seq and closes the group on a
    /// 40 ms idle), so a capacity that cannot hold a whole tick turns any scheduling hiccup into a
    /// torn tick instead of merely a stale display. 64 is the power of two above three whole ticks
    /// (3 × 12 = 36).
    ///
    /// <para>Measured rather than counted off the call sites, because the source allows a tick the
    /// client never asks for: <c>App.vue</c>'s IMD watcher makes a spec only for exactly two sine
    /// tones and clears it for anything else, so the intermodulation and multitone frames are
    /// mutually exclusive and the real worst case is <b>6 frames per channel</b> — noise figures,
    /// spectrum, meters, one of IMD/multitone, scope, scope residual — i.e. <b>12 per tick</b> for
    /// the usual two channels, not 14. Measured on the FFT screen at 4096 points / 192 kHz with the
    /// CCIF two-tone preset and the THD residual on, both channels: 12 frames per tick, every tick,
    /// at 12.57 ticks/s.</para>
    ///
    /// <para>This was <b>8</b> until 2026-10-02, which is why the frames are emitted in two
    /// per-channel bursts of 6 and it mostly survived: each burst on its own fits, and the client
    /// drains during the whole FFT that separates them. It does not survive a reader that misses
    /// both bursts, which is the 44–125 ms of thread-pool continuation parking <c>Program.cs</c>
    /// documents — 0.5 to 1.5 ticks here. Measured with every core saturated, two ordinary browser
    /// clients attached: <b>335 frames evicted from each over ~70 s</b>, about 3 % of the 151
    /// frames/s being broadcast, stopping dead the moment the load ended. Idle, at the full tick
    /// rate, and with one client, it was 0 throughout — so this is a contention failure, not a
    /// steady-state one, and it is invisible without the counter. At 64, the same load against the
    /// same two clients and the same settings evicted <b>nothing at all</b>.</para>
    ///
    /// <para>Deliberately not deeper still. <see cref="BoundedChannelFullMode.DropOldest"/> on a
    /// shallow queue is the right trade for a live display — newest frame wins — and depth buys
    /// latency, which for an oscilloscope is the wrong currency. The queue only fills when the
    /// reader is behind, so what capacity really sets is how much a recovering client replays
    /// before it is showing the present again.</para>
    /// </summary>
    public const int DefaultCapacityPerClient = 64;

    /// <summary>
    /// A client's eviction count, a class of its own because the drop callback has to be handed to
    /// <see cref="Channel.CreateBounded{T}(BoundedChannelOptions, Action{T})"/> before the
    /// <see cref="Client"/> that owns the channel can be constructed.
    /// </summary>
    private sealed class DropCount
    {
        private long _value;

        public long Value => Interlocked.Read(ref _value);

        public void Increment() => Interlocked.Increment(ref _value);

        public void Reset() => Interlocked.Exchange(ref _value, 0);
    }

    private sealed class Client(Channel<byte[]> channel, DropCount dropped)
    {
        public Channel<byte[]> Channel { get; } = channel;

        /// <summary>Frames evicted from this client's queue since the last <see cref="ResetDroppedFrames"/>.</summary>
        public DropCount Dropped { get; } = dropped;

        /// <summary>Null means "not zoomed" — this client takes the shared overview frame.</summary>
        public volatile SpectrumViewport? Viewport;
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    public int ClientCount => _clients.Count;

    /// <summary>Registers a new client and returns its id (for <see cref="Unsubscribe"/>) and its frame reader.</summary>
    public (Guid Id, ChannelReader<byte[]> Reader) Subscribe()
    {
        // The itemDropped callback rather than checking TryWrite's bool: it is the only thing that
        // sees an eviction made by Send as well as by Broadcast, it runs on whichever thread did the
        // writing (so there is nothing to sample or poll), and it cannot be forgotten at a new call
        // site the way the discarded bool was forgotten at both of the existing ones.
        var dropped = new DropCount();
        var channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(capacityPerClient)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => dropped.Increment());

        var id = Guid.NewGuid();
        _clients[id] = new Client(channel, dropped);
        return (id, channel.Reader);
    }

    /// <summary>
    /// The worst single client's eviction count, and the sum over every connected client. Both,
    /// because the hub's whole design point is that one client cannot affect another: a wedged
    /// browser tab says nothing about the others, so a sum alone cannot distinguish "one stalled
    /// tab" (harmless to everyone else) from "every client is losing frames" (what is on screen is
    /// torn). Counts only *currently connected* clients — a tab that has gone away is not showing
    /// anything there is a question about.
    /// </summary>
    public (long Max, long Total) DroppedFrames()
    {
        long max = 0, total = 0;
        foreach (var (_, client) in _clients)
        {
            long dropped = client.Dropped.Value;
            total += dropped;
            if (dropped > max)
                max = dropped;
        }

        return (max, total);
    }

    /// <summary>
    /// Zeroes every client's eviction count. Called from <see cref="SpectrumPipeline.Start"/>, so
    /// the figure is per acquisition for the reason the device's own two drop counters are: the only
    /// question these numbers are ever asked is whether the measurement on screen can be trusted,
    /// and a running total cannot answer it — a stale 8 from an earlier run reads exactly like 8
    /// that have just happened.
    /// </summary>
    public void ResetDroppedFrames()
    {
        foreach (var (_, client) in _clients)
            client.Dropped.Reset();
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
    /// <see cref="BoundedChannelFullMode.DropOldest"/> — and counted, see
    /// <see cref="DroppedFrames"/>.
    /// </summary>
    public void Broadcast(byte[] frame)
    {
        foreach (var (_, client) in _clients)
            client.Channel.Writer.TryWrite(frame);
    }
}
