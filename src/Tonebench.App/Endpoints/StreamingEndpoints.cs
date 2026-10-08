using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Threading.Channels;
using Tonebench.App.Streaming;

namespace Tonebench.App.Endpoints;

/// <summary>Data-plane endpoint: docs/tonebench-stack-spec.md §4.2, binary frames only.</summary>
public static class StreamingEndpoints
{
    public static void MapStreamingEndpoints(this WebApplication app)
    {
        app.Map("/ws/stream", async (
            HttpContext context, SpectrumBroadcastHub hub, SpectrumPipeline pipeline,
            IHostApplicationLifetime lifetime, ILogger<SpectrumBroadcastHub> logger) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var (id, reader) = hub.Subscribe();

            // Frames only ever left on a tick, so a client that connected to a *stopped* pipeline
            // saw an empty chart until the next RUN — including the common case of reloading the
            // page to look at a capture that is sitting right there in the retained snapshot.
            // Queued before the pump starts, so it is the first thing this client is sent; a
            // no-op while running, where the next tick serves it anyway.
            pipeline.SendRetainedSnapshotTo(id);

            // Which browser is on the other end, once per client. It belongs in the log for the
            // same reason the OS line does: the client is half of this app, several of its faults
            // have been rendering ones (a ResizeObserver that never fires, a canvas that comes out
            // transparent), and a log sent in otherwise cannot say whether it was Chrome, Edge or
            // Firefox. This is also the one place that counts *clients* -- two tabs attached is a
            // real diagnosis here (it halves each one's frames and was a whole bench session once),
            // and the subscriber id is what shows it.
            logger.LogInformation(
                "Stream client {Id} connected from {Remote}: {UserAgent}",
                // ToString() because Serilog quotes a Guid and renders a string bare, and this line
                // is prose rather than structured output.
                id.ToString(),
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : "no user agent");

            // ApplicationStopping as well as RequestAborted. A WebSocket is a request that never
            // completes on its own, and Kestrel drains in-flight requests before it will stop — so
            // without this token the shutdown sat here for the entire ShutdownTimeout with a browser
            // tab open, and only then began tearing the device down. Measured: a 3.018 s gap between
            // "Application is shutting down..." and the first device teardown line, i.e. the whole
            // budget spent waiting on this socket, leaving the QA403 teardown running on borrowed
            // time past the host's own deadline.
            //
            // Kestrel stops first because GenericWebHostService is registered during
            // WebApplicationBuilder.Build(), *after* AddHostedService<DeviceLifecycleHostedService>()
            // in Program.cs — and hosted services stop in reverse registration order. Adding the
            // device service later does not change that, so the socket has to yield instead.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, lifetime.ApplicationStopping);

            var receiveTask = ReceiveClientMessagesAsync(socket, hub, pipeline, id, cts);
            try
            {
                await PumpFramesAsync(socket, reader, cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // Client disconnected or the request was aborted — not an error.
            }
            finally
            {
                hub.Unsubscribe(id);
                cts.Cancel();
                try
                {
                    await receiveTask;
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                {
                }
            }
        });
    }

    private static async Task PumpFramesAsync(WebSocket socket, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var frame))
            {
                if (socket.State != WebSocketState.Open)
                    return;

                await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Client -&gt; server control message kinds. Everything else stays on REST.</summary>
    private enum ClientControlType : byte
    {
        /// <summary>
        /// <c>u8 type, f32 loFreqHz, f32 hiFreqHz</c> (little-endian, 9 bytes) — the client's
        /// current zoom window. A degenerate range (hi &lt;= lo) clears it, putting the client back
        /// on the shared overview frame. See <see cref="SpectrumBroadcastHub.SetViewport"/>.
        /// </summary>
        SetViewport = 1,
    }

    private const int SetViewportMessageLength = 9;

    /// <summary>
    /// Handles the one control message this stream accepts (viewport updates — see
    /// <see cref="ClientControlType"/>; everything else is REST), ignores anything unrecognized,
    /// and notices a client-initiated close so <see cref="PumpFramesAsync"/> stops promptly
    /// instead of blocking on a dead connection.
    /// </summary>
    private static async Task ReceiveClientMessagesAsync(
        WebSocket socket, SpectrumBroadcastHub hub, SpectrumPipeline pipeline, Guid clientId,
        CancellationTokenSource cts)
    {
        var buffer = new byte[256];
        while (!cts.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                cts.Cancel();
                return;
            }

            // Deliberately lenient: an unknown or malformed control message is dropped rather than
            // killing the stream. This socket carries the live display; a client sending junk
            // should lose its zoom, not its data.
            if (result.MessageType != WebSocketMessageType.Binary
                || !result.EndOfMessage
                || result.Count != SetViewportMessageLength
                || buffer[0] != (byte)ClientControlType.SetViewport)
            {
                continue;
            }

            float loFreqHz = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(1, 4));
            float hiFreqHz = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(5, 4));

            bool valid = float.IsFinite(loFreqHz) && float.IsFinite(hiFreqHz) && hiFreqHz > loFreqHz && loFreqHz >= 0.0f;
            var viewport = valid ? new SpectrumViewport(loFreqHz, hiFreqHz) : null;
            hub.SetViewport(clientId, viewport);

            // A running capture answers the new window on its next tick. A stopped one never ticks
            // again, so without this the chart would keep showing the frame it already had, at the
            // resolution it already had, while the full trace sat in the pipeline's retained
            // snapshot. Also on the invalid branch above, which clears the viewport: zooming back
            // out has to restore the overview, not leave the last zoomed frame on screen.
            pipeline.ResendRetainedTo(clientId, viewport);
        }
    }
}
