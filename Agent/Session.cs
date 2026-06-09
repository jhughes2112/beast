using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;


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

    private readonly ConcurrentQueue<string> _inputQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _commandQueue = new ConcurrentQueue<string>();
    private CancellationTokenSource? _turnCts;
    private bool _isSubagent;

    private readonly ConcurrentDictionary<string, Session> _children = new ConcurrentDictionary<string, Session>();

    public QueryLogger QueryLog { get; }

    // Set when a turn is interrupted; cleared when new user text arrives via AddUserMessage.
    // NeedsAttention() returns false while set so the loop waits instead of re-running immediately.
    private bool _interruptedAndWaiting = false;

    // Expose raw data only for persistence (SessionService.Save). All other access goes through
    // the typed properties and methods below.
    public BeastSession Data => _data;

    public string Id => _data.Id;
    public string DisplayName => _data.DisplayName;
    public string Model => _data.Model;
    public string Role => _data.Role;
    public bool Ephemeral => _data.Ephemeral;
    public bool IsSubagent => _isSubagent;
    public bool IsEmpty => string.IsNullOrEmpty(_data.DisplayName);
    public int ContextLength => _data.CurrentContextSize;
    public TokenUsageInfo? LastTokenUsage => _data.LastTokenUsage;
    public decimal TotalCost => _data.TotalCost;
    public int CumulativeInputTokens => _data.CumulativeInputTokens;
    public int CumulativeOutputTokens => _data.CumulativeOutputTokens;

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
    public string AllocateChildId() => $"{_data.Id}_{++_data.ChildCounter}";

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
            _transport.Idle(_data.Id);
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
    public void UpdateModel(string model) => _data.Model = model;

    public void UpdateRole(string role) => _data.Role = role;

    // Signals the bundle that the active protocol should be discarded on next turn.
    public void InvalidateProtocol() => _bundle.InvalidateProtocol();

    // Commits one turn's usage and cost into the monotonic session totals.
    // Called by LlmService after a successful protocol call.
    public void RecordTurnUsage(TokenUsageInfo usage, decimal cost, int currentContextSize)
    {
        _data.CumulativeInputTokens += usage.PromptTokens;
        _data.CumulativeOutputTokens += usage.CompletionTokens;
        _data.TotalCost += cost;
        _data.LastTokenUsage = usage;
        _data.CurrentContextSize = currentContextSize;
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
    public void RemoveChild(Session child) => _children.TryRemove(child.Id, out _);

    // Delivers a user message to the session matching targetId, recursing into children.
    // Parallel tool calls can produce multiple simultaneous children; all are searched.
    public void Deliver(string targetId, string text)
    {
        if (targetId == _data.Id)
        {
            AddUserMessage(text);
            return;
        }
        foreach (Session child in _children.Values)
            child.Deliver(targetId, text);
    }

    // Queues input. Commands go to _commandQueue (or fire Interrupt for /cancel); text goes to
    // _inputQueue and clears the interrupted-wait state. Thread-safe.
    public void AddUserMessage(string text)
    {
        if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            Interrupt();
        }
        else if (text.StartsWith("/"))
        {
            _commandQueue.Enqueue(text);
        }
        else
        {
            _interruptedAndWaiting = false;
            _inputQueue.Enqueue(text);
        }
    }

    // Drains the pending message queue into the bundle.
    // Call before saving a session that had AddUserMessage() called outside of a running turn.
    public void FlushPendingMessages()
    {
        while (_inputQueue.TryDequeue(out string? text))
            _bundle.OnUserMessage(text);
    }

    // Clears the conversation history.
    public void Clear()
    {
        _interruptedAndWaiting = false;
        _bundle.OnClear();
    }

    // ---- Replay / hydration ----

    // Hydrates the bundle from a list of stored exchanges (used when building a compacted session).
    // System messages are skipped — they are already applied in the constructor.
    public void ReplayExchanges(IReadOnlyList<CanonicalMessage> exchanges)
    {
        foreach (CanonicalMessage msg in exchanges)
        {
            if (msg is UserMessage um)
                _bundle.OnUserMessage(um.Text);
            else if (msg is AssistantMessage am)
                _bundle.OnAssistantTurn(am.Text, am.Thinking, am.ToolCalls);
            else if (msg is ToolResultMessage tr)
                _bundle.OnToolResult(tr.ToolCallId, new ToolResult(tr.Content, string.Empty, 0));
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
                    _transport.ToolResponseWithId(id, tr.ToolCallId, new ToolResult(tr.Content, string.Empty, 0));
            }
        }
    }

    // ---- Turn lifecycle ----

    // The conversation bundle — passed to LlmService by SessionRunner so it can read/write history.
    public ListenerBundle Bundle => _bundle;

    // Sets the display name from the first user message in history, if not already set.
    public void InferDisplayName()
    {
        if (!string.IsNullOrEmpty(_data.DisplayName)) return;
        string? first = GetFirstUserText();
        if (!string.IsNullOrWhiteSpace(first))
        {
            string name = first.Trim();
            _data.DisplayName = name.Length > 50 ? name.Substring(0, 50) : name;
        }
    }

    // Prepares for a new LLM turn: flushes pending messages and arms the per-turn CTS.
    // Returns the turn-specific cancellation token; always pair with EndTurn.
    // LlmService polls TryGetPendingInput between tool calls to pick up mid-turn user input.
    public CancellationToken BeginTurn()
    {
        FlushPendingMessages();
        _turnCts = new CancellationTokenSource();
        return _turnCts.Token;
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

    // Creates an independent copy of this session from this exact point in history.
    // The fork shares no mutable state with the original — both can run and diverge freely.
    // Messages are immutable so a shallow list copy suffices. System prompt is already in
    // the forked message list; do not re-inject it.
    public Session Fork(string newId, string newDisplayName, bool ephemeral)
    {
        List<CanonicalMessage> forkedMessages = new List<CanonicalMessage>(_data.Messages);
        BeastSession forked = new BeastSession(
            newId,
            newDisplayName,
            _data.Model,
            _data.Role,
            forkedMessages,
            _data.LastTokenUsage,
            _data.TotalCost,
            _data.CumulativeInputTokens,
            _data.CumulativeOutputTokens,
            _data.CurrentContextSize,
            ephemeral,
            0);
        return new Session(forked, string.Empty, _transport, _isSubagent);
    }

    // Cancels the in-progress turn, if any.
    public void Interrupt() => _turnCts?.Cancel();

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
