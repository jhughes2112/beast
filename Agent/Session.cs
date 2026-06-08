using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Stateful adapter over BeastSession. Owns all mutation, logic, and runtime wiring.
// One Session = one independent conversation thread. Sessions can run concurrently without
// sharing any mutable state — each has its own bundle, queue, and cancellation token.
//
// Callers load/save BeastSession (pure data), wrap it in Session, then talk to Session only.
// systemPrompt is applied once at construction. For sessions loaded from disk the prompt is
// already in Messages; pass string.Empty to skip re-injection.
public class Session
{
    private readonly BeastSession _data;
    private readonly ListenerBundle _bundle;
    private readonly ITransportServer _transport;
    private readonly ConcurrentQueue<string> _inputQueue = new ConcurrentQueue<string>();
    private CancellationTokenSource? _turnCts;
    private Action? _midTurnDrain;

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
    public bool IsEmpty => string.IsNullOrEmpty(_data.DisplayName);
    public int ContextLength => _data.CurrentContextSize;
    public TokenUsageInfo? LastTokenUsage => _data.LastTokenUsage;
    public decimal TotalCost => _data.TotalCost;
    public int CumulativeInputTokens => _data.CumulativeInputTokens;
    public int CumulativeOutputTokens => _data.CumulativeOutputTokens;

    public Session(BeastSession data, string systemPrompt, ITransportServer transport)
    {
        _data = data;
        _transport = transport;
        _bundle = new ListenerBundle(
            new CanonicalConversation(data.Messages),
            new ListenerTransport(transport));
        if (!string.IsNullOrEmpty(systemPrompt))
            _bundle.OnSystemMessage(systemPrompt);
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

    public bool NeedsAttention() => !_interruptedAndWaiting && (NeedsLlmAttention() || !_inputQueue.IsEmpty);

    public string? GetLastAssistantText() => _bundle.GetLastAssistantText();

    // Called by LlmService between tool calls to pick up any user input that arrived mid-turn.
    // Runs the mid-turn drain action first (if set) to move external queue items into _inputQueue.
    public string? TryGetPendingInput()
    {
        _midTurnDrain?.Invoke();
        if (_inputQueue.IsEmpty) return null;
        string accumulated = string.Empty;
        while (_inputQueue.TryDequeue(out string? line))
            accumulated = string.IsNullOrEmpty(accumulated) ? line : accumulated + "\n" + line;
        return string.IsNullOrEmpty(accumulated) ? null : accumulated;
    }

    // Queues a plain-text user message. Clears the interrupted-wait state so NeedsAttention()
    // becomes true again and the loop resumes. Thread-safe: the mid-turn drain callback may call
    // this while a turn is running, injecting text between tool calls via the checkForInput callback.
    public void AddUserMessage(string text)
    {
        _interruptedAndWaiting = false;
        _inputQueue.Enqueue(text);
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
        foreach (CanonicalMessage msg in _data.Messages)
        {
            if (msg is SystemMessage sm)
            {
                if (!string.IsNullOrEmpty(sm.Text))
                    _transport.System(sm.Text);
            }
            else if (msg is UserMessage um)
            {
                if (!string.IsNullOrEmpty(um.Text))
                    _transport.User(um.Text);
            }
            else if (msg is AssistantMessage am)
            {
                if (!string.IsNullOrEmpty(am.Thinking))
                    _transport.Thinking(am.Thinking);
                if (!string.IsNullOrEmpty(am.Text))
                    _transport.Output(am.Text);
                foreach (SemanticToolCall tc in am.ToolCalls)
                    _transport.ToolCallWithId(tc.Id, $"{tc.Name}({tc.ArgumentsJson})");
            }
            else if (msg is ToolResultMessage tr)
            {
                if (!string.IsNullOrEmpty(tr.Content))
                    _transport.ToolResponseWithId(tr.ToolCallId, new ToolResult(tr.Content, string.Empty, 0));
            }
        }
    }

    // ---- Turn execution ----

    // Runs one LLM turn. Flushes pending messages first, then polls the queue mid-turn.
    // Returns Completed, ContextFull, Failed, or Interrupted.
    // Re-throws OperationCanceledException only when appToken fires (process shutdown).
    // Interrupt() cancels the per-turn token; that case is absorbed and returned as Interrupted.
    // midTurnDrain: called between tool calls to move text from the orchestrator's input queue into
    // this session's _inputQueue, where checkForInput picks it up for immediate LLM injection.
    // Commands in the orchestrator queue are left in place and processed at the next loop iteration.
    public async Task<LlmResult> RunTurnAsync(LlmService service, Tool[] tools, int reserveTokens, CancellationToken appToken, Action? midTurnDrain)
    {
        _data.Model = service.Model.ConfigId;

        if (string.IsNullOrEmpty(_data.DisplayName))
        {
            string? first = GetFirstUserText();
            if (!string.IsNullOrWhiteSpace(first))
            {
                string name = first.Trim();
                _data.DisplayName = name.Length > 50 ? name.Substring(0, 50) : name;
            }
        }

        // Flush any messages queued before this turn starts.
        FlushPendingMessages();

        _midTurnDrain = midTurnDrain;
        _turnCts = new CancellationTokenSource();
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_turnCts.Token, appToken);
            try
            {
                return await service.RunToCompletionAsync(this, _bundle, tools, reserveTokens, _transport, linked.Token);
            }
            catch (OperationCanceledException) when (_turnCts.IsCancellationRequested && !appToken.IsCancellationRequested)
            {
                // Interrupt() was called — mark as waiting for user so NeedsAttention() returns false
                // until AddUserMessage() is called with new input.
                _interruptedAndWaiting = true;
                return new LlmResult(LlmExitReason.Interrupted, "Interrupted by user");
            }
        }
        finally
        {
            _midTurnDrain = null;
            _turnCts.Dispose();
            _turnCts = null;
        }
    }

    // Runs a summarization call in a temporary copy of this session and returns the assistant text.
    // This session is never modified — the temp copy is discarded after the call.
    // Returns null if the call fails or is interrupted.
    public async Task<string?> SummarizeAsync(LlmService service, string prompt, Tool[] tools, CancellationToken appToken)
    {
        Session temp = Fork($"{_data.Id}_sum", string.Empty, true);
        temp.AddUserMessage(prompt);
        LlmResult result = await temp.RunTurnAsync(service, tools, 0, appToken, null);
        if (result.ExitReason == LlmExitReason.Completed)
            return temp.GetLastAssistantText();
        return null;
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
            ephemeral);
        return new Session(forked, string.Empty, _transport);
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

        return $"({generation}) {base_}";
    }
}
