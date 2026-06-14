using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Fulfills one subagent-wrapped tool call by running an ephemeral "Tools"-role sub-session whose
// result is returned to the calling agent and inserted into its context.
//
// The sub-session is a real, continuous conversation (like the root SessionRunner drives): it is
// announced, it dispatches its own tool calls through the shared ToolDispatch, and it is saved as a
// durable record. There is no forking — the reply is measured and judged for fit BEFORE it is
// returned, so the work can simply accumulate in the one sub-session.
//
// Termination is explicit: a dedicated return_to_caller tool carries the result. The subagent must
// call it to finish; a turn that ends with no tool call is re-prompted to use it. This removes the
// guesswork of treating trailing assistant text as "the answer".
//
// Ownership / accounting rules:
//   - The subagent reasons and uses tools freely up to its OWN model's context window; only the
//     returned result must fit the calling agent's remaining room (outputBudgetTokens).
//   - Cost is spent the moment each call is made, so every turn's cost accumulates in the
//     sub-session and the whole sum is rolled up into the calling (root) session at the end.
//   - currentSession is read at call time because the owning runner's active session changes across
//     compaction and role transitions; the delegate keeps the captured tool handlers valid.
public class SubagentRunner
{
    private readonly LlmRegistry _registry;
    private readonly RoleService _roleService;
    private readonly ITransportServer _transport;
    private readonly Func<Session> _currentSession;

    public SubagentRunner(LlmRegistry registry, RoleService roleService, ITransportServer transport, Func<Session> currentSession)
    {
        _registry = registry;
        _roleService = roleService;
        _transport = transport;
        _currentSession = currentSession;
    }

    // Mutable sink the return_to_caller handler writes into; read by the drive loop after each tool
    // round. Fields, not properties: the handler mutates them directly and the loop resets Returned
    // when it asks for a shorter retry.
    private sealed class ReturnSink
    {
        public string? Value;
        public bool Returned;
    }

    private static string BuildSubSessionMessage(string toolName, JsonObject args, string goal, int outputBudgetTokens)
    {
        JsonObject displayArgs = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> kv in args)
        {
            if (kv.Key != "goal")
                displayArgs[kv.Key] = kv.Value?.DeepClone();
        }

