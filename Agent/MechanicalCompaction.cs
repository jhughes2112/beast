using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;


// First-pass compaction that rewrites history deterministically, with no LLM call at all. Tool
// traffic is the bulk of most conversations and the most re-derivable content — a file can be
// re-read, a command re-run — and the assistant's own following text usually states what it
// concluded from a result. So stale, bulky tool results are replaced with a one-line note built
// from the call's own arguments, oversized tool-call arguments (whole file bodies in write_file)
// are elided to a small stub, stale reasoning traces are cut, and the whip messages the system
// injected to keep the model working are dropped. Real user messages are ALWAYS kept verbatim:
// user text is small and irreplaceable, tool output is huge and re-derivable.
//
// The tool call/result pairing is never broken — a result is elided in place, its call retained —
// so the rewritten history satisfies every protocol's pairing rules, and ProtocolAnthropic's
// Rehydrate merges any same-role adjacency a dropped nudge leaves behind. The pass runs only when
// it can reclaim a meaningful fraction of the conversation (measured before committing to it);
// otherwise the caller falls through to the full staged summarization.
public static class MechanicalCompaction
{
	// Marker prefix identifying the file-ledger user message, so repeated compactions strip the
	// stale ledger and emit a fresh one instead of stacking them up.
	public const string LedgerMarker = "[Beast file ledger]";

	// Assistant turns at the tail of the conversation whose whole span stays verbatim — the model
	// may still be actively referencing its recent tool results mid-task.
	private const int kFreshAssistantTurns = 3;

	// Floors below which a block is never elided regardless of the conversation's size: the note
	// replacing it costs chars too, and rewriting small blocks reclaims nothing worth the rewrite.
	private const int kMinResultChars = 1024;
	private const int kMinArgsChars   = 2048;

	// A block must also be at least this fraction of the whole conversation (1/200 = 0.5%) so a
	// long conversation of uniformly small results is not shredded for marginal savings.
	private const int kSizeFractionDenominator = 200;

	// The pass replaces a full summarization only when it reclaims at least this percent of the
	// conversation's chars; below that the successor would refill almost immediately.
	private const int kMinSavingsPercent = 30;

	// Builds the elided successor history, or returns null when the reclaimable space is too small
	// to matter and the caller should run the full summarization instead. endOfTurnPrompt is the
	// role's own nudge text so injected copies of it can be recognized and dropped.
	public static List<CanonicalMessage>? TryBuild(IReadOnlyList<CanonicalMessage> messages, string endOfTurnPrompt)
	{
		List<CanonicalMessage>? result = null;

		int protectStart = ProtectedStart(messages);
		if (protectStart > 0)
		{
			// Thinking chars are deliberately excluded from the accounting: unsigned reasoning is
			// not resent to most providers, so counting it would overstate the savings and let a
			// pass "succeed" without actually freeing window space.
			int totalChars = CountChars(messages);
			int threshold  = totalChars / kSizeFractionDenominator;

			// Tool results are elided only when their call is present to describe them.
			Dictionary<string, SemanticToolCall> callsById = new Dictionary<string, SemanticToolCall>(StringComparer.Ordinal);
			foreach (CanonicalMessage msg in messages)
			{
				if (msg is AssistantMessage am)
				{
					foreach (SemanticToolCall tc in am.ToolCalls)
						callsById[tc.Id] = tc;
				}
			}

			List<CanonicalMessage> rebuilt = new List<CanonicalMessage>(messages.Count);
			for (int i = 0; i < messages.Count; i++)
			{
				CanonicalMessage msg = messages[i];
				if (msg is SystemMessage)
				{
					// The successor gets the role's system prompt re-injected at construction.
					continue;
				}
				if (i >= protectStart)
				{
					rebuilt.Add(msg);
					continue;
				}

				if (msg is UserMessage um)
				{
					// A stale ledger is stripped (a fresh one is emitted by the caller) and the
					// system's whip messages carry nothing once their turn is past. Everything the
					// user actually typed stays verbatim.
					if (!um.Text.StartsWith(LedgerMarker, StringComparison.Ordinal) && !Nudges.IsNudge(um.Text, endOfTurnPrompt))
						rebuilt.Add(um);
				}
				else if (msg is AssistantMessage am)
				{
					List<SemanticToolCall> calls = new List<SemanticToolCall>(am.ToolCalls.Count);
					foreach (SemanticToolCall tc in am.ToolCalls)
					{
						if (tc.ArgumentsJson.Length >= kMinArgsChars && tc.ArgumentsJson.Length >= threshold)
							calls.Add(new SemanticToolCall { Id = tc.Id, Name = tc.Name, ArgumentsJson = ElideArguments(tc.ArgumentsJson) });
						else
							calls.Add(tc);
					}
					rebuilt.Add(new AssistantMessage(am.Text, string.Empty, calls));
				}
				else if (msg is ToolResultMessage tr)
				{
					if (tr.Content.Length >= kMinResultChars && tr.Content.Length >= threshold && callsById.TryGetValue(tr.ToolCallId, out SemanticToolCall? call))
						rebuilt.Add(new ToolResultMessage(tr.ToolCallId, ResultNote(call!, tr.Content.Length)));
					else
						rebuilt.Add(tr);
				}
			}

			int savings = totalChars - CountChars(rebuilt);
			if ((long)savings * 100 >= (long)totalChars * kMinSavingsPercent)
				result = rebuilt;
		}

		return result;
	}

