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

	[JsonPropertyName("contextWindow")]
	public int ContextWindow { get; internal set; }

	[JsonPropertyName("role")]
	public string Role { get; internal set; }

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
	[JsonPropertyName("childCounter")]
	public int ChildCounter { get; internal set; }

	// Persisted termination status: Ongoing, Success, Failure, or Incomplete. Set by the terminal
	// tool handler (return_to_caller, task_complete, finish_review) so reloaded sessions remember
	// how they finished without re-deriving it from code paths.
	[JsonPropertyName("terminalStatus")]
	public string TerminalStatus { get; internal set; }

	// Creation order for sorting: for root sessions, a global counter assigned at creation.
	// For child sessions, the child number (N in parentId_N).
	[JsonPropertyName("creationOrder")]
	public long CreationOrder { get; internal set; }

	[JsonConstructor]
	public BeastSession(
		string id,
		string displayName,
		string model,
		string role,
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
		Messages = messages ?? new List<CanonicalMessage>();
		LastTokenUsage = lastTokenUsage;
		TotalCost = totalCost;
		CumulativeInputTokens = cumulativeInputTokens;
		CumulativeOutputTokens = cumulativeOutputTokens;
		CurrentContextSize = currentContextSize;
		Ephemeral = ephemeral;
		ChildCounter = 0;
		TerminalStatus = "Ongoing";
		CreationOrder = 0;
	}
}