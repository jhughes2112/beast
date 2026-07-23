using System;
using System.Collections.Generic;

public enum ProtocolCallOutcome
{
	Success,      // A well-formed assistant turn was received and written to session state.
	RateLimited,  // The API returned a rate-limit response. RetryAfter carries the backoff time when available.
	Transient,    // A recoverable error (network failure, timeout, 5xx, bad request). LlmService backs off and retries the same model a few times, then escalates to TooManyRetries so the caller can fall back.
	Failed,       // An unrecoverable error (auth failure, unknown protocol). LlmService marks the model permanently down.
	Interrupted,  // The turn was cancelled by the user. Not a retryable outcome; the caller handles session cleanup.
	TooManyRetries, // Rate-limited repeatedly; caller should try another model or abort.
	ContextFull,  // Caller's context budget is exhausted before attempting a call; not a model failure.
	Yielded,      // A retry backoff was interrupted because session input arrived. The caller drains the input (a /model or steering applies) and re-enters; not a failure.
}

// Normalised payload returned on ProviderCallOutcome.Success.
// Holds the explicit outcomes from a single turn: thinking, assistant text, tool call requests,
// tool results, and token counts for this exchange. Also carries protocol-specific native objects
// (wire-format JSON message/response nodes) so downstream code can inspect raw provider data
// without committing it directly to canonical state. The protocol itself controls what gets
// committed via its own Rehydrate/On* methods.
public class ProtocolCallPayload
{
	// Plain assistant text from the turn (empty string if none).
	public string AssistantText { get; }
	public string Thinking { get; }

	// Tool calls the model wants to execute this turn (empty when none).
	public IReadOnlyList<SemanticToolCall> ToolCalls { get; }

	// Tool results produced by this turn's execution (empty when no tools ran, gets filled in by the caller after the tools are done running).
	public List<ToolResult> ToolResults { get; }

	// Finish reason string as reported by the API (e.g. "stop", "tool_calls", "length").
	public string FinishReason { get; }

	// Token counts for this call.
	public TokenUsageInfo Usage { get; }

	// Cost in USD already computed by the provider using its model config.
	public decimal Cost { get; }

	// Protocol-specific native objects for raw inspection (not committed to canonical state).
	// ChatCompletions: JsonArray of message objects. Anthropic: wire-format JsonObject message.
	// Responses: JsonObject representing the response block. Null when not applicable.
	public object? NativeAnthropic { get; }
	public object? NativeResponses { get; }

	public ProtocolCallPayload(
		string assistantText,
		string thinking,
		IReadOnlyList<SemanticToolCall> toolCalls,
		List<ToolResult> toolResults,
		string finishReason,
		TokenUsageInfo usage,
		decimal cost,
		object? nativeAnthropic = null,
		object? nativeResponses = null)
	{
		AssistantText = assistantText;
		Thinking = thinking;
		ToolCalls = toolCalls;
		ToolResults = toolResults;
		FinishReason = finishReason;
		Usage = usage;
		Cost = cost;
		NativeAnthropic = nativeAnthropic;
		NativeResponses = nativeResponses;
	}
}

// Result of a single provider round-trip. Use the static factory methods to construct.
public class ProtocolResult
{
	public ProtocolCallOutcome Outcome { get; }

	// Populated when Outcome == Success.
	public ProtocolCallPayload? Payload { get; }

	// Populated when Outcome == RateLimited, and when a Transient error's response carried retry timing;
	// null if the provider gave no timing information (the caller then backs off on its own schedule).
	public DateTimeOffset? RetryAfter { get; }

	// Human-readable detail for non-success outcomes.
	public string ErrorMessage { get; }

	private ProtocolResult(ProtocolCallOutcome outcome, ProtocolCallPayload? payload, DateTimeOffset? retryAfter, string errorMessage)
	{
		Outcome = outcome;
		Payload = payload;
		RetryAfter = retryAfter;
		ErrorMessage = errorMessage;
	}

	public static ProtocolResult Succeeded(ProtocolCallPayload payload)
	{
		return new ProtocolResult(ProtocolCallOutcome.Success, payload, null, "");
	}

	public static ProtocolResult RateLimited(DateTimeOffset? retryAfter)
	{
		return new ProtocolResult(ProtocolCallOutcome.RateLimited, null, retryAfter, "");
	}

	public static ProtocolResult Transient(string errorMessage, DateTimeOffset? retryAfter)
	{
		return new ProtocolResult(ProtocolCallOutcome.Transient, null, retryAfter, errorMessage);
	}

	public static ProtocolResult Failed(string errorMessage)
	{
		return new ProtocolResult(ProtocolCallOutcome.Failed, null, null, errorMessage);
	}

	public static ProtocolResult Interrupted(string errorMessage, ProtocolCallPayload? payload)
	{
		return new ProtocolResult(ProtocolCallOutcome.Interrupted, payload, null, errorMessage);
	}

	public static ProtocolResult TooManyRetries()
	{
		return new ProtocolResult(ProtocolCallOutcome.TooManyRetries, null, null, "");
	}

	public static ProtocolResult ContextFull(string errorMessage)
	{
		return new ProtocolResult(ProtocolCallOutcome.ContextFull, null, null, errorMessage);
	}

	public static ProtocolResult Yielded()
	{
		return new ProtocolResult(ProtocolCallOutcome.Yielded, null, null, string.Empty);
	}
}