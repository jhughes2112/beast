using System;

// Governs execution policy for one SessionHandler run: turn budget, parent linkage (cost rollup
// and child-id allocation), and the live completion callback. The reply obligation itself — the
// terminator tool name and its output budget — lives on the session data (BeastSession) so it
// survives a save/load cycle and travels to compaction successors.
public class SessionRunConfig
{
	// null for root sessions; the session that spawned this child.
	public Session? Parent { get; }

	// Maximum working turns before wind-down begins. 0 = unlimited (root).
	public int MaxWorkTurns { get; }

	// Called once when the session answers its caller — terminator output, failure report, or
	// salvaged last text. null when no live caller is waiting (root sessions, restored children).
	public Action<bool, string, int>? OnComplete { get; }

	public SessionRunConfig(Session? parent, int maxWorkTurns, Action<bool, string, int>? onComplete)
	{
		Parent = parent;
		MaxWorkTurns = maxWorkTurns;
		OnComplete = onComplete;
	}
}
