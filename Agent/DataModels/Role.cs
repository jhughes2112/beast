using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;


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

	[JsonPropertyName("kind")]
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
	// task_complete, a subagent's return_to_caller). Empty = no reminder: the agent idles and waits for
	// the user instead of being kept on task. Data-drives how each role tells the model to finish.
	[JsonPropertyName("end_of_turn_prompt")]
	public string EndOfTurnPrompt { get; }

	// Bash commands run when a session of this role is entered / left. Empty = no hook. A nonzero exit
	// blocks the transition and the captured output becomes the error reported to the caller (see
	// EnterAsync / ExitAsync).
	[JsonPropertyName("on_enter")]
	public string OnEnter { get; }

	[JsonPropertyName("on_exit")]
	public string OnExit { get; }

	// The role's Tools names resolved to Tool instances, bound once at load time (see BindTools) so the
	// set is not rebuilt every turn. Not serialized.
	private Tool[] _builtTools = Array.Empty<Tool>();

	[JsonIgnore]
	public Tool[] BuiltTools => _builtTools;

	[JsonConstructor]
	public Role(
		string name,
		string description,
		RoleKind kind,
		List<string> models,
		List<string> tools,
		string systemPrompt,
		string summaryPrompt,
		string endOfTurnPrompt,
		string onEnter,
		string onExit)
	{
		Name = name ?? string.Empty;
		Description = description ?? string.Empty;
		Kind = kind;
		Models = models ?? new List<string>();
		Tools = tools ?? new List<string>();
		SystemPrompt = systemPrompt ?? string.Empty;
		SummaryPrompt = summaryPrompt ?? string.Empty;
		EndOfTurnPrompt = endOfTurnPrompt ?? string.Empty;
		OnEnter = onEnter ?? string.Empty;
		OnExit = onExit ?? string.Empty;
	}

	// Resolves and stores the role's tool instances. Called after roles and models are loaded.
	public void BindTools(Tool[] tools)
	{
		_builtTools = tools;
	}

	// Runs the entry hook on the bash command line. variables substitutes {key} placeholders in the
	// command first (e.g. {branch}). Returns string.Empty on success (exit 0); otherwise the command
	// followed by its stdout and stderr so the caller can report why entry was refused.
	public Task<string> EnterAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? variables) => RunHookAsync(OnEnter, variables, ct);

	// Runs the exit hook. Same contract as EnterAsync.
	public Task<string> ExitAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? variables) => RunHookAsync(OnExit, variables, ct);

	private static async Task<string> RunHookAsync(string command, IReadOnlyDictionary<string, string>? variables, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(command))
			return string.Empty;

		string expanded = command;
		if (variables != null)
		{
			foreach (KeyValuePair<string, string> kv in variables)
				expanded = expanded.Replace("{" + kv.Key + "}", kv.Value);
		}

		ToolResult result = await ShellTools.BashAsync("role_hook", expanded, null, ct);
		if (result.ExitCode == 0)
			return string.Empty;

		StringBuilder sb = new StringBuilder();
		sb.Append("$ ").Append(expanded);
		if (!string.IsNullOrEmpty(result.StdOut))
			sb.Append('\n').Append(result.StdOut);
		if (!string.IsNullOrEmpty(result.StdErr))
			sb.Append('\n').Append(result.StdErr);
		return sb.ToString();
	}
}
