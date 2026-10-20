using System.Collections.Generic;
using System.Text.Json.Serialization;


// Whether a role drives the top-level (root) session the user talks to, or is a worker spawned by the
// subagent tool. The two never mix: a root session only runs Agent roles, a SubagentSession only runs
// Subagent roles.
public enum RoleKind
{
	Agent,
	Subagent
}

// Defines an LLM role: model preferences, allowed tools, and system prompt.
public class Role
{
	[JsonPropertyName("name")]
	public string Name { get; }

	[JsonPropertyName("description")]
	public string Description { get; }

	// Not serialized: roles.json groups roles into an Agents block and a Subagents block, and the block a
	// role appears in determines its kind (see RoleService). So the field is reconstructed on load, never
	// read from or written to the file.
	[JsonIgnore]
	public RoleKind Kind { get; }

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
	// stop_work, a subagent's return_to_caller). Empty = no reminder: the agent idles and waits for
	// the user instead of being kept on task. Data-drives how each role tells the model to finish.
	[JsonPropertyName("end_of_turn_prompt")]
	public string EndOfTurnPrompt { get; }

	// Work budget in turns (one turn = one model response, tool calls included) when this role runs as
	// a subagent: warnings at 50% and 80%, the terminator forced at 100%. 0 = unlimited. A caller may
	// pick one of TurnBudgets instead.
	[JsonPropertyName("max_turns")]
	public int MaxTurns { get; }

	// Optional named budget sizes a delegating caller can choose from (the orchestrator's assign_work
	// effort), each a turn count. Empty = the role has one fixed budget, MaxTurns.
	[JsonPropertyName("turn_budgets")]
	public Dictionary<string, int> TurnBudgets { get; }

	// Run this role's models with thinking off, whatever their configured effort. For roles that
	// transform or digest rather than decide (Explorer, WebFetch, MediaReader): reasoning there only
	// spends output budget and time on deliberation nobody reads.
	[JsonPropertyName("no_reasoning")]
	public bool NoReasoning { get; }

	// Terminator tool this role finishes with when run as a subagent, chosen by its tool list;
	// return_to_caller is the fallback for roles that declare no explicit terminator. Terminators
	// are mutually exclusive — a role declares at most one.
	[JsonIgnore]
	public string TerminatorName
	{
		get
		{
			string name = "return_to_caller";
			if (Tools.Contains("task_complete"))
				name = "task_complete";
			else if (Tools.Contains("finish_review"))
				name = "finish_review";
			return name;
		}
	}

	[JsonConstructor]
	public Role(
		string                  name,
		string                  description,
		RoleKind                kind,
		List<string>            models,
		List<string>            tools,
		string                  systemPrompt,
		string                  summaryPrompt,
		string                  endOfTurnPrompt,
		int                     maxTurns,
		Dictionary<string, int> turnBudgets,
		bool                    noReasoning)
	{
		Name            = name ?? string.Empty;
		Description     = description ?? string.Empty;
		Kind            = kind;
		Models          = models ?? new List<string>();
		Tools           = tools ?? new List<string>();
		SystemPrompt    = systemPrompt ?? string.Empty;
		SummaryPrompt   = summaryPrompt ?? string.Empty;
		EndOfTurnPrompt = endOfTurnPrompt ?? string.Empty;
		MaxTurns        = maxTurns;
		TurnBudgets     = turnBudgets ?? new Dictionary<string, int>();
		NoReasoning     = noReasoning;
	}

	// A role with no work budget that reasons as its models are configured.
	public Role(string name, string description, RoleKind kind, List<string> models, List<string> tools, string systemPrompt, string summaryPrompt, string endOfTurnPrompt)
		: this(name, description, kind, models, tools, systemPrompt, summaryPrompt, endOfTurnPrompt, 0, new Dictionary<string, int>(), false)
	{
	}
}