using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Spawns child agents on behalf of the root agent's subagent tool. A child is a real, announced,
// saved sub-session assigned a named role — its system prompt, model, and tools — seeded with a
// natural-language task. It runs to completion, terminating only when the model calls return_to_caller;
// a turn that ends with no tool call is re-prompted to use it. The reply is measured and fit to the
// caller's budget before it is returned.
//
// Ownership / accounting rules:
//   - The returned result must fit the calling agent's remaining room (outputBudgetTokens); the fit
//     loop re-prompts for a shorter reply and hard-caps as a last resort.
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

    // Runs an explicitly-invoked subagent: a child session assigned the named role, seeded with the
    // caller's natural-language prompt, carrying that role's own tools plus return_to_caller. The session
    // runs to completion, terminating only when the model calls return_to_caller, and the result is fit to
    // the caller's outputBudgetTokens. Returns ok=false with the error text as the result when the role is
    // unknown/ineligible, no model is available, there is no budget, the role's enter/exit hook fails, or
    // the subagent never returned a result; the calling handler surfaces that text to the caller.
    public async Task<(bool ok, string text, int responseTokens)> RunSubagentAsync(string roleName, string prompt, int outputBudgetTokens, CancellationToken ct)
    {
        if (outputBudgetTokens <= 0)
            return (false, "No output budget remaining for a subagent.", 0);

        Role? role = _roleService.GetRole(roleName);
        if (role == null)
            return (false, $"Unknown role '{roleName}'.", 0);

        // Only Subagent-kind roles may run in a SubagentSession; an Agent role here is a caller error.
        if (role.Kind != RoleKind.Subagent)
            return (false, $"Role '{roleName}' is not a subagent role.", 0);

        LlmService? service = _registry.CreateService(role, string.Empty, 0);
        if (service == null)
            return (false, $"No model available for role '{roleName}'.", 0);

        // Entry hook gates the session: a nonzero exit aborts before any session is created.
        string enterError = await role.EnterAsync(ct, null);
        if (!string.IsNullOrEmpty(enterError))
            return (false, enterError, 0);

        string displayName = prompt.Length > 80 ? prompt.Substring(0, 80) : prompt;

        Session parent = _currentSession();
        BeastSession subData = new BeastSession(parent.AllocateChildId(), displayName, service.Model.ConfigId, role.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral, 0);
        Session subSession = new Session(subData, role.SystemPrompt, _transport, true);
        subSession.AddUserMessage(prompt);
        subSession.AnnounceToClient();
        parent.AddChild(subSession);

        // The child carries only its role's bound tools (no subagent tool), so nesting stops here.
        // fetch_url is injected by name like elsewhere (it needs the runWeb delegate); return_to_caller is
        // created in ToolFactory and added here as the explicit terminator.
        ReturnSink sink = new ReturnSink();
        Tool returnTool = ToolFactory.CreateReturnToCallerTool(output => { sink.Value = output; sink.Returned = true; });
        List<Tool> withReturn = new List<Tool>(role.BuiltTools);
        if (role.Tools.Contains("fetch_url"))
            withReturn.Add(ToolFactory.CreateFetchUrlTool(_registry, _roleService, _currentSession));
        withReturn.Add(returnTool);
        Tool[] fullTools = withReturn.ToArray();
        Tool[] returnOnlyTools = new Tool[] { returnTool };

        subSession.SendBusy();
        try
        {
            // Generous turn cap so a working subagent can iterate, while still bounding runaway loops.
            const int kMaxTurns = 50;
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
                    // The return_to_caller call carries the reply; its turn's completion tokens are the
                    // server-measured size we charge against the caller's budget.
                    responseTokens = subSession.LastTokenUsage?.CompletionTokens ?? 0;
                    if (responseTokens <= outputBudgetTokens || lastTurn)
                        break;

                    // Complete but over budget with turns remaining: ask for a shorter return next turn.
                    sink.Returned = false;
                    subSession.AddUserMessage(
                        $"That output is about {responseTokens} tokens but must fit within {outputBudgetTokens} tokens. "
                        + "Call return_to_caller again with a shorter output, preserving the key details (file paths, line numbers, names, key output).");
                    continue;
                }

                if (!hasToolCalls && !lastTurn)
                {
                    // A turn that ends with no tool call cannot terminate the subagent: nudge it with the
                    // role's end-of-turn prompt (data-driven) to keep working and finish via return_to_caller.
                    string nudge = string.IsNullOrEmpty(role.EndOfTurnPrompt)
                        ? "Continue the task, then call the return_to_caller tool with your final result to finish."
                        : role.EndOfTurnPrompt;
                    subSession.AddUserMessage(nudge);
                }
            }

            // Cost is spent regardless of whether the subagent ever returned a usable result: roll the
            // sub-session's entire spend up into the calling agent so the root's cost reflects total spend.
            parent.RecordCost(subSession.TotalCost);

            // Exit hook gates the result: a nonzero exit means the result returned to the caller is the
            // hook error rather than the subagent's output.
            string exitError = await role.ExitAsync(ct, null);
            if (!string.IsNullOrEmpty(exitError))
                return (false, exitError, responseTokens);

            if (sink.Value == null)
                return (false, "The subagent finished without returning a result.", responseTokens);

            return (true, sink.Value, responseTokens);
        }
        finally
        {
            if (!subSession.Ephemeral)
                SessionService.Save(subSession.Data, false);
            subSession.SendIdle();
        }
    }
}
