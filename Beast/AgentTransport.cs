using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Reads frames from the Agent container stdio stream and drives ConversationModel.Update() calls.
// The update model is [index, type, content]: streaming appends a growing content string in place,
// and a commit from StreamEnd promotes it to the final type.
public class AgentTransport : IDisposable
{
    private readonly ConversationModel _model;
    private readonly Action<string> _onStatus;
    private readonly Action _onDisconnected;
    private readonly Action _onReady;
    private readonly FrameParser _parser = new FrameParser();

    private int _nextIndex = 0;
    private int _streamIndex = -1;       // index of the currently open streaming slot
    private FrameType _streamType;       // the semantic type being streamed (Output, Thinking, Tool)
    private string _streamContent = "";  // accumulated content for the live stream slot

    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private DockerContext? _docker;
    private string? _pingNonce;
    private bool _readyFired;

    public AgentTransport(ConversationModel model, Action<string> onStatus, Action onDisconnected, Action onReady)
    {
        _model = model;
        _onStatus = onStatus;
        _onDisconnected = onDisconnected;
        _onReady = onReady;
    }

    public void Start(DockerContext docker)
    {
        _docker = docker;
        _readyFired = false;
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _readTask = Task.Run(() => ReadLoop(docker, token), token);
    }

    private async Task ReadLoop(DockerContext docker, CancellationToken token)
    {
        byte[] buffer = new byte[8192];

        while (!token.IsCancellationRequested)
        {
            int count = 0;
            bool eof = false;
            try
            {
                (count, eof) = await docker.ReadAsync(buffer, 0, buffer.Length, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _onStatus($"[transport error] {ex.Message}");
                break;
            }

            if (eof) break;

            Console.Error.WriteLine($"[transport] Read {count} bytes");
            _parser.Feed(buffer, count);

            List<(FrameType Type, string Content)> frames = _parser.TakeFrames();
            foreach ((FrameType type, string content) in frames)
            {
                ProcessFrame(type, content);
            }
        }

        _onDisconnected();
    }

    private void ProcessFrame(FrameType type, string content)
    {
        switch (type)
        {
            case FrameType.StreamStart:
            {
                // content is a StreamTag constant; map it to the semantic FrameType.
                _streamType = TagToFrameType(content);
                _streamContent = "";
                _streamIndex = _nextIndex++;
                _model.Update(_streamIndex, _streamType, _streamContent);
                break;
            }

            case FrameType.StreamChunk:
            {
                if (_streamIndex < 0) break;
                _streamContent += content;
                _model.Update(_streamIndex, _streamType, _streamContent);
                break;
            }

            case FrameType.StreamEnd:
            {
                // The committed message follows immediately as a typed frame; reset stream slot.
                _streamIndex = -1;
                _streamContent = "";
                break;
            }

            case FrameType.Status:
            {
                Console.Error.WriteLine($"[transport] Status frame: '{content}' (nonce={_pingNonce ?? "null"}, readyFired={_readyFired})");
                if (content == "ready")
                {
                    // Agent signals it is up; send a ping with a unique nonce to verify the round-trip.
                    _pingNonce = Guid.NewGuid().ToString("N");
                    string nonce = _pingNonce;
                    Console.Error.WriteLine($"[transport] Sending /ping {nonce}");
                    Task.Run(async () =>
                    {
                        try { await _docker!.SendAsync($"/ping {nonce}"); } catch (Exception ex) { Console.Error.WriteLine($"[transport] ping send failed: {ex.Message}"); }
                    });
                }
                else if (_pingNonce != null && content == $"pong {_pingNonce}" && !_readyFired)
                {
                    // Nonce matched — confirmed round-trip to this specific Agent instance.
                    Console.Error.WriteLine($"[transport] Pong matched, firing onReady");
                    _readyFired = true;
                    _onReady();
                }
                else
                {
                    _onStatus(content);
                }
                break;
            }

            default:
            {
                // Output, Error, Thinking, Tool, Completions — each is a new committed message.
                int index = _nextIndex++;
                _model.Update(index, type, content);
                break;
            }
        }
    }

    private static FrameType TagToFrameType(string tag)
    {
        if (tag == StreamTag.Thinking) return FrameType.Thinking;
        if (tag == StreamTag.Tool) return FrameType.Tool;
        return FrameType.Output;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _readTask?.Wait(2000);
        _cts?.Dispose();
    }
}
