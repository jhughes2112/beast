using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// A usage limit with a stated reset, on a role with nothing to fall back to: the session neither
// alerts nor parks, but waits on the model that recovers first and re-submits the turn the moment
// the registry says it can serve. Driven through the real SessionHandler, LlmRegistry and
// ChatCompletions protocol against FakeProvider; the limit "lifting" is the registry's backoff
// being cleared, which is exactly what the wait watches.
public static class UsageLimitResumeTests
{
	private const string kModelId  = "fake-limited";
	private const string kRoleName = "LimitTester";

	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  UsageLimitResumeTests");

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
			await TestWaitsOutLimitAndResumesAsync(ctx, server, cts.Token);
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

	private static async Task TestWaitsOutLimitAndResumesAsync(TestContext ctx, FakeProvider server, CancellationToken ct)
	{
		ModelConfig config = new ModelConfig { Id = kModelId, Name = "Fake Limited", ContextWindow = 200000, MaxOutputTokens = 4096 };
		LlmModel    model  = new LlmModel(kModelId, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);

		LlmRegistry                  registry = new LlmRegistry();
		Dictionary<string, LlmModel> models   = new Dictionary<string, LlmModel>(StringComparer.OrdinalIgnoreCase);
		models[kModelId]                      = model;
		registry.RestoreModels(models);
		RoleService roleService      = new RoleService(Environment.CurrentDirectory);
		roleService.Roles[kRoleName] = new Role(kRoleName, "one limited model", RoleKind.Agent, new List<string> { kModelId },
			new List<string>(), "You are a test driver.", string.Empty, string.Empty);

		// The first request is the fresh session's token count; the turn itself is refused for an
		// hour — far past the in-place retry window — and its re-submission is answered.
		server.Reset();
		server.Enqueue(FakeReply.Say("counted"));
		server.Enqueue(FakeReply.Error(429, "{\"error\":{\"type\":\"usage_limit_reached\",\"message\":\"The usage limit has been reached\",\"resets_in_seconds\":3600}}"));
		server.Enqueue(FakeReply.Say("Finished after the limit lifted."));

		TestCaptureTransport transport = new TestCaptureTransport();
		BeastSession         data      = new BeastSession(Guid.NewGuid().ToString("N"), "limited", kModelId, kRoleName,
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a test driver.", transport, false);
		session.UpdateModel(model);
		session.AddUserMessage("Do the work.");

		using CancellationTokenSource runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		SessionHandler handler               = new SessionHandler(session);
		session.TryAttachHandler();
		Task run = handler.RunAsync(registry, roleService, new SettingsService(Environment.CurrentDirectory), transport, new FakeLlmTests.StubOrchestrator(), false, runCts.Token);

		bool waiting = await WaitForStatusAsync(transport, "resumes automatically", ct);
		ctx.Assert(waiting, "UsageLimit: with no fallback, the session says it will resume on its own");
		ctx.Assert(!SentAny(transport, FrameType.Alert), "UsageLimit: a limit that lifts by itself raises no alert");

		// Still waiting a moment later: the turn is held, not retried against the limit.
		await Task.Delay(500, ct);
		int beforeLift = server.Requests().Count;
		ctx.AssertEqual(2, beforeLift, "UsageLimit: nothing is sent while the limit holds");

		registry.ResetAvailability(kModelId);
		bool resumed = await WaitForStatusAsync(transport, "Usage limit lifted", ct);
		ctx.Assert(resumed, "UsageLimit: the turn is re-submitted once the model can serve");
		ctx.Assert(server.Requests().Count > beforeLift, "UsageLimit: the re-submission reached the provider");
		ctx.Assert(            LastIsAssistant(session), "UsageLimit: the turn completes with the model's answer");
		ctx.Assert(!SentAny(transport, FrameType.Alert), "UsageLimit: and no alert was ever raised");

		runCts.Cancel();
		try
		{
			await run;
		}
		catch (OperationCanceledException)
		{
		}
	}

	private static async Task<bool> WaitForStatusAsync(TestCaptureTransport transport, string needle, CancellationToken ct)
	{
		bool seen = false;
		for (int i = 0; i < 200 && !seen; i++)
		{
			foreach ((FrameType type, string text) in new List<(FrameType Type, string Text)>(transport.Sent))
			{
				if (type == FrameType.Status && text.Contains(needle, StringComparison.Ordinal))
					seen = true;
			}
			if (!seen)
				await Task.Delay(50, ct);
		}
		return seen;
	}

	private static bool SentAny(TestCaptureTransport transport, FrameType wanted)
	{
		bool any = false;
		foreach ((FrameType type, string _) in new List<(FrameType Type, string Text)>(transport.Sent))
		{
			if (type == wanted)
				any = true;
		}
		return any;
	}

	private static bool LastIsAssistant(Session session)
	{
		List<CanonicalMessage> messages = session.Data.Messages;
		return messages.Count > 0 && messages[messages.Count - 1] is AssistantMessage;
	}
}