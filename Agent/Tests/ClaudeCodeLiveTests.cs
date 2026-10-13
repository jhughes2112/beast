using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Live checks of the Claude Code protocol against the real CLI and the subscription configured in
// ~/.beast/settings.json, through the production turn loop (LlmService, ToolDispatch). Opt-in with
// --test-claude-code and never part of --test: there is no fake for the CLI, and each run spends a
// few small Haiku requests from the subscription.
public static class ClaudeCodeLiveTests
{
	private const string kModelId = "claude-code/haiku";

	private sealed class Rig
	{
		public Session              Session   { get; }
		public LlmService           Service   { get; }
		public Tool[]               Tools     { get; }
		public TestCaptureTransport Transport { get; }

		public Rig(Session session, LlmService service, Tool[] tools, TestCaptureTransport transport)
		{
			Session   = session;
			Service   = service;
			Tools     = tools;
			Transport = transport;
		}
	}

	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  ClaudeCodeLiveTests");

		string token = string.Empty;
		foreach (AutoProviderConfig auto in new SettingsService(Environment.CurrentDirectory).Settings.Auto)
		{
			if (ClaudeCode.IsEndpoint(auto.BaseUrl))
				token = auto.ApiKey;
		}
		if (token.Length == 0)
		{
			ctx.Log("    SKIP: no Anthropic Subscription endpoint is configured");
			return;
		}

		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		Rig rig = Build(token);
		try
		{
			await TestPlainTextAsync(ctx, rig, cts.Token);
			await TestToolRoundTripAsync(ctx, rig, cts.Token);
			await TestTwoCallsAsync(ctx, rig, cts.Token);
			await TestReseedAsync(ctx, rig, token, cts.Token);
		}
		finally
		{
			rig.Session.InvalidateProtocol();
		}
	}

	private static async Task TestPlainTextAsync(TestContext ctx, Rig rig, CancellationToken ct)
	{
		rig.Session.Bundle.OnUserMessage("Reply with exactly the word: pong");
		ProtocolResult result = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, result.Outcome, $"ClaudeCode PlainText: succeeds ({result.ErrorMessage})");
		ctx.AssertContains((result.Payload?.AssistantText ?? string.Empty).ToLowerInvariant(), "pong", "ClaudeCode PlainText: the answer arrives");
		ReportUsage(ctx, "PlainText", result);
	}

	// One call, held over MCP until Beast's dispatch commits its result, then the model's answer.
	private static async Task TestToolRoundTripAsync(TestContext ctx, Rig rig, CancellationToken ct)
	{
		rig.Session.Bundle.OnUserMessage("Call the echo tool with the text 'zebra', then tell me exactly what it returned.");
		ProtocolResult call = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, call.Outcome, $"ClaudeCode ToolRoundTrip: the call turn succeeds ({call.ErrorMessage})");
		ctx.Assert(call.Payload != null && call.Payload.ToolCalls.Count == 1 && call.Payload.ToolCalls[0].Name == "echo",
			"ClaudeCode ToolRoundTrip: one echo call, named without the MCP prefix");
		ReportUsage(ctx, "ToolRoundTrip call", call);

		ProtocolResult answer = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, answer.Outcome, $"ClaudeCode ToolRoundTrip: the answer turn succeeds ({answer.ErrorMessage})");
		ctx.AssertContains(answer.Payload?.AssistantText ?? string.Empty, "echo: zebra", "ClaudeCode ToolRoundTrip: the model saw Beast's result");
		ReportUsage(ctx, "ToolRoundTrip answer", answer);
	}

	// Two calls in one turn: each held request is matched to its own call and answered.
	private static async Task TestTwoCallsAsync(TestContext ctx, Rig rig, CancellationToken ct)
	{
		rig.Session.Bundle.OnUserMessage("In a single response, call the add tool twice: once with a=2 b=3 and once with a=10 b=20. After both return, state the two sums.");
		ProtocolResult result = await StepAsync(rig, ct);
		int            calls  = result.Payload?.ToolCalls.Count ?? 0;
		int            turns  = 1;
		while (result.Outcome == ProtocolCallOutcome.Success && result.Payload!.ToolCalls.Count > 0 && turns < 4)
		{
			result = await StepAsync(rig, ct);
			calls += result.Payload?.ToolCalls.Count ?? 0;
			turns++;
		}
		ctx.AssertEqual(ProtocolCallOutcome.Success, result.Outcome, $"ClaudeCode TwoCalls: succeeds ({result.ErrorMessage})");
		ctx.AssertEqual(2, calls, "ClaudeCode TwoCalls: two add calls were made");
		string text = result.Payload?.AssistantText ?? string.Empty;
		ctx.Assert(text.Contains('5') && text.Contains("30"), $"ClaudeCode TwoCalls: both sums reach the answer ({text})");
	}

	// A new protocol instance has no process, so it seeds one from canonical as a transcript.
	private static async Task TestReseedAsync(TestContext ctx, Rig rig, string token, CancellationToken ct)
	{
		rig.Session.InvalidateProtocol();
		rig.Session.Bundle.OnUserMessage("What exact text did the echo tool return earlier in this conversation?");
		ProtocolResult result = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, result.Outcome, $"ClaudeCode Reseed: succeeds ({result.ErrorMessage})");
		ctx.AssertContains(result.Payload?.AssistantText ?? string.Empty, "zebra", "ClaudeCode Reseed: the history carried over");
		ReportUsage(ctx, "Reseed", result);
	}

	private static void ReportUsage(TestContext ctx, string label, ProtocolResult result)
	{
		TokenUsageInfo? usage = result.Payload?.Usage;
		ctx.Log($"    {label}: prompt {usage?.PromptTokens ?? 0} (cached {usage?.CachedTokens ?? 0}), completion {usage?.CompletionTokens ?? 0}");
		ctx.Assert(usage != null && usage.PromptTokens > 0 && usage.CompletionTokens > 0, $"ClaudeCode {label}: usage comes from the model's own counters");
	}

	private static Rig Build(string token)
	{
		ModelConfig config = new ModelConfig
		{
			Id            = kModelId,
			Name          = "Claude Haiku (subscription)",
			ContextWindow = 200000,
			Input         = new List<string> { "text", "image" }
		};
		LlmModel   model   = new LlmModel(config.Id, ClaudeCode.EndpointUrl, token, new List<JsonObject>(), new List<JsonObject>(), config);
		LlmService service = new LlmService(model, DetectedProtocol.ClaudeDashP, new ModelAvailability(), new EndpointFacts(), new List<string> { config.Id }, (id, effort, summaries, maxImages) => { });

		TestCaptureTransport transport = new TestCaptureTransport();
		BeastSession         data      = new BeastSession(Guid.NewGuid().ToString("N"), "claude-code-live", config.Id, "Tester",
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a test model. Use the tools exactly as asked and keep answers short.", transport, false);
		session.UpdateModel(model);

		return new Rig(session, service, new Tool[] { EchoTool(), AddTool() }, transport);
	}

	// One production turn: the call, the commit, and the tool round it asked for.
	private static async Task<ProtocolResult> StepAsync(Rig rig, CancellationToken ct)
	{
		ProtocolResult result = await rig.Service.RunToCompletionAsync(rig.Session, rig.Tools, null, 0, 0, false, rig.Transport, ct);
		if (result.Outcome == ProtocolCallOutcome.Success)
		{
			rig.Session.CommitAssistantTurn(result.Payload!);
			bool hasCalls = await ToolDispatch.DispatchAsync(result.Payload!, rig.Tools, rig.Session, rig.Transport, ct);
			if (hasCalls)
				rig.Session.CommitToolResults(result.Payload!);
		}
		return result;
	}

	private static Tool EchoTool()
	{
		JsonObject parameters = new JsonObject
		{
			["type"]       = "object",
			["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
			["required"]   = new JsonArray("text")
		};
		return new Tool
		{
			Definition = new ToolDefinition { Function = new FunctionDefinition { Name = "echo", Description = "Echoes text back, prefixed with 'echo: '.", Parameters = parameters } },
			Handler    = (args, id, ct, transport, sessionId, maxOutput) =>
				Task.FromResult(new ToolResult(id, $"echo: {args["text"]?.GetValue<string>() ?? string.Empty}", string.Empty, 0, 3))
		};
	}

	private static Tool AddTool()
	{
		JsonObject parameters = new JsonObject
		{
			["type"]       = "object",
			["properties"] = new JsonObject { ["a"] = new JsonObject { ["type"] = "number" }, ["b"] = new JsonObject { ["type"] = "number" } },
			["required"]   = new JsonArray("a", "b")
		};
		return new Tool
		{
			Definition = new ToolDefinition { Function = new FunctionDefinition { Name = "add", Description = "Adds two numbers.", Parameters = parameters } },
			Handler    = (args, id, ct, transport, sessionId, maxOutput) =>
				Task.FromResult(new ToolResult(id, $"sum: {(args["a"]?.GetValue<double>() ?? 0) + (args["b"]?.GetValue<double>() ?? 0)}", string.Empty, 0, 3))
		};
	}
}