using System.Collections.Generic;
using System.Text.Json.Serialization;


// Discriminated union of canonical conversation message types.
// The $type property in JSON is the polymorphism discriminator.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SystemMessage), "system")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "tool")]
public abstract class CanonicalMessage { }

public sealed class SystemMessage : CanonicalMessage
{
	public string Text { get; }

	[JsonConstructor]
	public SystemMessage(string text)
	{
		Text = text;
	}
}

public sealed class UserMessage : CanonicalMessage
{
	public string Text { get; }

	[JsonConstructor]
	public UserMessage(string text)
	{
		Text = text;
	}
}

// Thinking is persisted so ReplayToTransport can display it on reconnect.
// Protocols ignore it during Rehydrate — unsigned thinking cannot be replayed to the server.
public sealed class AssistantMessage : CanonicalMessage
{
	public string Text { get; }
	public string Thinking { get; }
	public IReadOnlyList<SemanticToolCall> ToolCalls { get; }

	[JsonConstructor]
	public AssistantMessage(string text, string thinking, IReadOnlyList<SemanticToolCall>? toolCalls)
	{
		Text = text;
		Thinking = thinking;
		ToolCalls = toolCalls ?? new List<SemanticToolCall>();
	}
}

public sealed class ToolResultMessage : CanonicalMessage
{
	public string ToolCallId { get; }
	public string Content { get; }

	[JsonConstructor]
	public ToolResultMessage(string toolCallId, string content)
	{
		ToolCallId = toolCallId;
		Content = content;
	}
}