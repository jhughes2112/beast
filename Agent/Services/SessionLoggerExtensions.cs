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
			modelId: model.ConfigId,
			modelName: model.Config.Name,
			endpoint: model.Endpoint,
			protocol: handler.GetDetectedProtocol().ToString(),
			failureType: failureType,
			httpStatusCode: httpStatusCode,
			errorMessage: errorMessage,
			retryCount: retryCount,
			maxRetries: maxRetries,
			retryAfter: retryAfter,
			willFallback: willFallback,
			exception: exception,
			stackTrace: stackTrace);
		return result;
	}

	/// <summary>Logs a model failure (no result to return) using a LlmModel + handler.</summary>
	public static void ModelFailure(this SessionLogger logger,
		string failureType, int? httpStatusCode, string errorMessage,
		int retryCount, int maxRetries, DateTimeOffset? retryAfter, bool willFallback,
		LlmModel model, ProtocolProxy handler, Exception? exception = null, string? stackTrace = null)
	{
		logger.ModelFailure(
			modelId: model.ConfigId,
			modelName: model.Config.Name,
			endpoint: model.Endpoint,
			protocol: handler.GetDetectedProtocol().ToString(),
			failureType: failureType,
			httpStatusCode: httpStatusCode,
			errorMessage: errorMessage,
			retryCount: retryCount,
			maxRetries: maxRetries,
			retryAfter: retryAfter,
			willFallback: willFallback,
			exception: exception,
			stackTrace: stackTrace);
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
			modelId: model.ConfigId,
			modelName: model.Config.Name,
			endpoint: model.Endpoint,
			protocol: protocol.ToString(),
			failureType: failureType,
			httpStatusCode: httpStatusCode,
			errorMessage: errorMessage,
			retryCount: retryCount,
			maxRetries: maxRetries,
			retryAfter: retryAfter,
			willFallback: willFallback,
			exception: exception,
			stackTrace: stackTrace);
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
			modelId: model.ConfigId,
			modelName: model.Config.Name,
			endpoint: model.Endpoint,
			protocol: handler.GetDetectedProtocol().ToString(),
			failureType: failureType,
			httpStatusCode: httpStatusCode,
			errorMessage: errorMessage,
			responseBody: responseBody,
			exception: exception);
		return result;
	}

	/// <summary>Logs a protocol failure and returns the supplied result. Uses a model + protocol directly.</summary>
	public static ProtocolResult ProtocolFailure(this SessionLogger logger, ProtocolResult result,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception, LlmModel model, DetectedProtocol protocol)
	{
		logger.ProtocolFailure(
			modelId: model.ConfigId,
			modelName: model.Config.Name,
			endpoint: model.Endpoint,
			protocol: protocol.ToString(),
			failureType: failureType,
			httpStatusCode: httpStatusCode,
			errorMessage: errorMessage,
			responseBody: responseBody,
			exception: exception);
		return result;
	}

	/// <summary>Logs a protocol failure (no result to return) using a LlmModel + handler.</summary>
	public static void ProtocolFailure(this SessionLogger logger,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception, LlmModel model, ProtocolProxy handler)
	{
		logger.ProtocolFailure(
			modelId: model.ConfigId,
			modelName: model.Config.Name,
			endpoint: model.Endpoint,
			protocol: handler.GetDetectedProtocol().ToString(),
			failureType: failureType,
			httpStatusCode: httpStatusCode,
			errorMessage: errorMessage,
			responseBody: responseBody,
			exception: exception);
	}

	/// <summary>Logs a protocol failure (no result to return) using a model + protocol directly.</summary>
	public static void ProtocolFailure(this SessionLogger logger,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception, LlmModel model, DetectedProtocol protocol)
	{
		logger.ProtocolFailure(
			modelId: model.ConfigId,
			modelName: model.Config.Name,
			endpoint: model.Endpoint,
			protocol: protocol.ToString(),
			failureType: failureType,
			httpStatusCode: httpStatusCode,
			errorMessage: errorMessage,
			responseBody: responseBody,
			exception: exception);
	}

	/// <summary>Logs a protocol failure using bare string identity fields. Used by ProtocolHelpers
	/// which only has the model name/endpoint/id as strings (no LlmModel instance).</summary>
	public static void ProtocolFailure(this SessionLogger logger,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception,
		string modelId, string modelName, string endpoint, string protocol)
	{
		logger.ProtocolFailure(
			modelId: modelId,
			modelName: modelName,
			endpoint: endpoint,
			protocol: protocol,
			failureType: failureType,
			httpStatusCode: httpStatusCode,
			errorMessage: errorMessage,
			responseBody: responseBody,
			exception: exception);
	}

	/// <summary>Logs a protocol failure and returns a result, using bare string identity fields.
	/// Used by ProtocolHelpers which only has the model name/endpoint/id as strings.</summary>
	public static ProtocolResult ProtocolFailure(this SessionLogger logger, ProtocolResult result,
		string failureType, int? httpStatusCode, string errorMessage,
		string? responseBody, Exception? exception,
		string modelId, string modelName, string endpoint, string protocol)
	{
		logger.ProtocolFailure(
			modelId: modelId,
			modelName: modelName,
			endpoint: endpoint,
			protocol: protocol,
			failureType: failureType,
			httpStatusCode: httpStatusCode,
			errorMessage: errorMessage,
			responseBody: responseBody,
			exception: exception);
		return result;
	}

	// ---- FallbackTransition / SessionFailure helpers ----

	/// <summary>Logs a fallback transition. Takes the source and target services so the caller
	/// doesn't have to pluck ConfigId/Name/Endpoint from each.</summary>
	public static void FallbackTransition(this SessionLogger logger,
		LlmService from, LlmService to, string reason, int failedRetries)
	{
		logger.FallbackTransition(
			fromModelId: from.Model.ConfigId,
			fromModelName: from.Model.Config.Name,
			toModelId: to.Model.ConfigId,
			toModelName: to.Model.Config.Name,
			reason: reason,
			failedRetries: failedRetries);
	}

	/// <summary>Logs a session failure. Takes the service so the caller doesn't have to pluck
	/// model identity and role model list.</summary>
	public static void SessionFailure(this SessionLogger logger,
		Session session, LlmService service, string finalError, int totalModelsTried)
	{
		logger.SessionFailure(
			sessionId: session.Id,
			modelId: service.Model.ConfigId,
			modelName: service.Model.Config.Name,
			endpoint: service.Model.Endpoint,
			protocol: service.Model.ConfigId, // kept verbatim from the original call site
			finalError: finalError,
			totalModelsTried: totalModelsTried);
	}
}
