using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Staged, chunked summarization that works with ANY summarizing model on ANY conversation, no
// matter how the sizes compare. The canonical history is rendered to a plain-text transcript,
// split into chunks sized to the summarizing model's window, and folded into a running summary
// one stage at a time — so a 32k local model can compact a conversation far larger than its own
// window, and awkward shapes (split tool call/result pairs, huge tool outputs, long assistant
// turns) cannot break it: chunk boundaries fall wherever they need to, because the transcript is
// just text with none of the protocol's pairing rules.
//
// The real session is never touched: each stage runs on a throwaway ephemeral session carrying
// only that stage's prompt (canonical-only, so the chunk text is not streamed to the client),
// reusing the real session's ID so the streamed summary renders in its window. The chunk char
// budget is a conservative estimate (3 chars/token), and the provider stays the authority: a
// stage the provider rejects as over-window is halved and retried, so a miscount can only ever
// cost a retry, never the compaction.
public static class Summarizer
{
	// Conservative chars-per-token used only to SPLIT the transcript, never to account tokens —
	// the provider's own overflow rejection is what actually enforces the window, via halving.
	private const int kCharsPerToken = 3;

	// Chars reserved for the stage scaffolding text on top of the role's summary prompt.
	private const int kScaffoldChars = 1024;

	// A chunk budget below this means the model's window is too small to make progress.
	private const int kMinChunkChars = 2048;

	// Ceiling on a stage's summary output; small windows use a quarter of the window instead.
	private const int kMaxSummaryOutputTokens = 8192;

	public static async Task<string?> SummarizeAsync(Session session, string prompt, LlmRegistry registry, RoleService roleService, ITransportServer transport, CancellationToken appToken)
	{
		string? summary = null;

		// Any model in the role can compact: no minimum context is required because the
		// transcript is chunked to whatever window the chosen model actually has.
		Role? role = roleService.GetRole(session.Role);
		LlmService? service = registry.CreateService(role, session.Model, 0);
		if (service != null)
		{
			List<string> blocks = RenderTranscript(session.Data.Messages);
			string running = string.Empty;
			int index = 0;
			int offset = 0;
			int stage = 0;
			bool failed = false;

			while (!failed && (index < blocks.Count || stage == 0))
			{
				int attemptBudget = ChunkCharBudget(service, prompt.Length + running.Length);
				bool stageDone = false;
				while (!stageDone && !failed)
				{
					if (attemptBudget < kMinChunkChars)
					{
						transport.Status(session.Id, $"[Compaction] Model {service.Model.Config.Name} window is too small to summarize with.");
						failed = true;
						continue;
					}

					(string chunk, int nextIndex, int nextOffset) = BuildChunk(blocks, index, offset, attemptBudget);
					bool isFinal = nextIndex >= blocks.Count;
					string stagePrompt = BuildStagePrompt(running, chunk, prompt, isFinal, isFinal && stage == 0);

					if (!isFinal || stage > 0)
						transport.Status(session.Id, $"[Compaction] Summarizing segment {stage + 1}...");

					// Each attempt gets a fresh throwaway session so a failed or oversized attempt
					// leaves no state behind; its provider-reported cost still rolls into the real
					// session so compaction spend is billed where it belongs.
					Session stageSession = BuildStageSession(session, service, stagePrompt, transport);
					ProtocolResult result = await service.RunToCompletionAsync(stageSession, System.Array.Empty<Tool>(), null, 0, SummaryOutputTokens(service), false, transport, appToken);
					session.RecordCost(stageSession.TotalCost);

					if (result.Outcome == ProtocolCallOutcome.Success)
					{
						running = result.Payload!.AssistantText;
						index = nextIndex;
						offset = nextOffset;
						stage++;
						stageDone = true;
						if (isFinal)
							summary = running;
					}
					else if (result.Outcome == ProtocolCallOutcome.ContextFull
						|| (result.Outcome == ProtocolCallOutcome.Failed && ProtocolHelpers.IsOverflowStatusCandidate(result.HttpStatus)))
					{
						// The chars-per-token estimate ran hot for this content; let the provider's
						// own rejection drive the split and try again with half the chunk. The
						// Failed leg is structural evidence for stage sessions, which carry no
						// provider measurement for LlmService's own overflow check: this request is
						// a single transcript chunk we sized ourselves, so a client-rejection
						// status on it means size whatever the body text says. That Failed path
						// already marked the model down — restore it before retrying smaller.
						if (result.Outcome == ProtocolCallOutcome.Failed)
							registry.ResetAvailability(service.Model.ConfigId);
						attemptBudget /= 2;
					}
					else if (result.Outcome == ProtocolCallOutcome.TooManyRetries)
					{
						// Sustained-rate-limited: fall back to the next usable model in the role's
						// list (like /model) and retry this stage. Window size is no constraint —
						// the chunk budget is recomputed for the new model.
						LlmService? fallback = registry.CreateFallbackService(service, 0);
						if (fallback != null)
						{
							service = fallback;
							transport.Status(session.Id, $"Rate limited; falling back to {service.Model.Config.Name}");
							attemptBudget = ChunkCharBudget(service, prompt.Length + running.Length);
						}
						else
						{
							failed = true;
						}
					}
					else
					{
						// Transient errors were already retried inside RunToCompletionAsync; what
						// reaches here is terminal for this compaction attempt.
						failed = true;
					}
				}
			}

			if (failed)
				summary = null;
		}

		return summary;
	}

