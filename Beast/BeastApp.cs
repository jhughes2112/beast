using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;


// Beast app: launches the Agent container and drives a session via the provided display.
public class BeastApp : IDisposable, IAsyncDisposable
{
    private readonly string _image;
    private readonly List<string> _messages;
    private readonly IDisplay _display;
    private readonly Log _log;
    private string? _idleSoundFile;

    private CancellationTokenSource? _cts;
    private ConversationModel? _model;
    private ITransportClient? _wsClient;
    private DockerContext? _docker;
    private string? _containerId;

    // Read loop state
    private Task? _readTask;
    private CancellationTokenSource? _readCts;
    private int _nextIndex = 0;
    private int _streamIndex = -1;
    private Dictionary<string, int> _streamTagToSlot = new Dictionary<string, int>();  // tag → slot
    private Dictionary<int, FrameType> _slotTypes = new Dictionary<int, FrameType>();     // slot → type
    private Dictionary<FrameType, int> _pendingCommit = new Dictionary<FrameType, int>();  // type → slot to reuse
    private string _streamContent = "";
    private string? _pingNonce;
    private bool _readyFired;
    // Inbound frames queued by ReadLoop and drained under _consoleLock by DrainFrameQueue().
    private readonly System.Collections.Concurrent.ConcurrentQueue<(FrameType Type, string Content)> _frameQueue
        = new System.Collections.Concurrent.ConcurrentQueue<(FrameType, string)>();

    public BeastApp(string image, List<string> messages, IDisplay display, Log log)
    {
        _image = image;
        _messages = messages;
        _display = display;
        _log = log;
    }

    // Sends /quit to the agent so it saves the session, then waits for it to disconnect.
    // Falls back to hard cancel after a timeout.
    private void RequestGracefulExit()
    {
        async Task GracefulExitAsync()
        {
            try
            {
                if (_wsClient != null)
                    await _wsClient.SendAsync("/quit");
            }
            catch { }

            if (_readTask != null)
                await Task.WhenAny(_readTask, Task.Delay(3000));

            _cts?.Cancel();
        }
        _ = GracefulExitAsync();
    }

    public async Task<int> Run()
    {
        _cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            RequestGracefulExit();
        };

        _model = new ConversationModel();
        _display.Attach(_model);
        _display.SetSendAsync(text => _wsClient!.SendAsync(text));
        _display.SetRequestExit(RequestGracefulExit);
        _display.SetFrameDrain(DrainFrameQueue);

        // Load settings to pick up optional idle sound path.
        SettingsService settings = new SettingsService(Directory.GetCurrentDirectory());
        _idleSoundFile = settings.Settings.IdleSoundFile;

        int exitCode = 0;
        try
        {
            _docker = new DockerContext(_log);
            string containerName = $"beastagent_{Guid.NewGuid():N}";
            await _docker.RemoveContainerByNameAsync(containerName);
            _containerId = await _docker.LaunchContainerAsync(_image, containerName, new List<string>());

            _wsClient = await RetryConnectAsync("ws://localhost:13131/", _log, _cts.Token);

            _readCts = new CancellationTokenSource();
            _readTask = ReadLoop(_wsClient, _readCts.Token);

            await _display.RunAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _log.Error($"[beast] Error: {ex.Message}");
            exitCode = 1;
        }

