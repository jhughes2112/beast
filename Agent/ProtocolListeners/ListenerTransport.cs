// Adapter that renders semantic conversation events to the connected transport client.
// It never originates events — every method maps to the corresponding transport frame.
public class ListenerTransport
{
	private readonly ITransportServer _transport;
	private readonly string _sessionId;

	public ListenerTransport(ITransportServer transport, string sessionId)
	{
		_transport = transport;
		_sessionId = sessionId;
	}

	public void OnSystemMessage(string text) => _transport.System(_sessionId, text);
	public void OnUserMessage(string text) => _transport.User(_sessionId, text);

	public void OnAssistantTurn(string text, string thinking, System.Collections.Generic.IReadOnlyList<SemanticToolCall> toolCalls)
	{
		if (!string.IsNullOrWhiteSpace(thinking))
			_transport.Thinking(_sessionId, thinking);
		// Whitespace-only assistant text (common when the turn is just thinking plus a tool call) must not
		// produce a visible output block — treat it as no text at all, not just the strictly-empty case.
		if (!string.IsNullOrWhiteSpace(text))
			_transport.Output(_sessionId, text);
		foreach (SemanticToolCall tc in toolCalls)
			_transport.ToolCallWithId(_sessionId, tc.Id, tc.Name + "(" + tc.ArgumentsJson + ")");
	}

	public void OnToolResult(ToolResult result) => _transport.ToolResponseWithId(_sessionId, result);

	public void Status(string text) => _transport.Status(_sessionId, text);

	public void OnStreamStart(string tag) => _transport.StreamStart(_sessionId, tag);

	public void OnStreamChunk(string tag, string chunk) => _transport.StreamChunk(_sessionId, chunk);

	public void OnStreamEnd(string tag) => _transport.StreamEnd(_sessionId, tag);
}