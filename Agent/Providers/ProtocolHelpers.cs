using System;
using System.Collections.Generic;
using System.Net.Http;


// Shared utilities used by all protocol implementations.
static class ProtocolHelpers
{
    // Single shared client. Authorization is set per-request on HttpRequestMessage so
    // nothing on the client itself varies between models or calls.
    private static readonly HttpClient SharedClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly HttpClient ProbeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    public static HttpClient GetClient()
    {
        return SharedClient;
    }

    public static HttpClient GetProbeClient()
    {
        return ProbeClient;
    }

    public static bool IsRateLimited(HttpResponseMessage response, string responseBody)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return true;
        if (response.Headers.Contains("Retry-After")) return true;

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? values))
        {
            foreach (string v in values)
            {
                if (v == "0") return true;
            }
        }

        return !string.IsNullOrEmpty(responseBody) && responseBody.Contains("\"code\":429");
    }

    public static DateTimeOffset ComputeRetryAfterTime(HttpResponseMessage response, string responseBody)
    {
        int seconds = ParseRateLimitSeconds(response, responseBody);
        return seconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(seconds) : DateTimeOffset.UtcNow.AddSeconds(5);
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
            if (parsed > 0) return parsed;
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
}
