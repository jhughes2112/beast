using System.Collections.Generic;


// Configuration snapshot for one LLM model entry.
// LlmService is downstream — it holds a reference to this and can be patched
// by LlmRegistry on reload without losing availability state.
public class LlmModel
{
	public string ConfigId { get; }
	public string Endpoint { get; }
	public string ApiKey { get; }
	public Dictionary<string, string> Extras { get; }
	public ModelConfig Config { get; }

	public LlmModel(string configId, string endpoint, string apiKey, Dictionary<string, string> extras, ModelConfig config)
	{
		ConfigId = configId;
		Endpoint = endpoint;
		ApiKey = apiKey;
		Extras = extras;
		Config = config;
	}
}