        return $"Use tools such as {toolName} with parameters {displayArgs.ToJsonString()} to accomplish: {goal}\n"
            + $"When you are finished, call the return_to_caller tool with your result as the output. That output is the entire response the calling agent receives and is inserted into its context, so it must fit within approximately {outputBudgetTokens} tokens. "
            + "If your raw findings are larger, summarize them to fit while preserving the exact details requested (file paths, line numbers, names, key output).";
    }

    // Runs the sub-session until it calls return_to_caller with a result that fits outputBudgetTokens,
    // then returns it for the calling agent to commit or discard. Returns null (caller falls back to
    // the raw handler) when there is no budget, no Tools role, no available model, or the sub-session
    // never returned a result.
    public async Task<(string? text, int responseTokens)> RunSubSessionAsync(string toolName, JsonObject args, string goal, int outputBudgetTokens, CancellationToken ct)
    {
        // No budget left for any reply (window nearly full): a sub-session is pointless. Fall back to
        // the raw handler, whose output the tool dispatcher truncates to the budget.
        if (outputBudgetTokens <= 0)
            return (null, 0);

        Role? toolsRole = _roleService.GetRole("Tools");
        if (toolsRole == null)
            return (null, 0);

        LlmService? service = _registry.CreateService(toolsRole, string.Empty, 0);
        if (service == null)
            return (null, 0);

        Tool[] innerTools = _registry.GetToolsForRole(toolsRole);

        string displayName = goal.Length > 80 ? goal.Substring(0, 80) : goal;

        Session parent = _currentSession();
        BeastSession subData = new BeastSession(parent.AllocateChildId(), displayName, service.Model.ConfigId, "Tools", new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral, 0);
        Session subSession = new Session(subData, toolsRole.SystemPrompt, _transport, true);
        subSession.AddUserMessage(BuildSubSessionMessage(toolName, args, goal, outputBudgetTokens));
        subSession.AnnounceToClient();
        parent.AddChild(subSession);

        // The first turn forces a tool and omits return_to_caller, so the subagent must actually do
        // work before it can finish. Every later turn offers the full set, return_to_caller included.
        ReturnSink sink = new ReturnSink();
        List<Tool> withReturn = new List<Tool>(innerTools);
        withReturn.Add(BuildReturnToCallerTool(sink));
        Tool[] fullTools = withReturn.ToArray();

        subSession.SendBusy();
        try
        {
            const int kMaxFitAttempts = 3;
            int fitAttempts = 0;
            int responseTokens = 0;
            bool forceTool = true;

            for (; ; )
            {
                Tool[] turnTools = forceTool ? innerTools : fullTools;
                string? forcedToolName = forceTool ? ProtocolProxy.AnyTool : null;
                forceTool = false;

                // Output is uncapped so the subagent can work up to its own context window; only after
                // it repeatedly overruns the caller's budget do we force a hard cap as a last resort.
                int outputCap = fitAttempts >= kMaxFitAttempts ? outputBudgetTokens : 0;

                ProtocolResult result = await service.RunToCompletionAsync(subSession, turnTools, forcedToolName, 0, outputCap, _transport, ct);
                if (result.Outcome != ProtocolCallOutcome.Success)
                    break;

                subSession.CommitAssistantTurn(result.Payload!);
                bool hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, turnTools, subSession, _transport, ct);
                if (hasToolCalls)
                    subSession.CommitToolResults(result.Payload!);

                if (sink.Returned)
                {
                    // The return_to_caller call carries the reply; its turn's completion tokens are
                    // the server-measured size we charge against the caller's budget.
                    responseTokens = subSession.LastTokenUsage?.CompletionTokens ?? 0;
                    if (responseTokens <= outputBudgetTokens || fitAttempts >= kMaxFitAttempts)
                        break;

                    // Complete but over budget: ask for a shorter return and try again.
                    sink.Returned = false;
                    fitAttempts++;
                    subSession.AddUserMessage(
                        $"That output is about {responseTokens} tokens but must fit within {outputBudgetTokens} tokens. "
                        + "Call return_to_caller again with a shorter output, preserving the exact details requested (file paths, line numbers, names, key output).");
                    continue;
                }

                if (!hasToolCalls)
                {
                    // A turn that ends with no tool call cannot terminate the sub-session: require the
                    // model to finish explicitly through return_to_caller.
                    subSession.AddUserMessage("You must call the return_to_caller tool with your desired output to finish.");
                }
            }

            // Cost is spent regardless of whether the subagent ever returned a usable result: roll the
            // sub-session's entire spend (every turn, including fit retries) up into the calling agent
            // so the root's cost reflects total spend. The reply is returned for the caller to use or
            // discard; an over-budget reply is acceptable — the last fit attempt is hard-capped.
            parent.RecordCost(subSession.TotalCost);
            return (sink.Value, responseTokens);
        }
        finally
        {
            if (!subSession.Ephemeral)
                SessionService.Save(subSession.Data);
            subSession.SendIdle();
        }
    }

    // The explicit termination tool. Its handler records the output into the sink and returns a
    // trivial result; the drive loop reads the sink to decide whether the sub-session is finished.
    private static Tool BuildReturnToCallerTool(ReturnSink sink)
    {
        JsonObject outputProp = new JsonObject();
        outputProp["type"] = "string";
        outputProp["description"] = "The complete result to return to the calling agent. This string is the entire response the caller receives.";

        JsonObject properties = new JsonObject();
        properties["output"] = outputProp;

        JsonArray required = new JsonArray();
        required.Add(JsonValue.Create("output"));

        JsonObject parameters = new JsonObject();
        parameters["type"] = "object";
        parameters["properties"] = properties;
        parameters["required"] = required;

        return new Tool
        {
            Definition = new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = "return_to_caller",
                    Description = "Return your final result to the calling agent and finish the task. Call this once the requested work is complete; the output string is the entire response the caller receives.",
                    Parameters = parameters
                }
            },
            Handler = (JsonObject args, string toolCallId, CancellationToken ct, ITransportServer transport, string sessionId, int maxOutputTokens) =>
            {
                string output = args["output"]?.GetValue<string>() ?? string.Empty;
                sink.Value = output;
                sink.Returned = true;
                string ack = "Returned to caller.";
                return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
            }
        };
    }
}
