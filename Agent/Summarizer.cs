using System.Threading;
using System.Threading.Tasks;


// Stateless summarization. SummarizeAsync runs the summary prompt as a real turn on the session and
// returns the assistant output taken straight from the protocol result.
//
// It does NOT fork. Forking only ever existed to keep the summary turn from polluting the session,
// but RunToCompletionAsync hands the result back WITHOUT committing it — so the summary text is read
// directly off result.Payload, and the only thing that lands in the session is whatever we choose to
// commit. We do commit the turn here so the session closes out with a complete prompt+summary
// exchange (no dangling user message, and the streamed summary matches the saved record). The caller
// (CompactAsync) takes the returned text and seeds a fresh compacted session with it.
public static class Summarizer
{
	public static async Task<string?> SummarizeAsync(Session session, string prompt, Tool[] tools, LlmRegistry registry, RoleService roleService, ITransportServer transport, CancellationToken appToken)
	{
		string? summary = null;

		Role? role = roleService.GetRole(session.Role);
		LlmService? service = registry.CreateService(role, session.Model, 0);
		if (service != null)
		{
			session.AddUserMessage(prompt);
			for (; ; )
			{
				ProtocolResult result = await service.RunToCompletionAsync(session, tools, null, 0, 0, transport, appToken);
				if (result.Outcome == ProtocolCallOutcome.Success)
				{
					session.CommitAssistantTurn(result.Payload!);
					summary = result.Payload!.AssistantText;
					break;
				}

				// Sustained-rate-limited: fall back to the next usable model in the role's list (like /model)
				// and retry. Any other failure, or an exhausted list, leaves the summary null.
							if (result.Outcome == ProtocolCallOutcome.TooManyRetries)
				{
					LlmService? fallback = registry.CreateFallbackService(service, 0);
					if (fallback != null)
					{
						service = fallback;
						transport.Status(session.Id, $"Rate limited; falling back to {service.Model.Config.Name}");
						continue;
					}
				}

				break;
			}
		}

		return summary;
	}
}