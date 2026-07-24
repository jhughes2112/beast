using System.Collections.Generic;
using System.Text.Json.Serialization;


// Wire payloads for the /config flow, carried in Config frames (Agent → Beast). Kind
// discriminates the payload: "endpoints", "catalog", or "applied". Beast parses these with
// JsonNode; the Agent serializes them through the source-generated context.
public class ConfigEndpointsPayload
{
	[JsonPropertyName("kind")]
	public string Kind { get; set; } = "endpoints";

	[JsonPropertyName("endpoints")]
	public List<ConfigEndpointInfo> Endpoints { get; set; } = new();
}

public class ConfigEndpointInfo
{
	[JsonPropertyName("baseUrl")]
	public string BaseUrl { get; set; } = string.Empty;

	// "auto" for /config-managed endpoints, "manual" for hand-edited providers (listed for
	// reference in the picker but not editable there).
	[JsonPropertyName("source")]
	public string Source { get; set; } = string.Empty;

	[JsonPropertyName("enabledCount")]
	public int EnabledCount { get; set; } = 0;
}

public class ConfigCatalogPayload
{
	[JsonPropertyName("kind")]
	public string Kind { get; set; } = "catalog";

	[JsonPropertyName("baseUrl")]
	public string BaseUrl { get; set; } = string.Empty;

	// Non-empty when the fetch failed; Models is then empty.
	[JsonPropertyName("error")]
	public string Error { get; set; } = string.Empty;

	[JsonPropertyName("models")]
	public List<ConfigModelInfo> Models { get; set; } = new();
}

// One catalog row for the picker. The value fields are the DISCOVERED values exactly as the
// endpoint reported them (0 / -1 / null = unknown) — never pre-merged — so the picker can tell
// "discoverable" from "user-supplied" and honor blank-means-rediscover edits. The persisted
// entry, when one exists, rides along whole as Override.
public class ConfigModelInfo
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("contextWindow")]
	public int ContextWindow { get; set; } = 0;

	[JsonPropertyName("maxOutputTokens")]
	public int MaxOutputTokens { get; set; } = 0;

	// Per-million USD; -1 = unknown.
	[JsonPropertyName("costInput")]
	public decimal CostInput { get; set; } = -1m;

	[JsonPropertyName("costOutput")]
	public decimal CostOutput { get; set; } = -1m;

	// Null = unknown.
	[JsonPropertyName("modalities")]
	public List<string>? Modalities { get; set; } = null;

	// True when a settings entry exists for this model at all; Enabled is that entry's flag.
	// A configured-but-disabled model keeps its overrides and shows as such in the picker.
	[JsonPropertyName("configured")]
	public bool Configured { get; set; } = false;

	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = false;

	// Unix epoch seconds the model was released; 0 = unknown.
	[JsonPropertyName("created")]
	public long Created { get; set; } = 0;

	// The persisted settings entry (sparse overrides), null when the model is unconfigured.
	[JsonPropertyName("override")]
	public AutoModelConfig? Override { get; set; } = null;
}

// Beast → Agent apply payload (sent as the argument of /config-apply): the full desired state of
// one auto endpoint. Models is the enabled set; each entry carries only user-supplied overrides.
public class ConfigApplyPayload
{
	[JsonPropertyName("baseUrl")]
	public string BaseUrl { get; set; } = string.Empty;

	[JsonPropertyName("apiKey")]
	public string ApiKey { get; set; } = string.Empty;

	[JsonPropertyName("models")]
	public List<AutoModelConfig> Models { get; set; } = new();
}
