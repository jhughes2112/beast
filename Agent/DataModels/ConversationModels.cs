using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;


// A single message in a conversation, matching the LLM ChatMessage wire format exactly.
public class ConversationMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ConversationToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    // OpenRouter web search plugin populates this with url_citation entries.
    // Ignored when null so it does not pollute outbound conversation history.
    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonElement>? Annotations { get; set; }
}

public class ConversationToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ConversationFunctionCall Function { get; set; } = new();
}

public class ConversationFunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

public class TokenUsageInfo
{
    [JsonPropertyName("promptTokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completionTokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; set; }
}

// A session is a conversation plus runtime tracking. Persisted to .beast/sessions/<id>.json.
// The JSON fields are the durable state; JsonIgnore fields are runtime-only.
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

    [JsonPropertyName("messages")]
    public List<ConversationMessage> Messages { get; set; } = new();

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("lastTokenUsage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TokenUsageInfo? LastTokenUsage { get; set; }

    // Runtime-only: tracks how many messages were already sent so we can detect new appends.
    [JsonIgnore] public int SentMessageCount { get; private set; }

    // Accumulated cost across all LLM turns in this session.
    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonConstructor]
    public BeastSession(string id, string displayName, string workflow, string model, List<ConversationMessage> messages, string role, TokenUsageInfo? lastTokenUsage, decimal totalCost)
    {
        Id = id;
        DisplayName = displayName;
        Workflow = workflow;
        Model = model;
        Messages = messages;
        Role = role;
        LastTokenUsage = lastTokenUsage;
        TotalCost = totalCost;
    }

    public static BeastSession CreateNew(string id, string role, string displayName)
    {
        List<ConversationMessage> messages = new List<ConversationMessage> { new ConversationMessage { Role = "system", Content = string.Empty } };
        return new BeastSession(id, displayName, string.Empty, string.Empty, messages, role, null, 0m);
    }

    public int GetUsedTokenCount()
    {
        if (LastTokenUsage != null)
        {
            return LastTokenUsage.TotalTokens;
        }

        long totalChars = 0;
        foreach (ConversationMessage m in Messages)
        {
            if (!string.IsNullOrEmpty(m.Content)) totalChars += m.Content.Length;
            if (m.ToolCalls != null)
            {
                foreach (ConversationToolCall tc in m.ToolCalls)
                {
                    if (!string.IsNullOrEmpty(tc.Function.Arguments)) totalChars += tc.Function.Arguments.Length;
                }
            }
        }
        return (int)(totalChars / 4);
    }

    // Returns true if content was appended to the last user message, false if a new message was added.
    // When false, the caller should not consider the input consumed if it was queued before the last send.
    public bool AddUserMessage(string content)
    {
        if (Messages.Count > 0 && Messages[^1].Role == "user" && Messages.Count - 1 >= SentMessageCount)
        {
            Messages[^1].Content = Messages[^1].Content + "\n" + content;
            return true;
        }
        else
        {
            Messages.Add(new ConversationMessage { Role = "user", Content = content });
            return false;
        }
    }

    public void MarkMessagesSent()
    {
        SentMessageCount = Messages.Count;
    }

    public void ResetSentMessageCount()
    {
        SentMessageCount = 0;
    }

	// This exists because assistant message ALSO include tool calls sometimes.
    public void AddAssistantMessage(ConversationMessage message)
    {
        Messages.Add(message);
    }

    public void AddAssistantMessage(string content)
    {
        Messages.Add(new ConversationMessage { Role = "assistant", Content = content });
    }

    public void AddSystemMessage(string content)
    {
        Messages.Add(new ConversationMessage { Role = "system", Content = content });
    }

    public void AddToolMessage(string toolCallId, string toolResult)
    {
        Messages.Add(new ConversationMessage { Role = "tool", Content = toolResult, ToolCallId = toolCallId });
    }

    // Sets the system prompt in the first message slot.
    // Called by LlmService immediately before each LLM request.
    public void SetSystemPrompt(string prompt)
    {
        Messages[0] = new ConversationMessage { Role = "system", Content = prompt };
    }
}
