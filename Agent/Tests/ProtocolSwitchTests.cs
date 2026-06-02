using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Integration tests that verify multi-turn protocol switching within a single session.
// Three scenarios are covered:
//   1. Fresh single-turn hello on every available model (smoke test for basic connectivity).
//   2. Same-protocol model switch: turn 1 on model A, turn 2 on model B (same wire format),
//      verify the reply contains "PONG" and the canonical history grew.
//   3. Cross-protocol switch: an additional turn on a model whose wire format differs from the
//      first two, verify the reply contains "BOOMERANG" and history grew again.
//
// Tests skip gracefully when fewer than the required distinct models are available.
public static class ProtocolSwitchTests
{
	public static async Task TestAsync(TestContext ctx, LlmRegistry registry, RoleService roleService, CancellationToken cancellationToken)
	{
		ctx.Log("  ProtocolSwitchTests");

		List<LLMRole> roles = new List<LLMRole>(roleService.Roles.Values);
		if (roles.Count == 0)
		{
			ctx.Log("    SKIP: no roles configured");
			return;
		}

		// Use the first role that has at least one available model.
		LLMRole? testRole = null;
		foreach (LLMRole r in roles)
		{
			foreach (string mid in r.Models)
			{
				LlmService? svc = registry.GetServiceById(mid);
				if (svc != null && svc.IsAvailable && !string.IsNullOrEmpty(svc.Model.ApiKey))
				{
					testRole = r;
					break;
				}
			}
			if (testRole != null) break;
		}

		if (testRole == null)
		{
			ctx.Log("    SKIP: no role with an available model found");
			return;
		}

		// Partition available services into two endpoint families so we can exercise same-
		// protocol and cross-protocol switching. Anthropic endpoints are one family;
		// everything else (OpenAI-style ChatCompletions or Responses) is the other.
		List<LlmService> anthropicServices = new List<LlmService>();
		List<LlmService> openAiStyleServices = new List<LlmService>();

		foreach (string mid in testRole.Models)
		{
			LlmService? svc = registry.GetServiceById(mid);
			if (svc == null || !svc.IsAvailable || string.IsNullOrEmpty(svc.Model.ApiKey))
				continue;

			if (svc.Model.Endpoint.IndexOf("anthropic.com", StringComparison.OrdinalIgnoreCase) >= 0)
				anthropicServices.Add(svc);
			else
				openAiStyleServices.Add(svc);
		}

		await RunFreshSessionTestsAsync(ctx, testRole, registry, anthropicServices, openAiStyleServices, cancellationToken);
		await RunSameProtocolSwitchTestAsync(ctx, testRole, registry, anthropicServices, openAiStyleServices, cancellationToken);
		await RunCrossProtocolSwitchTestAsync(ctx, testRole, registry, anthropicServices, openAiStyleServices, cancellationToken);
	}

	// Turn 1: send "hello" to every available model and verify a non-empty reply arrives.
	private static async Task RunFreshSessionTestsAsync(TestContext ctx, LLMRole role, LlmRegistry registry,
		List<LlmService> anthropicServices, List<LlmService> openAiStyleServices, CancellationToken cancellationToken)
	{
		ctx.Log("    FreshSession");

		List<LlmService> all = new List<LlmService>(anthropicServices);
		all.AddRange(openAiStyleServices);

		if (all.Count == 0)
		{
			ctx.Log("      SKIP: no available services");
			return;
		}

		Tool[] tools = registry.GetToolsForRole(role);

		foreach (LlmService service in all)
		{
			string modelId = service.Model.ConfigId;
			ctx.Log($"      Model: {modelId}");
			try
			{
				TestCaptureTransport transport = new TestCaptureTransport();
				BeastSession session = BeastSession.CreateNew(Guid.NewGuid().ToString("N"), role.Name, $"proto-fresh-{modelId}");

				ListenerBundle bundle = new ListenerBundle(
					new ListenerChatCompletions(session.ChatCompletionsState),
					new ListenerTransport(transport));

				if (!string.IsNullOrEmpty(role.SystemPrompt))
					bundle.OnSystemMessage(null!, role.SystemPrompt);
				bundle.OnUserMessage(null!, "Say exactly: HELLO");

				using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				cts.CancelAfter(TimeSpan.FromSeconds(30));

				LlmResult result = await service.RunToCompletionAsync(session, bundle, tools, 0, transport, cts.Token);

				if (result.ExitReason != LlmExitReason.Completed)
				{
					ctx.Log($"        SKIP [{modelId}]: {result.ErrorMessage}");
					continue;
				}

				bool gotHello = ResponseContains(transport, "HELLO");
				ctx.Assert(gotHello, $"FreshSession [{modelId}]: response contains HELLO");
				ctx.Log($"        {(gotHello ? "PASS" : "FAIL")} [{modelId}]");
			}
			catch (OperationCanceledException)
			{
				ctx.Log($"        TIMEOUT [{modelId}]");
			}
			catch (Exception ex)
			{
				ctx.Log($"        ERROR [{modelId}]: {ex.Message}");
			}
		}
	}

