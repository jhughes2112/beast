using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// WebSocket client connection to the Agent container.
// Connects to ws://localhost:port/ and exchanges plain text messages.
public class TransportClientWebsocket : ITransportClient, IDisposable
{
    private readonly string _url;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

    public TransportClientWebsocket(string url)
    {
        _url = url;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        Console.Error.WriteLine($"[ws-client] Connecting to {_url}");
        await _ws.ConnectAsync(new Uri(_url), cancellationToken);
        Console.Error.WriteLine($"[ws-client] Connected");
    }

    // Sends a plain text message over the websocket.
    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Reads one complete WebSocket message. Returns null on close/error.
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return null;
        byte[] buf = new byte[65536];
        System.Collections.Generic.List<byte> msg = new System.Collections.Generic.List<byte>(65536);
        try
        {
            while (true)
            {
                WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                msg.AddRange(new ArraySegment<byte>(buf, 0, result.Count));
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(msg.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ws-client] ReceiveAsync error: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _ws?.Dispose();
        _writeLock.Dispose();
    }
}
