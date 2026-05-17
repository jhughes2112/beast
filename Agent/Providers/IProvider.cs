using System;

public enum ProviderCallOutcome
{
    Success,     // A well-formed assistant message was received.
    RateLimited, // The API returned a rate-limit response. RetryAfter carries the backoff time when available.
    Failed,      // A transient or unclassified error. The provider may be retried later.
    PermanentFailure // An unrecoverable error (auth failure, permanent 5xx). The provider should not be retried.
}

// Normalised payload returned on ProviderCallOutcome.Success.
public class ProviderCallPayload
{
    // The assistant turn returned by the model.
    public ConversationMessage Message { get; }

    // Finish reason string as reported by the API (e.g. "stop", "tool_calls", "length").
    public string FinishReason { get; }

    // Token counts for this call.
    public TokenUsageInfo Usage { get; }

    // Cost in USD already computed by the provider using its model config.
    public decimal Cost { get; }

    public ProviderCallPayload(ConversationMessage message, string finishReason, TokenUsageInfo usage, decimal cost)
    {
        Message = message;
        FinishReason = finishReason;
        Usage = usage;
        Cost = cost;
    }
}

// Result of a single provider round-trip. Use the static factory methods to construct.
public class ProviderCallResult
{
    public ProviderCallOutcome Outcome { get; }

    // Populated when Outcome == Success.
    public ProviderCallPayload? Payload { get; }

    // Populated when Outcome == RateLimited; null if the provider had no timing information.
    public DateTimeOffset? RetryAfter { get; }

    // Human-readable detail for non-success outcomes.
    public string ErrorMessage { get; }

    private ProviderCallResult(ProviderCallOutcome outcome, ProviderCallPayload? payload, DateTimeOffset? retryAfter, string errorMessage)
    {
        Outcome = outcome;
        Payload = payload;
        RetryAfter = retryAfter;
        ErrorMessage = errorMessage;
    }

    public static ProviderCallResult Succeeded(ProviderCallPayload payload)
    {
        return new ProviderCallResult(ProviderCallOutcome.Success, payload, null, "");
    }

    public static ProviderCallResult RateLimited(DateTimeOffset? retryAfter)
    {
        return new ProviderCallResult(ProviderCallOutcome.RateLimited, null, retryAfter, "");
    }

    public static ProviderCallResult Failed(string errorMessage)
    {
        return new ProviderCallResult(ProviderCallOutcome.Failed, null, null, errorMessage);
    }

    public static ProviderCallResult PermanentFailure(string errorMessage)
    {
        return new ProviderCallResult(ProviderCallOutcome.PermanentFailure, null, null, errorMessage);
    }
}
