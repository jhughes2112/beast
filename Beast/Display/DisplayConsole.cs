using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Non-interactive display: prints output and errors to stdout/stderr, streams line by line.
// Blocks in RunAsync until cancellation fires.
public class DisplayConsole : IDisplay
{
    private readonly Log _log;
    private readonly bool _verbose;
    private int _streamIndex = -1;
    private FrameType _streamType = FrameType.Output;
    private FrameType _lastStreamType = FrameType.Output;
    private bool _didStream = false;
    private bool _hadPreviousStream = false;
    private readonly HashSet<int> _streamedSlots = new HashSet<int>();

    public DisplayConsole(Log log, bool verbose)
    {
        _log = log;
        _verbose = verbose;
    }

    public void Attach(ConversationModel model)
    {
        model.MessageUpdated += OnMessageUpdated;
    }

    public void SetStatus(string text)
    {
        _log.Verbose($"[status] {text}");
    }

    public void OnStreamStart(int streamIndex, FrameType type)
    {
        _streamIndex = streamIndex;
        _streamType = type;
        _didStream = false;
        _streamedSlots.Add(streamIndex);

        if (type == FrameType.Thinking && !_verbose) return;

        if (_hadPreviousStream && type != _lastStreamType)
            Console.WriteLine();

        if (type == FrameType.Thinking)
            Console.Write("<think>");
    }

    public void OnStreamChunk(string chunk)
    {
        if (_streamType == FrameType.Thinking && !_verbose) return;
        Console.Write(chunk);
        _didStream = true;
    }

    public void OnStreamEnd()
    {
        if (_streamType != FrameType.Thinking || _verbose)
        {
            if (_streamType == FrameType.Thinking)
                Console.Write("</think>");

            if (_didStream)
                Console.WriteLine();

            _hadPreviousStream = true;
        }

        _lastStreamType = _streamType;
        _streamIndex = -1;
        _didStream = false;
    }

    public void SetSendAsync(Func<string, Task> sendAsync)
    {
        // No interactive input in non-interactive mode.
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private void OnMessageUpdated(DisplayMessage msg)
    {
        if (msg.Index == _streamIndex) return;  // streaming is handled in OnStreamChunk/OnStreamEnd
        if (_streamedSlots.Contains(msg.Index)) return;  // already rendered live during streaming

        if (_verbose)
        {
            // Show everything except protocol-only frame types.
            if (msg.Type == FrameType.StreamStart || msg.Type == FrameType.StreamChunk ||
                msg.Type == FrameType.StreamEnd   || msg.Type == FrameType.Completions)
                return;

            if (msg.Type == FrameType.Thinking && !_verbose) return;
            if (string.IsNullOrEmpty(msg.Content)) return;

            if (msg.Type == FrameType.Error)
                _log.Error($"[error] {msg.Content}");
            else if (msg.Type == FrameType.ToolCall)
                _log.Verbose($"[tool-call] {msg.Content}");
            else if (msg.Type == FrameType.ToolResponse)
                _log.Verbose($"[tool-response] {msg.Content}");
            else if (msg.Type == FrameType.Status)
                _log.Verbose($"[status] {msg.Content}");
            else if (msg.Type == FrameType.Debug)
                _log.Verbose($"[debug] {msg.Content}");
            else
                Console.WriteLine(msg.Content);
        }
        else
        {
            if (msg.Type == FrameType.Output && !string.IsNullOrEmpty(msg.Content))
                Console.WriteLine(msg.Content);
            else if (msg.Type == FrameType.Error)
                _log.Error($"[error] {msg.Content}");
        }
    }
}
