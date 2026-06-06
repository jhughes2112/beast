using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Wraps BeastSession with its runtime wiring: the listener bundle, transport, and input queue.
// One Session = one independent conversation thread. Sessions can run concurrently without
// sharing any mutable state — each has its own bundle, queue, and cancellation token.
public class Session
{
    private readonly BeastSession _data;
    private readonly ListenerBundle _bundle;
    private readonly ITransportServer _transport;
    private readonly ConcurrentQueue<string> _inputQueue = new ConcurrentQueue<string>();
    private CancellationTokenSource? _turnCts;

    public BeastSession Data => _data;

    public Session(BeastSession data, ITransportServer transport)
    {
        _data = data;
        _transport = transport;
        _bundle = new ListenerBundle(
            new ListenerChatCompletions(data.ChatCompletionsState),
            new ListenerTransport(transport));
    }

    public bool NeedsAttention() => _data.NeedsLlmAttention() || !_inputQueue.IsEmpty;

    public string? GetLastAssistantText() => _bundle.GetLastAssistantText();

    // Queues a plain-text user message for injection at the next turn boundary.
    // Thread-safe: the reader task may call this while a turn is running.
    // Mid-turn: LlmService polls the queue between tool calls via the checkForInput callback.
    public void AddUserMessage(string text) => _inputQueue.Enqueue(text);

    // Applies a system prompt immediately (never queued — only called between turns).
    public void SetSystemPrompt(string prompt) => _bundle.OnSystemMessage(null!, prompt);

    // Drains the pending message queue into the bundle.
    // Call before saving a session that had AddUserMessage() called outside of a running turn.
    public void FlushPendingMessages()
    {
        while (_inputQueue.TryDequeue(out string? text))
            _bundle.OnUserMessage(null!, text);
    }

    // Clears the conversation history and resets availability.
    public void Clear() => _bundle.OnClear();

    // Signals the bundle that the active protocol should be discarded on next turn.
    public void InvalidateProtocol() => _bundle.InvalidateProtocol();

    // Hydrates the bundle from a list of stored exchange nodes (used when building a compacted session).
    public void ReplayExchanges(List<JsonNode> exchanges)
    {
        foreach (JsonNode node in exchanges)
        {
            string role = node["role"]?.GetValue<string>() ?? string.Empty;
            string content = node["content"]?.GetValue<string>() ?? string.Empty;

            if (role == "user")
            {
                _bundle.OnUserMessage(null!, content);
            }
            else if (role == "assistant")
            {
                List<SemanticToolCall> toolCalls = new List<SemanticToolCall>();
                JsonArray? tcs = node["tool_calls"]?.AsArray();
                if (tcs != null)
                {
                    foreach (JsonNode? tc in tcs)
                    {
                        if (tc == null) continue;
                        toolCalls.Add(new SemanticToolCall
                        {
                            Id = tc["id"]?.GetValue<string>() ?? string.Empty,
                            Name = tc["function"]?["name"]?.GetValue<string>() ?? string.Empty,
                            ArgumentsJson = tc["function"]?["arguments"]?.GetValue<string>() ?? string.Empty
                        });
                    }
                }
                string thinking = node["reasoning_content"]?.GetValue<string>() ?? string.Empty;
                _bundle.OnAssistantTurn(null!, content, thinking, toolCalls);
            }
            else if (role == "tool")
            {
                string toolCallId = node["tool_call_id"]?.GetValue<string>() ?? string.Empty;
                _bundle.OnToolResult(null!, toolCallId, new ToolResult(content, string.Empty, 0));
            }
        }
    }

