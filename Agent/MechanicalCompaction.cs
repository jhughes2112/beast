using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;


// First-pass compaction that rewrites history deterministically, with no LLM call at all. Tool
// traffic is the bulk of most conversations and the most re-derivable content — a file can be
// re-read, a command re-run — and the assistant's own text usually states what it concluded from a
// result. The successor is cold either way (nothing it is sent was cached), so the shape of the
// history is not worth keeping: only what it says.
//
// So everything before the last turn is rewritten. Real user messages are ALWAYS kept verbatim, as
// messages of their own: user text is small and irreplaceable, and a model trusts instructions only
// when they arrive as the user's. Each stretch between two of them becomes one digest, a message Beast
// writes in its own voice (DigestMarker): the assistant's own words from that stretch as it wrote
// them, and one line per tool call — the tool, the note the model gave for it (or the file it touched,
// or the summary a subagent returned), a short or failed outcome, and the lines of the compaction log
// that hold the call and its output in full. Repeats collapse into one line with a count. Stale
// reasoning is dropped, or kept as a line when a summary of it is ready. The whip messages the system
// injected to keep the model working are dropped.
//
// There are no tool calls left in the rewritten part, so no pairing to keep and nothing for a model to
// copy: the tool_call_memory records this pass used to leave in their place were copied, and called,
// by some models. MemoryTool is still declared for a history written before this change that has
// records in it; its next compaction folds them into a digest like any other call.
//
// The pass runs only when it can reclaim a meaningful fraction of the conversation (measured before
// committing to it); otherwise the caller falls through to the full staged summarization.
public static class MechanicalCompaction
{
	// Marker prefix identifying the file-ledger user message, so repeated compactions strip the
	// stale ledger and emit a fresh one instead of stacking them up.
	public const string LedgerMarker = "[Beast file ledger]";

	// How a digest starts: Beast's account of a stretch of earlier turns, never the user's words.
	public const string DigestMarker = "[Beast: compacted history]";

	// The name records made by earlier versions of this pass carry. Offered to a model only as MemoryTool,
	// which runs nothing, and only while a history still holds one.
	public const string MemoryToolName = "tool_call_memory";

	// The required note on what a shell command, edit or write is for, which outlives the call.
	public const string WhyArgument = "why";

	// How a subagent's report to its caller starts: the one line it wrote for a list of results like a
	// digest, ahead of the full report. Terminators add it (ToolFactory); a digest line keeps it.
	// The model is asked for one brief sentence; past this it is cut, a backstop rather than the request.
	public const string SummaryPrefix    = "Summary: ";
	public const int    kMaxSummaryChars = 256;

	private const int kMaxWhyChars = 128;

	// Without a note (a session from before they existed, or a model that left it out) the command's first
	// line stands in for it.
	private const int kMaxFallbackWhyChars = 200;

	// A result this short and on one line says more than a pointer to it would, so the digest states it.
	private const int kMaxInlineResultChars = 120;

	// How a reasoning reference from an earlier version of this pass starts.
	private const string kReasoningMarker = "[reasoning: ";

	// Assistant turns at the tail of the conversation whose whole span stays verbatim: the turn in flight,
	// with its calls and results, which the model is still acting on.
	private const int kFreshAssistantTurns = 1;

	// What "far enough" means for the mechanical pass: it stands in for a full summarization only if
	// it leaves the conversation at or under this share of the window. Anything more than this and
	// the successor is born nearly full and comes straight back for another compaction.
	private const int kTargetWindowPercent = 50;

