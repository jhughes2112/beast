using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;

// Shared utilities used by all protocol implementations.
static class ProtocolHelpers
{
	// Single shared client. Authorization is set per-request on HttpRequestMessage so
	// nothing on the client itself varies between models or calls.
	// MaxResponseContentBufferSize caps buffered body size to guard against unbounded malformed responses.
	private static readonly HttpClient SharedClient = new HttpClient
	{
		Timeout = TimeSpan.FromMinutes(5),
		MaxResponseContentBufferSize = 2 * 1024 * 1024  // 2 MB
    };

	public static HttpClient GetClient()
	{
		return SharedClient;
	}

	// Classifies an OperationCanceledException raised by an HTTP call. When the caller's own token is NOT
	// the one that tripped, the cancel came from the HttpClient timeout elapsing — the model was too slow
	// to start responding, or it was queued behind other requests on a busy local endpoint. Returns a
	// Transient result that says so explicitly (and names the timeout) so the loop retries instead of the
	// turn dying with an opaque "cancelled" message. Returns null when the caller genuinely cancelled, in
	// which case the protocol must rethrow so the cancel propagates as a real interrupt.
	public static ProtocolResult? TimeoutOrRethrow(CancellationToken cancellationToken, string modelName)
	{
		if (cancellationToken.IsCancellationRequested)
			return null;

		int seconds = (int)Math.Round(SharedClient.Timeout.TotalSeconds);
		return ProtocolResult.Transient($"Request to {modelName} timed out: the HTTP client timeout of {seconds}s elapsed before the model responded (too slow, or queued behind other requests). Retrying.", null);
	}

	public static bool IsRateLimited(HttpResponseMessage response, string responseBody)
	{
		if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
			return true;
		if (response.Headers.Contains("Retry-After"))
			return true;

		if (response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? values))
		{
			foreach (string v in values)
			{
				if (v == "0")
					return true;
			}
		}

		return !string.IsNullOrEmpty(responseBody) && responseBody.Contains("\"code\":429");
	}

	public static DateTimeOffset ComputeRetryAfterTime(HttpResponseMessage response, string responseBody)
	{
		int seconds = ParseRateLimitSeconds(response, responseBody);
		return seconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(seconds) : DateTimeOffset.UtcNow.AddSeconds(5);
	}

	// Like ComputeRetryAfterTime, but returns null when the response carried no retry timing at all, so a
	// transient (non-rate-limit) failure can prefer a server-stated retry time and otherwise fall back to
	// the caller's own backoff. ParseRateLimitSeconds already folds in the +1s margin.
	public static DateTimeOffset? TryGetRetryAfter(HttpResponseMessage response, string responseBody)
	{
		int seconds = ParseRateLimitSeconds(response, responseBody);
		return seconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(seconds) : (DateTimeOffset?)null;
	}

	private static int ParseRateLimitSeconds(HttpResponseMessage response, string responseBody)
	{
		string? retryAfter = GetFirstHeaderValue(response.Headers, "Retry-After");
		if (retryAfter != null && int.TryParse(retryAfter, out int seconds))
		{
			return seconds + 1;
		}

		string? rateLimitReset = GetFirstHeaderValue(response.Headers, "X-RateLimit-Reset");
		if (rateLimitReset != null && long.TryParse(rateLimitReset, out long epochValue))
		{
			return EpochToSecondsFromNow(epochValue);
		}

		if (responseBody.Contains("X-RateLimit-Reset"))
		{
			int parsed = ParseRateLimitSecondsFromBody(responseBody);
			if (parsed > 0)
				return parsed;
		}

		return 0;
	}

	private static string? GetFirstHeaderValue(System.Net.Http.Headers.HttpResponseHeaders headers, string headerName)
	{
		if (headers.TryGetValues(headerName, out IEnumerable<string>? values))
		{
			foreach (string value in values)
			{
				return value;
			}
		}

		return null;
	}

	private static int ParseRateLimitSecondsFromBody(string responseBody)
	{
		try
		{
			System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(responseBody);

			if (doc.RootElement.TryGetProperty("error", out System.Text.Json.JsonElement errorElement) &&
				errorElement.TryGetProperty("metadata", out System.Text.Json.JsonElement metadataElement) &&
				metadataElement.TryGetProperty("headers", out System.Text.Json.JsonElement headersElement) &&
				headersElement.TryGetProperty("X-RateLimit-Reset", out System.Text.Json.JsonElement resetElement))
			{
				string? resetStr = resetElement.GetString();
				if (!string.IsNullOrEmpty(resetStr) && long.TryParse(resetStr, out long epochValue))
				{
					return EpochToSecondsFromNow(epochValue);
				}
			}
		}
		catch (System.Text.Json.JsonException) { }

		return 0;
	}

	private static int EpochToSecondsFromNow(long epochValue)
	{
		long epochSeconds = epochValue > 2_000_000_000 ? epochValue / 1000 : epochValue;
		long nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		long delta = epochSeconds - nowSeconds + 1;
		return delta > 0 ? (int)delta : 0;
	}

	// The Responses API requires call_id values to start with "fc_". Canonical state may carry
	// foreign ids (e.g. OpenAI "call_...") so normalize before sending. Applied symmetrically
	// to function_call id/call_id and function_call_output call_id so pairs stay linked.
	public static string NormalizeToolCallId(string id)
	{
		if (string.IsNullOrEmpty(id))
			return id;
		if (id.StartsWith("fc_", StringComparison.Ordinal))
			return id;
		return $"fc_{id}";
	}

	// A 4xx other than the 429 handled above (and the genuinely retryable 408/425) is a permanent
	// client error: the request itself is bad, so retrying just burns the transient budget and then
	// surfaces as a misleading "rate limited". Fail fast with the body so the real cause is visible;
	// 5xx and the retryable 4xx stay transient.
	public static bool IsPermanentClientError(int statusCode)
	{
		return statusCode >= 400 && statusCode < 500 && statusCode != 408 && statusCode != 425;
	}

	// Returns true for 5xx responses — these are server-side failures that may succeed on retry.
	// The caller uses this to pick the failureType ("ServerError" vs "Transient") when logging.
	public static bool IsTransientServerError(int statusCode)
	{
		return statusCode >= 500;
	}

	// Logs and returns a Failed result for permanent client errors (4xx excluding retryable).
	public static ProtocolResult Failure(string protocol, int statusCode, string responseBody, SessionLogger logger, string modelName, string endpoint, string modelId)
	{
		string failureType = (statusCode == 401 || statusCode == 403) ? "AuthFailure" : "ClientError";
		logger.ProtocolFailure(modelId, modelName, endpoint, protocol, failureType, statusCode, responseBody, responseBody, null);
		return ProtocolResult.Failed($"HTTP {statusCode}: {responseBody}");
	}

	// Logs and returns a Transient result for server errors or retryable 4xx (408/425).
	public static ProtocolResult TransientFailure(string protocol, int statusCode, string responseBody, SessionLogger logger, string modelName, string endpoint, string modelId, HttpResponseMessage response)
	{
		string failureType = statusCode >= 500 ? "ServerError" : "Transient";
		logger.ProtocolFailure(modelId, modelName, endpoint, protocol, failureType, statusCode, responseBody, responseBody, null);
		return ProtocolResult.Transient($"HTTP {statusCode}: {responseBody}", TryGetRetryAfter(response, responseBody));
	}
}