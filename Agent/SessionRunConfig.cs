using System;

// Governs execution policy for a SessionHandler. Role-specific behaviour (compaction via
// SummaryPrompt, end-of-turn nudges via EndOfTurnPrompt) is read from the Role directly; this
// class covers the structural differences between a root session and a child subagent.
public class SessionRunConfig
{
	// null for root sessions; the session that spawned this child (for cost rollup).
	public Session? Parent { get; }

	// Tool name the model must call to end this run and deliver a result.
	// null for root sessions, which run until the process is cancelled.
	public string? TerminatorName { get; }

	// Maximum tokens allowed in the terminator response. 0 = no limit (root).
	public int OutputBudgetTokens { get; }

	// Maximum working turns before wind-down begins. 0 = unlimited (root).
	public int MaxWorkTurns { get; }

	// Called once when the handler finishes — either via the terminator or salvaged last text.
	// null for root sessions and for restored orphan children (no caller waiting for a result).
	public Action<bool, string, int>? OnComplete { get; }

	public SessionRunConfig(Session? parent, string? terminatorName, int outputBudgetTokens, int maxWorkTurns, Action<bool, string, int>? onComplete)
	{
		Parent = parent;
		TerminatorName = terminatorName;
		OutputBudgetTokens = outputBudgetTokens;
		MaxWorkTurns = maxWorkTurns;
		OnComplete = onComplete;
	}
}
