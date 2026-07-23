using System.Collections.Generic;
using System.Text.Json.Serialization;


// Pure serializable data for a single conversation. Load from disk, wrap in Session, talk to Session.
// All mutation goes through Session; BeastSession only stores and transports the values.
public class BeastSession
{
	[JsonPropertyName("id")]
	public string Id { get; }

	// Set once from the first user message; empty until then.
	[JsonPropertyName("displayName")]
	public string DisplayName { get; internal set; }

	// Last model used; empty on new sessions (resolved at first turn by the registry).
	[JsonPropertyName("model")]
	public string Model { get; internal set; }

	// JsonInclude: with a JsonConstructor present, properties that are not constructor parameters
	// are only repopulated on load through public setters; JsonInclude opts the internal setter in.
	[JsonInclude]
	[JsonPropertyName("contextWindow")]
	public int ContextWindow { get; internal set; }

	[JsonPropertyName("role")]
	public string Role { get; internal set; }

	// The reply obligation this session carries: the terminator tool it must call to answer the
	// caller that spawned it, and the token budget for that answer. Empty when no caller is
	// waiting — root sessions, sessions that already replied, and compaction predecessors that
	// handed the obligation to their successor. Persisted so a reloaded session still knows
	// whether it may respond as a tool.
	[JsonPropertyName("terminatorName")]
	public string TerminatorName { get; internal set; }

	[JsonPropertyName("outputBudgetTokens")]
	public int OutputBudgetTokens { get; internal set; }

	// Working-turn budget carried with the reply obligation: how many turns the session may work
	// before wind-down forces the terminator. 0 = unlimited (root sessions). Travels to a
	// compaction successor and clears with the obligation — once the caller has been answered,
	// no budget applies.
	[JsonInclude]
	[JsonPropertyName("maxWorkTurns")]
	public int MaxWorkTurns { get; internal set; }

	// Typed canonical conversation history. Single source of truth for the session.
	// Live protocol listeners keep their own native runtime state and rehydrate from this list
	// on creation or model switch.
	[JsonPropertyName("messages")]
	public List<CanonicalMessage> Messages { get; }

	[JsonPropertyName("lastTokenUsage")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public TokenUsageInfo? LastTokenUsage { get; internal set; }

	// The actual context size after the last turn: raw prompt_tokens + completion_tokens
	// as reported by the provider (before subtracting cached tokens). This is the true
	// conversation size that determines how much space remains in the context window.
	[JsonPropertyName("currentContextSize")]
	public int CurrentContextSize { get; internal set; }

	[JsonPropertyName("totalCost")]
	public decimal TotalCost { get; internal set; }

	// Absolute session totals. These only ever increase over the life of a session and are never
	// reset, not on model switch and not on compaction. CumulativeInputTokens and
	// CumulativeOutputTokens are what the client displays as the running in/out counters, while
	// LastTokenUsage stays scoped to the most recent turn for context-occupancy math.
	[JsonPropertyName("cumulativeInputTokens")]
	public int CumulativeInputTokens { get; internal set; }

	[JsonPropertyName("cumulativeOutputTokens")]
	public int CumulativeOutputTokens { get; internal set; }

	// Not persisted; deserialization always produces false (correct: loaded sessions are never ephemeral).
	[JsonIgnore]
	public bool Ephemeral { get; }

	// Tracks the highest child ID suffix allocated by this session, persisted so that reloaded sessions
	// do not reuse an already-taken child ID (which would append to an unrelated session file).
	[JsonInclude]
	[JsonPropertyName("childCounter")]
	public int ChildCounter { get; internal set; }

	// Persisted termination status: Ongoing, Success, Failure, or Incomplete. Stamped when the
	// session's reply (or failure report) is actually delivered to its caller — including callers
	// that struck the session off after a failure — so reloaded sessions remember how they
	// finished without re-deriving it from code paths.
	[JsonInclude]
	[JsonPropertyName("terminalStatus")]
	public string TerminalStatus { get; internal set; }

	// Creation order for sorting: for root sessions, a global counter assigned at creation.
	// For child sessions, the child number (N in parentId_N).
	[JsonInclude]
	[JsonPropertyName("creationOrder")]
	public long CreationOrder { get; internal set; }

	[JsonConstructor]
	public BeastSession(
		string id,
		string displayName,
		string model,
		string role,
		string terminatorName,
		int outputBudgetTokens,
		List<CanonicalMessage> messages,
		TokenUsageInfo? lastTokenUsage,
		decimal totalCost,
		int cumulativeInputTokens,
		int cumulativeOutputTokens,
		int currentContextSize,
		bool ephemeral)
	{
		Id = id;
		DisplayName = displayName;
		Model = model;
		Role = role;
		// Null-coalesced so session files written before the obligation existed still load.
		TerminatorName = terminatorName ?? string.Empty;
		OutputBudgetTokens = outputBudgetTokens;
		Messages = messages ?? new List<CanonicalMessage>();
		LastTokenUsage = lastTokenUsage;
		TotalCost = totalCost;
		CumulativeInputTokens = cumulativeInputTokens;
		CumulativeOutputTokens = cumulativeOutputTokens;
		CurrentContextSize = currentContextSize;
		Ephemeral = ephemeral;
		MaxWorkTurns = 0;
		ChildCounter = 0;
		TerminalStatus = "Ongoing";
		CreationOrder = 0;
	}
}