	// Builds the elided successor history, or returns null when the elision alone would not get the
	// conversation under kTargetWindowPercent of the window and the caller should summarize instead.
	// measuredTokens is the provider's count for this conversation and windowTokens the model's
	// window; both come from real measurements, and the char ratio only scales one into a projection
	// of the other — an elision that removes half the characters removes about half the tokens.
	// endOfTurnPrompt is the role's own nudge text so injected copies of it can be recognized.
	// thinkingResent says the measurement includes the model's replayed reasoning. toolChars is the size
	// of the tool definitions, which the measurement also includes and the successor carries unchanged.
	// log collects what the pass took out; the caller saves it if it uses the result. reasoningSummaries
	// holds whatever summaries are ready, keyed by the trace.
	public static List<CanonicalMessage>? TryBuild(IReadOnlyList<CanonicalMessage> messages, string endOfTurnPrompt, int measuredTokens, int windowTokens, bool thinkingResent, int toolChars, CompactionLog log, IReadOnlyDictionary<string, string> reasoningSummaries)
	{
		List<CanonicalMessage>? result = null;

		// Thinking counts only where it is on the wire: leaving it out where it is replayed understates
		// what the pass frees, and counting it elsewhere would let a pass "succeed" without actually
		// freeing window space.
		List<CanonicalMessage> rebuilt = Elide(messages, endOfTurnPrompt, log, reasoningSummaries);
		long                   total   = CountChars(messages, thinkingResent);
		long                   kept    = CountChars( rebuilt, thinkingResent);

		if (total > 0 && windowTokens > 0 && measuredTokens > 0)
		{
			// The system prompt and tool definitions are part of the measurement but no elision shrinks
			// them, so they scale with neither side of the ratio's change: they stay in both.
			long fixedChars = toolChars + SystemPromptChars(messages);
			long projected  = measuredTokens * (kept + fixedChars) / (total + fixedChars);
			if (projected * 100 <= (long)windowTokens * kTargetWindowPercent)
			{
				result = rebuilt;
			}
		}
		else if (total > 0 && kept * 2 <= total)
		{
			// No measurement to project from (a conversation the provider has not sized yet). Fall
			// back to the same question asked of the text alone: did this halve the conversation?
			result = rebuilt;
		}

		return result;
	}

