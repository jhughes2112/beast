using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Agent.Services;


// The wire protocol spoken by an endpoint — detected once per endpoint and cached in LlmRegistry.
public enum DetectedProtocol
{
	Unknown,
	Anthropic,
	Responses,
	ChatCompletions
}

// Every LLMService has a single ProtocolProxy that abstracts which protocol it speaks and makes sure that the correct one gets called.
// The lifetime of a ProtocolProxy is tied to the LLMService it is created for.
// Protocol is inferred from the endpoint URL path (/messages → Anthropic, /chat/completions → ChatCompletions, /responses → Responses).
// This class also routes calls to the appropriate protocol implementation and injects the model's
// headers and body extras onto the outgoing request.
//
// Extras and headers are replicated verbatim — no key interpretation. The model's "extras" entries
// are merged as top-level JSON body fields (strings, arrays, objects, numbers, booleans) and its
// "headers" entries become HTTP request headers. To steer OpenRouter routing, declare a "provider"
// object directly in extras; we no longer translate any or_* shorthand. Null and empty-string values
// are skipped so the settings file can carry self-documenting placeholder keys.
public class ProtocolProxy
{
	// Sentinel forcedToolName meaning "the model must call some tool this turn, but may pick which."
	// Distinct from a real tool name (which forces that exact tool) and from null (free choice).
	// Used by the subagent loop to require the model to actually do work with tools.
	public const string AnyTool = "__any_tool__";

	private const string OpenRouterReferer = "https://mooncast.productions";
	private const string OpenRouterTitle = "Beast";
	private const string OpenRouterCategories = "cli-agent";

	private LlmModel _model;

	// The detected protocol is cached after the first successful probe.
	// _model.Endpoint may be rewritten once if the localhost fallback fires.
	private DetectedProtocol _detected;

	// Exactly one of these is non-null once the first turn executes.
	private ProtocolChatCompletions? _protocolChatCompletions;
	private ProtocolResponses?       _protocolResponses;
	private ProtocolAnthropic?       _protocolAnthropic;

	public ProtocolProxy(LlmModel model)
	{
		_model = model;
		_detected = DetectedProtocol.Unknown;
	}

	// Create with a known protocol — skips the per-turn probe in ExecuteAsync.
	// Always prefer this constructor; use the no-protocol overload only as a fallback.
	public ProtocolProxy(LlmModel model, DetectedProtocol detected)
	{
		_model = model;
		_detected = detected;
	}

