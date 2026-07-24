using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

// Source-generated JSON metadata for every typed (de)serialization root in the Agent. Native AOT
// has no reflection-based serializer, so each root type is registered here and every serializer
// call site goes through one of the BeastJson options whose TypeInfoResolver is this context.
// Nested object graphs (CanonicalMessage hierarchy, provider configs, manifest entries) are
// discovered from the roots automatically.
//
// NOTE: the framework REQUIRES a JsonSerializerContext to be declared partial — the generator
// emits the other half. This is the one sanctioned exception to the no-partial-classes rule.
[JsonSerializable(typeof(BeastSession))]
[JsonSerializable(typeof(BeastSettings))]
[JsonSerializable(typeof(SessionService.SessionManifest))]
[JsonSerializable(typeof(RoleService.RolesFile))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(ConfigEndpointsPayload))]
[JsonSerializable(typeof(ConfigCatalogPayload))]
[JsonSerializable(typeof(ConfigApplyPayload))]
internal partial class BeastJsonContext : JsonSerializerContext
{
}

// Context instances bound to each options flavor. Call sites use the typed JsonTypeInfo
// properties (e.g. BeastJson.Persist.BeastSession) — the options-based JsonSerializer overloads
// are annotated RequiresUnreferencedCode and would leave AOT warnings even with a resolver set,
// while the typed overloads are statically clean.
internal static class BeastJson
{
	// Persisted/human-readable files (sessions, manifest, settings, roles): indented, relaxed
	// escaping so prompts and paths stay readable in the files.
	public static readonly BeastJsonContext Persist = new BeastJsonContext(new JsonSerializerOptions
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	});

	// Compact wire frames (transport payloads).
	public static readonly BeastJsonContext Compact = new BeastJsonContext(new JsonSerializerOptions());

	// Tolerant reader for the hand-edited config files: comments and trailing commas allowed.
	public static readonly BeastJsonContext Config = new BeastJsonContext(new JsonSerializerOptions
	{
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	});
}
