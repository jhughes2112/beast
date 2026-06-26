using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


// Stateful adapter over BeastSession. Owns all mutation of conversation state.
// One Session = one independent conversation thread with its own bundle, queues, and turn CTS.
//
// Callers load/save BeastSession (pure data), wrap it in Session, then talk to Session only.
// SessionRunner drives the LLM; Session provides BeginTurn/EndTurn lifecycle hooks and exposes
// Bundle so SessionRunner can pass it through to LlmService.
// systemPrompt is applied once at construction. For sessions loaded from disk the prompt is
// already in Messages; pass string.Empty to skip re-injection.
public class Session
{
	private readonly BeastSession _data;
	private readonly ListenerBundle _bundle;
	private readonly ITransportServer _transport;
	private readonly ContextBudget _budget = new ContextBudget();

	// Single ordered queue of pending inbound lines: plain user text and slash commands interleaved
	// in arrival order. /cancel is never queued — Deliver handles it immediately. Both the turn-boundary
	// drain and the mid-turn checkpoint pull from this one queue, so commands and steering text are
	// picked up with identical timing.
	private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();
	private readonly SemaphoreSlim _inputSignal = new SemaphoreSlim(0, 1);
	private CancellationTokenSource? _turnCts;
	// The whole-turn cancellation scope owned by the runner. Unlike _turnCts (alive only for the duration of a
	// single LLM call), this spans the entire turn including the tool-dispatch rounds between LLM calls, so a
	// /cancel that arrives while a tool is running still interrupts it instead of being dropped.
	private CancellationTokenSource? _dispatchCts;
	private bool _isSubagent;

	// " (high)" style suffix for the active model's reasoning level, shown after the model name in stats.
	// Set by UpdateModel each turn; empty until the first turn or when thinking is off.
	private string _modelDisplaySuffix = string.Empty;

	private readonly ConcurrentDictionary<string, Session> _children = new ConcurrentDictionary<string, Session>();

	public SessionLogger QueryLog { get; }

	// Set when a turn is interrupted; cleared when new user text arrives via AddUserMessage.
	// NeedsAttention() returns false while set, so the idle loop waits for real new input.
	private bool _interruptedAndWaiting = false;

	// True while the session is in a delegation loop: assign_work sets it, stop_work clears it. While set,
	// the runner re-injects the role's end-of-turn prompt after a no-tool-call turn (so the session is asked
	// to delegate more or stop) and exposes the stop_work tool. Runtime-only state, not persisted; a resumed
	// or switched-in session starts out of the loop, and compaction carries it forward explicitly.
	private bool _workInProgress = false;

	// Expose raw data only for persistence (SessionService.Save). All other access goes through
	// the typed properties and methods below.
	public BeastSession Data => _data;

	public string Id => _data.Id;
	public string DisplayName => _data.DisplayName;
	public string Model => _data.Model;
	public int ContextWindow => _data.ContextWindow;
	public string Role => _data.Role;
	public bool Ephemeral => _data.Ephemeral;
	public bool IsSubagent => _isSubagent;
	public bool IsEmpty => string.IsNullOrEmpty(_data.DisplayName);
	public int ContextLength => _data.CurrentContextSize;
	public TokenUsageInfo? LastTokenUsage => _data.LastTokenUsage;
	public decimal TotalCost => _data.TotalCost;
	public int CumulativeInputTokens => _data.CumulativeInputTokens;
	public int CumulativeOutputTokens => _data.CumulativeOutputTokens;

	// Central token accounting for the context window. LlmService configures it at turn start and
	// queries it for completion sizing and tool-response reservations.
	public ContextBudget Budget => _budget;

	// Sends current session stats to the client using explicit values.
	public void SendStats()
	{
		_transport.Stats(_data.Id, _data.Model + _modelDisplaySuffix, _data.Role,
			_data.CumulativeInputTokens, _data.CumulativeOutputTokens,
			_data.TotalCost, _data.ContextWindow, _data.CurrentContextSize);
	}

	public Session(BeastSession data, string systemPrompt, ITransportServer transport, bool isSubagent)
	{
		_data = data;
		_transport = transport;
		_isSubagent = isSubagent;
		QueryLog = new SessionLogger(data.Id);
		_bundle = new ListenerBundle(
			new CanonicalConversation(data.Messages),
			new ListenerTransport(_transport, data.Id));
		// Persist the system prompt to canonical only — never emit it to the transport here. Display is
		// owned by ReplayToTransport (called on every display path), so emitting at construction too would
		// show the prompt twice on a fresh conversation. The active protocol rehydrates it from canonical.
		if (!string.IsNullOrEmpty(systemPrompt))
			_bundle.Canonical.OnSystemMessage(systemPrompt);
	}

