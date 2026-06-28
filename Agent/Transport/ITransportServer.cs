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
	ToolCall = 11,         // agent is about to call a tool: content is "toolName(args)"
	ToolResponse = 12,     // tool returned a result: content is the response text
	Thinking = 4,
	Completions = 5,       // JSON array of completion strings in response to /complete
	System = 6,            // system prompt message
	StreamStart = 7,       // begins a streaming response block; content is a type tag (see StreamTag)
	StreamChunk = 8,       // one text delta belonging to the current open stream
	StreamEnd = 9,         // closes the current stream block; content is the same type tag as StreamStart
	Debug = 10,            // diagnostic output; suppressed unless Beast is running in verbose mode
	User = 14,             // user message; used when replaying history to the client
	Stats = 15,            // JSON stats payload: model, promptTokens, completionTokens, totalCost
	Idle = 16,             // agent is waiting for user input (not processing anything)
	Busy = 17,             // agent is actively processing
	SessionAnnounce = 18,  // agent announces a session; content is JSON {id, name}
	SessionReset = 19,     // agent reset its session set (e.g. active root deleted from F10); client forgets all sessions and adopts the named one
	SessionStatus = 20     // agent reports a session's termination status; content is a SessionStatus enum name
}

// Session termination status reported via SessionStatus frames.
public enum SessionStatus
{
	Ongoing,      // Session is currently busy or has not yet finished.
	Success,      // Session finished with a successful result (ok=true).
	Failure,      // Session finished with a failure (ok=false).
	Incomplete    // Session ended without any tool call being made.
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

// Framed communication between Agent and Beast.
// Every outbound method takes a sessionId (empty string for orchestrator-level frames).
// Frame routing and encoding are each transport's private concern; Send is not on this interface.
public interface ITransportServer
{
	void Output(string sessionId, string text);
	void Error(string sessionId, string text);
	void Status(string sessionId, string text);
	void Thinking(string sessionId, string text);
	void System(string sessionId, string text);
	void User(string sessionId, string text);
	void Debug(string sessionId, string text);
	void Stats(string sessionId, string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens, int cachedTokens);
	void Completions(string sessionId, string json);
	// subagent distinguishes sub-session completion from main-agent completion so the client
	// can react differently (e.g. play a different notification sound).
	void Idle(string sessionId, bool subagent);
	void Busy(string sessionId);
	// Frame content is "callId\x01text" so Beast can pair by identity.
	void ToolCallWithId(string sessionId, string callId, string text);
	// Frame content is "callId\x01exitCode\x01stdout\x01stderr" so Beast can display results richly.
	void ToolResponseWithId(string sessionId, ToolResult result);
	// Announces the session's ID and display name so Beast can show a human-readable label.
	void SessionAnnounce(string sessionId, string json);
	// Tells the client to forget every session it knows and adopt the named one as the sole active session.
	// Sent when the agent stands up a fresh root (e.g. the active root was deleted from F10) so the F10 list resets.
	void SessionReset(string sessionId);
	// Reports a session's termination status (Success / Failure / Incomplete) so the F10 overlay can color it.
	void SessionStatus(string sessionId, string status);
	// Streaming: bracket a sequence of incremental chunks with start/end frames.
	// Use StreamTag constants for the tag argument.
	void StreamStart(string sessionId, string tag);
	void StreamChunk(string sessionId, string chunk);
	void StreamEnd(string sessionId, string tag);

	// Inbound: read one frame. Returns the content string, or null on EOF.
	Task<string?> ReadAsync(CancellationToken cancellationToken);

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
			if (cancellationToken.IsCancellationRequested)
				return null;
			return string.Empty;
		}
	}
}