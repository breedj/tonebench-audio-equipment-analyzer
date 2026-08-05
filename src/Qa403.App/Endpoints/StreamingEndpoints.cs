using System.Net.WebSockets;
using System.Threading.Channels;
using Qa403.App.Streaming;

namespace Qa403.App.Endpoints;

/// <summary>Data-plane endpoint: docs/qa403-app-stack-spec.md §4.2, binary frames only.</summary>
public static class StreamingEndpoints
{
    public static void MapStreamingEndpoints(this WebApplication app)
    {
        app.Map("/ws/stream", async (HttpContext context, SpectrumBroadcastHub hub, ILogger<SpectrumBroadcastHub> logger) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var (id, reader) = hub.Subscribe();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            var receiveTask = DrainClientMessagesAsync(socket, cts);
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

    /// <summary>
    /// The stream is one-way (server -&gt; client) — no control messages are defined here, control
    /// is REST. This just drains whatever the client sends (browsers auto-reply to pings) and
    /// notices a client-initiated close so <see cref="PumpFramesAsync"/> stops promptly instead
    /// of blocking on a dead connection.
    /// </summary>
    private static async Task DrainClientMessagesAsync(WebSocket socket, CancellationTokenSource cts)
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
        }
    }
}
