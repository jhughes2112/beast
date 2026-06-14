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
        const string systemPrompt = "You are a helpful assistant.";
        const string summaryPrompt = """
            Output only a summary of the preceding conversation retaining the theme, critical concepts, current status, discovered context, most recent transaction in this discussion, and any other exact details that would help maintain continuity in a new conversation.  Be concise.
            """;
        const string endOfTurnPrompt = "";
        Dictionary<string, string> statements = new Dictionary<string, string>();
        return new Role("Default", new List<string> { "*" }, toolNames, systemPrompt, summaryPrompt, endOfTurnPrompt, statements);
	}

	public static Role TaskRole(List<string> toolNames)
	{
        const string systemPrompt = "You are a capable assistant. Read the MEMORY.md file for project-level knowledge, read PLAN.md for tasks in progress. Perform the requested task, asking clarifying questions as needed before getting started.";
        const string summaryPrompt = """
            Update project-level learnings that have long-term value in MEMORY.md, update PLAN.md so that the status of the current task is reflected.
            Output only a summary of the preceding conversation retaining the objective, critical concepts, current status, discovered context, key next steps, and exact details that would help perform them. Be concise. Retain only that which will help complete the task.
            """;
        const string endOfTurnPrompt = "Conversation will continue until you call the state_transition tool to end it.";
        Dictionary<string, string> statements = new Dictionary<string, string>()
            {
                { "This task is complete.", "Default" },
                { "There is more work to do.", "Task" }
            };
        return new Role("Task", new List<string> { "*" }, toolNames, systemPrompt, summaryPrompt, endOfTurnPrompt, statements);
	}

    public static Role ToolsRole(List<string> toolNames)
    {
        const string systemPrompt = 
            """
			You are a research agent performing a single tool call. Your purpose is to minimize noise in the main agent's context.
			You received a goal and an initial command. Run this command.\nIf the output achieves the goal, do no further work, do not summarize the results, respond immediately and provide the results with the exact output.\nIf the command returns an error or fails to achieve the goal by the output, attempt to provide the requested information through other tool calls and report the minimum output needed to accomplish the goal. 
			Do not interpret the results unless asked.
			If the goal cannot be achieved, be very brief and report this and list any commands attempted, in addition to the exact output from the original command.
			""";
        return new Role("Tools", new List<string> { "*" }, toolNames, systemPrompt, string.Empty, string.Empty, new Dictionary<string, string>());
    }
}
