using System;
using System.Collections.Generic;
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
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
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
    }

    private async Task RecvLoop(CancellationToken token)
    {
        byte[] buf = new byte[65536];
        List<byte> msg = new List<byte>(65536);
        Console.Error.WriteLine("[ws-server] RecvLoop started");
        try
        {
            while (!token.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.Error.WriteLine("[ws-server] Client sent close");
                    _frames.Writer.Complete();
                    return;
                }

                msg.AddRange(new ArraySegment<byte>(buf, 0, result.Count));

                if (result.EndOfMessage)
                {
                    string text = Encoding.UTF8.GetString(msg.ToArray());
                    msg.Clear();
                    Console.Error.WriteLine($"[ws-server] Received frame: '{text}'");
                    _frames.Writer.TryWrite(text);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ws-server] RecvLoop error: {ex.Message}");
        }
        _frames.Writer.TryComplete();
    }

    public void Send(FrameType type, string text)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;

        string frame = $"{(byte)type}|{text}";
        byte[] bytes = Encoding.UTF8.GetBytes(frame);

        _writeLock.Wait();
        try
        {
            _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        finally
        {
            _writeLock.Release();
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
        _cts.Cancel();
        _listener.Stop();
        _ws?.Dispose();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