	// ---- Child ID allocation ----

	// Allocates a unique child session ID rooted at this session's ID.
	// IDs form a path: "parentId_N" where N increments from 1.
	public string AllocateChildId()
	{
		int n = Interlocked.Increment(ref _data.ChildCounter);
		return $"{_data.Id}_{n}";
	}

	// ---- Busy/Idle signaling ----

	// Reference count: Busy fires on 0→1, Idle fires on 1→0.
	// Safe for concurrent callers (parallel tool calls adding/removing children).
	private int _busyCount;

	public void SendBusy()
	{
		if (Interlocked.Increment(ref _busyCount) == 1)
			_transport.Busy(_data.Id);
	}

	public void SendIdle()
	{
		if (Interlocked.Decrement(ref _busyCount) == 0)
			_transport.Idle(_data.Id, _isSubagent);
	}

	public void AnnounceToClient()
	{
		InferDisplayName();
		if (string.IsNullOrEmpty(_data.DisplayName))
			return;
		string json = JsonSerializer.Serialize(new { id = _data.Id, name = _data.DisplayName });
		_transport.SessionAnnounce(_data.Id, json);
	}

	// Names a still-nameless root from its first user message and announces it immediately, so the
	// client shows the name from the first turn rather than the raw session ID for its whole duration
	// (the name was previously only set when the turn finished and the session was saved). Flushing the
	// pending input here is safe: a nameless session has no prior assistant turn, so there are no
	// dangling tool calls to order the user message against. A no-op once the session already has a name.
	public void EnsureNamedAndAnnounce()
	{
		if (!string.IsNullOrEmpty(_data.DisplayName))
			return;
		FlushPendingMessages();
		AnnounceToClient();
	}

	// ---- Work loop ----

	// True while a delegation loop is active (assign_work called, stop_work not yet). Read by the runner to
	// decide whether to re-prompt with the end-of-turn prompt and whether to expose stop_work.
	public bool WorkInProgress => _workInProgress;

	// Enters the delegation loop. Called from the assign_work tool handler.
	public void BeginWork() => _workInProgress = true;

	// Leaves the delegation loop. Called from the stop_work tool handler.
	public void EndWork() => _workInProgress = false;

	// ---- Mutation ----

	// Sets the active model name. Call InvalidateProtocol() separately if the model switch
	// requires discarding the in-progress protocol (e.g. via the /model command).
	public void UpdateModel(LlmModel model) { _data.Model = model.ConfigId; _data.ContextWindow = model.Config.ContextWindow; _modelDisplaySuffix = ReasoningEffort.DisplaySuffix(model.Config.ReasoningEffort); }

	// Signals the bundle that the active protocol should be discarded on next turn.
	public void InvalidateProtocol() => _bundle.InvalidateProtocol();

	// Commits the full assistant turn from a successful ProtocolCallPayload to both canonical
	// storage and the active protocol's native state. Handles the assistant message (text,
	// thinking, tool calls) plus any tool results that came back with this payload. This is the
	// single entry point for turning an in-flight provider response into committed session state.
	public void CommitAssistantTurn(ProtocolCallPayload payload)
	{
		_bundle.OnAssistantTurn(payload.AssistantText, payload.Thinking, payload.ToolCalls);

		// RecordTurnUsage feeds the reported size into the budget (ContextBudget.RecordMeasurement),
		// which resets pending reservations: the size already includes any prior tool outputs.
		RecordTurnUsage(payload.Usage);
	}

	public void CommitToolResults(ProtocolCallPayload payload)
	{
		foreach (ToolResult result in payload.ToolResults)
		{
			_bundle.OnToolResult(result);
		}
	}

	// Guards TotalCost: a single turn records its own cost, but parallel subagent tool calls roll
	// their spend up into the same parent session concurrently after their rounds complete.
	private readonly object _costLock = new object();

	// Commits cost into the session total. Called by LlmService immediately on success so cost is
	// accurate before the caller commits the assistant turn, and by SubagentRunner to roll a
	// completed sub-session's spend up into the calling agent.
	public void RecordCost(decimal cost)
	{
		lock (_costLock)
			_data.TotalCost += cost;
	}

