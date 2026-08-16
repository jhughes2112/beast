using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// End-to-end soak against the FakeLlm chaos server: a real LlmService streams real HTTP turns
// through the real ChatCompletions protocol, real tools dispatch and reserve budget, and the 50k
// window genuinely fills over and over — millions of tokens of traffic. Fill phases alternate to
// exercise BOTH compaction pathways: tool-heavy phases (large and small tool results) leave bulk
// the mechanical pass can elide, while text-heavy phases (no tools, big user messages the pass
// must keep verbatim) leave nothing reclaimable, forcing the full staged Summarizer to run — for
// real, against the fake model, through a real LlmRegistry and RoleService. The pass criterion is
// the user-facing one: the session survives every fill without a single unexpected failure.
public static class FakeLlmTests
{
	private const int    kMaxTurns         = 800;
	private const int    kRequiredEachPath = 2;
	private const long   kRequiredTokens   = 2_000_000;
	private const string kRoleName         = "ChaosTester";
	private const string kSummaryPrompt    = "Summarize this conversation: completed tasks, unfinished work, the user's explicit requests, and the critical details needed to continue.";

	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  FakeLlmTests");

		FakeLlm? server = FakeLlm.StartOnRandomPort();
		if (server == null)
		{
			ctx.Log("    SKIP: no free port for the fake LLM server");
			return;
		}

		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		Task serverTask                   = server.RunAsync(cts.Token);

		ModelConfig config = new ModelConfig
		{
			Id              = FakeLlm.ModelId,
			Name            = "Fake Chaos",
			ContextWindow   = FakeLlm.ContextWindow,
			MaxOutputTokens = 8192,
		};
		LlmModel model = new LlmModel(FakeLlm.ModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);

		// A real registry and role service around the fake model, so the Summarizer path runs the
		// exact production machinery (CreateService, stage sessions, chunking) against it.
		Role chaosRole = new Role(kRoleName, "chaos soak", RoleKind.Agent, new List<string> { FakeLlm.ModelId },
			new List<string>(), "You are a chaos test driver.", kSummaryPrompt, string.Empty);
		LlmRegistry                  registry = new LlmRegistry();
		Dictionary<string, LlmModel> models   = new Dictionary<string, LlmModel>(StringComparer.OrdinalIgnoreCase);
		models[FakeLlm.ModelId]               = model;
		registry.RestoreModels(models);
		RoleService roleService      = new RoleService(Environment.CurrentDirectory);
		roleService.Roles[kRoleName] = chaosRole;

		TestCaptureTransport transport = new TestCaptureTransport();
		Session              session   = BuildSession("fake-e2e", model, transport);
		session.AddUserMessage("Begin chaos.");

		Tool[] chaosTools = BuildChaosTools();
		Tool[] noTools    = Array.Empty<Tool>();
		int    reserve    = Math.Min((int)(FakeLlm.ContextWindow * 0.1), 7500);

		// A fresh service per session, exactly as CompactAsync's CreateService does for a
		// successor: the ProtocolProxy caches the conversation's native state, so a successor must
		// never inherit the predecessor's service.
		LlmService service = BuildService(model);

		bool toolPhase       = true;
		int  mechCompactions = 0;
		int  fullCompactions = 0;
		int  turnsAfterFinal = 0;
		int  turns           = 0;
		int  unmeasuredTurns = 0;
		long totalTokens     = 0;
		bool failed          = false;

