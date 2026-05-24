using System.Collections.Generic;

// Semantic conversation surface. Every event that happens in a conversation flows through
// this interface — user input, system prompt changes, assistant deltas, completed assistant
// turns, tool calls, and tool results. Concrete listeners include the per-protocol native
// state writers (ChatCompletions/Responses/Anthropic) and the transport adapter that renders
// to the client. Listeners are composed via ListenerBundle which fans every call out to all
// peers EXCEPT the sender, so a protocol that received a streaming chunk from its provider
// only echoes it to the others (not back to itself).
public interface IProtocolListener
{
    // Discrete completed events.
    void OnSystemMessage(IProtocolListener sender, string text);
    void OnUserMessage(IProtocolListener sender, string text);
    void OnAssistantTurn(IProtocolListener sender, string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls);
    void OnToolResult(IProtocolListener sender, string toolCallId, string content);

    // Live streaming. tag is one of StreamTag.Assistant/Thinking; protocols translate to native,
    // the transport adapter forwards as StreamStart/Chunk/End frames.
    void OnStreamStart(IProtocolListener sender, string tag);
    void OnStreamChunk(IProtocolListener sender, string tag, string chunk);
    void OnStreamEnd(IProtocolListener sender, string tag);

    // Lifecycle — clears all conversation state held by this listener.
    void OnClear();

    // Returns the most recent assistant text held by this listener, or null.
    string? GetLastAssistantText();

    }
