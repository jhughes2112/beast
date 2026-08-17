using System;
using System.Collections.Generic;
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
		List<CanonicalMessage>? mechanical = MechanicalCompaction.TryBuild(settled, "keep going", kFullWindow, kFullWindow);
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
		List<CanonicalMessage> elided = MechanicalCompaction.Elide(settled, "keep going");
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

		List<CanonicalMessage>? rebuilt = MechanicalCompaction.TryBuild(messages, "keep going", kFullWindow, kFullWindow);
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
		ctx.AssertEqual("[elided 5000 chars: read_file(file_path: a.cs, offset: 10, lines: 500)]",
			big!.Content, "TryBuild: elision note names the call and its size, tersely");
		ctx.AssertEqual("ok", small!.Content, "TryBuild: small result untouched");

		// Stale thinking is cut, oversized write arguments become a stub that keeps file_path,
		// and the small read arguments stay verbatim.
		AssistantMessage first = (AssistantMessage)rebuilt[1];
		ctx.AssertEqual("", first.Thinking, "TryBuild: stale thinking cut");
		ctx.AssertContains(first.ToolCalls[0].ArgumentsJson, "\"offset\":10", "TryBuild: small arguments untouched");
		ctx.AssertContains(first.ToolCalls[1].ArgumentsJson, "compacted",     "TryBuild: oversized arguments elided");
		ctx.AssertContains(first.ToolCalls[1].ArgumentsJson, "b.cs",          "TryBuild: elided arguments keep file_path");

		// The protected tail is verbatim.
		AssistantMessage last = (AssistantMessage)rebuilt[rebuilt.Count - 1];
		ctx.AssertEqual("done", last.Text, "TryBuild: fresh tail kept verbatim");
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
		ctx.AssertNull(MechanicalCompaction.TryBuild(messages, "", kFullWindow, kFullWindow), "TryBuild: null when the reclaimable space is too small to matter");

		// A conversation too short to have a stale prefix is never rewritten.
		List<CanonicalMessage> shortConv = new List<CanonicalMessage>
		{
			new UserMessage("hi"),
			new AssistantMessage("hello", "", null)
		};
		ctx.AssertNull(MechanicalCompaction.TryBuild(shortConv, "", kFullWindow, kFullWindow), "TryBuild: null when every turn is fresh");
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
			+ "Files this conversation has touched (read = seen, wrote/edited = changed):\n"
			+ "a.cs: read 10-510\n"
			+ "b.cs: wrote, edited x2",
			ledger, "BuildLedger: complete ledger text with ranges and collapsed edits");
		ctx.Assert(!ledger.Contains("bash"), "BuildLedger: non-file tools ignored");

		ctx.AssertEqual("", MechanicalCompaction.BuildLedger(new List<CanonicalMessage> { new UserMessage("hi") }), "BuildLedger: empty when no files touched");
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
		List<CanonicalMessage> rebuilt  = MechanicalCompaction.TryBuild(messages, "keep going", kFullWindow, kFullWindow)!;
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
					if (call["function"]!["name"]!.GetValue<string>() == "write_file")
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
					if (block["name"]!.GetValue<string>() == "write_file")
						elided = block["input"] as JsonObject;
				}
				else if (type == "tool_result")
				{
					resultIds.Add(block["tool_use_id"]!.GetValue<string>());
				}
			}
		}
		ctx.Assert(                      useIds.SetEquals(resultIds), "Rehydrate Anthropic: tool_use and tool_result ids pair exactly");
		ctx.Assert(elided != null && elided.ContainsKey("compacted"), "Rehydrate Anthropic: elided arguments parsed into a real input object");

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
			if (msg is ToolResultMessage tr && tr.Content.StartsWith("[elided ", StringComparison.Ordinal))
				noteSurvived = true;
			if (msg is UserMessage um && um.Text.StartsWith(MechanicalCompaction.LedgerMarker, StringComparison.Ordinal))
				ledgerSurvived = true;
		}
		ctx.Assert(  noteSurvived, "PersistRoundTrip: elision note survives save/load");
		ctx.Assert(ledgerSurvived, "PersistRoundTrip: ledger message survives save/load");
	}

	private static void TestNudgeRecognition(TestContext ctx)
	{
		ctx.Assert(Nudges.IsNudge(Nudges.ContinueTask("return_to_caller"), ""), "IsNudge: continue-task template");
		ctx.Assert(     Nudges.IsNudge(Nudges.OutOfTurns("task_complete"), ""), "IsNudge: out-of-turns template");
		// No longer generated (replies are never rewritten to fit a caller's budget) but still
		// present in histories written by earlier versions, so it must stay strippable.
		ctx.Assert(Nudges.IsNudge("That output is about 900 tokens but must fit within 500 tokens.", ""), "IsNudge: legacy over-budget template");
		ctx.Assert(                    Nudges.IsNudge(Nudges.InvalidToolCall("missing 'file_path'"), ""), "IsNudge: invalid-tool-call template");
		ctx.Assert(                      Nudges.IsNudge("keep going", "keep going"), "IsNudge: role end-of-turn prompt");
		ctx.Assert(!Nudges.IsNudge("please continue with the design", "keep going"), "IsNudge: real user text is not a nudge");
	}
}