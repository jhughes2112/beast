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

	// Last known termination status. Ongoing while busy; updated by SetTerminationStatus.
	// Initialized from BeastSession.TerminalStatus so reloaded sessions reflect their prior state.
	private SessionStatus _status = SessionStatus.Ongoing;

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

	private readonly Dictionary<string, Session> _children = new Dictionary<string, Session>(StringComparer.Ordinal);

	public SessionLogger QueryLog { get; }

	// Set when a turn is interrupted; cleared when new user text arrives via AddUserMessage.
	// NeedsAttention() returns false while set, so the idle loop waits for real new input.
	private bool _interruptedAndWaiting = false;

	// True while the session is in a delegation loop: assign_work sets it, stop_work clears it. While set,
	// the runner re-injects the role's end-of-turn prompt after a no-tool-call turn (so the session is asked
	// to delegate more or stop) and exposes the stop_work tool. Runtime-only state, not persisted; a resumed
	// or switched-in session starts out of the loop, and compaction carries it forward explicitly.
	private bool _workInProgress = false;

	// Tracks the model the user explicitly selected via /model. Null means "use whatever the
	// system picks" (fresh session, resume, or after /clear). While set, system paths (fallback,
	// refresh) must not overwrite it — only a new /model command or session rehydration can change it.
	private string? _userSelectedModel;

	// Set by the orchestrator's /reload command to signal the handler to re-fetch its role and
	// recreate its LlmService on the next loop iteration. Checked in DrainInput, cleared after
	// processing. The flag survives the idle wait because it is not tied to the pending queue.
	private bool _needsRefresh;

	// Last completions payload sent; compared each update to suppress redundant frames.
	private string _completionsJson = string.Empty;

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

	// The status the client should display: a session that has not terminated but still owes its
	// caller a reply reads as Working, distinct from a free-floating Ongoing conversation.
	private SessionStatus EffectiveStatus => _status == SessionStatus.Ongoing && OwesReply ? SessionStatus.Working : _status;

	// Sends current session stats and termination status to the client.
	public void SendStats()
	{
		int cachedTokens = _data.LastTokenUsage?.CachedTokens ?? 0;
		_transport.Stats(_data.Id, _data.Model + _modelDisplaySuffix, _data.Role,
			_data.CumulativeInputTokens, _data.CumulativeOutputTokens,
			_data.TotalCost, _data.ContextWindow, _data.CurrentContextSize, cachedTokens);
		_transport.SessionStatus(_data.Id, EffectiveStatus.ToString());
	}

	// Updates the tab-completion candidates. Serializes to JSON and sends over transport only when
	// the candidate list has changed since the last call.
	public void UpdateCompletions(List<string> candidates)
	{
		string json = System.Text.Json.JsonSerializer.Serialize(candidates);
		if (string.Equals(json, _completionsJson, StringComparison.Ordinal))
			return;
		_completionsJson = json;
		_transport.Completions(_data.Id, json);
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
		if (Enum.TryParse(data.TerminalStatus, out SessionStatus persisted))
			_status = persisted;
	}

	// Monotonically increasing counter for child session IDs, rooted at this session's ID.
	// Session.AllocateChildId() increments this and returns "{Id}_{n}".
	// Backed by BeastSession.ChildCounter so the counter survives reload — a re-entered worktree
	// must not reuse a child ID that was already issued to another session.
	private int _childCounter;

	// ---- Child ID allocation ----

	// Guards allocation so the persisted counter only ever moves forward: with a bare interlocked
	// increment, two parallel allocations could write ChildCounter back in reverse order (2 then 1),
	// and a reload would re-issue an ID already given out.
	private readonly object _childIdLock = new object();

	// Allocates a unique child session ID rooted at this session's ID.
	// IDs form a path: "parentId_N" where N increments from 1.
	public string AllocateChildId()
	{
		int n;
		lock (_childIdLock)
		{
			// Seed from the persisted counter so a reloaded session continues past any IDs already issued.
			if (_data.ChildCounter > _childCounter)
				_childCounter = _data.ChildCounter;
			n = ++_childCounter;
			_data.ChildCounter = n;
		}
		return $"{_data.Id}_{n}";
	}

	// ---- Handler attachment ----

	// At most one SessionHandler may drive this session at a time. Every handler-start path claims
	// the session via TryAttachHandler first; the handler detaches on exit (or hands attachment to
	// its compaction successor). The orchestrator uses this to revive a dormant session — one whose
	// handler has exited — with a fresh handler when new input arrives for it.
	private int _handlerAttached;

	public bool TryAttachHandler() => Interlocked.CompareExchange(ref _handlerAttached, 1, 0) == 0;

	public void DetachHandler() => Interlocked.Exchange(ref _handlerAttached, 0);

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

	// Reports the session's termination status to the client and persists it so reloaded sessions
	// reflect how they finished. Called by SessionHandler.NotifyComplete when the caller's reply is
	// delivered (terminator, failure report, or salvage), and by the restore pass (Incomplete).
	public void SetTerminationStatus(SessionStatus status)
	{
		_status = status;
		_data.TerminalStatus = status.ToString();
		_transport.SessionStatus(_data.Id, status.ToString());
	}

	// Resets the session back to Ongoing, clearing the persisted terminal status. Called when
	// new user input arrives on a completed session so it can run again. Reported through
	// EffectiveStatus so a session that still owes its reply reads as Working, not Ongoing.
	public void ResumeFromComplete()
	{
		_status = SessionStatus.Ongoing;
		_data.TerminalStatus = string.Empty;
		_transport.SessionStatus(_data.Id, EffectiveStatus.ToString());
	}

	public SessionStatus Status => _status;

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

	// ---- Reply obligation ----

	// The terminator tool this session must call to answer the caller that spawned it; empty when
	// no caller is waiting. Lives on the persisted data so it survives a save/load cycle and can
	// be handed to a compaction successor.
	public string TerminatorName => _data.TerminatorName;

	// Token budget for the terminator reply. 0 = no limit.
	public int OutputBudgetTokens => _data.OutputBudgetTokens;

	// Working-turn budget before wind-down forces the terminator. 0 = unlimited. Part of the
	// reply obligation: set at spawn, handed to a compaction successor, cleared with the reply.
	public int MaxWorkTurns => _data.MaxWorkTurns;

	public void SetMaxWorkTurns(int maxWorkTurns) => _data.MaxWorkTurns = maxWorkTurns;

	// True while this session may still respond to its caller as a tool.
	public bool OwesReply => !string.IsNullOrEmpty(_data.TerminatorName);

	// Ends this session's ability to respond as a tool: called once the reply (or failure report)
	// has been delivered, or when compaction hands the obligation to a successor. The session
	// remains viable for conversation afterwards, with no budgets applying. Pushes the status to
	// the client since Working derives from the obligation that just cleared.
	public void ClearReplyObligation()
	{
		_data.TerminatorName = string.Empty;
		_data.OutputBudgetTokens = 0;
		_data.MaxWorkTurns = 0;
		_transport.SessionStatus(_data.Id, EffectiveStatus.ToString());
	}

	// ---- Mutation ----

	// Sets the active model name. Call InvalidateProtocol() separately if the model switch
	// requires discarding the in-progress protocol (e.g. via the /model command).
	public void UpdateModel(LlmModel model) { _data.Model = model.ConfigId; _data.ContextWindow = model.Config.ContextWindow; _modelDisplaySuffix = ReasoningEffort.DisplaySuffix(model.Config.ReasoningEffort); }

	// Tracks the model the user explicitly selected via /model. Null means "use whatever the
	// system picks" (fresh session, resume, or after /clear). While set, system paths (fallback,
	// refresh) must not overwrite it — only a new /model command or session rehydration can change it.
	public string? UserSelectedModel => _userSelectedModel;

	// Marks the model as user-selected via /model command. System paths (fallback, refresh) respect
	// this and will not overwrite session.Model while it is set. Cleared on /clear, session reset,
	// or when a new /model is issued.
	public void MarkModelUserSelected(string modelId) => _userSelectedModel = modelId;

	// Clears the user-selected model marker. Called on /clear, session reset, or when loading from disk.
	public void ClearUserSelectedModel() => _userSelectedModel = null;

	// Resets all mutable session state. Called when a session is cleared or rehydrated.
	public void Reset() => _userSelectedModel = null;

	// Set by the orchestrator's /reload command to signal the handler to re-fetch its role and
	// recreate its LlmService on the next loop iteration. The handler checks this at the top of
	// its loop and clears it after processing.
	public bool NeedsRefresh => _needsRefresh;

	// Signals the handler to re-fetch the session's role and recreate its LlmService on the next
	// loop iteration. Also wakes the handler if it is idling on the input wait.
	public void RequestRefresh()
	{
		_needsRefresh = true;
		Signal();
	}

	// Clears the refresh flag after the handler has processed it.
	public void ClearRefresh() => _needsRefresh = false;

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
	// accurate before the caller commits the assistant turn, and by SessionHandler to roll a
	// child session's spend up into the calling agent.
	public void RecordCost(decimal cost)
	{
		lock (_costLock)
			_data.TotalCost += cost;
	}

	// Commits one turn's usage and cost into the monotonic session totals.
	internal void RecordTurnUsage(TokenUsageInfo usage)
	{
		// Cumulative input tracks only fresh (non-cached) tokens — cached reads are already "paid for"
		// and don't represent additional spend across turns. PromptTokens is the TOTAL the provider
		// processed (including cached), so subtract CachedTokens to get the fresh portion.
		_data.CumulativeInputTokens += usage.PromptTokens - usage.CachedTokens;
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

	// True while any line remains queued. The handler ends the turn cluster when this is set so the
	// boundary drain applies the queued input in arrival order.
	public bool HasPending => !_pending.IsEmpty;

	// Dequeues the next pending line in arrival order (plain text or slash command), or returns false
	// when the queue is empty. The boundary drain uses this to process everything that is waiting.
	public bool TryDequeuePending(out string? line) => _pending.TryDequeue(out line);

	// Returns a snapshot of everything currently in the pending queue without consuming any items.
	public string[] PeekAllPending() => _pending.ToArray();

	public void AddChild(Session child) => _children[child.Id] = child;

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
	// EndTurn. The handler drains pending input between tool rounds to pick up mid-turn steering.
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
			_data.TerminatorName,
			_data.OutputBudgetTokens,
			forkedMessages,
			_data.LastTokenUsage,
			_data.TotalCost,
			_data.CumulativeInputTokens,
			_data.CumulativeOutputTokens,
			_data.CurrentContextSize,
			true);
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

	// Set once by MarkDeleted when the user deletes this session; never cleared. The handler loop
	// exits on it, and SaveSession refuses to write — a late save would resurrect the files
	// delete-session just removed.
	private volatile bool _deleted;

	public bool Deleted => _deleted;

	// Marks the session deleted and wakes whatever its handler is doing — an in-flight turn is
	// interrupted, and the input wait is released — so the handler observes the flag and exits.
	public void MarkDeleted()
	{
		_deleted = true;
		Interrupt();
		Signal();
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