	// Commits one turn's usage and cost into the monotonic session totals.
	internal void RecordTurnUsage(TokenUsageInfo usage)
	{
		_data.CumulativeInputTokens += usage.PromptTokens;
		_data.CumulativeOutputTokens += usage.CompletionTokens;
		_data.LastTokenUsage = usage;
		// The context size is exactly the tokens the provider processed this turn: the whole prompt it
		// read plus the completion it produced. That is the conversation's true size going into the
		// next turn, and it already includes any tool outputs appended since the last response, so the
		// budget's pending reservations are now fully accounted for.
		_data.CurrentContextSize = usage.PromptTokens + usage.CompletionTokens;
		_budget.RecordMeasurement(_data.CurrentContextSize);
	}

	// ---- Attention / input ----

	public bool NeedsAttention() => !_pending.IsEmpty || (!_interruptedAndWaiting && NeedsLlmAttention());

	public string? GetLastAssistantText() => _bundle.GetLastAssistantText();

	// Drains and merges consecutive leading plain-text lines from the pending queue, stopping at the
	// first queued slash command (left in place so the boundary drain applies it in arrival order).
	// Returns the merged text, or null when the queue is empty or its head is a command. Called at each
	// mid-turn checkpoint so steering text is injected the instant the in-flight tool round commits.
	public string? TryDequeueLeadingText()
	{
		string accumulated = string.Empty;
		while (_pending.TryPeek(out string? line))
		{
			if (line!.StartsWith("/", StringComparison.Ordinal))
				break;
			_pending.TryDequeue(out string? _);
			accumulated = accumulated.Length == 0 ? line : accumulated + "\n" + line;
		}
		return accumulated.Length == 0 ? null : accumulated;
	}

	// True while any line remains queued. After TryDequeueLeadingText returns, a true here means the
	// head is a slash command, so the caller ends the turn to let the boundary drain apply it.
	public bool HasPending => !_pending.IsEmpty;

	// Dequeues the next pending line in arrival order (plain text or slash command), or returns false
	// when the queue is empty. The boundary drain uses this to process everything that is waiting.
	public bool TryDequeuePending(out string? line) => _pending.TryDequeue(out line);

	public void AddChild(Session child) => _children.TryAdd(child.Id, child);

	// Routes incoming text to the correct session by ID.
// - If targetId matches this session (targetId == _data.Id):
//     /cancel is special: it interrupts the session immediately instead of being queued.
//     Everything else (plain text and slash commands alike) goes to the pending queue
//     in arrival order, to be drained at the next turn boundary or mid-turn checkpoint.
// - If targetId is a descendant (targetId.StartsWith(_data.Id + "_", ...)):
//     ALL inputs are forwarded through unchanged to the child subtree. The only special
//     casing for /cancel happens at the target session itself — when the recursion lands
//     on the session whose Id == targetId, the targetId == _data.Id branch above handles
//     /cancel by calling Interrupt() on that (sub)session. No filtering is applied here.
	public void Deliver(string targetId, string text)
	{
		if (targetId == _data.Id)
		{
			if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
			{
				Interrupt();
			}
			else if (text.StartsWith("/"))
			{
				_pending.Enqueue(text);
				Signal();
			}
			else
			{
				AddUserMessage(text);
			}
			return;
		}
		if (!targetId.StartsWith(_data.Id + "_", StringComparison.Ordinal))
			return;
		foreach (Session child in _children.Values)
			child.Deliver(targetId, text);
	}

	// Enqueues plain user text, clears the interrupted-wait state, and wakes the idle loop.
	public void AddUserMessage(string text)
	{
		_interruptedAndWaiting = false;
		_pending.Enqueue(text);
		Signal();
	}

	// Signals the idle loop that there is work to do. Capped at 1 so rapid calls do not
	// accumulate; the loop consumes exactly one permit per iteration and re-evaluates state.
	// Concurrent producers (transport thread + runner thread) can race past the count check,
	// so an over-release is swallowed — the permit is already pending either way.
	private void Signal()
	{
		if (_inputSignal.CurrentCount == 0)
		{
			try
			{
				_inputSignal.Release();
			}
			catch (SemaphoreFullException)
			{
			}
		}
	}

	// Returns a task that completes when input or a command arrives, or when ct is cancelled.
	// Used by SessionRunner to break out of the inter-turn delay early.
	public Task WaitForInputAsync(CancellationToken ct) => _inputSignal.WaitAsync(ct);

	// Drains leading plain-text lines from the pending queue into the bundle, stopping at the first
	// queued slash command (left for the boundary drain to apply in order). Call before saving a
	// session that had AddUserMessage() called outside of a running turn.
	public void FlushPendingMessages()
	{
		while (_pending.TryPeek(out string? line))
		{
			if (line!.StartsWith("/", StringComparison.Ordinal))
				break;
			_pending.TryDequeue(out string? _);
			_bundle.OnUserMessage(line);
		}
	}

