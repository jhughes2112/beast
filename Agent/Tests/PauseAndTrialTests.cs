using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// /pause and /resume through the real SessionHandler, and the trial request /config sends when a model's
// settings change, both against FakeProvider.
public static class PauseAndTrialTests
{
	private const string kModelId  = "fake-pausable";
	private const string kRoleName = "PauseTester";

	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  PauseAndTrialTests");

		FakeProvider? server = FakeProvider.StartOnRandomPort();
		if (server == null)
		{
			ctx.Log("    SKIP: no free port for the fake provider");
			return;
		}

		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		Task serverTask                   = server.RunAsync(cts.Token);
		try
		{
			TestTreeRules(ctx);
			TestFinishedSessionsCannotContinue(ctx);
			await TestPausedBeforeAnyTurnAsync(ctx, server, cts.Token);
			await TestPauseTakesEffectAtEndOfTurnAsync(ctx, server, cts.Token);
			await TestUserTextEndsPauseAsync(ctx, server, cts.Token);
			await TestResumeWithNothingToSendAsync(ctx, server, cts.Token);
			await TestResumeAfterCancelAsync(ctx, server, cts.Token);
			await TestTrialAsync(ctx, server, cts.Token);
			await TestReasoningSummaryAsync(ctx, server, cts.Token);
			TestSummarizerRoles(ctx);
			await TestSummarizerCallAsync(ctx, server, cts.Token);
		}
		finally
		{
			cts.Cancel();
			try
			{
				await serverTask;
			}
			catch (OperationCanceledException)
			{
			}
		}
	}

	// Pausing reaches children still working and skips finished ones; resuming releases only what was
	// paused, so a child parked for its own reasons stays parked.
	private static void TestTreeRules(TestContext ctx)
	{
		TestCaptureTransport transport = new TestCaptureTransport();
		Session              parent    = NewSession("tree",   transport);
		Session              busy      = NewSession("tree_1", transport);
		Session              done      = NewSession("tree_2", transport);
		Session              parked    = NewSession("tree_3", transport);
		parent.AddChild(busy);
		parent.AddChild(done);
		parent.AddChild(parked);
		busy.SendBusy();
		parked.Bundle.OnUserMessage("unanswered");
		parked.MarkInterrupted();

		parent.Deliver("tree", "/pause");
		ctx.Assert(  parent.Paused && busy.Paused, "Pause: reaches a child that is working");
		ctx.Assert(!done.Paused && !parked.Paused, "Pause: leaves idle children alone");

		Session late = NewSession("tree_4", transport);
		parent.AddChild(late);
		ctx.Assert(late.Paused, "Pause: a child started while paused waits too");

		parent.Deliver("tree", "/resume");
		ctx.Assert(!parent.Paused && !busy.Paused && !late.Paused, "Resume: releases what was paused");
		ctx.Assert(                      !parked.NeedsAttention(), "Resume: a child parked for its own reasons stays parked");

		int frames = 0;
		foreach ((FrameType type, string text) in transport.Snapshot())
		{
			if (type == FrameType.Config && text.Contains("session-state", StringComparison.Ordinal))
				frames++;
		}
		ctx.AssertEqual(6, frames, "Pause: the client hears each change of state");
		busy.SendIdle();
	}

	private static async Task TestPausedBeforeAnyTurnAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		await using Rig rig = await Rig.StartAsync(server, ct);
		rig.Session.Deliver(rig.Session.Id, "hello");
		rig.Session.Deliver(rig.Session.Id, "/pause");
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Say("answered"));

		await Task.Delay(500, ct);
		ctx.AssertEqual(0, server.Requests().Count, "Pause: a paused session sends nothing");
		ctx.Assert(LastPlayState(rig.Transport).Contains("\"canContinue\":true", StringComparison.Ordinal), "Play: offered for a message waiting behind a pause");

		rig.Session.Deliver(rig.Session.Id, "/resume");
		ctx.Assert(await WaitForAsync(() => LastIs<AssistantMessage>(rig.Session), ct), "Resume: the waiting message is sent");
		ctx.Assert(!rig.Session.Paused, "Resume: the session is no longer paused");
	}

	// The pause lands while the model's tool-call turn is on the wire: that turn and its tool run finish
	// and commit, and the next request does not go out until /resume.
	private static async Task TestPauseTakesEffectAtEndOfTurnAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		await using Rig rig = await Rig.StartAsync(server, ct);
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("call_1", "ls", "{\"folder\":\".\"}"));
		server.Enqueue(FakeReply.Say("finished"));
		server.OnRequest(count =>
		{
			if (count == 2)
				rig.Session.Deliver(rig.Session.Id, "/pause");
		});

		rig.Session.Deliver(rig.Session.Id, "go");
		ctx.Assert(await WaitForAsync(() => LastIs<ToolResultMessage>(rig.Session), ct), "Pause: the turn in flight finishes, its tool result committed");

		await Task.Delay(500, ct);
		ctx.AssertEqual(2, server.Requests().Count, "Pause: no further request goes out");
		ctx.Assert(LastIs<ToolResultMessage>(rig.Session), "Pause: the conversation waits on the tool result");
		ctx.Assert(LastPlayState(rig.Transport).Contains("\"canContinue\":true", StringComparison.Ordinal), "Play: offered when stopped on a tool result");

		rig.Session.Deliver(rig.Session.Id, "/resume");
		ctx.Assert(await WaitForAsync(() => LastIs<AssistantMessage>(rig.Session), ct), "Resume: the conversation continues where it stopped");
		ctx.AssertEqual(3, server.Requests().Count, "Resume: with exactly one more request");
	}

	private static async Task TestUserTextEndsPauseAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		await using Rig rig = await Rig.StartAsync(server, ct);
		rig.Session.Deliver(rig.Session.Id, "/pause");
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Say("answered"));

		rig.Session.Deliver(rig.Session.Id, "are you there");
		ctx.Assert(await WaitForAsync(() => LastIs<AssistantMessage>(rig.Session), ct), "Pause: text from the user is answered");
		ctx.Assert(!rig.Session.Paused, "Pause: and ends the pause");
	}

	private static async Task TestResumeWithNothingToSendAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		await using Rig rig = await Rig.StartAsync(server, ct);
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Say("answered"));
		rig.Session.Deliver(rig.Session.Id, "hello");
		await WaitForAsync(() => LastIs<AssistantMessage>(rig.Session), ct);
		int before = server.Requests().Count;

		rig.Session.Deliver(rig.Session.Id, "/resume");
		ctx.Assert(await WaitForAsync(() => HasStatus(rig.Transport, "Nothing to resume"), ct), "Resume: says so when the model has already answered");
		ctx.AssertEqual(before, server.Requests().Count, "Resume: and sends nothing");
		ctx.Assert(!LastPlayState(rig.Transport).Contains("\"canContinue\":true", StringComparison.Ordinal), "Play: not offered once the model has answered");
	}

	// A subagent that delivered its result ends on the terminator's tool result, which must not read as
	// more to send, and a compaction predecessor stays parked whatever it ended on.
	private static void TestFinishedSessionsCannotContinue(TestContext ctx)
	{
		TestCaptureTransport transport = new TestCaptureTransport();

		Session finished = NewSession("finished", transport);
		finished.Data.Messages.Add(new ToolResultMessage("call_1", "ok"));
		finished.ReportPlayState();
		ctx.Assert(LastPlayState(transport).Contains("\"canContinue\":true", StringComparison.Ordinal), "Play: offered for a conversation stopped on a tool result");
		finished.SetTerminationStatus(SessionStatus.Success);
		finished.ReportPlayState();
		ctx.Assert(LastPlayState(transport).Contains("\"canContinue\":false", StringComparison.Ordinal), "Play: withdrawn once the session has delivered its result");
		ctx.Assert(!finished.RequestSend(), "Resume: a finished session has nothing to send");

		Session failed = NewSession("failed", transport);
		failed.Bundle.OnUserMessage("unanswered");
		failed.SetTerminationStatus(SessionStatus.Failure);
		ctx.Assert(!failed.RequestSend(), "Resume: nor does one that reported failure");

		Session predecessor = NewSession("predecessor", transport);
		predecessor.Bundle.OnUserMessage("unanswered");
		predecessor.MarkInterrupted();
		predecessor.MarkCompactedAway();
		predecessor.Deliver("predecessor", "/resume");
		ctx.Assert(!predecessor.RequestSend(), "Resume: a compaction predecessor has nothing to send");
		while (predecessor.TryDequeuePending(out string? _))
		{
		}
		ctx.Assert(!predecessor.NeedsAttention(), "Resume: and stays parked");
	}

	private static string LastPlayState(TestCaptureTransport transport)
	{
		string last = string.Empty;
		foreach ((FrameType type, string text) in transport.Snapshot())
		{
			if (type == FrameType.Config && text.Contains("session-state", StringComparison.Ordinal))
				last = text;
		}
		return last;
	}

	private static async Task TestResumeAfterCancelAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		await using Rig rig = await Rig.StartAsync(server, ct);
		rig.Session.Bundle.OnUserMessage("left unanswered");
		rig.Session.MarkInterrupted();
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Say("answered"));

		await Task.Delay(300, ct);
		ctx.AssertEqual(0, server.Requests().Count, "Resume: an interrupted session is parked");

		rig.Session.Deliver(rig.Session.Id, "/resume");
		ctx.Assert(await WaitForAsync(() => LastIs<AssistantMessage>(rig.Session), ct), "Resume: an interrupted session is sent again");
	}

	private static async Task TestTrialAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		LlmRegistry registry = new LlmRegistry();
		registry.RecordCatalog(server.ChatEndpoint, new List<DiscoveredModel>
		{
			new DiscoveredModel { Id = "trial", Name = "Trial", ContextWindow = 200000 },
			new DiscoveredModel { Id = "decider", Name = "Decider", ContextWindow = 16384, Outputs = new List<string> { OutputModality.Decisions } }
		});
		AutoModelConfig candidate = new AutoModelConfig { Id = "trial", Temperature = 0.7m };

		// A reasoning model can spend the whole allowance thinking; it still answered.
		server.Reset();
		server.Enqueue(FakeReply.Turn("hmm", string.Empty, new List<(string, string, string)>()).WithFinish("length"));
		(LlmService? passing, string none) = registry.CreateTrialService(server.ChatEndpoint, "fake-key", candidate);
		ctx.AssertEqual(string.Empty, none, "Trial: a listed model gets a service");
		ctx.AssertEqual(string.Empty, await ModelTrial.RunAsync(passing!, "trial-session", ct), "Trial: thinking until cut off counts as an answer");
		ctx.AssertEqual(1, server.Requests().Count, "Trial: one request, not retried");
		ctx.Assert(passing!.ResendsThinking, "Thinking: replayed on Chat Completions by default");
		JsonNode body = server.Requests()[0].Body;
		ctx.AssertEqual(  16, body["max_completion_tokens"]?.GetValue<int>() ?? 0, "Trial: asks for 16 tokens");
		ctx.AssertEqual(0.7m,      body["temperature"]?.GetValue<decimal>() ?? 0m, "Trial: with the unsaved settings");

		// Compaction's projection relies on this: only plain-text replay fills the window from canonical.
		server.Reset();
		server.Enqueue(FakeReply.Say("hi"));
		passing.Model.Config.RetainReasoning = false;
		ctx.Assert(!passing.ResendsThinking, "Thinking: not replayed once the model is set not to retain it");

		registry.RecordCatalog(server.MessagesEndpoint, new List<DiscoveredModel> { new DiscoveredModel { Id = "trial", Name = "Trial", ContextWindow = 200000 } });
		(LlmService? messages, string _) = registry.CreateTrialService(server.MessagesEndpoint, "fake-key", new AutoModelConfig { Id = "trial" });
		ctx.AssertEqual(string.Empty, await ModelTrial.RunAsync(messages!, "trial-session", ct), "Thinking: the Messages trial answers");
		ctx.Assert(!messages!.ResendsThinking, "Thinking: Messages does not replay it from canonical");

		server.Reset();
		// A real provider refuses every form of the request, so the script does too: the protocol's own
		// fallbacks (such as retrying without streaming) must not read as the model having answered.
		for (int i = 0; i < 8; i++)
			server.Enqueue(FakeReply.Error(400, "{\"error\":{\"message\":\"temperature must be below 2\"}}"));
		(LlmService? failing, string _) = registry.CreateTrialService(server.ChatEndpoint, "fake-key", candidate);
		string refused                  = await ModelTrial.RunAsync(failing!, "trial-session", ct);
		ctx.Assert(refused.Contains("temperature must be below 2", StringComparison.Ordinal), $"Trial: the provider's refusal comes back ({refused}, after {server.Requests().Count} requests)");
		ctx.Assert(server.Requests().Count <= 3, $"Trial: a refusal is not ground through the retries ({server.Requests().Count} requests)");

		(LlmService? fresh, string _) = registry.CreateTrialService(server.ChatEndpoint, "fake-key", candidate);
		ctx.Assert(!fresh!.IsDown, "Trial: a failure marks nothing down");

		(LlmService? missing, string gone) = registry.CreateTrialService(server.ChatEndpoint, "fake-key", new AutoModelConfig { Id = "nope" });
		ctx.Assert(missing == null && gone.Contains("not in the catalog", StringComparison.Ordinal), "Trial: a model the endpoint does not list is reported");

		(LlmService? decider, string skipped) = registry.CreateTrialService(server.ChatEndpoint, "fake-key", new AutoModelConfig { Id = "decider" });
		ctx.Assert(decider == null && skipped.Length == 0, "Trial: a decisions-only model has nothing to try and no error");
	}

	// The whole path through a real handler: a long trace on a model that replays reasoning costs nothing while
	// the conversation runs, and /compact summarizes it, swapping the summary and a reference in, with the
	// original in the log. A compaction that goes on to a full summary pays for no trace summaries at all.
	private static async Task TestReasoningSummaryAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		const string kAsk = "Condense this AI agent";
		string trace        = "worked it out\n" + new string('t', 3000);
		await using Rig rig = await Rig.StartAsync(server, ct);
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Turn(trace, "answered", new List<(string, string, string)>()));

		rig.Session.Deliver(rig.Session.Id, "first");
		ctx.Assert(await WaitForAsync(() => AssistantCount(rig.Session) == 1, ct), "Summary: the long trace's turn is answered");

		server.Enqueue(FakeReply.Say("two"));
		rig.Session.Deliver(rig.Session.Id, "second");
		ctx.Assert(await WaitForAsync(() => AssistantCount(rig.Session) == 2, ct), "Summary: the conversation carries on");
		server.Enqueue(FakeReply.Say("three"));
		rig.Session.Deliver(rig.Session.Id, "third");
		ctx.Assert(await WaitForAsync(() => AssistantCount(rig.Session) == 3, ct), "Summary: and on");
		server.Enqueue(FakeReply.Say("four"));
		rig.Session.Deliver(rig.Session.Id, "fourth");
		ctx.Assert(await WaitForAsync(() => AssistantCount(rig.Session) == 4, ct), "Summary: and on again");
		await Task.Delay(300, ct);
		ctx.Assert(!AnyRequestContains(server, kAsk), "Summary: nothing is summarized before compaction");

		string logPath = CompactionLog.PathFor(rig.Session.Id);
		server.Enqueue(FakeReply.Say("The trace concluded something."));
		rig.Session.Deliver(rig.Session.Id, "/compact");
		ctx.Assert(await WaitForAsync(() => File.Exists(logPath), ct), "Summary: compaction writes its log");
		ctx.AssertContains(File.ReadAllText(logPath), trace, "Summary: with the original trace");
		File.Delete(logPath);
		JsonNode? summaryRequest = LastRequestContaining(server, kAsk);
		ctx.Assert(summaryRequest != null, "Summary: a mechanical compaction summarizes the trace it takes out");
		ctx.AssertContains(summaryRequest!.ToJsonString(), "worked it out", "Summary: the request carries the trace");
		ctx.Assert(summaryRequest["tools"] == null, "Summary: and no tools");

		// A user message too big for the mechanical pass to help: it goes straight to the full summary.
		await using Rig full = await Rig.StartAsync(server, ct);
		int before           = server.Requests().Count;
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Turn(trace, "answered", new List<(string, string, string)>()));
		full.Session.Deliver(full.Session.Id, new string('u', 60000));
		ctx.Assert(await WaitForAsync(() => AssistantCount(full.Session) == 1, ct), "Summary: the big conversation answers");
		for (int i = 2; i <= 4; i++)
		{
			int want = i;
			server.Enqueue(FakeReply.Say($"turn {i}"));
			full.Session.Deliver(full.Session.Id, $"more {i}");
			ctx.Assert(await WaitForAsync(() => AssistantCount(full.Session) == want, ct), $"Summary: the big conversation reaches turn {i}");
		}
		for (int i = 0; i < 8; i++)
			server.Enqueue(FakeReply.Say("A summary of everything."));

		// The rig measures every prompt at 100 tokens, so this window leaves the mechanical pass unable to reach
		// half of it: the big user message stays verbatim.
		full.Session.Data.ContextWindow = 150;
		full.Session.Deliver(full.Session.Id, "/compact");
		ctx.Assert(await WaitForAsync(() => SentContains(full.Transport, "[Compaction] Complete.") || SentContains(full.Transport, "[Compaction] Failed."), ct), "Summary: the compaction finishes");
		ctx.Assert(!SentContains(full.Transport, "elided mechanically"), "Summary: the mechanical pass declined");
		ctx.Assert(           !AnyRequestContains(server, kAsk, before), "Summary: and no trace summaries are paid for");
		File.Delete(CompactionLog.PathFor(full.Session.Id));

		// With the flag off nothing replays reasoning, so no summary is paid for.
		await using Rig plain                = await Rig.StartAsync(server, ct);
		plain.Service.Config.RetainReasoning = false;
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Turn(trace, "answered", new List<(string, string, string)>()));
		plain.Session.Deliver(plain.Session.Id, "first");
		ctx.Assert(await WaitForAsync(() => LastIs<AssistantMessage>(plain.Session), ct), "Summary: the flag-off conversation answers");
		await Task.Delay(300, ct);
		ctx.AssertEqual(2, server.Requests().Count, "Summary: and nothing extra is requested");
	}

	// A resumed subagent carries on with the budget it had, rather than being handed it all again. One that has
	// already answered runs free.
	private static void TestBudgetSurvivesResume(TestContext ctx, TestCaptureTransport transport)
	{
		BeastSession data    = new BeastSession("budget-resume", "budget", "model", "Developer", "return_to_caller", new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		data.WorkTurns       = 7;
		data.BudgetWarnLevel = 1;
		string       saved   = System.Text.Json.JsonSerializer.Serialize(data, BeastJson.Persist.BeastSession);
		BeastSession loaded  = System.Text.Json.JsonSerializer.Deserialize(saved, BeastJson.Persist.BeastSession)!;
		ctx.AssertEqual(7,       loaded.WorkTurns, "Resume: work turns are saved");
		ctx.AssertEqual(1, loaded.BudgetWarnLevel, "Resume: and the last warning");

		SessionHandler owing = new SessionHandler(new Session(loaded, "prompt", transport, true));
		ctx.AssertEqual(7,       (int)Reflect.GetField(owing, "_workTurns")!, "Resume: a session owing a reply carries on");
		ctx.AssertEqual(1, (int)Reflect.GetField(owing, "_budgetWarnLevel")!, "Resume: without its warning again");

		BeastSession answered = new BeastSession("budget-answered", "budget", "model", "Developer", string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		answered.WorkTurns    = 7;
		SessionHandler free   = new SessionHandler(new Session(answered, "prompt", transport, true));
		ctx.AssertEqual(0, (int)Reflect.GetField(free, "_workTurns")!, "Resume: one that has answered runs free");
	}

	// WebFetch answers only through return_fetched: sorted paths, or a summary a fresh session writes from the
	// seed's goal and the one file, with none of what the role saw on the way there.
	private static async Task TestReturnFetchedAsync(TestContext ctx, FakeProvider server, AgentOrchestrator orchestrator, Session parent, CancellationToken ct)
	{
		string folder = Path.Combine(Path.GetTempPath(), $"beast-fetched-{Guid.NewGuid():N}");
		Directory.CreateDirectory(folder);
		string b = Path.Combine(folder, "b.txt");
		string a = Path.Combine(folder, "a.txt");
		File.WriteAllText(b, "the file's own words");
		File.WriteAllText(a, "other");
		string       seed = "URL: http://example.test/secret-url\nObjective: the original goal\n---\nContent type: text\n\nnoise from the fetch";
		List<string> job  = new List<string> { "read_file", "bash", "return_fetched" };
		string       Args(params string[] paths) => new JsonObject { ["file_paths"] = new JsonArray(Array.ConvertAll(paths, p => (JsonNode?)JsonValue.Create(p))) }.ToJsonString();

		server.Reset();
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("f1", "return_fetched", Args(b, a)));
		(bool okList, string list, int _) = await orchestrator.RunSummarizerAsync(parent, "WebFetch", "fetch", "Extract it.", seed, job, 12, ct);
		ctx.Assert(okList, $"Fetched: paths come back ({list})");
		ctx.AssertEqual($"Fetched files:\n{a}\n{b}", list, "Fetched: the caller gets the paths, sorted");
		List<string> names = ToolNames(server.Requests()[server.Requests().Count - 1].Body);
		ctx.Assert(names.Contains("return_fetched") && !names.Contains("return_to_caller"), $"Fetched: it is the role's only way out ({string.Join(",", names)})");

		server.Reset();
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("f2", "return_fetched", Args()));
		(bool okEmpty, string empty, int _) = await orchestrator.RunSummarizerAsync(parent, "WebFetch", "fetch", "Extract it.", seed, job, 12, ct);
		ctx.Assert(!okEmpty, "Fetched: nothing downloaded is a failure");
		ctx.AssertContains(empty, "No files could be fetched.", "Fetched: in one fixed sentence");

		server.Reset();
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("f3", "return_fetched", Args(Path.Combine(folder, "missing.txt"))));
		server.Enqueue(FakeReply.Call("f4", "return_fetched", new JsonObject { ["summarize_file"] = b, ["file_paths"] = new JsonArray(JsonValue.Create(a)) }.ToJsonString()));
		server.Enqueue(FakeReply.Call("f5", "return_fetched", new JsonObject { ["summarize_file"] = b }.ToJsonString()));
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("f6", "return_to_caller", "{\"output\":\"the summary\",\"success\":true}"));
		(bool okSummary, string summary, int _) = await orchestrator.RunSummarizerAsync(parent, "WebFetch", "fetch", "Extract it.", seed, job, 12, ct);
		ctx.Assert(okSummary, $"Fetched: a summary comes back, after a missing file and both arguments are refused ({summary})");
		ctx.AssertEqual("the summary", summary, "Fetched: the caller gets what the summarizer wrote");
		string sent = server.Requests()[server.Requests().Count - 1].Body["messages"]!.ToJsonString();
		ctx.AssertContains(sent, "the original goal", "Fetched: the summarizer gets the caller's goal");
		ctx.AssertContains(sent, "the file\\u0027s own words", "Fetched: and the file");
		ctx.Assert(!sent.Contains("secret-url", StringComparison.Ordinal) && !sent.Contains("noise from the fetch", StringComparison.Ordinal) && !sent.Contains("b.txt", StringComparison.Ordinal), $"Fetched: and nothing else from the fetch — {sent}");

		Directory.Delete(folder, true);
		server.Reset();
	}

	// The small jobs are prompts on the Summarizer's models, and have nothing to show in /role.
	private static void TestSummarizerRoles(TestContext ctx)
	{
		RoleService roles      = new RoleService(Environment.CurrentDirectory);
		Role        summarizer = roles.Roles[ReasoningSummarizer.RoleName];
		ctx.Assert(RoleService.IsListed(summarizer), "Summarizer: has the one model list");
		foreach (string job in new string[] { "Explorer", "WebFetch", "WebFetchSummary", "WebSearch" })
		{
			ctx.Assert(!RoleService.IsListed(roles.Roles[job]), $"Summarizer: {job} has no models of its own and is not listed");
			ctx.AssertEqual(string.Join(",", summarizer.Models), string.Join(",", roles.GetRole(job)!.Models), $"Summarizer: {job} runs on its models");
		}
		ctx.Assert(RoleService.IsListed(roles.Roles["MediaReader"]), "Summarizer: MediaReader keeps its own list");

		ctx.AssertEqual( 3,    roles.GetRole("Explorer")!.MaxTurns, "Summarizer: Explorer gets 3 turns");
		ctx.AssertEqual( 0, roles.GetRole("Explorer")!.Tools.Count, "Summarizer: and no tools");
		ctx.AssertEqual(12,    roles.GetRole("WebFetch")!.MaxTurns, "Summarizer: WebFetch gets 12");
		ctx.Assert(roles.GetRole("WebFetch")!.Tools.Contains("bash") && roles.GetRole("WebFetch")!.Tools.Contains("read_file"), "Summarizer: and bash and read_file");

		string searchPrompt = roles.SummarizerPrompt("WebSearch");
		ctx.AssertContains(searchPrompt, "untrusted data",   "Summarizer: web search gets the shared hygiene");
		ctx.AssertContains(searchPrompt, "web search agent", "Summarizer: on top of its own prompt");
	}

	// The call itself: the caller's prompt on top of the Summarizer's, the first message, and exactly the
	// tools asked for, through the real orchestrator against the fake provider.
	private static async Task TestSummarizerCallAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		server.Reset();
		ModelConfig                  config   = new ModelConfig { Id = kModelId, Name = "Fake Pausable", ContextWindow = 200000, MaxOutputTokens = 4096 };
		LlmModel                     model    = new LlmModel(kModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);
		LlmRegistry                  registry = new LlmRegistry();
		Dictionary<string, LlmModel> models   = new Dictionary<string, LlmModel>(StringComparer.OrdinalIgnoreCase);
		models[kModelId]                      = model;
		registry.RestoreModels(models);

		RoleService roles                         = new RoleService(Environment.CurrentDirectory);
		roles.Roles[ReasoningSummarizer.RoleName] = roles.Roles[ReasoningSummarizer.RoleName].WithModels(new List<string> { kModelId });

		using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
		TestCaptureTransport transport         = new TestCaptureTransport();
		AgentOrchestrator    orchestrator      = new AgentOrchestrator(registry, roles, new SettingsService(), transport, lifetime, true);
		Session              parent            = NewSession(Guid.NewGuid().ToString("N"), transport);
		orchestrator.RegisterSession(parent);

		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("r1", "return_to_caller", "{\"output\":\"lines 1 to 2\",\"success\":true}"));
		(bool ok, string text, int _) = await orchestrator.RunSummarizerAsync(parent, "Explorer", "explore", "Cite the lines.", "the goal and a window", new List<string>(), 3, ct);
		ctx.Assert(ok, $"Call: comes back ({text})");
		ctx.AssertEqual("lines 1 to 2", text, "Call: with what the job returned");

		JsonNode body = server.Requests()[server.Requests().Count - 1].Body;
		string   sent = body["messages"]!.ToJsonString();
		ctx.AssertContains(sent, "untrusted data",        "Call: the Summarizer's hygiene is in the system prompt");
		ctx.AssertContains(sent, "Cite the lines.",       "Call: with the caller's prompt added");
		ctx.AssertContains(sent, "the goal and a window", "Call: and the caller's first message");
		ctx.AssertEqual("return_to_caller", string.Join(",", ToolNames(body)), "Call: no tools means only the way to reply");

		server.Reset();
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("r2", "return_to_caller", "{\"output\":\"done\",\"success\":true}"));
		(bool okTools, string _, int _) = await orchestrator.RunSummarizerAsync(parent, "WebFetch", "fetch", "Extract it.", "the page", new List<string> { "read_file", "bash" }, 12, ct);
		ctx.Assert(okTools, "Call: with tools comes back");
		List<string> names = ToolNames(server.Requests()[server.Requests().Count - 1].Body);
		ctx.Assert(names.Contains("read_file") && names.Contains("bash") && names.Contains("return_to_caller") && names.Count == 3, $"Call: exactly the tools asked for ({string.Join(",", names)})");
		ctx.Assert(!server.Requests()[server.Requests().Count - 1].Body["messages"]!.ToJsonString().Contains("This run is deterministically seeded", StringComparison.Ordinal), "Seeded run: a model with no seed is told nothing");

		await TestReturnFetchedAsync(ctx, server, orchestrator, parent, ct);
		TestBudgetSurvivesResume(ctx, transport);

		// A call that starts a child waits while it runs, and the child's total reaches the parent when it replies.
		server.Reset();
		long      before  = parent.WorkedMs;
		ToolClock clock   = new ToolClock();
		ToolClock.Current = clock;
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("r3", "return_to_caller", "{\"output\":\"done\",\"success\":true}").WithGenerate(800));
		(bool okTimed, string _, int _) = await orchestrator.RunSummarizerAsync(parent, "Explorer", "timed", "Cite the lines.", "the goal", new List<string>(), 3, ct);
		long callWorked                 = clock.WorkedMs;
		ToolClock.Current               = null;
		long childWorked                = parent.WorkedMs - before;
		ctx.Assert(okTimed && childWorked >= 700 && childWorked < 2500, $"WorkTime: a child's time reaches its parent by the time it has replied (got {childWorked} ms)");
		ctx.Assert(callWorked < 400, $"WorkTime: the call that started it counts only its own time, not the child's run (got {callWorked} ms)");

		// With a seed, a conversation with tools is told.
		ModelConfig seededConfig = new ModelConfig { Id = kModelId, Name = "Fake Seeded", ContextWindow = 200000, MaxOutputTokens = 4096, Seed = 7 };
		models[kModelId]         = new LlmModel(kModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), seededConfig);
		registry.RestoreModels(models);
		server.Reset();
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("r4", "return_to_caller", "{\"output\":\"done\",\"success\":true}"));
		await orchestrator.RunSummarizerAsync(parent, "Explorer", "seeded", "Cite the lines.", "the goal", new List<string>(), 3, ct);
		ctx.AssertContains(server.Requests()[server.Requests().Count - 1].Body["messages"]!.ToJsonString(), "This run is deterministically seeded", "Seeded run: a seeded model is warned in its system prompt");
		ctx.Assert(!server.Requests()[server.Requests().Count - 1].Body["messages"]!.ToJsonString().Contains("[Worktree]", StringComparison.Ordinal), "Seeded run: a subagent is not told a branch or worktree name");

		// A role's model with no seed takes its caller's, and keeps it as its session goes on.
		ModelConfig plainConfig = new ModelConfig { Id = "plain", Name = "Fake Plain", ContextWindow = 200000, MaxOutputTokens = 4096 };
		LlmModel    plainModel  = new LlmModel("plain", server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), plainConfig);
		models["plain"]         = plainModel;
		registry.RestoreModels(models);
		roles.Roles[ReasoningSummarizer.RoleName] = roles.Roles[ReasoningSummarizer.RoleName].WithModels(new List<string> { "plain" });
		server.Reset();
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Call("r5", "return_to_caller", "{\"output\":\"done\",\"success\":true}"));
		await orchestrator.RunSummarizerAsync(parent, "Explorer", "conferred", "Cite the lines.", "the goal", new List<string>(), 3, ct);
		JsonNode conferred = server.Requests()[server.Requests().Count - 1].Body;
		ctx.AssertEqual(7L, conferred["seed"]?.GetValue<long>() ?? -1L, "Seeded run: a model with no seed takes its caller's");
		ctx.AssertContains(conferred["messages"]!.ToJsonString(), "This run is deterministically seeded", "Seeded run: and is told about the run");

		BeastSession child     = new BeastSession("p_9", "p_9", "plain", "r", string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		child.ConferredSeed    = 7;
		BeastSession? reloaded = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(child, BeastJson.Persist.BeastSession), BeastJson.Persist.BeastSession);
		ctx.AssertEqual(7L, reloaded?.ConferredSeed ?? -1L, "Seeded run: a conferred seed is kept in the session's file");
		ctx.AssertEqual(7L, registry.SeedOf(new Session(child, string.Empty, transport, true)) ?? -1L, "Seeded run: and passes on to what that session calls");

		// A seeded model does not fall back. The same list falls back without a seed.
		roles.Roles[ReasoningSummarizer.RoleName] = roles.Roles[ReasoningSummarizer.RoleName].WithModels(new List<string> { kModelId, "plain" });
		Role        twoModels                     = roles.Roles[ReasoningSummarizer.RoleName];
		LlmService? seededTop                     = registry.CreateService(twoModels, kModelId, 0, false);
		ctx.Assert(seededTop != null && seededTop.Seeded && registry.CreateFallbackService(seededTop, 0, false) == null, "Seeded run: a seeded model has no fallback");
		models[kModelId] = new LlmModel(kModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), new ModelConfig { Id = kModelId, Name = "Fake", ContextWindow = 200000, MaxOutputTokens = 4096 });
		registry.RestoreModels(models);
		LlmService? plainTop = registry.CreateService(twoModels, kModelId, 0, false);
		ctx.Assert(plainTop != null && registry.CreateFallbackService(plainTop, 0, false) != null, "Unseeded run: the same list falls back");

		// A seed only counts on a protocol that has one.
		LlmModel Seeded(string endpoint) => new LlmModel("m", endpoint, "k", new List<JsonObject>(), new List<JsonObject>(), new ModelConfig { Id = "m", Name = "m", ContextWindow = 1000, MaxOutputTokens = 100, Seed = 3 });
		foreach ((DetectedProtocol protocol, bool takes) in new[] { (DetectedProtocol.ChatCompletions, true), (DetectedProtocol.Anthropic, false), (DetectedProtocol.Responses, false), (DetectedProtocol.ClaudeDashP, false) })
			ctx.AssertEqual(takes, Service(Seeded("http://x/v1/y"), protocol, new ModelAvailability()).Seeded, $"Seeded run: a seed on {protocol} {(takes ? "counts" : "does not count")}");
		ctx.Assert(ProtocolProxy.EndpointTakesSeed(ModelCatalog.NormalizeRequestEndpoint("http://x/v1")) && !ProtocolProxy.EndpointTakesSeed("https://x/v1/messages") && !ProtocolProxy.EndpointTakesSeed("https://x/v1/responses"), "Seeded run: the config page can tell which endpoints take a seed");

		// Hiding host_command leaves everything else in order.
		Tool   Named(string name) => new Tool { Definition = new ToolDefinition { Type = "function", Function = new FunctionDefinition { Name = name, Description = name, Parameters = new JsonObject() } } };
		Tool[] kept                = (Tool[])Reflect.Static(typeof(SessionHandler), "WithoutHostCommand", new Type[] { typeof(Tool[]) }, new object[] { new Tool[] { Named("bash"), Named("host_command"), Named("ls") } })!;
		ctx.AssertEqual("bash,ls", string.Join(",", Array.ConvertAll(kept, t => t.Definition.Function.Name)), "Seeded run: host_command is not offered");

		// Root IDs count up, and hold nothing that reads as a child.
		long first = long.Parse(SessionService.AllocateRootId());
		ctx.AssertEqual(first + 1, long.Parse(SessionService.AllocateRootId()), "Session IDs: each root takes the next number");

		await WaitsOutFailuresAsync(ctx, server, ct);

		lifetime.Cancel();
	}

	private static LlmService Service(LlmModel model, DetectedProtocol protocol, ModelAvailability availability)
	{
		return new LlmService(model, protocol, availability, new EndpointFacts(), new List<string> { model.ConfigId }, (id, effort, summaries, retain, maxImages) => { });
	}

	// More failures than the retry limits allow. A seeded model waits through them, and an unseeded one gives up so
	// it can fall back. The backoffs are skipped, or this would take minutes.
	private static async Task WaitsOutFailuresAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		async Task<ProtocolCallOutcome> RunAsync(long? seed)
		{
			ModelAvailability    availability = new ModelAvailability();
			LlmModel             model        = new LlmModel("m", server.ChatEndpoint, "k", new List<JsonObject>(), new List<JsonObject>(), new ModelConfig { Id = "m", Name = "m", ContextWindow = 200000, MaxOutputTokens = 4096, Seed = seed });
			TestCaptureTransport transport    = new TestCaptureTransport();
			Session              session      = NewSession(Guid.NewGuid().ToString("N"), transport);
			session.AddUserMessage("hello");

			using CancellationTokenSource done = CancellationTokenSource.CreateLinkedTokenSource(ct);
			Task skipBackoffs                  = Task.Run(async () =>
			{
				while (!done.IsCancellationRequested)
				{
					availability.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1);
					await Task.Delay(10);
				}
			});
			ProtocolResult result = await Service(model, DetectedProtocol.ChatCompletions, availability).RunToCompletionAsync(session, Array.Empty<Tool>(), null, 0, 0, false, transport, ct);
			done.Cancel();
			await skipBackoffs;
			return result.Outcome;
		}

		server.Reset();
		for (int i = 0; i < 12; i++)
			server.Enqueue(FakeReply.Error(429, "{\"error\":{\"message\":\"slow down\"}}"));
		for (int i = 0; i < 7; i++)
			server.Enqueue(FakeReply.Error(500, "{\"error\":{\"message\":\"boom\"}}"));
		server.Enqueue(FakeReply.Say("still here"));
		ctx.AssertEqual(ProtocolCallOutcome.Success, await RunAsync(7), "Seeded run: a seeded model waits through rate limits and server errors");

		server.Reset();
		for (int i = 0; i < 7; i++)
			server.Enqueue(FakeReply.Error(500, "{\"error\":{\"message\":\"boom\"}}"));
		ctx.Assert(await RunAsync(null) != ProtocolCallOutcome.Success, "Unseeded run: the same errors end the call");
	}

	private static List<string> ToolNames(JsonNode body)
	{
		List<string> names = new List<string>();
		JsonArray?   tools = body["tools"] as JsonArray;
		if (tools != null)
		{
			foreach (JsonNode? tool in tools)
				names.Add(tool?["function"]?["name"]?.GetValue<string>() ?? string.Empty);
		}
		return names;
	}

	private static bool SentContains(TestCaptureTransport transport, string text)
	{
		bool found = false;
		foreach ((FrameType _, string sent) in transport.Snapshot())
			found = found || sent.Contains(text, StringComparison.Ordinal);
		return found;
	}

	private static bool AnyRequestContains(FakeProvider server, string text)
	{
		return AnyRequestContains(server, text, 0);
	}

	private static bool AnyRequestContains(FakeProvider server, string text, int from)
	{
		bool              found    = false;
		List<FakeRequest> requests = server.Requests();
		for (int i = from; i < requests.Count; i++)
			found = found || requests[i].Body.ToJsonString().Contains(text, StringComparison.Ordinal);
		return found;
	}

	private static JsonNode? LastRequestContaining(FakeProvider server, string text)
	{
		JsonNode? last = null;
		foreach (FakeRequest request in server.Requests())
		{
			if (request.Body.ToJsonString().Contains(text, StringComparison.Ordinal))
				last = request.Body;
		}
		return last;
	}

	private static int AssistantCount(Session session)
	{
		int count = 0;
		foreach (CanonicalMessage message in session.Data.Messages)
		{
			if (message is AssistantMessage)
				count++;
		}
		return count;
	}

	private static Session NewSession(string id, ITransportServer transport)
	{
		BeastSession data = new BeastSession(id, id, kModelId, kRoleName, string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		return new Session(data, "You are a test driver.", transport, false);
	}

	private static async Task<bool> WaitForAsync(Func<bool> condition, CancellationToken ct)
	{
		bool met = condition();
		for (int i = 0; i < 200 && !met; i++)
		{
			await Task.Delay(50, ct);
			met = condition();
		}
		return met;
	}

	private static bool LastIs<T>(Session session)
	{
		List<CanonicalMessage> messages = session.Data.Messages;
		return messages.Count > 0 && messages[messages.Count - 1] is T;
	}

	private static bool HasStatus(TestCaptureTransport transport, string needle)
	{
		bool seen = false;
		foreach ((FrameType type, string text) in transport.Snapshot())
		{
			if (type == FrameType.Status && text.Contains(needle, StringComparison.Ordinal))
				seen = true;
		}
		return seen;
	}

	// A real handler driving one session against the fake provider, with a role that may call ls.
	private sealed class Rig : IAsyncDisposable
	{
		public Session              Session   { get; }
		public TestCaptureTransport Transport { get; }
		public LlmModel             Service   { get; }

		private readonly CancellationTokenSource _cts;
		private readonly Task                    _run;

		private Rig(Session session, TestCaptureTransport transport, LlmModel service, CancellationTokenSource cts, Task run)
		{
			Session   = session;
			Transport = transport;
			Service   = service;
			_cts      = cts;
			_run      = run;
		}

		public static Task<Rig> StartAsync(FakeProvider server, CancellationToken ct)
		{
			server.Reset();

			ModelConfig config = new ModelConfig { Id = kModelId, Name = "Fake Pausable", ContextWindow = 200000, MaxOutputTokens = 4096 };
			LlmModel    model  = new LlmModel(kModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);

			LlmRegistry                  registry = new LlmRegistry();
			Dictionary<string, LlmModel> models   = new Dictionary<string, LlmModel>(StringComparer.OrdinalIgnoreCase);
			models[kModelId]                      = model;
			registry.RestoreModels(models);
			RoleService roleService      = new RoleService(Environment.CurrentDirectory);
			roleService.Roles[kRoleName] = new Role(kRoleName, "pausable", RoleKind.Agent, new List<string> { kModelId },
				new List<string> { "ls" }, "You are a test driver.", "Summarize.", string.Empty);
			roleService.Roles[ReasoningSummarizer.RoleName] = new Role(ReasoningSummarizer.RoleName, "summarizes", RoleKind.Subagent, new List<string> { kModelId },
				new List<string>(), "Condense this.", string.Empty, string.Empty);

			TestCaptureTransport transport = new TestCaptureTransport();
			Session              session   = NewSession(Guid.NewGuid().ToString("N"), transport);
			session.UpdateModel(model);

			CancellationTokenSource cts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
			SessionHandler          handler = new SessionHandler(session);
			session.TryAttachHandler();
			Task run = handler.RunAsync(registry, roleService, new SettingsService(), transport, new FakeLlmTests.StubOrchestrator(), false, cts.Token);
			return Task.FromResult(new Rig(session, transport, model, cts, run));
		}

		public async ValueTask DisposeAsync()
		{
			_cts.Cancel();
			try
			{
				await _run;
			}
			catch (OperationCanceledException)
			{
			}
			_cts.Dispose();
		}
	}
}