	// Renders the canonical history to per-message text blocks. System prompts are skipped (the
	// compacted successor gets the role's system prompt again) and so is thinking (unsigned
	// reasoning is display-only). Tool calls and results become labeled text, which is what frees
	// chunk boundaries from the protocol's call/result pairing rules.
	internal static List<string> RenderTranscript(IReadOnlyList<CanonicalMessage> messages)
	{
		List<string> blocks = new List<string>();
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is UserMessage um)
			{
				if (!string.IsNullOrWhiteSpace(um.Text))
					blocks.Add($"[user]\n{um.Text}\n");
			}
			else if (msg is AssistantMessage am)
			{
				StringBuilder sb = new StringBuilder();
				if (!string.IsNullOrWhiteSpace(am.Text))
					sb.Append($"[assistant]\n{am.Text}\n");
				foreach (SemanticToolCall tc in am.ToolCalls)
					sb.Append($"[assistant tool call: {tc.Name}]\n{tc.ArgumentsJson}\n");
				if (sb.Length > 0)
					blocks.Add(sb.ToString());
			}
			else if (msg is ToolResultMessage tr)
			{
				if (!string.IsNullOrEmpty(tr.Content))
					blocks.Add($"[tool result]\n{tr.Content}\n");
			}
		}
		return blocks;
	}

	// Assembles the next transcript chunk starting at (index, offset) within blocks, packing whole
	// blocks until the char budget is spent. A block bigger than the remaining budget is split
	// mid-block and the next chunk resumes at the returned offset, so even a single enormous tool
	// result flows through in window-sized pieces.
	internal static (string Chunk, int NextIndex, int NextOffset) BuildChunk(IReadOnlyList<string> blocks, int index, int offset, int charBudget)
	{
		StringBuilder sb = new StringBuilder();
		while (index < blocks.Count && sb.Length < charBudget)
		{
			string block = blocks[index];
			int remaining = charBudget - sb.Length;
			int available = block.Length - offset;
			if (available <= remaining)
			{
				sb.Append(block, offset, available);
				index++;
				offset = 0;
			}
			else
			{
				sb.Append(block, offset, remaining);
				offset += remaining;
			}
		}
		return (sb.ToString(), index, offset);
	}

	// Builds the prompt for one stage. A conversation that fits in one chunk gets the role's
	// summary prompt directly against the whole transcript; a staged run folds each segment into
	// the running summary and applies the role's prompt only on the final segment.
	internal static string BuildStagePrompt(string runningSummary, string chunk, string finalPrompt, bool isFinal, bool isOnlyStage)
	{
		StringBuilder sb = new StringBuilder();
		if (isOnlyStage)
		{
			sb.Append("Below is the complete transcript of the conversation to summarize.\n<transcript>\n");
			sb.Append(chunk);
			sb.Append("\n</transcript>\n\n");
			sb.Append(finalPrompt);
		}
		else
		{
			sb.Append("A conversation too large to process at once has been split into sequential segments.\n");
			if (runningSummary.Length > 0)
			{
				sb.Append("Running summary of the conversation so far:\n<summary>\n");
				sb.Append(runningSummary);
				sb.Append("\n</summary>\n\n");
			}
			sb.Append(isFinal ? "Final segment of the conversation transcript:\n" : "Next segment of the conversation transcript:\n");
			sb.Append("<transcript>\n");
			sb.Append(chunk);
			sb.Append("\n</transcript>\n\n");
			if (isFinal)
			{
				sb.Append("Treat the running summary plus this final segment as the complete conversation, then do the following.\n");
				sb.Append(finalPrompt);
			}
			else
			{
				sb.Append("Update the running summary to fold in this segment. Preserve the user's explicit requests and intents, key decisions, technical concepts, file names and code sections, problems solved, and unfinished work, in chronological order. Respond with ONLY the updated running summary — later segments will be folded in after this one.");
			}
		}
		return sb.ToString();
	}

	// Transcript chars a stage may carry: the window minus the stage's output reserve, converted
	// at the conservative chars-per-token rate, minus the scaffolding, role prompt, and running
	// summary the stage input also carries. Can go negative for absurdly small windows — the
	// caller treats anything under the minimum as "this model cannot summarize".
	private static int ChunkCharBudget(LlmService service, int overheadChars)
	{
		long inputChars = (long)(service.Model.Config.ContextWindow - SummaryOutputTokens(service)) * kCharsPerToken;
		long budget = inputChars - overheadChars - kScaffoldChars;
		return budget > int.MaxValue ? int.MaxValue : (int)budget;
	}

	// Output tokens reserved for a stage's summary: a quarter of small windows, capped for large
	// ones, and never above the model's own output ceiling.
	private static int SummaryOutputTokens(LlmService service)
	{
		int output = service.Model.Config.ContextWindow / 4;
		if (output < 1024)
			output = 1024;
		if (output > kMaxSummaryOutputTokens)
			output = kMaxSummaryOutputTokens;
		int modelMax = service.Model.Config.MaxOutputTokens;
		if (modelMax > 0 && modelMax < output)
			output = modelMax;
		return output;
	}

	// A throwaway ephemeral session holding only the stage prompt. It reuses the real session's
	// ID so the streamed summary renders in that session's client view, but the prompt lands in
	// canonical only — the client never sees the transcript chunks replayed at it.
	private static Session BuildStageSession(Session session, LlmService service, string stagePrompt, ITransportServer transport)
	{
		BeastSession data = new BeastSession(session.Id, session.DisplayName, service.Model.ConfigId, session.Role,
			string.Empty, 0, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session stage = new Session(data, string.Empty, transport, session.IsSubagent);
		stage.UpdateModel(service.Model);
		stage.Bundle.Canonical.OnUserMessage(stagePrompt);
		return stage;
	}
}
