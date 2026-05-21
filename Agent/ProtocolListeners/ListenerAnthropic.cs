using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

// Anthropic native-state listener. Stores all Anthropic-specific state inside a single
// JsonObject with two keys:
//   "system"   — string, the hoisted system prompt (no in-stream system concept in Anthropic)
//   "messages" — JsonArray of native Anthropic message objects
//
// Emits Anthropic Messages API items with strict user/assistant alternation. Tool results
// are packed as tool_result blocks inside a user message. Consecutive same-role messages
// are merged. Thinking from foreign protocols is inlined as plain <thinking> text; native
// thinking blocks with signatures are only preserved when the producer is the Anthropic
// protocol itself via AppendNativeAssistant.
public class ListenerAnthropic : IProtocolListener
{
    private readonly JsonObject _state;

    public ListenerAnthropic(JsonObject state)
    {
        _state = state;
    }

    // The hoisted system prompt. ProtocolAnthropic reads this when building the request body.
    public string? System => _state["system"]?.GetValue<string>();

    // The messages array, lazily initialised inside the state object.
    public JsonArray Messages
    {
        get
        {
            if (_state["messages"] is not JsonArray arr)
            {
                arr = new JsonArray();
                _state["messages"] = arr;
            }
            return arr;
        }
    }

    public void OnSystemMessage(IProtocolListener sender, string text)
    {
        // Anthropic hoists the system prompt to a top-level field rather than an in-stream
        // message; overwrite it here so the next request body picks up the latest value.
        _state["system"] = text;
    }

    public void OnUserMessage(IProtocolListener sender, string text)
    {
        AppendUserBlock(BuildTextBlock(text));
    }

    public void OnToolResult(IProtocolListener sender, string toolCallId, string content)
    {
        JsonObject block = new JsonObject();
        block["type"] = "tool_result";
        block["tool_use_id"] = toolCallId;
        block["content"] = content;
        AppendUserBlock(block);
    }

    public void OnAssistantTurn(IProtocolListener sender, string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        JsonArray content = new JsonArray();

        if (!string.IsNullOrEmpty(text))
        {
            JsonObject textBlock = new JsonObject();
            textBlock["type"] = "text";
            textBlock["text"] = text;
            content.Add(textBlock);
        }

        foreach (SemanticToolCall tc in toolCalls)
        {
            JsonObject toolBlock = new JsonObject();
            toolBlock["type"] = "tool_use";
            toolBlock["id"] = tc.Id;
            toolBlock["name"] = tc.Name;
            JsonNode? parsed = null;
            if (!string.IsNullOrEmpty(tc.ArgumentsJson))
            {
                try { parsed = JsonNode.Parse(tc.ArgumentsJson); } catch (JsonException) { parsed = null; }
            }
            toolBlock["input"] = parsed ?? new JsonObject();
            content.Add(toolBlock);
        }

        if (content.Count == 0) return;
        AppendOrMergeAssistantContent(content);
    }

    // Streaming is consumed by the transport listener; Anthropic native state is rebuilt from
    // the producing protocol's accumulated blocks via AppendNativeAssistant.
    public void OnStreamStart(IProtocolListener sender, string tag) { }
    public void OnStreamChunk(IProtocolListener sender, string tag, string chunk) { }
    public void OnStreamEnd(IProtocolListener sender, string tag) { }

    public void OnClear()
    {
        _state.Remove("system");
        _state.Remove("messages");
    }

