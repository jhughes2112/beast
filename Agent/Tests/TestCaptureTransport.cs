using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// In-memory IFramedTransport for test scenarios. Captures all sent output.
public class TestCaptureTransport : ITransportServer
{
	private readonly List<(FrameType Type, string Text)> _sent = new List<(FrameType Type, string Text)>();
	private readonly Queue<string> _pendingReads = new Queue<string>();

	public IReadOnlyList<(FrameType Type, string Text)> Sent => _sent;

	public void EnqueueRead(string text) => _pendingReads.Enqueue(text);

	private void Send(FrameType type, string sessionId, string text) => _sent.Add((type, text));

	public void Output(string sessionId, string text) => Send(FrameType.Output, sessionId, text);
	public void Error(string sessionId, string text) => Send(FrameType.Error, sessionId, text);
	public void Status(string sessionId, string text) => Send(FrameType.Status, sessionId, text);
	public void Thinking(string sessionId, string text) => Send(FrameType.Thinking, sessionId, text);
	public void System(string sessionId, string text) => Send(FrameType.System, sessionId, text);
	public void User(string sessionId, string text) => Send(FrameType.User, sessionId, text);
	public void Debug(string sessionId, string text) => Send(FrameType.Debug, sessionId, text);
	public void Stats(string sessionId, string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens, int cachedTokens)
		=> Send(FrameType.Stats, sessionId, $"model={model},role={role},{promptTokens},{completionTokens},{totalCost},{maxContext},{contextTokens},{cachedTokens}");
	public void Completions(string sessionId, string json) => Send(FrameType.Completions, sessionId, json);
	public void Idle(string sessionId, bool subagent) => Send(FrameType.Idle, sessionId, subagent ? "subagent" : string.Empty);
	public void Busy(string sessionId) => Send(FrameType.Busy, sessionId, string.Empty);
	public void ToolCallWithId(string sessionId, string callId, string text) => Send(FrameType.ToolCall, sessionId, text);
	public void ToolResponseWithId(string sessionId, ToolResult result) => Send(FrameType.ToolResponse, sessionId, result.StdOut);
	public void SessionAnnounce(string sessionId, string json) => Send(FrameType.SessionAnnounce, sessionId, json);
	public void SessionReset(string sessionId) => Send(FrameType.SessionReset, sessionId, string.Empty);
	public void SessionStatus(string sessionId, string status) => Send(FrameType.SessionStatus, sessionId, status);
	public void StreamStart(string sessionId, string tag) => Send(FrameType.StreamStart, sessionId, tag);
	public void StreamChunk(string sessionId, string chunk) => Send(FrameType.StreamChunk, sessionId, chunk);
	public void StreamEnd(string sessionId, string tag) => Send(FrameType.StreamEnd, sessionId, tag);

	public Task<string?> ReadAsync(CancellationToken cancellationToken)
	{
		if (_pendingReads.Count > 0)
			return Task.FromResult<string?>(_pendingReads.Dequeue());
		return Task.FromResult<string?>(null);
	}
}