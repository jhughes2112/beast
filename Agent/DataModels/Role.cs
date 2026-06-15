using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;


// Defines an LLM role: model preferences, allowed tools, and system prompt.
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

	// Reminder appended after a turn that ends without the agent calling its terminator tool (the root's
	// task_complete, a subagent's return_to_caller). Empty = no reminder: the agent idles and waits for
	// the user instead of being kept on task. Data-drives how each role tells the model to finish.
	[JsonPropertyName("end_of_turn_prompt")]
	public string EndOfTurnPrompt { get; }

	// The role's Tools names resolved to Tool instances, bound once at load time (see BindTools) so the
	// set is not rebuilt every turn. Not serialized.
	private Tool[] _builtTools = Array.Empty<Tool>();

	[JsonIgnore]
	public Tool[] BuiltTools => _builtTools;

	[JsonConstructor]
	public Role(
		string name,
		List<string> models,
		List<string> tools,
		string systemPrompt,
		string summaryPrompt,
		string endOfTurnPrompt)
	{
		Name = name ?? string.Empty;
		Models = models ?? new List<string>();
		Tools = tools ?? new List<string>();
		SystemPrompt = systemPrompt ?? string.Empty;
		SummaryPrompt = summaryPrompt ?? string.Empty;
		EndOfTurnPrompt = endOfTurnPrompt ?? string.Empty;
	}

	// Resolves and stores the role's tool instances. Called after roles and models are loaded.
	public void BindTools(Tool[] tools)
	{
		_builtTools = tools;
	}

	public static Role DefaultRole(List<string> toolNames)
	{
        const string systemPrompt = "You are a helpful assistant.";
        const string summaryPrompt = """
            Output only a summary of the preceding conversation retaining the theme, critical concepts, current status, discovered context, most recent transaction in this discussion, and any other exact details that would help maintain continuity in a new conversation.  Be concise.
            """;
        // Conversational steady state: no end-of-turn reminder, so the assistant simply waits for the user.
        const string endOfTurnPrompt = "";
        return new Role("Default", new List<string> { "*" }, toolNames, systemPrompt, summaryPrompt, endOfTurnPrompt);
	}

	public static Role TaskRole(List<string> toolNames)
	{
        const string systemPrompt = "You are a capable assistant. Read the MEMORY.md file for project-level knowledge, read PLAN.md for tasks in progress. Perform the requested task, asking clarifying questions as needed before getting started.";
        const string summaryPrompt = """
            Update project-level learnings that have long-term value in MEMORY.md, update PLAN.md so that the status of the current task is reflected.
            Output only a summary of the preceding conversation retaining the objective, critical concepts, current status, discovered context, key next steps, and exact details that would help perform them. Be concise. Retain only that which will help complete the task.
            """;
        const string endOfTurnPrompt = "If the task is finished, call the task_complete tool with the final status. Otherwise keep working.";
        return new Role("Task", new List<string> { "*" }, toolNames, systemPrompt, summaryPrompt, endOfTurnPrompt);
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
        const string endOfTurnPrompt = "If you have achieved the goal, call the return_to_caller tool with the result. Otherwise keep working.";
        return new Role("Tools", new List<string> { "*" }, toolNames, systemPrompt, string.Empty, endOfTurnPrompt);
    }
}
