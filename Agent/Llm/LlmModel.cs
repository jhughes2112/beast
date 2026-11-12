using System;
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

	// The same model thinking as little as it can: thinking off, or its lowest option when the endpoint
	// says its thinking cannot be turned off. Returns a copy — the registry's own instance is shared by
	// every session, so the effort setting is never mutated in place. Used where a call is a mechanical
	// transformation rather than a problem to solve (compaction), and reasoning tokens would only spend
	// the output budget on deliberation nobody reads.
	public LlmModel WithoutReasoning()
	{
		List<string> options = Config.ReasoningEfforts ?? new List<string>();
		string       least   = Config.ThinkingRequired && options.Count > 0 ? options[0] : ReasoningEffort.Off;
		if (string.Equals(Config.ReasoningEffort, least, StringComparison.OrdinalIgnoreCase))
			return this;

		ModelConfig quiet     = CopyConfig();
		quiet.ReasoningEffort = least;
		// Nobody reads the thinking on a mechanical call, so do not pay to have it summarized.
		quiet.ReasoningSummaries = false;
		return new LlmModel(ConfigId, Endpoint, ApiKey, Extras, Headers, quiet);
	}

	// The same model with a seed its own settings do not give it, conferred by the seeded session that called it.
	public LlmModel WithSeed(long seed)
	{
		ModelConfig seeded = CopyConfig();
		seeded.Seed        = seed;
		return new LlmModel(ConfigId, Endpoint, ApiKey, Extras, Headers, seeded);
	}

	private ModelConfig CopyConfig()
	{
		return new ModelConfig
		{
			Id                 = Config.Id,
			Name               = Config.Name,
			Enabled            = Config.Enabled,
			ContextWindow      = Config.ContextWindow,
			MaxOutputTokens    = Config.MaxOutputTokens,
			ReasoningEffort    = Config.ReasoningEffort,
			ReasoningEfforts   = Config.ReasoningEfforts,
			EffortsListed      = Config.EffortsListed,
			DefaultEffort      = Config.DefaultEffort,
			ThinkingRequired   = Config.ThinkingRequired,
			ReasoningSummaries = Config.ReasoningSummaries,
			// Carried: a model that needs its own reasoning replayed needs it on every call.
			RetainReasoning = Config.RetainReasoning,
			MaxImages       = Config.MaxImages,
			// Carried: a slow box needs its patience on every call, quiet or not.
			RequestTimeoutSeconds = Config.RequestTimeoutSeconds,
			// Carried: a reproducible run stays reproducible through its mechanical calls.
			Seed              = Config.Seed,
			Temperature       = Config.Temperature,
			TopP              = Config.TopP,
			TopK              = Config.TopK,
			MinP              = Config.MinP,
			FrequencyPenalty  = Config.FrequencyPenalty,
			PresencePenalty   = Config.PresencePenalty,
			RepetitionPenalty = Config.RepetitionPenalty,
			Cost              = Config.Cost,
			Input             = Config.Input,
			Output            = Config.Output,
			Extras            = Config.Extras,
			Headers           = Config.Headers
		};
	}
}