	// Infers the protocol from the URL route suffix.
	private static DetectedProtocol DetectProtocolFromUrl(string endpoint)
	{
		if (endpoint.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
			return DetectedProtocol.Anthropic;
		if (endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
			return DetectedProtocol.ChatCompletions;
		if (endpoint.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
			return DetectedProtocol.Responses;
		return DetectedProtocol.Unknown;
	}

	// Plain client for reachability probes. A bare GET is enough to tell whether a server is listening.
	private static readonly HttpClient _probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

	// True if a server answers at the address. We send a deliberately wrong request — a bare GET — so
	// the server rejects it fast (e.g. 404); ANY HTTP response means a server is there. Only a
	// connection-level failure (refused, unreachable, timeout) counts as nothing listening.
	private static async Task<bool> CanReachEndpointAsync(string endpoint)
	{
		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, endpoint);
			using HttpResponseMessage response = await _probeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
			return true;
		}
		catch
		{
			return false;
		}
	}

	// Protocol comes from the URL route. If a server answers at the configured address, use it. If not
	// and the address is host.docker.internal, retry against localhost so a native/debugger run reaches
	// a server bound to localhost. Otherwise keep the configured address for the real call to surface.
	public static async Task<(DetectedProtocol detected, string effectiveEndpoint)> ProbeEndpointAsync(
		string endpoint, CancellationToken ct = default)
	{
		DetectedProtocol protocol = DetectProtocolFromUrl(endpoint);
		if (protocol == DetectedProtocol.Unknown)
			return (DetectedProtocol.Unknown, endpoint);

		if (await CanReachEndpointAsync(endpoint))
			return (protocol, endpoint);

		if (endpoint.Contains("host.docker.internal", StringComparison.OrdinalIgnoreCase))
		{
			string fallback = endpoint.Replace("host.docker.internal", "localhost", StringComparison.OrdinalIgnoreCase);
			if (await CanReachEndpointAsync(fallback))
				return (protocol, fallback);
		}

		return (protocol, endpoint);
	}

	// Resets detection and discards the protocol instance so the next ExecuteAsync re-probes
	// and rehydrates from canonical. Called by ListenerBundle.InvalidateProtocol().
	public void Invalidate()
	{
		_detected = DetectedProtocol.Unknown;
		_protocolChatCompletions = null;
		_protocolResponses = null;
		_protocolAnthropic = null;
	}

	// Fan-out methods called by ListenerBundle for external events (user input, tool results,
	// replayed turns). Routes to whichever protocol instance is currently active.
	public void OnSystemMessage(string text)
	{
		_protocolChatCompletions?.OnSystemMessage(text);
		_protocolResponses?.OnSystemMessage(text);
		_protocolAnthropic?.OnSystemMessage(text);
	}
	public void OnUserMessage(string text)
	{
		_protocolChatCompletions?.OnUserMessage(text);
		_protocolResponses?.OnUserMessage(text);
		_protocolAnthropic?.OnUserMessage(text);
	}

	public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
	{
		_protocolChatCompletions?.OnAssistantTurn(text, thinking, toolCalls);
		_protocolResponses?.OnAssistantTurn(text, thinking, toolCalls);
		_protocolAnthropic?.OnAssistantTurn(text, thinking, toolCalls);
	}

	public void OnToolResult(ToolResult result)
	{
		_protocolChatCompletions?.OnToolResult(result);
		_protocolResponses?.OnToolResult(result);
		_protocolAnthropic?.OnToolResult(result);
	}

	public async Task<ProtocolResult> ExecuteAsync(ListenerBundle bundle, List<ToolDefinition> tools, string? forcedToolName, int? maxCompletionTokens, 
LiveUsageProgress onProgress, ITransportServer transport, string sessionId, QueryLogger? queryLogger, CancellationToken cancellationToken)
	{
		bundle.SetActiveProxy(this);

		(Dictionary<string, string> headers, Dictionary<string, JsonNode?> payload) = BuildExtras(_model.Extras, _model.Headers);
		string endpoint = _model.Endpoint;

		if (_detected == DetectedProtocol.Unknown)
		{
			(DetectedProtocol probed, string effectiveEndpoint) = await ProbeEndpointAsync(endpoint, cancellationToken);
			_detected = probed;
			if (effectiveEndpoint != endpoint)
				_model = new LlmModel(_model.ConfigId, effectiveEndpoint, _model.ApiKey, _model.Extras, _model.Headers, _model.Config);
		}

		IReadOnlyList<CanonicalMessage> canonical = bundle.Canonical.Messages;

		if (_detected == DetectedProtocol.Anthropic)
			return await ExecuteWithLoggingAsync(EnsureProtocolAnthropic(canonical), _model, bundle, tools, forcedToolName, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);

		if (_detected == DetectedProtocol.Responses)
			return await ExecuteWithLoggingAsync(EnsureProtocolResponses(canonical), _model, bundle, tools, forcedToolName, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);

		if (_detected == DetectedProtocol.ChatCompletions)
			return await ExecuteWithLoggingAsync(EnsureProtocolChatCompletions(canonical), _model, bundle, tools, forcedToolName, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);

		Log.ProtocolFailure(
			modelId: _model.ConfigId,
			modelName: _model.Config.Name,
			endpoint: _model.Endpoint,
			protocol: _detected.ToString(),
			failureType: "UnknownProtocol",
			httpStatusCode: null,
			errorMessage: $"Endpoint speaks no recognized protocol: {endpoint}");
		return ProtocolResult.Failed($"Endpoint speaks no recognized protocol: {endpoint}");
	}

	private async Task<ProtocolResult> ExecuteWithLoggingAsync(IProtocol protocol, LlmModel model, ListenerBundle bundle, List<ToolDefinition> tools, string? forcedToolName, int? maxCompletionTokens, Dictionary<string, string> headers, Dictionary<string, JsonNode?> payload, LiveUsageProgress onProgress, ITransportServer transport, string sessionId, QueryLogger? queryLogger, CancellationToken cancellationToken)
	{
		try
		{
			return await protocol.ExecuteAsync(model, bundle, tools, forcedToolName, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);
		}
		catch (Exception ex)
		{
			Log.ProtocolFailure(
				modelId: model.ConfigId,
				modelName: model.Config.Name,
				endpoint: model.Endpoint,
				protocol: _detected.ToString(),
				failureType: "Exception",
				httpStatusCode: null,
				errorMessage: ex.Message,
				exception: ex);
			return ProtocolResult.Transient(ex.ToString(), null);
		}
			}

	internal ProtocolChatCompletions EnsureProtocolChatCompletions(IReadOnlyList<CanonicalMessage> canonical)
	{
		if (_protocolChatCompletions == null)
		{
			_protocolChatCompletions = new ProtocolChatCompletions();
			_protocolChatCompletions.Rehydrate(canonical);
			_protocolResponses = null;
			_protocolAnthropic = null;
		}
		return _protocolChatCompletions;
	}

	internal ProtocolResponses EnsureProtocolResponses(IReadOnlyList<CanonicalMessage> canonical)
	{
		if (_protocolResponses == null)
		{
			_protocolResponses = new ProtocolResponses();
			_protocolResponses.Rehydrate(canonical);
			_protocolChatCompletions = null;
			_protocolAnthropic = null;
		}
		return _protocolResponses;
	}

	internal ProtocolAnthropic EnsureProtocolAnthropic(IReadOnlyList<CanonicalMessage> canonical)
	{
		if (_protocolAnthropic == null)
		{
			_protocolAnthropic = new ProtocolAnthropic();
			_protocolAnthropic.Rehydrate(canonical);
			_protocolChatCompletions = null;
			_protocolResponses = null;
		}
		return _protocolAnthropic;
	}

	// Returns the currently detected protocol for logging purposes.
	public DetectedProtocol GetDetectedProtocol()
	{
		return _detected;
	}

	// The protocol an endpoint speaks, resolved once by probing and then cached.
	// Returns true if a JsonNode represents an "empty" extra value that should be skipped.
	// Null nodes and empty-string JsonValues are skipped so settings files can contain
	// self-documenting placeholder keys. Non-string nodes (arrays, objects, numbers) are
	// never skipped — they are always intentional.
	private static bool IsEmptyExtra(JsonNode? node) =>
		node is null ||
		(node is JsonValue jv && jv.TryGetValue<string>(out string? s) && string.IsNullOrEmpty(s));

	// Builds the extra-headers and extra-payload dictionaries from the model's headers and extras.
	// Both are replicated verbatim with no key interpretation: each extras entry's properties are
	// merged as top-level body fields, each headers entry's properties become HTTP headers. Entries
	// apply in order so later keys win on collision. Null and empty-string values are skipped.
	public static (Dictionary<string, string> headers, Dictionary<string, JsonNode?> payload) BuildExtras(
		List<JsonObject> extras, List<JsonObject> headerObjects)
	{
		Dictionary<string, string> headers = new();
		Dictionary<string, JsonNode?> payload = new();

		// Always inject OpenRouter identification headers — harmless on non-OpenRouter endpoints.
		// The model's own headers can override these.
		headers["HTTP-Referer"] = OpenRouterReferer;
		headers["X-Title"] = OpenRouterTitle;
		headers["X-OpenRouter-Title"] = OpenRouterTitle;
		headers["X-OpenRouter-Categories"] = OpenRouterCategories;

		foreach (JsonObject headerObject in headerObjects)
		{
			foreach ((string name, JsonNode? value) in headerObject)
			{
				if (IsEmptyExtra(value))  // empty/null values are ignored, so the settings file can be self-documenting
					continue;

				headers[name] = value!.ToString();
			}
		}

		foreach (JsonObject extraObject in extras)
		{
			foreach ((string name, JsonNode? value) in extraObject)
			{
				if (IsEmptyExtra(value))
					continue;

				// DeepClone: the node belongs to the extras tree and gets parented into the
				// request body downstream; re-using it across turns would throw.
				payload[name] = value!.DeepClone();
			}
		}

		return (headers, payload);
	}
}