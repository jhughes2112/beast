using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;


// Unit tests for the mechanical compaction pass: stale-result elision, oversized-argument stubs,
// thinking cuts, nudge and stale-ledger stripping, the too-small-to-matter bailout, the file
// ledger builder, and the nudge recognizers. The rehydration test then runs the rebuilt history
// through all three protocols' wire converters â€” the pass is only safe if every protocol still
// produces a well-formed request from it â€” and the persistence test round-trips it through the
// source-generated serializer the sessions are saved with.
public static class MechanicalCompactionTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  MechanicalCompactionTests");

		TestTryBuildElides(ctx);
		TestTryBuildBailsOut(ctx);
		TestProjectionAccounting(ctx);
		TestShellRecords(ctx);
		TestReasoningMoves(ctx);
		TestStaleTraces(ctx);
		TestLogAccessAndExitCode(ctx);
		TestBuildLedger(ctx);
		TestResumePrompt(ctx);
		TestOpenToolCallsHeldAside(ctx);
		TestRetreatBoundaries(ctx);
		TestNudgeRecognition(ctx);
		TestRehydratesOnEveryProtocol(ctx);
		TestPersistRoundTrip(ctx);
	}

	// The tests describe a conversation that exactly fills its window, so the "does this get us under
	// half the window" gate reduces to the plainest form of the same question: did the pass halve it?
	private const int kFullWindow = 10000;

	private static readonly Dictionary<string, string> NoSummaries = new Dictionary<string, string>();

	// In memory only until something saves it; nothing here reaches the path unless a test asks.
	private static CompactionLog NewLog()
	{
		return new CompactionLog(Path.Combine(Path.GetTempPath(), $"beast-compaction-{Guid.NewGuid():N}.log"));
	}

	// Enough later turns that everything before them is stale.
	private static void AddFreshTurns(List<CanonicalMessage> messages)
	{
		messages.Add(new AssistantMessage("second", string.Empty, null));
		messages.Add(new AssistantMessage("third", string.Empty, null));
		messages.Add(new AssistantMessage("done", string.Empty, null));
	}

	// The lines a "lines X to Y" reference names, read back from the saved file the way read_file counts them.
	private static string ReadReference(string path, string text)
	{
		string   marker = " lines ";
		int      at     = text.LastIndexOf(marker, StringComparison.Ordinal);
		string[] range  = text.Substring(at + marker.Length).TrimEnd(']').Split(" to ");
		int      first  = int.Parse(range[0]);
		int      last   = int.Parse(range[1]);
		string[] lines  = File.ReadAllText(path).Split('\n');
		return string.Join("\n", lines, first - 1, last - first + 1);
	}

	private static void TestShellRecords(TestContext ctx)
	{
		string                 longCommand = "python3 - <<'EOF'\nprint('hello')\nEOF";
		string                 longOutput  = new string('o', 2000) + "\nlast line";
		List<CanonicalMessage> messages    = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("running", string.Empty, new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "s1", Name = "bash", ArgumentsJson = new JsonObject { ["why"] = "Check the greeting prints", ["command"] = longCommand, ["timeout_seconds"] = 30 }.ToJsonString() },
				new SemanticToolCall { Id = "s2", Name = "readonly_bash", ArgumentsJson = new JsonObject { ["why"] = "List files", ["command"] = "ls" }.ToJsonString() },
				new SemanticToolCall { Id = "s3", Name = "bash", ArgumentsJson = new JsonObject { ["command"] = "make test && echo finished\nsecond line" }.ToJsonString() },
				new SemanticToolCall { Id = "s4", Name = "bash", ArgumentsJson = new JsonObject { ["why"] = new string('w', 300), ["command"] = "pwd" }.ToJsonString() },
				new SemanticToolCall { Id = "s5", Name = "host_command", ArgumentsJson = new JsonObject { ["why"] = "Check the host's GPU", ["shell"] = "bash", ["command"] = "nvidia-smi --query-gpu=name --format=csv" }.ToJsonString() }
			}),
			new ToolResultMessage("s1", longOutput, null, 1),
			new ToolResultMessage("s2",    "a.txt", null, 0),
			new ToolResultMessage("s3", "ok"),
			new ToolResultMessage("s4", "/workspace",     null, 0),
			new ToolResultMessage("s5", "NVIDIA GeForce", null, 0)
		};
		AddFreshTurns(messages);

		CompactionLog          log     = NewLog();
		List<CanonicalMessage> rebuilt = MechanicalCompaction.Elide(messages, string.Empty, log, NoSummaries);
		log.Save();
		try
		{
			AssistantMessage turn = (AssistantMessage)rebuilt[1];
			ctx.AssertEqual(MechanicalCompaction.MemoryToolName, turn.ToolCalls[0].Name, "Shell: a stale call becomes a record");
			ctx.AssertEqual("s1", turn.ToolCalls[0].Id, "Shell: paired with its result as before");

			JsonNode first = JsonNode.Parse(turn.ToolCalls[0].ArgumentsJson)!;
			ctx.AssertEqual("bash",                         first["tool"]!.GetValue<string>(), "Shell: the record names the real tool");
			ctx.AssertEqual("Check the greeting prints",     first["why"]!.GetValue<string>(), "Shell: and keeps the note");
			ctx.AssertEqual("elided",                    first["command"]!.GetValue<string>(), "Shell: a long command is cut");
			ctx.AssertEqual(30, first["timeout_seconds"]!.GetValue<int>(), "Shell: a short argument is stated");

			ctx.AssertEqual("ls",                         JsonNode.Parse(turn.ToolCalls[1].ArgumentsJson)!["command"]!.GetValue<string>(), "Shell: a short command is stated too");
			ctx.AssertEqual("make test && echo finished",     JsonNode.Parse(turn.ToolCalls[2].ArgumentsJson)!["why"]!.GetValue<string>(), "Shell: with no note, the first line stands in");
			ctx.AssertEqual(128, JsonNode.Parse(turn.ToolCalls[3].ArgumentsJson)!["why"]!.GetValue<string>().Length, "Shell: a long note is cut to size");

			JsonNode host = JsonNode.Parse(turn.ToolCalls[4].ArgumentsJson)!;
			ctx.AssertEqual(MechanicalCompaction.MemoryToolName,           turn.ToolCalls[4].Name, "Shell: a stale host_command becomes a record too");
			ctx.AssertEqual(                     "host_command", host["tool"]!.GetValue<string>(), "Shell: named as host_command");
			ctx.AssertEqual("Check the host's GPU",   host["why"]!.GetValue<string>(), "Shell: with its note");
			ctx.AssertEqual("bash",                 host["shell"]!.GetValue<string>(), "Shell: and the shell it ran in");

			string cut = ((ToolResultMessage)rebuilt[2]).Content;
			ctx.Assert(cut.StartsWith("exit 1\ntool call is in file ", StringComparison.Ordinal), $"Shell: the exit code and the pointer ({cut})");
			ctx.Assert(                     !cut.Contains("last line", StringComparison.Ordinal), "Shell: long output is gone from the conversation");
			string chunk = ReadReference(log.FilePath, cut);
			ctx.Assert(chunk.StartsWith("bash\n", StringComparison.Ordinal) && chunk.EndsWith("last line", StringComparison.Ordinal), "Shell: the pointer's lines are exactly the chunk, under the tool's name with no call id");
			ctx.AssertContains(chunk, longCommand, "Shell: the chunk has the whole command");
			ctx.AssertContains(chunk,    "exit 1", "Shell: and the exit code");

			ctx.AssertEqual("exit 0\na.txt", ((ToolResultMessage)rebuilt[3]).Content, "Shell: short output and a short command need no pointer");
			string kept = ((ToolResultMessage)rebuilt[4]).Content;
			ctx.Assert(kept.StartsWith("ok\ntool call is in file ", StringComparison.Ordinal), $"Shell: short output stays, a cut command still gets a pointer, and an unknown exit code is left out ({kept})");
			ctx.AssertEqual("exit 0\n/workspace", ((ToolResultMessage)rebuilt[5]).Content, "Shell: a long note alone sends nothing to the log");

			string secondChunk = ReadReference(log.FilePath, kept);
			ctx.Assert(secondChunk.StartsWith("bash\n", StringComparison.Ordinal) && secondChunk.EndsWith("ok", StringComparison.Ordinal), "Shell: later chunks' lines are right too");
			ctx.AssertContains(File.ReadAllText(log.FilePath), "----------", "Shell: chunks are divided");

			// A later compaction leaves a record alone: its pointer is into a log that still exists.
			List<CanonicalMessage> again = MechanicalCompaction.Elide(rebuilt, string.Empty, NewLog(), NoSummaries);
			ctx.AssertEqual(turn.ToolCalls[0].ArgumentsJson, ((AssistantMessage)again[1]).ToolCalls[0].ArgumentsJson, "Shell: a record survives a second pass unchanged");
			ctx.AssertEqual(cut, ((ToolResultMessage)again[2]).Content, "Shell: and so does its result");
			ctx.Assert(MechanicalCompaction.HasMemoryCalls(again), "Shell: the history can be asked whether it holds records");

			// A recent call stays real: only stale ones become records.
			List<CanonicalMessage> fresh = new List<CanonicalMessage>
			{
				new UserMessage("go"),
				new AssistantMessage("old", string.Empty, null),
				new AssistantMessage("now", string.Empty, new List<SemanticToolCall> { new SemanticToolCall { Id = "f1", Name = "bash", ArgumentsJson = "{\"command\":\"ls\"}" } }),
				new ToolResultMessage("f1", "a.txt", null, 0),
				new AssistantMessage("next", string.Empty, null),
				new AssistantMessage("done", string.Empty, null)
			};
			List<CanonicalMessage> tail = MechanicalCompaction.Elide(fresh, string.Empty, NewLog(), NoSummaries);
			ctx.AssertEqual("bash", ((AssistantMessage)tail[2]).ToolCalls[0].Name, "Shell: a recent call stays as it was");

			// Models copying a record get told, and nothing runs.
			Dictionary<string, Tool> noTools = new Dictionary<string, Tool>();
			ToolResult               copied  = ToolDispatch.ExecuteToolAsync(new SemanticToolCall { Id = "c1", Name = MechanicalCompaction.MemoryToolName, ArgumentsJson = "{}" }, noTools, "s", 100, new TransportSilent(), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
			ctx.Assert(copied.ExitCode != 0 && copied.StdErr.Contains("not a tool", StringComparison.Ordinal), "Shell: calling a record explains itself");

			string note = MechanicalCompaction.WithMemoryNote(string.Empty);
			ctx.Assert(note.StartsWith(MechanicalCompaction.LedgerMarker, StringComparison.Ordinal), "Shell: the note counts as the ledger, so the next compaction replaces it");

			(JsonObject? noWhy, string? noWhyError) = FixJson.TryParseWithSchema("{\"command\":\"ls\"}", ShellSchema(), null);
			ctx.Assert(noWhy != null && noWhyError == null, "Shell: a command with no note still runs");
			(JsonObject? _, string? noCommandError) = FixJson.TryParseWithSchema("{\"why\":\"x\"}", ShellSchema(), null);
			ctx.Assert(noCommandError != null, "Shell: a note with no command does not");
		}
		finally
		{
			File.Delete(log.FilePath);
		}
	}

	private static FunctionDefinition ShellSchema()
	{
		JsonObject parameters = new JsonObject
		{
			["type"]       = "object",
			["properties"] = new JsonObject { ["why"] = new JsonObject { ["type"] = "string" }, ["command"] = new JsonObject { ["type"] = "string" } },
			["required"]   = new JsonArray("why", "command")
		};
		return new FunctionDefinition { Name = "bash", Parameters = parameters };
	}

	private static void TestReasoningMoves(TestContext ctx)
	{
		string                 longTrace   = "first thought\n" + new string('t', 2000);
		string                 summedTrace = "summed thought\n" + new string('u', 2000);
		string                 shortTrace  = "brief";
		List<CanonicalMessage> messages    = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("one",     longTrace, null),
			new AssistantMessage("two",    shortTrace, null),
			new AssistantMessage("three", summedTrace, null)
		};
		AddFreshTurns(messages);
		Dictionary<string, string> summaries = new Dictionary<string, string> { { summedTrace, "Concluded the shader was wrong." } };

		CompactionLog          log     = NewLog();
		List<CanonicalMessage> rebuilt = MechanicalCompaction.Elide(messages, string.Empty, log, summaries);
		log.Save();
		try
		{
			string reference = ((AssistantMessage)rebuilt[1]).Thinking;
			ctx.Assert(reference.StartsWith("[reasoning: ", StringComparison.Ordinal) && !reference.Contains("first thought", StringComparison.Ordinal), $"Reasoning: only a reference stays ({reference})");
			ctx.AssertEqual("reasoning\n" + longTrace, ReadReference(log.FilePath, reference), "Reasoning: the log has the original");

			ctx.AssertEqual(shortTrace, ((AssistantMessage)rebuilt[2]).Thinking, "Reasoning: a short trace stays intact");

			string summed = ((AssistantMessage)rebuilt[3]).Thinking;
			ctx.Assert(summed.StartsWith("Concluded the shader was wrong.\n[reasoning: ", StringComparison.Ordinal), $"Reasoning: a summary goes ahead of the reference ({summed})");
			ctx.AssertEqual("reasoning\n" + summedTrace, ReadReference(log.FilePath, summed), "Reasoning: and the log still has the original");

			List<CanonicalMessage> again = MechanicalCompaction.Elide(rebuilt, string.Empty, NewLog(), NoSummaries);
			ctx.AssertEqual(summed, ((AssistantMessage)again[3]).Thinking, "Reasoning: a later compaction leaves a summary and reference alone");

			// The cutoff is twice the replacement's size, summary included.
			string                 worthIt  = new string('x', reference.Length * 2 + 8);
			string                 notWorth = new string('y', reference.Length * 2 - 8);
			List<CanonicalMessage> edge     = new List<CanonicalMessage> { new UserMessage("go"), new AssistantMessage("a", worthIt, null), new AssistantMessage("b", notWorth, null) };
			AddFreshTurns(edge);
			List<CanonicalMessage> edged = MechanicalCompaction.Elide(edge, string.Empty, NewLog(), NoSummaries);
			ctx.Assert(((AssistantMessage)edged[1]).Thinking != worthIt, "Reasoning: at twice the reference it moves");
			ctx.AssertEqual(notWorth, ((AssistantMessage)edged[2]).Thinking, "Reasoning: under twice the reference it stays");
		}
		finally
		{
			File.Delete(log.FilePath);
		}
	}

	private static void TestLogAccessAndExitCode(TestContext ctx)
	{
		string logPath = CompactionLog.PathFor("some-session");
		ctx.Assert(Sandbox.Confine(logPath, false, false).Path != null, "Log: the model may read a compaction log");
		ctx.Assert( Sandbox.Confine(logPath, true, false).Path == null, "Log: but never write one");
		ctx.Assert(Sandbox.Confine(Path.Combine(AgentState.LogsDir, "some-session.log"), false, false).Path == null, "Log: the session logs stay off limits");

		// The path goes in the prompt, so it comes from the session's ID alone, which repeats whatever order conversations compact in.
		ctx.AssertEqual("compaction_1_1.log", Path.GetFileName(CompactionLog.PathFor("1_1")), "Log: named for the session's ID");

		CanonicalConversation canonical = new CanonicalConversation(new List<CanonicalMessage>());
		canonical.OnToolResult(new ToolResult("t1", "out", string.Empty, 7, 0));
		ToolResultMessage recorded = (ToolResultMessage)canonical.Messages[0];
		ctx.AssertEqual(    7, recorded.ExitCode ?? -1, "ExitCode: reaches the saved history");
		ctx.AssertEqual("out",        recorded.Content, "ExitCode: without changing what the model is sent");

		BeastSession session = new BeastSession("x", "x", "m", "r", string.Empty, new List<CanonicalMessage> { recorded, new ToolResultMessage("t2", "old") }, null, 0m, 0, 0, 0, true);
		string       json    = JsonSerializer.Serialize(session, BeastJson.Persist.BeastSession);
		BeastSession back    = JsonSerializer.Deserialize(json, BeastJson.Persist.BeastSession)!;
		ctx.AssertEqual(7, ((ToolResultMessage)back.Messages[0]).ExitCode ?? -1, "ExitCode: survives save and load");
		ctx.Assert(((ToolResultMessage)back.Messages[1]).ExitCode == null, "ExitCode: an older result has none, rather than a guess");
	}

	private static List<CanonicalMessage> BuildConversation(string bigResult)
	{
		string bigArgs = "{\"file_path\":\"b.cs\",\"content\":\"" + new string('y', 3000) + "\"}";
		return new List<CanonicalMessage>
		{
			new SystemMessage("sys prompt"),
			new UserMessage(MechanicalCompaction.LedgerMarker + "\nstale ledger line"),
			new UserMessage("do the thing"),
			new AssistantMessage("working on it", "private reasoning", new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "id1", Name = "read_file", ArgumentsJson = "{\"file_path\":\"a.cs\",\"offset\":10,\"lines\":500}" },
				new SemanticToolCall { Id = "id2", Name = "write_file", ArgumentsJson = bigArgs }
			}),
			new ToolResultMessage("id1", bigResult),
			new ToolResultMessage("id2",      "ok"),
			new UserMessage(Nudges.ContinueTask("return_to_caller")),
			new AssistantMessage("second turn", "", null),
			new AssistantMessage("third turn",  "", null),
			new AssistantMessage("done",        "", null)
		};
	}

	// A conversation can reach compaction with tool calls still unanswered â€” a session restored from
	// an interrupted save is the common one. Whatever compaction does with the rest of the history,
	// it must never hand its successor a call with no result: every protocol rejects that outright
	// ("No tool output found for function call ..."), which would take the successor down on its
	// very first request â€” at the exact moment the session has nowhere else to go.
	private static void TestOpenToolCallsHeldAside(TestContext ctx)
	{
		List<CanonicalMessage> messages = BuildConversation(new string('x', 5000));
		messages.Add(new AssistantMessage("now reading the last file", string.Empty, new List<SemanticToolCall>
		{
			new SemanticToolCall { Id = "open1", Name = "read_file", ArgumentsJson = "{\"file_path\":\"z.cs\"}" }
		}));

		// The split is what protects it: the open turn is not history to rewrite, so it lands in the
		// pending tail and the settled prefix both passes operate on contains no half-formed pair.
		(List<CanonicalMessage> settled, List<CanonicalMessage> pending) = MechanicalCompaction.SplitPending(messages);
		ctx.AssertEqual(0, UnansweredCalls(settled), "OpenCalls: the settled prefix compaction rewrites has no unanswered call");
		ctx.AssertEqual(                 1, pending.Count, "OpenCalls: the in-flight turn is held aside");
		ctx.AssertEqual(messages.Count - 1, settled.Count, "OpenCalls: everything before it stays compactable");

		// Both successor shapes then carry the tail back, closed, so the successor is well-formed.
		List<CanonicalMessage>? mechanical = MechanicalCompaction.TryBuild(settled, "keep going", kFullWindow, kFullWindow, false, 0, NewLog(), NoSummaries);
		ctx.AssertNotNull(mechanical, "OpenCalls: the mechanical pass still runs on the settled prefix");
		if (mechanical != null)
		{
			MechanicalCompaction.CloseOpenCalls(pending);
			mechanical.AddRange(pending);
			ctx.AssertEqual(0, UnansweredCalls(mechanical), "OpenCalls: mechanical successor carries no unanswered tool call");
			ctx.AssertContains(((ToolResultMessage)mechanical[mechanical.Count - 1]).Content, "still open when the conversation was compacted",
				"OpenCalls: the reattached call is answered with what actually happened to it");
			ctx.Assert(MechanicalCompaction.NeedsResumePrompt(mechanical), "OpenCalls: a successor ending on the closed call is prodded to continue");
		}

		// The summarize path folds the same settled prefix, so its transcript never contains the
		// half-formed pair either â€” the call is re-attached to the summary afterwards, not into it.
		List<CanonicalMessage> elided = MechanicalCompaction.Elide(settled, "keep going", NewLog(), NoSummaries);
		ctx.AssertEqual(0, UnansweredCalls(elided), "OpenCalls: the elided history the summarizer folds carries no unanswered tool call");

		// CloseOpenCalls only fills genuinely empty slots: a call already answered is left alone.
		List<CanonicalMessage> answered = new List<CanonicalMessage>
		{
			new AssistantMessage("done", string.Empty, new List<SemanticToolCall> { new SemanticToolCall { Id = "a1", Name = "read_file", ArgumentsJson = "{}" } }),
			new ToolResultMessage("a1", "real content")
		};
		MechanicalCompaction.CloseOpenCalls(answered);
		ctx.AssertEqual(2, answered.Count, "OpenCalls: an answered call gets no second result");
	}

	// The boundary the summarize retreat walks: keep the last N turns verbatim, summarize everything
	// in front of them. Each retreat keeps MORE recent history and hands the summarizer LESS to fold,
	// so a summarize that could not complete over the whole backlog gets a smaller job rather than a
	// failure — and the tail the model needs to carry on from is never the part that got summarized.
	private static void TestRetreatBoundaries(TestContext ctx)
	{
		List<CanonicalMessage> messages = new List<CanonicalMessage>();
		for (int i = 0; i < 10; i++)
		{
			messages.Add(new UserMessage($"ask {i}"));
			messages.Add(new AssistantMessage($"answer {i}", "", null));
		}

		int keep2 = MechanicalCompaction.TailStart(messages, 2);
		int keep4 = MechanicalCompaction.TailStart(messages, 4);
		int keep8 = MechanicalCompaction.TailStart(messages, 8);

		// Keeping more turns moves the boundary EARLIER, so each retreat summarizes strictly less.
		ctx.Assert(keep4 < keep2, "Retreat: keeping 4 turns summarizes less than keeping 2");
		ctx.Assert(keep8 < keep4, "Retreat: keeping 8 turns summarizes less than keeping 4");

		// The boundary always lands on an assistant message — the head of a turn — so a retreat never
		// splits a turn's own tool traffic across the summary/verbatim line.
		ctx.Assert(messages[keep2] is AssistantMessage, "Retreat: the boundary is a turn boundary");
		ctx.Assert(messages[keep8] is AssistantMessage, "Retreat: still a turn boundary further back");

		// Asking to keep more turns than exist reports 0: there is nothing ahead of the tail left to
		// summarize, which is how the retreat knows to stop rather than summarize an empty prefix.
		ctx.AssertEqual(0, MechanicalCompaction.TailStart(messages, 50), "Retreat: exhausted when the retreat outruns the conversation");
	}

	// Counts assistant tool calls with no matching tool result anywhere in the list.
	private static int UnansweredCalls(IReadOnlyList<CanonicalMessage> messages)
	{
		HashSet<string> satisfied = new HashSet<string>(StringComparer.Ordinal);
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is ToolResultMessage tr)
				satisfied.Add(tr.ToolCallId);
		}

		int open = 0;
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall call in am.ToolCalls)
				{
					if (!satisfied.Contains(call.Id))
						open++;
				}
			}
		}
		return open;
	}

	private static void TestTryBuildElides(TestContext ctx)
	{
		// The last three assistant turns are protected; everything before them is elidable.
		List<CanonicalMessage> messages = BuildConversation(new string('x', 5000));

		List<CanonicalMessage>? rebuilt = MechanicalCompaction.TryBuild(messages, "keep going", kFullWindow, kFullWindow, false, 0, NewLog(), NoSummaries);
		ctx.AssertNotNull(rebuilt, "TryBuild: elides a conversation dominated by stale tool traffic");

		// System message dropped (the successor re-injects the role prompt), stale ledger and the
		// nudge stripped, the real user message kept verbatim.
		int userCount = 0;
		foreach (CanonicalMessage msg in rebuilt!)
		{
			ctx.Assert(!(msg is SystemMessage), "TryBuild: no system message in the rebuilt history");
			if (msg is UserMessage um)
			{
				userCount++;
				ctx.AssertEqual("do the thing", um.Text, "TryBuild: real user text kept verbatim");
			}
		}
		ctx.AssertEqual(1, userCount, "TryBuild: ledger and nudge user messages stripped");

		// The stale big result is replaced with a note naming the call; the small one is untouched.
		ToolResultMessage? big   = null;
		ToolResultMessage? small = null;
		foreach (CanonicalMessage msg in rebuilt)
		{
			if (msg is ToolResultMessage tr)
			{
				if (tr.ToolCallId == "id1")
					big = tr;
				else if (tr.ToolCallId == "id2")
					small = tr;
			}
		}
		ctx.AssertNotNull(big, "TryBuild: elided result still present (pairing intact)");
		// The full note is pinned so any drift in what the model actually reads shows up in review.
		// It identifies the call and states the size, and stops there: the note repeats once per
		// elided result, so every extra word is space the pass just failed to reclaim.
		ctx.Assert(      big!.Content.StartsWith("tool call is in file ", StringComparison.Ordinal), $"TryBuild: a stale read's contents become a pointer ({big.Content})");
		ctx.Assert(small!.Content.StartsWith("ok\ntool call is in file ", StringComparison.Ordinal), $"TryBuild: a write's short result stays, with its pointer ({small.Content})");

		// Thinking too short to be worth a reference stays, and stale file calls become records that keep
		// their paths and read ranges and lose the contents.
		AssistantMessage first = (AssistantMessage)rebuilt[1];
		ctx.AssertEqual("private reasoning", first.Thinking, "TryBuild: short stale thinking stays");
		ctx.AssertContains(first.ToolCalls[0].ArgumentsJson, "\"offset\":10", "TryBuild: a read record keeps its range");
		ctx.AssertEqual(MechanicalCompaction.MemoryToolName, first.ToolCalls[1].Name, "TryBuild: an oversized write becomes a record");
		ctx.AssertContains(first.ToolCalls[1].ArgumentsJson, "b.cs", "TryBuild: and keeps file_path");
		ctx.Assert(first.ToolCalls[1].ArgumentsJson.Length < 100, "TryBuild: without its contents");

		// The protected tail is verbatim.
		AssistantMessage last = (AssistantMessage)rebuilt[rebuilt.Count - 1];
		ctx.AssertEqual("done", last.Text, "TryBuild: fresh tail kept verbatim");
	}

	// The projection must count what the measurement counted: replayed thinking is freed by the pass, and
	// the system prompt and tool definitions are not.
	private static void TestProjectionAccounting(TestContext ctx)
	{
		// Nothing here is elidable but the stale thinking, so whether the pass frees enough rests on it.
		List<CanonicalMessage> thoughtful = new List<CanonicalMessage>
		{
			new UserMessage("do the thing"),
			new AssistantMessage("working", new string('t', 9000), null),
			new AssistantMessage("second",           string.Empty, null),
			new AssistantMessage("third",            string.Empty, null),
			new AssistantMessage("done",             string.Empty, null)
		};
		ctx.AssertNull(MechanicalCompaction.TryBuild(thoughtful, string.Empty, kFullWindow, kFullWindow, false, 0, NewLog(), NoSummaries), "Projection: thinking the wire never carried frees nothing");
		ctx.AssertNotNull(MechanicalCompaction.TryBuild(thoughtful, string.Empty, kFullWindow, kFullWindow, true, 0, NewLog(), NoSummaries), "Projection: replayed thinking counts as freed");
		ctx.AssertNotNull(          MechanicalCompaction.TryBuild(thoughtful, string.Empty, 0, kFullWindow, true, 0, NewLog(), NoSummaries), "Projection: with no measurement too");

		// Thinking in the protected tail survives the pass, so it is not counted as freed.
		List<CanonicalMessage> freshThought = new List<CanonicalMessage>
		{
			new UserMessage("do the thing"),
			new AssistantMessage("working",          string.Empty, null),
			new AssistantMessage("second",  new string('t', 9000), null),
			new AssistantMessage("third",            string.Empty, null),
			new AssistantMessage("done",             string.Empty, null)
		};
		ctx.AssertNull(MechanicalCompaction.TryBuild(freshThought, string.Empty, kFullWindow, kFullWindow, true, 0, NewLog(), NoSummaries), "Projection: recent thinking stays, so it frees nothing");

		// Tool traffic that would clear the bar alone does not once fixed overhead is most of the measurement.
		List<CanonicalMessage> plain = BuildConversation(new string('x', 5000));
		ctx.AssertNotNull(MechanicalCompaction.TryBuild(plain, "keep going", kFullWindow, kFullWindow, false, 0, NewLog(), NoSummaries), "Projection: with no overhead the elision is enough");
		ctx.AssertNull(MechanicalCompaction.TryBuild(plain, "keep going", kFullWindow, kFullWindow, false, 40000, NewLog(), NoSummaries), "Projection: tool definitions do not shrink with the conversation");

		List<CanonicalMessage> longPrompt = BuildConversation(new string('x', 5000));
		longPrompt[0]                     = new SystemMessage(new string('s', 40000));
		ctx.AssertNull(MechanicalCompaction.TryBuild(longPrompt, "keep going", kFullWindow, kFullWindow, false, 0, NewLog(), NoSummaries), "Projection: neither does the system prompt");

		Tool tool                     = new Tool();
		tool.Definition.Function.Name = "abc";
		tool.Definition.Function.Description = "defgh";
		tool.Definition.Function.Parameters  = new JsonObject();
		ctx.AssertEqual(10, MechanicalCompaction.ToolDefinitionChars(new Tool[] { tool }), "Projection: a tool's size is its name, description and schema");
	}

	private static void TestTryBuildBailsOut(TestContext ctx)
	{
		// Small results leave nothing worth reclaiming below the savings threshold... except the
		// big write args still elide, so shrink those too by keeping the result tiny and the args
		// under the floor. Build a conversation whose only elidable content is trivial.
		List<CanonicalMessage> messages = new List<CanonicalMessage>
		{
			new UserMessage("do the thing and here is a long description of what to do so the total is not tiny"),
			new AssistantMessage("working", "", new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "id1", Name = "read_file", ArgumentsJson = "{\"file_path\":\"a.cs\"}" }
			}),
			new ToolResultMessage("id1", "short contents"),
			new AssistantMessage("second", "", null),
			new AssistantMessage("third",  "", null),
			new AssistantMessage("done",   "", null)
		};
		ctx.AssertNull(MechanicalCompaction.TryBuild(messages, "", kFullWindow, kFullWindow, false, 0, NewLog(), NoSummaries), "TryBuild: null when the reclaimable space is too small to matter");

		// A conversation too short to have a stale prefix is never rewritten.
		List<CanonicalMessage> shortConv = new List<CanonicalMessage>
		{
			new UserMessage("hi"),
			new AssistantMessage("hello", "", null)
		};
		ctx.AssertNull(MechanicalCompaction.TryBuild(shortConv, "", kFullWindow, kFullWindow, false, 0, NewLog(), NoSummaries), "TryBuild: null when every turn is fresh");
	}

	// Only what a pass would take out is worth a summary, and only once.
	private static void TestStaleTraces(TestContext ctx)
	{
		string                 old      = new string('o', 700);
		string                 fresh    = new string('f', 700);
		string                 reduced  = "note\n[reasoning: x lines 1 to 2]" + new string('r', 700);
		List<CanonicalMessage> messages = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("a",               old, null),
			new AssistantMessage("b",               old, null),
			new AssistantMessage("c",           "short", null),
			new AssistantMessage("d",           reduced, null),
			new AssistantMessage("second",        fresh, null),
			new AssistantMessage("third",  string.Empty, null),
			new AssistantMessage("done",   string.Empty, null)
		};
		List<string> traces = MechanicalCompaction.StaleTraces(messages, 600);
		ctx.AssertEqual(1, traces.Count, "StaleTraces: one long stale trace, once, without the short, reduced or recent ones");
		ctx.AssertEqual(old, traces[0], "StaleTraces: and it is the old one");
		ctx.AssertEqual(0, MechanicalCompaction.StaleTraces(new List<CanonicalMessage> { new UserMessage("hi"), new AssistantMessage("x", old, null) }, 600).Count, "StaleTraces: a conversation with nothing stale has none");
	}

	private static void TestBuildLedger(TestContext ctx)
	{
		List<CanonicalMessage> messages = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("", "", new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "1", Name = "read_file", ArgumentsJson = "{\"file_path\":\"a.cs\",\"offset\":10,\"lines\":500}" },
				new SemanticToolCall { Id = "2", Name = "write_file", ArgumentsJson = "{\"file_path\":\"b.cs\",\"content\":\"data\"}" },
				new SemanticToolCall { Id = "3", Name = "edit_file", ArgumentsJson = "{\"file_path\":\"b.cs\",\"old_text\":\"x\",\"new_text\":\"y\"}" },
				new SemanticToolCall { Id = "4", Name = "edit_file", ArgumentsJson = "{\"file_path\":\"b.cs\",\"old_text\":\"p\",\"new_text\":\"q\"}" },
				new SemanticToolCall { Id = "5", Name = "bash", ArgumentsJson = "{\"command\":\"ls\"}" }
			})
		};

		// The whole ledger is pinned so the exact text the model reads is visible in the test.
		string ledger = MechanicalCompaction.BuildLedger(messages);
		ctx.AssertEqual(MechanicalCompaction.LedgerMarker + "\n"
			+ "Files this conversation has created or modified:\n"
			+ "b.cs",
			ledger, "BuildLedger: only changed files, each once");
		ctx.Assert(!ledger.Contains("a.cs"), "BuildLedger: reads are not listed");
		ctx.Assert(!ledger.Contains("bash"), "BuildLedger: non-file tools ignored");

		ctx.AssertEqual("", MechanicalCompaction.BuildLedger(new List<CanonicalMessage> { new UserMessage("hi") }), "BuildLedger: empty when no files changed");

		// A failed edit changed nothing, and an edit already turned into a record still did.
		List<CanonicalMessage> later = new List<CanonicalMessage>
		{
			new AssistantMessage("", "", new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "f", Name = "edit_file", ArgumentsJson = "{\"file_path\":\"failed.cs\",\"old_text\":\"x\",\"new_text\":\"y\"}" },
				new SemanticToolCall { Id = "r", Name = MechanicalCompaction.MemoryToolName, ArgumentsJson = "{\"tool\":\"edit_file\",\"file_path\":\"record.cs\"}" },
				new SemanticToolCall { Id = "s", Name = MechanicalCompaction.MemoryToolName, ArgumentsJson = "{\"tool\":\"read_file\",\"file_path\":\"seen.cs\"}" }
			}),
			new ToolResultMessage("f", "Error: not found", null, 1),
			new ToolResultMessage("r", "exit 0",           null, 0),
			new ToolResultMessage("s", "exit 0",           null, 0)
		};
		ctx.AssertEqual(MechanicalCompaction.LedgerMarker + "\nFiles this conversation has created or modified:\nrecord.cs",
			MechanicalCompaction.BuildLedger(later), "BuildLedger: failed edits are out, and records carry through");

		TestFileRecords(ctx);
	}

	// Stale file calls leave a note, a path and a range, with the contents in the log, unless that costs more.
	private static void TestFileRecords(TestContext ctx)
	{
		string                 body     = new string('b', 3000);
		List<CanonicalMessage> messages = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("working", string.Empty, new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "r1", Name = "read_file", ArgumentsJson = "{\"file_path\":\"src/a.cs\",\"offset\":10,\"lines\":50}" },
				new SemanticToolCall { Id = "e1", Name = "edit_file", ArgumentsJson = new JsonObject { ["why"] = "Fix the retry", ["file_path"] = "src/a.cs", ["old_text"] = new string('o', 400), ["new_text"] = "one\ntwo\nthree" }.ToJsonString() },
				new SemanticToolCall { Id = "w1", Name = "write_file", ArgumentsJson = new JsonObject { ["why"] = "Add the notes", ["file_path"] = "src/b.cs", ["content"] = body }.ToJsonString() },
				new SemanticToolCall { Id = "e2", Name = "edit_file", ArgumentsJson = "{\"file_path\":\"c\",\"old_text\":\"x\",\"new_text\":\"y\"}" },
				new SemanticToolCall { Id = "e3", Name = "edit_file", ArgumentsJson = new JsonObject { ["why"] = "Try it", ["file_path"] = "src/d.cs", ["old_text"] = new string('o', 400), ["new_text"] = "z" }.ToJsonString() }
			}),
			new ToolResultMessage("r1", new string('r', 2000), null, 0),
			new ToolResultMessage("e1", "Edit applied at line 40:\n" + new string('d', 300), null, 0),
			new ToolResultMessage("w1", "Wrote src/b.cs",            null, 0),
			new ToolResultMessage("e2", "ok",                        null, 0),
			new ToolResultMessage("e3", "Error: old_text not found", null, 1)
		};
		AddFreshTurns(messages);

		CompactionLog          log     = NewLog();
		List<CanonicalMessage> rebuilt = MechanicalCompaction.Elide(messages, string.Empty, log, NoSummaries);
		log.Save();
		try
		{
			AssistantMessage turn = (AssistantMessage)rebuilt[1];
			ctx.AssertEqual("{\"tool\":\"read_file\",\"file_path\":\"src/a.cs\",\"offset\":10,\"lines\":50}",                    turn.ToolCalls[0].ArgumentsJson, "FileRecords: a read keeps its path and range, with no note");
			ctx.AssertEqual("{\"tool\":\"edit_file\",\"why\":\"Fix the retry\",\"file_path\":\"src/a.cs\",\"lines\":\"40-42\"}", turn.ToolCalls[1].ArgumentsJson, "FileRecords: an edit keeps its note, path and rough lines");
			ctx.AssertEqual("{\"tool\":\"write_file\",\"why\":\"Add the notes\",\"file_path\":\"src/b.cs\"}",                    turn.ToolCalls[2].ArgumentsJson, "FileRecords: a write keeps its note and path");
			ctx.AssertEqual("edit_file", turn.ToolCalls[3].Name, "FileRecords: a call smaller than its record stays as it was");

			string readResult = ((ToolResultMessage)rebuilt[2]).Content;
			ctx.Assert(readResult.StartsWith("exit 0\ntool call is in file ", StringComparison.Ordinal), $"FileRecords: a read's contents are replaced by a pointer ({readResult})");
			string chunk = ReadReference(log.FilePath, readResult);
			ctx.Assert(chunk.StartsWith("read_file\n", StringComparison.Ordinal) && chunk.EndsWith(new string('r', 2000), StringComparison.Ordinal), "FileRecords: the pointer's lines hold what was read");

			string writeChunk = ReadReference(log.FilePath, ((ToolResultMessage)rebuilt[4]).Content);
			ctx.AssertContains(writeChunk,                  body, "FileRecords: the log has what was written");
			ctx.AssertContains(writeChunk, "why:\nAdd the notes", "FileRecords: and the note");

			ToolResultMessage failed = (ToolResultMessage)rebuilt[6];
			ctx.Assert(failed.ExitCode == 1 && failed.Content.StartsWith("exit 1\n", StringComparison.Ordinal), "FileRecords: a failure stays visible");
			ctx.AssertEqual("{\"tool\":\"edit_file\",\"why\":\"Try it\",\"file_path\":\"src/d.cs\"}", turn.ToolCalls[4].ArgumentsJson, "FileRecords: and claims no lines");
		}
		finally
		{
			File.Delete(log.FilePath);
		}
	}

	// Compaction fires mid-tool-round, so the elided history it hands over ends on tool results the
	// model has already been given â€” nothing there asks it for anything, and the successor would sit
	// idle exactly when the work should carry on. This is the rule that catches that.
	private static void TestResumePrompt(TestContext ctx)
	{
		List<CanonicalMessage> midToolRound = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("calling", "", new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "id1", Name = "read_file", ArgumentsJson = "{\"file_path\":\"a.cs\"}" }
			}),
			new ToolResultMessage("id1", "contents")
		};
		ctx.Assert(MechanicalCompaction.NeedsResumePrompt(midToolRound), "NeedsResumePrompt: history ending on a satisfied tool result would stall");

		List<CanonicalMessage> plainReply = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("here you go", "", null)
		};
		ctx.Assert(MechanicalCompaction.NeedsResumePrompt(plainReply), "NeedsResumePrompt: history ending on a plain assistant reply would stall");

		List<CanonicalMessage> awaitingModel = new List<CanonicalMessage>
		{
			new AssistantMessage("done", "", null),
			new UserMessage("now do the next thing")
		};
		ctx.Assert(!MechanicalCompaction.NeedsResumePrompt(awaitingModel), "NeedsResumePrompt: history already ending on user text needs nothing");

		// Appending user text after an unanswered call would break call/result pairing, so this
		// shape is never touched â€” and it already needs the model's attention anyway.
		List<CanonicalMessage> openCall = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("calling", "", new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "id1", Name = "read_file", ArgumentsJson = "{}" }
			})
		};
		ctx.Assert(!MechanicalCompaction.NeedsResumePrompt(openCall), "NeedsResumePrompt: an outstanding tool call is left alone");

		// The resume message is itself a whip: a later compaction must strip it, not carry it.
		ctx.Assert(Nudges.IsNudge(Nudges.ResumeAfterCompaction(), ""), "NeedsResumePrompt: the resume message is a strippable nudge");
	}

	// The successor exactly as CompactAsync seeds it: elided history headed by the ledger, with
	// the role's system prompt at the front (Session's constructor performs that insert).
	private static List<CanonicalMessage> BuildSuccessorSeed()
	{
		List<CanonicalMessage> messages = BuildConversation(new string('x', 5000));
		List<CanonicalMessage> rebuilt  = MechanicalCompaction.TryBuild(messages, "keep going", kFullWindow, kFullWindow, false, 0, NewLog(), NoSummaries)!;
		rebuilt.Insert(0, new UserMessage(MechanicalCompaction.BuildLedger(messages)));
		rebuilt.Insert(0, new SystemMessage("role prompt"));
		return rebuilt;
	}

	private static void TestRehydratesOnEveryProtocol(TestContext ctx)
	{
		List<CanonicalMessage> rebuilt = BuildSuccessorSeed();

		LlmModel      model = new LlmModel("test", "http://localhost", string.Empty, new List<JsonObject>(), new List<JsonObject>(), new ModelConfig());
		ProtocolProxy proxy = new ProtocolProxy(model);

		// Chat Completions: every tool-role message answers a known call, and the elided
		// write_file arguments are still valid JSON on the wire.
		ProtocolChatCompletions cc       = proxy.EnsureProtocolChatCompletions(rebuilt);
		JsonArray               ccNative = (JsonArray)Reflect.GetField(cc, "_native")!;
		HashSet<string>         ccIds    = new HashSet<string>(StringComparer.Ordinal);
		string?                 ccArgs   = null;
		foreach (JsonNode? node in ccNative)
		{
			JsonArray? calls = node?["tool_calls"]?.AsArray();
			if (calls != null)
			{
				foreach (JsonNode? call in calls)
				{
					ccIds.Add(call!["id"]!.GetValue<string>());
					if (call["function"]!["arguments"]!.GetValue<string>().Contains("write_file", StringComparison.Ordinal))
						ccArgs = call["function"]!["arguments"]!.GetValue<string>();
				}
			}
			if (node?["role"]?.GetValue<string>() == "tool")
				ctx.Assert(ccIds.Contains(node["tool_call_id"]!.GetValue<string>()), "Rehydrate CC: tool result answers a preceding call");
		}
		ctx.AssertNotNull(                 ccArgs, "Rehydrate CC: elided call present on the wire");
		ctx.AssertNotNull(JsonNode.Parse(ccArgs!), "Rehydrate CC: elided arguments are valid JSON");

		// Anthropic: strict role alternation must hold even with the nudge between assistant turns
		// removed, tool_use/tool_result ids must pair exactly, and the elided arguments must parse
		// into a real input object â€” ParseInput silently substitutes {} for garbage, so an empty
		// object here would mean the stub was not actually valid JSON.
		ProtocolAnthropic anthropic = proxy.EnsureProtocolAnthropic(rebuilt);
		JsonArray         anNative  = (JsonArray)Reflect.GetField(anthropic, "_native")!;
		string            prevRole  = string.Empty;
		HashSet<string>   useIds    = new HashSet<string>(StringComparer.Ordinal);
		HashSet<string>   resultIds = new HashSet<string>(StringComparer.Ordinal);
		JsonObject?       elided    = null;
		foreach (JsonNode? msg in anNative)
		{
			string msgRole = msg!["role"]!.GetValue<string>();
			ctx.Assert(msgRole != prevRole, "Rehydrate Anthropic: roles alternate after nudge removal");
			prevRole = msgRole;
			foreach (JsonNode? block in msg["content"]!.AsArray())
			{
				string type = block!["type"]!.GetValue<string>();
				if (type == "tool_use")
				{
					useIds.Add(block["id"]!.GetValue<string>());
					if (block["input"]!.ToJsonString().Contains("write_file", StringComparison.Ordinal))
						elided = block["input"] as JsonObject;
				}
				else if (type == "tool_result")
				{
					resultIds.Add(block["tool_use_id"]!.GetValue<string>());
				}
			}
		}
		ctx.Assert(                      useIds.SetEquals(resultIds), "Rehydrate Anthropic: tool_use and tool_result ids pair exactly");
		ctx.Assert(elided != null && elided.ContainsKey("file_path"), "Rehydrate Anthropic: elided arguments parsed into a real input object");

		// Responses: every function_call_output pairs with a preceding function_call by call_id.
		ProtocolResponses responses = proxy.EnsureProtocolResponses(rebuilt);
		JsonArray         input     = (JsonArray)Reflect.GetField(responses, "_rehydratedInput")!;
		HashSet<string>   fnIds     = new HashSet<string>(StringComparer.Ordinal);
		int               outputs   = 0;
		foreach (JsonNode? item in input)
		{
			string type = item!["type"]?.GetValue<string>() ?? string.Empty;
			if (type == "function_call")
			{
				fnIds.Add(item["call_id"]!.GetValue<string>());
			}
			else if (type == "function_call_output")
			{
				outputs++;
				ctx.Assert(fnIds.Contains(item["call_id"]!.GetValue<string>()), "Rehydrate Responses: output pairs with a preceding function_call");
			}
		}
		ctx.AssertEqual(2, outputs, "Rehydrate Responses: both tool results present");
	}

	private static void TestPersistRoundTrip(TestContext ctx)
	{
		// The rebuilt history must survive the same source-generated serializer sessions are saved
		// with, so a compacted successor written to disk reloads intact.
		List<CanonicalMessage> rebuilt = BuildSuccessorSeed();
		BeastSession           data    = new BeastSession("rt-id", "rt-name", "rt-model", "rt-role", string.Empty, rebuilt, null, 0m, 0, 0, 0, false);

		string        json   = JsonSerializer.Serialize(data, BeastJson.Persist.BeastSession);
		BeastSession? loaded = JsonSerializer.Deserialize(json, BeastJson.Persist.BeastSession);
		ctx.AssertNotNull(loaded, "PersistRoundTrip: deserializes");
		ctx.AssertEqual(rebuilt.Count, loaded!.Messages.Count, "PersistRoundTrip: message count survives");

		bool noteSurvived   = false;
		bool ledgerSurvived = false;
		foreach (CanonicalMessage msg in loaded.Messages)
		{
			if (msg is ToolResultMessage tr && tr.Content.Contains("tool call is in file ", StringComparison.Ordinal))
				noteSurvived = true;
			if (msg is UserMessage um && um.Text.StartsWith(MechanicalCompaction.LedgerMarker, StringComparison.Ordinal))
				ledgerSurvived = true;
		}
		ctx.Assert(  noteSurvived, "PersistRoundTrip: elision note survives save/load");
		ctx.Assert(ledgerSurvived, "PersistRoundTrip: ledger message survives save/load");
	}

	private static void TestNudgeRecognition(TestContext ctx)
	{
		ctx.Assert(        Nudges.IsNudge(Nudges.ContinueTask("return_to_caller"), ""), "IsNudge: continue-task template");
		ctx.Assert(             Nudges.IsNudge(Nudges.OutOfTurns("task_complete"), ""), "IsNudge: out-of-turns template");
		ctx.Assert(Nudges.IsNudge(Nudges.BudgetCheck(80, 48, 60, "task_complete"), ""), "IsNudge: budget check template");
		ctx.Assert(                       !Nudges.IsNudge(Nudges.BudgetNotice(60), ""), "IsNudge: the budget notice is task text, not a nudge");
		// No longer generated (replies are never rewritten to fit a caller's budget) but still
		// present in histories written by earlier versions, so it must stay strippable.
		ctx.Assert(Nudges.IsNudge("That output is about 900 tokens but must fit within 500 tokens.", ""), "IsNudge: legacy over-budget template");
		ctx.Assert(                    Nudges.IsNudge(Nudges.InvalidToolCall("missing 'file_path'"), ""), "IsNudge: invalid-tool-call template");
		ctx.Assert(                      Nudges.IsNudge("keep going", "keep going"), "IsNudge: role end-of-turn prompt");
		ctx.Assert(!Nudges.IsNudge("please continue with the design", "keep going"), "IsNudge: real user text is not a nudge");
	}
}