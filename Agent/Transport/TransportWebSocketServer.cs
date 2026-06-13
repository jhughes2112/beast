using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

// WebSocket server transport for the Agent.  Listens on the given port, accepts exactly one client,
// and exposes the ITransportServer interface for the orchestrator.
public class TransportWebSocketServer : ITransportServer, IDisposable
{
    private readonly int _port;
    private HttpListener _listener = new HttpListener();
    private WebSocket? _ws;
    private readonly Channel<string> _frames = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _writeTask;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public TransportWebSocketServer(int port)
    {
        _port = port;
    }

    // Registers the http://*:{port}/ URL ACL via an elevated netsh call (UAC prompt).
    // Returns true if the ACL was added successfully, false if the user cancelled.
    private bool EnsureUrlAcl()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"http add urlacl url=http://*:{_port}/ user=Everyone",
            Verb = "runas",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using Process p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    // Starts listening, accepts the first client, begins the receive loop.
    // Blocks until a client connects.
    public async Task AcceptAsync(CancellationToken cancellationToken)
    {
        _listener.Prefixes.Add($"http://*:{_port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5 && OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("[ws-server] Access denied — requesting URL ACL registration (UAC prompt)...");
            if (!EnsureUrlAcl())
                throw new InvalidOperationException($"Could not register URL ACL for port {_port}. User cancelled UAC prompt.");
            // The failed Start() leaves the listener in a broken state; replace it.
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{_port}/");
            _listener.Start();
        }
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

        _ = RecvLoop(_cts.Token);
        _writeTask = WriteLoop(_cts.Token);
    }

    private async Task RecvLoop(CancellationToken token)
    {
        byte[] buf = new byte[65536];
        System.Collections.Generic.List<byte> msg = new System.Collections.Generic.List<byte>(65536);
        try
        {
            while (!token.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                // Accumulate fragments until EndOfMessage so large messages arrive intact.
                msg.AddRange(new ArraySegment<byte>(buf, 0, result.Count));
                if (!result.EndOfMessage) continue;

                string text = Encoding.UTF8.GetString(msg.ToArray());
                msg.Clear();
                _frames.Writer.TryWrite(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ws-server] RecvLoop error: {ex.Message}");
            try { Error("", $"WebSocket receive error: {ex.Message}"); } catch { }
        }
        _frames.Writer.TryComplete();
    }

    private void Send(FrameType type, string sessionId, string text)
    {
        // Wire format: N|sessionId|content (empty sessionId produces N||content for orchestrator frames).
        _outbound.Writer.TryWrite($"{(byte)type}|{sessionId}|{text}");
    }

    public void Output(string sessionId, string text)      => Send(FrameType.Output,      sessionId, text);
    public void Error(string sessionId, string text)       => Send(FrameType.Error,        sessionId, text);
    public void Status(string sessionId, string text)      => Send(FrameType.Status,       sessionId, text);
    public void Thinking(string sessionId, string text)    => Send(FrameType.Thinking,     sessionId, text);
    public void System(string sessionId, string text)      => Send(FrameType.System,       sessionId, text);
    public void User(string sessionId, string text)        => Send(FrameType.User,         sessionId, text);
    public void Debug(string sessionId, string text)       => Send(FrameType.Debug,        sessionId, text);
    public void Stats(string sessionId, string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens)
        => Send(FrameType.Stats, sessionId, JsonSerializer.Serialize(new
            { model, role, promptTokens, completionTokens, totalCost, maxContext, contextTokens }));
    public void Completions(string sessionId, string json) => Send(FrameType.Completions,  sessionId, json);
    public void Idle(string sessionId, bool subagent)      => Send(FrameType.Idle,         sessionId, subagent ? "subagent" : string.Empty);
    public void Busy(string sessionId)                     => Send(FrameType.Busy,         sessionId, string.Empty);
    public void ToolCallWithId(string sessionId, string callId, string text)  => Send(FrameType.ToolCall,     sessionId, callId + "\x01" + text);
    public void ToolResponseWithId(string sessionId, string callId, ToolResult result) => Send(FrameType.ToolResponse, sessionId, callId + "\x01" + result.ExitCode + "\x01" + result.StdOut + "\x01" + result.StdErr);
    public void SessionAnnounce(string sessionId, string json) => Send(FrameType.SessionAnnounce, sessionId, json);
    public void StreamStart(string sessionId, string tag)  => Send(FrameType.StreamStart,  sessionId, tag);
    public void StreamChunk(string sessionId, string chunk) => Send(FrameType.StreamChunk, sessionId, chunk);
    public void StreamEnd(string sessionId, string tag)    => Send(FrameType.StreamEnd,    sessionId, tag);

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

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
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
