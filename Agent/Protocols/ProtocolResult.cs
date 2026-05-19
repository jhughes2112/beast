using System;
using System.Collections.Generic;

public enum ProtocolCallOutcome
{
    Success,     // A well-formed assistant turn was received and written to session state.
    RateLimited, // The API returned a rate-limit response. RetryAfter carries the backoff time when available.
    Failed,      // A transient or unclassified error. The provider may be retried later.
    PermanentFailure // An unrecoverable error (auth failure, permanent 5xx). The provider should not be retried.
}

// Normalised payload returned on ProviderCallOutcome.Success.
// The assistant turn has already been written into session state by the protocol; this
// payload carries only the semantic info LlmService needs to drive tool execution and
// the follow-up transport.Output for committed assistant text.
public class ProtocolCallPayload
{
    // Plain assistant text from the turn (empty string if none).
    public string AssistantText { get; }

    // Tool calls the model wants to execute this turn (empty when none).
    public IReadOnlyList<SemanticToolCall> ToolCalls { get; }

    // Finish reason string as reported by the API (e.g. "stop", "tool_calls", "length").
    public string FinishReason { get; }

    // Token counts for this call.
    public TokenUsageInfo Usage { get; }

    // Cost in USD already computed by the provider using its model config.
    public decimal Cost { get; }

    public ProtocolCallPayload(string assistantText, IReadOnlyList<SemanticToolCall> toolCalls, string finishReason, TokenUsageInfo usage, decimal cost)
    {
        AssistantText = assistantText;
        ToolCalls = toolCalls;
        FinishReason = finishReason;
        Usage = usage;
        Cost = cost;
    }
}

// Result of a single provider round-trip. Use the static factory methods to construct.
public class ProtocolResult
{
    public ProtocolCallOutcome Outcome { get; }

    // Populated when Outcome == Success.
    public ProtocolCallPayload? Payload { get; }

    // Populated when Outcome == RateLimited; null if the provider had no timing information.
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

    public static ProtocolResult Failed(string errorMessage)
    {
        return new ProtocolResult(ProtocolCallOutcome.Failed, null, null, errorMessage);
    }

    public static ProtocolResult PermanentFailure(string errorMessage)
    {
        return new ProtocolResult(ProtocolCallOutcome.PermanentFailure, null, null, errorMessage);
    }
}
