using System.Collections.Generic;
using System.Text.Json.Nodes;

// Semantic conversation surface. Every event that happens in a conversation flows through
// this interface — user input, system prompt changes, assistant deltas, completed assistant
// turns, tool calls, and tool results. Concrete listeners include the canonical ChatCompletions
// store, the executing protocol-listeners, and the transport adapter that renders to the client.
// Listeners are composed via ListenerBundle which fans every call out to all peers EXCEPT the
// sender, so a protocol that received a streaming chunk from its provider only echoes it to the
// others (not back to itself).
public interface IProtocolListener
{
    // Discrete completed events.
    void OnSystemMessage(IProtocolListener sender, string text);
    void OnUserMessage(IProtocolListener sender, string text);
    void OnAssistantTurn(IProtocolListener sender, string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls);
    void OnToolResult(IProtocolListener sender, string toolCallId, ToolResult result);

    // Live streaming. tag is one of StreamTag.Assistant/Thinking; protocols translate to native,
    // the transport adapter forwards as StreamStart/Chunk/End frames.
    void OnStreamStart(IProtocolListener sender, string tag);
    void OnStreamChunk(IProtocolListener sender, string tag, string chunk);
    void OnStreamEnd(IProtocolListener sender, string tag);

    // Lifecycle — clears all conversation state held by this listener.
    void OnClear();

    // Seeds protocol-native state from the canonical ChatCompletions array. Called by
    // ListenerBundle immediately after creating or switching to a new protocol instance.
    void Rehydrate(JsonArray canonical);
}
