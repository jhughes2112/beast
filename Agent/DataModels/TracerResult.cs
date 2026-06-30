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

	public TracerResult(bool succeeded, bool contextBlown, int inputTokens, int cachedTokens, string? errorMessage)
	{
		Succeeded = succeeded;
		ContextBlown = contextBlown;
		InputTokens = inputTokens;
		CachedTokens = cachedTokens;
		ErrorMessage = errorMessage;
	}

	public static TracerResult Success(int inputTokens, int cachedTokens)
	{
		return new TracerResult(true, false, inputTokens, cachedTokens, null);
	}

	public static TracerResult ContextExceeded(int statusCode)
	{
		return new TracerResult(false, true, 0, 0, $"HTTP {statusCode}: context exceeds limit");
	}

	public static TracerResult Failed(string errorMessage)
	{
		return new TracerResult(false, false, 0, 0, errorMessage);
	}
}