	// ---- Replay / hydration ----

	// Hydrates the conversation from a list of stored exchanges (used when building a compacted
	// session and when adopting a fork's result). System messages are skipped — they are already
	// applied in the constructor. toClient=false writes only the canonical record: used when the
	// client already watched the same content stream in under this session's ID via a fork.
	public void ReplayExchanges(IReadOnlyList<CanonicalMessage> exchanges, bool toClient)
	{
		foreach (CanonicalMessage msg in exchanges)
		{
			if (msg is UserMessage um)
			{
				if (toClient)
					_bundle.OnUserMessage(um.Text);
				else
					_bundle.Canonical.OnUserMessage(um.Text);
			}
			else if (msg is AssistantMessage am)
			{
				if (toClient)
					_bundle.OnAssistantTurn(am.Text, am.Thinking, am.ToolCalls);
				else
					_bundle.Canonical.OnAssistantTurn(am.Text, am.Thinking, am.ToolCalls);
			}
			else if (msg is ToolResultMessage tr)
			{
				if (toClient)
					_bundle.OnToolResult(new ToolResult(tr.ToolCallId, tr.Content, string.Empty, 0, 0));
				else
					_bundle.Canonical.OnToolResult(new ToolResult(tr.ToolCallId, tr.Content, string.Empty, 0, 0));
			}
		}
	}

	// Sends the full conversation history to the transport (for display on client reconnect).
	public void ReplayToTransport()
	{
		string id = _data.Id;
		foreach (CanonicalMessage msg in _data.Messages)
		{
			if (msg is SystemMessage sm)
			{
				if (!string.IsNullOrEmpty(sm.Text))
					_transport.System(id, sm.Text);
			}
			else if (msg is UserMessage um)
			{
				if (!string.IsNullOrEmpty(um.Text))
					_transport.User(id, um.Text);
			}
			else if (msg is AssistantMessage am)
			{
				if (!string.IsNullOrWhiteSpace(am.Thinking))
					_transport.Thinking(id, am.Thinking);
				// Skip whitespace-only assistant text so replaying history never reproduces a blank output
				// block for a turn that was really just thinking plus a tool call.
				if (!string.IsNullOrWhiteSpace(am.Text))
					_transport.Output(id, am.Text);
				foreach (SemanticToolCall tc in am.ToolCalls)
					_transport.ToolCallWithId(id, tc.Id, tc.Name + "(" + tc.ArgumentsJson + ")");
			}
			else if (msg is ToolResultMessage tr)
			{
				if (!string.IsNullOrEmpty(tr.Content))
					_transport.ToolResponseWithId(id, new ToolResult(tr.ToolCallId, tr.Content, string.Empty, 0, 0));
			}
		}
	}

	// ---- Turn lifecycle ----

	// The conversation bundle — passed to LlmService by SessionRunner so it can read/write history.
	public ListenerBundle Bundle => _bundle;

	// Sets the display name to "{Role} {first user message}", if not already set. Subagent and helper
	// sessions set their own name at creation, so this only ever names a root agent session.
	// Returns true if the name was newly assigned (so the caller can announce it to the client).
	public bool InferDisplayName()
	{
		if (!string.IsNullOrEmpty(_data.DisplayName))
			return false;
		foreach (CanonicalMessage msg in _data.Messages)
		{
			if (msg is UserMessage um && !string.IsNullOrWhiteSpace(um.Text))
			{
				string name = um.Text.Trim();
				if (name.Length > 50)
					name = name.Substring(0, 50);
				// Prefix the role so the session tree reads "{Role} {first message}" (e.g. "Task Add dark mode").
				_data.DisplayName = string.IsNullOrEmpty(_data.Role) ? name : $"{_data.Role} {name}";
				return true;
			}
		}
		return false;
	}

	// Prepares for a new LLM turn: repairs dangling tool calls, flushes pending messages, and
	// arms the per-turn CTS. Returns the turn-specific cancellation token; always pair with
	// EndTurn. The dispatch loop polls TryDequeueLeadingText between tool rounds to pick up mid-turn input.
	public CancellationToken BeginTurn()
	{
		// Repair before flushing so synthesized results land directly after their assistant
		// turn, ahead of any newly queued user text.
		CompleteDanglingToolCalls();
		FlushPendingMessages();
		_turnCts = new CancellationTokenSource();
		return _turnCts.Token;
	}

