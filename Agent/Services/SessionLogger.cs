using System;
using System.IO;
using System.Text;

// Per-session logger. One instance per Session.
// The session's log is meant to be a COMPLETE timeline of that session: every request actually sent
// (with the context sizing that shaped it), every mid-turn event the client was told about, and
// every failure. Failures are additionally collected in the shared errors.log for a quick sweep
// across sessions, but nothing lives only there — a log with the failures removed reads as a series
// of unexplained repeats.
public class SessionLogger
{
	private static string LogsDir => Path.Combine(Environment.CurrentDirectory, ".beast", "logs");

	private readonly        string _path;
	private static readonly object _fileLock = new object();

	// The turn's context accounting, stamped on every request header. Reading a log without these
	// means reconstructing occupancy by measuring the JSON — which is how a starved output ceiling
	// and a chunk that never actually shrank both stayed invisible for as long as they did.
	private int _contextTokens;
	private int _windowTokens;
	private int _maxOutputTokens;

	public SessionLogger(string sessionId)
	{
		try
		{ Directory.CreateDirectory(LogsDir); }
		catch { }
		_path = Path.Combine(LogsDir, $"{sessionId}.log");
	}

	// Records what the next request is being sized against. Called by LlmService before each attempt;
	// the values are provider-measured (or zero before the first response has measured anything).
	public void SetTurnContext(int contextTokens, int windowTokens, int maxOutputTokens)
	{
		_contextTokens   = contextTokens;
		_windowTokens    = windowTokens;
		_maxOutputTokens = maxOutputTokens;
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
			if (_windowTokens > 0)
			{
				int percent = (int)((long)_contextTokens * 100 / _windowTokens);
				sb.AppendLine($"context:  {_contextTokens} / {_windowTokens} tokens ({percent}%)");
			}
			if (_maxOutputTokens > 0)
				sb.AppendLine($"max_out:  {_maxOutputTokens}");
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

	// Logs a fallback from one model to another. Called from SessionRunner. `detail` is the error the
	// model actually died on — a fixed retry budget used to be printed here instead, which claimed five
	// attempts on a failure that fell back on its first.
	public void FallbackTransition(LlmService from, LlmService to, string reason, string detail)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("============================================================");
		sb.AppendLine($"time:           {DateTimeOffset.UtcNow:u}");
		sb.AppendLine($"level:          WARN");
		sb.AppendLine($"category:       FallbackTransition");
		sb.AppendLine($"from:           {from.Model.ConfigId} ({from.Model.Config.Name})");
		sb.AppendLine($"to:             {to.Model.ConfigId} ({to.Model.Config.Name})");
		sb.AppendLine($"reason:         {reason}");
		sb.AppendLine($"detail:         {detail}");
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

	// How the request that was just logged actually turned out. Written for EVERY attempt, success
	// included: a request entry with no outcome beside it tells you what was asked and nothing about
	// what came back, which is how a stream that died mid-reasoning looked identical in the log to
	// one that answered fine.
	public void WriteOutcome(string outcome, int? httpStatus, string? error)
	{
		try
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("----------------------------------------------------------");
			sb.AppendLine($"time:     {DateTimeOffset.UtcNow:u}");
			sb.AppendLine($"outcome:  {outcome}");
			if (httpStatus.HasValue && httpStatus.Value > 0)
				sb.AppendLine($"http:     {httpStatus.Value}");
			if (!string.IsNullOrEmpty(error))
			{
				string trimmed = error!.Length > 2000 ? error.Substring(0, 2000) + "... [truncated]" : error;
				sb.AppendLine($"error:    {trimmed}");
			}
			sb.AppendLine();

			lock (_fileLock)
			{
				File.AppendAllText(_path, sb.ToString());
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[SessionLogger] WriteOutcome failed: {ex}");
		}
	}

	// A one-line event in this session's own timeline: the things the client is told about a turn
	// mid-flight — an empty response being retried, a forced tool call not honored, a reasoning
	// feature being switched off — which otherwise reach the user and vanish. Without them the log
	// shows two identical requests with nothing between them, and no way to tell that the model
	// answered in between with reasoning and nothing else.
	public void Note(string text)
	{
		try
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("----------------------------------------------------------");
			sb.AppendLine($"time:     {DateTimeOffset.UtcNow:u}");
			sb.AppendLine($"note:     {text}");
			sb.AppendLine();

			lock (_fileLock)
			{
				File.AppendAllText(_path, sb.ToString());
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[SessionLogger] Note failed: {ex}");
		}
	}

	// Failures go to stderr, to the shared errors.log, AND to this session's own log. The session
	// log is the timeline someone actually reads to understand what a session did; a failure that
	// only lands in errors.log leaves an unexplained gap in it, and correlating the two by timestamp
	// is work the log should have done itself.
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
				File.AppendAllText(  _path, entry);
			}
		}
		catch { }
	}
}