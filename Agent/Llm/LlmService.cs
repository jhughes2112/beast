using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


public enum LlmExitReason
{
    Completed, ContextFull, Failed, Interrupted
}

// Result returned by LlmService after running a conversation to completion.
public class LlmResult
{
    public LlmExitReason ExitReason { get; }
    public string ErrorMessage { get; }

    public LlmResult(LlmExitReason exitReason, string errorMessage)
    {
        ExitReason = exitReason;
        ErrorMessage = errorMessage;
    }
}

// Manages an LLM provider and drives a conversation to completion with tool calling.
// Protocols own the native assistant turn (they write into BeastSession.<protocol>State directly
// and fan out to the other listeners). LlmService is concerned only with running the loop,
// dispatching tools, handling XML-tool-call fallback, and surfacing terminal results.
public class LlmService
{
    private ProtocolProxy _handler = null!;  // set in UpdateModel during the constructor
    private LlmModel _model;
    private DateTimeOffset _availableAt = DateTimeOffset.MinValue;
    public LlmModel Model => _model;
    public bool IsAvailable => DateTimeOffset.UtcNow >= _availableAt;
    public DateTimeOffset AvailableAt => _availableAt;

    public LlmService(LlmModel model)
    {
        _model = model;
        UpdateModel(model);
    }

    public void UpdateModel(LlmModel model)
    {
        _model = model;
        _handler = new ProtocolProxy(model);
    }

    // Clears any down-timer so this service becomes a candidate again.
    // Call when the user explicitly indicates intent to retry (new input, /reload, /model, /clear).
    public void ResetAvailability()
    {
        _availableAt = DateTimeOffset.MinValue;
    }

    public async Task<LlmResult> RunToCompletionAsync(BeastSession conversation, ListenerBundle bundle, Tool[] tools, int reserveTokens, ITransportServer transport, Func<string?> checkForUserInput, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            if (_availableAt == DateTimeOffset.MaxValue)
            {
                return new LlmResult(LlmExitReason.Failed, $"LLM {_model.Config.Name} is permanently down");
            }

            return new LlmResult(LlmExitReason.Failed, $"LLM {_model.Config.Name} is unavailable until {_availableAt:u}");
        }

