using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Fulfills one subagent-wrapped tool call by running an ephemeral "Tools"-role sub-session.
// Split from SessionRunner so the fork/retry/adoption invariants live in one place:
//   - The sub-session is the durable record: announced, saved, and completed by adopting the
//     best attempt's conversation back into it.
//   - Forks are internal retry attempts: ephemeral, sharing the sub-session's ID so their
//     turns stream live into the sub-session's client view; adoption is canonical-only
//     because the client already watched the content arrive.
//   - Each attempt runs on a fresh LlmService so protocol state never leaks between attempts.
// currentSession is read at call time because the owning runner's active session changes across
// compaction and role transitions; the delegate keeps the captured tool handlers valid.
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

    private static string BuildSubSessionMessage(string toolName, JsonObject args, string goal, int outputBudgetTokens)
    {
        JsonObject displayArgs = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> kv in args)
        {
            if (kv.Key != "goal")
                displayArgs[kv.Key] = kv.Value?.DeepClone();
        }

        return $"Use tools such as {toolName} with parameters {displayArgs.ToJsonString()} to return: {goal}\n"
            + $"Your final message is the response returned to the calling agent and is inserted into its context, so it must fit within approximately {outputBudgetTokens} tokens. "
            + "If your raw findings are larger, summarize them to fit while preserving the exact details requested (file paths, line numbers, names, key output).";
    }

    // Runs an ephemeral sub-session using the "Tools" role to fulfill a single tool call. The result
    // is inserted into the calling agent's context and must fit within outputBudgetTokens. Each
    // attempt forks from the clean sub-session so a clipped or over-budget reply never pollutes the
    // session history. Returns the last fitting assistant text, or null if the Tools role is
    // unavailable (the caller falls back to the raw handler).
    public async Task<(string? text, int responseTokens)> RunSubSessionAsync(string toolName, JsonObject args, string goal, int outputBudgetTokens, CancellationToken ct)
    {
        // No budget left for any reply (window nearly full): a sub-session is pointless, and a
        // 0 cap would collide with the "uncapped" sentinel on retries. Fall back to the raw
        // handler, whose output the tool dispatcher truncates to the budget.
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
        subSession.AnnounceToClient();
        subSession.AddUserMessage(BuildSubSessionMessage(toolName, args, goal, outputBudgetTokens));
        // Flush immediately: forks copy Data.Messages, and the sub-session never runs a turn
        // itself, so without this the goal message never reaches the forks (or the saved record).
        subSession.FlushPendingMessages();
        parent.AddChild(subSession);

        subSession.SendBusy();
        try
        {
            const int MaxFitAttempts = 3;
            string? finalText = null;
            Session? bestAttempt = null;
            // Exact token size of the adopted reply, measured by the attempt's provider response.
            // Returned so the calling agent's budget can free the unused part of this tool's reservation.
            int bestResponseTokens = 0;
            int promptCount = subSession.Data.Messages.Count;
            // First attempt is uncapped so the subagent can read large files or reason extensively;
            // only its final reply must fit. Retries use outputBudgetTokens as a hard cap so the
            // model is forced to produce a concise reply rather than getting clipped mid-sentence.
            int outputCap = 0;
            for (int attempt = 0; attempt < MaxFitAttempts; attempt++)
            {
                // Each attempt needs a fresh service: the proxy's protocol accumulates native
                // conversation state during a turn, so reusing it would resend the previous
                // attempt's conversation on top of this fork's clean history.
                LlmService? attemptService = attempt == 0 ? service : _registry.CreateService(toolsRole, string.Empty, 0);
                if (attemptService == null)
                    break;

                // Fork from the clean sub-session so each attempt starts from the same state.
                // The fork shares the sub-session's ID, so the attempt's turn streams live into
                // the sub-session's client view; a retry simply streams after the prior attempt.
                Session forkSession = subSession.Fork();

                ProtocolResult result = await attemptService.RunToCompletionAsync(forkSession, innerTools, null, 0, outputCap, _transport, ct);
                  if (result.Outcome != ProtocolCallOutcome.Success)
                      break;

                  // Commit the assistant turn to canonical and protocol state.
                  forkSession.CommitAssistantTurn(result.Payload!);

                  if (result.Payload!.FinishReason == "length" || result.Payload.FinishReason == "max_tokens")
                {
                    // Clipped response is useless — retry with a hard cap so the model must fit.
                    outputCap = outputBudgetTokens;
                    continue;
                }

				int responseTokens = bestAttempt?.CumulativeOutputTokens ?? 0;
					finalText = forkSession.GetLastAssistantText();
					bestAttempt = forkSession;
					int completionTokens = forkSession.LastTokenUsage?.CompletionTokens ?? 0;
					bestResponseTokens = completionTokens;
                if (responseTokens <= outputBudgetTokens)
                    break;

                // Complete but over budget — cap the next attempt.
                outputCap = outputBudgetTokens;
            }

            // Adopt the best attempt's conversation into the sub-session so its saved record holds
            // the complete exchange, not just the initial prompt. Canonical-only: the client already
            // watched the attempt stream in under this ID, so replaying to it would duplicate the
            // display. The usage totals are carried so the saved record reflects what the work cost.
            if (bestAttempt != null)
            {
                List<CanonicalMessage> tail = new List<CanonicalMessage>();
                IReadOnlyList<CanonicalMessage> attemptMessages = bestAttempt.Data.Messages;
                for (int i = promptCount; i < attemptMessages.Count; i++)
                    tail.Add(attemptMessages[i]);
                subSession.ReplayExchanges(tail, false);
                subSession.RecordTurnUsage(
                    new TokenUsageInfo { PromptTokens = bestAttempt.CumulativeInputTokens, CompletionTokens = bestAttempt.CumulativeOutputTokens },
                    bestAttempt.TotalCost,
                    bestAttempt.ContextLength);
            }

            // Over-budget finalText (or null on no clean completion) is acceptable here; the tool
            // dispatcher (LlmService.ExecuteToolAsync) hard-truncates to the budget as a last resort.
            return (finalText, bestResponseTokens);
        }
        finally
        {
            if (!subSession.Ephemeral)
                SessionService.Save(subSession.Data);
            subSession.SendIdle();
        }
    }
}
