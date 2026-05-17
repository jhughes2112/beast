using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// The whole purpose of this class is to 1) detect the protocol by probing the endpoint for Anthropic, ChatCompletions, or Responses,
// and then 2) route the call to the appropriate protocol implementation, 3) inject headers and payload fields based on the Extras dictionary.
// They all work the same way, so this saves a lot of boilerplate and simplifies the protocol handling.
//
// Extras key conventions:
//   header_<name>  — added verbatim as an HTTP request header
//   or_*           — OpenRouter-specific (see below); injected as structured fields
//   Anything else  — added verbatim as a top-level JSON payload field (string value)
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
//   or_models                      — comma-separated fallback model names
public class ProtocolProxy
{
    private const string OpenRouterReferer = "https://mooncast.productions";
    private const string OpenRouterTitle = "Beast";
    private const string OpenRouterCategories = "cli-agent";

    private readonly LlmModel _model;
    private IProtocol? _protocol;

    public ProtocolProxy(LlmModel model)
    {
        _model = model;
    }

    public async Task<ProviderCallResult> ExecuteAsync(List<ConversationMessage> messages, List<ToolDefinition> tools, int maxCompletionTokens, IStreamingMessage? stream, CancellationToken cancellationToken)
    {
        if (_protocol == null)
        {
            _protocol = await DetectProtocolAsync(_model);
        }

        (Dictionary<string, string> headers, Dictionary<string, JsonNode?> payload) = BuildExtras(_model);
        return await _protocol.ExecuteAsync(_model, messages, tools, maxCompletionTokens, headers, payload, stream, cancellationToken);
    }

    // Probes the endpoint to determine which protocol it speaks.
    // Order: Anthropic (unique error shape) -> Responses (/responses path) -> ChatCompletions (fallback).
    private static async Task<IProtocol> DetectProtocolAsync(LlmModel model)
    {
        if (model.Endpoint.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            return new ProtocolAnthropic();
        }

        ProbeResult anthropic = await ProtocolAnthropic.ProbeAsync(model.ApiKey, model.Endpoint);
        if (anthropic.Outcome == ProbeOutcome.Supported)
        {
            return new ProtocolAnthropic();
        }

        ProbeResult responses = await ProtocolResponses.ProbeAsync(model.ApiKey, model.Endpoint);
        if (responses.Outcome == ProbeOutcome.Supported)
        {
            return new ProtocolResponses();
        }

        return new ProtocolChatCompletions();
    }

    // Builds the extra-headers and extra-payload dictionaries from Extras.
    // OpenRouter headers are always injected; or_* extras populate the provider routing block.
    // header_* keys become HTTP headers; everything else goes directly into the payload.
    private static (Dictionary<string, string> headers, Dictionary<string, JsonNode?> payload) BuildExtras(LlmModel model)
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
        if (model.Endpoint.Contains("openrouter.ai", System.StringComparison.OrdinalIgnoreCase))
        {
            orProvider["allow_fallbacks"] = JsonValue.Create(true);
            hasOrProvider = true;
        }

        foreach (KeyValuePair<string, string> kv in model.Extras)
        {
            string key = kv.Key;
            string value = kv.Value;

            if (string.IsNullOrEmpty(value))  // empty values are ignored, so the settings file can be self-documenting
                continue;

            if (key.StartsWith("header_"))
            {
                headers[key.Substring("header_".Length)] = value;
            }
            else if (key == "or_user")
            {
                payload["user"] = JsonValue.Create(value);
            }
            else if (key == "or_models")
            {
                JsonArray arr = BuildCsvArray(value);
                if (arr.Count > 0) payload["models"] = arr;
            }
            else if (key.StartsWith("or_provider_"))
            {
                string field = key.Substring("or_provider_".Length);
                switch (field)
                {
                    case "order":
                        JsonArray order = BuildCsvArray(value);
                        if (order.Count > 0) { orProvider["order"] = order; hasOrProvider = true; }
                        break;
                    case "only":
                        JsonArray only = BuildCsvArray(value);
                        if (only.Count > 0) { orProvider["only"] = only; hasOrProvider = true; }
                        break;
                    case "ignore":
                        JsonArray ignore = BuildCsvArray(value);
                        if (ignore.Count > 0) { orProvider["ignore"] = ignore; hasOrProvider = true; }
                        break;
                    case "sort":
                        orProvider["sort"] = JsonValue.Create(value); hasOrProvider = true;
                        break;
                    case "allow_fallbacks":
                        orProvider["allow_fallbacks"] = JsonValue.Create(value != "false"); hasOrProvider = true;
                        break;
                    case "require_parameters":
                        if (value == "true") { orProvider["require_parameters"] = JsonValue.Create(true); hasOrProvider = true; }
                        break;
                    case "data_collection":
                        orProvider["data_collection"] = JsonValue.Create(value); hasOrProvider = true;
                        break;
                    case "zdr":
                        if (value == "true") { orProvider["zdr"] = JsonValue.Create(true); hasOrProvider = true; }
                        break;
                }
            }
            else
            {
                payload[key] = JsonValue.Create(value);
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