        return await ExecuteConversationAsync(conversation, bundle, tools, reserveTokens, transport, checkForUserInput, cancellationToken);
    }

    private async Task<LlmResult> ExecuteConversationAsync(BeastSession conversation, ListenerBundle bundle, Tool[] tools, int reserveTokens, ITransportServer transport, Func<string?> checkForUserInput, CancellationToken cancellationToken)
    {
        LlmModel model = _model;
        LlmResult finalResult = new LlmResult(LlmExitReason.Completed, "");

        try
        {
            List<ToolDefinition> toolDefs = new List<ToolDefinition>();
            for (int i = 0; i < tools.Length; i++)
            {
                toolDefs.Add(tools[i].Definition);
            }

            int emptyResponseCount = 0;
            const int kMaxEmptyResponses = 10;
            int rateLimitRetries = 0;
            const int kMaxRateLimitRetries = 10;

            for (; ; )
            {
                if (conversation.GetContextLength() + reserveTokens > model.Config.ContextWindow)
                {
                    finalResult = new LlmResult(LlmExitReason.ContextFull, "Context limit reached");
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                int? maxCompletionTokens = ComputeMaxCompletionTokens(conversation, model.Config.ContextWindow, reserveTokens);

                // Provisional live stats while the turn streams. Protocols report inputTokens as the
                // whole-conversation input the provider bills this turn (Anthropic via StreamStart
                // usage, Responses via response.created usage), which is exactly what the committed
                // frame reports as promptTokens. outputTokens is the per-turn completion count. The
                // displayed in/out counters are the absolute session totals, so the live frame adds
                // the in-flight turn on top of the persisted cumulative baselines; contextTokens
                // stays as the current context occupancy. These are superseded at commit by the
                // authoritative cumulative values.
                decimal costBaseline = conversation.TotalCost;
                int contextBaseline = conversation.GetContextLength();
                int inputBaseline = conversation.CumulativeInputTokens;
                int outputBaseline = conversation.CumulativeOutputTokens;
                LiveUsageProgress onProgress = (inputTokens, outputTokens, turnCost) =>
                {
                    int liveContextTokens = contextBaseline;
                    int livePromptTokens = inputBaseline + inputTokens;
                    int liveCompletionTokens = outputBaseline + outputTokens;
                    string liveJson = BuildStatsJson(conversation.Model, livePromptTokens, liveCompletionTokens, costBaseline + turnCost, model.Config.ContextWindow, liveContextTokens);
                    transport.Stats(liveJson);
                };

                ProtocolResult callResult = await _handler.ExecuteAsync(bundle, toolDefs, maxCompletionTokens, onProgress, transport, cancellationToken);

                if (callResult.Outcome == ProtocolCallOutcome.Success)
                {
                    ProtocolCallPayload payload = callResult.Payload!;
                    conversation.AddTurnUsage(payload.Usage, payload.Cost, payload.CurrentContextSize);

                    SendCostUpdate(conversation, model.Config.ContextWindow, transport);

                    (LlmResult? terminalResult, bool toolsDispatched) = await ProcessAssistantResponseAsync(payload, tools, conversation, bundle, transport, cancellationToken);
                    if (terminalResult != null)
                    {
                        finalResult = terminalResult;
                        break;
                    }

                    // Check if new user input has arrived. If so, apply it and continue the turn
                    // so the LLM can see and respond to it on the next iteration.
                    string? newUserInput = checkForUserInput();
                    if (!string.IsNullOrEmpty(newUserInput))
                    {
                        bundle.OnUserMessage(null!, newUserInput);
                    }

                    if (toolsDispatched)
                    {
                        emptyResponseCount = 0;
                    }
                    else
                    {
                        emptyResponseCount++;
                        if (emptyResponseCount >= kMaxEmptyResponses)
                        {
                            finalResult = new LlmResult(LlmExitReason.Failed, $"Model returned {kMaxEmptyResponses} consecutive empty responses.");
                            break;
                        }
                    }
                }
                else if (callResult.Outcome == ProtocolCallOutcome.RateLimited)
                {
                    rateLimitRetries++;
                    DateTimeOffset retryAt = callResult.RetryAfter ?? DateTimeOffset.UtcNow.AddSeconds(5);

                    if (rateLimitRetries <= kMaxRateLimitRetries)
                    {
                        TimeSpan delay = retryAt - DateTimeOffset.UtcNow;
                        transport.Status($"Rate limited {(int)Math.Ceiling(delay.TotalSeconds)}s, retry (attempt {rateLimitRetries}/{kMaxRateLimitRetries})");
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay, cancellationToken);
                        }
                    }
                    else
                    {
                        _availableAt = retryAt;
                        finalResult = new LlmResult(LlmExitReason.Failed, $"Rate limited after {kMaxRateLimitRetries} retries, retry after {_availableAt:u}");
                        break;
                    }
                }
                else if (callResult.Outcome == ProtocolCallOutcome.Transient)
                {
                    _availableAt = DateTimeOffset.UtcNow.AddSeconds(30);
                    finalResult = new LlmResult(LlmExitReason.Failed, callResult.ErrorMessage);
                    break;
                }
                else
                {
                    _availableAt = DateTimeOffset.MaxValue;
                    finalResult = new LlmResult(LlmExitReason.Failed, callResult.ErrorMessage);
                    break;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                finalResult = new LlmResult(LlmExitReason.Interrupted, "Interrupted by user");
            }
            else
            {
                string reason = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                finalResult = new LlmResult(LlmExitReason.Interrupted, $"Cancelled: {reason}");
                throw;
            }
        }

        return finalResult;
    }

    private async Task<(LlmResult? terminalResult, bool toolsDispatched)> ProcessAssistantResponseAsync(ProtocolCallPayload payload, Tool[] tools, BeastSession conversation, ListenerBundle bundle, ITransportServer transport, CancellationToken ct)
    {
        string text = (payload.AssistantText ?? string.Empty).Trim();
        bool hasToolCalls = payload.ToolCalls.Count > 0;

        // Empty turn: no content and no tool calls. Caller decides if this counts toward empty-streak.
        if (text.Length == 0 && !hasToolCalls)
        {
            return (null, false);
        }

        List<SemanticToolCall> toolCalls = new List<SemanticToolCall>(payload.ToolCalls);

        if (!hasToolCalls)
        {
            // The producing protocol already fanned the assistant turn out through the bundle,
            // which means the transport listener rendered it. No direct transport call here.
            return (new LlmResult(LlmExitReason.Completed, ""), false);
        }

        (string toolName, ToolResult toolResult)[] completedTools = new (string, ToolResult)[toolCalls.Count];

        Task[] tasks = new Task[toolCalls.Count];
        for (int i = 0; i < toolCalls.Count; i++)
        {
            int index = i;
            SemanticToolCall toolCall = toolCalls[index];
            tasks[index] = ExecuteToolAsync(toolCall, tools, transport, ct)
                .ContinueWith(t => completedTools[index] = (toolCall.Name, t.Result), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        await Task.WhenAll(tasks);

        for (int i = 0; i < completedTools.Length; i++)
        {
            ToolResult result = completedTools[i].toolResult;

            // Sender is null so every listener (each protocol's native state and the transport)
            // records the tool result. Listeners can inspect StdOut, StdErr, and ExitCode to
            // decide how to handle the result.
            bundle.OnToolResult(null!, toolCalls[i].Id, result);
        }

        return (null, true);
    }

    private async Task<ToolResult> ExecuteToolAsync(SemanticToolCall toolCall, Tool[] tools, ITransportServer transport, CancellationToken ct)
    {
        Tool? matchedTool = null;
        foreach (Tool t in tools)
        {
            if (t.Definition.Function.Name == toolCall.Name)
            {
                matchedTool = t;
                break;
            }
        }

        if (matchedTool == null)
        {
            return new ToolResult(string.Empty, $"Error: Tool '{toolCall.Name}' not found in available tools.", 1);
        }

        JsonObject? argsObj;
        try
        {
            argsObj = JsonNode.Parse(toolCall.ArgumentsJson)?.AsObject();
        }
        catch (JsonException)
        {
            argsObj = null;
        }

        if (argsObj == null)
        {
            return new ToolResult(string.Empty, $"Error: Tool '{toolCall.Name}' received malformed arguments: {toolCall.ArgumentsJson}", 1);
        }

// ToolCall framing is already emitted by the TransportListener when the assistant turn
        // is fanned out by the producing protocol; ToolResponse framing is emitted when the tool
        // result is fanned out via Bundle.OnToolResult above. Just run the handler here.
        ToolResult result;
        try
        {
            result = await matchedTool.Handler(argsObj, ct, transport);
        }
        catch (Exception ex)
        {
            result = new ToolResult(string.Empty, $"Tool '{toolCall.Name}' threw exception: {ex.Message}", 1);
        }
        return result;
    }

    internal int? ComputeMaxCompletionTokens(BeastSession conversation, int contextLength, int reserveTokens)
    {
        int usedTokens = conversation.GetContextLength();
        long available = contextLength - usedTokens - reserveTokens;
        if (available <= 0) return 0;

        if (_model.Config.MaxOutputTokens <= 0) return null;

        return (int)Math.Min(available, _model.Config.MaxOutputTokens);
    }

    // Pushes a Stats frame to the client the moment the session cost changes so the displayed
    // total updates in realtime, rather than only at turn boundaries via the orchestrator. This
    // carries the authoritative committed totals and supersedes any provisional live frame.
    private void SendCostUpdate(BeastSession conversation, int maxContext, ITransportServer transport)
    {
        int prompt = conversation.CumulativeInputTokens;
        int completion = conversation.CumulativeOutputTokens;
        int contextTokens = conversation.GetContextLength();

        string json = BuildStatsJson(conversation.Model, prompt, completion, conversation.TotalCost, maxContext, contextTokens);
        transport.Stats(json);
    }

    private static string BuildStatsJson(string model, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens)
    {
        return JsonSerializer.Serialize(new
        {
            model,
            promptTokens,
            completionTokens,
            totalCost,
            maxContext,
            contextTokens
        });
    }
}