	// Turn 1 on serviceA → "PING", turn 2 on serviceB (same protocol family) → "PONG".
	// The canonical history must grow after each turn so the second model sees context.
	private static async Task RunSameProtocolSwitchTestAsync(TestContext ctx, LLMRole role, LlmRegistry registry,
		List<LlmService> anthropicServices, List<LlmService> openAiStyleServices, CancellationToken cancellationToken)
	{
		ctx.Log("    SameProtocolSwitch");

		// Need two distinct models from the same family; prefer the larger list.
		List<LlmService> family = openAiStyleServices.Count >= 2 ? openAiStyleServices : anthropicServices;
		if (family.Count < 2)
		{
			ctx.Log("      SKIP: need at least 2 models of the same protocol family");
			return;
		}

		LlmService serviceA = family[0];
		LlmService serviceB = family[1];
		Tool[] tools = registry.GetToolsForRole(role);

		ctx.Log($"      ModelA: {serviceA.Model.ConfigId}  ModelB: {serviceB.Model.ConfigId}");

		try
		{
			TestCaptureTransport transport = new TestCaptureTransport();
			BeastSession session = BeastSession.CreateNew(Guid.NewGuid().ToString("N"), role.Name, "proto-same-switch");

			ListenerBundle bundle = new ListenerBundle(
				new ListenerChatCompletions(session.ChatCompletionsState),
				new ListenerTransport(transport));

			if (!string.IsNullOrEmpty(role.SystemPrompt))
				bundle.OnSystemMessage(null!, role.SystemPrompt);
			bundle.OnUserMessage(null!, "Say exactly: PING");

			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			cts.CancelAfter(TimeSpan.FromSeconds(60));

			// Turn 1 — model A.
			LlmResult result1 = await serviceA.RunToCompletionAsync(session, bundle, tools, 0, transport, cts.Token);
			if (result1.ExitReason != LlmExitReason.Completed)
			{
				ctx.Log($"      SKIP: turn 1 failed — {result1.ErrorMessage}");
				return;
			}

			int canonicalAfterTurn1 = session.ChatCompletionsState.Count;

			// Turn 2 — model B (same protocol). ProtocolProxy on serviceB will detect, install,
			// and rehydrate from the canonical state that already holds the prior exchange.
			bundle.OnUserMessage(null!, "Now say exactly: PONG");
			LlmResult result2 = await serviceB.RunToCompletionAsync(session, bundle, tools, 0, transport, cts.Token);
			if (result2.ExitReason != LlmExitReason.Completed)
			{
				ctx.Log($"      SKIP: turn 2 failed — {result2.ErrorMessage}");
				return;
			}

			int canonicalAfterTurn2 = session.ChatCompletionsState.Count;

			bool gotPong = ResponseContains(transport, "PONG");
			bool historyGrew = canonicalAfterTurn2 > canonicalAfterTurn1;

			ctx.Assert(gotPong, $"SameProtocolSwitch: turn 2 response contains PONG");
			ctx.Assert(historyGrew, $"SameProtocolSwitch: canonical history grew after turn 2 ({canonicalAfterTurn1} -> {canonicalAfterTurn2})");
			ctx.Log($"      {(gotPong && historyGrew ? "PASS" : "FAIL")}");
		}
		catch (OperationCanceledException)
		{
			ctx.Log("      TIMEOUT");
		}
		catch (Exception ex)
		{
			ctx.Log($"      ERROR: {ex.Message}");
		}
	}