		try
		{
			// Keep driving until both compaction pathways have each run twice and the token target
			// is met, then prove the last successor still takes a couple of ordinary turns.
			while (turns < kMaxTurns && !failed
				&& (mechCompactions < kRequiredEachPath || fullCompactions < kRequiredEachPath || totalTokens < kRequiredTokens || turnsAfterFinal < 2))
			{
				Tool[]         tools  = toolPhase ? chaosTools : noTools;
				ProtocolResult result = await service.RunToCompletionAsync(session, tools, null, reserve, 0, false, transport, cts.Token);

				if (result.Outcome == ProtocolCallOutcome.Success)
				{
					// The fake reports the prompt size early in the stream and the completion count at
					// the end, so a turn that commits with no prompt count means the protocol dropped
					// what the provider already told it — the failure that made the status bar read
					// c:0 i:0 and the context measure itself at output size alone.
					if (result.Payload!.Usage.PromptTokens <= 0)
						unmeasuredTurns++;

					session.CommitAssistantTurn(result.Payload!);
					totalTokens += result.Payload!.Usage.PromptTokens + result.Payload.Usage.CompletionTokens;

					bool hasCalls = await ToolDispatch.DispatchAsync(result.Payload!, tools, session, transport, cts.Token);
					if (hasCalls)
						session.CommitToolResults(result.Payload!);
					else if (toolPhase)
						session.Bundle.OnUserMessage("Keep going with more chaos.");
					else
						session.Bundle.OnUserMessage($"Consider this large brief and keep going:\n{JunkText(6000 + Random.Shared.Next(3000))}");

					if (mechCompactions >= kRequiredEachPath && fullCompactions >= kRequiredEachPath && totalTokens >= kRequiredTokens)
						turnsAfterFinal++;
				}
				else if (result.Outcome == ProtocolCallOutcome.ContextFull)
				{
					// The same decision CompactAsync makes: mechanical first, full staged
					// summarization (real Summarizer, real registry) when nothing is reclaimable.
					List<CanonicalMessage>? seed = MechanicalCompaction.TryBuild(session.Data.Messages, string.Empty);
					if (seed != null)
					{
						mechCompactions++;
						ctx.Log($"    fill compacted MECHANICALLY at turn {turns} ({totalTokens / 1000}k tokens so far)");
					}
					else
					{
						string? summary = await Summarizer.SummarizeAsync(session, kSummaryPrompt, registry, roleService, transport, cts.Token);
						ctx.AssertNotNull(summary, $"FakeLlm: Summarizer produced a summary at turn {turns}");
						if (summary != null)
						{
							seed = new List<CanonicalMessage> { new UserMessage(summary) };
							fullCompactions++;
							ctx.Log($"    fill compacted by SUMMARIZER at turn {turns} ({totalTokens / 1000}k tokens so far)");
						}
					}

					if (seed == null)
					{
						failed = true;
					}
					else
					{
						string ledger = MechanicalCompaction.BuildLedger(session.Data.Messages);
						if (ledger.Length > 0)
							seed.Insert(0, new UserMessage(ledger));
						// Exactly CompactAsync's rule: the successor is only prodded when its seeded
						// history asks the model for nothing (the mechanical pass's usual shape).
						if (MechanicalCompaction.NeedsResumePrompt(seed))
							seed.Add(new UserMessage(Nudges.ResumeAfterCompaction()));

						session = BuildSession($"fake-e2e-{mechCompactions + fullCompactions + 1}", model, transport);
						session.Data.Messages.AddRange(seed);
						ctx.Assert(session.NeedsAttention(), $"FakeLlm: successor at turn {turns} has something to answer (no stall)");
						service   = BuildService(model);
						toolPhase = !toolPhase;
					}
				}
				else
				{
					ctx.Log($"    unexpected outcome at turn {turns}: {result.Outcome} — {result.ErrorMessage}");
					failed = true;
				}
				turns++;
			}
		}
		finally
		{
			cts.Cancel();
			await serverTask;
		}

		ctx.Log($"    soak totals: {turns} turns, {totalTokens / 1000}k tokens, {mechCompactions} mechanical + {fullCompactions} summarizer compactions");
		ctx.Assert(!failed, "FakeLlm: no unexpected failures across the soak");
		ctx.AssertEqual(0, unmeasuredTurns, "FakeLlm: every committed turn kept the prompt count the stream reported");
		ctx.Assert(mechCompactions >= kRequiredEachPath, $"FakeLlm: mechanical compaction ran {kRequiredEachPath}+ times (got {mechCompactions} in {turns} turns)");
		ctx.Assert(fullCompactions >= kRequiredEachPath, $"FakeLlm: staged summarization ran {kRequiredEachPath}+ times (got {fullCompactions} in {turns} turns)");
		ctx.Assert(      totalTokens >= kRequiredTokens, $"FakeLlm: soak moved {kRequiredTokens / 1000}k+ tokens (got {totalTokens / 1000}k)");
		ctx.Assert(turnsAfterFinal >= 2, "FakeLlm: conversation keeps working after the final compaction");

