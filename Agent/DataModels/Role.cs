using System.Collections.Generic;
using System.Text.Json.Serialization;


// Defines an LLM role: model preferences, allowed tools, system prompt,
// and optional state-transition logic (query, evaluatorRole, truths).
public class Role
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

	[JsonPropertyName("summary_prompt")]
	public string SummaryPrompt { get; }

    // Prompt given to the evaluator after each completed turn. Null or empty = no automatic transitions.
	[JsonPropertyName("end_of_turn_prompt")]
	public string EndOfTurnPrompt { get; }

    // Maps evaluator truth labels to the next role name.
	[JsonPropertyName("truths")]
	public Dictionary<string, string> Truths { get; }

	[JsonConstructor]
	public Role(
		string name,
		List<string> models,
		List<string> tools,
		string systemPrompt,
		string summaryPrompt,
		string endOfTurnPrompt,
		Dictionary<string, string> truths)
	{
		Name = name ?? string.Empty;
		Models = models ?? new List<string>();
		Tools = tools ?? new List<string>();
		SystemPrompt = systemPrompt ?? string.Empty;
		SummaryPrompt = summaryPrompt ?? string.Empty;
		EndOfTurnPrompt = endOfTurnPrompt ?? string.Empty;
		Truths = truths ?? new Dictionary<string, string>();
	}

	public static Role DefaultRole(List<string> toolNames)
	{
        const string systemPrompt = "You are a helpful assistant. Read the MEMORY.md file for project-level knowledge, read PLAN.md for tasks in progress.";
        const string summaryPrompt = """
            Update project-level learnings that have long-term value in MEMORY.md, update PLAN.md so that the status of the current task is reflected.
            Output only a summary of the conversation retaining the objective, current status, discovered context, and key next steps and exact details that would help perform them. 
            """;
        const string endOfTurnPrompt = "Are you finished?";
        Dictionary<string, string> truths = new Dictionary<string, string>()
            {  
                { "This task is complete.", "" },
                { "There is more work to do.", "Default" }
            };
        return new Role("Default", new List<string> { "*" }, toolNames, systemPrompt, summaryPrompt, endOfTurnPrompt, truths);
	}
}
