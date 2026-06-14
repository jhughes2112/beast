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

    private readonly ConcurrentQueue<string> _inputQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _commandQueue = new ConcurrentQueue<string>();
    private readonly SemaphoreSlim _inputSignal = new SemaphoreSlim(0, 1);
    private CancellationTokenSource? _turnCts;
    private bool _isSubagent;

    private readonly ConcurrentDictionary<string, Session> _children = new ConcurrentDictionary<string, Session>();

    public QueryLogger QueryLog { get; }

    // Set when a turn is interrupted; cleared when new user text arrives via AddUserMessage.
    // NeedsAttention() returns false while set, so the idle loop waits for real new input.
    private bool _interruptedAndWaiting = false;

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
        _transport.Stats(_data.Id, _data.Model, _data.Role,
            _data.CumulativeInputTokens, _data.CumulativeOutputTokens,
            _data.TotalCost, _data.ContextWindow, _data.CurrentContextSize);
    }

    public Session(BeastSession data, string systemPrompt, ITransportServer transport, bool isSubagent)
    {
        _data = data;
        _transport = transport;
        _isSubagent = isSubagent;
        QueryLog = new QueryLogger(data.Id);
        _bundle = new ListenerBundle(
            new CanonicalConversation(data.Messages),
            new ListenerTransport(_transport, data.Id));
        if (!string.IsNullOrEmpty(systemPrompt))
            _bundle.OnSystemMessage(systemPrompt);
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
        if (string.IsNullOrEmpty(_data.DisplayName)) return;
        string json = JsonSerializer.Serialize(new { id = _data.Id, name = _data.DisplayName });
        _transport.SessionAnnounce(_data.Id, json);
    }

    // ---- Mutation ----

    // Sets the active model name. Call InvalidateProtocol() separately if the model switch
    // requires discarding the in-progress protocol (e.g. via the /model command).
    public void UpdateModel(LlmModel model) { _data.Model = model.ConfigId; _data.ContextWindow = model.Config.ContextWindow;  }

    public void UpdateRole(string role) => _data.Role = role;

    // Signals the bundle that the active protocol should be discarded on next turn.
     public void InvalidateProtocol() => _bundle.InvalidateProtocol();

     // Commits the full assistant turn from a successful ProtocolCallPayload to both canonical
        // storage and the active protocol's native state. Handles the assistant message (text,
        // thinking, tool calls) plus any tool results that came back with this payload. This is the
        // single entry point for turning an in-flight provider response into committed session state.
           public void CommitAssistantTurn(ProtocolCallPayload payload)
           {
               _bundle.OnAssistantTurn(payload.AssistantText, payload.Thinking, payload.ToolCalls);
           }

     // Commits one turn's usage and cost into the monotonic session totals.
     // Called by LlmService after a successful protocol call.
    public void RecordTurnUsage(TokenUsageInfo usage, decimal cost, int currentContextSize)
    {
        _data.CumulativeInputTokens += usage.PromptTokens;
        _data.CumulativeOutputTokens += usage.CompletionTokens;
        _data.TotalCost += cost;
        _data.LastTokenUsage = usage;
        _data.CurrentContextSize = currentContextSize;
        // The reported size already includes any tool outputs appended since the last response, so
        // the budget's pending reservations are now fully accounted for.
        _budget.RecordMeasurement(currentContextSize);
    }

    // ---- Attention / input ----

    public bool NeedsAttention() => !_commandQueue.IsEmpty || (!_interruptedAndWaiting && (NeedsLlmAttention() || !_inputQueue.IsEmpty));

    public string? GetLastAssistantText() => _bundle.GetLastAssistantText();

    // Called by LlmService between tool calls to pick up any user input that arrived mid-turn.
    // Input is delivered directly via Deliver(), so no drain action is needed here.
    public string? TryGetPendingInput()
    {
        if (_inputQueue.IsEmpty) return null;
        string accumulated = string.Empty;
        while (_inputQueue.TryDequeue(out string? line))
            accumulated = string.IsNullOrEmpty(accumulated) ? line : accumulated + "\n" + line;
        return string.IsNullOrEmpty(accumulated) ? null : accumulated;
    }

    // Dequeues one pending command from the command queue, or returns false if none.
    public bool TryDequeueCommand(out string? command) => _commandQueue.TryDequeue(out command);

    public void AddChild(Session child) => _children.TryAdd(child.Id, child);

    // Routes incoming text to the correct session. Commands targeting this session are parsed
    // here: /cancel interrupts, other /commands go to the command queue, plain text goes to
    // AddUserMessage. Routing to children only forwards /cancel and plain text — non-cancel
    // commands are never forwarded because child command queues are never drained externally.
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
                _commandQueue.Enqueue(text);
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
        if (text.StartsWith("/") && !text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
            return;
        foreach (Session child in _children.Values)
            child.Deliver(targetId, text);
    }

    // Enqueues plain user text, clears the interrupted-wait state, and wakes the idle loop.
    public void AddUserMessage(string text)
    {
        _interruptedAndWaiting = false;
        _inputQueue.Enqueue(text);
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

    // Drains the pending message queue into the bundle.
    // Call before saving a session that had AddUserMessage() called outside of a running turn.
    public void FlushPendingMessages()
    {
        while (_inputQueue.TryDequeue(out string? text))
            _bundle.OnUserMessage(text);
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
                if (toClient) _bundle.OnUserMessage(um.Text);
                else _bundle.Canonical.OnUserMessage(um.Text);
            }
            else if (msg is AssistantMessage am)
            {
                if (toClient) _bundle.OnAssistantTurn(am.Text, am.Thinking, am.ToolCalls);
                else _bundle.Canonical.OnAssistantTurn(am.Text, am.Thinking, am.ToolCalls);
            }
            else if (msg is ToolResultMessage tr)
            {
                if (toClient) _bundle.OnToolResult(new ToolResult(tr.ToolCallId, tr.Content, string.Empty, 0, 0));
                else _bundle.Canonical.OnToolResult(new ToolResult(tr.ToolCallId, tr.Content, string.Empty, 0, 0));
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
                if (!string.IsNullOrEmpty(am.Thinking))
                    _transport.Thinking(id, am.Thinking);
                if (!string.IsNullOrEmpty(am.Text))
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

    // Sets the display name from the first user message in history, if not already set.
    // Returns true if the name was newly assigned (so the caller can announce it to the client).
    public bool InferDisplayName()
    {
        if (!string.IsNullOrEmpty(_data.DisplayName)) return false;
        string? first = GetFirstUserText();
        if (!string.IsNullOrWhiteSpace(first))
        {
            string name = first.Trim();
            _data.DisplayName = name.Length > 50 ? name.Substring(0, 50) : name;
            return true;
        }
        return false;
    }

    // Prepares for a new LLM turn: repairs dangling tool calls, flushes pending messages, and
    // arms the per-turn CTS. Returns the turn-specific cancellation token; always pair with
    // EndTurn. LlmService polls TryGetPendingInput between tool calls to pick up mid-turn input.
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
        _activeFork = fork;
        return fork;
    }

    // The most recent fork of this session. Forks share this session's ID, so a /cancel routed
    // here by ID must also reach the fork actually running the turn. A stale fork is harmless
    // to interrupt — its turn CTS is already gone.
    private Session? _activeFork;

    // Cancels the in-progress turn, if any — including a turn running in a same-ID fork.
    public void Interrupt()
    {
        _turnCts?.Cancel();
        _activeFork?.Interrupt();
    }

    // ---- Private logic ----

    private bool NeedsLlmAttention()
    {
        IReadOnlyList<CanonicalMessage> messages = _data.Messages;
        if (messages.Count == 0) return false;

        if (messages[messages.Count - 1] is UserMessage) return true;

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
                    if (!satisfiedIds.Contains(tc.Id)) return true;
                }
            }
        }

        return false;
    }

    private string? GetFirstUserText()
    {
        foreach (CanonicalMessage msg in _data.Messages)
        {
            if (msg is UserMessage um && !string.IsNullOrWhiteSpace(um.Text))
                return um.Text;
        }
        return null;
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
