using System;

// Lightweight extension methods over SessionLogger that collapse the verbose named-parameter
// calls at the common failure sites. Each helper captures the model/protocol identity that is
// repeated on every call site, so the call sites can just state the *new* information — the
// failure kind and whatever is specific to that branch — and let the helper fill in the rest.
//
// The helpers are defined in their own file so they don't bloat SessionLogger itself, and so
// that call sites in LlmService / Protocol* / SessionRunner can `using static SessionLoggerExtensions;`
// and get them automatically without importing a separate namespace.
public static class SessionLoggerExtensions
{
	// ---- ModelFailure helpers ----
	//
	// The ModelFailure signature is long (14 params). Every call site already has a Session
	// (or a Model + handler) so we can derive modelId/modelName/endpoint/protocol from it.
	// The helpers below take only the *failure-specific* arguments.

	/// <summary>Logs a model failure and returns the supplied ProtocolResult so the call site can
	/// return it directly. Preserves the original shape: `logger.ModelFailure(...); return result;`
	/// becomes `return logger.ModelFailure(result, ..., "Interrupted");`.</summary>
	public static ProtocolResult ModelFailure(this SessionLogger logger, ProtocolResult result,
		string failureType, int? httpStatusCode, string errorMessage,
		int retryCount, int maxRetries, DateTimeOffset? retryAfter, bool willFallback,
		LlmModel model, ProtocolProxy handler, Exception? exception = null, string? stackTrace = null)
	{
		logger.ModelFailure(
		model.ConfigId,
		model.Config.Name,
		model.Endpoint,
		handler.GetDetectedProtocol().ToString(),
		failureType,
		httpStatusCode,
		errorMessage,
		retryCount,
		maxRetries,
		retryAfter,
		willFallback,
		null,
		exception,
		stackTrace);
		return result;
	}

	/// <summary>Logs a model failure (no result to return) using a LlmModel + handler.</summary>
	public static void ModelFailure(this SessionLogger logger,
		string failureType, int? httpStatusCode, string errorMessage,
		int retryCount, int maxRetries, DateTimeOffset? retryAfter, bool willFallback,
		LlmModel model, ProtocolProxy handler, Exception? exception = null, string? stackTrace = null)
	{
		logger.ModelFailure(
			model.ConfigId,
			model.Config.Name,
			model.Endpoint,
			handler.GetDetectedProtocol().ToString(),
			failureType,
			httpStatusCode,
			errorMessage,
			retryCount,
			maxRetries,
			retryAfter,
			willFallback,
			null,
			exception,
			stackTrace);
	}

	/// <summary>Logs a model failure and returns the supplied ProtocolResult. Overload that takes
	/// an LlmModel + DetectedProtocol directly (used by protocol classes that hold the model but
	/// not the session).</summary>
	public static ProtocolResult ModelFailure(this SessionLogger logger, ProtocolResult result,
		string failureType, int? httpStatusCode, string errorMessage,
		int retryCount, int maxRetries, DateTimeOffset? retryAfter, bool willFallback,
		LlmModel model, DetectedProtocol protocol, Exception? exception = null, string? stackTrace = null)
	{
		logger.ModelFailure(
			model.ConfigId,
			model.Config.Name,
			model.Endpoint,
			protocol.ToString(),
			failureType,
			httpStatusCode,
			errorMessage,
			retryCount,
			maxRetries,
			retryAfter,
			willFallback,
			null,
			exception,
			stackTrace);
		return result;
	}

	// ---- ProtocolFailure helpers ----
	//
	// ProtocolFailure has 8 params. The model/protocol identity is always available at the call
	// site, so we collapse it the same way.

	/// <summary>Logs a protocol failure and returns the supplied result. Uses a LlmModel + handler.</summary>
	public static ProtocolResult ProtocolFailure(this SessionLogger logger, ProtocolResult result,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception, LlmModel model, ProtocolProxy handler)
	{
		logger.ProtocolFailure(
			model.ConfigId,
			model.Config.Name,
			model.Endpoint,
			handler.GetDetectedProtocol().ToString(),
			failureType,
			httpStatusCode,
			errorMessage,
			responseBody,
			exception);
		return result;
	}

	/// <summary>Logs a protocol failure and returns the supplied result. Uses a model + protocol directly.</summary>
	public static ProtocolResult ProtocolFailure(this SessionLogger logger, ProtocolResult result,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception, LlmModel model, DetectedProtocol protocol)
	{
		logger.ProtocolFailure(
			model.ConfigId,
			model.Config.Name,
			model.Endpoint,
			protocol.ToString(),
			failureType,
			httpStatusCode,
			errorMessage,
			responseBody,
			exception);
		return result;
	}

	/// <summary>Logs a protocol failure (no result to return) using a LlmModel + handler.</summary>
	public static void ProtocolFailure(this SessionLogger logger,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception, LlmModel model, ProtocolProxy handler)
	{
		logger.ProtocolFailure(
			model.ConfigId,
			model.Config.Name,
			model.Endpoint,
			handler.GetDetectedProtocol().ToString(),
			failureType,
			httpStatusCode,
			errorMessage,
			responseBody,
			exception);
	}

	/// <summary>Logs a protocol failure (no result to return) using a model + protocol directly.</summary>
	public static void ProtocolFailure(this SessionLogger logger,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception, LlmModel model, DetectedProtocol protocol)
	{
		logger.ProtocolFailure(
			model.ConfigId,
			model.Config.Name,
			model.Endpoint,
			protocol.ToString(),
			failureType,
			httpStatusCode,
			errorMessage,
			responseBody,
			exception);
	}

	/// <summary>Logs a protocol failure using bare string identity fields. Used by ProtocolHelpers
	/// which only has the model name/endpoint/id as strings (no LlmModel instance).</summary>
	public static void ProtocolFailure(this SessionLogger logger,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception,
		string modelId, string modelName, string endpoint, string protocol)
	{
		logger.ProtocolFailure(
			modelId,
			modelName,
			endpoint,
			protocol,
			failureType,
			httpStatusCode,
			errorMessage,
			responseBody,
			exception);
	}

	/// <summary>Logs a protocol failure and returns a result, using bare string identity fields.
	/// Used by ProtocolHelpers which only has the model name/endpoint/id as strings.</summary>
	public static ProtocolResult ProtocolFailure(this SessionLogger logger, ProtocolResult result,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception,
		string modelId, string modelName, string endpoint, string protocol)
	{
		logger.ProtocolFailure(
			modelId,
			modelName,
			endpoint,
			protocol,
			failureType,
			httpStatusCode,
			errorMessage,
			responseBody,
			exception);
		return result;
	}

	// ---- FallbackTransition / SessionFailure helpers ----

	/// <summary>Logs a fallback transition. Takes the source and target services so the caller
	/// doesn't have to pluck ConfigId/Name/Endpoint from each.</summary>
	public static void FallbackTransition(this SessionLogger logger,
		LlmService from, LlmService to, string reason, int failedRetries)
	{
		logger.FallbackTransition(
			from.Model.ConfigId,
			from.Model.Config.Name,
			to.Model.ConfigId,
			to.Model.Config.Name,
			reason,
			failedRetries);
	}

	/// <summary>Logs a session failure. Takes the service so the caller doesn't have to pluck
	/// model identity and role model list.</summary>
	public static void SessionFailure(this SessionLogger logger,
		Session session, LlmService service, string finalError, int totalModelsTried)
	{
		logger.SessionFailure(
			session.Id,
			service.Model.ConfigId,
			service.Model.Config.Name,
			service.Model.Endpoint,
			service.Model.ConfigId,
			finalError,
			totalModelsTried);
	}
}