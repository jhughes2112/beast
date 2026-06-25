using System;
using System.IO;
using System.Text;


// Unified per-session logger. One instance per Session.
// - Logs successful LLM request wire payloads to {sessionId}.log
// - Logs failures (model, protocol, fallback, session) to errors.log with full diagnostic context
// Replaces the former QueryLogger (success payloads) and AgentLog (error/failure logging).
public class SessionLogger
{
	private static string LogsDir => Path.Combine(Environment.CurrentDirectory, ".beast", "logs");

	private readonly string _path;
	private static readonly object _fileLock = new object();

	public SessionLogger(string sessionId)
	{
		try
		{ Directory.CreateDirectory(LogsDir); }
		catch { }
		_path = Path.Combine(LogsDir, $"{sessionId}.log");
	}

	// ---- Success logging (replaces QueryLogger.Write) ----

	// Appends one LLM request entry. json is the exact wire payload sent to the provider.
	public void Write(string modelName, string endpoint, string json)
	{
		try
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("==========================================================");
			sb.AppendLine($"time:     {DateTimeOffset.UtcNow:u}");
			sb.AppendLine($"model:    {modelName}");
			sb.AppendLine($"endpoint: {endpoint}");
			sb.AppendLine();
			sb.AppendLine(json);
			sb.AppendLine();

			File.AppendAllText(_path, sb.ToString());
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[SessionLogger] Write failed: {ex}");
		}
	}

	// ---- Failure logging (replaces AgentLog) ----

	// Logs a model failure with full diagnostic context.
	// Call this whenever an LLM call fails (rate limit, transient, auth, timeout, etc.)
	public void ModelFailure(
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
	public void ProtocolFailure(
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
	public void FallbackTransition(
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
	public void SessionFailure(
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
			Directory.CreateDirectory(LogsDir);
			string path = Path.Combine(LogsDir, "errors.log");
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
