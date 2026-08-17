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
	private const string kStuckRoleName    = "StuckTester";
	private const string kChokeRoleName    = "ChokeTester";
	private const string kReserveRoleName  = "ReserveTester";
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
		// The same server under a second model id that rejects everything as over-window, with a
		// role of its own so nothing can fall back off it. Its window is configured large, which is
		// the point: what it CLAIMS and what it accepts disagree, exactly like a local server whose
		// real -c is smaller than its advertised one.
		ModelConfig alwaysFullConfig = new ModelConfig
		{
			Id              = FakeLlm.AlwaysFullModelId,
			Name            = "Fake Always Full",
			ContextWindow   = FakeLlm.ContextWindow,
			MaxOutputTokens = 8192,
		};
		LlmModel alwaysFull = new LlmModel(FakeLlm.AlwaysFullModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), alwaysFullConfig);
		Role     stuckRole  = new Role(kStuckRoleName, "always full", RoleKind.Agent, new List<string> { FakeLlm.AlwaysFullModelId },
			new List<string>(), "You are a chaos test driver.", kSummaryPrompt, string.Empty);

		// A third id on the same server: it aborts the connection above a prompt size instead of
		// reporting anything, so the only way through is to offer it less. Alone in its role, so a
		// model switch cannot rescue the run and the shrink is what has to do the work.
		ModelConfig chokeConfig = new ModelConfig
		{
			Id              = FakeLlm.ChokesOnBigPromptModelId,
			Name            = "Fake Chokes On Big Prompts",
			ContextWindow   = FakeLlm.ContextWindow,
			MaxOutputTokens = 8192,
		};
		LlmModel choke     = new LlmModel(FakeLlm.ChokesOnBigPromptModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), chokeConfig);
		Role     chokeRole = new Role(kChokeRoleName, "chokes on big prompts", RoleKind.Agent, new List<string> { FakeLlm.ChokesOnBigPromptModelId },
			new List<string>(), "You are a chaos test driver.", kSummaryPrompt, string.Empty);

		// A fourth id that counts prompt + max_tokens against the window, as llama-server does. It
		// declares NO output ceiling of its own, which is what makes the sizing visible: with nothing
		// configured, whatever the budget asks for is entirely the budget's own decision.
		ModelConfig reserveConfig = new ModelConfig
		{
			Id              = FakeLlm.ReservationModelId,
			Name            = "Fake Reservation Counter",
			ContextWindow   = FakeLlm.ContextWindow,
			MaxOutputTokens = 0,
		};
		LlmModel reserveModel = new LlmModel(FakeLlm.ReservationModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), reserveConfig);
		Role     reserveRole  = new Role(kReserveRoleName, "counts the reservation", RoleKind.Agent, new List<string> { FakeLlm.ReservationModelId },
			new List<string>(), "You are a chaos test driver.", kSummaryPrompt, string.Empty);

		// A fifth id that reports usage only in the trailing chunk, as plain OpenAI does — the one
		// server on which the caller's pre-usage reporting can be seen.
		ModelConfig lateConfig = new ModelConfig
		{
			Id              = FakeLlm.LateUsageModelId,
			Name            = "Fake Late Usage",
			ContextWindow   = FakeLlm.ContextWindow,
			MaxOutputTokens = 8192,
		};
		LlmModel lateModel = new LlmModel(FakeLlm.LateUsageModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), lateConfig);

		LlmRegistry                  registry    = new LlmRegistry();
		Dictionary<string, LlmModel> models      = new Dictionary<string, LlmModel>(StringComparer.OrdinalIgnoreCase);
		models[FakeLlm.ModelId]                  = model;
		models[FakeLlm.AlwaysFullModelId]        = alwaysFull;
		models[FakeLlm.ChokesOnBigPromptModelId] = choke;
		models[FakeLlm.ReservationModelId]       = reserveModel;
		registry.RestoreModels(models);
		RoleService roleService             = new RoleService(Environment.CurrentDirectory);
		roleService.Roles[kRoleName]        = chaosRole;
		roleService.Roles[kStuckRoleName]   = stuckRole;
		roleService.Roles[kChokeRoleName]   = chokeRole;
		roleService.Roles[kReserveRoleName] = reserveRole;

		await TestFullContextStillTakesWholeToolOutputAsync(ctx, model, cts.Token);
		await TestAccountErrorIsNotOverflowAsync(ctx, model, cts.Token);
		await TestHugeConversationAlwaysSummarizesAsync(ctx, model, registry, roleService, cts.Token);
		await TestChokingServerStillSummarizesAsync(ctx, registry, roleService, cts.Token);
		await TestWholeTranscriptReachesTheModelAsync(ctx, server, model, registry, roleService, cts.Token);
		await TestCompactionSpinStopsAsync(ctx, server, alwaysFull, registry, roleService, cts.Token);
		await TestFirstRequestFitsTheWindowAsync(ctx, reserveModel, cts.Token);
		await TestFreshSessionIsCountedFirstAsync(ctx, server, reserveModel, registry, roleService, cts.Token);
		await TestProvisionalStatsTreatPriorTurnAsCachedAsync(ctx, lateModel, cts.Token);

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
					// The same sequence CompactAsync runs: hold any in-flight tool round aside, then
					// mechanical first, full staged summarization (real Summarizer, real registry)
					// when nothing is reclaimable, then re-attach the tail.
					(List<CanonicalMessage> settled, List<CanonicalMessage> pending) = MechanicalCompaction.SplitPending(session.Data.Messages);
					List<CanonicalMessage>? seed = MechanicalCompaction.TryBuild(settled, string.Empty, session.ContextLength, FakeLlm.ContextWindow);
					if (seed != null)
					{
						mechCompactions++;
						ctx.Log($"    fill compacted MECHANICALLY at turn {turns} ({totalTokens / 1000}k tokens so far)");
					}
					else
					{
						string? summary = await Summarizer.SummarizeAsync(session, settled, kSummaryPrompt, registry, roleService, transport, cts.Token);
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
						if (pending.Count > 0)
						{
							MechanicalCompaction.CloseOpenCalls(pending);
							seed.AddRange(pending);
						}
						// Exactly CompactAsync's rule: the successor is only prodded when its seeded
						// history asks the model for nothing (the mechanical pass's usual shape).
						if (MechanicalCompaction.NeedsResumePrompt(seed))
							seed.Add(new UserMessage(Nudges.ResumeAfterCompaction()));

						session = BuildSession($"fake-e2e-{mechCompactions + fullCompactions + 1}", model, transport);
						session.Data.Messages.AddRange(seed);
						ctx.Assert(session.NeedsAttention(), $"FakeLlm: successor at turn {turns} has something to answer (no stall)");
						ctx.AssertEqual(0, OpenCallCount(session.Data.Messages), $"FakeLlm: successor at turn {turns} carries no unanswered tool call");
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

	// An unpaid account rejects with a bare 400 whose body is the only clue, and it lands on a
	// session already deep into its window — the exact state where a 400 with unrecognized wording
	// IS read as overflow. Treating this one that way sent the session into compaction, then into
	// compaction failure, then straight back around: two sessions spun ~10 requests a second for
	// forty minutes and wrote a 579MB log before the app died. It must come back as a plain failure
	// so the model is marked down and the human is told the one thing that fixes it.
	private static async Task TestAccountErrorIsNotOverflowAsync(TestContext ctx, LlmModel model, CancellationToken ct)
	{
		TestCaptureTransport transport = new TestCaptureTransport();

		// Non-ephemeral, and measured at 80% of the window, so the structural overflow inference is
		// live: this tests the guard, not the absence of the condition.
		BeastSession data = new BeastSession(Guid.NewGuid().ToString("N"), "billing", FakeLlm.ModelId, kRoleName,
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, (int)(FakeLlm.ContextWindow * 0.8), false);
		Session session = new Session(data, "You are a chaos test driver.", transport, false);
		session.UpdateModel(model);
		session.Bundle.OnUserMessage($"Continue the work. {FakeLlm.BillingSentinel}");

		LlmService     service = BuildService(model);
		ProtocolResult result  = await service.RunToCompletionAsync(session, Array.Empty<Tool>(), null, 0, 0, false, transport, ct);

		ctx.Assert(result.Outcome != ProtocolCallOutcome.ContextFull, "AccountError: an unpaid account is never reported as a full context");
		ctx.AssertEqual(ProtocolCallOutcome.Failed, result.Outcome, "AccountError: reported as a permanent failure");

		// The human has to hear about it — that alert is what ends the loop instead of prolonging it.
		bool alerted = false;
		foreach ((FrameType type, string text) in transport.Sent)
		{
			if (type == FrameType.Alert)
				alerted = true;
		}
		ctx.Assert(alerted, "AccountError: the human is alerted that the model needs a fix");
	}

	// The exact shape a compaction successor has on its very first request: real content in the
	// conversation, no measurement of it yet (CurrentContextSize is 0 until a response reports one),
	// and a provider that counts prompt + max_tokens against its window. The budget used to read the
	// unmeasured 0 as "the window is empty" and ask for nearly all of it as OUTPUT, so the sum
	// overran and came back worded as overflow — which compacts, which builds another unmeasured
	// successor, which does the same thing. The field log has eleven of them in nine minutes, each
	// dying on request one. The first request has to fit.
	private static async Task TestFirstRequestFitsTheWindowAsync(TestContext ctx, LlmModel model, CancellationToken ct)
	{
		TestCaptureTransport transport = new TestCaptureTransport();

		// Ephemeral and unmeasured, exactly as CompactAsync seeds a successor.
		BeastSession data = new BeastSession(Guid.NewGuid().ToString("N"), "successor", FakeLlm.ReservationModelId, kReserveRoleName,
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a chaos test driver.", transport, false);
		session.UpdateModel(model);
		ctx.AssertEqual(0, session.ContextLength, "FirstRequest: the successor starts with no measurement, as it must");

		// A summary the size the field one was — about a fifth of the window, nowhere near full.
		session.Bundle.OnUserMessage(new string('x', FakeLlm.ContextWindow) + " Continue the work from where it left off.");

		LlmService     service = BuildService(model);
		int            reserve = Math.Min((int)(FakeLlm.ContextWindow * 0.1), 7500);
		ProtocolResult result  = await service.RunToCompletionAsync(session, Array.Empty<Tool>(), null, reserve, 0, false, transport, ct);

		ctx.AssertEqual(ProtocolCallOutcome.Success, result.Outcome, "FirstRequest: a conversation that fits is not rejected on its first request");
		ctx.Assert(result.Outcome != ProtocolCallOutcome.ContextFull, "FirstRequest: and is never reported as a full context, which is what compacted in a loop");

		// Committing is the caller's job, and it is what ends the unmeasured state: from here the
		// floor has a real size to stand on and the model gets the rest of the window to answer in.
		if (result.Payload != null)
			session.CommitAssistantTurn(result.Payload);
		ctx.Assert(session.ContextLength > 0, "FirstRequest: the committed response measures the session, so the next request can size itself properly");
	}

	// The question every other compaction test dodges: did the conversation ACTUALLY reach the model?
	// Counting stages does not answer it, and neither does getting a summary back — a summarizer that
	// re-sent its first chunk forever produced exactly the right number of stages and a perfectly
	// plausible summary, and was wrong about everything after segment one. So this asserts against
	// what the SERVER received: a unique marker is planted in every part of the transcript, and every
	// one of them has to turn up in a real request body. Nothing short of that distinguishes a working
	// staged summarizer from one summarizing the same page over and over.
	private static async Task TestWholeTranscriptReachesTheModelAsync(TestContext ctx, FakeLlm server, LlmModel model, LlmRegistry registry, RoleService roleService, CancellationToken ct)
	{
		TestCaptureTransport transport = new TestCaptureTransport();
		Session              session   = BuildSession("coverage", model, transport);

		// Big enough to need many stages through the 50k window, with a marker in every turn.
		const int kSegments = 14;
		for (int i = 0; i < kSegments; i++)
		{
			session.Bundle.OnUserMessage($"SEGMENT-MARKER-{i} instructions.\n{JunkText(9000)}");
			session.Bundle.OnAssistantTurn($"Acknowledged SEGMENT-REPLY-{i}.\n{JunkText(9000)}", string.Empty, new List<SemanticToolCall>());
		}

		server.StartRecording();
		string?      summary  = await Summarizer.SummarizeAsync(session, session.Data.Messages, kSummaryPrompt, registry, roleService, transport, ct);
		List<string> received = server.StopRecording();

		ctx.AssertNotNull(summary, "Coverage: the staged run produced a summary");
		ctx.Assert(received.Count > 1, $"Coverage: it really was staged (saw {received.Count} requests)");

		// Every marker must appear in something the server was actually handed.
		int missing = 0;
		for (int i = 0; i < kSegments; i++)
		{
			bool seen = false;
			foreach (string body in received)
			{
				if (body.Contains($"SEGMENT-MARKER-{i}", StringComparison.Ordinal))
				{
					seen = true;
					break;
				}
			}
			if (!seen)
				missing++;
		}
		ctx.AssertEqual(0, missing, $"Coverage: every one of the {kSegments} transcript segments reached the model");

		// And the stages were not the same page repeatedly: the bodies must differ from one another.
		HashSet<string> distinct = new HashSet<string>(StringComparer.Ordinal);
		foreach (string body in received)
			distinct.Add(body);
		ctx.AssertEqual(received.Count, distinct.Count, "Coverage: no two stage requests were byte-identical");
	}

	// The failure that actually killed a compaction in the field: a local server handed a 71k-char
	// stage chunk did not report an overflow — it dropped the connection. That reaches the client as
	// a transient with no status and no explanation, which the summarizer treated as terminal, so it
	// retried the SAME size three times and then gave up, parking the session with nowhere to go. Now
	// every failure it cannot attribute to the account shrinks the source context and tries again, so
	// a server that only chokes above a size still gets summarized at the size it can take.
	private static async Task TestChokingServerStillSummarizesAsync(TestContext ctx, LlmRegistry registry, RoleService roleService, CancellationToken ct)
	{
		TestCaptureTransport transport = new TestCaptureTransport();
		LlmModel             choke     = registry.GetModel(FakeLlm.ChokesOnBigPromptModelId)!;
		BeastSession         data      = new BeastSession(Guid.NewGuid().ToString("N"), "choke", FakeLlm.ChokesOnBigPromptModelId, kChokeRoleName,
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a chaos test driver.", transport, false);
		session.UpdateModel(choke);

		// Comfortably more transcript than the choke threshold, so the first full-size stage is
		// guaranteed to hit it and the run has to shrink its way down to get anywhere.
		for (int i = 0; i < 6; i++)
		{
			session.Bundle.OnUserMessage($"Chunk {i} of the work order.\n{JunkText(12000)}");
			session.Bundle.OnAssistantTurn($"Acknowledged {i}.\n{JunkText(12000)}", string.Empty, new List<SemanticToolCall>());
		}

		string? summary = await Summarizer.SummarizeAsync(session, session.Data.Messages, kSummaryPrompt, registry, roleService, transport, ct);

		ctx.AssertNotNull(summary, "ChokingServer: a server that dies on big prompts still yields a summary");

		// And it got there by shrinking, not by luck: the status trail has to show it.
		int shrinks = 0;
		foreach ((FrameType type, string text) in transport.Sent)
		{
			if (type == FrameType.Status && text.Contains("retrying with less source context", StringComparison.Ordinal))
				shrinks++;
		}
		ctx.Assert(shrinks > 0, $"ChokingServer: the run shrank its source context in response to the failures (saw {shrinks})");
	}

	// The guarantee compaction rests on: a conversation is never too big to summarize. The
	// summarizer sizes every stage against the SUMMARIZING MODEL's window and folds the transcript
	// in one window-sized piece at a time, so conversation size only ever changes how many stages
	// run — never whether it works. Here that is 2MB of transcript folded through a 50k window.
	private static async Task TestHugeConversationAlwaysSummarizesAsync(TestContext ctx, LlmModel model, LlmRegistry registry, RoleService roleService, CancellationToken ct)
	{
		TestCaptureTransport transport = new TestCaptureTransport();
		Session              session   = BuildSession("huge", model, transport);

		// Forty turns of ~50KB each: every single message is itself larger than a stage could be
		// asked to swallow whole, so the mid-block splitting is exercised too.
		for (int i = 0; i < 20; i++)
		{
			session.Bundle.OnUserMessage($"Segment {i} of the work order.\n{JunkText(50000)}");
			session.Bundle.OnAssistantTurn($"Working on segment {i}.\n{JunkText(50000)}", string.Empty, new List<SemanticToolCall>());
		}

		int transcriptChars = 0;
		foreach (string block in Summarizer.RenderTranscript(session.Data.Messages))
			transcriptChars += block.Length;
		ctx.Assert(transcriptChars > 1_000_000, $"HugeSummary: the transcript really is oversized ({transcriptChars} chars vs a {FakeLlm.ContextWindow} token window)");

		string? summary = await Summarizer.SummarizeAsync(session, session.Data.Messages, kSummaryPrompt, registry, roleService, transport, ct);

		ctx.AssertNotNull(summary, "HugeSummary: a conversation many times the model's window still summarizes");
		ctx.Assert(summary != null && summary.Length > 0, "HugeSummary: the summary has content");

		// It must have taken many folds to get there — a single-shot summary would mean the
		// transcript never actually reached the model.
		int segments = 0;
		foreach ((FrameType type, string text) in transport.Sent)
		{
			if (type == FrameType.Status && text.Contains("Summarizing segment", StringComparison.Ordinal))
				segments++;
		}
		ctx.Assert(segments >= 5, $"HugeSummary: folded through many staged segments (saw {segments})");
	}

	// A provider that answers every request with an over-window rejection, however small the request
	// really is (a real window smaller than the configured one does exactly this). Compaction can
	// never satisfy it, and the mechanical pass costs nothing and takes no time — so without a limit
	// the handler compacts, retries, compacts, retries at network speed, spawning a successor
	// session per pass. The session must give up and wait for a human instead.
	private static async Task TestCompactionSpinStopsAsync(TestContext ctx, FakeLlm server, LlmModel model, LlmRegistry registry, RoleService roleService, CancellationToken ct)
	{
		TestCaptureTransport transport    = new TestCaptureTransport();
		SettingsService      settings     = new SettingsService(Environment.CurrentDirectory);
		StubOrchestrator     orchestrator = new StubOrchestrator();

		BeastSession data = new BeastSession(Guid.NewGuid().ToString("N"), "spin", FakeLlm.AlwaysFullModelId, kStuckRoleName,
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a chaos test driver.", transport, false);
		session.UpdateModel(model);
		session.AddUserMessage("Do the work.");

		using CancellationTokenSource runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		SessionHandler handler               = new SessionHandler(session);
		session.TryAttachHandler();
		Task run = handler.RunAsync(registry, roleService, settings, transport, orchestrator, null, false, runCts.Token);

		// Wait for the session to report that it cannot continue — that alert is the handler
		// deciding to stop rather than go around again.
		bool alerted = false;
		for (int i = 0; i < 200 && !alerted; i++)
		{
			foreach ((FrameType type, string text) in transport.Sent)
			{
				if (type == FrameType.Alert && text.Contains("compaction could not fix it", StringComparison.Ordinal))
					alerted = true;
			}
			if (!alerted)
				await Task.Delay(50, ct);
		}
		ctx.Assert(alerted, "CompactionSpin: the session reports it cannot continue");

		// Parked means parked: no further requests reach the provider while it waits for input.
		int served = server.RequestsServed;
		await Task.Delay(500, ct);
		ctx.AssertEqual(served, server.RequestsServed, "CompactionSpin: no further requests once the session has given up");

		runCts.Cancel();
		try
		{
			await run;
		}
		catch (OperationCanceledException)
		{
		}
	}

	// What the status bar shows BEFORE the server has spoken. Everything the previous turn read and
	// wrote is one contiguous prefix of the request now going out, so on submission it is all cached
	// and none of it is fresh input. The bar used to carry the previous turn's cached FIGURE forward
	// instead, which reported a whole conversation as fresh input on every turn — the expensive half
	// of the reading, and wrong every time after turn one.
	private static async Task TestProvisionalStatsTreatPriorTurnAsCachedAsync(TestContext ctx, LlmModel model, CancellationToken ct)
	{
		TestCaptureTransport transport = new TestCaptureTransport();
		Session              session   = BuildSession("stats-roll", model, transport);
		LlmService           service   = BuildService(model);

		session.AddUserMessage("First turn.");
		ProtocolResult first = await service.RunToCompletionAsync(session, Array.Empty<Tool>(), null, 0, 0, false, transport, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, first.Outcome, "StatsRoll: the first turn runs");
		if (first.Payload != null)
			session.CommitAssistantTurn(first.Payload);

		int settled = session.ContextLength;
		ctx.Assert(settled > 0, "StatsRoll: the first turn measured the session");

		// Everything from here is the second turn's reporting.
		int mark = transport.StatFrames.Count;
		session.AddUserMessage("Second turn.");
		ProtocolResult second = await service.RunToCompletionAsync(session, Array.Empty<Tool>(), null, 0, 0, false, transport, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, second.Outcome, "StatsRoll: the second turn runs");

		ctx.Assert(transport.StatFrames.Count > mark, "StatsRoll: the second turn reported live stats");
		if (transport.StatFrames.Count > mark)
		{
			(int Input, int Output, int MaxContext, int Context, int Cached) live = transport.StatFrames[mark];
			ctx.AssertEqual(settled, live.Cached, "StatsRoll: the previous turn's whole context reads as cached on the next submission");
			ctx.AssertEqual(0, live.Input, "StatsRoll: and none of it reads as fresh input");
			ctx.AssertEqual(live.Cached + live.Input + live.Output, live.Context, "StatsRoll: cached + input + output is the whole context");
		}
	}

	// A session is COUNTED before it is sized, never sized from the chars/3 gate. That gate exists
	// only to decide whether the real count is worth asking for; on a fresh session it reads near
	// zero however much conversation is seeded, so the handler has to count anyway. Driven through
	// the real handler against the provider that charges prompt + max_tokens, because the point is
	// the order of operations: the count has to land BEFORE the first submission is sized.
	private static async Task TestFreshSessionIsCountedFirstAsync(TestContext ctx, FakeLlm server, LlmModel model, LlmRegistry registry, RoleService roleService, CancellationToken ct)
	{
		TestCaptureTransport transport    = new TestCaptureTransport();
		SettingsService      settings     = new SettingsService(Environment.CurrentDirectory);
		StubOrchestrator     orchestrator = new StubOrchestrator();

		// Seeded history and no measurement: a compaction successor exactly as CompactAsync builds it.
		List<CanonicalMessage> seed = new List<CanonicalMessage>();
		seed.Add(new UserMessage(new string('x', FakeLlm.ContextWindow) + " Continue the work from where it left off."));
		BeastSession data = new BeastSession(Guid.NewGuid().ToString("N"), "counted", FakeLlm.ReservationModelId, kReserveRoleName,
			string.Empty, seed, null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a chaos test driver.", transport, false);
		session.UpdateModel(model);
		ctx.AssertEqual(0, session.ContextLength, "CountFirst: the session starts unmeasured, as a successor does");

		server.StartRecording();
		using CancellationTokenSource runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		SessionHandler handler               = new SessionHandler(session);
		session.TryAttachHandler();
		Task run = handler.RunAsync(registry, roleService, settings, transport, orchestrator, null, false, runCts.Token);

		for (int i = 0; i < 100 && session.ContextLength == 0; i++)
		{
			await Task.Delay(50, ct);
		}
		ctx.Assert(session.ContextLength > 0, "CountFirst: the conversation is measured without a turn having to fail first");

		// The order is the whole point, and only the server can testify to it: the FIRST request this
		// session ever makes has to be the count (max_tokens 1, no generation), not a generation sized
		// from a number nobody had. Asserting the session ends up measured proves nothing on its own —
		// a turn that succeeds measures it too, which is exactly the path that failed in the field.
		List<string> bodies = server.StopRecording();
		ctx.Assert(bodies.Count >= 2, "CountFirst: the session both counted and ran");
		if (bodies.Count >= 2)
		{
			JsonNode? first     = JsonNode.Parse(bodies[0]);
			JsonNode? second    = JsonNode.Parse(bodies[1]);
			int       firstMax  = first?["max_completion_tokens"]?.GetValue<int>() ?? first?["max_tokens"]?.GetValue<int>() ?? 0;
			int       secondMax = second?["max_completion_tokens"]?.GetValue<int>() ?? second?["max_tokens"]?.GetValue<int>() ?? 0;
			ctx.AssertEqual(1, firstMax, "CountFirst: the first request is the token count, not a generation");
			ctx.Assert(secondMax > 1, "CountFirst: the generation follows, sized from what the count reported");
		}

		// Nothing was rejected on the way: the loop this replaces announced itself by compacting.
		bool sizingFailure = false;
		foreach ((FrameType type, string text) in transport.Sent)
		{
			if ((type == FrameType.Alert || type == FrameType.Status) && text.Contains("compact", StringComparison.OrdinalIgnoreCase))
				sizingFailure = true;
		}
		ctx.Assert(!sizingFailure, "CountFirst: a conversation that fits its window never reaches compaction");

		runCts.Cancel();
		try
		{
			await run;
		}
		catch (OperationCanceledException)
		{
		}
	}

	// Minimal orchestrator for driving a SessionHandler in isolation: it records nothing and starts
	// no other handlers, so the test observes only the session under test.
	private sealed class StubOrchestrator : ISessionOrchestrator
	{
		public Task<(bool ok, string text, int responseTokens)> SpawnChildAsync(BeastSettings settings, Session parent, string roleName, string? displayName, string prompt, int maxWorkTurns, CancellationToken ct)
			=> Task.FromResult((false, "no subagents in this test", 0));

		public void     Deliver           (string sessionId, string content) { }
		public void     RegisterSession   (Session session)                  { }
		public void     UnregisterSession (string sessionId)                 { }
		public void     EnsureHandler     (Session session)                  { }
		public Session? FindParent        (Session session) => null;
		public void     TransferCompletion(string fromSessionId, string toSessionId)                   { }
		public void     CompleteSession   (string sessionId, bool ok, string text, int responseTokens) { }
	}

	private static LlmService BuildService(LlmModel model)
	{
		return new LlmService(model, DetectedProtocol.ChatCompletions, new ModelAvailability(), new List<string> { FakeLlm.ModelId }, (id, effort, summaries) => { });
	}

	// The case that motivated all of this: an 89%-full conversation asks a subagent for a review and
	// the reply has to fit in the handful of tokens left over. It cannot, and no answer that small is
	// worth having. So the round's size is no longer the caller's to dictate — the output arrives
	// whole, the budget is charged what it actually cost, and the conversation reads as exhausted,
	// which is what sends the next pass into compaction. No provider traffic is involved.
	private static async Task TestFullContextStillTakesWholeToolOutputAsync(TestContext ctx, LlmModel model, CancellationToken ct)
	{
		TestCaptureTransport transport = new TestCaptureTransport();
		Session              session   = BuildSession("full-context", model, transport);

		// 89% occupied, with a compaction reserve on top: under the old rule the round's share of
		// what remained was a few tokens, and the reply was truncated to it.
		int window   = FakeLlm.ContextWindow;
		int measured = (int)(window * 0.89);
		session.Budget.Configure(window, 8192, (int)(window * 0.1), 0, measured);

		// A review-sized answer: far larger than the room left, far smaller than the raw ceiling.
		string     review   = JunkText(24000);
		ToolResult reply    = new ToolResult("call-1", review, string.Empty, 0, ToolDispatch.EstimateTokens(review));
		Tool       reviewer = new Tool
		{
			Definition = new ToolDefinition
			{
				Type     = "function",
				Function = new FunctionDefinition
				{
					Name        = "review_work",
					Description = "Review the work and report findings.",
					Parameters  = Schema(("prompt", "string")),
				}
			},
			Handler = (args, toolCallId, cancel, tp, sessionId, maxOutputTokens) => Task.FromResult(reply)
		};

		List<SemanticToolCall> calls = new List<SemanticToolCall>
		{
			new SemanticToolCall { Id = "call-1", Name = "review_work", ArgumentsJson = "{\"prompt\":\"review it\"}" }
		};
		ProtocolCallPayload payload = new ProtocolCallPayload(string.Empty, string.Empty, calls,
			new List<ToolResult>(), "tool_calls", new TokenUsageInfo(), 0m);

		bool ran = await ToolDispatch.DispatchAsync(payload, new Tool[] { reviewer }, session, transport, ct);

		ctx.Assert(ran, "FullContext: the round dispatched");
		ctx.AssertEqual(1, payload.ToolResults.Count, "FullContext: one result came back");
		ctx.AssertEqual(review.Length, payload.ToolResults[0].StdOut.Length, "FullContext: the reply arrives whole, not cut to the caller's leftovers");

		// And the caller now knows what it is holding: the round is charged at its real size, which
		// is what makes the next pass compact instead of asking for the impossible again.
		ctx.AssertEqual(reply.MeasuredOutputTokens, session.Budget.PendingReserve, "FullContext: the budget is charged the reply's real size");
		ctx.Assert(session.Budget.IsExhausted(), "FullContext: an over-window conversation reads as exhausted, so the turn compacts");
	}

	// Assistant tool calls with no matching result. Every successor the soak builds is checked, so a
	// compaction that ever handed one on would fail here rather than 400 against a real provider.
	private static int OpenCallCount(IReadOnlyList<CanonicalMessage> messages)
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

	private static Session BuildSession(string name, LlmModel model, TestCaptureTransport transport)
	{
		BeastSession data = new BeastSession(Guid.NewGuid().ToString("N"), name, FakeLlm.ModelId, kRoleName,
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
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