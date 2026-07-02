using System.Threading;
using System.Threading.Tasks;


// Signature for spawning a subagent session and awaiting its reply. Tool handlers receive this
// shape so any tool can delegate work to a role without knowing the orchestrator. displayName
// null derives the name from the prompt's first line.
public delegate Task<(bool ok, string text, int responseTokens)> SpawnSubagent(string roleName, string? displayName, string prompt, int maxWorkTurns, int outputBudgetTokens, CancellationToken ct);

// Interface that AgentOrchestrator implements and SessionHandlers depend on.
// Decouples session handlers from the concrete orchestrator so they can spawn
// child sessions and route messages without knowing the full orchestrator type.
public interface ISessionOrchestrator
{
	// Spawns a child session under parent for the named role, seeds it with prompt, runs it until
	// it answers via its terminator, and returns the result. The child session stays alive after
	// answering so the user can keep interacting with it.
	Task<(bool ok, string text, int responseTokens)> SpawnChildAsync(BeastSettings settings, Session parent, string roleName, string? displayName, string prompt, int maxWorkTurns, int outputBudgetTokens, CancellationToken ct);

	// Routes a message to the session identified by sessionId. Non-blocking; the message
	// is queued in the target session and processed at the next turn boundary.
	void Deliver(string sessionId, string content);

	// Registers a session so it can be tracked, routed to, and managed by the orchestrator.
	void RegisterSession(Session session);

	// Removes a session from the orchestrator's tracking. Called when a session is
	// compacted, deleted, or replaced.
	void UnregisterSession(string sessionId);

	// Starts the servicing handler for a session when none is attached; a no-op while one is
	// already driving it. Every session that exists gets a handler — this is how sessions without
	// their own configured handler (restored from disk, compaction predecessors) get serviced, and
	// Deliver uses it as a self-healing backstop after a handler failure.
	void EnsureHandler(Session session);

	// Resolves the registered parent of a child session ("parentId_N") for cost rollup and
	// child-id allocation. Null for roots and for parents no longer registered.
	Session? FindParent(Session session);

	// Moves the live completion callback (a caller awaiting the session's reply) from a compacted
	// session to its successor, which inherited the reply obligation.
	void TransferCompletion(string fromSessionId, string toSessionId);

	// Resolves and clears the completion callback waiting on this session, if any. Loaded-from-disk
	// sessions never have one — a callback is a live caller in this process, not persistable state.
	void CompleteSession(string sessionId, bool ok, string text, int responseTokens);
}
