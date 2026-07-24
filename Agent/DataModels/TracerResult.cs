// Result of a tracer call (max_output_tokens=1) used to probe context size before the real request.
public class TracerResult
{
	// True if the tracer call succeeded and returned accurate token counts.
	public bool Succeeded { get; }

	// True if the tracer returned a 4xx error indicating the context is blown past the limit.
	public bool ContextBlown { get; }

	// Accurate input token count from the provider (includes cached tokens).
	public int InputTokens { get; }

	// Cached tokens from the provider's prompt cache.
	public int CachedTokens { get; }

	// Error message when the tracer failed (not context-blown).
	public string? ErrorMessage { get; }

	// HTTP status of the provider response that failed the tracer; 0 when the failure was not an
	// HTTP response. The tracer only runs when the caller's estimate already sits at the
	// compaction threshold, so a client-rejection status here is structural overflow evidence
	// even when the body text is unrecognized.
	public int HttpStatus { get; }

	public TracerResult(bool succeeded, bool contextBlown, int inputTokens, int cachedTokens, string? errorMessage, int httpStatus)
	{
		Succeeded = succeeded;
		ContextBlown = contextBlown;
		InputTokens = inputTokens;
		CachedTokens = cachedTokens;
		ErrorMessage = errorMessage;
		HttpStatus = httpStatus;
	}

	public static TracerResult Success(int inputTokens, int cachedTokens)
	{
		return new TracerResult(true, false, inputTokens, cachedTokens, null, 0);
	}

	public static TracerResult ContextExceeded(int statusCode)
	{
		return new TracerResult(false, true, 0, 0, $"HTTP {statusCode}: context exceeds limit", statusCode);
	}

	public static TracerResult Failed(string errorMessage)
	{
		return new TracerResult(false, false, 0, 0, errorMessage, 0);
	}

	public static TracerResult FailedHttp(int statusCode, string errorMessage)
	{
		return new TracerResult(false, false, 0, 0, errorMessage, statusCode);
	}
}