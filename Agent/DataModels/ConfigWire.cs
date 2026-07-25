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

	// Every web-search provider Beast supports, with its live key resolution — the picker shows
	// the whole roster so an unconfigured one can be added and a keyless one explains itself.
	[JsonPropertyName("search")]
	public List<ConfigSearchInfo> Search { get; set; } = new();
}

// One web-search provider row for the picker.
public class ConfigSearchInfo
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	// The endpoint host whose API key this provider borrows.
	[JsonPropertyName("domain")]
	public string Domain { get; set; } = string.Empty;

	[JsonPropertyName("price")]
	public decimal PricePerThousand { get; set; } = 0m;

	// True when a settings entry exists at all; Enabled is that entry's flag.
	[JsonPropertyName("configured")]
	public bool Configured { get; set; } = false;

	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = false;

	// False when no configured endpoint carries the provider's domain — the provider is then
	// disabled in memory for this run no matter what Enabled says.
	[JsonPropertyName("hasKey")]
	public bool HasKey { get; set; } = false;

	// The search model in effect (the override, or the provider's default).
	[JsonPropertyName("model")]
	public string Model { get; set; } = string.Empty;
}

// Agent → Beast: every role and the model order it currently uses, for the /role editor.
public class ConfigRolesPayload
{
	[JsonPropertyName("kind")]
	public string Kind { get; set; } = "roles";

	[JsonPropertyName("roles")]
	public List<ConfigRoleInfo> Roles { get; set; } = new();

	// The role the active session is running, so the editor can open on it.
	[JsonPropertyName("active")]
	public string Active { get; set; } = string.Empty;
}

public class ConfigRoleInfo
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("kind")]
	public string Kind { get; set; } = string.Empty;

	// The role's models in preference order, each already resolved to a display label.
	[JsonPropertyName("models")]
	public List<ConfigRoleModel> Models { get; set; } = new();
}

public class ConfigRoleModel
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	// Cost and modalities, so the ordering decision can be made without leaving the editor.
	[JsonPropertyName("label")]
	public string Label { get; set; } = string.Empty;

	// False when the id is configured for the role but not currently registered (endpoint down,
	// model disabled). Kept in the order so saving cannot silently drop it.
	[JsonPropertyName("available")]
	public bool Available { get; set; } = true;
}

// Beast → Agent: one role's new model order.
public class ConfigRoleApplyPayload
{
	[JsonPropertyName("role")]
	public string Role { get; set; } = string.Empty;

	[JsonPropertyName("models")]
	public List<string> Models { get; set; } = new();
}

// Beast → Agent: the full desired state of the web-search provider list.
public class ConfigSearchApplyPayload
{
	[JsonPropertyName("providers")]
	public List<WebSearchProviderConfig> Providers { get; set; } = new();
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