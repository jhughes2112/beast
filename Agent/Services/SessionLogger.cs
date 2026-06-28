using System;
using System.IO;
using System.Text;

// Per-session logger. One instance per Session.
// Logs LLM request wire payloads to {sessionId}.log and failures to errors.log + stderr.
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

	// Logs a model-level failure (rate limit, transient, auth, timeout, etc.) from LlmService.
	public void ModelFailure(LlmModel model, ProtocolProxy handler, string failureType, int? statusCode, string message, int retryCount, int maxRetries, DateTimeOffset? retryAfter, bool willFallback)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("============================================================");
		sb.AppendLine($"time:       {DateTimeOffset.UtcNow:u}");
		sb.AppendLine($"level:      ERROR");
		sb.AppendLine($"category:   ModelFailure");
		sb.AppendLine($"model:      {model.ConfigId} ({model.Config.Name})");
		sb.AppendLine($"endpoint:   {model.Endpoint}");
		sb.AppendLine($"protocol:   {handler.GetDetectedProtocol()}");
		sb.AppendLine($"failure:    {failureType}");
		if (statusCode.HasValue)
			sb.AppendLine($"http_code:  {statusCode.Value}");
		sb.AppendLine($"error:      {message}");
		sb.AppendLine($"retry:      {retryCount}/{maxRetries}");
		if (retryAfter.HasValue)
			sb.AppendLine($"retry_after: {retryAfter.Value:u} (in {(retryAfter.Value - DateTimeOffset.UtcNow).TotalSeconds:F1}s)");
		sb.AppendLine($"fallback:   {(willFallback ? "YES" : "NO")}");
		sb.AppendLine();

		Log(sb.ToString());
	}

	// Logs a protocol-level failure. Protocol classes always have LlmModel + DetectedProtocol.
	public void ProtocolFailure(LlmModel model, DetectedProtocol protocol, string failureType, int? statusCode, string message, string? body, Exception? ex)
	{
		Log(BuildProtocolEntry(model.ConfigId, model.Config.Name, model.Endpoint, protocol.ToString(), failureType, statusCode, message, body, ex));
	}

	// Logs a protocol failure and returns the result, for one-liner call sites.
	public ProtocolResult ProtocolFailure(ProtocolResult result, LlmModel model, DetectedProtocol protocol, string failureType, int? statusCode, string message, string? body, Exception? ex)
	{
		ProtocolFailure(model, protocol, failureType, statusCode, message, body, ex);
		return result;
	}

	// Logs a protocol failure using bare string identity (for ProtocolHelpers which has no LlmModel).
	public void ProtocolFailure(string modelId, string modelName, string endpoint, string protocol, string failureType, int? statusCode, string message, string? body, Exception? ex)
	{
		Log(BuildProtocolEntry(modelId, modelName, endpoint, protocol, failureType, statusCode, message, body, ex));
	}

	// Logs a fallback from one model to another. Called from SessionRunner.
	public void FallbackTransition(LlmService from, LlmService to, string reason, int failedRetries)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("============================================================");
		sb.AppendLine($"time:           {DateTimeOffset.UtcNow:u}");
		sb.AppendLine($"level:          WARN");
		sb.AppendLine($"category:       FallbackTransition");
		sb.AppendLine($"from:           {from.Model.ConfigId} ({from.Model.Config.Name})");
		sb.AppendLine($"to:             {to.Model.ConfigId} ({to.Model.Config.Name})");
		sb.AppendLine($"reason:         {reason}");
		sb.AppendLine($"failed_retries: {failedRetries}");
		sb.AppendLine();

		Log(sb.ToString());
	}

	// Logs a session-level failure (all fallbacks exhausted). Called from SessionRunner.
	public void SessionFailure(Session session, LlmService service, string finalError, int totalModelsTried)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("============================================================");
		sb.AppendLine($"time:         {DateTimeOffset.UtcNow:u}");
		sb.AppendLine($"level:        ERROR");
		sb.AppendLine($"category:     SessionFailure");
		sb.AppendLine($"session:      {session.Id}");
		sb.AppendLine($"last_model:   {service.Model.ConfigId} ({service.Model.Config.Name})");
		sb.AppendLine($"endpoint:     {service.Model.Endpoint}");
		sb.AppendLine($"final_error:  {finalError}");
		sb.AppendLine($"models_tried: {totalModelsTried}");
		sb.AppendLine();

		Log(sb.ToString());
	}

	private static string BuildProtocolEntry(string modelId, string modelName, string endpoint, string protocol, string failureType, int? statusCode, string message, string? body, Exception? ex)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("============================================================");
		sb.AppendLine($"time:       {DateTimeOffset.UtcNow:u}");
		sb.AppendLine($"level:      ERROR");
		sb.AppendLine($"category:   ProtocolFailure");
		sb.AppendLine($"model:      {modelId} ({modelName})");
		sb.AppendLine($"endpoint:   {endpoint}");
		sb.AppendLine($"protocol:   {protocol}");
		sb.AppendLine($"failure:    {failureType}");
		if (statusCode.HasValue)
			sb.AppendLine($"http_code:  {statusCode.Value}");
		sb.AppendLine($"error:      {message}");
		if (!string.IsNullOrEmpty(body))
		{
			string truncated = body.Length > 2000 ? body.Substring(0, 2000) + "... [truncated]" : body;
			sb.AppendLine($"response:   {truncated}");
		}
		if (ex != null)
		{
			sb.AppendLine($"exception:  {ex.GetType().Name}: {ex.Message}");
			sb.AppendLine($"stack:      {ex.StackTrace}");
		}
		sb.AppendLine();
		return sb.ToString();
	}

	private void Log(string entry)
	{
		Console.Error.Write(entry);

		try
		{
			Directory.CreateDirectory(LogsDir);
			string errPath = Path.Combine(LogsDir, "errors.log");
			lock (_fileLock)
			{
				File.AppendAllText(errPath, entry);
			}
		}
		catch { }
	}
}
