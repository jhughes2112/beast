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

// Reports running token counts and protocol-computed cost for the current in-flight assistant
// turn while a response streams. Provisional; superseded at commit by the authoritative payload.
public delegate void LiveUsageProgress(int inputTokens, int outputTokens, decimal turnCost);

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
    public bool IsDown => _availableAt == DateTimeOffset.MaxValue;

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

    public async Task<LlmResult> RunToCompletionAsync(Session conversation, ListenerBundle bundle, Tool[] tools, int reserveTokens, ITransportServer transport, CancellationToken cancellationToken)
    {
        // Waits until the backoff timer expires. Returns false immediately if permanently down.
        if (_availableAt == DateTimeOffset.MaxValue)
        {
            return new LlmResult(LlmExitReason.Failed, $"LLM {_model.Config.Name} is permanently down");
        }

        LlmModel model = _model;
        LlmResult finalResult = new LlmResult(LlmExitReason.Completed, "");
        string sessionId = conversation.Id;

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
                if (rateLimitRetries <= kMaxRateLimitRetries)
                {
                    TimeSpan delay = _availableAt - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        if (rateLimitRetries>0)
                        {
                            transport.Status(sessionId, $"Rate limited {(int)Math.Ceiling(delay.TotalSeconds)}s, retry ({rateLimitRetries}/{kMaxRateLimitRetries})");
                        }
                        await Task.Delay(delay, cancellationToken);
                    }
                }
                else
                {
                    finalResult = new LlmResult(LlmExitReason.Failed, $"Rate limited after {kMaxRateLimitRetries} retries, retry after {_availableAt:u}");
                    break;
                }

                if (conversation.ContextLength + reserveTokens > model.Config.ContextWindow)
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
                int contextBaseline = conversation.ContextLength;
                int inputBaseline = conversation.CumulativeInputTokens;
                int outputBaseline = conversation.CumulativeOutputTokens;
                LiveUsageProgress onProgress = (inputTokens, outputTokens, turnCost) =>
                {
                    int liveContextTokens = contextBaseline;
                    int livePromptTokens = inputBaseline + inputTokens;
                    int liveCompletionTokens = outputBaseline + outputTokens;
                    string liveJson = BuildStatsJson(conversation.Model, livePromptTokens, liveCompletionTokens, costBaseline + turnCost, model.Config.ContextWindow, liveContextTokens);
                    transport.Stats(sessionId, liveJson);
                };

                ProtocolResult callResult = await _handler.ExecuteAsync(bundle, toolDefs, maxCompletionTokens, onProgress, transport, sessionId, conversation.QueryLog, cancellationToken);

                if (callResult.Outcome == ProtocolCallOutcome.Success)
                {
                    ProtocolCallPayload payload = callResult.Payload!;
                    conversation.RecordTurnUsage(payload.Usage, payload.Cost, payload.CurrentContextSize);

                    SendCostUpdate(conversation, model.Config.ContextWindow, transport);

                    (LlmResult? terminalResult, bool toolsDispatched) = await ProcessAssistantResponseAsync(payload, tools, bundle, transport, sessionId, cancellationToken);
                    if (terminalResult != null)
                    {
                        finalResult = terminalResult;
                        break;
                    }

                    // Check if new user input has arrived between tool calls; inject it so the
                    // LLM can see and respond to it on the next iteration.
                    string? newUserInput = conversation.TryGetPendingInput();
                    if (!string.IsNullOrEmpty(newUserInput))
                    {
                        bundle.OnUserMessage(newUserInput);
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
                    _availableAt = callResult.RetryAfter ?? DateTimeOffset.UtcNow.AddSeconds(5);
                    // loop and retry
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

    private async Task<(LlmResult? terminalResult, bool toolsDispatched)> ProcessAssistantResponseAsync(ProtocolCallPayload payload, Tool[] tools, ListenerBundle bundle, ITransportServer transport, string sessionId, CancellationToken ct)
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
            tasks[index] = ExecuteToolAsync(toolCall, tools, transport, sessionId, ct)
                .ContinueWith(t => completedTools[index] = (toolCall.Name, t.Result), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        await Task.WhenAll(tasks);

        for (int i = 0; i < completedTools.Length; i++)
        {
            ToolResult result = completedTools[i].toolResult;

            bundle.OnToolResult(toolCalls[i].Id, result);
        }

        return (null, true);
    }

    private async Task<ToolResult> ExecuteToolAsync(SemanticToolCall toolCall, Tool[] tools, ITransportServer transport, string sessionId, CancellationToken ct)
    {
        Action<string> fixLog = msg => transport.Status(sessionId, msg);

        Tool? matchedTool = null;
        foreach (Tool t in tools)
        {
            if (t.Definition.Function.Name == toolCall.Name)
            {
                matchedTool = t;
                break;
            }
        }

        // Stage 3: fuzzy name correction when exact match fails
        if (matchedTool == null)
        {
            string[] knownNames = new string[tools.Length];
            for (int i = 0; i < tools.Length; i++)
                knownNames[i] = tools[i].Definition.Function.Name;

            string? correctedName = FixJson.FuzzyMatchToolName(toolCall.Name, knownNames, 3, fixLog);
            if (correctedName != null)
            {
                foreach (Tool t in tools)
                {
                    if (t.Definition.Function.Name == correctedName)
                    {
                        matchedTool = t;
                        break;
                    }
                }
            }
        }

        if (matchedTool == null)
        {
            return new ToolResult(string.Empty, $"Error: Tool '{toolCall.Name}' not found in available tools.", 1);
        }

        (JsonObject? argsObj, string? argError) = FixJson.TryParseWithSchema(toolCall.ArgumentsJson, matchedTool.Definition.Function, fixLog);

        if (argsObj == null || argError != null)
        {
            return new ToolResult(string.Empty, argError ?? $"Error: Tool '{toolCall.Name}' received malformed arguments: {toolCall.ArgumentsJson}", 1);
        }

// ToolCall framing is already emitted by the TransportListener when the assistant turn
        // is fanned out by the producing protocol; ToolResponse framing is emitted when the tool
        // result is fanned out via Bundle.OnToolResult above. Just run the handler here.
        ToolResult result;
        try
        {
            result = await matchedTool.Handler(argsObj, ct, transport, sessionId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new ToolResult(string.Empty, $"Tool '{toolCall.Name}' threw exception: {ex.Message}", 1);
        }
        return result;
    }

    internal int? ComputeMaxCompletionTokens(Session conversation, int contextLength, int reserveTokens)
    {
        int usedTokens = conversation.ContextLength;
        long available = contextLength - usedTokens - reserveTokens;
        if (available <= 0) return 0;

        if (_model.Config.MaxOutputTokens <= 0) return null;

        return (int)Math.Min(available, _model.Config.MaxOutputTokens);
    }

    // Pushes a Stats frame to the client the moment the session cost changes so the displayed
    // total updates in realtime, rather than only at turn boundaries via the orchestrator. This
    // carries the authoritative committed totals and supersedes any provisional live frame.
    private void SendCostUpdate(Session conversation, int maxContext, ITransportServer transport)
    {
        int prompt = conversation.CumulativeInputTokens;
        int completion = conversation.CumulativeOutputTokens;
        int contextTokens = conversation.ContextLength;

        string json = BuildStatsJson(conversation.Model, prompt, completion, conversation.TotalCost, maxContext, contextTokens);
        transport.Stats(conversation.Id, json);
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
