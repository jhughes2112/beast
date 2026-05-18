using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

// WebSocket server transport for the Agent.  Listens on the given port, accepts exactly one client,
// and exposes the ITransportServer interface for the orchestrator.
public class TransportWebSocketServer : ITransportServer, IDisposable
{
    private readonly int _port;
    private readonly HttpListener _listener = new HttpListener();
    private WebSocket? _ws;
    private readonly Channel<string> _frames = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _writeTask;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public TransportWebSocketServer(int port)
    {
        _port = port;
    }

    // Starts listening, accepts the first client, begins the receive loop.
    // Blocks until a client connects.
    public async Task AcceptAsync(CancellationToken cancellationToken)
    {
        _listener.Prefixes.Add($"http://*:{_port}/");
        _listener.Start();
        Console.Error.WriteLine($"[ws-server] Listening on port {_port}");

        HttpListenerContext ctx = await _listener.GetContextAsync();
        if (!ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            throw new InvalidOperationException("Non-WebSocket connection rejected.");
        }

        HttpListenerWebSocketContext wsCtx = await ctx.AcceptWebSocketAsync(null);
        _ws = wsCtx.WebSocket;
        Console.Error.WriteLine($"[ws-server] Client connected from {ctx.Request.RemoteEndPoint}");

        _ = Task.Run(() => RecvLoop(_cts.Token), _cts.Token);
        _writeTask = Task.Run(() => WriteLoop(_cts.Token), _cts.Token);
    }

    private async Task RecvLoop(CancellationToken token)
    {
        byte[] buf = new byte[65536];
        Console.Error.WriteLine("[ws-server] RecvLoop started");
        try
        {
            while (!token.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.Error.WriteLine("[ws-server] Client sent close");
                    break;
                }

                string text = Encoding.UTF8.GetString(buf, 0, result.Count);
                Console.Error.WriteLine($"[ws-server] Received: '{text}'");
                _frames.Writer.TryWrite(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ws-server] RecvLoop error: {ex.Message}");
        }
        _frames.Writer.TryComplete();
    }

    public void Send(FrameType type, string text)
    {
        _outbound.Writer.TryWrite($"{(byte)type}|{text}");
    }

    private async Task WriteLoop(CancellationToken token)
    {
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(token))
            {
                while (_outbound.Reader.TryRead(out string? frame))
                {
                    if (_ws == null || _ws.State != WebSocketState.Open) continue;
                    byte[] bytes = Encoding.UTF8.GetBytes(frame);
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ws-server] WriteLoop error: {ex.Message}");
        }
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            bool ok = await _frames.Reader.WaitToReadAsync(cancellationToken);
            if (!ok) return null;
            _frames.Reader.TryRead(out string? frame);
            return frame;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    public void Dispose()
    {
        if (_ws != null && _ws.State == WebSocketState.Open)
        {
            try
            {
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch { }
        }
        _cts.Cancel();
        _outbound.Writer.TryComplete();
        _writeTask?.Wait(2000);
        _listener.Stop();
        _ws?.Dispose();
        _cts.Dispose();
    }
}
