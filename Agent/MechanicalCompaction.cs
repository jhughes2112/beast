using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;


// First-pass compaction that rewrites history deterministically, with no LLM call at all. Tool
// traffic is the bulk of most conversations and the most re-derivable content — a file can be
// re-read, a command re-run — and the assistant's own following text usually states what it
// concluded from a result. So stale, bulky tool results are replaced with a one-line note built
// from the call's own arguments, oversized tool-call arguments (whole file bodies in write_file)
// are elided to a small stub, stale reasoning traces move to the compaction log, and the whip messages
// the system injected to keep the model working are dropped. Real user messages are ALWAYS kept verbatim:
// user text is small and irreplaceable, tool output is huge and re-derivable.
//
// Shell calls can change things, so re-running one is not a way to find out what it did. Each stale one
// goes to the compaction log whole and leaves a tool_call_memory call in its place: the note the model
// wrote when it ran the command, the exit code, and where the rest is. That tool does not exist, so a
// model copying the records it sees can never run anything.
//
// File calls become the same records, at any size, whenever the record is the smaller: a read keeps its
// path and range, an edit its note, path and rough line range, a write its note and path. What they read or
// wrote is in the log, and the file itself is there to read again.
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

	// The record a stale shell call becomes. Never offered to a model, so never run.
	public const string MemoryToolName = "tool_call_memory";

	// The required note on what a shell command, edit or write is for, which outlives the call.
	public const string WhyArgument = "why";

	private const int kMaxWhyChars = 128;

	// Without a note (a session from before they existed, or a model that left it out) the command's first
	// line stands in for it.
	private const int kMaxFallbackWhyChars = 200;

	// A short argument costs about what the word replacing it would, so it stays.
	private const int kMaxVerbatimArgChars = 10;

	private const string kElidedArg = "elided";

	// How a reasoning reference starts, so a later compaction can recognize one and leave it alone.
	private const string kReasoningMarker = "[reasoning: ";

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
				result = rebuilt;
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

		int protectStart = ProtectedStart(messages);
		if (protectStart > 0)
		{
			// A block must be a meaningful fraction of the whole to be worth eliding.
			long totalChars = CountChars(messages, false);
			long threshold  = totalChars / kSizeFractionDenominator;

			// Tool results are elided only when their call is present to describe them.
			Dictionary<string, SemanticToolCall>  callsById   = new Dictionary<string, SemanticToolCall>(StringComparer.Ordinal);
			Dictionary<string, ToolResultMessage> resultsById = new Dictionary<string, ToolResultMessage>(StringComparer.Ordinal);
			foreach (CanonicalMessage msg in messages)
			{
				if (msg is AssistantMessage am)
				{
					foreach (SemanticToolCall tc in am.ToolCalls)
						callsById[tc.Id] = tc;
				}
				else if (msg is ToolResultMessage tr)
				{
					resultsById[tr.ToolCallId] = tr;
				}
			}

			// The calls this pass turned into records, whose results become the records' results.
			HashSet<string> recorded = new HashSet<string>(StringComparer.Ordinal);

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
						resultsById.TryGetValue(tc.Id, out ToolResultMessage? callResult);
						if (IsShellCall(tc) || FileRecordIsSmaller(tc, callResult, log))
						{
							recorded.Add(tc.Id);
							calls.Add(new SemanticToolCall { Id = tc.Id, Name = MemoryToolName, ArgumentsJson = MemoryArguments(tc, callResult) });
						}
						else if (tc.ArgumentsJson.Length >= kMinArgsChars && tc.ArgumentsJson.Length >= threshold)
							calls.Add(new SemanticToolCall { Id = tc.Id, Name = tc.Name, ArgumentsJson = ElideArguments(tc.ArgumentsJson) });
						else
							calls.Add(tc);
					}
					rebuilt.Add(new AssistantMessage(am.Text, ElideThinking(am.Thinking, log, reasoningSummaries), calls));
				}
				else if (msg is ToolResultMessage tr)
				{
					// A result carrying media is elided on size of the media, not its one-line text:
					// the file is what fills the window, and the note keeps the path so the model
					// can ask for it again.
					bool bulky = tr.Content.Length >= kMinResultChars && tr.Content.Length >= threshold;
					if (recorded.Contains(tr.ToolCallId) && callsById.TryGetValue(tr.ToolCallId, out SemanticToolCall? recordedCall))
						rebuilt.Add(new ToolResultMessage(tr.ToolCallId, MemoryResult(recordedCall!, tr, log), null, tr.ExitCode));
					else if (tr.Media != null && tr.Media.Count > 0 && callsById.TryGetValue(tr.ToolCallId, out SemanticToolCall? mediaCall))
						rebuilt.Add(new ToolResultMessage(tr.ToolCallId, $"[elided media {MediaPaths(tr.Media)}: {mediaCall!.Name}({ArgSummary(mediaCall.ArgumentsJson)})]"));
					else if (bulky && callsById.TryGetValue(tr.ToolCallId, out SemanticToolCall? call))
						rebuilt.Add(new ToolResultMessage(tr.ToolCallId, ResultNote(call!, tr.Content.Length)));
					else
						rebuilt.Add(tr);
				}
			}
		}
		else
		{
			// Too short to have a stale span: nothing is safely elidable, so the history stands as
			// it is (minus the system prompt, which the successor's role re-injects).
			foreach (CanonicalMessage msg in messages)
			{
				if (!(msg is SystemMessage))
					rebuilt.Add(msg);
			}
		}

		return rebuilt;
	}

	// Tells a successor what the records in its history are. Part of the ledger message, so a later
	// compaction replaces it along with the ledger instead of stacking them up.
	public static string WithMemoryNote(string ledger)
	{
		string note = $"{MemoryToolName} entries are records of earlier tool calls, not a tool: call the real tool instead. Each result gives the exit code and, when something was cut, where the full call is. Reasoning references point the same way. read_file reads those files, with offset and lines.";
		return ledger.Length > 0 ? $"{ledger}\n\n{note}" : $"{LedgerMarker}\n{note}";
	}

	public static bool HasMemoryCalls(IReadOnlyList<CanonicalMessage> messages)
	{
		bool found = false;
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall tc in am.ToolCalls)
					found = found || string.Equals(tc.Name, MemoryToolName, StringComparison.Ordinal);
			}
		}
		return found;
	}

	// Builds the file ledger from the tool calls in the history: the files successfully created or modified,
	// in first-changed order. Deterministic — the paths come straight from the recorded arguments, never
	// from a model's guess. Returns string.Empty when the conversation changed no files.
	public static string BuildLedger(IReadOnlyList<CanonicalMessage> messages)
	{
		HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is ToolResultMessage tr && tr.ExitCode.HasValue && tr.ExitCode.Value != 0)
				failed.Add(tr.ToolCallId);
		}

		List<string>    order = new List<string>();
		HashSet<string> seen  = new HashSet<string>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					string path = ChangedPath(tc);
					if (path.Length > 0 && !failed.Contains(tc.Id) && seen.Add(path))
						order.Add(path);
				}
			}
		}

		string ledger = string.Empty;
		if (order.Count > 0)
			ledger = $"{LedgerMarker}\nFiles this conversation has created or modified:\n{string.Join("\n", order)}";
		return ledger;
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
				satisfied.Add(tr.ToolCallId);
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
				settled.Add(messages[i]);
			else
				pending.Add(messages[i]);
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
				satisfied.Add(tr.ToolCallId);
		}

		List<CanonicalMessage> closers = new List<CanonicalMessage>();
		foreach (CanonicalMessage msg in pending)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall call in am.ToolCalls)
				{
					if (satisfied.Add(call.Id))
						closers.Add(new ToolResultMessage(call.Id, $"[no result: the {call.Name} call was still open when the conversation was compacted — call it again if you still need it]"));
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

	// ---- Shell call records ----

	private static bool IsShellCall(SemanticToolCall call)
	{
		return string.Equals(call.Name, "bash", StringComparison.Ordinal) || string.Equals(call.Name, "readonly_bash", StringComparison.Ordinal) || string.Equals(call.Name, "host_command", StringComparison.Ordinal);
	}

	private static bool IsFileCall(SemanticToolCall call)
	{
		return string.Equals(call.Name, "read_file", StringComparison.Ordinal) || string.Equals(call.Name, "write_file", StringComparison.Ordinal) || string.Equals(call.Name, "edit_file", StringComparison.Ordinal);
	}

	// A small file call stays as it is when its record would cost more. A result carrying media keeps its
	// own elision, which names the file.
	private static bool FileRecordIsSmaller(SemanticToolCall call, ToolResultMessage? result, CompactionLog log)
	{
		bool smaller = false;
		if (IsFileCall(call) && result != null && (result.Media == null || result.Media.Count == 0))
		{
			(string text, bool _, string _, string _) = BuildMemoryResult(call, result, log);
			smaller = MemoryArguments(call, result).Length + text.Length < call.ArgumentsJson.Length + result.Content.Length;
		}
		return smaller;
	}

	// The record's arguments: the tool it was, the note on why, and every other argument as itself if
	// short or as "elided" if not. Empty ones are left out, having nothing to say. File calls keep their path
	// and drop what they wrote, adding roughly where an edit landed.
	private static string MemoryArguments(SemanticToolCall call, ToolResultMessage? result)
	{
		JsonObject memory = new JsonObject { ["tool"] = call.Name };
		string     why    = WhyOf(call);
		if (why.Length > 0)
			memory[WhyArgument] = why;

		JsonObject? args = ParseArguments(call.ArgumentsJson);
		if (args != null)
		{
			foreach ((string key, JsonNode? value) in args)
			{
				string text = value?.ToString() ?? string.Empty;
				if (IsFileCall(call) && (key == "content" || key == "old_text" || key == "new_text"))
					continue;
				bool verbatim = text.Length <= kMaxVerbatimArgChars || key == "file_path";
				if (!string.Equals(key, WhyArgument, StringComparison.Ordinal) && text.Length > 0)
					memory[key] = verbatim ? value!.DeepClone() : kElidedArg;
			}
		}

		string edited = EditedLines(call, result);
		if (edited.Length > 0)
			memory["lines"] = edited;
		return memory.ToJsonString();
	}

	// The record's result: the exit code, the output if it is shorter than a pointer to it, and a pointer
	// to the log whenever something was cut. A short command with short output loses nothing and needs none.
	private static string MemoryResult(SemanticToolCall call, ToolResultMessage result, CompactionLog log)
	{
		(string text, bool cut, string heading, string body) = BuildMemoryResult(call, result, log);
		if (cut)
			log.Add(heading, body);
		return text;
	}

	private static (string Text, bool Cut, string Heading, string Body) BuildMemoryResult(SemanticToolCall call, ToolResultMessage result, CompactionLog log)
	{
		// The name alone: the model can read this log, and a call id differs on every run.
		string heading    = call.Name;
		string body       = IsFileCall(call) ? FileCallBody(call, result) : $"{WhyArgument}: {WhyOf(call)}\ncommand:\n{CommandOf(call.ArgumentsJson)}\n{ExitLine(result)}output:\n{result.Content}";
		string pointer    = $"tool call is in file {log.ReferenceTo(heading, body)}";
		bool   keepOutput = result.Content.Length <= pointer.Length;
		bool   cut        = IsFileCall(call) || !keepOutput || CommandOf(call.ArgumentsJson).Length > kMaxVerbatimArgChars;

		List<string> lines = new List<string>();
		if (result.ExitCode.HasValue)
			lines.Add($"exit {result.ExitCode.Value}");
		if (keepOutput && result.Content.Length > 0)
			lines.Add(result.Content);
		if (cut)
			lines.Add(pointer);
		return (string.Join("\n", lines), cut, heading, body);
	}

	// A file call as the log keeps it, with each argument as plain text so its lines read as they were.
	private static string FileCallBody(SemanticToolCall call, ToolResultMessage result)
	{
		StringBuilder sb   = new StringBuilder();
		JsonObject?   args = ParseArguments(call.ArgumentsJson);
		if (args != null)
		{
			foreach ((string key, JsonNode? value) in args)
				sb.Append(key).Append(":\n").Append(value?.ToString() ?? string.Empty).Append('\n');
		}
		sb.Append(ExitLine(result)).Append("output:\n").Append(result.Content);
		return sb.ToString();
	}

	// Roughly where an edit landed, from the line its result reports and the size of the new text. Only as
	// good as when it was made: later edits move things, and the model can work that out.
	private static string EditedLines(SemanticToolCall call, ToolResultMessage? result)
	{
		const string header = "Edit applied at line ";
		string range = string.Empty;
		if (string.Equals(call.Name, "edit_file", StringComparison.Ordinal) && result != null && result.Content.StartsWith(header, StringComparison.Ordinal))
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

	private static string WhyOf(SemanticToolCall call)
	{
		JsonObject? args = ParseArguments(call.ArgumentsJson);
		string      why  = args != null && args.TryGetPropertyValue(WhyArgument, out JsonNode? node) && node != null ? node.ToString().Trim() : string.Empty;

		string result = string.Empty;
		if (why.Length > 0)
		{
			result = why.Length > kMaxWhyChars ? why.Substring(0, kMaxWhyChars) : why;
		}
		else if (IsShellCall(call))
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
				traces.Add(am.Thinking);
		}
		return traces;
	}

	// A stale reasoning trace goes to the log whole, and what stays is the reference to it, after a summary
	// if one is ready. A trace under twice the size of that replacement stays as it is: swapping it would
	// save next to nothing and lose the words. So does one a previous compaction already reduced.
	private static string ElideThinking(string thinking, CompactionLog log, IReadOnlyDictionary<string, string> reasoningSummaries)
	{
		string result = thinking;
		if (thinking.Length > 0 && !thinking.Contains(kReasoningMarker, StringComparison.Ordinal))
		{
			const string heading = "reasoning";
			string reference   = $"{kReasoningMarker}{log.ReferenceTo(heading, thinking)}]";
			string replacement = reasoningSummaries.TryGetValue(thinking, out string? summary) ? $"{summary}\n{reference}" : reference;
			if (thinking.Length >= replacement.Length * 2)
			{
				log.Add(heading, thinking);
				result = replacement;
			}
		}
		return result;
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

	// The files a result showed, comma-separated, so the note says where to look again.
	private static string MediaPaths(IReadOnlyList<MediaFile> media)
	{
		List<string> paths = new List<string>();
		foreach (MediaFile file in media)
			paths.Add(file.Path);
		return string.Join(", ", paths);
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
				total += system.Text.Length;
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
					total += am.Thinking.Length;
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

	// The file a write or edit changed, or "" for any other call. A record made by an earlier compaction
	// still counts, so the list carries through successive ones.
	private static string ChangedPath(SemanticToolCall call)
	{
		string      path = string.Empty;
		JsonObject? args = ParseArguments(call.ArgumentsJson);
		if (args != null)
		{
			string tool    = string.Equals(call.Name, MemoryToolName, StringComparison.Ordinal) && args.TryGetPropertyValue("tool", out JsonNode? toolNode) && toolNode != null ? toolNode.ToString() : call.Name;
			bool   changes = string.Equals(     tool,   "write_file", StringComparison.Ordinal) || string.Equals(tool, "edit_file", StringComparison.Ordinal);
			if (changes && args.TryGetPropertyValue("file_path", out JsonNode? pathNode) && pathNode != null)
				path = pathNode.ToString();
		}
		return path;
	}
}