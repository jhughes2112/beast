using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Condenses the reasoning traces a mechanical compaction is about to take out, for a model whose reasoning is
// replayed to it, so each leaves a summary beside the reference to the full trace. Only compaction calls it, and
// only once it has settled on the mechanical pass: a conversation that never compacts pays for no summaries, and
// neither do traces a full summarization would fold away. They run one at a time on the Summarizer role's models,
// and one that fails just means its reference stands alone. The one job that is a single silent request rather
// than a child session: no tools, no turns, and nothing for the user to watch.
internal static class ReasoningSummarizer
{
	public const string RoleName = "Summarizer";

	// A trace is swapped out only when it is at least twice its replacement, so anything shorter than a
	// summary and a reference could not be replaced anyway.
	private const int kMinChars = 600;

	private const int kMaxOutputTokens = 160;

	private const string kTask = "Condense this AI agent's private reasoning into a note it will read later in place of the original. In at most three sentences, state what it concluded, what it ruled out and why, and what it decided to do next.";

	// The summaries of history's stale traces, keyed by the trace, for the compaction waiting on them.
	public static async Task<Dictionary<string, string>> SummarizeStaleAsync(Session session, IReadOnlyList<CanonicalMessage> history, RoleService roleService, LlmRegistry registry, ITransportServer transport, CancellationToken ct)
	{
		Dictionary<string, string> summaries = new Dictionary<string, string>(StringComparer.Ordinal);
		List<string>               traces    = MechanicalCompaction.StaleTraces(history, kMinChars);
		if (traces.Count > 0)
			transport.Status(session.Id, $"[Compaction] Summarizing {traces.Count} reasoning trace(s).");

		foreach (string thinking in traces)
		{
			try
			{
				LlmService? service = registry.CreateService(roleService.GetRole(RoleName), string.Empty, 0, true, registry.SeedOf(session));
				if (service != null)
				{
					string? summary = await SummarizeAsync(session, service, thinking, roleService, registry, ct);
					if (!string.IsNullOrWhiteSpace(summary))
						summaries[thinking] = summary.Trim();
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				session.QueryLog.Note($"Reasoning summary failed: {ex.Message}");
			}
		}
		return summaries;
	}

	// Runs on its own throwaway session with its own log, so a summary never interleaves with the turn it
	// summarizes in the session's log. Silent: this is bookkeeping the user did not ask to watch.
	private static async Task<string?> SummarizeAsync(Session session, LlmService service, string thinking, RoleService roleService, LlmRegistry registry, CancellationToken ct)
	{
		TransportSilent quiet = new TransportSilent();
		BeastSession    data  = new BeastSession($"{session.Id}-reasoning", "reasoning summary", service.Model.ConfigId, RoleName,
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session stage = new Session(data, roleService.GetRole(RoleName)!.SystemPrompt, quiet, session.IsSubagent);
		stage.MarkStagePrompt();
		stage.UpdateModel(service.Model);
		stage.Bundle.Canonical.OnUserMessage($"{kTask}\n\n<reasoning>\n{thinking}\n</reasoning>");

		ProtocolResult result = await service.RunToCompletionAsync(stage, Array.Empty<Tool>(), null, 0, kMaxOutputTokens, false, quiet, ct);
		session.RecordCost(stage.TotalCost);
		ToolClock.Report(session, stage.WorkedMs);

		string? summary = null;
		if (result.Outcome == ProtocolCallOutcome.Success)
		{
			summary = result.Payload!.AssistantText;
		}
		else
		{
			// A failed summary says nothing about the model the conversation depends on.
			if (result.Outcome == ProtocolCallOutcome.Failed)
				registry.ResetAvailability(service.Model.ConfigId);
			session.QueryLog.Note($"Reasoning summary failed on {service.Model.Config.Name}: {result.ErrorMessage}");
		}
		return summary;
	}
}