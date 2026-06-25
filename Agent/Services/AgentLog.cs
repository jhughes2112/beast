using System;
using System.IO;
using System.Text;


// Centralized error logging for server-side failures.
// Writes to stderr (visible in container logs) and optionally to a persistent error log file.
// Unlike QueryLogger (which logs successful requests), this logs FAILURES with full context
// so operators can immediately diagnose: which model, what endpoint, what error, what happened next.
public static class AgentLog
{
    private static readonly string ErrorLogDir = Path.Combine(Environment.CurrentDirectory, ".beast", "logs");
    private static readonly object _fileLock = new object();

    // Logs a model failure with full diagnostic context.
    // Call this whenever an LLM call fails (rate limit, transient, auth, timeout, etc.)
    public static void ModelFailure(
        string modelId,
        string modelName,
        string endpoint,
        string protocol,
        string failureType,      // "RateLimited", "Transient", "Failed", "Timeout", "ContextFull", "TooManyRetries", "Interrupted"
        int? httpStatusCode,
        string errorMessage,
        int retryCount,
        int maxRetries,
        DateTimeOffset? retryAfter,
        bool willFallback,
        string? fallbackModelId = null,
        Exception? exception = null,
        string? stackTrace = null)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("============================================================");
        sb.AppendLine($"time:       {DateTimeOffset.UtcNow:u}");
        sb.AppendLine($"level:      ERROR");
        sb.AppendLine($"category:   ModelFailure");
        sb.AppendLine($"model_id:   {modelId}");
        sb.AppendLine($"model_name: {modelName}");
        sb.AppendLine($"endpoint:   {endpoint}");
        sb.AppendLine($"protocol:   {protocol}");
        sb.AppendLine($"failure:    {failureType}");
        if (httpStatusCode.HasValue)
            sb.AppendLine($"http_code:  {httpStatusCode.Value}");
        sb.AppendLine($"error:      {errorMessage}");
        sb.AppendLine($"retry:      {retryCount}/{maxRetries}");
        if (retryAfter.HasValue)
            sb.AppendLine($"retry_after: {retryAfter.Value:u} (in {(retryAfter.Value - DateTimeOffset.UtcNow).TotalSeconds:F1}s)");
        sb.AppendLine($"fallback:   {(willFallback ? "YES" : "NO")}" + (fallbackModelId != null ? $" -> {fallbackModelId}" : ""));
        
        if (exception != null)
        {
            sb.AppendLine($"exception:  {exception.GetType().Name}: {exception.Message}");
            sb.AppendLine($"stack:      {exception.StackTrace}");
        }
        else if (!string.IsNullOrEmpty(stackTrace))
        {
            sb.AppendLine($"stack:      {stackTrace}");
        }
        sb.AppendLine();

        string logEntry = sb.ToString();

        // Always write to stderr (container logs)
        Console.Error.Write(logEntry);

        // Also append to persistent error log file
        WriteToErrorLog(logEntry);
    }

    // Logs a protocol-level failure (detection, invalid response, etc.)
    public static void ProtocolFailure(
        string modelId,
        string modelName,
        string endpoint,
        string protocol,
        string failureType,
        int? httpStatusCode,
        string errorMessage,
        string? responseBody = null,
        Exception? exception = null)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("============================================================");
        sb.AppendLine($"time:       {DateTimeOffset.UtcNow:u}");
        sb.AppendLine($"level:      ERROR");
        sb.AppendLine($"category:   ProtocolFailure");
        sb.AppendLine($"model_id:   {modelId}");
        sb.AppendLine($"model_name: {modelName}");
        sb.AppendLine($"endpoint:   {endpoint}");
        sb.AppendLine($"protocol:   {protocol}");
        sb.AppendLine($"failure:    {failureType}");
        if (httpStatusCode.HasValue)
            sb.AppendLine($"http_code:  {httpStatusCode.Value}");
        sb.AppendLine($"error:      {errorMessage}");
        if (!string.IsNullOrEmpty(responseBody))
        {
            // Truncate very long response bodies
            string truncated = responseBody.Length > 2000 ? responseBody.Substring(0, 2000) + "... [truncated]" : responseBody;
            sb.AppendLine($"response:   {truncated}");
        }
        if (exception != null)
        {
            sb.AppendLine($"exception:  {exception.GetType().Name}: {exception.Message}");
            sb.AppendLine($"stack:      {exception.StackTrace}");
        }
        sb.AppendLine();

        string logEntry = sb.ToString();
        Console.Error.Write(logEntry);
        WriteToErrorLog(logEntry);
    }

    // Logs a fallback transition
    public static void FallbackTransition(
        string fromModelId,
        string fromModelName,
        string toModelId,
        string toModelName,
        string reason,
        int failedRetries)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("============================================================");
        sb.AppendLine($"time:         {DateTimeOffset.UtcNow:u}");
        sb.AppendLine($"level:        WARN");
        sb.AppendLine($"category:     FallbackTransition");
        sb.AppendLine($"from_model:   {fromModelId} ({fromModelName})");
        sb.AppendLine($"to_model:     {toModelId} ({toModelName})");
        sb.AppendLine($"reason:       {reason}");
        sb.AppendLine($"failed_retries: {failedRetries}");
        sb.AppendLine();

        string logEntry = sb.ToString();
        Console.Error.Write(logEntry);
        WriteToErrorLog(logEntry);
    }

    // Logs a session-level failure (no fallbacks left)
    public static void SessionFailure(
        string sessionId,
        string modelId,
        string modelName,
        string endpoint,
        string protocol,
        string finalError,
        int totalModelsTried)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("============================================================");
        sb.AppendLine($"time:           {DateTimeOffset.UtcNow:u}");
        sb.AppendLine($"level:          ERROR");
        sb.AppendLine($"category:       SessionFailure");
        sb.AppendLine($"session_id:     {sessionId}");
        sb.AppendLine($"last_model:     {modelId} ({modelName})");
        sb.AppendLine($"endpoint:       {endpoint}");
        sb.AppendLine($"protocol:       {protocol}");
        sb.AppendLine($"final_error:    {finalError}");
        sb.AppendLine($"models_tried:   {totalModelsTried}");
        sb.AppendLine();

        string logEntry = sb.ToString();
        Console.Error.Write(logEntry);
        WriteToErrorLog(logEntry);
    }

    private static void WriteToErrorLog(string entry)
    {
        try
        {
            Directory.CreateDirectory(ErrorLogDir);
            string path = Path.Combine(ErrorLogDir, "errors.log");
            lock (_fileLock)
            {
                File.AppendAllText(path, entry);
            }
        }
        catch
        {
            // Best effort - don't throw on logging failure
        }
    }
}
