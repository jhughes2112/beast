using System.Collections.Generic;
using System.Text.Json.Nodes;

// Responses native-state listener. Emits OpenAI Responses API flat input items.
// Streaming is consumed by the transport listener; native state is rebuilt from the
// final response.completed event by the producing protocol via AppendNativeItem.
public class ListenerResponses : IProtocolListener
{
    private readonly JsonArray _state;

    public ListenerResponses(JsonArray state)
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
            JsonArray? existingContent = _state[0]!["content"]?.AsArray();
            if (existingContent != null && existingContent.Count > 0)
            {
                existingContent[0]!["text"] = text;
            }
            return;
        }

        JsonObject item = new JsonObject();
        item["type"] = "message";
        item["role"] = "system";
        JsonArray content = new JsonArray();
        JsonObject block = new JsonObject();
        block["type"] = "input_text";
        block["text"] = text;
        content.Add(block);
        item["content"] = content;
        _state.Insert(0, item);
    }

    public void OnUserMessage(IProtocolListener sender, string text)
    {
        JsonObject item = new JsonObject();
        item["type"] = "message";
        item["role"] = "user";
        JsonArray content = new JsonArray();
        JsonObject block = new JsonObject();
        block["type"] = "input_text";
        block["text"] = text;
        content.Add(block);
        item["content"] = content;
        _state.Add(item);
    }

    public void OnAssistantTurn(IProtocolListener sender, string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        if (!string.IsNullOrEmpty(text))
        {
            JsonObject item = new JsonObject();
            item["type"] = "message";
            item["role"] = "assistant";
            JsonArray content = new JsonArray();
            JsonObject block = new JsonObject();
            block["type"] = "output_text";
            block["text"] = text;
            content.Add(block);
            item["content"] = content;
            _state.Add(item);
        }

        foreach (SemanticToolCall tc in toolCalls)
        {
            JsonObject item = new JsonObject();
            item["type"] = "function_call";
            item["id"] = tc.Id;
            item["call_id"] = tc.Id;
            item["name"] = tc.Name;
            item["arguments"] = tc.ArgumentsJson;
            _state.Add(item);
        }
    }

    public void OnToolResult(IProtocolListener sender, string toolCallId, string content)
    {
        JsonObject item = new JsonObject();
        item["type"] = "function_call_output";
        item["call_id"] = toolCallId;
        item["output"] = content;
        _state.Add(item);
    }

    public void OnStreamStart(IProtocolListener sender, string tag) { }
    public void OnStreamChunk(IProtocolListener sender, string tag, string chunk) { }
    public void OnStreamEnd(IProtocolListener sender, string tag) { }

    public void OnClear()
    {
        _state.Clear();
    }

    public string? GetLastAssistantText()
    {
        string? text = null;
        for (int i = _state.Count - 1; i >= 0; i--)
        {
            JsonNode? n = _state[i];
            if (n == null) continue;
            if (n["type"]?.GetValue<string>() == "message" && n["role"]?.GetValue<string>() == "assistant")
            {
                JsonNode? content = n["content"];
                if (content is JsonArray ca)
                {
                    foreach (JsonNode? block in ca)
                    {
                        if (block != null && block["type"]?.GetValue<string>() == "output_text")
                        {
                            text = block["text"]?.GetValue<string>();
                            break;
                        }
                    }
                }
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
            if (n == null) continue;
            if (n["type"]?.GetValue<string>() == "message" && n["role"]?.GetValue<string>() == "assistant")
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
            if (n != null && n["type"]?.GetValue<string>() == "message" && n["role"]?.GetValue<string>() == "user")
            {
                // Extract text from the first input_text block.
                string? text = null;
                JsonNode? content = n["content"];
                if (content is JsonArray ca)
                {
                    foreach (JsonNode? block in ca)
                    {
                        if (block != null && block["type"]?.GetValue<string>() == "input_text")
                        {
                            text = block["text"]?.GetValue<string>();
                            break;
                        }
                    }
                }
                _state.RemoveAt(i);
                return text;
            }
        }
        return null;
    }

    // Producer-only: append a verbatim native item directly.
    public void AppendNativeItem(JsonObject nativeItem)
    {
        _state.Add(nativeItem);
    }
}
