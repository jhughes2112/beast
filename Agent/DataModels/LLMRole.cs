using System.Collections.Generic;
using System.Text.Json.Serialization;


// Defines an LLM role with its model preferences, allowed tools, and behavior.
// Roles are loaded from JSON (roles.json). Core properties are set-once via
// deserialization. lastModel is updated at runtime when the user switches models
// and persisted back to roles.json.
public class LLMRole
{
	[JsonPropertyName("name")]
	public string Name { get; }

    // One special note about the list of models is '*' means any model is allowed.  Order is still respected, so you can put * last in the list and set preferences higher.
	[JsonPropertyName("models")]
	public List<string> Models { get; }

	[JsonPropertyName("tools")]
	public List<string> Tools { get; }

	[JsonPropertyName("system_prompt")]
	public string SystemPrompt { get; }

	[JsonConstructor]
	public LLMRole(string name, List<string> models, List<string> tools, string systemPrompt)
	{
		Name = name;
		Models = models;
		Tools = tools;
		SystemPrompt = systemPrompt;
	}

	public static LLMRole DefaultRole(List<string> modelIds, List<string> toolNames)
	{
		return new LLMRole("Default", modelIds, toolNames, "You are a helpful assistant.");
	}
}