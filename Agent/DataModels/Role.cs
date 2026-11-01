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

	// The output modality this role needs from its models (see the OutputModality class); blank means
	// text. Only models declaring it may serve the role: '*' expands to those alone and model selection
	// skips the rest, so a chat role never lands on a decision model or the reverse. A decisions role
	// (HallMonitor) keeps its questions in code, not here: they are calibrated per model, and an edit
	// would silently invalidate every calibration.
	[JsonPropertyName("output_modality")]
	public string OutputModality { get; }

	// The output modality with the blank settled, for comparing against what models produce.
	[JsonIgnore]
	public string Produces => OutputModality.Length > 0 ? OutputModality : global::OutputModality.Text;

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
		bool                    noReasoning,
		string                  outputModality)
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
		OutputModality  = outputModality ?? string.Empty;
	}

	// A text role with a work budget: every agent and subagent that runs a conversation.
	public Role(string name, string description, RoleKind kind, List<string> models, List<string> tools, string systemPrompt, string summaryPrompt, string endOfTurnPrompt,
		int maxTurns, Dictionary<string, int> turnBudgets, bool noReasoning)
		: this(name, description, kind, models, tools, systemPrompt, summaryPrompt, endOfTurnPrompt, maxTurns, turnBudgets, noReasoning, global::OutputModality.Text)
	{
	}

	// A text role with no work budget that reasons as its models are configured.
	public Role(string name, string description, RoleKind kind, List<string> models, List<string> tools, string systemPrompt, string summaryPrompt, string endOfTurnPrompt)
		: this(name, description, kind, models, tools, systemPrompt, summaryPrompt, endOfTurnPrompt, 0, new Dictionary<string, int>(), false)
	{
	}

	// This role with a different model list; everything else is carried over.
	public Role WithModels(List<string> models)
	{
		return new Role(Name, Description, Kind, models, Tools, SystemPrompt, SummaryPrompt, EndOfTurnPrompt, MaxTurns, TurnBudgets, NoReasoning, OutputModality);
	}

	// This role as the given kind, which roles.json carries by block rather than per role.
	public Role WithKind(RoleKind kind)
	{
		return new Role(Name, Description, kind, Models, Tools, SystemPrompt, SummaryPrompt, EndOfTurnPrompt, MaxTurns, TurnBudgets, NoReasoning, OutputModality);
	}
}