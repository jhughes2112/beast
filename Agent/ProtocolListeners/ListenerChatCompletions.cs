using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

// ChatCompletions canonical-state listener. Emits OpenAI-style {role, content, tool_calls,
// tool_call_id} items and is the single source of truth for the conversation. Thinking is
// never persisted. Streaming events are translated into in-place mutation of an open assistant
// message so the saved state matches what a real ChatCompletions stream would have produced.
public class ListenerChatCompletions : IProtocolListener
{
    private readonly JsonArray _state;

    // In-flight streaming assistant message and its open builders (null when no stream open).
    private JsonObject? _streamingMessage;
    private StringBuilder? _streamingContent;

    public ListenerChatCompletions(JsonArray state)
    {
        _state = state;
    }

    public JsonArray State => _state;

    public void OnSystemMessage(IProtocolListener sender, string text)
    {
        // If a system message already exists at the head of the state, update it in-place
        // so that resuming a session or changing roles never injects a duplicate mid-history.
        if (_state.Count > 0 && _state[0]?["role"]?.GetValue<string>() == "system")
        {
            _state[0]!["content"] = text;
            return;
        }

        JsonObject msg = new JsonObject();
        msg["role"] = "system";
        msg["content"] = text;
        _state.Insert(0, msg);
    }

    public void OnUserMessage(IProtocolListener sender, string text)
    {
        // If the last item is already a user message, merge text in-place rather than appending
        // a new turn. This collapses multiple rapid user inputs into a single turn.
        int count = _state.Count;
        if (count > 0)
        {
            JsonNode? last = _state[count - 1];
            if (last != null && last["role"]?.GetValue<string>() == "user")
            {
                string? existing = last["content"]?.GetValue<string>();
                last["content"] = string.IsNullOrEmpty(existing) ? text : existing + "\n" + text;
                return;
            }
        }

        JsonObject msg = new JsonObject();
        msg["role"] = "user";
        msg["content"] = text;
        _state.Add(msg);
    }

    public void OnAssistantTurn(IProtocolListener sender, string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        // Canonical assistant turns are normalized text + tool_calls. Thinking is broadcast to
        // transport listeners but never persisted in canonical state.
        JsonObject msg = new JsonObject();
        msg["role"] = "assistant";

        string body = ComposeBody(text);
        msg["content"] = body;

        if (toolCalls.Count > 0)
        {
            JsonArray tcArr = new JsonArray();
            foreach (SemanticToolCall tc in toolCalls)
            {
                JsonObject tcObj = new JsonObject();
                tcObj["id"] = tc.Id;
                tcObj["type"] = "function";
                JsonObject fn = new JsonObject();
                fn["name"] = tc.Name;
                fn["arguments"] = tc.ArgumentsJson;
                tcObj["function"] = fn;
                tcArr.Add(tcObj);
            }
            msg["tool_calls"] = tcArr;
        }

        _state.Add(msg);
    }

    public void OnToolResult(IProtocolListener sender, string toolCallId, string content)
    {
        JsonObject msg = new JsonObject();
        msg["role"] = "tool";
        msg["content"] = content;
        msg["tool_call_id"] = toolCallId;
        _state.Add(msg);
    }

    public void OnStreamStart(IProtocolListener sender, string tag)
    {
        if (_streamingMessage == null)
        {
            _streamingMessage = new JsonObject();
            _streamingMessage["role"] = "assistant";
            _streamingMessage["content"] = string.Empty;
            _streamingContent = new StringBuilder();
            _state.Add(_streamingMessage);
        }
    }

    public void OnStreamChunk(IProtocolListener sender, string tag, string chunk)
    {
        if (_streamingMessage == null) return;
        if (tag != StreamTag.Thinking)
        {
            _streamingContent!.Append(chunk);
            _streamingMessage["content"] = _streamingContent.ToString();
        }
    }

    public void OnStreamEnd(IProtocolListener sender, string tag)
    {
        // Stream end alone does not finalise — the producing protocol follows with a
        // completed OnAssistantTurn which replaces the buffered message. We drop the
        // in-flight scaffold here so the authoritative version wins.
        if (_streamingMessage != null)
        {
            int idx = _state.IndexOf(_streamingMessage);
            if (idx >= 0) _state.RemoveAt(idx);
            _streamingMessage = null;
            _streamingContent = null;
        }
    }

    public void OnClear()
    {
        _state.Clear();
        _streamingMessage = null;
        _streamingContent = null;
    }

    public void Rehydrate(JsonArray canonical) { }

    private static string ComposeBody(string text)
    {
        return text ?? string.Empty;
    }
}
