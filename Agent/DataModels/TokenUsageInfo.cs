using System.Text.Json.Serialization;

// Used by LlmService for in-memory XML-tool-call parsing only. Not persisted; not a wire type.
public class ConversationToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "function";
    public ConversationFunctionCall Function { get; set; } = new ConversationFunctionCall();
}

public class ConversationFunctionCall
{
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

// A semantic tool call as raised by a producing protocol's fan-out. Each foreign
// listener translates this into the native shape its wire format expects.
public class SemanticToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = string.Empty;
}

// Token usage reported by the provider for the most recent turn.
public class TokenUsageInfo
{
    [JsonPropertyName("promptTokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completionTokens")]
    public int CompletionTokens { get; set; }
}