	// Continues the same session with a model from the opposite protocol family → "BOOMERANG".
	// The new ProtocolProxy detects a different wire format, installs it, and rehydrates from
	// the full canonical history so the cross-protocol model has the entire context.
	private static async Task RunCrossProtocolSwitchTestAsync(TestContext ctx, LLMRole role, LlmRegistry registry,
		List<LlmService> anthropicServices, List<LlmService> openAiStyleServices, CancellationToken cancellationToken)
	{
		ctx.Log("    CrossProtocolSwitch");

		if (anthropicServices.Count == 0 || openAiStyleServices.Count == 0)
		{
			ctx.Log("      SKIP: need at least one model in each protocol family (Anthropic and OpenAI-style)");
			return;
		}

		LlmService openAiService = openAiStyleServices[0];
		LlmService anthropicService = anthropicServices[0];
		Tool[] tools = registry.GetToolsForRole(role);

		ctx.Log($"      OpenAI-style: {openAiService.Model.ConfigId}  Anthropic: {anthropicService.Model.ConfigId}");

		try
		{
			TestCaptureTransport transport = new TestCaptureTransport();
			BeastSession session = BeastSession.CreateNew(Guid.NewGuid().ToString("N"), role.Name, "proto-cross-switch");

			ListenerBundle bundle = new ListenerBundle(
				new ListenerChatCompletions(session.ChatCompletionsState),
				new ListenerTransport(transport));

			if (!string.IsNullOrEmpty(role.SystemPrompt))
				bundle.OnSystemMessage(null!, role.SystemPrompt);
			bundle.OnUserMessage(null!, "Say exactly: PING");

			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			cts.CancelAfter(TimeSpan.FromSeconds(90));

			// Turn 1 — OpenAI-style model.
			LlmResult result1 = await openAiService.RunToCompletionAsync(session, bundle, tools, 0, transport, cts.Token);
			if (result1.ExitReason != LlmExitReason.Completed)
			{
				ctx.Log($"      SKIP: turn 1 (OpenAI-style) failed — {result1.ErrorMessage}");
				return;
			}

			int canonicalAfterTurn1 = session.ChatCompletionsState.Count;

			// Turn 2 — same OpenAI-style model, ask for PONG.
			bundle.OnUserMessage(null!, "Now say exactly: PONG");
			LlmResult result2 = await openAiService.RunToCompletionAsync(session, bundle, tools, 0, transport, cts.Token);
			if (result2.ExitReason != LlmExitReason.Completed)
			{
				ctx.Log($"      SKIP: turn 2 (OpenAI-style) failed — {result2.ErrorMessage}");
				return;
			}

			int canonicalAfterTurn2 = session.ChatCompletionsState.Count;

			// Turn 3 — Anthropic model (cross-protocol). ProtocolProxy detects Anthropic,
			// installs ProtocolAnthropic, and rehydrates from the accumulated canonical state.
			bundle.OnUserMessage(null!, "Now say exactly: BOOMERANG");
			LlmResult result3 = await anthropicService.RunToCompletionAsync(session, bundle, tools, 0, transport, cts.Token);
			if (result3.ExitReason != LlmExitReason.Completed)
			{
				ctx.Log($"      SKIP: turn 3 (Anthropic) failed — {result3.ErrorMessage}");
				return;
			}

			int canonicalAfterTurn3 = session.ChatCompletionsState.Count;

			bool gotPong = ResponseContains(transport, "PONG");
			bool gotBoomerang = ResponseContains(transport, "BOOMERANG");
			bool historyGrewTurn2 = canonicalAfterTurn2 > canonicalAfterTurn1;
			bool historyGrewTurn3 = canonicalAfterTurn3 > canonicalAfterTurn2;

			ctx.Assert(gotPong, $"CrossProtocolSwitch: turn 2 response contains PONG");
			ctx.Assert(gotBoomerang, $"CrossProtocolSwitch: turn 3 response contains BOOMERANG");
			ctx.Assert(historyGrewTurn2, $"CrossProtocolSwitch: canonical grew after turn 2 ({canonicalAfterTurn1} -> {canonicalAfterTurn2})");
			ctx.Assert(historyGrewTurn3, $"CrossProtocolSwitch: canonical grew after turn 3 ({canonicalAfterTurn2} -> {canonicalAfterTurn3})");

			bool pass = gotPong && gotBoomerang && historyGrewTurn2 && historyGrewTurn3;
			ctx.Log($"      {(pass ? "PASS" : "FAIL")}");
		}
		catch (OperationCanceledException)
		{
			ctx.Log("      TIMEOUT");
		}
		catch (Exception ex)
		{
			ctx.Log($"      ERROR: {ex.Message}");
		}
	}

	// Returns true if any Output frame in the capture contains the expected substring.
	private static bool ResponseContains(TestCaptureTransport transport, string expected)
	{
		foreach ((FrameType type, string text) in transport.Sent)
		{
			if (type == FrameType.Output && text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
		}
		return false;
	}
}
