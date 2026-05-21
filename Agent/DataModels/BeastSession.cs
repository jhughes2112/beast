using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// A session is conversation data plus minimal runtime tracking. Persisted to .beast/sessions/<id>.json.
// State is stored separately for each protocol in native wire format so switching protocols
// mid-conversation preserves provider-specific block fidelity (signatures, unknown types).
// This class is pure data — no listeners, no bundle, no transport.
public class BeastSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("workflow")]
    public string Workflow { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    // Native protocol state: each is the exact collection of message/input items in the
    // shape the corresponding protocol sends to its endpoint.
    [JsonPropertyName("chatCompletionsState")]
    public JsonArray ChatCompletionsState { get; set; } = new JsonArray();

    [JsonPropertyName("responsesState")]
    public JsonArray ResponsesState { get; set; } = new JsonArray();

    // AnthropicState is a JsonObject with two keys:
    //   "system"   — string, the hoisted system prompt (updated by ListenerAnthropic.OnSystemMessage)
    //   "messages" — JsonArray of native Anthropic message objects
    [JsonPropertyName("anthropicState")]
    public JsonObject AnthropicState { get; set; } = new JsonObject();

    [JsonPropertyName("lastTokenUsage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TokenUsageInfo? LastTokenUsage { get; set; }

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    // True when there is uncommitted user/tool input awaiting an LLM response.
    // Set by the orchestrator when user input arrives or tool results are recorded;
    // cleared when the LLM turn completes.
    [JsonIgnore]
    public bool NeedsLlmAttention { get; set; }

    [JsonConstructor]
    public BeastSession(
        string id,
        string displayName,
        string workflow,
        string model,
        string role,
        JsonArray chatCompletionsState,
        JsonArray responsesState,
        JsonObject anthropicState,
        TokenUsageInfo? lastTokenUsage,
        decimal totalCost)
    {
        Id = id;
        DisplayName = displayName;
        Workflow = workflow;
        Model = model;
        Role = role;
        ChatCompletionsState = chatCompletionsState ?? new JsonArray();
        ResponsesState = responsesState ?? new JsonArray();
        AnthropicState = anthropicState ?? new JsonObject();
        LastTokenUsage = lastTokenUsage;
        TotalCost = totalCost;
    }

    public static BeastSession CreateNew(string id, string role, string displayName)
    {
        return new BeastSession(
            id, displayName, string.Empty, string.Empty, role,
            new JsonArray(), new JsonArray(), new JsonObject(), null, 0m);
    }

    // Rough token estimate by character count when no provider-reported usage is available.
    public int GetUsedTokenCount()
    {
        if (LastTokenUsage != null)
        {
            return LastTokenUsage.TotalTokens;
        }

        return (int)(MeasureChars(ChatCompletionsState) / 4);
    }

    private static long MeasureChars(JsonArray arr)
    {
        long total = 0;
        foreach (JsonNode? node in arr)
        {
            if (node != null) total += node.ToJsonString().Length;
        }
        return total;
    }

    // Returns the first system message content from ChatCompletionsState, or empty if none.
    public string GetSystemPrompt()
    {
        foreach (JsonNode? n in ChatCompletionsState)
        {
            if (n != null && n["role"]?.GetValue<string>() == "system")
            {
                return n["content"]?.GetValue<string>() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    // Returns the first non-empty user message text, used to pick a friendly DisplayName.
    public string? GetFirstUserText()
    {
        foreach (JsonNode? n in ChatCompletionsState)
        {
            if (n != null && n["role"]?.GetValue<string>() == "user")
            {
                string? c = n["content"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }
        }
        return null;
    }

    // Deep snapshot of all three states; used by compaction for rollback.
    public StateSnapshot Snapshot()
    {
        return new StateSnapshot(
            (JsonArray)ChatCompletionsState.DeepClone(),
            (JsonArray)ResponsesState.DeepClone(),
            (JsonObject)AnthropicState.DeepClone());
    }

    // Restores state in-place so existing listener array references remain valid.
    public void RestoreSnapshot(StateSnapshot snapshot)
    {
        ChatCompletionsState.Clear();
        foreach (JsonNode? node in snapshot.ChatCompletions)
        {
            ChatCompletionsState.Add(node?.DeepClone());
        }

        ResponsesState.Clear();
        foreach (JsonNode? node in snapshot.Responses)
        {
            ResponsesState.Add(node?.DeepClone());
        }

        List<string> keys = new List<string>();
        foreach (KeyValuePair<string, JsonNode?> kvp in AnthropicState)
        {
            keys.Add(kvp.Key);
        }
        foreach (string key in keys)
        {
            AnthropicState.Remove(key);
        }
        foreach (KeyValuePair<string, JsonNode?> kvp in snapshot.Anthropic)
        {
            AnthropicState[kvp.Key] = kvp.Value?.DeepClone();
        }
    }
}
