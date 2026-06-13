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
    // Runs a summarization prompt in a temporary fork of the session and returns the assistant
    // text, or null when no service is available or the turn did not complete. Creates its own
    // fresh LlmService so the fork's ProtocolProxy is never shared with the parent session. The
    // fork shares the session's ID, so the summary turn streams live into the session's client
    // view; the fork is discarded after the call and the original session's record is untouched.
    public static async Task<string?> SummarizeAsync(Session session, string prompt, Tool[] tools, LlmRegistry registry, RoleService roleService, ITransportServer transport, CancellationToken appToken)
    {
        string? summary = null;

        Role? role = roleService.GetRole(session.Role);
        LlmService? service = registry.CreateService(role, session.Model, 0);
        if (service != null)
        {
            Session temp = session.Fork();
            temp.AddUserMessage(prompt);
            ProtocolResult result = await service.RunToCompletionAsync(temp, tools, null, 0, 0, transport, appToken);
            if (result.Outcome == ProtocolCallOutcome.Success)
                summary = temp.GetLastAssistantText();
        }

        return summary;
    }
}
