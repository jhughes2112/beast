using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Staged, chunked summarization that works with ANY summarizing model on ANY conversation, no
// matter how the sizes compare. The history is put through the mechanical elision pass first — the
// summary of an elided transcript is the same summary for a fraction of the stages — then the
// result is rendered to a plain-text transcript,
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

	// Upper bound used the other way round: when converting a token ALLOWANCE into the chars it
	// could occupy, over-estimating is the safe direction, so this is deliberately higher.
	private const int kMaxCharsPerToken = 4;

	// Chars reserved for the stage scaffolding text on top of the role's summary prompt.
	private const int kScaffoldChars = 1024;

	// Progress floor. Any positive chunk advances through the transcript, so this only needs to be
	// big enough that a stage is worth its round trip — NOT big enough to hold a conversation. A
	// budget under it can only ever mean the model's own window is too small, which is a static
	// property of the model: no conversation, however large, can push the budget down to it.
	private const int kMinChunkChars = 256;

	// The transcript may occupy at most this fraction of the summarizing model's window, leaving
	// the rest for the stage's output, its scaffolding, and the running summary carried into it.
	// This is the guarantee that a stage prompt is sized by the MODEL, never by the conversation.
	private const int kChunkWindowPercent = 75;

	// Ceiling on a stage's summary output; small windows use a fraction of the window instead. The
	// running summary IS a previous stage's output, so this doubles as the bound that keeps the
	// summary from crowding out the transcript as stages accumulate.
	private const int kMaxSummaryOutputTokens = 8192;
	private const int kMinSummaryOutputTokens = 512;
	private const int kSummaryOutputDivisor   = 8;

	// history is the SETTLED part of the conversation — the caller holds any in-flight tool round
	// aside and re-attaches it afterwards, so a call/result pair still being formed is never folded
	// into a summary that would swallow half of it.
	public static async Task<string?> SummarizeAsync(Session session, IReadOnlyList<CanonicalMessage> history, string prompt, LlmRegistry registry, RoleService roleService, ITransportServer transport, CancellationToken appToken)
	{
		string? summary = null;

		// Any model in the role can compact: no minimum context is required because the
		// transcript is chunked to whatever window the chosen model actually has.
		Role?       role    = roleService.GetRole(session.Role);
		LlmService? service = registry.CreateService(role, session.Model, 0, true);
		if (service != null)
		{
			// Elide mechanically FIRST, always. The caller only reaches the summarizer when the
			// elision alone did not free enough room — but "not enough to skip summarizing" is not
			// "not worth doing": a 100k-char tool result and its one-line note summarize to the same
			// few sentences, so folding the elided history costs a fraction of the stages and the
			// provider spend, and leaves more of the running summary's allowance for content that
			// actually says something. The protected recent turns pass through verbatim either way.
			List<CanonicalMessage> elided  = MechanicalCompaction.Elide(history, role != null ? role.EndOfTurnPrompt : string.Empty);
			List<string>           blocks  = RenderTranscript(elided);
			string                 running = string.Empty;
			int                    index   = 0;
			int                    offset  = 0;
			int                    stage   = 0;
			bool                   failed  = false;

			// A ceiling that only ever ratchets DOWN, carried across stages. Once a chunk size has
			// failed against this model there is no sense offering the next stage the same size again:
			// whatever the server could not take, it still cannot take. Without this the shrink was
			// per-stage and every stage restarted at full size, so a server that chokes at 68k chars
			// pays for a fresh round of doomed attempts on every segment of the transcript.
			int budgetCeiling = int.MaxValue;

			while (!failed && (index < blocks.Count || stage == 0))
			{
				// A stage never carries more running summary than the budget reserved for it: an
				// oversized summary is folded down to size first. Without this the summary grows
				// with the conversation, eats the chunk budget stage by stage, and compaction dies
				// on exactly the long conversations it exists to rescue.
				if (running.Length > RunningSummaryAllowance(service))
					running = await CompressRunningAsync(session, service, running, transport, appToken);

				int  attemptBudget = StageBudget(service, prompt.Length, budgetCeiling);
				bool stageDone     = false;
				while (!stageDone && !failed)
				{
					if (attemptBudget < kMinChunkChars)
					{
						// Either the model's own window cannot hold a minimum stage, or this model has
						// refused everything down to the progress floor. Both are reasons to move to
						// another model rather than abandon the compaction — and the new model starts
						// from its own full budget, since the ratchet described what the OLD one could
						// not take.
						LlmService? smaller = registry.CreateFallbackService(service, 0);
						if (smaller != null)
						{
							service       = smaller;
							budgetCeiling = int.MaxValue;
							attemptBudget = StageBudget(service, prompt.Length, budgetCeiling);
							transport.Status(session.Id, $"[Compaction] {service.Model.Config.Name} could not summarize at any chunk size; switching models.");
							continue;
						}
						transport.Status(session.Id, $"[Compaction] Model {service.Model.Config.Name} window is too small to summarize with.");
						failed = true;
						continue;
					}

					(string chunk, int nextIndex, int nextOffset) = BuildChunk(blocks, index, offset, attemptBudget);
					bool   isFinal     = nextIndex >= blocks.Count;
					string stagePrompt = BuildStagePrompt(running, chunk, prompt, isFinal, isFinal && stage == 0);

					if (!isFinal || stage > 0)
						transport.Status(session.Id, $"[Compaction] Summarizing segment {stage + 1}...");

					// Each attempt gets a fresh throwaway session so a failed or oversized attempt
					// leaves no state behind; its provider-reported cost still rolls into the real
					// session so compaction spend is billed where it belongs.
					Session        stageSession = BuildStageSession(session, service, stagePrompt, transport);
					ProtocolResult result       = await service.RunToCompletionAsync(stageSession, System.Array.Empty<Tool>(), null, 0, SummaryOutputTokens(service), false, transport, appToken);
					session.RecordCost(stageSession.TotalCost);

					if (result.Outcome == ProtocolCallOutcome.Success)
					{
						running = result.Payload!.AssistantText;
						index   = nextIndex;
						offset  = nextOffset;
						stage++;
						stageDone = true;
						if (isFinal)
							summary = running;
					}
					else if (ProtocolHelpers.IsAccountError(result.ErrorMessage ?? string.Empty))
					{
						// The one failure a smaller chunk cannot fix: no amount of shrinking buys
						// credit or repairs a key. Stop immediately rather than burning a descending
						// series of requests against a provider that will refuse every one of them.
						transport.Status(session.Id, $"[Compaction] {service.Model.Config.Name} rejected the request for account reasons: {result.ErrorMessage}");
						failed = true;
					}
					else if (result.Outcome == ProtocolCallOutcome.TooManyRetries)
					{
						// Sustained-rate-limited: fall back to the next usable model in the role's
						// list (like /model) and retry this stage. Window size is no constraint —
						// the chunk budget is recomputed for the new model.
						LlmService? fallback = registry.CreateFallbackService(service, 0);
						if (fallback != null)
						{
							service       = fallback;
							budgetCeiling = int.MaxValue;
							transport.Status(session.Id, $"Rate limited; falling back to {service.Model.Config.Name}");
							attemptBudget = StageBudget(service, prompt.Length, budgetCeiling);
						}
						else
						{
							transport.Status(session.Id, $"[Compaction] {service.Model.Config.Name} is rate limited and no fallback model is available.");
							failed = true;
						}
					}
					else
					{
						// EVERY other failure retries with less source context. A stage prompt is the
						// one thing here we chose the size of, so shrinking it is always worth trying
						// before declaring compaction dead — and the reason a server gives is not
						// reliable evidence of what went wrong. A local server handed a chunk it cannot
						// take does not politely report an overflow: it drops the connection, and that
						// arrives as a plain transient failure after its retries are spent. Treating
						// that as terminal is what killed a real compaction after three attempts at the
						// SAME 71k-char chunk, leaving the session with nowhere to go. Halving is
						// geometric, so this bottoms out at the progress floor in a handful of tries
						// rather than looping. A Failed outcome already marked the model down; restore
						// it, since the model is not what was broken.
						if (result.Outcome == ProtocolCallOutcome.Failed)
							registry.ResetAvailability(service.Model.ConfigId);
						attemptBudget /= 2;
						budgetCeiling  = attemptBudget;
						transport.Status(session.Id, $"[Compaction] Segment failed on {service.Model.Config.Name} ({result.ErrorMessage}); retrying with less source context ({attemptBudget} chars).");
					}
				}
			}

			if (failed)
				summary = null;
		}
		else
		{
			transport.Status(session.Id, "[Compaction] No usable model is available to summarize with.");
		}

		return summary;
	}

	// Folds an over-long running summary back inside its allowance so the next stage keeps its full
	// chunk budget. The condensed text is a stage output like any other, so the model's own output
	// ceiling bounds it. If the model cannot do it, the summary is cut to fit instead: losing the
	// tail of a summary costs detail, whereas failing here costs the whole compaction — and a
	// compaction that cannot run leaves the session with nowhere to go.
	private static async Task<string> CompressRunningAsync(Session session, LlmService service, string running, ITransportServer transport, CancellationToken appToken)
	{
		int allowance = RunningSummaryAllowance(service);

		transport.Status(session.Id, "[Compaction] Condensing the running summary...");
		string compressPrompt = "The running summary below has grown too long to carry forward. Rewrite it shorter while preserving the user's explicit requests and intents, key decisions, technical concepts, file names and code sections, problems solved, and unfinished work, in chronological order. Respond with ONLY the rewritten summary.\n<summary>\n"
			+ running + "\n</summary>";

		Session        stageSession = BuildStageSession(session, service, compressPrompt, transport);
		ProtocolResult result       = await service.RunToCompletionAsync(stageSession, System.Array.Empty<Tool>(), null, 0, SummaryOutputTokens(service), false, transport, appToken);
		session.RecordCost(stageSession.TotalCost);

		string compressed = running;
		if (result.Outcome == ProtocolCallOutcome.Success && result.Payload!.AssistantText.Length > 0)
			compressed = result.Payload.AssistantText;

		if (compressed.Length > allowance)
			compressed = compressed.Substring(0, allowance);

		return compressed;
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
			string block     = blocks[index];
			int    remaining = charBudget - sb.Length;
			int    available = block.Length - offset;
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

	// The chunk size to attempt next: the model-sized budget, held under any ceiling earlier failures
	// ratcheted down during this run. Separating the two keeps ChunkCharBudget a pure statement about
	// the model while still letting a run learn, request by request, what this server will actually
	// accept — which is the only way to find out when it reports "connection closed" instead of a
	// window size.
	private static int StageBudget(LlmService service, int promptChars, int ceiling)
	{
		int budget = ChunkCharBudget(service, promptChars);
		return budget > ceiling ? ceiling : budget;
	}

	// Transcript chars a stage may carry. Every term is derived from the MODEL's window and the
	// role's prompt — nothing here scales with the conversation — so the budget a stage gets is the
	// same on stage one and stage five hundred. The running summary is charged at its full
	// allowance rather than its current length, so a stage can never be squeezed by how much the
	// summary happens to have grown; CompressRunningAsync keeps it inside that allowance.
	internal static int ChunkCharBudget(LlmService service, int promptChars)
	{
		int  window     = service.Model.Config.ContextWindow;
		long inputChars = (long)(window - SummaryOutputTokens(service)) * kCharsPerToken;
		long budget     = inputChars - RunningSummaryAllowance(service) - promptChars - kScaffoldChars;

		// The transcript never takes more than its share of the window, however much room the
		// arithmetic above says is free.
		long ceiling = (long)window * kChunkWindowPercent / 100 * kCharsPerToken;
		if (budget > ceiling)
			budget = ceiling;

		return budget > int.MaxValue ? int.MaxValue : (int)budget;
	}

	// Chars the running summary may occupy going into a stage. It is a previous stage's output, so
	// its own token ceiling bounds it — converted at the pessimistic rate, since under-estimating
	// here would let it overrun the space the chunk budget reserved for it.
	internal static int RunningSummaryAllowance(LlmService service)
	{
		return SummaryOutputTokens(service) * kMaxCharsPerToken;
	}

	// Output tokens reserved for a stage's summary: a fraction of the window (so the summary and
	// the transcript can both fit alongside each other), floored so tiny windows still produce a
	// usable summary, capped for large ones, and never above the model's own output ceiling.
	private static int SummaryOutputTokens(LlmService service)
	{
		int output = service.Model.Config.ContextWindow / kSummaryOutputDivisor;
		if (output < kMinSummaryOutputTokens)
			output = kMinSummaryOutputTokens;
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
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session stage = new Session(data, string.Empty, transport, session.IsSubagent);
		stage.MarkStagePrompt();
		stage.UpdateModel(service.Model);
		stage.Bundle.Canonical.OnUserMessage(stagePrompt);
		return stage;
	}
}