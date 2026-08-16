using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;


// Unit tests for the mechanical compaction pass: stale-result elision, oversized-argument stubs,
// thinking cuts, nudge and stale-ledger stripping, the too-small-to-matter bailout, the file
// ledger builder, and the nudge recognizers. The rehydration test then runs the rebuilt history
// through all three protocols' wire converters — the pass is only safe if every protocol still
// produces a well-formed request from it — and the persistence test round-trips it through the
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
		TestNudgeRecognition(ctx);
		TestRehydratesOnEveryProtocol(ctx);
		TestPersistRoundTrip(ctx);
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

	private static void TestTryBuildElides(TestContext ctx)
	{
		// The last three assistant turns are protected; everything before them is elidable.
		List<CanonicalMessage> messages = BuildConversation(new string('x', 5000));

		List<CanonicalMessage>? rebuilt = MechanicalCompaction.TryBuild(messages, "keep going");
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
		ctx.AssertNull(MechanicalCompaction.TryBuild(messages, ""), "TryBuild: null when the reclaimable space is too small to matter");

		// A conversation too short to have a stale prefix is never rewritten.
		List<CanonicalMessage> shortConv = new List<CanonicalMessage>
		{
			new UserMessage("hi"),
			new AssistantMessage("hello", "", null)
		};
		ctx.AssertNull(MechanicalCompaction.TryBuild(shortConv, ""), "TryBuild: null when every turn is fresh");
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
	// model has already been given — nothing there asks it for anything, and the successor would sit
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
		// shape is never touched — and it already needs the model's attention anyway.
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
		List<CanonicalMessage> rebuilt  = MechanicalCompaction.TryBuild(messages, "keep going")!;
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
		// into a real input object — ParseInput silently substitutes {} for garbage, so an empty
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
		BeastSession           data    = new BeastSession("rt-id", "rt-name", "rt-model", "rt-role", string.Empty, 0, rebuilt, null, 0m, 0, 0, 0, false);

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
		ctx.Assert(          Nudges.IsNudge(Nudges.ContinueTask("return_to_caller"), ""), "IsNudge: continue-task template");
		ctx.Assert(               Nudges.IsNudge(Nudges.OutOfTurns("task_complete"), ""), "IsNudge: out-of-turns template");
		ctx.Assert(Nudges.IsNudge(Nudges.ReplyOverBudget(900, 500, "task_complete"), ""), "IsNudge: over-budget template");
		ctx.Assert(    Nudges.IsNudge(Nudges.InvalidToolCall("missing 'file_path'"), ""), "IsNudge: invalid-tool-call template");
		ctx.Assert(                      Nudges.IsNudge("keep going", "keep going"), "IsNudge: role end-of-turn prompt");
		ctx.Assert(!Nudges.IsNudge("please continue with the design", "keep going"), "IsNudge: real user text is not a nudge");
	}
}