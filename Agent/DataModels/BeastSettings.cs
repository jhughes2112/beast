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

	// Auto-configured endpoints: each carries only the endpoint, its key, and the models the user
	// explicitly enabled through /config. Everything discoverable from the endpoint's catalog
	// (context window, pricing, modalities) is NOT persisted here — it is re-discovered at every
	// load; a model entry carries a value only when discovery cannot determine it (the /config
	// picker asks at enable time) and that value then always wins over discovery.
	[JsonPropertyName("auto")]
	public List<AutoProviderConfig> Auto { get; set; } = new();

	[JsonPropertyName("tools")]
	public Dictionary<string, ToolConfig> Tools { get; set; } = new();

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

// One auto-configured endpoint. Models listed here are the CONFIGURED set — disabling a model in
// /config keeps its entry (enabled=false) so its overrides survive, and also drops it from every
// role ordering. A model that vanishes from the endpoint's catalog is temporarily disabled in
// memory for that run; its entry is never rewritten or flagged on disk, because the user's intent
// (temporary outage vs. permanent removal) cannot be known here.
public class AutoProviderConfig
{
	[JsonPropertyName("baseUrl")]
	public string BaseUrl { get; set; } = string.Empty;

	[JsonPropertyName("apiKey")]
	public string ApiKey { get; set; } = string.Empty;

	[JsonPropertyName("models")]
	public List<AutoModelConfig> Models { get; set; } = new();
}

// One configured model under an auto endpoint. Every field except Id/Enabled is a sparse
// override: the zero value (0, null, empty) means "use what discovery reports"; anything else was
// either supplied by the user in the /config details prompt or is a legitimate unknown the user
// chose to fill. Disabling a model keeps its entry (and its overrides) with enabled=false —
// disabling is not forgetting.
public class AutoModelConfig
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = true;

	[JsonPropertyName("contextWindow")]
	public int ContextWindow { get; set; } = 0;

	[JsonPropertyName("maxOutputTokens")]
	public int MaxOutputTokens { get; set; } = 0;

	// Null = discovery determines pricing (or it is genuinely free/unknown and the user accepted 0).
	[JsonPropertyName("cost")]
	public CostConfig? Cost { get; set; } = null;

	// Null = discovery determines modalities; ["text"] etc. when the user set them explicitly.
	[JsonPropertyName("modalities")]
	public List<string>? Modalities { get; set; } = null;

	[JsonPropertyName("reasoningEffort")]
	public string ReasoningEffort { get; set; } = string.Empty;

	// False (the default and the behavior for every other model) = the model's reasoning is never
	// replayed to the server. Not discoverable — no catalog states it — so it is always the user's
	// call. See ModelConfig.RetainReasoning for what it does.
	[JsonPropertyName("retainReasoning")]
	public bool RetainReasoning { get; set; } = false;

	// True (the default) = ask for thinking summaries. Unlike the fields above this is not a sparse
	// override — there is nothing to discover — and it is written back to false automatically when an
	// endpoint refuses. See ModelConfig.ReasoningSummaries.
	[JsonPropertyName("reasoningSummaries")]
	public bool ReasoningSummaries { get; set; } = true;
}

public class ToolConfig
{
	[JsonPropertyName("description")]
	public string Description { get; set; } = string.Empty;

	[JsonPropertyName("parameters")]
	public Dictionary<string, string> Parameters { get; set; } = new();
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

	// Ask the provider to summarize this model's thinking so the client can show it while the turn
	// runs, instead of a silent pause. On by default — it is the only window into the slowest and
	// most expensive part of a reasoning turn. Nothing in any catalog says whether an endpoint will
	// honor it, so this starts optimistic and is written back to false the first time a server
	// refuses (a model that does not reason, or an unverified OpenAI organization), which is why the
	// refusal costs one request per model rather than one per turn. Turn it off by hand to stop
	// paying for summary tokens. Responses API only.
	[JsonPropertyName("reasoningSummaries")]
	public bool ReasoningSummaries { get; set; } = true;

	// Replay this model's own reasoning back to it on later turns, in the shape the server emitted.
	// Off by default and for every other model: unsigned thinking is normally stripped, because most
	// providers either ignore it or reject it. A few models are trained on preserved thinking and get
	// measurably worse without it — Moonshot's Kimi K3 states that the complete assistant message,
	// reasoning_content included, must be passed back unchanged in multi-turn and tool-calling
	// conversations. ChatCompletions only; the Anthropic and Responses protocols carry their own
	// native reasoning state (signed blocks, server-side threads) and ignore this.
	[JsonPropertyName("retainReasoning")]
	public bool RetainReasoning { get; set; } = false;

	// How long a single request to this model may stay SILENT before it is abandoned and retried.
	// Streaming readers push this deadline out again on every chunk that arrives, so it never bounds
	// the length of a turn — only a mute connection. Raise it for a machine whose prompt processing
	// runs for minutes before the first token appears (a big quantized model on a slow local box).
	[JsonPropertyName("requestTimeoutSeconds")]
	public int RequestTimeoutSeconds { get; set; } = 300;

	[JsonPropertyName("cost")]
	public CostConfig Cost { get; set; } = new();

	// Input modalities the model accepts ("text", "image", "audio"). Empty means text-only.
	// Populated from catalog discovery for auto models; declarable manually for others.
	[JsonPropertyName("input")]
	public List<string> Input { get; set; } = new();

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
	// Legacy single-provider block. Still honored as a source of the OpenRouter API key (and its
	// enabled flag seeds the OpenRouter provider), so existing settings files keep working, but
	// new configuration goes through Providers.
	[JsonPropertyName("openrouter")]
	public OpenrouterSearchConfig? Openrouter { get; set; }

	// The web-search providers the user turned on in /config. No API key lives here: each
	// provider's key is resolved at load time from the configured endpoint sharing its domain,
	// so a key is entered exactly once no matter how many things use it.
	[JsonPropertyName("providers")]
	public List<WebSearchProviderConfig> Providers { get; set; } = new();
}

// One configured web-search provider. Enabled is the user's intent; a provider whose key cannot
// be resolved is disabled in memory for that run WITHOUT touching this flag — the user may be
// mid-setup, and their stated intent is not ours to rewrite.
public class WebSearchProviderConfig
{
	// Provider id: openrouter, perplexity, xai, anthropic, openai, gemini.
	[JsonPropertyName("provider")]
	public string Provider { get; set; } = string.Empty;

	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = true;

	// Overrides the provider's built-in default search model; empty means use the default.
	[JsonPropertyName("model")]
	public string Model { get; set; } = string.Empty;
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
	// LlmService (see WebSearchOpenrouter), whose budget math needs a real window — a zero
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
		"websearch",
		Endpoint,
		ApiKey,
		Extras,
		Headers,
		new ModelConfig { Id = Model, Name = Model, ContextWindow = ContextWindow, MaxOutputTokens = MaxOutputTokens });
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
		JsonNode?        node   = JsonNode.Parse(ref reader);

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