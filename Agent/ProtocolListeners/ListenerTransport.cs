using System.Collections.Generic;

// Adapter that renders semantic conversation events to the connected transport client.
// It never originates events — every method maps to the corresponding transport frame.
public class ListenerTransport
{
    private readonly ITransportServer _transport;

    public ListenerTransport(ITransportServer transport)
    {
        _transport = transport;
    }

    public void OnSystemMessage(string text)
    {
        _transport.System(text);
    }

    public void OnUserMessage(string text)
    {
        _transport.User(text);
    }

    public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        if (!string.IsNullOrEmpty(thinking)) _transport.Thinking(thinking);
        if (!string.IsNullOrEmpty(text)) _transport.Output(text);
        foreach (SemanticToolCall tc in toolCalls)
        {
            _transport.ToolCallWithId(tc.Id, $"{tc.Name}({tc.ArgumentsJson})");
        }
    }

    public void OnToolResult(string toolCallId, ToolResult result)
    {
        _transport.ToolResponseWithId(toolCallId, result);
    }

    public void OnStreamStart(string tag)
    {
        _transport.StreamStart(tag);
    }

    public void OnStreamChunk(string tag, string chunk)
    {
        _transport.StreamChunk(chunk);
    }

    public void OnStreamEnd(string tag)
    {
        _transport.StreamEnd(tag);
    }

    public void OnClear()
    {
        _transport.Clear();
    }
}
