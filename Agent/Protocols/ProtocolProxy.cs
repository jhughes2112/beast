using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


public enum ProbeOutcome
{
    Supported,    // The endpoint speaks this protocol.
    NotSupported, // The endpoint returned a definitive 404 or wrong-shaped body — not this protocol.
    Unreachable   // The probe could not connect at all (network error, timeout).
}

public class ProbeResult
{
    public ProbeOutcome Outcome { get; }
    public string       Detail  { get; }

    private ProbeResult(ProbeOutcome outcome, string detail)
    {
        Outcome = outcome;
        Detail = detail;
    }

    public static ProbeResult Supported()                    => new ProbeResult(ProbeOutcome.Supported, "");
    public static ProbeResult NotSupported(string detail)    => new ProbeResult(ProbeOutcome.NotSupported, detail);
    public static ProbeResult Unreachable(string detail)     => new ProbeResult(ProbeOutcome.Unreachable, detail);
}

// So it is clear, Every LLMService has a single ProtocolProxy that abstracts which protocol it speaks and makes sure that the correct one gets called.
// The lifetime of a ProtocolProxy is tied to the LLMService it is created for.
// The whole purpose of this class is to 1) detect the protocol by probing the endpoint for Anthropic, ChatCompletions, or Responses,
// and then 2) route the call to the appropriate protocol implementation, 3) inject headers and payload fields based on the Extras dictionary.
// They all work the same way, so this saves a lot of boilerplate and simplifies the protocol handling.
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

    public void OnToolResult(string toolCallId, ToolResult result)
    {
        _protocolChatCompletions?.OnToolResult(toolCallId, result);
        _protocolResponses?.OnToolResult(toolCallId, result);
        _protocolAnthropic?.OnToolResult(toolCallId, result);
    }

    public void OnClear()
    {
        _protocolChatCompletions?.OnClear();
        _protocolResponses?.OnClear();
        _protocolAnthropic?.OnClear();
    }

    public async Task<ProtocolResult> ExecuteAsync(ListenerBundle bundle, List<ToolDefinition> tools, int? maxCompletionTokens, LiveUsageProgress onProgress, ITransportServer transport, string sessionId, QueryLogger? queryLogger, CancellationToken cancellationToken)
    {
        bundle.SetActiveProxy(this);

        (Dictionary<string, string> headers, Dictionary<string, JsonNode?> payload) = BuildExtras(_model.Extras, _model.Endpoint);
        string endpoint = _model.Endpoint;

        if (_detected == DetectedProtocol.Unknown)
        {
            ProbeResult anthropic = await ProtocolAnthropic.ProbeAsync(_model.ApiKey, endpoint);
            if (anthropic.Outcome == ProbeOutcome.Supported)
            {
                _detected = DetectedProtocol.Anthropic;
            }
            else
            {
                ProbeResult responses = await ProtocolResponses.ProbeAsync(_model.ApiKey, endpoint);
                if (responses.Outcome == ProbeOutcome.Supported)
                {
                    _detected = DetectedProtocol.Responses;
                }
                else
                {
                    ProbeResult chat = await ProtocolChatCompletions.ProbeAsync(_model.ApiKey, endpoint);
                    if (chat.Outcome == ProbeOutcome.Supported)
                    {
                        _detected = DetectedProtocol.ChatCompletions;
                    }
                    else if (endpoint.Contains("host.docker.internal", System.StringComparison.OrdinalIgnoreCase))
                    {
                        // Fallback for debug mode: retry with localhost instead of host.docker.internal
                        string fallbackEndpoint = endpoint.Replace("host.docker.internal", "localhost", System.StringComparison.OrdinalIgnoreCase);

                        ProbeResult anthropicFallback = await ProtocolAnthropic.ProbeAsync(_model.ApiKey, fallbackEndpoint);
                        if (anthropicFallback.Outcome == ProbeOutcome.Supported)
                        {
                            _detected = DetectedProtocol.Anthropic;
                            _model = new LlmModel(_model.ConfigId, fallbackEndpoint, _model.ApiKey, _model.Extras, _model.Config);
                        }
                        else
                        {
                            ProbeResult responsesFallback = await ProtocolResponses.ProbeAsync(_model.ApiKey, fallbackEndpoint);
                            if (responsesFallback.Outcome == ProbeOutcome.Supported)
                            {
                                _detected = DetectedProtocol.Responses;
                                _model = new LlmModel(_model.ConfigId, fallbackEndpoint, _model.ApiKey, _model.Extras, _model.Config);
                            }
                            else
                            {
                                ProbeResult chatFallback = await ProtocolChatCompletions.ProbeAsync(_model.ApiKey, fallbackEndpoint);
                                if (chatFallback.Outcome == ProbeOutcome.Supported)
                                {
                                    _detected = DetectedProtocol.ChatCompletions;
                                    _model = new LlmModel(_model.ConfigId, fallbackEndpoint, _model.ApiKey, _model.Extras, _model.Config);
                                }
                            }
                        }
                    }
                }
            }
        }

        IReadOnlyList<CanonicalMessage> canonical = bundle.Canonical.Messages;

        if (_detected == DetectedProtocol.Anthropic)
            return await EnsureProtocolAnthropic(canonical).ExecuteAsync(_model, bundle, tools, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);

        if (_detected == DetectedProtocol.Responses)
            return await EnsureProtocolResponses(canonical).ExecuteAsync(_model, bundle, tools, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);

        if (_detected == DetectedProtocol.ChatCompletions)
            return await EnsureProtocolChatCompletions(canonical).ExecuteAsync(_model, bundle, tools, maxCompletionTokens, headers, payload, onProgress, transport, sessionId, queryLogger, cancellationToken);

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
    private enum DetectedProtocol
    {
        Unknown,
        Anthropic,
        Responses,
        ChatCompletions
    }

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
                    payload["models"] = (JsonArray)value;
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
