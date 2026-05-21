using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

// ChatCompletions native-state listener. Emits OpenAI-style {role, content, tool_calls,
// tool_call_id} items. Thinking from foreign protocols is inlined as <thinking>...</thinking>
// at the head of content. Streaming events are translated into in-place mutation of an
// open assistant message so the saved state matches what a real ChatCompletions stream would
// have produced if it had been the source.
public class ListenerChatCompletions : IProtocolListener
{
    private readonly JsonArray _state;

    // In-flight streaming assistant message and its open builders (null when no stream open).
    private JsonObject? _streamingMessage;
    private StringBuilder? _streamingContent;
    private StringBuilder? _streamingThinking;

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
        // If the producer is us, the protocol writes its own native turn directly via
        // AppendNativeAssistant — this method only handles fan-out from peer protocols.
        JsonObject msg = new JsonObject();
        msg["role"] = "assistant";

        string body = ComposeBody(text, thinking);
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
            _streamingThinking = new StringBuilder();
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
        // completed OnAssistantTurn (or its native append) which replaces the buffered
        // message. We drop the in-flight scaffold here so the authoritative version wins.
        if (_streamingMessage != null)
        {
            int idx = _state.IndexOf(_streamingMessage);
            if (idx >= 0) _state.RemoveAt(idx);
            _streamingMessage = null;
            _streamingContent = null;
            _streamingThinking = null;
        }
    }

    // Producer-only: append a verbatim assistant message (with any native fields preserved).
    public void AppendNativeAssistant(JsonObject nativeMessage)
    {
        _state.Add(nativeMessage);
    }

    public void OnClear()
    {
        _state.Clear();
        _streamingMessage = null;
        _streamingContent = null;
        _streamingThinking = null;
    }

    public string? GetLastAssistantText()
    {
        string? text = null;
        for (int i = _state.Count - 1; i >= 0; i--)
        {
            JsonNode? n = _state[i];
            if (n != null && n["role"]?.GetValue<string>() == "assistant")
            {
                text = n["content"]?.GetValue<string>();
                break;
            }
        }
        return text;
    }

    public void RewriteLastAssistant(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        for (int i = _state.Count - 1; i >= 0; i--)
        {
            JsonNode? n = _state[i];
            if (n != null && n["role"]?.GetValue<string>() == "assistant")
            {
                _state.RemoveAt(i);
                break;
            }
        }
        OnAssistantTurn(null!, text, thinking, toolCalls);
    }

    public string? PopLastUserMessage()
    {
        for (int i = _state.Count - 1; i >= 0; i--)
        {
            JsonNode? n = _state[i];
            if (n != null && n["role"]?.GetValue<string>() == "user")
            {
                string? text = n["content"]?.GetValue<string>();
                _state.RemoveAt(i);
                return text;
            }
        }
        return null;
    }

    private static string ComposeBody(string text, string thinking)
    {
        return text ?? string.Empty;
    }
}
