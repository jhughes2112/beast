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
    public bool Success => ExitReason == LlmExitReason.Completed || ExitReason == LlmExitReason.ContextFull;
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

    public async Task<LlmResult> RunToCompletionAsync(BeastSession conversation, ListenerBundle bundle, Tool[] tools, int reserveTokens, ITransportServer transport, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            if (_availableAt == DateTimeOffset.MaxValue)
            {
                return new LlmResult(LlmExitReason.Failed, $"LLM {_model.Config.Name} is permanently down");
            }

            return new LlmResult(LlmExitReason.Failed, $"LLM {_model.Config.Name} is unavailable until {_availableAt:u}");
        }

        return await ExecuteConversationAsync(conversation, bundle, tools, reserveTokens, transport, cancellationToken);
    }

    private async Task<LlmResult> ExecuteConversationAsync(BeastSession conversation, ListenerBundle bundle, Tool[] tools, int reserveTokens, ITransportServer transport, CancellationToken cancellationToken)
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

            for (; ; )
            {
                if (model.Config.ContextWindow - conversation.GetUsedTokenCount() <= reserveTokens)
                {
                    finalResult = new LlmResult(LlmExitReason.ContextFull, "Context limit reached");
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                int maxCompletionTokens = ComputeMaxCompletionTokens(conversation, model.Config.ContextWindow, reserveTokens) ?? 0;

                ProtocolResult callResult = await _handler.ExecuteAsync(bundle, toolDefs, maxCompletionTokens, transport, cancellationToken);

                if (callResult.Outcome == ProtocolCallOutcome.Success)
                {
                    ProtocolCallPayload payload = callResult.Payload!;
                    conversation.TotalCost += payload.Cost;
                    conversation.LastTokenUsage = payload.Usage;

                    (LlmResult? terminalResult, bool toolsDispatched) = await ProcessAssistantResponseAsync(payload, tools, conversation, bundle, transport, cancellationToken);
                    if (terminalResult != null)
                    {
                        finalResult = terminalResult;
                        break;
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
                    _availableAt = callResult.RetryAfter ?? DateTimeOffset.UtcNow.AddSeconds(5);
                    finalResult = new LlmResult(LlmExitReason.Failed, $"Rate limited, retry after {_availableAt:u}");
                    break;
                }
                else if (callResult.Outcome == ProtocolCallOutcome.PermanentFailure)
                {
                    _availableAt = DateTimeOffset.MaxValue;
                    finalResult = new LlmResult(LlmExitReason.Failed, callResult.ErrorMessage);
                    break;
                }
                else
                {
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
        List<string> xmlParseErrors = new List<string>();

        // XML tool-call fallback: some models emit <tool_call>{...}</tool_call> inline.
        if (!hasToolCalls && text.Length > 0)
        {
            (List<ConversationToolCall> xmlToolCalls, List<string> xmlErrors) = TryParseXmlToolCalls(text, tools);

            if (xmlToolCalls.Count > 0)
            {
                NormalizeToolCalls(xmlToolCalls);
                foreach (ConversationToolCall tc in xmlToolCalls)
                {
                    toolCalls.Add(new SemanticToolCall { Id = tc.Id, Name = tc.Function.Name, ArgumentsJson = tc.Function.Arguments });
                }

                // The producing protocol already committed a tool-less assistant turn into its own
                // native state and fanned it out. Rewrite every protocol's last assistant turn to
                // include the extracted tool calls so subsequent tool results have a valid parent.
                bundle.RewriteLastAssistant(text, string.Empty, toolCalls);
                hasToolCalls = true;
            }

            xmlParseErrors = xmlErrors;
        }

        foreach (string error in xmlParseErrors)
        {
            bundle.OnUserMessage(null!, error);
        }

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
            if (!completedTools[i].toolResult.MessageHandled)
            {
                // Sender is null so every listener (each protocol's native state and the transport)
                // records the tool result.
                bundle.OnToolResult(null!, toolCalls[i].Id, completedTools[i].toolResult.Response);
            }
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
            return new ToolResult($"Error: Tool '{toolCall.Name}' not found in available tools.", false);
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
            return new ToolResult($"Error: Tool '{toolCall.Name}' received malformed arguments: {toolCall.ArgumentsJson}", false);
        }

        // ToolCall framing is already emitted by the TransportListener when the assistant turn
        // is fanned out by the producing protocol; ToolResponse framing is emitted when the tool
        // result is fanned out via Bundle.OnToolResult above. Just run the handler here.
        ToolResult result = await matchedTool.Handler(argsObj, ct, transport);
        return result;
    }

    private static void NormalizeToolCalls(List<ConversationToolCall> toolCalls)
    {
        foreach (ConversationToolCall tc in toolCalls)
        {
            tc.Id = Guid.NewGuid().ToString();
            tc.Function.Name = tc.Function.Name.Trim();

            if (string.IsNullOrWhiteSpace(tc.Function.Arguments))
            {
                tc.Function.Arguments = "{}";
            }

            JsonObject argsObj = JsonNode.Parse(tc.Function.Arguments)!.AsObject();
            List<(string key, JsonNode? value)> entries = new();
            foreach (KeyValuePair<string, JsonNode?> kvp in argsObj)
            {
                entries.Add((kvp.Key.Trim(), kvp.Value));
            }

            JsonObject trimmed = new();
            foreach ((string key, JsonNode? value) in entries)
            {
                trimmed[key] = value?.DeepClone();
            }

            tc.Function.Arguments = trimmed.ToJsonString();
        }
    }

    private (List<ConversationToolCall> calls, List<string> errors) TryParseXmlToolCalls(string content, Tool[] tools)
    {
        List<ConversationToolCall> result = new();
        List<string> errors = new();
        string[] tagNames = ["tool_call", "function_call"];

        foreach (string tagName in tagNames)
        {
            string openTag = $"<{tagName}>";
            string closeTag = $"</{tagName}>";
            int searchStart = 0;

            while (searchStart < content.Length)
            {
                int openIndex = content.IndexOf(openTag, searchStart, StringComparison.OrdinalIgnoreCase);
                if (openIndex < 0) break;

                int contentStart = openIndex + openTag.Length;
                int closeIndex = content.IndexOf(closeTag, contentStart, StringComparison.OrdinalIgnoreCase);
                if (closeIndex < 0) break;

                string inner = content.Substring(contentStart, closeIndex - contentStart).Trim();
                searchStart = closeIndex + closeTag.Length;

                (ConversationToolCall? toolCall, string? error) = TryParseXmlToolCallJson(inner, tools);
                if (toolCall != null) result.Add(toolCall);
                else if (error != null) errors.Add(error);
            }
        }

        return (result, errors);
    }

    private (ConversationToolCall? call, string? error) TryParseXmlToolCallJson(string json, Tool[] tools)
    {
        try
        {
            JsonObject? obj = JsonNode.Parse(json)?.AsObject();
            if (obj == null) return (null, $"Tool call contained invalid JSON: {json}");

            string? name = null;
            if (obj.TryGetPropertyValue("name", out JsonNode? nameNode))
            {
                name = nameNode?.GetValue<string>();
            }

            if (string.IsNullOrEmpty(name)) return (null, "Tool call is missing a name.");

            JsonObject args = new();
            if (obj.TryGetPropertyValue("arguments", out JsonNode? argsNode) || obj.TryGetPropertyValue("parameters", out argsNode))
            {
                JsonObject? parsed = argsNode?.AsObject();
                if (parsed != null) args = parsed;
            }

            Tool? matchedTool = null;
            foreach (Tool tool in tools)
            {
                if (tool.Definition.Function.Name == name)
                {
                    matchedTool = tool;
                    break;
                }
            }

            if (matchedTool == null) return (null, $"Tool '{name}' is not available.");

            JsonObject? definedProperties = matchedTool.Definition.Function.Parameters["properties"]?.AsObject();

            foreach (KeyValuePair<string, JsonNode?> arg in args)
            {
                if (definedProperties == null || !definedProperties.ContainsKey(arg.Key))
                {
                    return (null, $"Tool '{name}' does not have a parameter named '{arg.Key}'.");
                }
            }

            return (new ConversationToolCall
            {
                Id = $"xmltc_{Guid.NewGuid():N}",
                Type = "function",
                Function = new ConversationFunctionCall
                {
                    Name = name,
                    Arguments = args.ToJsonString()
                }
            }, null);
        }
        catch (JsonException ex)
        {
            return (null, $"Tool call contained invalid JSON: {ex.Message}");
        }
    }

    internal int? ComputeMaxCompletionTokens(BeastSession conversation, int contextLength, int reserveTokens)
    {
        const int DefaultCompletion = 65536;

        int usedTokens = conversation.GetUsedTokenCount();
        long available = contextLength - usedTokens - reserveTokens;
        if (available <= 0) return 0;

        int ceiling = _model.Config.MaxOutputTokens > 0 ? _model.Config.MaxOutputTokens : DefaultCompletion;
        return (int)Math.Min(available, ceiling);
    }
}
