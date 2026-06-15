using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Runs an optional post-filter over a tool's real output. The wrapped tool always executes directly
// in the calling agent; only when that call carries subagent_instructions is the literal output
// handed to an ephemeral "Tools"-role sub-session, which summarizes or filters it per those
// instructions. Only the sub-session's reply is returned to the caller — the raw output is dropped.
//
// The sub-session is a real, announced, saved conversation. It is seeded with the raw output and the
// instructions and carries the full tool complement, so it can research the output and correct errors
// in the original tool call as needed before finishing. Termination is explicit through the
// return_to_caller tool; a turn that ends with no tool call is re-prompted to use it. The reply is
// measured and judged for fit BEFORE it is returned.
//
// Ownership / accounting rules:
//   - The returned summary must fit the calling agent's remaining room (outputBudgetTokens); the
//     fit loop re-prompts for a shorter reply and hard-caps as a last resort.
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

    private static string BuildFilterMessage(string toolName, JsonObject args, string rawOutput, string instructions, int outputBudgetTokens)
    {
        JsonObject displayArgs = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> kv in args)
        {
            if (kv.Key != "subagent_instructions")
                displayArgs[kv.Key] = kv.Value?.DeepClone();
        }

        return $"Tool: {toolName}\n"
			+ $"Args: {displayArgs.ToJsonString()}\n\n"
            + $"--- BEGIN OUTPUT ---\n{rawOutput}\n--- END OUTPUT ---\n\n"
			+ $"{instructions}\n\n"
			+ "If the original tool call resulted in an error, that has already been provided to the caller. "
            + "Call the return_to_caller tool with only the content the caller asked for as the final result.";
    }

    // Runs the filter sub-session until it calls return_to_caller with a summary that fits
    // outputBudgetTokens, then returns it for the calling agent to insert in place of the raw tool
    // output. Returns null (caller falls back to the literal output) when there is no budget, no Tools
    // role, no available model, or the sub-session never returned a result.
    public async Task<(string? text, int responseTokens)> RunFilterAsync(string toolName, JsonObject args, string rawOutput, string instructions, int outputBudgetTokens, CancellationToken ct)
    {
        // No budget left for any reply (window nearly full): a sub-session is pointless. Fall back to
        // the literal output, which the tool dispatcher truncates to the budget.
        if (outputBudgetTokens <= 0)
            return (null, 0);

        Role? toolsRole = _roleService.GetRole("Tools");
        if (toolsRole == null)
            return (null, 0);

        LlmService? service = _registry.CreateService(toolsRole, string.Empty, 0);
        if (service == null)
            return (null, 0);

        string displayName = instructions.Length > 80 ? instructions.Substring(0, 80) : instructions;

        Session parent = _currentSession();
        BeastSession subData = new BeastSession(parent.AllocateChildId(), displayName, service.Model.ConfigId, "Tools", new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral, 0);
        Session subSession = new Session(subData, toolsRole.SystemPrompt, _transport, true);
        subSession.AddUserMessage(BuildFilterMessage(toolName, args, rawOutput, instructions, outputBudgetTokens));
        subSession.AnnounceToClient();
        parent.AddChild(subSession);

        // Early turns carry the full tool complement plus return_to_caller, so the filter can research
        // the output and correct errors in the original call. The final turn offers return_to_caller
        // alone and forces it — the sub-session must report its result, it is not free to keep working.
        ReturnSink sink = new ReturnSink();
        Tool returnTool = BuildReturnToCallerTool(sink);
        List<Tool> withReturn = new List<Tool>(_registry.GetToolsForRole(toolsRole));
        withReturn.Add(returnTool);
        Tool[] fullTools = withReturn.ToArray();
        Tool[] returnOnlyTools = new Tool[] { returnTool };

        subSession.SendBusy();
        try
        {
            const int kMaxTurns = 3;
            int responseTokens = 0;

            for (int turn = 1; turn <= kMaxTurns; turn++)
            {
                // On the last allotted turn return_to_caller is the only tool and is required, and the
                // output is hard-capped to the budget since there is no further turn to shorten it.
                bool lastTurn = turn == kMaxTurns;
                Tool[] turnTools = lastTurn ? returnOnlyTools : fullTools;
                string? forcedToolName = lastTurn ? "return_to_caller" : null;
                int outputCap = lastTurn ? outputBudgetTokens : 0;

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
                    if (responseTokens <= outputBudgetTokens || lastTurn)
                        break;

                    // Complete but over budget with turns remaining: ask for a shorter return next turn.
                    sink.Returned = false;
                    subSession.AddUserMessage(
                        $"That output is about {responseTokens} tokens but must fit within {outputBudgetTokens} tokens. "
                        + "Call return_to_caller again with a shorter output, preserving the exact details the instructions asked for (file paths, line numbers, names, key output).");
                    continue;
                }

                if (!hasToolCalls && !lastTurn)
                {
                    // A turn that ends with no tool call cannot terminate the sub-session: nudge the
                    // model to finish its work and report through return_to_caller.
                    subSession.AddUserMessage("Continue toward the result, then call the return_to_caller tool with your filtered output to finish.");
                }
            }

            // Cost is spent regardless of whether the subagent ever returned a usable result: roll the
            // sub-session's entire spend (every turn) up into the calling agent so the root's cost
            // reflects total spend. The reply is returned for the caller to use or discard; an
            // over-budget reply is acceptable — the final turn's output is hard-capped.
            parent.RecordCost(subSession.TotalCost);
            return (sink.Value, responseTokens);
        }
        finally
        {
            if (!subSession.Ephemeral)
                SessionService.Save(subSession.Data, false);
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
