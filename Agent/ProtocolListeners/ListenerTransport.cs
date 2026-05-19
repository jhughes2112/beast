using System.Collections.Generic;

// Adapter that turns an ITransportServer into an IProtocolListener so the transport can sit
// in the bundle alongside the protocol listeners. It never originates events, only renders
// them — every method maps to the corresponding transport frame. Streaming pass-through is
// straightforward; assistant turns are emitted as Thinking + Output committed frames.
public class ListenerTransport : IProtocolListener
{
    private readonly ITransportServer _transport;

    public ListenerTransport(ITransportServer transport)
    {
        _transport = transport;
    }

    public void OnSystemMessage(IProtocolListener sender, string text)
    {
        _transport.System(text);
    }

    public void OnUserMessage(IProtocolListener sender, string text)
    {
        // User messages originate at the transport; nothing to render back.
    }

    public void OnAssistantTurn(IProtocolListener sender, string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        if (!string.IsNullOrEmpty(thinking)) _transport.Thinking(thinking);
        if (!string.IsNullOrEmpty(text)) _transport.Output(text);
        foreach (SemanticToolCall tc in toolCalls)
        {
            _transport.ToolCall($"{tc.Name}({tc.ArgumentsJson})");
        }
    }

    public void OnToolResult(IProtocolListener sender, string toolCallId, string content)
    {
        _transport.ToolResponse(content);
    }

    public void OnStreamStart(IProtocolListener sender, string tag)
    {
        _transport.StreamStart(tag);
    }

    public void OnStreamChunk(IProtocolListener sender, string tag, string chunk)
    {
        _transport.StreamChunk(chunk);
    }

    public void OnStreamEnd(IProtocolListener sender, string tag)
    {
        _transport.StreamEnd(tag);
    }

    public void OnClear()
    {
        _transport.Clear();
    }

    public string? GetLastAssistantText() { return null; }
    public void RewriteLastAssistant(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls) { }
    public string? PopLastUserMessage() { return null; }
}