	// Synthesizes an error result for any assistant tool call that never received one — the turn
	// was interrupted mid-round, or the app shut down and the session was reloaded. Without this
	// the next request would carry tool calls with no matching tool results, which providers
	// reject or models misread.
	private void CompleteDanglingToolCalls()
	{
		System.Collections.Generic.HashSet<string> satisfiedIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in _data.Messages)
		{
			if (msg is ToolResultMessage tr)
				satisfiedIds.Add(tr.ToolCallId);
		}

		// Collect first: OnToolResult appends to the list being scanned.
		System.Collections.Generic.List<string> danglingIds = new System.Collections.Generic.List<string>();
		foreach (CanonicalMessage msg in _data.Messages)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					if (!satisfiedIds.Contains(tc.Id))
						danglingIds.Add(tc.Id);
				}
			}
		}

		foreach (string id in danglingIds)
			_bundle.OnToolResult(new ToolResult(id, string.Empty, "Tool call was interrupted before it completed.", 1, 0));
	}

	// Cleans up after a turn. If interrupted, sets the wait state so NeedsAttention() stays
	// false until new user text arrives via AddUserMessage.
	public void EndTurn(bool interrupted)
	{
		_turnCts?.Dispose();
		_turnCts = null;
		if (interrupted)
			_interruptedAndWaiting = true;
	}

	// Creates an independent ephemeral copy of this session from this exact point in history.
	// The fork keeps this session's ID, so everything it streams renders in this session's
	// client view as if this session were running the turn — forks are never announced, saved,
	// or added to the session tree. The fork shares no mutable conversation state with the
	// original; messages are immutable so a shallow list copy suffices. System prompt is
	// already in the forked message list; do not re-inject it.
	public Session Fork()
	{
		List<CanonicalMessage> forkedMessages = new List<CanonicalMessage>(_data.Messages);
		BeastSession forked = new BeastSession(
			_data.Id,
			_data.DisplayName,
			_data.Model,
			_data.Role,
			forkedMessages,
			_data.LastTokenUsage,
			_data.TotalCost,
			_data.CumulativeInputTokens,
			_data.CumulativeOutputTokens,
			_data.CurrentContextSize,
			true,
			0);
		Session fork = new Session(forked, string.Empty, _transport, _isSubagent);
		return fork;
	}

	// Cancels the in-progress turn, if any. Both scopes are cancelled: _turnCts catches an LLM call that is
	// streaming right now, and _dispatchCts catches a tool that is running between LLM calls (when _turnCts is
	// null). Either may be null; cancelling both makes /cancel take effect immediately whatever is in flight.
	public void Interrupt()
	{
		_turnCts?.Cancel();
		_dispatchCts?.Cancel();
	}

	// Registers (or clears) the runner's whole-turn cancellation scope so Interrupt can reach a running tool.
	public void SetDispatchScope(CancellationTokenSource? cts)
	{
		_dispatchCts = cts;
	}

	// Marks the turn interrupted so the loop idles until new user text arrives. Used when a /cancel lands
	// during tool dispatch (no LLM call is active to run EndTurn).
	public void MarkInterrupted()
	{
		_interruptedAndWaiting = true;
	}

	// ---- Private logic ----

	private bool NeedsLlmAttention()
	{
		IReadOnlyList<CanonicalMessage> messages = _data.Messages;
		if (messages.Count == 0)
			return false;

		if (messages[messages.Count - 1] is UserMessage)
			return true;

		// Collect all tool result IDs present.
		System.Collections.Generic.HashSet<string> satisfiedIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is ToolResultMessage tr)
				satisfiedIds.Add(tr.ToolCallId);
		}

		// Check if any assistant tool call lacks a result.
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					if (!satisfiedIds.Contains(tc.Id))
						return true;
				}
			}
		}

		return false;
	}

	// Produces a display name for a compacted continuation: strips any existing "(N) " prefix,
	// then prepends "(N+1) " so the chain reads "(1) hello", "(2) hello", etc.
	internal static string IncrementDisplayName(string displayName)
	{
		string base_ = displayName;
		int generation = 1;

		if (displayName.Length > 3 && displayName[0] == '(')
		{
			int close = displayName.IndexOf(')');
			if (close > 1 && int.TryParse(displayName.Substring(1, close - 1), out int n))
			{
				generation = n + 1;
				base_ = displayName.Substring(close + 1).TrimStart();
			}
		}

		return $"({generation:D2}) {base_}";
	}
}