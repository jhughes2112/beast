using System.Collections.Generic;
using System.Text.Json.Serialization;


// Defines an LLM role with its model preferences, allowed tools, and behavior.
// Roles are loaded from JSON (roles.json). Core properties are set-once via
// deserialization. lastModel is updated at runtime when the user switches models
// and persisted back to roles.json.
public class LLMRole
{
	public string Name { get; }

	[JsonPropertyName("models")]
	public List<string> ModelNames { get; }

	[JsonPropertyName("tools")]
	public List<string> ToolNames { get; }

	[JsonPropertyName("system_prompt")]
	public string SystemPrompt { get; }

	[JsonConstructor]
	public LLMRole(string name, List<string> models, List<string> tools, string systemPrompt)
	{
		Name = name;
		ModelNames = models;
		ToolNames = tools;
		SystemPrompt = systemPrompt;
	}

	public static LLMRole DefaultRole()
	{
		return new LLMRole("Default", new List<string>(), new List<string>(), "You are a helpful assistant.");
	}
}