    // Sends the full conversation history to the transport (for display on client reconnect).
    public void ReplayToTransport()
    {
        foreach (JsonNode? node in _data.ChatCompletionsState)
        {
            if (node == null) continue;
            string role = node["role"]?.GetValue<string>() ?? string.Empty;
            string content = node["content"]?.GetValue<string>() ?? string.Empty;

            if (role == "system")
            {
                if (!string.IsNullOrEmpty(content))
                    _transport.System(content);
            }
            else if (role == "user")
            {
                if (!string.IsNullOrEmpty(content))
                    _transport.User(content);
            }
            else if (role == "assistant")
            {
                string thinking = node["reasoning_content"]?.GetValue<string>() ?? string.Empty;
                if (!string.IsNullOrEmpty(thinking))
                    _transport.Thinking(thinking);
                if (!string.IsNullOrEmpty(content))
                    _transport.Output(content);
                JsonArray? toolCalls = node["tool_calls"]?.AsArray();
                if (toolCalls != null)
                {
                    foreach (JsonNode? tc in toolCalls)
                    {
                        if (tc == null) continue;
                        string name = tc["function"]?["name"]?.GetValue<string>() ?? string.Empty;
                        string args = tc["function"]?["arguments"]?.GetValue<string>() ?? string.Empty;
                        string tcId = tc["id"]?.GetValue<string>() ?? string.Empty;
                        _transport.ToolCallWithId(tcId, $"{name}({args})");
                    }
                }
            }
            else if (role == "tool")
            {
                if (!string.IsNullOrEmpty(content))
                {
                    string toolCallId = node["tool_call_id"]?.GetValue<string>() ?? string.Empty;
                    _transport.ToolResponseWithId(toolCallId, new ToolResult(content, string.Empty, 0));
                }
            }
        }
    }

    // Runs one LLM turn. Flushes pending messages first, then polls the queue mid-turn.
    // Returns Completed, ContextFull, Failed, or Interrupted.
    // Re-throws OperationCanceledException only when appToken fires (process shutdown).
    // Interrupt() cancels the per-turn token; that case is absorbed and returned as Interrupted.
    public async Task<LlmResult> RunTurnAsync(LlmService service, Tool[] tools, int reserveTokens, CancellationToken appToken)
    {
        _data.Model = service.Model.ConfigId;

        if (string.IsNullOrEmpty(_data.DisplayName))
        {
            string? first = _data.GetFirstUserText();
            if (!string.IsNullOrWhiteSpace(first))
            {
                string name = first.Trim();
                _data.DisplayName = name.Length > 50 ? name.Substring(0, 50) : name;
            }
        }

        // Flush any messages queued before this turn starts.
        FlushPendingMessages();

        // Poll the queue between LLM tool calls so parallel callers can inject messages mid-turn.
        Func<string?> checkForInput = () =>
        {
            if (_inputQueue.IsEmpty) return null;
            string accumulated = string.Empty;
            while (_inputQueue.TryDequeue(out string? line))
                accumulated = string.IsNullOrEmpty(accumulated) ? line : accumulated + "\n" + line;
            return string.IsNullOrEmpty(accumulated) ? null : accumulated;
        };

        _turnCts = new CancellationTokenSource();
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_turnCts.Token, appToken);
            try
            {
                return await service.RunToCompletionAsync(_data, _bundle, tools, reserveTokens, _transport, checkForInput, linked.Token);
            }
            catch (OperationCanceledException) when (_turnCts.IsCancellationRequested && !appToken.IsCancellationRequested)
            {
                // Interrupt() was called (user /cancel) — absorb and return Interrupted.
                return new LlmResult(LlmExitReason.Interrupted, "Interrupted by user");
            }
        }
        finally
        {
            _turnCts.Dispose();
            _turnCts = null;
        }
    }

    // Runs a summarization call in a temporary copy of this session and returns the assistant text.
    // This session is never modified — the temp copy is discarded after the call.
    // Returns null if the call fails or is interrupted.
    public async Task<string?> SummarizeAsync(LlmService service, string prompt, CancellationToken appToken)
    {
        Session temp = Fork($"{_data.Id}_sum", string.Empty);
        temp.Data.Ephemeral = true;
        temp.AddUserMessage(prompt);
        LlmResult result = await temp.RunTurnAsync(service, Array.Empty<Tool>(), 0, appToken);
        if (result.ExitReason == LlmExitReason.Completed)
            return temp.GetLastAssistantText();
        return null;
    }

    // Creates an independent deep copy of this session from this exact point in history.
    // The fork shares no mutable state with the original — both can run and diverge freely.
    public Session Fork(string newId, string newDisplayName)
    {
        BeastSession forked = new BeastSession(
            newId,
            newDisplayName,
            _data.Workflow,
            _data.WorkflowState,
            _data.Model,
            _data.Role,
            (JsonArray)_data.ChatCompletionsState.DeepClone(),
            _data.LastTokenUsage,
            _data.TotalCost,
            _data.CumulativeInputTokens,
            _data.CumulativeOutputTokens,
            _data.CurrentContextSize);
        forked.Ephemeral = _data.Ephemeral;
        return new Session(forked, _transport);
    }

    // Cancels the in-progress turn, if any.
    public void Interrupt() => _turnCts?.Cancel();
}
