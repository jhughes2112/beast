using System.Collections.Generic;
using System.Text.Json.Serialization;


// Defines an LLM role: model preferences, allowed tools, system prompt,
// and optional state-transition logic (query, evaluatorRole, truths).
public class Role
{
	[JsonPropertyName("name")]
	public string Name { get; }

    // '*' expands at load time to all enabled model IDs at that position. Order is still respected.
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
	[JsonPropertyName("statements")]
	public Dictionary<string, string> Statements { get; }

	[JsonConstructor]
	public Role(
		string name,
		List<string> models,
		List<string> tools,
		string systemPrompt,
		string summaryPrompt,
		string endOfTurnPrompt,
		Dictionary<string, string> statements)
	{
		Name = name ?? string.Empty;
		Models = models ?? new List<string>();
		Tools = tools ?? new List<string>();
		SystemPrompt = systemPrompt ?? string.Empty;
		SummaryPrompt = summaryPrompt ?? string.Empty;
		EndOfTurnPrompt = endOfTurnPrompt ?? string.Empty;
		Statements = statements ?? new Dictionary<string, string>();
	}

	public static Role DefaultRole(List<string> toolNames)
	{
        const string systemPrompt = "You are a helpful assistant. Read the MEMORY.md file for project-level knowledge, read PLAN.md for tasks in progress.";
        const string summaryPrompt = """
            Update project-level learnings that have long-term value in MEMORY.md, update PLAN.md so that the status of the current task is reflected.
            Output only a summary of the conversation retaining the objective, current status, discovered context, and key next steps and exact details that would help perform them.
            """;
        const string endOfTurnPrompt = "Are you finished?";
        Dictionary<string, string> statements = new Dictionary<string, string>()
            {
                { "This task is complete.", "" },
                { "There is more work to do.", "Default" }
            };
        return new Role("Default", new List<string> { "*" }, toolNames, systemPrompt, summaryPrompt, endOfTurnPrompt, statements);
	}

    public static Role ToolsRole(List<string> toolNames)
    {
        const string systemPrompt = 
            """
            You are a research agent performing a single tool call. Your purpose is to minimize noise in the main agent's context.
            You received a goal and an initial command. Run this command. 
            If the output achieves the goal, do no further work, respond immediately with the exact output.
            If the command returns an error or the goal is not achieved by the output, try up to 10 tool calls to determine the correct command that produces 
            the minimum results needed to accomplish the goal. Respond only with the exact command and its output, without commentary.
            Cite precisely: file paths, line numbers, function names, exact outputs. 
            If the goal is not achieved, be very brief, but report this fact, list exact tools called, in addition to the exact output from the original command.
            """;
        return new Role("Tools", new List<string> { "*" }, toolNames, systemPrompt, string.Empty, string.Empty, new Dictionary<string, string>());
    }
}
