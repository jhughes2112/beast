using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;


// Unit tests for the mechanical compaction pass: the digests stale turns become (one line per call,
// repeats collapsed, a subagent's summary kept, every call in the log at the lines it names), reasoning,
// records from earlier versions folded in, nudge and stale-ledger stripping, the too-small-to-matter
// bailout, the file ledger builder, and the nudge recognizers. The rehydration test then runs the rebuilt
// history through all three protocols' wire converters — the pass is only safe if every protocol still
// produces a well-formed request from it — and the persistence test round-trips it through the
// source-generated serializer the sessions are saved with.
public static class MechanicalCompactionTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  MechanicalCompactionTests");

		TestTryBuildElides(ctx);
		TestTryBuildBailsOut(ctx);
		TestProjectionAccounting(ctx);
		TestDigest(ctx);
		TestDigestFoldsOldRecords(ctx);
		TestReportSummary(ctx);
		TestReasoning(ctx);
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

	// The turn in flight, after which everything before is stale.
	private static void AddFreshTurns(List<CanonicalMessage> messages)
	{
		messages.Add(new AssistantMessage("done", string.Empty, null));
	}

	// The lines a digest line's "(lines X-Y)" names, read back from the saved file the way read_file counts them.
	private static string ReadReference(string path, string line)
	{
		string   marker = "lines ";
		int      at     = line.LastIndexOf(marker, StringComparison.Ordinal);
		string[] range  = line.Substring(at + marker.Length).TrimEnd(')').Split('-');
		int      first  = int.Parse(range[0]);
		int      last   = int.Parse(range[1]);
		string[] lines  = File.ReadAllText(path).Split('\n');
		return string.Join("\n", lines, first - 1, last - first + 1);
	}

	// The digest line that starts with the given text, or "" when there is none.
	private static string DigestLine(string digest, string start)
	{
		string found = string.Empty;
		foreach (string line in digest.Split('\n'))
		{
			if (found.Length == 0 && line.StartsWith(start, StringComparison.Ordinal))
			{
				found = line;
			}
		}
		return found;
	}

	private static int ToolTraffic(IReadOnlyList<CanonicalMessage> messages)
	{
		int count = 0;
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is ToolResultMessage || (msg is AssistantMessage am && am.ToolCalls.Count > 0))
			{
				count++;
			}
		}
		return count;
	}

	// Every stale call becomes one digest line. The lines are pinned so any drift in what the model actually
	// reads shows up in review.
	private static void TestDigest(TestContext ctx)
	{
		string                 longCommand = "python3 - <<'EOF'\nprint('hello')\nEOF";
		string                 longOutput  = new string('o', 200) + "\nlast line";
		string                 report      = $"\nstderr: {MechanicalCompaction.SummaryPrefix}REJECTED, the chorus is too long\n\nThe full review: " + new string('r', 3000);
		List<CanonicalMessage> messages    = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("running", string.Empty, new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "s1", Name = "bash", ArgumentsJson = new JsonObject { ["why"] = "Check the greeting prints", ["command"] = longCommand, ["timeout_seconds"] = 30 }.ToJsonString() },
				new SemanticToolCall { Id = "s2", Name = "readonly_bash", ArgumentsJson = new JsonObject { ["why"] = "List files", ["command"] = "ls" }.ToJsonString() },
				new SemanticToolCall { Id = "s3", Name = "bash", ArgumentsJson = new JsonObject { ["command"] = "make test && echo finished\nsecond line" }.ToJsonString() },
				new SemanticToolCall { Id = "s4", Name = "bash", ArgumentsJson = new JsonObject { ["why"] = new string('w', 300), ["command"] = "pwd" }.ToJsonString() },
				new SemanticToolCall { Id = "r1", Name = "read_file", ArgumentsJson = "{\"file_path\":\"src/a.cs\",\"offset\":10,\"lines\":50}" },
				new SemanticToolCall { Id = "e1", Name = "edit_file", ArgumentsJson = new JsonObject { ["why"] = "Fix the retry", ["file_path"] = "src/a.cs", ["old_text"] = new string('o', 400), ["new_text"] = "one\ntwo\nthree" }.ToJsonString() },
				new SemanticToolCall { Id = "e2", Name = "edit_file", ArgumentsJson = new JsonObject { ["why"] = "Try it", ["file_path"] = "src/d.cs", ["old_text"] = "x", ["new_text"] = "z" }.ToJsonString() }
			}),
			new ToolResultMessage("s1", longOutput, null, 1),
			new ToolResultMessage("s2",    "a.txt", null, 0),
			new ToolResultMessage("s3", "ok"),
			new ToolResultMessage("s4",          "/workspace", null, 0),
			new ToolResultMessage("r1", new string('f', 2000), null, 0),
			new ToolResultMessage("e1", "Edit applied at line 40:\n" + new string('d', 300), null, 0),
			new ToolResultMessage("e2",                         "Error: old_text not found", null, 1),
			new AssistantMessage(string.Empty, string.Empty, new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "v1", Name = "review_work", ArgumentsJson = new JsonObject { ["prompt"] = new string('p', 4000) }.ToJsonString() }
			}),
			new ToolResultMessage("v1", report, null, 1),
			new AssistantMessage("checking twice", string.Empty, new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "t1", Name = "bash", ArgumentsJson = new JsonObject { ["why"] = "Poll the tests", ["command"] = "cat done" }.ToJsonString() },
				new SemanticToolCall { Id = "t2", Name = "bash", ArgumentsJson = new JsonObject { ["why"] = "Poll the tests", ["command"] = "cat done" }.ToJsonString() }
			}),
			new ToolResultMessage("t1", "pending", null, 0),
			new ToolResultMessage("t2", "pending", null, 0),
			new UserMessage("now the other one"),
			new AssistantMessage("on it", string.Empty, null)
		};
		AddFreshTurns(messages);

		CompactionLog          log     = NewLog();
		List<CanonicalMessage> rebuilt = MechanicalCompaction.Elide(messages, string.Empty, log, NoSummaries);
		log.Save();
		try
		{
			// Shape: each user message as itself, one digest per stretch between them, then the turn in flight.
			ctx.AssertEqual(   5,                  rebuilt.Count, "Digest: user, digest, user, digest, the turn in flight");
			ctx.AssertEqual("go", ((UserMessage)rebuilt[0]).Text, "Digest: the user's words stay a message of their own");
			ctx.AssertEqual("now the other one",      ((UserMessage)rebuilt[2]).Text, "Digest: every one of them");
			ctx.AssertEqual("done",              ((AssistantMessage)rebuilt[4]).Text, "Digest: the last turn stays as it was");
			ctx.AssertEqual(0, ToolTraffic(rebuilt), "Digest: no tool calls or results are left for a model to copy");

			string digest = ((UserMessage)rebuilt[1]).Text;
			ctx.Assert(digest.StartsWith($"{MechanicalCompaction.DigestMarker} Earlier turns, shortened. Full calls and output: {log.FilePath}\n", StringComparison.Ordinal), $"Digest: Beast's voice, naming its log ({digest})");
			ctx.Assert(MechanicalCompaction.HasDigest(rebuilt), "Digest: the history can be asked whether it holds one");
			ctx.AssertContains(digest, "\nYou: running\n", "Digest: the assistant's own words, as it wrote them");
			ctx.Assert(!digest.Contains("last line", StringComparison.Ordinal) && !digest.Contains(new string('f', 50), StringComparison.Ordinal), "Digest: long output stays in the log");

			ctx.Assert(DigestLine(digest, $"- bash: Check the greeting prints → failed (exit 1): {new string('o', 120)}… (lines ").Length > 0, "Digest: a failure shows with its first line, cut short");
			ctx.Assert(             DigestLine(digest, "- readonly_bash: List files → a.txt (lines ").Length > 0, "Digest: a short result is stated");
			ctx.Assert(         DigestLine(digest, "- bash: make test && echo finished → ok (lines ").Length > 0, "Digest: with no note, the command's first line stands in");
			ctx.Assert(    DigestLine(digest, $"- bash: {new string('w', 128)} → /workspace (lines ").Length > 0, "Digest: a long note is cut to size");
			ctx.Assert(    DigestLine(digest, "- read_file: src/a.cs from line 10, 50 lines (lines ").Length > 0, "Digest: a read by its path and range, never its contents");
			ctx.Assert(DigestLine(digest, "- edit_file: src/a.cs lines 40-42, Fix the retry (lines ").Length > 0, "Digest: an edit by its path, rough lines and note");
			ctx.Assert(DigestLine(digest, "- edit_file: src/d.cs, Try it → failed (exit 1): Error: old_text not found (lines ").Length > 0, "Digest: a failed edit claims no lines");
			ctx.Assert(         DigestLine(digest, "- review_work: REJECTED, the chorus is too long → failed (exit 1) (lines ").Length > 0, "Digest: a subagent's report by its own summary");
			ctx.Assert(DigestLine(digest, "- bash: Poll the tests → pending (×2, lines ").Length > 0, "Digest: repeats collapse into one line with a count");

			// Every line's range holds that call in full: its arguments as plain text, its exit code and output.
			string chunk = ReadReference(log.FilePath, DigestLine(digest, "- bash: Check the greeting prints"));
			ctx.Assert(chunk.StartsWith("bash\n", StringComparison.Ordinal) && chunk.EndsWith("last line", StringComparison.Ordinal), "Digest: a line's range is exactly its chunk, under the tool's name with no call id");
			ctx.AssertContains(chunk, longCommand, "Digest: the chunk has the whole command");
			ctx.AssertContains(chunk,    "exit 1", "Digest: and the exit code");
			ctx.AssertContains(ReadReference(log.FilePath, DigestLine(digest, "- review_work")), new string('r', 3000), "Digest: and the whole report, past its summary");
			string polls = ReadReference(log.FilePath, DigestLine(digest, "- bash: Poll the tests"));
			ctx.Assert(polls.StartsWith("bash\n", StringComparison.Ordinal) && polls.Contains("----------", StringComparison.Ordinal), "Digest: a collapsed line spans every repeat");

			// A later compaction keeps a digest as it is: it is already as short as it gets.
			List<CanonicalMessage> again = MechanicalCompaction.Elide(rebuilt, string.Empty, NewLog(), NoSummaries);
			ctx.AssertEqual(digest, ((UserMessage)again[1]).Text, "Digest: survives a second pass unchanged");

			// The note that heads the ledger says what digests are, and counts as the ledger.
			string note = MechanicalCompaction.WithDigestNote(string.Empty);
			ctx.Assert(note.StartsWith(MechanicalCompaction.LedgerMarker, StringComparison.Ordinal) && note.Contains(MechanicalCompaction.DigestMarker, StringComparison.Ordinal), "Digest: the note counts as the ledger, so the next compaction replaces it");

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

	// A Reviewer's report opens with the one line its caller's digest keeps. A summary left out costs no turn:
	// the report's first line stands in, and an over-long one is cut.
	private static void TestReportSummary(TestContext ctx)
	{
		Role                     reviewer = new RoleService(Environment.CurrentDirectory).Roles["Reviewer"];
		List<string>             reports  = new List<string>();
		Tool[]                   tools    = ToolFactory.BuildForRole(new SettingsService(), reviewer, null, null, null, false, null, null, null, null, (ok, text) => reports.Add(text), null);
		Dictionary<string, Tool> byName   = new Dictionary<string, Tool>();
		foreach (Tool tool in tools)
		{
			byName[tool.Definition.Function.Name] = tool;
		}

		string     tooLong = new string('s', MechanicalCompaction.kMaxSummaryChars + 44);
		ToolResult given   = ToolDispatch.ExecuteToolAsync(new SemanticToolCall { Id = "f1", Name = "finish_review", ArgumentsJson = new JsonObject { ["approved"] = false, ["comments"] = "Line 3 is wrong.\nMore detail.", ["summary"] = tooLong }.ToJsonString() }, byName, "s", 100, new TransportSilent(), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
		ToolResult missing = ToolDispatch.ExecuteToolAsync(new SemanticToolCall { Id = "f2", Name = "finish_review", ArgumentsJson = "{\"approved\":true,\"comments\":\"Approved: all good.\\nNotes follow.\"}" }, byName, "s", 100, new TransportSilent(), System.Threading.CancellationToken.None).GetAwaiter().GetResult();

		ctx.Assert(given.ExitCode == 0 && missing.ExitCode == 0, $"ReportSummary: both reviews finish ({given.StdErr}{missing.StdErr})");
		ctx.AssertEqual(2, reports.Count, "ReportSummary: both reach the caller");
		ctx.AssertEqual($"{MechanicalCompaction.SummaryPrefix}{new string('s', MechanicalCompaction.kMaxSummaryChars)}\n\nLine 3 is wrong.\nMore detail.", reports[0], "ReportSummary: the summary first, cut to size, then the whole report");
		ctx.AssertEqual($"{MechanicalCompaction.SummaryPrefix}Approved: all good.\n\nApproved: all good.\nNotes follow.", reports[1], "ReportSummary: with none given, the report's first line stands in");
	}

	// A history written before digests holds tool_call_memory records. Until it compacts again its model may
	// still copy one, so the record tool stays declared and explains itself; its next compaction folds them.
	private static void TestDigestFoldsOldRecords(TestContext ctx)
	{
		string                 record   = "{\"tool\":\"bash\",\"why\":\"Old check\",\"command\":\"elided\"}";
		List<CanonicalMessage> messages = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("earlier", "[reasoning: /old.log lines 1 to 9]", new List<SemanticToolCall>
			{
				new SemanticToolCall { Id = "m1", Name = MechanicalCompaction.MemoryToolName, ArgumentsJson = record },
				new SemanticToolCall { Id = "m2", Name = MechanicalCompaction.MemoryToolName, ArgumentsJson = "{\"tool\":\"edit_file\",\"file_path\":\"record.cs\"}" }
			}),
			new ToolResultMessage("m1", "exit 0\ntool call is in file /old.log lines 10 to 20", null, 0),
			new ToolResultMessage("m2", "exit 0", null, 0)
		};
		AddFreshTurns(messages);
		ctx.Assert(MechanicalCompaction.HasMemoryCalls(messages), "OldRecords: the history can be asked whether it holds records");

		CompactionLog          log     = NewLog();
		List<CanonicalMessage> rebuilt = MechanicalCompaction.Elide(messages, string.Empty, log, NoSummaries);
		log.Save();
		try
		{
			string digest = ((UserMessage)rebuilt[1]).Text;
			ctx.Assert(!MechanicalCompaction.HasMemoryCalls(rebuilt), "OldRecords: none survive a compaction");
			ctx.Assert(DigestLine(digest, "- bash: Old check (lines ").Length > 0, $"OldRecords: a record becomes a line for the tool it stood for ({digest})");
			ctx.Assert(DigestLine(digest, "- edit_file: record.cs → exit 0 (lines ").Length > 0, "OldRecords: a file record keeps its path");
			ctx.AssertContains(digest, "\nYou thought: [reasoning: /old.log lines 1 to 9]", "OldRecords: an old reasoning reference is kept as it was");
			ctx.AssertContains(ReadReference(log.FilePath, DigestLine(digest, "- bash: Old check")), "/old.log lines 10 to 20", "OldRecords: the old pointer is in the new log");

			// Models copying a record get told, and nothing runs.
			Dictionary<string, Tool> noTools = new Dictionary<string, Tool>();
			ToolResult               copied  = ToolDispatch.ExecuteToolAsync(new SemanticToolCall { Id = "c1", Name = MechanicalCompaction.MemoryToolName, ArgumentsJson = "{}" }, noTools, "s", 100, new TransportSilent(), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
			ctx.Assert(copied.ExitCode != 0 && copied.StdErr.Contains("not a tool", StringComparison.Ordinal), "OldRecords: calling a record explains itself");

			// Declared, for a server that only lets declared tools be called, it says the same and still runs
			// nothing; a copied record's arguments are no reason to refuse the turn.
			Tool                     declared = MechanicalCompaction.MemoryTool();
			Dictionary<string, Tool> withTool = new Dictionary<string, Tool> { [MechanicalCompaction.MemoryToolName] = declared };
			ToolResult               answered = ToolDispatch.ExecuteToolAsync(new SemanticToolCall { Id = "c2", Name = MechanicalCompaction.MemoryToolName, ArgumentsJson = record }, withTool, "s", 100, new TransportSilent(), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
			ctx.Assert(answered.ExitCode != 0 && answered.StdErr.Contains("not a tool", StringComparison.Ordinal), "OldRecords: a declared record explains itself too");
			(JsonObject? copiedArgs, string? copiedError) = FixJson.TryParseWithSchema(record, declared.Definition.Function, null);
			ctx.Assert(copiedArgs != null && copiedError == null, "OldRecords: a copied record passes the declared schema");
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

	// Stale reasoning is dropped, unless a summary of it is ready: then the summary is a line, and the log
	// has the original. The turn in flight keeps its reasoning, which some protocols require.
	private static void TestReasoning(TestContext ctx)
	{
		string                 longTrace   = "first thought\n" + new string('t', 2000);
		string                 summedTrace = "summed thought\n" + new string('u', 2000);
		string                 freshTrace  = "thinking now";
		List<CanonicalMessage> messages    = new List<CanonicalMessage>
		{
			new UserMessage("go"),
			new AssistantMessage("one",     longTrace, null),
			new AssistantMessage("three", summedTrace, null),
			new AssistantMessage("done",   freshTrace, null)
		};
		Dictionary<string, string> summaries = new Dictionary<string, string> { { summedTrace, "Concluded the shader was wrong." } };

		CompactionLog          log     = NewLog();
		List<CanonicalMessage> rebuilt = MechanicalCompaction.Elide(messages, string.Empty, log, summaries);
		log.Save();
		try
		{
			string digest = ((UserMessage)rebuilt[1]).Text;
			ctx.Assert(!digest.Contains("first thought", StringComparison.Ordinal) && !digest.Contains("summed thought", StringComparison.Ordinal), "Reasoning: no stale trace stays in the conversation");

			string summed = DigestLine(digest, "You thought: Concluded the shader was wrong. (lines ");
			ctx.Assert(summed.Length > 0, $"Reasoning: a ready summary is a line ({digest})");
			ctx.AssertEqual("reasoning\n" + summedTrace, ReadReference(log.FilePath, summed), "Reasoning: and the log has the original");
			ctx.Assert(!File.ReadAllText(log.FilePath).Contains("first thought", StringComparison.Ordinal), "Reasoning: a trace nothing points to is not logged either");
			ctx.AssertEqual(freshTrace, ((AssistantMessage)rebuilt[2]).Thinking, "Reasoning: the turn in flight keeps its own");
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
			{
				satisfied.Add(tr.ToolCallId);
			}
		}

		int open = 0;
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is AssistantMessage am)
			{
				foreach (SemanticToolCall call in am.ToolCalls)
				{
					if (!satisfied.Contains(call.Id))
					{
						open++;
					}
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

		// System message dropped (the successor re-injects the role prompt), stale ledger and the nudge
		// stripped, the real user message kept verbatim, and everything else in one digest.
		int    userCount = 0;
		string digest    = string.Empty;
		foreach (CanonicalMessage msg in rebuilt!)
		{
			ctx.Assert(!(msg is SystemMessage), "TryBuild: no system message in the rebuilt history");
			if (msg is UserMessage um && um.Text.StartsWith(MechanicalCompaction.DigestMarker, StringComparison.Ordinal))
			{
				digest = um.Text;
			}
			else if (msg is UserMessage real)
			{
				userCount++;
				ctx.AssertEqual("do the thing", real.Text, "TryBuild: real user text kept verbatim");
			}
		}
		ctx.AssertEqual(1,            userCount, "TryBuild: ledger and nudge user messages stripped");
		ctx.AssertEqual(0, ToolTraffic(rebuilt), "TryBuild: no stale tool traffic is left");

		// A read keeps its path and range and loses the contents; a write its path and short result. Stale
		// thinking with no summary ready is gone.
		ctx.Assert(DigestLine(digest, "- read_file: a.cs from line 10, 500 lines (lines ").Length > 0, $"TryBuild: a stale read is its path and range ({digest})");
		ctx.Assert(                  DigestLine(digest, "- write_file: b.cs → ok (lines ").Length > 0, "TryBuild: a stale write is its path and result");
		ctx.Assert(                   !digest.Contains("private reasoning", StringComparison.Ordinal), "TryBuild: stale thinking is dropped");

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
			new AssistantMessage("second",           string.Empty, null),
			new AssistantMessage("third",            string.Empty, null),
			new AssistantMessage("done",    new string('t', 9000), null)
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
			new AssistantMessage("second", string.Empty, null),
			new AssistantMessage("third",  string.Empty, null),
			new AssistantMessage("done",          fresh, null)
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

		// A compacted history's earlier edits are digest lines now, so its own ledger carries them: listed first,
		// each once, with the note that follows the list left behind.
		List<CanonicalMessage> successor = new List<CanonicalMessage>
		{
			new UserMessage(MechanicalCompaction.WithDigestNote($"{MechanicalCompaction.LedgerMarker}\nFiles this conversation has created or modified:\nold.cs\nb.cs")),
			new AssistantMessage("", "", new List<SemanticToolCall> { new SemanticToolCall { Id = "n", Name = "edit_file", ArgumentsJson = "{\"file_path\":\"new.cs\",\"old_text\":\"x\",\"new_text\":\"y\"}" } }),
			new AssistantMessage("", "",                      new List<SemanticToolCall> { new SemanticToolCall { Id = "b", Name = "write_file", ArgumentsJson = "{\"file_path\":\"b.cs\",\"content\":\"z\"}" } })
		};
		ctx.AssertEqual(MechanicalCompaction.LedgerMarker + "\nFiles this conversation has created or modified:\nold.cs\nb.cs\nnew.cs",
			MechanicalCompaction.BuildLedger(successor), "BuildLedger: an earlier ledger's files carry through compaction after compaction");
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
	// Ends on a turn in flight with a call and its result, as compaction mid-tool-round leaves it.
	private static List<CanonicalMessage> BuildSuccessorSeed()
	{
		List<CanonicalMessage> messages = BuildConversation(new string('x', 5000));
		messages.Add(new AssistantMessage("checking the last file", string.Empty, new List<SemanticToolCall>
		{
			new SemanticToolCall { Id = "tail1", Name = "read_file", ArgumentsJson = "{\"file_path\":\"z.cs\"}" }
		}));
		messages.Add(new ToolResultMessage("tail1", "contents of z"));

		List<CanonicalMessage> rebuilt = MechanicalCompaction.TryBuild(messages, "keep going", kFullWindow, kFullWindow, false, 0, NewLog(), NoSummaries)!;
		rebuilt.Insert(0, new UserMessage(MechanicalCompaction.WithDigestNote(MechanicalCompaction.BuildLedger(messages))));
		rebuilt.Insert(0, new SystemMessage("role prompt"));
		return rebuilt;
	}

	private static void TestRehydratesOnEveryProtocol(TestContext ctx)
	{
		List<CanonicalMessage> rebuilt = BuildSuccessorSeed();

		LlmModel      model = new LlmModel("test", "http://localhost", string.Empty, new List<JsonObject>(), new List<JsonObject>(), new ModelConfig());
		ProtocolProxy proxy = new ProtocolProxy(model);

		// Chat Completions: every tool-role message answers a known call, and the digest is user text.
		ProtocolChatCompletions cc       = proxy.EnsureProtocolChatCompletions(rebuilt);
		JsonArray               ccNative = (JsonArray)Reflect.GetField(cc, "_native")!;
		HashSet<string>         ccIds    = new HashSet<string>(StringComparer.Ordinal);
		int                     ccTools  = 0;
		bool                    ccDigest = false;
		foreach (JsonNode? node in ccNative)
		{
			JsonArray? calls = node?["tool_calls"]?.AsArray();
			if (calls != null)
			{
				foreach (JsonNode? call in calls)
				{
					ccIds.Add(call!["id"]!.GetValue<string>());
				}
			}
			if (node?["role"]?.GetValue<string>() == "tool")
			{
				ccTools++;
				ctx.Assert(ccIds.Contains(node["tool_call_id"]!.GetValue<string>()), "Rehydrate CC: tool result answers a preceding call");
			}
			ccDigest = ccDigest || (node?["role"]?.GetValue<string>() == "user" && node["content"]!.ToJsonString().Contains(MechanicalCompaction.DigestMarker.Substring(1), StringComparison.Ordinal));
		}
		ctx.AssertEqual(1, ccTools, "Rehydrate CC: only the turn in flight carries tool traffic");
		ctx.Assert(ccDigest, "Rehydrate CC: the digest is user text");

		// Anthropic: strict role alternation must hold with the user's message, the ledger and the digest
		// all user text in a row, and tool_use/tool_result ids must pair exactly.
		ProtocolAnthropic anthropic = proxy.EnsureProtocolAnthropic(rebuilt);
		JsonArray         anNative  = (JsonArray)Reflect.GetField(anthropic, "_native")!;
		string            prevRole  = string.Empty;
		HashSet<string>   useIds    = new HashSet<string>(StringComparer.Ordinal);
		HashSet<string>   resultIds = new HashSet<string>(StringComparer.Ordinal);
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
				}
				else if (type == "tool_result")
				{
					resultIds.Add(block["tool_use_id"]!.GetValue<string>());
				}
			}
		}
		ctx.Assert(useIds.Count == 1 && useIds.SetEquals(resultIds), "Rehydrate Anthropic: tool_use and tool_result ids pair exactly");

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
		ctx.AssertEqual(1, outputs, "Rehydrate Responses: the turn in flight's result is present");
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

		bool ledgerSurvived = false;
		foreach (CanonicalMessage msg in loaded.Messages)
		{
			if (msg is UserMessage um && um.Text.StartsWith(MechanicalCompaction.LedgerMarker, StringComparison.Ordinal))
			{
				ledgerSurvived = true;
			}
		}
		ctx.Assert(MechanicalCompaction.HasDigest(loaded.Messages), "PersistRoundTrip: the digest survives save/load");
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