    public string? GetLastAssistantText()
    {
        string? text = null;
        JsonArray messages = Messages;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            JsonNode? n = messages[i];
            if (n == null || n["role"]?.GetValue<string>() != "assistant") continue;

            JsonNode? content = n["content"];
            if (content is JsonValue jv)
            {
                jv.TryGetValue<string>(out text);
            }
            else if (content is JsonArray ca)
            {
                foreach (JsonNode? block in ca)
                {
                    if (block != null && block["type"]?.GetValue<string>() == "text")
                    {
                        text = block["text"]?.GetValue<string>();
                        break;
                    }
                }
            }
            break;
        }
        return text;
    }

    public void RewriteLastAssistant(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        JsonArray messages = Messages;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            JsonNode? n = messages[i];
            if (n != null && n["role"]?.GetValue<string>() == "assistant")
            {
                messages.RemoveAt(i);
                break;
            }
        }
        OnAssistantTurn(null!, text, thinking, toolCalls);
    }

    public string? PopLastUserMessage()
    {
        JsonArray messages = Messages;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            JsonNode? n = messages[i];
            if (n == null || n["role"]?.GetValue<string>() != "user") continue;

            // Extract text from the first text block or inline string.
            string? text = null;
            JsonNode? content = n["content"];
            if (content is JsonValue jv)
            {
                jv.TryGetValue<string>(out text);
            }
            else if (content is JsonArray ca)
            {
                foreach (JsonNode? block in ca)
                {
                    if (block != null && block["type"]?.GetValue<string>() == "text")
                    {
                        text = block["text"]?.GetValue<string>();
                        break;
                    }
                }
            }
            messages.RemoveAt(i);
            return text;
        }
        return null;
    }

    // Producer-only: append a verbatim native assistant message (preserves thinking blocks
    // with signatures and any unknown block types Anthropic returns).
    public void AppendNativeAssistant(JsonObject nativeMessage)
    {
        JsonArray messages = Messages;
        if (messages.Count > 0)
        {
            JsonNode? last = messages[messages.Count - 1];
            if (last != null && last["role"]?.GetValue<string>() == "assistant")
            {
                JsonNode? incoming = nativeMessage["content"];
                JsonArray incomingArr = incoming is JsonArray ia ? (JsonArray)ia.DeepClone() : WrapTextAsArray(incoming);

                JsonArray existing = EnsureArrayContent(last);
                foreach (JsonNode? block in incomingArr)
                {
                    if (block != null) existing.Add(block.DeepClone());
                }
                return;
            }
        }

        messages.Add(nativeMessage);
    }

    private void AppendUserBlock(JsonObject block)
    {
        JsonArray messages = Messages;
        if (messages.Count > 0)
        {
            JsonNode? last = messages[messages.Count - 1];
            if (last != null && last["role"]?.GetValue<string>() == "user")
            {
                JsonArray content = EnsureArrayContent(last);
                content.Add(block);
                return;
            }
        }

        JsonObject msg = new JsonObject();
        msg["role"] = "user";
        JsonArray arr = new JsonArray();
        arr.Add(block);
        msg["content"] = arr;
        messages.Add(msg);
    }

    private void AppendOrMergeAssistantContent(JsonArray content)
    {
        JsonArray messages = Messages;
        if (messages.Count > 0)
        {
            JsonNode? last = messages[messages.Count - 1];
            if (last != null && last["role"]?.GetValue<string>() == "assistant")
            {
                JsonArray existing = EnsureArrayContent(last);
                foreach (JsonNode? block in content)
                {
                    if (block != null) existing.Add(block.DeepClone());
                }
                return;
            }
        }

        JsonObject msg = new JsonObject();
        msg["role"] = "assistant";
        msg["content"] = content;
        messages.Add(msg);
    }

    private static JsonObject BuildTextBlock(string text)
    {
        JsonObject block = new JsonObject();
        block["type"] = "text";
        block["text"] = text;
        return block;
    }

    private static JsonArray EnsureArrayContent(JsonNode message)
    {
        JsonNode? content = message["content"];
        if (content is JsonArray arr) return arr;

        JsonArray wrapped = WrapTextAsArray(content);
        message["content"] = wrapped;
        return wrapped;
    }

    private static JsonArray WrapTextAsArray(JsonNode? content)
    {
        JsonArray arr = new JsonArray();
        if (content is JsonValue jv && jv.TryGetValue<string>(out string? s) && !string.IsNullOrEmpty(s))
        {
            JsonObject block = new JsonObject();
            block["type"] = "text";
            block["text"] = s;
            arr.Add(block);
        }
        return arr;
    }
}
