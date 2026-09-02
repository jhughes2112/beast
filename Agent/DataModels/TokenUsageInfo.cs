using System.Text.Json.Serialization;

// Used by LlmService for in-memory XML-tool-call parsing only. Not persisted; not a wire type.
public class ConversationToolCall
{
	public string                   Id       { get; set; } = string.Empty;
	public string                   Type     { get; set; } = "function";
	public ConversationFunctionCall Function { get; set; } = new ConversationFunctionCall();
}

public class ConversationFunctionCall
{
	public string Name      { get; set; } = string.Empty;
	public string Arguments { get; set; } = string.Empty;
}

// A semantic tool call as raised by a producing protocol's fan-out. Each foreign
// listener translates this into the native shape its wire format expects.
public class SemanticToolCall
{
	public string Id            { get; set; } = string.Empty;
	public string Name          { get; set; } = string.Empty;
	public string ArgumentsJson { get; set; } = string.Empty;
}

// Result returned by a tool after execution.
// ExitCode: 0 = success, non-zero = error
// StdOut: Response content when successful (or both stdout and stderr may be present)
// StdErr: Error message when exitCode is non-zero (or both stdout and stderr may be present)
public class ToolResult
{
	public string Id       { get; }
	public string StdOut   { get; }
	public string StdErr   { get; }
	public int    ExitCode { get; }

	// Token size of this result. A sub-session reply carries its provider's exact measurement; a raw
	// handler's output has no server count, so it is estimated (~4 chars/token) and clipped to the
	// fixed raw-output ceiling. Always a real positive count — never a zero placeholder.
	public int MeasuredOutputTokens { get; }

	// A media file the result carries for the model to see natively, referenced by path rather
	// than copied: protocols read the bytes when they build the wire message. Null for the
	// ordinary text-only result.
	public string? MediaPath     { get; }
	public string? MediaMimeType { get; }

	public ToolResult(string id, string stdOut, string stdErr, int exitCode, int measuredOutputTokens)
		: this(id, stdOut, stdErr, exitCode, measuredOutputTokens, null, null)
	{
	}

	public ToolResult(string id, string stdOut, string stdErr, int exitCode, int measuredOutputTokens, string? mediaPath, string? mediaMimeType)
	{
		Id                   = id;
		StdOut               = stdOut;
		StdErr               = stdErr;
		ExitCode             = exitCode;
		MeasuredOutputTokens = measuredOutputTokens;
		MediaPath            = mediaPath;
		MediaMimeType        = mediaMimeType;
	}
}

// Token usage reported by the provider for the most recent turn.
public class TokenUsageInfo
{
	[JsonPropertyName("promptTokens")]
	public int PromptTokens { get; set; }

	[JsonPropertyName("completionTokens")]
	public int CompletionTokens { get; set; }

	// Cached tokens read from the provider's prompt cache this turn (0 when not applicable).
	[JsonPropertyName("cachedTokens")]
	public int CachedTokens { get; set; }
}