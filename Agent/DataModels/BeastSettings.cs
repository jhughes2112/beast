using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;


// Mirrors the Pi Agent models.json format for LLM provider/model configuration.
public class BeastSettings
{
    [JsonPropertyName("providers")]
    public List<ProviderConfig> Providers { get; set; } = new();

    [JsonPropertyName("webSearch")]
    public WebSearchConfig? WebSearch { get; set; }

    [JsonPropertyName("lastSessionId")]
    public string? LastSessionId { get; set; }

    [JsonPropertyName("compactionPrompt")]
    public string CompactionPrompt { get; set; } = "Your task is to create a detailed summary of the conversation so far, paying close attention to the user's explicit requests and your previous actions. This summary should be thorough in capturing technical details, code patterns, and architectural decisions that would be essential for continuing development work without losing context.";

    [JsonPropertyName("continueMessage")]
    public string ContinueMessage { get; set; } = "Are you done? If finished, respond accordingly.";
}

public class ProviderConfig
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("models")]
    public List<ModelConfig> Models { get; set; } = new();

    // Provider-specific key/value pairs. Used to pass vendor-specific options
    // (e.g. routing preferences for OpenRouter) without changing the schema.
    // Values are JsonNode so structured payloads (arrays, objects) are supported
    // alongside plain strings. Null values and empty strings are skipped.
    [JsonPropertyName("extras")]
    public Dictionary<string, JsonNode?> Extras { get; set; } = new();
}

public class ModelConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("contextWindow")]
    public int ContextWindow { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; set; }

    [JsonPropertyName("cost")]
    public CostConfig Cost { get; set; } = new();

    // Per-model overrides. header_* keys become HTTP headers; everything else is injected
    // as top-level JSON payload fields, overriding the typed fields above when names match.
    // Values are JsonNode so structured payloads (arrays, objects) are supported
    // alongside plain strings. Null values and empty strings are skipped.
    [JsonPropertyName("extras")]
    public Dictionary<string, JsonNode?> Extras { get; set; } = new();
}

public class CostConfig
{
    [JsonPropertyName("input")]
    public decimal Input { get; set; }

    [JsonPropertyName("output")]
    public decimal Output { get; set; }

    [JsonPropertyName("cacheRead")]
    public decimal CacheRead { get; set; }

    [JsonPropertyName("cacheWrite")]
    public decimal CacheWrite { get; set; }
}

// Top-level web search config; contains one entry per supported provider.
public class WebSearchConfig
{
    [JsonPropertyName("openrouter")]
    public OpenrouterSearchConfig? Openrouter { get; set; }
}

// Configuration for web search via the OpenRouter plugin API.
// Extras are injected as top-level JSON payload fields on the chat completion request,
// so structured values like the plugins array can be declared in settings.
public class OpenrouterSearchConfig
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    // Model used to invoke the web search plugin.
    [JsonPropertyName("model")]
    public string Model { get; set; } = "openai/gpt-4o-mini";

    // Extra key/value pairs injected into the chat completion payload.
    // header_* keys become HTTP headers; or_* keys control OpenRouter routing;
    // everything else is merged verbatim as a top-level JSON field.
    // Values are JsonNode so structured payloads (arrays, objects) are supported.
    [JsonPropertyName("extras")]
    public Dictionary<string, JsonNode?> Extras { get; set; } = new();

    public LlmModel BuildModel()
    {
        return new LlmModel(
            configId: "websearch",
            endpoint: Endpoint,
            apiKey: ApiKey,
            extras: Extras,
            config: new ModelConfig { Id = Model, Name = Model });
    }
}