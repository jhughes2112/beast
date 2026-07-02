using System.Threading;
using System.Threading.Tasks;


// Interface that AgentOrchestrator implements and SessionHandlers depend on.
// Decouples session handlers from the concrete orchestrator so they can spawn
// child sessions and route messages without knowing the full orchestrator type.
public interface ISessionOrchestrator
{
	// Spawns a child session under parent for the named role, seeds it with prompt, runs it
	// to completion, and returns the result. Called from tool handlers in SessionHandler.
	Task<(bool ok, string text, int responseTokens)> SpawnChildAsync(BeastSettings settings, Session parent, string roleName, string prompt, int outputBudgetTokens, CancellationToken ct);

	// Routes a message to the session identified by sessionId. Non-blocking; the message
	// is queued in the target session and processed at the next turn boundary.
	void Deliver(string sessionId, string content);

	// Registers a session so it can be tracked, routed to, and managed by the orchestrator.
	void RegisterSession(Session session);

	// Removes a session from the orchestrator's tracking. Called when a session is
	// compacted, deleted, or replaced.
	void UnregisterSession(string sessionId);
}