		// The streamed frames prove the fake's SSE actually drove the display path: thinking and
		// tool calls both reached the transport at some point during the soak.
		bool sawThinking = false;
		bool sawToolCall = false;
		foreach ((FrameType type, string text) in transport.Sent)
		{
			if (type == FrameType.Thinking || (type == FrameType.StreamStart && text == StreamTag.Thinking))
				sawThinking = true;
			if (type == FrameType.ToolCall)
				sawToolCall = true;
		}
		ctx.Assert(sawThinking, "FakeLlm: reasoning streamed through to the transport");
		ctx.Assert(sawToolCall, "FakeLlm: tool calls streamed through to the transport");

		// Every stats frame must partition the context: cached + fresh input + output IS the
		// occupancy, so the status bar's counts always account for the fullness shown beside them.
		// The fake reports usage only in its final chunk (as ChatCompletions providers do), so the
		// thousands of live frames here are exactly the case where the counts used to read zero.
		int  statFrames = 0;
		int  breakdowns = 0;
		bool partitions = true;
		foreach ((int input, int output, int maxContext, int context, int cached) in transport.StatFrames)
		{
			statFrames++;
			if (cached + input + output != context)
				partitions = false;
			if (input > 0 && output > 0)
				breakdowns++;
		}
		ctx.Assert(statFrames > 0, "FakeLlm: stats frames reached the transport");
		ctx.Assert(    partitions, "FakeLlm: every stats frame's cached+input+output equals its context");
		ctx.Assert(breakdowns > 0, "FakeLlm: live frames carry a real input breakdown, not zeros");
	}

	private static LlmService BuildService(LlmModel model)
	{
		return new LlmService(model, DetectedProtocol.ChatCompletions, new ModelAvailability(), new List<string> { FakeLlm.ModelId }, (id, effort, summaries) => { });
	}

	private static Session BuildSession(string name, LlmModel model, TestCaptureTransport transport)
	{
		BeastSession data = new BeastSession(Guid.NewGuid().ToString("N"), name, FakeLlm.ModelId, kRoleName,
			string.Empty, 0, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a chaos test driver. Do random things.", transport, false);
		session.UpdateModel(model);
		return session;
	}

	// Two stub tools for the fake model to call: one returns a big block of junk (filling the
	// window quickly is the point of the soak), one a small answer, so elision sees both sides of
	// its size thresholds. Output is measured through MeasureRawResult exactly like production
	// tools, so budget reservations are exercised too.
	private static Tool[] BuildChaosTools()
	{
		Tool noise = new Tool
		{
			Definition = new ToolDefinition
			{
				Type     = "function",
				Function = new FunctionDefinition
				{
					Name        = "emit_noise",
					Description = "Produce a large block of chaos data about a topic.",
					Parameters  = Schema(("topic", "string"), ("size", "integer")),
				}
			},
			Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
			{
				ToolResult raw = new ToolResult(toolCallId, JunkText(10000 + Random.Shared.Next(10000)), string.Empty, 0, 0);
				return Task.FromResult(ToolDispatch.MeasureRawResult(raw, maxOutputTokens));
			}
		};

		Tool probe = new Tool
		{
			Definition = new ToolDefinition
			{
				Type     = "function",
				Function = new FunctionDefinition
				{
					Name        = "probe_state",
					Description = "Answer a small question about the current chaos state.",
					Parameters  = Schema(("query", "string")),
				}
			},
			Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
			{
				ToolResult raw = new ToolResult(toolCallId, JunkText(200 + Random.Shared.Next(400)), string.Empty, 0, 0);
				return Task.FromResult(ToolDispatch.MeasureRawResult(raw, maxOutputTokens));
			}
		};

		return new Tool[] { noise, probe };
	}

	private static JsonObject Schema(params (string Name, string Type)[] fields)
	{
		JsonObject properties = new JsonObject();
		JsonArray  required   = new JsonArray();
		foreach ((string name, string type) in fields)
		{
			properties[name] = new JsonObject { ["type"] = type, ["description"] = name };
			required.Add((JsonNode)name);
		}
		return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
	}

	private static string JunkText(int length)
	{
		StringBuilder sb = new StringBuilder(length);
		while (sb.Length < length)
			sb.Append("chaos-").Append(Random.Shared.Next(100000)).Append(' ');
		return sb.ToString(0, length);
	}
}