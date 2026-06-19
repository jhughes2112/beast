using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;


// Mirrors the Pi Agent models.json format for LLM provider/model configuration.
// Mirrors the Pi Agent models.json format for LLM provider/model configuration.
public class BeastSettings
{
    [JsonPropertyName("providers")]
    public List<ProviderConfig> Providers { get; set; } = new();

    [JsonPropertyName("webSearch")]
    public WebSearchConfig? WebSearch { get; set; }

    [JsonPropertyName("idleSoundFile")]
    public string IdleSoundFile { get; set; } = string.Empty;

    // Played when a subagent sub-session completes; empty means no sound.
    [JsonPropertyName("subagentSoundFile")]
    public string SubagentSoundFile { get; set; } = string.Empty;

    [JsonPropertyName("compactionReserveTokens")]
    public int CompactionReserveTokens { get; set; } = 0;
}

public class ProviderConfig
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("models")]
    public List<ModelConfig> Models { get; set; } = new();
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

    // Friendly reasoning/thinking level: none, minimal, low, medium, high, max (and common synonyms).
    // The word is translated to each provider's native control in code (Anthropic thinking budget,
    // OpenAI reasoning effort, etc.); the user never sees the underlying numbers. Empty means none.
    [JsonPropertyName("reasoningEffort")]
    public string ReasoningEffort { get; set; } = string.Empty;

    [JsonPropertyName("cost")]
    public CostConfig Cost { get; set; } = new();

    // Extra top-level fields merged verbatim into the outgoing request body. Each entry is a
    // JSON object whose properties are copied into the payload as-is (strings, arrays, objects,
    // numbers, booleans). Entries are applied in order; later keys win on collision. Null and
    // empty-string values are skipped so the settings file can carry self-documenting placeholders.
    [JsonPropertyName("extras")]
    [JsonConverter(typeof(JsonObjectListConverter))]
    public List<JsonObject> Extras { get; set; } = new();

    // Extra HTTP request headers. Each entry is a JSON object of header-name → value, copied
    // verbatim onto the request. Applied in order; later entries win. Empty values are skipped.
    [JsonPropertyName("headers")]
    [JsonConverter(typeof(JsonObjectListConverter))]
    public List<JsonObject> Headers { get; set; } = new();
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
// Extras are merged verbatim as top-level JSON payload fields on the chat completion request,
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

    // Context window and per-response output ceiling for the search model. The search now runs through
    // LlmService (see WebSearchOpenrouter/HelperSession), whose budget math needs a real window — a zero
    // window reads as "context full" before the first turn. Defaults are sane for any hosted search model.
    [JsonPropertyName("contextWindow")]
    public int ContextWindow { get; set; } = 128000;

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; set; } = 4096;

    // Extra top-level body fields merged verbatim into the chat completion payload.
    // Each entry is a JSON object copied as-is; later keys win on collision.
    [JsonPropertyName("extras")]
    [JsonConverter(typeof(JsonObjectListConverter))]
    public List<JsonObject> Extras { get; set; } = new();

    // Extra HTTP request headers, copied verbatim. Each entry is a name → value JSON object.
    [JsonPropertyName("headers")]
    [JsonConverter(typeof(JsonObjectListConverter))]
    public List<JsonObject> Headers { get; set; } = new();

    public LlmModel BuildModel()
    {
        return new LlmModel(
            configId: "websearch",
            endpoint: Endpoint,
            apiKey: ApiKey,
            extras: Extras,
            headers: Headers,
            config: new ModelConfig { Id = Model, Name = Model, ContextWindow = ContextWindow, MaxOutputTokens = MaxOutputTokens });
    }
}

// Reads extras/headers as a list of JSON objects, but also accepts a single object for
// convenience (it becomes a one-element list). This lets the settings file write the natural
// { "temperature": 0.7, "top_p": 0.95 } shape instead of requiring an outer array.
public class JsonObjectListConverter : JsonConverter<List<JsonObject>>
{
    public override List<JsonObject> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        List<JsonObject> result = new List<JsonObject>();
        JsonNode? node = JsonNode.Parse(ref reader);

        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonObject obj)
                    result.Add((JsonObject)obj.DeepClone());
            }
        }
        else if (node is JsonObject single)
        {
            result.Add((JsonObject)single.DeepClone());
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<JsonObject> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (JsonObject obj in value)
        {
            obj.WriteTo(writer, options);
        }
        writer.WriteEndArray();
    }
}