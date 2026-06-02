using System;
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

    // Native protocol state in canonical ChatCompletions wire shape. This is the single
    // persisted source of truth. Live protocol-listeners (Responses, Anthropic) keep their own
    // native runtime state in-memory and rehydrate from this array on creation or model switch.
    [JsonPropertyName("chatCompletionsState")]
    public JsonArray ChatCompletionsState { get; set; } = new JsonArray();

    [JsonPropertyName("lastTokenUsage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TokenUsageInfo? LastTokenUsage { get; set; }

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    // Absolute session totals. These only ever increase over the life of a session and are never
    // reset, not on model switch and not on compaction. CumulativeInputTokens and
    // CumulativeOutputTokens are what the client displays as the running in/out counters, while
    // LastTokenUsage stays scoped to the most recent turn for context-occupancy math.
    [JsonPropertyName("cumulativeInputTokens")]
    public int CumulativeInputTokens { get; set; }

    [JsonPropertyName("cumulativeOutputTokens")]
    public int CumulativeOutputTokens { get; set; }

    // Accrues one committed turn's authoritative usage and cost into the monotonic session totals,
    // and records the turn usage for context-occupancy. The only path that mutates these counters,
    // so they can only grow.
    public void AddTurnUsage(TokenUsageInfo usage, decimal cost)
    {
        CumulativeInputTokens += usage.PromptTokens;
        CumulativeOutputTokens += usage.CompletionTokens;
        TotalCost += cost;
        LastTokenUsage = usage;
    }

    // Returns true when the LLM has pending work: either the last message is a user turn,
    // or there is an assistant tool_call ID with no matching tool result anywhere in state.
    public bool NeedsLlmAttention()
    {
        int count = ChatCompletionsState.Count;
        if (count == 0) return false;

        string? lastRole = ChatCompletionsState[count - 1]?["role"]?.GetValue<string>();
        if (lastRole == "user") return true;

        // Collect all tool result IDs present in state.
        System.Collections.Generic.HashSet<string> satisfiedIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in ChatCompletionsState)
        {
            if (node != null && node["role"]?.GetValue<string>() == "tool")
            {
                string? id = node["tool_call_id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) satisfiedIds.Add(id);
            }
        }

        // Check if any assistant tool_call lacks a result.
        foreach (JsonNode? node in ChatCompletionsState)
        {
            if (node == null || node["role"]?.GetValue<string>() != "assistant") continue;
            JsonArray? toolCalls = node["tool_calls"]?.AsArray();
            if (toolCalls == null) continue;
            foreach (JsonNode? tc in toolCalls)
            {
                string? id = tc?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id) && !satisfiedIds.Contains(id)) return true;
            }
        }

        return false;
    }

    // True when the session has no meaningful content worth persisting.
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(DisplayName);

    // When true, the session is strictly in-memory: never saved to disk and never set as the
    // resume target. Used by "/session none" for benchmarks and throwaway queries.
    [JsonIgnore]
    public bool Ephemeral { get; set; }

    [JsonConstructor]
    public BeastSession(
        string id,
        string displayName,
        string workflow,
        string model,
        string role,
        JsonArray chatCompletionsState,
        TokenUsageInfo? lastTokenUsage,
        decimal totalCost,
        int cumulativeInputTokens,
        int cumulativeOutputTokens)
    {
        Id = id;
        DisplayName = displayName;
        Workflow = workflow;
        Model = model;
        Role = role;
        ChatCompletionsState = chatCompletionsState ?? new JsonArray();
        LastTokenUsage = lastTokenUsage;
        TotalCost = totalCost;
        CumulativeInputTokens = cumulativeInputTokens;
        CumulativeOutputTokens = cumulativeOutputTokens;
    }

    public static BeastSession CreateNew(string id, string role, string displayName)
    {
        return new BeastSession(
            id, displayName, string.Empty, string.Empty, role,
            new JsonArray(), null, 0m, 0, 0);
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

    // Produces a display name for a compacted continuation: strips any existing "(N) " prefix,
    // then prepends "(N+1) " so the chain reads "(1) hello", "(2) hello", etc.
    public static string IncrementDisplayName(string displayName)
    {
        string base_ = displayName;
        int generation = 1;

        if (displayName.Length > 3 && displayName[0] == '(')
        {
            int close = displayName.IndexOf(')');
            if (close > 1 && int.TryParse(displayName.Substring(1, close - 1), out int n))
            {
                generation = n + 1;
                base_ = displayName.Substring(close + 1).TrimStart();
            }
        }

        return $"({generation}) {base_}";
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

    // Deep snapshot of canonical state; used by compaction for rollback.
    public JsonArray Snapshot()
    {
        return (JsonArray)ChatCompletionsState.DeepClone();
    }

    // Restores state in-place so existing listener array references remain valid.
    public void RestoreSnapshot(JsonArray snapshot)
    {
        ChatCompletionsState.Clear();
        foreach (JsonNode? node in snapshot)
        {
            ChatCompletionsState.Add(node?.DeepClone());
        }
    }
}
