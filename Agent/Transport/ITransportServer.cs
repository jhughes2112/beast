using System;
using System.Threading;
using System.Threading.Tasks;


// Typed message categories — used only for outbound (Agent → Beast).
public enum FrameType : byte
{
    Output = 0,
    Error = 1,
    Status = 2,
    Tool = 3,
    ToolCall = 11,      // agent is about to call a tool: content is "toolName(args)"
    ToolResponse = 12,  // tool returned a result: content is the response text
    Thinking = 4,
    Completions = 5,  // JSON array of completion strings in response to /complete
    System = 6,       // system prompt message
    StreamStart = 7,  // begins a streaming response block; content is a type tag (see StreamTag)
    StreamChunk = 8,  // one text delta belonging to the current open stream
    StreamEnd = 9,    // closes the current stream block; content is the same type tag as StreamStart
    Debug = 10,       // diagnostic output; suppressed unless Beast is running in verbose mode
    Clear = 13,       // clears the client's mirrored conversation memory/display
    User = 14,        // user message; used when replaying history to the client
    Stats = 15        // JSON stats payload: model, promptTokens, completionTokens, totalCost
}

// Single-character tags that identify the type of a streaming block.
// Sent in StreamStart and StreamEnd frames so the client knows how to render the block.
public static class StreamTag
{
    public const string Assistant = "A";
    public const string Tool = "T";
    public const string Thinking = "K";
    public const string System = "S";
}

// Scoped interface for streaming only — passed to protocol implementations so they cannot
// touch the broader transport surface (output, errors, reads, etc.).
// Use StreamTag constants for the tag argument.
public interface IStreamingMessage
{
    void StreamStart(string tag);
    void StreamChunk(string chunk);
    void StreamEnd(string tag);
}

// Abstraction for framed stdio communication.
// Outbound: typed frames so the client can render with appropriate styling.
// Inbound: plain text strings (framing is only an envelope; content is what matters).
// Single-threaded: the caller reads in its own loop — no background threads or events.
public interface ITransportServer : IStreamingMessage
{
    // Outbound: send a typed frame to the client.
    void Send(FrameType type, string text);

    void Output(string text) => Send(FrameType.Output, text);
    void Error(string text) => Send(FrameType.Error, text);
    void Status(string text) => Send(FrameType.Status, text);
    void Tool(string text) => Send(FrameType.Tool, text);
    void ToolCall(string text) => Send(FrameType.ToolCall, text);
    void ToolResponse(string text) => Send(FrameType.ToolResponse, text);
    void Thinking(string text) => Send(FrameType.Thinking, text);
    void Completions(string json) => Send(FrameType.Completions, json);
    void System(string text) => Send(FrameType.System, text);
    void Debug(string text) => Send(FrameType.Debug, text);
    void User(string text) => Send(FrameType.User, text);
    void Stats(string json) => Send(FrameType.Stats, json);
    void Clear() => Send(FrameType.Clear, string.Empty);

    // Streaming: bracket a sequence of incremental chunks with start/end frames.
    // The client accumulates chunks for live display. After StreamEnd, the caller
    // must immediately follow with the matching semantic call (Output, Thinking, etc.)
    // which is the authoritative committed version — the client discards the stream and
    // replaces it. Use StreamTag constants for the tag argument.
    void IStreamingMessage.StreamStart(string tag) => Send(FrameType.StreamStart, tag);
    void IStreamingMessage.StreamChunk(string chunk) => Send(FrameType.StreamChunk, chunk);
    void IStreamingMessage.StreamEnd(string tag) => Send(FrameType.StreamEnd, tag);

    // Inbound: read one frame. Returns the content string, or null on EOF.
    // Call this from the orchestrator's own loop — no threading.
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    // Non-blocking read: returns content if a frame arrived within timeoutMs, empty string on timeout, null on EOF.
    async Task<string?> TryReadAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            return string.Empty;
        }
    }
}
