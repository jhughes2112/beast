using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


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
// This class also routes calls to the appropriate protocol implementation and injects headers/payload fields from the Extras dictionary.
//
// Extras key conventions:
//   header_<name>  — added verbatim as an HTTP request header
//   or_*           — OpenRouter-specific (see below); injected as structured fields
//   Anything else  — added verbatim as a top-level JSON payload field (string, array, object, etc.)
//
// OpenRouter extras (or_*):
//   or_provider_order              — comma-separated provider names, e.g. "Anthropic,OpenAI"
//   or_provider_sort               — "price", "throughput", or "latency"
//   or_provider_allow_fallbacks    — "true" or "false" (default: true when on openrouter)
//   or_provider_require_parameters — "true" to only route to providers supporting all params
//   or_provider_data_collection    — "deny" to exclude providers that store prompts
//   or_provider_ignore             — comma-separated provider names to exclude
//   or_provider_only               — comma-separated provider names; restricts routing strictly
//   or_provider_zdr                — "true" to restrict to zero-data-retention endpoints
//   or_user                        — stable identifier for your end-user (abuse detection)
//   or_models                      — comma-separated fallback model names (string) or JSON array of model names
public class ProtocolProxy
{
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

    // Resolves the host from a URL within a tight timeout; returns false if unreachable.
    private static async Task<bool> CanResolveHostAsync(string endpoint)
    {
        try
        {
            Uri uri = new Uri(endpoint);
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.Host, cts.Token);
            return addresses.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // Infers the protocol from the URL route, then resolves the effective endpoint URL.
    // If host.docker.internal fails to resolve, rewrites to localhost (on-host fallback).
    // Returns Unknown if the URL route is unrecognized or the host cannot be resolved.
    public static async Task<(DetectedProtocol detected, string effectiveEndpoint)> ProbeEndpointAsync(
        string endpoint, CancellationToken ct = default)
    {
        DetectedProtocol protocol = DetectProtocolFromUrl(endpoint);
        if (protocol == DetectedProtocol.Unknown)
            return (DetectedProtocol.Unknown, endpoint);

        if (await CanResolveHostAsync(endpoint))
            return (protocol, endpoint);

        if (!endpoint.Contains("host.docker.internal", StringComparison.OrdinalIgnoreCase))
            return (DetectedProtocol.Unknown, endpoint);

        string fallback = endpoint.Replace("host.docker.internal", "localhost", StringComparison.OrdinalIgnoreCase);
        if (await CanResolveHostAsync(fallback))
            return (protocol, fallback);

        return (DetectedProtocol.Unknown, endpoint);
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

    public async Task<ProtocolResult> ExecuteAsync(ListenerBundle bundle, List<ToolDefinition> tools, string? forcedToolName, int? maxCompletionTokens, LiveUsageProgress onProgress, ITransportServer transport, string sessionId, QueryLogger? queryLogger, CancellationToken cancellationToken)
    {
        bundle.SetActiveProxy(this);

        (Dictionary<string, string> headers, Dictionary<string, JsonNode?> payload) = BuildExtras(_model.Extras, _model.Endpoint);
        string endpoint = _model.Endpoint;

        if (_detected == DetectedProtocol.Unknown)
        {
            (DetectedProtocol probed, string effectiveEndpoint) = await ProbeEndpointAsync(endpoint, cancellationToken);
            _detected = probed;
            if (effectiveEndpoint != endpoint)
                _model = new LlmModel(_model.ConfigId, effectiveEndpoint, _model.ApiKey, _model.Extras, _model.Config);
        }

        IReadOnlyList<CanonicalMessage> canonical = bundle.Canonical.Messages;

        if (_detected == DetectedProtocol.Anthropic)
            return await EnsureProtocolAnthropic(canonical).ExecuteAsync(_model, bundle, tools, forcedToolName, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);

        if (_detected == DetectedProtocol.Responses)
            return await EnsureProtocolResponses(canonical).ExecuteAsync(_model, bundle, tools, forcedToolName, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);

        if (_detected == DetectedProtocol.ChatCompletions)
            return await EnsureProtocolChatCompletions(canonical).ExecuteAsync(_model, bundle, tools, forcedToolName, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);

        return ProtocolResult.Failed($"Endpoint speaks no recognized protocol: {endpoint}");
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

    // The protocol an endpoint speaks, resolved once by probing and then cached.
    // Returns true if a JsonNode represents an "empty" extra value that should be skipped.
    // Null nodes and empty-string JsonValues are skipped so settings files can contain
    // self-documenting placeholder keys. Non-string nodes (arrays, objects, numbers) are
    // never skipped — they are always intentional.
    private static bool IsEmptyExtra(JsonNode? node) =>
        node is null ||
        (node is JsonValue jv && jv.TryGetValue<string>(out var s) && string.IsNullOrEmpty(s));

    // Builds the extra-headers and extra-payload dictionaries from Extras.
    // OpenRouter headers are always injected; or_* extras populate the provider routing block.
    // header_* keys become HTTP headers; everything else goes directly into the payload
    // as a JsonNode, preserving structured values (arrays, objects) verbatim.
    public static (Dictionary<string, string> headers, Dictionary<string, JsonNode?> payload) BuildExtras(
        Dictionary<string, JsonNode?> extras, string endpoint)
    {
        Dictionary<string, string> headers = new();
        Dictionary<string, JsonNode?> payload = new();

        // Always inject OpenRouter identification headers — harmless on non-OpenRouter endpoints.
        headers["HTTP-Referer"] = OpenRouterReferer;
        headers["X-Title"] = OpenRouterTitle;
        headers["X-OpenRouter-Title"] = OpenRouterTitle;
        headers["X-OpenRouter-Categories"] = OpenRouterCategories;

        JsonObject orProvider = new JsonObject();
        bool hasOrProvider = false;

        // Default allow_fallbacks to true when talking to OpenRouter.
        if (endpoint.Contains("openrouter.ai", System.StringComparison.OrdinalIgnoreCase))
        {
            orProvider["allow_fallbacks"] = JsonValue.Create(true);
            hasOrProvider = true;
        }

        foreach (KeyValuePair<string, JsonNode?> kv in extras)
        {
            string key = kv.Key;
            JsonNode? value = kv.Value;

            if (IsEmptyExtra(value))  // empty/null values are ignored, so the settings file can be self-documenting
                continue;

            if (key.StartsWith("header_"))
            {
                headers[key.Substring("header_".Length)] = value!.ToString();
            }
            else if (key == "or_user")
            {
                payload["user"] = value!.DeepClone();
            }
            else if (key == "or_models")
            {
                if (value!.GetValueKind() == JsonValueKind.String)
                {
                    string str = value.ToString();
                    if (!string.IsNullOrEmpty(str))
                    {
                        JsonArray arr = BuildCsvArray(str);
                        if (arr.Count > 0) payload["models"] = arr;
                    }
                }
                else if (value.GetValueKind() == JsonValueKind.Array)
                {
                    // DeepClone: the node belongs to the extras tree and gets parented into the
                    // request body downstream; re-using it across turns would throw.
                    payload["models"] = (JsonArray)value.DeepClone();
                }
            }
            else if (key.StartsWith("or_provider_"))
            {
                string field = key.Substring("or_provider_".Length);
                string str = value!.ToString();
                switch (field)
                {
                    case "order":
                        JsonArray order = BuildCsvArray(str);
                        if (order.Count > 0) { orProvider["order"] = order; hasOrProvider = true; }
                        break;
                    case "only":
                        JsonArray only = BuildCsvArray(str);
                        if (only.Count > 0) { orProvider["only"] = only; hasOrProvider = true; }
                        break;
                    case "ignore":
                        JsonArray ignore = BuildCsvArray(str);
                        if (ignore.Count > 0) { orProvider["ignore"] = ignore; hasOrProvider = true; }
                        break;
                    case "sort":
                        orProvider["sort"] = JsonValue.Create(str); hasOrProvider = true;
                        break;
                    case "allow_fallbacks":
                        orProvider["allow_fallbacks"] = JsonValue.Create(str != "false"); hasOrProvider = true;
                        break;
                    case "require_parameters":
                        if (str == "true") { orProvider["require_parameters"] = JsonValue.Create(true); hasOrProvider = true; }
                        break;
                    case "data_collection":
                        orProvider["data_collection"] = JsonValue.Create(str); hasOrProvider = true;
                        break;
                    case "zdr":
                        if (str == "true") { orProvider["zdr"] = JsonValue.Create(true); hasOrProvider = true; }
                        break;
                }
            }
            else
            {
                // Generic extra — pass the JsonNode verbatim into the payload.
                // This supports strings, arrays, objects, numbers, booleans, etc.
                payload[key] = value!.DeepClone();
            }
        }

        if (hasOrProvider)
        {
            payload["provider"] = orProvider;
        }

        return (headers, payload);
    }

    private static JsonArray BuildCsvArray(string csv)
    {
        JsonArray arr = new JsonArray();
        foreach (string part in csv.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) arr.Add(JsonValue.Create(trimmed));
        }
        return arr;
    }
}
