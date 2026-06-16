using System.Collections.Generic;
using System.Text.Json.Nodes;


// Configuration snapshot for one LLM model entry.
// LlmService is downstream — it holds a reference to this and can be patched
// by LlmRegistry on reload without losing availability state.
public class LlmModel
{
	public string ConfigId { get; }
	public string Endpoint { get; }
	public string ApiKey { get; }
	public List<JsonObject> Extras { get; }
	public List<JsonObject> Headers { get; }
	public ModelConfig Config { get; }

	public LlmModel(string configId, string endpoint, string apiKey, List<JsonObject> extras, List<JsonObject> headers, ModelConfig config)
	{
		ConfigId = configId;
		Endpoint = endpoint;
		ApiKey = apiKey;
		Extras = extras;
		Headers = headers;
		Config = config;
	}
}
