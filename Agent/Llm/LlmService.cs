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
    // Provider finish/stop reason of the completing turn ("length"/"max_tokens" when the response
    // was cut off by the output limit). Empty for non-completing exits.
    public string FinishReason { get; }

    public LlmResult(LlmExitReason exitReason, string errorMessage)
        : this(exitReason, errorMessage, string.Empty)
    {
    }

    public LlmResult(LlmExitReason exitReason, string errorMessage, string finishReason)
    {
        ExitReason = exitReason;
        ErrorMessage = errorMessage;
        FinishReason = finishReason;
    }
}

// Manages an LLM provider and drives a conversation to completion with tool calling.
// Protocols own the native assistant turn (they write into BeastSession.<protocol>State directly
// and fan out to the other listeners). LlmService is concerned only with running the loop,
// dispatching tools, handling XML-tool-call fallback, and surfacing terminal results.
public class LlmService
{
    private readonly ProtocolProxy _handler;
    private readonly LlmModel _model;
    private readonly ModelAvailability _availability;

    public LlmModel Model => _model;
    public bool IsDown => _availability.IsDown;

    // detectedProtocol is resolved by LlmRegistry.ProbeEndpointsAsync before any session starts.
    // availability is shared across all service instances for the same model so rate-limit and
    // down state set by one session are visible to others picking that model next.
    public LlmService(LlmModel model, DetectedProtocol detectedProtocol, ModelAvailability availability)
    {
        _model = model;
        _availability = availability;
        _handler = new ProtocolProxy(model, detectedProtocol);
    }

    // maxOutputCap (0 = none) hard-limits each response's max_tokens, so a sub-session's reply is
    // guaranteed to fit the calling agent's allotted space without any post-hoc truncation.
    public async Task<LlmResult> RunToCompletionAsync(Session conversation, ListenerBundle bundle, Tool[] tools, string? forcedToolName, int reserveTokens, int maxOutputCap, ITransportServer transport, CancellationToken cancellationToken)
    {
        if (_availability.IsDown)
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

            // Central context accounting for this turn. Seeded with the model's window and limits, the
            // compaction reserve, and the sub-session output cap. It owns all "how much room is left"
            // math: the context-full gate, completion sizing, and per-tool response reservations.
            ContextBudget budget = conversation.Budget;
            budget.Configure(model.Config.ContextWindow, model.Config.MaxOutputTokens, reserveTokens, maxOutputCap, conversation.ContextLength);

            for (; ; )
            {
                if (rateLimitRetries <= kMaxRateLimitRetries)
                {
                    TimeSpan delay = _availability.AvailableAt - DateTimeOffset.UtcNow;
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
                    finalResult = new LlmResult(LlmExitReason.Failed, $"Rate limited after {kMaxRateLimitRetries} retries, retry after {_availability.AvailableAt:u}");
                    break;
                }

                if (budget.IsExhausted())
                {
                    finalResult = new LlmResult(LlmExitReason.ContextFull, "Context limit reached");
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Size the response against the exact context plus any not-yet-reported tool outputs,
                // so input + output stays within the window even right after a tool round.
                int? maxCompletionTokens = budget.MaxCompletionTokens();

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
                    string liveJson = BuildStatsJson(conversation.Model, conversation.Role, livePromptTokens, liveCompletionTokens, costBaseline + turnCost, model.Config.ContextWindow, liveContextTokens);
                    transport.Stats(sessionId, liveJson);
                };

                ProtocolResult callResult = await _handler.ExecuteAsync(bundle, toolDefs, forcedToolName, maxCompletionTokens, onProgress, transport, sessionId, conversation.QueryLog, cancellationToken);

                if (callResult.Outcome == ProtocolCallOutcome.Success)
                {
                    ProtocolCallPayload payload = callResult.Payload!;
                    // RecordTurnUsage feeds the reported size into the budget (ContextBudget.RecordMeasurement),
                    // which resets pending reservations: the size already includes any prior tool outputs.
                    conversation.RecordTurnUsage(payload.Usage, payload.Cost, payload.CurrentContextSize);

                    SendCostUpdate(conversation, model.Config.ContextWindow, transport);

                    (LlmResult? terminalResult, bool toolsDispatched) = await ProcessAssistantResponseAsync(payload, tools, bundle, budget, transport, sessionId, cancellationToken);
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
                    _availability.AvailableAt = callResult.RetryAfter ?? DateTimeOffset.UtcNow.AddSeconds(5);
                    // loop and retry
                }
                else if (callResult.Outcome == ProtocolCallOutcome.Transient)
                {
                    _availability.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(30);
                    finalResult = new LlmResult(LlmExitReason.Failed, callResult.ErrorMessage);
                    break;
                }
                else
                {
                    _availability.AvailableAt = DateTimeOffset.MaxValue;
                    finalResult = new LlmResult(LlmExitReason.Failed, callResult.ErrorMessage);
                    break;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            // Our token cancelled: rethrow so RunTurnAsync classifies interrupt vs app shutdown.
            if (cancellationToken.IsCancellationRequested)
                throw;

            // Cancellation without our token = client-side timeout (e.g. HttpClient). Surface it
            // as a failure so the user sees it instead of a silent retry loop.
            string reason = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
            finalResult = new LlmResult(LlmExitReason.Failed, $"LLM call cancelled unexpectedly (client-side timeout): {reason}");
        }

        return finalResult;
    }

    private async Task<(LlmResult? terminalResult, bool toolsDispatched)> ProcessAssistantResponseAsync(ProtocolCallPayload payload, Tool[] tools, ListenerBundle bundle, ContextBudget budget, ITransportServer transport, string sessionId, CancellationToken ct)
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
            // Carry the finish reason so a capped sub-session can tell a cut-off reply from a complete one.
            return (new LlmResult(LlmExitReason.Completed, "", payload.FinishReason), false);
        }