        return exitCode;
    }

    private async Task ReadLoop(ITransportClient wsClient, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? message = await wsClient.ReceiveAsync(token);
                if (message == null) break;

                (FrameType type, string content) = ParseFrame(message);
                _frameQueue.Enqueue((type, content));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _display.SetStatus($"[read error] {ex.Message}");
        }

        _display.SetStatus("Agent disconnected.");
        _cts?.Cancel();
    }

    // Drains all queued inbound frames. Must be called under the display's lock.
    public void DrainFrameQueue()
    {
        while (_frameQueue.TryDequeue(out (FrameType Type, string Content) frame))
            ProcessFrame(frame.Type, frame.Content);
    }

    private static (FrameType Type, string Content) ParseFrame(string message)
    {
        int pipe = message.IndexOf('|');
        if (pipe < 0 || !byte.TryParse(message.Substring(0, pipe), out byte typeByte))
            return (FrameType.Output, message);

        return ((FrameType)typeByte, message.Substring(pipe + 1));
    }

    private void ProcessFrame(FrameType type, string content)
    {
        switch (type)
        {
            case FrameType.StreamStart:
                FrameType startType = content == StreamTag.Thinking ? FrameType.Thinking : content == StreamTag.Tool ? FrameType.Tool : FrameType.Output;
                _streamContent = "";
                _streamIndex = _nextIndex++;
                _streamTagToSlot[content] = _streamIndex;
                _slotTypes[_streamIndex] = startType;
                _model!.Update(_streamIndex, startType, _streamContent);
                _display.OnStreamStart(_streamIndex, startType);
                break;

            case FrameType.StreamChunk:
                if (_streamIndex < 0) break;
                _streamContent += content;
                _slotTypes.TryGetValue(_streamIndex, out FrameType chunkType);
                _model!.Update(_streamIndex, chunkType, _streamContent);
                _display.OnStreamChunk(content);
                break;

            case FrameType.StreamEnd:
                FrameType endType = content == StreamTag.Thinking ? FrameType.Thinking : content == StreamTag.Tool ? FrameType.Tool : FrameType.Output;
                if (_streamTagToSlot.TryGetValue(content, out int endSlot))
                {
                    _pendingCommit[endType] = endSlot;
                    _streamTagToSlot.Remove(content);
                }
                _streamIndex = -1;
                _streamContent = "";
                _display.OnStreamEnd();
                break;

            case FrameType.Status:
                if (content == "ready")
                {
                    _pingNonce = Guid.NewGuid().ToString("N");
                    string nonce = _pingNonce;
                    async Task SendPingAsync()
                    {
                        try { await _wsClient!.SendAsync($"/ping {nonce}"); }
                        catch (Exception ex) { _display.SetStatus($"[ping error] {ex.Message}"); }
                    }
                    _ = SendPingAsync();
                }
                else if (_pingNonce != null && content == $"pong {_pingNonce}" && !_readyFired)
                {
                    _readyFired = true;
                    async Task SendMessagesAsync()
                    {
                        if (_display.RequestHistory)
                            await _wsClient!.SendAsync("/history");
                        foreach (string msg in _messages)
                            await _wsClient!.SendAsync(msg);
                    }
                    _ = SendMessagesAsync();
                }
                else
                {
                    _display.SetStatus(content);
                }
                break;

            case FrameType.Debug:
                // Debug frames are suppressed on the Beast side; they appear in the agent's own console.
                break;

            case FrameType.Idle:
                _display.SetAgentBusy(false);
                PlayIdleSound();
                break;

            case FrameType.User:
                _model!.Update(_nextIndex++, FrameType.User, content);
                break;

            case FrameType.Stats:
                try
                {
                    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(content);
                    System.Text.Json.JsonElement root = doc.RootElement;
                    string model = root.TryGetProperty("model", out System.Text.Json.JsonElement m) ? m.GetString() ?? "" : "";
                    int prompt = root.TryGetProperty("promptTokens", out System.Text.Json.JsonElement p) ? p.GetInt32() : 0;
                    int completion = root.TryGetProperty("completionTokens", out System.Text.Json.JsonElement c) ? c.GetInt32() : 0;
                    decimal cost = root.TryGetProperty("totalCost", out System.Text.Json.JsonElement tc) ? tc.GetDecimal() : 0m;
                    int maxContext = root.TryGetProperty("maxContext", out System.Text.Json.JsonElement mc) ? mc.GetInt32() : 0;
                    int contextTokens = root.TryGetProperty("contextTokens", out System.Text.Json.JsonElement ct) ? ct.GetInt32() : 0;
                    _display.SetStatsInfo(model, prompt, completion, cost, maxContext, contextTokens);
                }
                catch { }
                break;

            case FrameType.Completions:
                try
                {
                    string[]? completions = JsonSerializer.Deserialize<string[]>(content);
                    _display.SetCompletions(completions ?? Array.Empty<string>());
                }
                catch
                {
                    _display.SetCompletions(Array.Empty<string>());
                }
                break;

            case FrameType.Clear:
                _model!.Clear();
                _nextIndex = 0;
                _streamIndex = -1;
                _streamContent = "";
                _streamTagToSlot.Clear();
                _slotTypes.Clear();
                _pendingCommit.Clear();
                break;

            case FrameType.ToolCall:
                _model!.Update(_nextIndex++, FrameType.ToolCall, content);
                break;

            default:
                // Reuse the stream slot for the committed frame that immediately follows StreamEnd.
                int slotIndex;
                if (_pendingCommit.TryGetValue(type, out int pendingSlot))
                {
                    slotIndex = pendingSlot;
                    _pendingCommit.Remove(type);
                }
                else
                {
                    slotIndex = _nextIndex++;
                }
                _model!.Update(slotIndex, type, content);
                break;
        }
    }

    // Plays the configured idle sound file via NAudio, which supports WAV, MP3, and most other formats.
    // Playback is fire-and-forget on a threadpool thread; the player is disposed when playback stops.
    // Errors are silently swallowed — a missing or unplayable file is not fatal.
    private void PlayIdleSound()
    {
        if (string.IsNullOrEmpty(_idleSoundFile)) return;
        if (!File.Exists(_idleSoundFile)) return;
        try
        {
            AudioFileReader reader = new AudioFileReader(_idleSoundFile);
            WaveOutEvent output = new WaveOutEvent();
            output.Init(reader);
            output.PlaybackStopped += (s, e) =>
            {
                output.Dispose();
                reader.Dispose();
            };
            output.Play();
        }
        catch { }
    }

    // Retries WebSocket connection until success or cancellation, with 200ms delays.
    private static async Task<ITransportClient> RetryConnectAsync(string url, Log log, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TransportClientWebsocket wsClient = new TransportClientWebsocket(url, log);
            try
            {
                await wsClient.ConnectAsync(cancellationToken);
                return wsClient;
            }
            catch (OperationCanceledException)
            {
                wsClient.Dispose();
                throw;
            }
            catch (Exception)
            {
                wsClient.Dispose();
                await Task.Delay(200, cancellationToken);
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }

    public void Dispose()
    {
        _readCts?.Cancel();
        _readTask?.Wait(2000);
        _readCts?.Dispose();
        _wsClient?.Dispose();
        _docker?.Dispose();
        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        _readTask?.Wait(2000);
        _readCts?.Dispose();
        _wsClient?.Dispose();
        if (_containerId != null && _docker != null)
        {
            try { await _docker.StopAndRemoveContainerAsync(_containerId); } catch { }
        }
        _docker?.Dispose();
        _cts?.Dispose();
    }
}
