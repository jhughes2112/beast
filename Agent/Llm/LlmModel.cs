using System.Collections.Generic;
using System.Text.Json.Nodes;


// Configuration snapshot for one LLM model entry.
// LlmService is downstream — it holds a reference to this and can be patched
// by LlmRegistry on reload without losing availability state.
public class LlmModel
{
	public string           ConfigId { get; }
	public string           Endpoint { get; }
	public string           ApiKey   { get; }
	public List<JsonObject> Extras   { get; }
	public List<JsonObject> Headers  { get; }
	public ModelConfig      Config   { get; }

	public LlmModel(string configId, string endpoint, string apiKey, List<JsonObject> extras, List<JsonObject> headers, ModelConfig config)
	{
		ConfigId = configId;
		Endpoint = endpoint;
		ApiKey   = apiKey;
		Extras   = extras;
		Headers  = headers;
		Config   = config;
	}

	// The same model with thinking turned off. Returns a copy — the registry's own instance is
	// shared by every session, so the effort setting is never mutated in place. Used where a call
	// is a mechanical transformation rather than a problem to solve (compaction), and reasoning
	// tokens would only spend the output budget on deliberation nobody reads.
	public LlmModel WithoutReasoning()
	{
		// Blank no longer means "off" — it means "nobody configured this", which now reads as the
		// default level (see ReasoningEffort.DefaultWord). Off is the explicit word, both in the test
		// below and in the copy, or a model with no configured effort would keep right on thinking.
		if (ReasoningEffort.Parse(Config.ReasoningEffort) == ReasoningLevel.None)
			return this;

		ModelConfig quiet = new ModelConfig
		{
			Id              = Config.Id,
			Name            = Config.Name,
			Enabled         = Config.Enabled,
			ContextWindow   = Config.ContextWindow,
			MaxOutputTokens = Config.MaxOutputTokens,
			ReasoningEffort = "none",
			// Nobody reads the thinking on a mechanical call, so do not pay to have it summarized.
			ReasoningSummaries = false,
			// Carried: a model that needs its own reasoning replayed needs it on every call.
			RetainReasoning = Config.RetainReasoning,
			Cost            = Config.Cost,
			Input           = Config.Input,
			Extras          = Config.Extras,
			Headers         = Config.Headers
		};
		return new LlmModel(ConfigId, Endpoint, ApiKey, Extras, Headers, quiet);
	}
}