        // Allocate this round's tool-response budget from the window; the budget splits it evenly
        // across the parallel calls and records the whole round as pending.
        int perToolBudget = budget.ReserveToolResponses(toolCalls.Count);

        (string toolName, ToolResult toolResult)[] completedTools = new (string, ToolResult)[toolCalls.Count];

        Task[] tasks = new Task[toolCalls.Count];
        for (int i = 0; i < toolCalls.Count; i++)
        {
            int index = i;
            SemanticToolCall toolCall = toolCalls[index];
            tasks[index] = ExecuteToolAsync(toolCall, tools, transport, sessionId, perToolBudget, ct)
                .ContinueWith(t => completedTools[index] = (toolCall.Name, t.Result), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        await Task.WhenAll(tasks);

        for (int i = 0; i < completedTools.Length; i++)
        {
            ToolResult result = completedTools[i].toolResult;

            // A sub-session tool reports its reply's exact size; free the unused part of its
            // reservation so the next request can size against the real remaining room.
            if (result.MeasuredOutputTokens.HasValue)
                budget.SettleToolResponse(perToolBudget, result.MeasuredOutputTokens.Value);

            bundle.OnToolResult(toolCalls[i].Id, result);
        }

        return (null, true);
    }

    private async Task<ToolResult> ExecuteToolAsync(SemanticToolCall toolCall, Tool[] tools, ITransportServer transport, string sessionId, int maxOutputTokens, CancellationToken ct)
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
            result = await matchedTool.Handler(argsObj, ct, transport, sessionId, maxOutputTokens);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new ToolResult(string.Empty, $"Tool '{toolCall.Name}' threw exception: {ex.Message}", 1);
        }
        return TruncateToBudget(result, maxOutputTokens);
    }

    // Hard-enforces the per-call output budget. The conversation-loop math assumes every tool
    // result fits its budget, but raw handlers don't bound themselves and a sub-session reply can
    // come back over budget after its retries are exhausted. ≈4 chars/token matches the
    // protocols' streaming estimates. StdErr keeps priority so error text survives truncation.
    private static ToolResult TruncateToBudget(ToolResult result, int maxOutputTokens)
    {
        ToolResult bounded;
        int maxChars = maxOutputTokens * 4;
        if (result.StdOut.Length + result.StdErr.Length <= maxChars)
        {
            bounded = result;
        }
        else
        {
            const string marker = "\n[Output truncated: it exceeded the remaining context budget for this tool call.]";
            string stdErr = result.StdErr;
            string stdOut = result.StdOut;
            if (stdErr.Length > maxChars)
            {
                stdErr = stdErr.Substring(0, maxChars) + marker;
                stdOut = string.Empty;
            }
            else
            {
                stdOut = stdOut.Substring(0, maxChars - stdErr.Length) + marker;
            }
            bounded = new ToolResult(stdOut, stdErr, result.ExitCode);
        }
        return bounded;
    }

    // Pushes a Stats frame to the client the moment the session cost changes so the displayed
    // total updates in realtime, rather than only at turn boundaries via the orchestrator. This
    // carries the authoritative committed totals and supersedes any provisional live frame.
    private void SendCostUpdate(Session conversation, int maxContext, ITransportServer transport)
    {
        int prompt = conversation.CumulativeInputTokens;
        int completion = conversation.CumulativeOutputTokens;
        int contextTokens = conversation.ContextLength;

        string json = BuildStatsJson(conversation.Model, conversation.Role, prompt, completion, conversation.TotalCost, maxContext, contextTokens);
        transport.Stats(conversation.Id, json);
    }

    private static string BuildStatsJson(string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens)
    {
        return JsonSerializer.Serialize(new
        {
            model,
            role,
            promptTokens,
            completionTokens,
            totalCost,
            maxContext,
            contextTokens
        });
    }
}
