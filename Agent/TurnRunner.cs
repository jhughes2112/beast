using System;
using System.Threading;
using System.Threading.Tasks;


// Stateless turn execution. RunTurnAsync runs one LLM turn on a session; SummarizeAsync runs a
// prompt in a discarded fork and returns the assistant text. Shared by SessionRunner (main
// turns, eval, compaction/transition summaries) and SubagentRunner (fork attempts). Delegates
// bundle management and turn-CTS lifecycle to the session; owns the LlmService call and
// interrupt classification.
public static class TurnRunner
{
    // forcedToolName (null = free choice) requires the model to call that exact tool this turn.
    // maxOutputCap (0 = none) hard-limits each response's max_tokens for capped sub-session retries.
    public static async Task<LlmResult> RunTurnAsync(Session session, LlmService service, Tool[] tools, string? forcedToolName, int reserveTokens, int maxOutputCap, ITransportServer transport, CancellationToken appToken)
    {
        session.UpdateModel(service.Model.ConfigId);
        CancellationToken turnToken = session.BeginTurn();
        // BeginTurn flushes _inputQueue into Messages, so InferDisplayName can now see the first user text.
        if (session.InferDisplayName())
            session.AnnounceToClient();
        bool interrupted = false;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(turnToken, appToken);
            try
            {
                return await service.RunToCompletionAsync(session, session.Bundle, tools, forcedToolName, reserveTokens, maxOutputCap, transport, linked.Token);
            }
            catch (OperationCanceledException) when (turnToken.IsCancellationRequested && !appToken.IsCancellationRequested)
            {
                interrupted = true;
                return new LlmResult(LlmExitReason.Interrupted, "Interrupted by user");
            }
        }
        finally
        {
            session.EndTurn(interrupted);
        }
    }

    // Runs a summarization prompt in a temporary fork of the session and returns the assistant
    // text, or null when no service is available or the turn did not complete. Creates its own
    // fresh LlmService so the fork's ProtocolProxy is never shared with the parent session. The
    // fork is silent — it inherits the parent's display name so it is never announced as a
    // session of its own — and is discarded after the call; the original session is untouched.
    public static async Task<string?> SummarizeAsync(Session session, string prompt, Tool[] tools, LlmRegistry registry, RoleService roleService, ITransportServer transport, CancellationToken appToken)
    {
        string? summary = null;

        Role? role = roleService.GetRole(session.Role);
        LlmService? service = registry.CreateService(role, session.Model, 0);
        if (service != null)
        {
            Session temp = session.Fork($"{session.Id}_sum", session.DisplayName, true);
            temp.AddUserMessage(prompt);
            session.AddChild(temp);
            LlmResult result = await RunTurnAsync(temp, service, tools, null, 0, 0, transport, appToken);
            if (result.ExitReason == LlmExitReason.Completed)
                summary = temp.GetLastAssistantText();
        }

        return summary;
    }
}