	// The elision itself, with no savings gate: always returns the rewritten history, which equals
	// the input when there is nothing stale enough to elide. Summarization runs this first — the
	// transcript it folds through the model is worth the same whether the bulky tool results are
	// present or replaced by their notes, and eliding them means far fewer stages to get there.
	public static List<CanonicalMessage> Elide(IReadOnlyList<CanonicalMessage> messages, string endOfTurnPrompt, CompactionLog log, IReadOnlyDictionary<string, string> reasoningSummaries)
	{
		List<CanonicalMessage> rebuilt = new List<CanonicalMessage>(messages.Count);

		// 0 protects everything: a conversation with a single turn has nothing stale to rewrite.
		int protectStart = ProtectedStart(messages);

		Dictionary<string, ToolResultMessage> resultsById = new Dictionary<string, ToolResultMessage>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is ToolResultMessage tr)
			{
				resultsById[tr.ToolCallId] = tr;
			}
		}

		List<DigestLine> digest = new List<DigestLine>();
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
				FlushDigest(rebuilt, digest, log);
				rebuilt.Add(msg);
			}
			else if (msg is UserMessage um)
			{
				// A stale ledger is stripped (a fresh one is emitted by the caller) and the system's whip
				// messages carry nothing once their turn is past. Everything the user actually typed stays
				// verbatim, and so does an earlier digest, which is already as short as it gets.
				if (!um.Text.StartsWith(LedgerMarker, StringComparison.Ordinal) && !Nudges.IsNudge(um.Text, endOfTurnPrompt))
				{
					FlushDigest(rebuilt, digest, log);
					rebuilt.Add(um);
				}
			}
			else if (msg is AssistantMessage am)
			{
				DigestTurn(am, resultsById, digest, log, reasoningSummaries);
			}

			// A tool result is folded into the digest with its call.
		}

		return rebuilt;
	}

	// One line of a digest. Text is what the line says before its log range; consecutive lines saying the
	// same thing collapse into one with a count, spanning all their ranges.
	private sealed class DigestLine
	{
		public string Text = string.Empty;
		public int    Count;
		public int    First;
		public int    Last;
	}

	// The assistant's words, its reasoning where a summary of it is ready, and a line for each call.
	private static void DigestTurn(AssistantMessage am, Dictionary<string, ToolResultMessage> resultsById, List<DigestLine> digest, CompactionLog log, IReadOnlyDictionary<string, string> reasoningSummaries)
	{
		if (am.Thinking.Contains(kReasoningMarker, StringComparison.Ordinal))
		{
			// Reduced by an earlier version of this pass: what is left is the summary and its reference.
			digest.Add(new DigestLine { Text = $"You thought: {am.Thinking.Trim()}" });
		}
		else if (reasoningSummaries.TryGetValue(am.Thinking, out string? summary))
		{
			(int first, int last) = log.Add("reasoning", am.Thinking);
			digest.Add(new DigestLine { Text = $"You thought: {summary.Trim()}", Count = 1, First = first, Last = last });
		}

		if (!string.IsNullOrWhiteSpace(am.Text))
		{
			digest.Add(new DigestLine { Text = $"You: {am.Text.Trim()}" });
		}

		foreach (SemanticToolCall call in am.ToolCalls)
		{
			resultsById.TryGetValue(call.Id, out ToolResultMessage? result);
			(int first, int last) = log.Add(RealToolName(call), LogBody(call, result));
			string      text      = $"- {CallLine(call, result)}";
			DigestLine? previous  = digest.Count > 0 ? digest[digest.Count - 1] : null;
			if (previous != null && previous.Count > 0 && previous.Text == text)
			{
				previous.Count++;
				previous.Last = last;
			}
			else
			{
				digest.Add(new DigestLine { Text = text, Count = 1, First = first, Last = last });
			}
		}
	}

	// Ends the current stretch: its lines become one digest message, headed by the log they point into.
	private static void FlushDigest(List<CanonicalMessage> rebuilt, List<DigestLine> digest, CompactionLog log)
	{
		if (digest.Count > 0)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append(DigestMarker).Append(" Earlier turns, shortened. Full calls and output: ").Append(log.FilePath);
			foreach (DigestLine line in digest)
			{
				sb.Append('\n').Append(line.Text);
				if (line.Count > 1)
				{
					sb.Append($" (×{line.Count}, lines {line.First}-{line.Last})");
				}
				else if (line.Count == 1)
				{
					sb.Append($" (lines {line.First}-{line.Last})");
				}
			}

			rebuilt.Add(new UserMessage(sb.ToString()));
			digest.Clear();
		}
	}

	// Tells a successor what its digests are. Part of the ledger message, so a later compaction replaces it
	// along with the ledger instead of stacking them up.
	public static string WithDigestNote(string ledger)
	{
		string note = $"Messages that begin {DigestMarker} are Beast's shortened account of earlier turns, not the user's words: what you wrote, and a line for each tool call you made. Each line ends with the lines of the named log file that hold that call and its output in full; read_file, grep and sed can all read it.";
		return ledger.Length > 0 ? $"{ledger}\n\n{note}" : $"{LedgerMarker}\n{note}";
	}

	public static bool HasDigest(IReadOnlyList<CanonicalMessage> messages)
	{
		bool found = false;
		foreach (CanonicalMessage msg in messages)
		{
			found = found || (msg is UserMessage um && um.Text.StartsWith(DigestMarker, StringComparison.Ordinal));
		}
		return found;
	}

	// The record's name as a tool a model can be shown, for a history that holds records. A model copies what
	// it sees, and a server that constrains output to the declared tools (llama-server's tool grammar) cannot
	// emit a name outside them: with 182 records in a history it failed every request with a 500 before the
	// copy could be answered. Declared, the copy arrives, and the answer tells the model to call the real tool.
	public static Tool MemoryTool()
	{
		return new Tool
		{
			Definition = new ToolDefinition
			{
				Type     = "function",
				Function = new FunctionDefinition
				{
					Name        = MemoryToolName,
					Description = "Not a callable tool. Entries under this name are records of earlier tool calls, left where the calls were. Calling it does nothing and returns an error: call the real tool the record names instead.",
					Parameters  = new JsonObject
					{
						["type"]       = "object",
						["properties"] = new JsonObject
						{
							["tool"] = new JsonObject { ["type"] = "string", ["description"] = "The real tool the record stands for." }
						}
					}
				}
			},
			Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) => Task.FromResult(ToolDispatch.MemoryRecordResult(toolCallId))
		};
	}

	public static bool HasMemoryCalls(IReadOnlyList<CanonicalMessage> messages)
	{
		bool found = false;
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					found = found || string.Equals(tc.Name, MemoryToolName, StringComparison.Ordinal);
				}
			}
		}
		return found;
	}

	// Builds the file ledger from the tool calls in the history: the files successfully created or modified,
	// in first-changed order. Deterministic — the paths come straight from the recorded arguments, never
	// from a model's guess. A history that was compacted before has its earlier calls only as digest lines,
	// so the files its own ledger listed come first. Returns string.Empty when the conversation changed no files.
	public static string BuildLedger(IReadOnlyList<CanonicalMessage> messages)
	{
		HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is ToolResultMessage tr && tr.ExitCode.HasValue && tr.ExitCode.Value != 0)
			{
				failed.Add(tr.ToolCallId);
			}
		}

		List<string>    order = new List<string>();
		HashSet<string> seen  = new HashSet<string>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is UserMessage um && um.Text.StartsWith(LedgerMarker, StringComparison.Ordinal))
			{
				foreach (string path in LedgerFiles(um.Text))
				{
					if (seen.Add(path))
					{
						order.Add(path);
					}
				}
			}
			else if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					string path = ChangedPath(tc);
					if (path.Length > 0 && !failed.Contains(tc.Id) && seen.Add(path))
					{
						order.Add(path);
					}
				}
			}
		}

		string ledger = string.Empty;
		if (order.Count > 0)
		{
			ledger = $"{LedgerMarker}\n{kLedgerHeading}\n{string.Join("\n", order)}";
		}

		return ledger;
	}

	private const string kLedgerHeading = "Files this conversation has created or modified:";

	// The files a ledger message lists: the lines under its heading, up to the blank line before its note.
	private static List<string> LedgerFiles(string ledger)
	{
		List<string> files = new List<string>();
		int          at    = ledger.IndexOf(kLedgerHeading, StringComparison.Ordinal);
		if (at >= 0)
		{
			string[] lines = ledger.Substring(at + kLedgerHeading.Length).Split('\n');
			bool     ended = false;
			foreach (string line in lines)
			{
				string path = line.Trim();
				ended       = ended || (path.Length == 0 && files.Count > 0);
				if (!ended && path.Length > 0)
				{
					files.Add(path);
				}
			}
		}
		return files;
	}

	// Splits history into the settled prefix compaction may rewrite and the in-flight tail it must
	// not touch. The tail starts at the FIRST assistant turn whose tool calls have no results yet, so
	// whatever comes back from the split, the settled part is guaranteed to contain no unanswered
	// call — neither pass may elide or summarize away half of a call/result pair that is still being
	// formed. Returns the whole history as settled (and an empty tail) in the ordinary case where
	// every call has been answered.
	public static (List<CanonicalMessage> Settled, List<CanonicalMessage> Pending) SplitPending(IReadOnlyList<CanonicalMessage> messages)
	{
		HashSet<string> satisfied = new HashSet<string>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is ToolResultMessage tr)
			{
				satisfied.Add(tr.ToolCallId);
			}
		}

		int split = messages.Count;
		for (int i = 0; i < messages.Count && split == messages.Count; i++)
		{
			if (messages[i] is AssistantMessage am)
			{
				foreach (SemanticToolCall call in am.ToolCalls)
				{
					if (!satisfied.Contains(call.Id))
					{
						split = i;
						break;
					}
				}
			}
		}

		List<CanonicalMessage> settled = new List<CanonicalMessage>(split);
		List<CanonicalMessage> pending = new List<CanonicalMessage>(messages.Count - split);
		for (int i = 0; i < messages.Count; i++)
		{
			if (i < split)
			{
				settled.Add(messages[i]);
			}
			else
			{
				pending.Add(messages[i]);
			}
		}
		return (settled, pending);
	}

	// Closes any tool call in the held-aside tail that is still unanswered once compaction is done.
	// Re-attaching an open call verbatim would leave the successor malformed — every protocol rejects
	// a call with no result — so each one gets a truthful note in its result slot instead. The model
	// sees the call it made, sees that it never came back, and can reissue it; the pairing rules the
	// providers enforce are satisfied either way.
	public static void CloseOpenCalls(List<CanonicalMessage> pending)
	{
		HashSet<string> satisfied = new HashSet<string>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in pending)
		{
			if (msg is ToolResultMessage tr)
			{
				satisfied.Add(tr.ToolCallId);
			}
		}

		List<CanonicalMessage> closers = new List<CanonicalMessage>();
		foreach (CanonicalMessage msg in pending)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall call in am.ToolCalls)
				{
					if (satisfied.Add(call.Id))
					{
						closers.Add(new ToolResultMessage(call.Id, $"[no result: the {call.Name} call was still open when the conversation was compacted — call it again if you still need it]"));
					}
				}
			}
		}
		pending.AddRange(closers);
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

	// ---- Digest lines ----

	private static bool IsShellTool(string tool)
	{
		return string.Equals(tool, "bash", StringComparison.Ordinal) || string.Equals(tool, "readonly_bash", StringComparison.Ordinal) || string.Equals(tool, "host_command", StringComparison.Ordinal);
	}

	private static bool IsFileTool(string tool)
	{
		return string.Equals(tool, "read_file", StringComparison.Ordinal) || string.Equals(tool, "write_file", StringComparison.Ordinal) || string.Equals(tool, "edit_file", StringComparison.Ordinal);
	}

	// The tool a call really was: a record from an earlier version of this pass names it in its arguments.
	private static string RealToolName(SemanticToolCall call)
	{
		string tool = call.Name;
		if (string.Equals(call.Name, MemoryToolName, StringComparison.Ordinal))
		{
			JsonObject? args = ParseArguments(call.ArgumentsJson);
			if (args != null && args.TryGetPropertyValue("tool", out JsonNode? node) && node != null)
			{
				tool = node.ToString();
			}
		}
		return tool;
	}

	// What a digest line says about a call, before its log range: the tool, what it was for, and how it went.
	private static string CallLine(SemanticToolCall call, ToolResultMessage? result)
	{
		string tool    = RealToolName(call);
		string what    = Describe(call, tool, result);
		string outcome = Outcome(tool, result);
		return what.Length > 0 ? $"{tool}: {what}{outcome}" : $"{tool}{outcome}";
	}

	// A file call by its path (and range, for a read or an edit) and its note; a subagent's report by the
	// summary it gave; anything else by its note, or failing that a short form of its arguments.
	private static string Describe(SemanticToolCall call, string tool, ToolResultMessage? result)
	{
		JsonObject? args    = ParseArguments(call.ArgumentsJson);
		string      why     = WhyOf(call, tool);
		string      summary = result != null ? ReportSummary(result.Content) : string.Empty;

		string what;
		if (IsFileTool(tool) && args != null && args.TryGetPropertyValue("file_path", out JsonNode? pathNode) && pathNode != null)
		{
			string range = string.Equals(tool, "read_file", StringComparison.Ordinal) ? ReadRange(args) : EditedLinesNote(call, result);
			what         = why.Length > 0 ? $"{pathNode}{range}, {why}" : $"{pathNode}{range}";
		}
		else if (summary.Length > 0)
		{
			what = summary;
		}
		else if (why.Length > 0)
		{
			what = why;
		}
		else
		{
			what = ArgSummary(call.ArgumentsJson);
		}
		return what;
	}

	// A failure always shows, with its first line unless a report's summary already says what happened.
	// Otherwise a short one-line result is stated outright, unless it is a file's contents or a report.
	// Media names its files.
	private static string Outcome(string tool, ToolResultMessage? result)
	{
		string outcome = string.Empty;
		if (result == null)
		{
			outcome = " → no result";
		}
		else if (result.Media != null && result.Media.Count > 0)
		{
			outcome = $" → media {MediaPaths(result.Media)}";
		}
		else
		{
			string content = result.Content.Trim();
			string first   = FirstLine(content);
			bool   report  = ReportSummary(content).Length > 0;
			if (result.ExitCode.HasValue && result.ExitCode.Value != 0)
			{
				outcome = first.Length > 0 && !report ? $" → failed (exit {result.ExitCode.Value}): {Clip(first, kMaxInlineResultChars)}" : $" → failed (exit {result.ExitCode.Value})";
			}
			else if (content.Length > 0 && content.Length <= kMaxInlineResultChars && first.Length == content.Length
				&& !string.Equals(tool, "read_file", StringComparison.Ordinal) && !report)
			{
				outcome = $" → {content}";
			}
		}
		return outcome;
	}

	// The one line a subagent's report opens with (SummaryPrefix), or "" when it has none. A failed report
	// arrives as error output, after the "stderr: " the result text gives it.
	private static string ReportSummary(string content)
	{
		string text = content.TrimStart();
		if (text.StartsWith("stderr: ", StringComparison.Ordinal))
		{
			text = text.Substring("stderr: ".Length).TrimStart();
		}

		string summary = string.Empty;
		if (text.StartsWith(SummaryPrefix, StringComparison.Ordinal))
		{
			summary = Clip(FirstLine(text.Substring(SummaryPrefix.Length)).Trim(), kMaxSummaryChars);
		}
		return summary;
	}

	private static string ReadRange(JsonObject args)
	{
		string offset = args.TryGetPropertyValue("offset", out JsonNode? o) && o != null ? o.ToString() : string.Empty;
		string lines  = args.TryGetPropertyValue("lines",  out JsonNode? l) && l != null ? l.ToString() : string.Empty;
		string range  = string.Empty;
		if (offset.Length > 0 && lines.Length > 0)
		{
			range = $" from line {offset}, {lines} lines";
		}
		else if (offset.Length > 0)
		{
			range = $" from line {offset}";
		}
		else if (lines.Length > 0)
		{
			range = $", {lines} lines";
		}
		return range;
	}

	private static string EditedLinesNote(SemanticToolCall call, ToolResultMessage? result)
	{
		string lines = EditedLines(call, result);
		return lines.Length > 0 ? $" lines {lines}" : string.Empty;
	}

	private static string FirstLine(string text)
	{
		int newline = text.IndexOf('\n');
		return (newline >= 0 ? text.Substring(0, newline) : text).TrimEnd();
	}

	private static string Clip(string text, int max)
	{
		return text.Length > max ? $"{text.Substring(0, max)}…" : text;
	}

	// A call as the log keeps it, with each argument as plain text so its lines read as they were, then its
	// exit code and output.
	private static string LogBody(SemanticToolCall call, ToolResultMessage? result)
	{
		StringBuilder sb   = new StringBuilder();
		JsonObject?   args = ParseArguments(call.ArgumentsJson);
		if (args != null)
		{
			foreach ((string key, JsonNode? value) in args)
			{
				sb.Append(key).Append(":\n").Append(value?.ToString() ?? string.Empty).Append('\n');
			}
		}
		else
		{
			sb.Append("arguments:\n").Append(call.ArgumentsJson).Append('\n');
		}

		if (result != null)
		{
			sb.Append(ExitLine(result)).Append("output:\n").Append(result.Content);
		}
		else
		{
			sb.Append("output:\n(none: the call never returned)");
		}
		return sb.ToString();
	}

	// Roughly where an edit landed, from the line its result reports and the size of the new text. Only as
	// good as when it was made: later edits move things, and the model can work that out.
	private static string EditedLines(SemanticToolCall call, ToolResultMessage? result)
	{
		const string header = "Edit applied at line ";
		string range = string.Empty;
		if (string.Equals(RealToolName(call), "edit_file", StringComparison.Ordinal) && result != null && result.Content.StartsWith(header, StringComparison.Ordinal))
		{
			int colon = result.Content.IndexOf(':');
			if (colon > 0 && int.TryParse(result.Content.Substring(header.Length, colon - header.Length), out int first))
			{
				JsonObject? args = ParseArguments(call.ArgumentsJson);
				string      text = args != null && args.TryGetPropertyValue("new_text", out JsonNode? node) && node != null ? node.ToString() : string.Empty;
				int         last = first + Math.Max(0, text.Split('\n').Length - 1);
				range            = last > first ? $"{first}-{last}" : $"{first}";
			}
		}
		return range;
	}

	private static string ExitLine(ToolResultMessage result)
	{
		return result.ExitCode.HasValue ? $"exit {result.ExitCode.Value}\n" : string.Empty;
	}

	private static JsonObject? ParseArguments(string argsJson)
	{
		JsonObject? args = null;
		try
		{
			args = JsonNode.Parse(argsJson) as JsonObject;
		}
		catch (System.Text.Json.JsonException)
		{
		}
		return args;
	}

	private static string CommandOf(string argsJson)
	{
		JsonObject? args = ParseArguments(argsJson);
		return args != null && args.TryGetPropertyValue("command", out JsonNode? node) && node != null ? node.ToString() : argsJson;
	}

	private static string WhyOf(SemanticToolCall call, string tool)
	{
		JsonObject? args = ParseArguments(call.ArgumentsJson);
		string      why  = args != null && args.TryGetPropertyValue(WhyArgument, out JsonNode? node) && node != null ? node.ToString().Trim() : string.Empty;

		string result = string.Empty;
		if (why.Length > 0)
		{
			result = why.Length > kMaxWhyChars ? why.Substring(0, kMaxWhyChars) : why;
		}
		else if (IsShellTool(tool))
		{
			string command = CommandOf(call.ArgumentsJson).TrimStart();
			int    newline = command.IndexOf('\n');
			string first   = (newline >= 0 ? command.Substring(0, newline) : command).TrimEnd();
			result         = first.Length > kMaxFallbackWhyChars ? first.Substring(0, kMaxFallbackWhyChars) : first;
		}
		return result;
	}

	// ---- Reasoning ----

	// The traces a pass over these messages would take out, once each and oldest first, that are long enough
	// to be worth a summary. The protected tail keeps its reasoning, and one already reduced has nothing left.
	public static List<string> StaleTraces(IReadOnlyList<CanonicalMessage> messages, int minChars)
	{
		List<string>    traces = new List<string>();
		HashSet<string> seen   = new HashSet<string>(StringComparer.Ordinal);
		int             stale  = ProtectedStart(messages);
		for (int i = 0; i < stale; i++)
		{
			if (messages[i] is AssistantMessage am && am.Thinking.Length >= minChars
				&& !am.Thinking.Contains(kReasoningMarker, StringComparison.Ordinal) && seen.Add(am.Thinking))
			{
				traces.Add(am.Thinking);
			}
		}
		return traces;
	}

	// ---- Argument summaries ----

	// The files a result showed, comma-separated, so the line says where to look again.
	private static string MediaPaths(IReadOnlyList<MediaFile> media)
	{
		List<string> paths = new List<string>();
		foreach (MediaFile file in media)
		{
			paths.Add(file.Path);
		}

		return string.Join(", ", paths);
	}

	// Compact "key: value" rendering of a call's arguments for a digest line, with long values
	// truncated so a line can never itself be bulky.
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
					{
						text = text.Substring(0, 60) + "…";
					}

					if (sb.Length > 0)
					{
						sb.Append(", ");
					}

					sb.Append(key).Append(": ").Append(text);
					if (sb.Length > 160)
					{
						break;
					}
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

	// ---- Internals ----

	// Index of the first message in the protected tail: the span from the kFreshAssistantTurns-th
	// assistant message (counting from the end) onward stays verbatim. 0 protects everything —
	// a conversation that short has nothing safely elidable.
	private static int ProtectedStart(IReadOnlyList<CanonicalMessage> messages)
	{
		return TailStart(messages, kFreshAssistantTurns);
	}

	// Index where the last `assistantTurns` turns begin, counting an assistant message as the head of
	// a turn. Returns 0 when the conversation has fewer turns than asked for, which reads naturally as
	// "all of it is tail" — a caller retreating its summarize point uses that to know it has run out
	// of history to summarize.
	public static int TailStart(IReadOnlyList<CanonicalMessage> messages, int assistantTurns)
	{
		int start = 0;
		int seen  = 0;
		for (int i = messages.Count - 1; i >= 0; i--)
		{
			if (messages[i] is AssistantMessage)
			{
				seen++;
				if (seen == assistantTurns)
				{
					start = i;
					break;
				}
			}
		}
		return start;
	}

	// The size of the tool definitions a model is sent with every request, as the wire carries them.
	public static int ToolDefinitionChars(Tool[] tools)
	{
		int total = 0;
		foreach (Tool tool in tools)
		{
			FunctionDefinition function = tool.Definition.Function;
			total                      += function.Name.Length + function.Description.Length + function.Parameters.ToJsonString().Length;
		}
		return total;
	}

	private static int SystemPromptChars(IReadOnlyList<CanonicalMessage> messages)
	{
		int total = 0;
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is SystemMessage system)
			{
				total += system.Text.Length;
			}
		}
		return total;
	}

	// Chars of conversation the wire actually carries again next turn: user text, assistant text, tool
	// call arguments, and tool result contents, plus thinking where it is replayed (see TryBuild).
	private static long CountChars(IReadOnlyList<CanonicalMessage> messages, bool includeThinking)
	{
		long total = 0;
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is UserMessage um)
			{
				total += um.Text.Length;
			}
			else if (msg is AssistantMessage am)
			{
				total += am.Text.Length;
				if (includeThinking)
				{
					total += am.Thinking.Length;
				}

				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					total += tc.ArgumentsJson.Length;
				}
			}
			else if (msg is ToolResultMessage tr)
			{
				total += tr.Content.Length;
			}
		}
		return total;
	}

	// The file a write or edit changed, or "" for any other call. A record made by an earlier version of this
	// pass still counts, so the list carries through.
	private static string ChangedPath(SemanticToolCall call)
	{
		string      path = string.Empty;
		JsonObject? args = ParseArguments(call.ArgumentsJson);
		if (args != null)
		{
			string tool    = RealToolName(call);
			bool   changes = string.Equals(     tool,   "write_file", StringComparison.Ordinal) || string.Equals(tool, "edit_file", StringComparison.Ordinal);
			if (changes && args.TryGetPropertyValue("file_path", out JsonNode? pathNode) && pathNode != null)
			{
				path = pathNode.ToString();
			}
		}
		return path;
	}
}