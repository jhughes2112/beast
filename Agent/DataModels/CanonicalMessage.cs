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

// A media part (image or audio) carried by a user message, stored as base64 so the canonical
// record is self-contained across save/load. Protocols convert it to their wire format.
public sealed class MediaAttachment
{
	public string MimeType { get; }
	public string Base64Data { get; }

	[JsonConstructor]
	public MediaAttachment(string mimeType, string base64Data)
	{
		MimeType = mimeType;
		Base64Data = base64Data;
	}
}

public sealed class UserMessage : CanonicalMessage
{
	public string Text { get; }

	// Media parts sent with this message; null for plain text (the overwhelmingly common case,
	// and what every session file written before attachments existed deserializes to).
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public IReadOnlyList<MediaAttachment>? Attachments { get; }

	[JsonConstructor]
	public UserMessage(string text, IReadOnlyList<MediaAttachment>? attachments)
	{
		Text = text;
		Attachments = attachments;
	}

	public UserMessage(string text)
		: this(text, null)
	{
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