	// Builds the concise file ledger from the tool calls in the history: which files were read,
	// written, and edited, with line ranges where the call arguments carried them. Deterministic —
	// the paths and ranges come straight from the recorded arguments, never from a model's guess.
	// Returns string.Empty when the conversation touched no files.
	public static string BuildLedger(IReadOnlyList<CanonicalMessage> messages)
	{
		// Per-file action lines in first-touched order.
		List<string>                     order   = new List<string>();
		Dictionary<string, List<string>> actions = new Dictionary<string, List<string>>(StringComparer.Ordinal);

		foreach (CanonicalMessage msg in messages)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					(string path, string action) = ClassifyCall(tc);
					if (path.Length > 0)
					{
						if (!actions.TryGetValue(path, out List<string>? list))
						{
							list          = new List<string>();
							actions[path] = list;
							order.Add(path);
						}
						list!.Add(action);
					}
				}
			}
		}

		string ledger = string.Empty;
		if (order.Count > 0)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append(LedgerMarker).Append('\n');
			sb.Append("Files this conversation has touched (read = seen, wrote/edited = changed):\n");
			foreach (string path in order)
			{
				sb.Append(path).Append(": ").Append(Collapse(actions[path])).Append('\n');
			}
			ledger = sb.ToString().TrimEnd('\n');
		}
		return ledger;
	}

	// True when a successor seeded with this history has nothing left to answer: it ends on a
	// satisfied tool result or a plain assistant reply, so the model would be called with no new
	// input and the session parks. The mechanical pass hands over exactly that shape whenever the
	// context filled mid-tool-round, which is precisely when the work must carry on — so the caller
	// appends a resume message. Assistant turns with outstanding tool calls are excluded: those DO
	// need attention, and appending user text after them would break the call/result pairing.
	public static bool NeedsResumePrompt(IReadOnlyList<CanonicalMessage> seed)
	{
		bool needs = false;
		if (seed.Count > 0)
		{
			CanonicalMessage tail = seed[seed.Count - 1];
			bool             open = tail is AssistantMessage am && am.ToolCalls.Count > 0;
			needs                 = !(tail is UserMessage) && !open;
		}
		return needs;
	}

	// ---- Elision notes ----

	// The one-line note that replaces an elided tool result. Built from the call's own arguments, so
	// it is always accurate. Deliberately terse: the note repeats once per elided result, and the
	// model can already see the call itself right above it — restating what to do about it every
	// time is the kind of filler that eats back the space the pass just reclaimed.
	private static string ResultNote(SemanticToolCall call, int chars)
	{
		return $"[elided {chars} chars: {call.Name}({ArgSummary(call.ArgumentsJson)})]";
	}

	// Compact "key: value" rendering of a call's arguments for the elision note, with long values
	// truncated so a note can never itself be bulky.
	private static string ArgSummary(string argsJson)
	{
		string summary;
		try
		{
			JsonNode?     node = JsonNode.Parse(argsJson);
			StringBuilder sb   = new StringBuilder();
			if (node is JsonObject obj)
			{
				foreach ((string key, JsonNode? value) in obj)
				{
					string text = value?.ToString() ?? "null";
					if (text.Length > 60)
						text = text.Substring(0, 60) + "…";
					if (sb.Length > 0)
						sb.Append(", ");
					sb.Append(key).Append(": ").Append(text);
					if (sb.Length > 160)
						break;
				}
			}
			summary = sb.ToString();
		}
		catch (System.Text.Json.JsonException)
		{
			summary = argsJson.Length > 60 ? argsJson.Substring(0, 60) + "…" : argsJson;
		}
		return summary;
	}

	// Replacement for oversized call arguments (whole file bodies in write_file, long edit texts).
	// Stays valid JSON — protocols resend arguments verbatim and some providers parse them — and
	// keeps file_path when present, since that is the part later turns actually refer back to.
	private static string ElideArguments(string argsJson)
	{
		JsonObject stub   = new JsonObject();
		stub["compacted"] = $"arguments elided ({argsJson.Length} chars)";
		try
		{
			JsonNode? node = JsonNode.Parse(argsJson);
			if (node is JsonObject obj && obj.TryGetPropertyValue("file_path", out JsonNode? pathNode) && pathNode != null)
				stub["file_path"] = pathNode.ToString();
		}
		catch (System.Text.Json.JsonException)
		{
		}
		return stub.ToJsonString();
	}

	// ---- Internals ----

	// Index of the first message in the protected tail: the span from the kFreshAssistantTurns-th
	// assistant message (counting from the end) onward stays verbatim. 0 protects everything —
	// a conversation that short has nothing safely elidable.
	private static int ProtectedStart(IReadOnlyList<CanonicalMessage> messages)
	{
		int start = 0;
		int seen  = 0;
		for (int i = messages.Count - 1; i >= 0; i--)
		{
			if (messages[i] is AssistantMessage)
			{
				seen++;
				if (seen == kFreshAssistantTurns)
				{
					start = i;
					break;
				}
			}
		}
		return start;
	}

	// Chars the wire actually carries again next turn: user text, assistant text, tool call
	// arguments, and tool result contents. Thinking is excluded (see TryBuild).
	private static int CountChars(IReadOnlyList<CanonicalMessage> messages)
	{
		int total = 0;
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is UserMessage um)
			{
				total += um.Text.Length;
			}
			else if (msg is AssistantMessage am)
			{
				total += am.Text.Length;
				foreach (SemanticToolCall tc in am.ToolCalls)
					total += tc.ArgumentsJson.Length;
			}
			else if (msg is ToolResultMessage tr)
			{
				total += tr.Content.Length;
			}
		}
		return total;
	}

	// Maps one tool call onto a ledger entry: (path, action) or ("", "") for calls that touch no
	// file. read_file carries a line range when its arguments do.
	private static (string Path, string Action) ClassifyCall(SemanticToolCall call)
	{
		string path   = string.Empty;
		string action = string.Empty;

		bool isRead  = string.Equals(call.Name, "read_file",  StringComparison.Ordinal) || string.Equals(call.Name, "find_relevant_file_sections", StringComparison.Ordinal);
		bool isWrite = string.Equals(call.Name, "write_file", StringComparison.Ordinal);
		bool isEdit  = string.Equals(call.Name, "edit_file",  StringComparison.Ordinal);
		if (isRead || isWrite || isEdit)
		{
			try
			{
				JsonNode? node = JsonNode.Parse(call.ArgumentsJson);
				if (node is JsonObject obj && obj.TryGetPropertyValue("file_path", out JsonNode? pathNode) && pathNode != null)
				{
					path = pathNode.ToString();
					if (isWrite)
					{
						action = "wrote";
					}
					else if (isEdit)
					{
						action = "edited";
					}
					else
					{
						int  offset    = 0;
						int  lines     = 0;
						bool hasOffset = obj.TryGetPropertyValue("offset", out JsonNode? offsetNode) && offsetNode != null && int.TryParse(offsetNode.ToString(), out offset);
						bool hasLines  = obj.TryGetPropertyValue("lines",   out JsonNode? linesNode) && linesNode != null && int.TryParse(linesNode.ToString(), out lines);
						if (hasOffset && hasLines)
							action = $"read {offset}-{offset + lines}";
						else if (hasOffset)
							action = $"read from {offset}";
						else
							action = "read";
					}
				}
			}
			catch (System.Text.Json.JsonException)
			{
				path = string.Empty;
			}
		}
		return (path, action);
	}

	// Collapses repeated identical actions on one file into "action xN", preserving order of the
	// distinct actions, so a file edited eight times is one short entry rather than eight.
	private static string Collapse(List<string> actions)
	{
		List<string>            order  = new List<string>();
		Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (string action in actions)
		{
			if (!counts.TryGetValue(action, out int n))
				order.Add(action);
			counts[action] = n + 1;
		}

		StringBuilder sb = new StringBuilder();
		foreach (string action in order)
		{
			if (sb.Length > 0)
				sb.Append(", ");
			sb.Append(action);
			if (counts[action] > 1)
				sb.Append(" x").Append(counts[action]);
		}
		return sb.ToString();
	}
}