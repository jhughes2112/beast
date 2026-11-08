using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// The same conversations driven through all three wire protocols — Chat Completions, Anthropic
// Messages, OpenAI Responses — against FakeProvider, over real HTTP and real streaming, through the
// production turn loop (LlmService, ToolDispatch, the real inspect_media tool). Every assertion is
// on what the SERVER received, which is the only honest measure of what a model would have seen.
public static class ProtocolConformanceTests
{
	private const string AnsweredMarker = "Media removed from context";

	private sealed class Api
	{
		public string           Name     { get; }
		public DetectedProtocol Protocol { get; }
		public string           Endpoint { get; }

		// Messages and ChatCompletions drop media once answered; Responses leaves its server-side
		// history alone.
		public bool ElidesAnswered => Protocol != DetectedProtocol.Responses;

		public Api(string name, DetectedProtocol protocol, string endpoint)
		{
			Name     = name;
			Protocol = protocol;
			Endpoint = endpoint;
		}
	}

	private sealed class Rig
	{
		public Session              Session   { get; }
		public LlmService           Service   { get; }
		public Tool[]               Tools     { get; }
		public TestCaptureTransport Transport { get; }
		public List<int>            Learned   { get; }

		public Rig(Session session, LlmService service, Tool[] tools, TestCaptureTransport transport, List<int> learned)
		{
			Session   = session;
			Service   = service;
			Tools     = tools;
			Transport = transport;
			Learned   = learned;
		}
	}

	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  ProtocolConformanceTests");

		FakeProvider? server = FakeProvider.StartOnRandomPort();
		if (server == null)
		{
			ctx.Log("    SKIP: no free port for the fake provider");
			return;
		}

		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		Task   serverTask                 = server.RunAsync(cts.Token);
		string folder                     = Path.Combine(Path.GetTempPath(), $"beast-conformance-{Guid.NewGuid():N}");
		Directory.CreateDirectory(folder);

		try
		{
			List<Api> apis = new List<Api>
			{
				new Api("ChatCompletions", DetectedProtocol.ChatCompletions,      server.ChatEndpoint),
				new Api("Messages",              DetectedProtocol.Anthropic,  server.MessagesEndpoint),
				new Api("Responses",             DetectedProtocol.Responses, server.ResponsesEndpoint)
			};

			foreach (Api api in apis)
			{
				ctx.Log($"    {api.Name}");
				await TestPlainTextAsync(ctx, server, api, cts.Token);
				await TestToolRoundTripAsync(ctx, server, api, cts.Token);
				await TestCompactSseAsync(ctx, server, api, cts.Token);
				await TestSeedAsync(ctx, server, api, cts.Token);
				await TestSamplingAsync(ctx, server, api, cts.Token);
				if (api.Protocol == DetectedProtocol.ChatCompletions)
				{
					await TestCutOffTurnAsync(ctx, server, api, cts.Token);
					await TestStreamErrorsAsync(ctx, server, api, cts.Token);
					await TestWorkingTimeAsync(ctx, server, api, cts.Token);
				}
				await TestMediaAcrossToolTurnsAsync(ctx, server, api, folder, cts.Token);
				await TestTwoImagesInOneInspectAsync(ctx, server, api, folder, cts.Token);
				await TestPixelsReachServerAsync(ctx, server, api, folder, cts.Token);
				await TestThinkingAnswersMediaAsync(ctx, server, api, folder, cts.Token);
				await TestDroppedAttachmentAsync(ctx, server, api, cts.Token);
				await TestImageCountRefusalAsync(ctx, server, api, folder, false, cts.Token);
				await TestMediaBytesRefusalAsync(ctx, server, api, folder, cts.Token);
				await TestWrongModelIsNotTrimmedAsync(ctx, server, api, folder, cts.Token);
				if (api.Protocol != DetectedProtocol.Anthropic)
					await TestImageCountRefusalAsync(ctx, server, api, folder, true, cts.Token);
				if (api.Protocol == DetectedProtocol.Responses)
					await TestCountRouteProbedOnceAsync(ctx, server, api, cts.Token);
			}
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
			Directory.Delete(folder, true);
		}
	}

	// ---- Scenarios ----

	// Unset controls never reach the wire, on Messages included, where a current model refuses them with
	// a 400. Set ones reach each API that has the field and stay off the rest.
	private static async Task TestSamplingAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		string[] all = { "temperature", "top_p", "top_k", "min_p", "frequency_penalty", "presence_penalty", "repetition_penalty" };

		Rig unset = Build(server, api);
		unset.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.Say("answer"));
		await StepAsync(unset, ct);
		foreach (string name in all)
			ctx.Assert(server.Requests()[0].Body[name] == null, $"{api.Name} Sampling: unset {name} is not sent");

		ModelConfig config = new ModelConfig
		{
			Temperature       = 0.7m,
			TopP              = 0.9m,
			TopK              = 40,
			MinP              = 0.05m,
			FrequencyPenalty  = 0.1m,
			PresencePenalty   = -0.2m,
			RepetitionPenalty = 1.1m
		};
		Rig set = Build(server, api, null, config);
		set.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.Say("answer"));
		await StepAsync(set, ct);
		JsonNode body = server.Requests()[0].Body;

		ctx.AssertEqual(0.7m, body["temperature"]?.GetValue<decimal>() ?? -9m, $"{api.Name} Sampling: temperature is sent");
		ctx.AssertEqual(0.9m,       body["top_p"]?.GetValue<decimal>() ?? -9m, $"{api.Name} Sampling: top_p is sent");

		bool chat = api.Protocol == DetectedProtocol.ChatCompletions;
		ctx.AssertEqual(api.Protocol != DetectedProtocol.Responses ? 40 : -9, body["top_k"]?.GetValue<int>() ?? -9, $"{api.Name} Sampling: top_k only where the API has it");
		ctx.AssertEqual(chat ? 0.05m : -9m,              body["min_p"]?.GetValue<decimal>() ?? -9m, $"{api.Name} Sampling: min_p only on ChatCompletions");
		ctx.AssertEqual( chat ? 0.1m : -9m,  body["frequency_penalty"]?.GetValue<decimal>() ?? -9m, $"{api.Name} Sampling: frequency_penalty only on ChatCompletions");
		ctx.AssertEqual(chat ? -0.2m : -9m,   body["presence_penalty"]?.GetValue<decimal>() ?? -9m, $"{api.Name} Sampling: presence_penalty only on ChatCompletions");
		ctx.AssertEqual( chat ? 1.1m : -9m, body["repetition_penalty"]?.GetValue<decimal>() ?? -9m, $"{api.Name} Sampling: repetition_penalty only on ChatCompletions");
	}

	// No configured seed sends none. A configured one, zero included, goes out on ChatCompletions, and an
	// empty-turn retry steps it because the same seed would reproduce the same empty turn. The other two
	// APIs have no seed field, so a configured seed must stay off their wire.
	private static async Task TestSeedAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		Rig unset = Build(server, api, null);
		unset.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.Say("answer"));
		await StepAsync(unset, ct);
		ctx.Assert(server.Requests()[0].Body["seed"] == null, $"{api.Name} Seed: blank sends no seed");

		Rig zero = Build(server, api, 0);
		zero.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.Say("answer"));
		await StepAsync(zero, ct);
		JsonNode? zeroSeed = server.Requests()[0].Body["seed"];

		if (api.Protocol == DetectedProtocol.ChatCompletions)
		{
			ctx.AssertEqual(0L, zeroSeed?.GetValue<long>() ?? -1L, $"{api.Name} Seed: zero is a real seed");

			Rig retried = Build(server, api, 41);
			retried.Session.Bundle.OnUserMessage("hello");
			server.Enqueue(FakeReply.Turn("only thought", string.Empty, new List<(string, string, string)>()));
			server.Enqueue(FakeReply.Say("answer"));
			await StepAsync(retried, ct);
			ctx.AssertEqual(41L, server.Requests()[0].Body["seed"]?.GetValue<long>() ?? -1L, $"{api.Name} Seed: the configured value is sent");
			ctx.AssertEqual(42L, server.Requests()[1].Body["seed"]?.GetValue<long>() ?? -1L, $"{api.Name} Seed: an empty-turn retry steps it");

			// Each assistant turn already in the conversation steps it too, so no two turns share their random draws.
			retried.Session.Bundle.OnUserMessage("again");
			server.Enqueue(FakeReply.Say("second answer"));
			await StepAsync(retried, ct);
			ctx.AssertEqual(42L, server.Requests()[2].Body["seed"]?.GetValue<long>() ?? -1L, $"{api.Name} Seed: the next turn adds one per assistant turn before it");
			retried.Session.Bundle.OnUserMessage("and again");
			server.Enqueue(FakeReply.Say("third answer"));
			await StepAsync(retried, ct);
			ctx.AssertEqual(43L, server.Requests()[3].Body["seed"]?.GetValue<long>() ?? -1L, $"{api.Name} Seed: and keeps moving on");
		}
		else
		{
			ctx.Assert(zeroSeed == null, $"{api.Name} Seed: never sent to an API without the field");
		}
	}

	// The Responses token-count route is probed once per ENDPOINT, not once per protocol instance —
	// every session, rehydrate and summarizer stage builds a fresh one. On a Meta-style server the
	// first count pays one 404 on OpenAI's spelling and every later count goes straight to Meta's;
	// on a server with neither, later counts skip both and go to the tracer.
	private static async Task TestCountRouteProbedOnceAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		ModelConfig   config = new ModelConfig { Id = "fake-count", Name = "Fake count", ContextWindow = 200000, MaxOutputTokens = 4096 };
		LlmModel      model  = new LlmModel(config.Id, api.Endpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);
		SessionLogger logger = new SessionLogger("count-route-test");

		server.Reset();
		server.ServeCount("/responses/input_tokens");
		EndpointFacts meta = new EndpointFacts();
		for (int instance = 0; instance < 3; instance++)
		{
			TracerResult counted = await CountOnFreshInstanceAsync(model, meta, logger, ct);
			ctx.Assert(counted.Succeeded && counted.InputTokens == 321, $"{api.Name} CountRoute: instance {instance} counts through Meta's route");
		}
		List<string> metaPaths = server.CountRequests();
		ctx.AssertEqual(4, metaPaths.Count, $"{api.Name} CountRoute: one 404 on OpenAI's route, then one count per instance");
		if (metaPaths.Count == 4)
			ctx.Assert(metaPaths[0].EndsWith("/input_tokens/count", StringComparison.Ordinal), $"{api.Name} CountRoute: OpenAI's spelling is tried first");

		server.Reset();
		EndpointFacts none = new EndpointFacts();
		for (int instance = 0; instance < 3; instance++)
			await CountOnFreshInstanceAsync(model, none, logger, ct);
		ctx.AssertEqual(2, server.CountRequests().Count, $"{api.Name} CountRoute: an endpoint with no count route is probed once, then skipped");
	}

	private static async Task<TracerResult> CountOnFreshInstanceAsync(LlmModel model, EndpointFacts facts, SessionLogger logger, CancellationToken ct)
	{
		ProtocolResponses protocol = new ProtocolResponses(null, facts, false);
		protocol.Rehydrate(new List<CanonicalMessage> { new UserMessage("count me") });
		return await protocol.CountTokensAsync(model, new List<ToolDefinition>(), null, new Dictionary<string, string>(), new Dictionary<string, JsonNode?>(), logger, ct);
	}

	private static async Task TestPlainTextAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		Rig rig = Build(server, api);
		server.Enqueue(FakeReply.Turn("pondering", "hello back", new List<(string, string, string)>()));
		rig.Session.Bundle.OnUserMessage("hello");

		ProtocolResult result = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, result.Outcome, $"{api.Name} PlainText: succeeds");
		ctx.AssertEqual("hello back", result.Payload?.AssistantText ?? string.Empty, $"{api.Name} PlainText: text arrives");
		ctx.AssertEqual("pondering",       result.Payload?.Thinking ?? string.Empty, $"{api.Name} PlainText: thinking arrives");
		ctx.Assert(server.Requests()[0].Streaming, $"{api.Name} PlainText: the request streams");
		ctx.Assert(server.Requests()[0].Body.ToJsonString().Contains("hello", StringComparison.Ordinal), $"{api.Name} PlainText: the user text reaches the server");
	}

	private static async Task TestToolRoundTripAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		Rig rig = Build(server, api);
		server.Enqueue(FakeReply.Call("call_echo", "echo", "{\"text\":\"ping\"}"));
		server.Enqueue(FakeReply.Say("done"));
		rig.Session.Bundle.OnUserMessage("run echo");

		await StepAsync(rig, ct);
		ProtocolResult final = await StepAsync(rig, ct);
		ctx.AssertEqual("done", final.Payload?.AssistantText ?? string.Empty, $"{api.Name} ToolRoundTrip: the model's answer after the tool arrives");

		string second = server.Requests()[1].Body.ToJsonString();
		ctx.Assert(second.Contains("echo: ping", StringComparison.Ordinal), $"{api.Name} ToolRoundTrip: the tool output is sent back");
		ctx.Assert( second.Contains("call_echo", StringComparison.Ordinal), $"{api.Name} ToolRoundTrip: paired with the call id");
	}

	// The output limit ending a turn near the compaction line means compact, once, with nothing kept:
	// not three retries of a request that can only be cut off the same way, and not a half-written call
	// committed. The field session lost 12 minutes to the retries. Well below the line nothing changes:
	// a model that ran into a small limit keeps its turn.
	private static async Task TestCutOffTurnAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		const int Window = 200000;

		Rig rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("write the file");
		rig.Session.RecordCountedContext(Window * 8 / 10, 0);
		int committed = rig.Session.Data.Messages.Count;
		server.Enqueue(FakeReply.Turn("thinking only", string.Empty, new List<(string, string, string)>()).WithFinish("length"));
		ProtocolResult thinking = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.ContextFull, thinking.Outcome, $"{api.Name} CutOff: reasoning cut off near the line asks for compaction");
		ctx.AssertEqual(        1,         server.Requests().Count, $"{api.Name} CutOff: and is not retried");
		ctx.AssertEqual(committed, rig.Session.Data.Messages.Count, $"{api.Name} CutOff: and is not committed");

		rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("write the file");
		rig.Session.RecordCountedContext(Window * 8 / 10, 0);
		committed = rig.Session.Data.Messages.Count;
		server.Enqueue(FakeReply.Turn(string.Empty, string.Empty, new List<(string, string, string)> { ("call_cut", "echo", "{\"text\":\"unfini") }).WithFinish("length"));
		ProtocolResult call = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.ContextFull, call.Outcome, $"{api.Name} CutOff: a half-written tool call asks for compaction");
		ctx.AssertEqual(        1,         server.Requests().Count, $"{api.Name} CutOff: without a repair retry");
		ctx.AssertEqual(committed, rig.Session.Data.Messages.Count, $"{api.Name} CutOff: and never enters the history");

		// An empty turn that was NOT cut off is still retried.
		rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.Turn("only thought", string.Empty, new List<(string, string, string)>()));
		server.Enqueue(FakeReply.Say("answer"));
		ProtocolResult retried = await StepAsync(rig, ct);
		ctx.AssertEqual("answer", retried.Payload?.AssistantText ?? string.Empty, $"{api.Name} CutOff: an empty turn that finished normally is still retried");

		// Unmeasured, the ask is only the model's 4096 allowance: nowhere near the line.
		rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.Turn(string.Empty, "partial answer", new List<(string, string, string)>()).WithFinish("length"));
		ProtocolResult small = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, small.Outcome, $"{api.Name} CutOff: a cut-off far below the line commits as before");
		ctx.AssertEqual("length", small.Payload?.FinishReason ?? string.Empty, $"{api.Name} CutOff: reporting why it ended");
	}

	// "data:{...}" with no space after the colon is legal SSE, and uzu streams exactly that. A reader
	// that matched only "data: " dropped every line, saw an empty turn, and retried it forever.
	// Once a stream has started, its 200 can't change, so an error has to travel inside it. The peachy gateway
	// sends one after keep-alives when a queued model won't load or refuses the request. Before this was handled
	// the error was skipped as a chunk with no choices and the turn looked empty.
	private static async Task TestWorkingTimeAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		// A tool calling a model is waiting on it: its clock stops, and the model's time stays on the conversation.
		ToolClock.Current = new ToolClock();
		await Task.Delay(100, ct);
		Rig inTool = Build(server, api);
		inTool.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.Say("answer").WithQueue(1000));
		await StepAsync(inTool, ct);
		long toolWorked   = ToolClock.Current.WorkedMs;
		ToolClock.Current = null;
		ctx.Assert(toolWorked >= 50 && toolWorked < 700, $"{api.Name} WorkingTime: a tool's clock stops while it waits on a model (got {toolWorked} ms)");

		// The queue is not work, with or without keep-alive comments holding the connection open.
		foreach (bool keepAlive in new[] { false, true })
		{
			Rig queued = Build(server, api);
			queued.Session.Bundle.OnUserMessage("hello");
			FakeReply reply = FakeReply.Say("answer").WithQueue(1500);
			server.Enqueue(keepAlive ? reply.WithKeepAlive() : reply);
			ProtocolResult waited = await StepAsync(queued, ct);
			ctx.AssertEqual("answer", waited.Payload?.AssistantText ?? string.Empty, $"{api.Name} WorkingTime: the queued call answers");
			ctx.Assert(queued.Session.WorkedMs < 750, $"{api.Name} WorkingTime: 1500 ms in the gateway's queue is not counted (keep-alive {keepAlive}, got {queued.Session.WorkedMs} ms)");
		}

		// Receiving the response is.
		Rig generating = Build(server, api);
		generating.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.Say("answer").WithQueue(500).WithGenerate(800));
		await StepAsync(generating, ct);
		ctx.Assert(generating.Session.WorkedMs >= 700 && generating.Session.WorkedMs < 1250, $"{api.Name} WorkingTime: the 800 ms of receiving is counted and the 500 ms before it is not (got {generating.Session.WorkedMs} ms)");
	}

	private static async Task TestStreamErrorsAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		Rig rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.Say("answer").WithKeepAlive());
		ProtocolResult kept = await StepAsync(rig, ct);
		ctx.AssertEqual("answer", kept.Payload?.AssistantText ?? string.Empty, $"{api.Name} StreamError: keep-alive comments are skipped");

		// A server error is retried, and the retry's answer is the turn.
		rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.StreamError("{\"type\":\"error\",\"error\":{\"message\":\"the engine exited during startup\",\"type\":\"server_error\",\"code\":502}}"));
		server.Enqueue(FakeReply.Say("recovered"));
		ProtocolResult retried = await StepAsync(rig, ct);
		ctx.AssertEqual("recovered", retried.Payload?.AssistantText ?? string.Empty, $"{api.Name} StreamError: a 5xx in the stream is retried");
		ctx.AssertEqual(2, server.Requests().Count, $"{api.Name} StreamError: once");

		// A refusal is asked again without streaming, whose real status the plain path classifies. Streaming stays on.
		rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.StreamError("{\"error\":{\"message\":\"bad request\",\"code\":400}}"));
		server.Enqueue(FakeReply.Say("plain answer"));
		ProtocolResult plain = await StepAsync(rig, ct);
		ctx.AssertEqual("plain answer", plain.Payload?.AssistantText ?? string.Empty, $"{api.Name} StreamError: a 4xx in the stream re-sends");
		ctx.Assert(!server.Requests()[1].Streaming, $"{api.Name} StreamError: without streaming");
		rig.Session.Bundle.OnUserMessage("again");
		server.Enqueue(FakeReply.Say("streamed"));
		await StepAsync(rig, ct);
		ctx.Assert(server.Requests()[2].Streaming, $"{api.Name} StreamError: and the next turn streams again");

		// A refusal of the plain request then fails exactly as it would have without the wait.
		rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("hello");
		string overflow = "{\"error\":{\"message\":\"This model's maximum context length is 131072 tokens.\",\"code\":400}}";
		server.Enqueue(FakeReply.StreamError(overflow));
		server.Enqueue(FakeReply.Error(400, overflow));
		ProtocolResult full = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.ContextFull, full.Outcome, $"{api.Name} StreamError: a 400 overflow in the stream reaches compaction");

		// With no status to go on, the words still say overflow.
		rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.StreamError("{\"error\":{\"message\":\"This model's maximum context length is 131072 tokens.\"}}"));
		ProtocolResult bare = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.ContextFull, bare.Outcome, $"{api.Name} StreamError: an overflow with no status");

		// A bare string is an error too, and must not throw on the way to being read.
		rig = Build(server, api);
		rig.Session.Bundle.OnUserMessage("hello");
		server.Enqueue(FakeReply.StreamError("{\"error\":\"boom\"}"));
		server.Enqueue(FakeReply.Say("after a string"));
		ProtocolResult text = await StepAsync(rig, ct);
		ctx.AssertEqual("after a string", text.Payload?.AssistantText ?? string.Empty, $"{api.Name} StreamError: an error that's only a string is retried");
	}

	private static async Task TestCompactSseAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		Rig rig = Build(server, api);
		server.Enqueue(FakeReply.Call("call_echo", "echo", "{\"text\":\"ping\"}").WithCompactSse());
		server.Enqueue(FakeReply.Turn("pondering", "done", new List<(string, string, string)>()).WithCompactSse());
		rig.Session.Bundle.OnUserMessage("run echo");

		await StepAsync(rig, ct);
		ProtocolResult final = await StepAsync(rig, ct);
		ctx.AssertEqual(2, server.Requests().Count, $"{api.Name} CompactSse: one request per turn, no retries");
		ctx.Assert(server.Requests()[1].Body.ToJsonString().Contains("echo: ping", StringComparison.Ordinal), $"{api.Name} CompactSse: the tool call was read and run");
		ctx.AssertEqual("done",      final.Payload?.AssistantText ?? string.Empty, $"{api.Name} CompactSse: text arrives");
		ctx.AssertEqual("pondering",      final.Payload?.Thinking ?? string.Empty, $"{api.Name} CompactSse: thinking arrives");
	}

	// Several inspects in a row with nothing said between them keep every image in view, and they stay
	// in view after the model answers: they are only replaced once newer media arrives.
	private static async Task TestMediaAcrossToolTurnsAsync(TestContext ctx, FakeProvider server, Api api, string folder, CancellationToken ct)
	{
		Rig    rig = Build(server, api);
		string a   = Png(folder, "a.png", 64);
		string b   = Png(folder, "b.png", 64);
		server.Enqueue(FakeReply.Call("c1", "inspect_media", Args(a)));
		server.Enqueue(FakeReply.Call("c2", "inspect_media", Args(b)));
		server.Enqueue(FakeReply.Say("both look fine"));
		server.Enqueue(FakeReply.Say("you're welcome"));
		rig.Session.Bundle.OnUserMessage("look at a and b");

		await StepAsync(rig, ct);
		await StepAsync(rig, ct);
		await StepAsync(rig, ct);
		rig.Session.Bundle.OnUserMessage("thanks");
		await StepAsync(rig, ct);

		List<FakeRequest> sent = server.Requests();
		ctx.AssertEqual(4,                sent.Count, $"{api.Name} MediaAcrossTools: one request per turn");
		ctx.AssertEqual(1, sent[1].VisibleMediaCount, $"{api.Name} MediaAcrossTools: the first image is sent after its inspect");
		ctx.AssertEqual(2, sent[2].VisibleMediaCount, $"{api.Name} MediaAcrossTools: a tool-only turn keeps it, so both are in view");
		ctx.AssertEqual(2, sent[3].VisibleMediaCount, $"{api.Name} MediaAcrossTools: answered but not superseded, both stay in view");
		ctx.Assert(!sent[3].Body.ToJsonString().Contains(AnsweredMarker, StringComparison.Ordinal), $"{api.Name} MediaAcrossTools: no removal note until newer media arrives");
		if (!api.ElidesAnswered)
			ctx.AssertEqual(1, sent[2].MediaCount, $"{api.Name} MediaAcrossTools: chaining sends only the new image");
	}

	// One inspect naming two files puts both in front of the model in the same request, in the order
	// asked, so it can compare them directly.
	private static async Task TestTwoImagesInOneInspectAsync(TestContext ctx, FakeProvider server, Api api, string folder, CancellationToken ct)
	{
		// Each filled with its own letter, so its base64 holds a run ("QUFB" for 'A', "QkJC" for 'B')
		// that identifies it in the request without any character JSON would escape.
		Rig    rig = Build(server, api);
		string a   = Path.Combine(folder, "before.png");
		string b   = Path.Combine(folder, "after.png");
		File.WriteAllBytes(a, LetterPng((byte)'A'));
		File.WriteAllBytes(b, LetterPng((byte)'B'));
		string args = new JsonObject { ["file_paths"] = new JsonArray(a, b) }.ToJsonString();
		server.Enqueue(FakeReply.Call("c1", "inspect_media", args));
		server.Enqueue(FakeReply.Say("after.png moved the button"));
		rig.Session.Bundle.OnUserMessage("compare before and after");

		await StepAsync(rig, ct);
		await StepAsync(rig, ct);

		List<FakeRequest> sent = server.Requests();
		ctx.AssertEqual(2,         sent.Count, $"{api.Name} TwoImages: one inspect, then the answer");
		ctx.AssertEqual(2, sent[1].MediaCount, $"{api.Name} TwoImages: both images ride in the same request");
		string body   = sent[1].Body.ToJsonString();
		int    before = body.IndexOf("QUFBQUFBQUFB", StringComparison.Ordinal);
		int    after  = body.IndexOf("QkJCQkJCQkJC", StringComparison.Ordinal);
		ctx.Assert(before >= 0 && after > before, $"{api.Name} TwoImages: in the order they were asked for");
	}

	// Three real PNGs, each a solid background with a 4x4 patch of another color in the top-left corner,
	// sent once as an array and once one call at a time. The images the SERVER received are decoded and
	// every pixel is checked: each must hold exactly its own two colors (nothing overlaid on it) and
	// arrive in the order asked.
	private static async Task TestPixelsReachServerAsync(TestContext ctx, FakeProvider server, Api api, string folder, CancellationToken ct)
	{
		(byte R, byte G, byte B)[] backgrounds = { (220, 30, 30), (30, 200, 40), (30, 60, 230) };
		(byte R, byte G, byte B)[] patches     = { (255, 255, 0), (255, 0, 255), (0, 255, 255) };
		string[] paths                         = new string[3];
		for (int i = 0; i < 3; i++)
		{
			paths[i] = Path.Combine(folder, $"pixels{i}.png");
			File.WriteAllBytes(paths[i], SolidPng(24, 16, backgrounds[i], patches[i]));
		}

		Rig rig = Build(server, api);
		server.Enqueue(FakeReply.Call("c1", "inspect_media", new JsonObject { ["file_paths"] = new JsonArray(paths[0], paths[1], paths[2]) }.ToJsonString()));
		server.Enqueue(FakeReply.Say("seen"));
		rig.Session.Bundle.OnUserMessage("look at all three");
		await StepAsync(rig, ct);
		await StepAsync(rig, ct);
		CheckPixels(ctx, $"{api.Name} Pixels(array)", WireImages(server.Requests()), paths, backgrounds, patches);

		rig = Build(server, api);
		for (int i = 0; i < 3; i++)
			server.Enqueue(FakeReply.Call($"s{i}", "inspect_media", Args(paths[i])));
		server.Enqueue(FakeReply.Say("seen"));
		rig.Session.Bundle.OnUserMessage("look at them one by one");
		for (int i = 0; i < 4; i++)
			await StepAsync(rig, ct);
		CheckPixels(ctx, $"{api.Name} Pixels(solo)", WireImages(server.Requests()), paths, backgrounds, patches);
	}

	private static void CheckPixels(TestContext ctx, string label, List<byte[]> received, string[] paths, (byte R, byte G, byte B)[] backgrounds, (byte R, byte G, byte B)[] patches)
	{
		ctx.AssertEqual(3, received.Count, $"{label}: three distinct images reach the server");
		for (int i = 0; i < received.Count && i < 3; i++)
		{
			ctx.Assert(received[i].AsSpan().SequenceEqual(File.ReadAllBytes(paths[i])), $"{label}: image {i + 1} is byte-for-byte the file, in the order asked");

			(int width, int height, byte[] rgb) = DecodePng(received[i]);
			ctx.AssertEqual(24,  width, $"{label}: image {i + 1} width");
			ctx.AssertEqual(16, height, $"{label}: image {i + 1} height");

			HashSet<int> colors = new HashSet<int>();
			for (int p = 0; p + 2 < rgb.Length; p += 3)
				colors.Add((rgb[p] << 16) | (rgb[p + 1] << 8) | rgb[p + 2]);
			ctx.AssertEqual(2, colors.Count, $"{label}: image {i + 1} holds only its background and patch colors");
			ctx.AssertEqual(    Pack(patches[i]),   PixelAt(rgb, width, 0, 0), $"{label}: image {i + 1} patch pixel");
			ctx.AssertEqual(Pack(backgrounds[i]),  PixelAt(rgb, width, 12, 8), $"{label}: image {i + 1} background pixel");
			ctx.AssertEqual(Pack(backgrounds[i]), PixelAt(rgb, width, 23, 15), $"{label}: image {i + 1} far corner");
		}
	}

	// Distinct images found in the requests, in first-appearance order, base64 decoded. A thread that
	// resends earlier images (or one that chains and sends only the new one) yields the same list.
	private static List<byte[]> WireImages(List<FakeRequest> requests)
	{
		List<string> seen = new List<string>();
		foreach (FakeRequest request in requests)
			CollectImageData(request.Body["messages"] ?? request.Body["input"], seen);

		List<byte[]> images = new List<byte[]>();
		foreach (string data in seen)
			images.Add(Convert.FromBase64String(data));
		return images;
	}

	private static void CollectImageData(JsonNode? node, List<string> seen)
	{
		if (node is JsonArray array)
		{
			foreach (JsonNode? child in array)
				CollectImageData(child, seen);
		}
		else if (node is JsonObject obj)
		{
			string  type = obj["type"] is JsonValue v && v.TryGetValue<string>(out string? s) ? s : string.Empty;
			string? data = null;
			if (type == "image")
				data = obj["source"]?["data"]?.GetValue<string>();
			else if (type == "image_url")
				data = obj["image_url"] is JsonObject inner ? inner["url"]?.GetValue<string>() : obj["image_url"]?.GetValue<string>();
			else if (type == "input_image")
				data = obj["image_url"]?.GetValue<string>();

			if (data != null)
			{
				int    comma   = data.IndexOf("base64,", StringComparison.Ordinal);
				string payload = comma >= 0 ? data.Substring(comma + "base64,".Length) : data;
				if (!seen.Contains(payload))
					seen.Add(payload);
			}
			else
			{
				foreach ((string _, JsonNode? child) in obj)
					CollectImageData(child, seen);
			}
		}
	}

	private static int Pack((byte R, byte G, byte B) color)
	{
		return (color.R << 16) | (color.G << 8) | color.B;
	}

	private static int PixelAt(byte[] rgb, int width, int x, int y)
	{
		int p = (y * width + x) * 3;
		return (rgb[p] << 16) | (rgb[p + 1] << 8) | rgb[p + 2];
	}

	// An RGB PNG, unfiltered rows: a solid background with a 4x4 patch in the top-left corner.
	private static byte[] SolidPng(int width, int height, (byte R, byte G, byte B) background, (byte R, byte G, byte B) patch)
	{
		byte[] raw = new byte[height * (1 + width * 3)];
		for (int y = 0; y < height; y++)
		{
			int row = y * (1 + width * 3);
			for (int x = 0; x < width; x++)
			{
				(byte r, byte g, byte b) = x < 4 && y < 4 ? patch : background;
				raw[row + 1 + x * 3]     = r;
				raw[row + 2 + x * 3]     = g;
				raw[row + 3 + x * 3]     = b;
			}
		}

		byte[] header = new byte[13];
		WriteBigEndian(header, 0,  width);
		WriteBigEndian(header, 4, height);
		header[8] = 8;
		header[9] = 2;

		byte[] compressed;
		using (MemoryStream packed = new MemoryStream())
		{
			using (ZLibStream zlib = new ZLibStream(packed, CompressionLevel.Optimal, true))
				zlib.Write(raw, 0, raw.Length);
			compressed = packed.ToArray();
		}

		using MemoryStream png = new MemoryStream();
		png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
		WriteChunk(png, "IHDR",              header);
		WriteChunk(png, "IDAT",          compressed);
		WriteChunk(png, "IEND", Array.Empty<byte>());
		return png.ToArray();
	}

	private static void WriteChunk(MemoryStream png, string type, byte[] data)
	{
		byte[] typed = new byte[4 + data.Length];
		for (int i = 0; i < 4; i++)
			typed[i] = (byte)type[i];
		Array.Copy(data, 0, typed, 4, data.Length);

		uint crc = 0xFFFFFFFF;
		foreach (byte b in typed)
		{
			crc ^= b;
			for (int bit = 0; bit < 8; bit++)
				crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
		}
		crc ^= 0xFFFFFFFF;

		byte[] length = new byte[4];
		WriteBigEndian(length, 0, data.Length);
		byte[] check = new byte[4];
		WriteBigEndian(check, 0, (int)crc);
		png.Write(length);
		png.Write(typed);
		png.Write(check);
	}

	private static void WriteBigEndian(byte[] target, int offset, int value)
	{
		target[offset]     = (byte)(value >> 24);
		target[offset + 1] = (byte)(value >> 16);
		target[offset + 2] = (byte)(value >> 8);
		target[offset + 3] = (byte)value;
	}

	// Width, height and RGB bytes of a PNG made by SolidPng (8-bit RGB, no row filtering).
	private static (int Width, int Height, byte[] Rgb) DecodePng(byte[] png)
	{
		int          width  = 0;
		int          height = 0;
		MemoryStream idat   = new MemoryStream();
		int          at     = 8;
		while (at + 8 <= png.Length)
		{
			int    length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
			string type   = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
			if (type == "IHDR")
			{
				width  = (png[at + 8] << 24) | (png[at + 9] << 16) | (png[at + 10] << 8) | png[at + 11];
				height = (png[at + 12] << 24) | (png[at + 13] << 16) | (png[at + 14] << 8) | png[at + 15];
			}
			else if (type == "IDAT")
			{
				idat.Write(png, at + 8, length);
			}
			at += 12 + length;
		}

		idat.Position          = 0;
		using MemoryStream raw = new MemoryStream();
		using (ZLibStream zlib = new ZLibStream(idat, CompressionMode.Decompress))
			zlib.CopyTo(raw);
		byte[] rows = raw.ToArray();

		byte[] rgb = new byte[width * height * 3];
		for (int y = 0; y < height; y++)
			Array.Copy(rows, y * (1 + width * 3) + 1, rgb, y * width * 3, width * 3);
		return (width, height, rgb);
	}

	// A PNG signature followed by one repeated letter.
	private static byte[] LetterPng(byte letter)
	{
		byte[] bytes = new byte[128];
		byte[] magic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
		Array.Copy(magic, bytes, magic.Length);
		for (int i = magic.Length; i < bytes.Length; i++)
			bytes[i] = letter;
		return bytes;
	}

	// Thinking is an answer where it survives on the wire (Messages replays signed thinking), but an
	// answer alone no longer removes the newest media: with nothing newer, the image stays everywhere.
	private static async Task TestThinkingAnswersMediaAsync(TestContext ctx, FakeProvider server, Api api, string folder, CancellationToken ct)
	{
		Rig    rig = Build(server, api);
		string a   = Png(folder, "t.png", 64);
		server.Enqueue(FakeReply.Call("c1", "inspect_media", Args(a)));
		server.Enqueue(FakeReply.Turn("the button is misaligned", string.Empty, new List<(string, string, string)> { ("c2", "echo", "{\"text\":\"x\"}") }));
		server.Enqueue(FakeReply.Say("fixed"));
		rig.Session.Bundle.OnUserMessage("check t.png");

		await StepAsync(rig, ct);
		await StepAsync(rig, ct);
		await StepAsync(rig, ct);

		FakeRequest third = server.Requests()[2];
		ctx.AssertEqual(1, third.VisibleMediaCount, $"{api.Name} ThinkingAnswers: media after a thinking turn stays until newer media arrives");
	}

	private static async Task TestDroppedAttachmentAsync(TestContext ctx, FakeProvider server, Api api, CancellationToken ct)
	{
		Rig rig = Build(server, api);
		server.Enqueue(FakeReply.Say("a cat"));
		server.Enqueue(FakeReply.Say("sure"));
		rig.Session.Bundle.OnUserMessage("what is this?", new List<MediaAttachment> { new MediaAttachment("image/png", Convert.ToBase64String(PngBytes(64))) });
		await StepAsync(rig, ct);
		rig.Session.Bundle.OnUserMessage("thanks");
		await StepAsync(rig, ct);

		List<FakeRequest> sent = server.Requests();
		ctx.AssertEqual(1,        sent[0].MediaCount, $"{api.Name} DroppedAttachment: sent with the question");
		ctx.AssertEqual(1, sent[1].VisibleMediaCount, $"{api.Name} DroppedAttachment: after the answer, still in view");
	}

	// A provider that takes at most two images refuses the third; the oldest goes and the request is
	// re-sent — still streaming — and the stated cap is persisted. Responses only ever sends the new
	// item while chaining, so it never reaches the limit at all.
	private static async Task TestImageCountRefusalAsync(TestContext ctx, FakeProvider server, Api api, string folder, bool noStreaming, CancellationToken ct)
	{
		string label = noStreaming ? "ImageCountRefusal(no streaming)" : "ImageCountRefusal";
		Rig    rig   = Build(server, api);
		server.Limit(2, 0, noStreaming);
		for (int i = 0; i < 3; i++)
			server.Enqueue(FakeReply.Call($"c{i}", "inspect_media", Args(Png(folder, $"n{i}.png", 64))));
		server.Enqueue(FakeReply.Say("saw them"));
		rig.Session.Bundle.OnUserMessage("look at all three");

		ProtocolResult last = ProtocolResult.Failed("not run");
		for (int i = 0; i < 4; i++)
			last = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, last.Outcome, $"{api.Name} {label}: the turn completes");
		ctx.AssertEqual("saw them", last.Payload?.AssistantText ?? string.Empty, $"{api.Name} {label}: with the model's answer");

		List<FakeRequest> sent    = server.Requests();
		FakeRequest?      refused = FirstMediaRefusal(sent, 400);
		if (api.ElidesAnswered)
		{
			ctx.AssertEqual(3, refused?.MediaCount ?? 0, $"{api.Name} {label}: three images were refused");
			FakeRequest retried = sent[sent.Count - 1];
			ctx.AssertEqual(           2, retried.MediaCount, $"{api.Name} {label}: re-sent with the newest two");
			ctx.AssertEqual(!noStreaming,  retried.Streaming, $"{api.Name} {label}: a media refusal leaves streaming as it was");
			ctx.Assert(rig.Learned.Contains(2), $"{api.Name} {label}: the stated cap is persisted");
		}
		else
		{
			ctx.Assert(refused == null, $"{api.Name} {label}: chaining never sends more than the new image, so nothing is refused");
		}
	}

	// OpenRouter's byte cap: nothing states a count, so items go oldest-first until it fits, and the
	// derived cap is not written to settings.
	private static async Task TestMediaBytesRefusalAsync(TestContext ctx, FakeProvider server, Api api, string folder, CancellationToken ct)
	{
		Rig rig = Build(server, api);
		server.Limit(0, 700 * 1024, false);
		for (int i = 0; i < 3; i++)
			server.Enqueue(FakeReply.Call($"c{i}", "inspect_media", Args(Png(folder, $"big{i}.png", 300 * 1024))));
		server.Enqueue(FakeReply.Say("saw them"));
		rig.Session.Bundle.OnUserMessage("look at the big ones");

		ProtocolResult last = ProtocolResult.Failed("not run");
		for (int i = 0; i < 4; i++)
			last = await StepAsync(rig, ct);
		ctx.AssertEqual(ProtocolCallOutcome.Success, last.Outcome, $"{api.Name} BytesRefusal: the turn completes");

		List<FakeRequest> sent = server.Requests();
		if (api.ElidesAnswered)
		{
			ctx.AssertEqual(3, FirstMediaRefusal(sent, 413)?.MediaCount ?? 0, $"{api.Name} BytesRefusal: 900KB of images was refused");
			ctx.AssertEqual(2,               sent[sent.Count - 1].MediaCount, $"{api.Name} BytesRefusal: re-sent without the oldest");
		}
		ctx.AssertEqual(0, rig.Learned.Count, $"{api.Name} BytesRefusal: a derived cap is not persisted");
	}

	// "No endpoints found that support image input" means the wrong model, not too much media: the
	// request is not trimmed and retried, it fails for the human to decide.
	private static async Task TestWrongModelIsNotTrimmedAsync(TestContext ctx, FakeProvider server, Api api, string folder, CancellationToken ct)
	{
		Rig    rig         = Build(server, api);
		string unsupported = "{\"error\":{\"message\":\"No endpoints found that support image input\",\"code\":404}}";
		server.Enqueue(FakeReply.Call("c1", "inspect_media", Args(Png(folder, "w.png", 64))));
		// Twice: a 4xx on a streamed request is first taken as "streaming unsupported" and re-sent plain.
		server.Enqueue(FakeReply.Error(404, unsupported));
		server.Enqueue(FakeReply.Error(404, unsupported));
		rig.Session.Bundle.OnUserMessage("look");

		await StepAsync(rig, ct);
		ProtocolResult result = await StepAsync(rig, ct);
		ctx.Assert(result.Outcome != ProtocolCallOutcome.Success, $"{api.Name} WrongModel: the refusal is not papered over");

		List<FakeRequest> sent = server.Requests();
		ctx.Assert(sent[sent.Count - 1].MediaCount > 0, $"{api.Name} WrongModel: no retry stripped the image away");
	}

	// ---- Rig ----

	private static Rig Build(FakeProvider server, Api api)
	{
		return Build(server, api, null);
	}

	private static Rig Build(FakeProvider server, Api api, long? seed)
	{
		return Build(server, api, seed, new ModelConfig());
	}

	// Sampling carries the controls under test; only those fields are read from it.
	private static Rig Build(FakeProvider server, Api api, long? seed, ModelConfig sampling)
	{
		server.Reset();

		ModelConfig config = new ModelConfig
		{
			Id                = $"fake-{api.Name.ToLowerInvariant()}",
			Name              = $"Fake {api.Name}",
			ContextWindow     = 200000,
			MaxOutputTokens   = 4096,
			Seed              = seed,
			Temperature       = sampling.Temperature,
			TopP              = sampling.TopP,
			TopK              = sampling.TopK,
			MinP              = sampling.MinP,
			FrequencyPenalty  = sampling.FrequencyPenalty,
			PresencePenalty   = sampling.PresencePenalty,
			RepetitionPenalty = sampling.RepetitionPenalty,
			Input             = new List<string> { "text", "image" }
		};
		LlmModel model = new LlmModel(config.Id, api.Endpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);

		LlmRegistry                  registry = new LlmRegistry();
		Dictionary<string, LlmModel> models   = new Dictionary<string, LlmModel>(StringComparer.OrdinalIgnoreCase);
		models[config.Id]                     = model;
		registry.RestoreModels(models);

		List<int>           learned = new List<int>();
		ModelCapabilitySink sink    = (id, effort, summaries, retainReasoning, maxImages) =>
		{
			if (maxImages.HasValue)
				learned.Add(maxImages.Value);
		};
		LlmService service = new LlmService(model, api.Protocol, new ModelAvailability(), new EndpointFacts(), new List<string> { config.Id }, sink);

		TestCaptureTransport transport = new TestCaptureTransport();
		BeastSession         data      = new BeastSession(Guid.NewGuid().ToString("N"), "conformance", config.Id, "Tester",
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a test model.", transport, false);
		session.UpdateModel(model);

		Tool[] tools = new Tool[] { EchoTool(), InspectTool(registry, session) };
		return new Rig(session, service, tools, transport, learned);
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
			Definition = new ToolDefinition { Function = new FunctionDefinition { Name = "echo", Description = "Echoes text.", Parameters = parameters } },
			Handler    = (args, id, ct, transport, sessionId, maxOutput) =>
				Task.FromResult(new ToolResult(id, $"echo: {args["text"]?.GetValue<string>() ?? string.Empty}", string.Empty, 0, 3))
		};
	}

	// The real inspect_media, native path: the session's model declares image input.
	private static Tool InspectTool(LlmRegistry registry, Session session)
	{
		JsonObject parameters = new JsonObject
		{
			["type"]       = "object",
			["properties"] = new JsonObject { ["file_paths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
			["required"]   = new JsonArray("file_paths")
		};
		MediaInspector inspector = new MediaInspector();
		return new Tool
		{
			Definition = new ToolDefinition { Function = new FunctionDefinition { Name = "inspect_media", Description = "Looks at media files.", Parameters = parameters } },
			Handler    = async (args, id, ct, transport, sessionId, maxOutput) =>
			{
				List<string> paths = new List<string>();
				foreach (JsonNode? path in (JsonArray)args["file_paths"]!)
					paths.Add(path!.GetValue<string>());
				return await inspector.InspectAsync(id, paths, 0, string.Empty, false, null, registry, session, transport, maxOutput, ct);
			}
		};
	}

	// ---- Helpers ----

	private static string Args(string path)
	{
		return new JsonObject { ["file_paths"] = new JsonArray(path) }.ToJsonString();
	}

	// A PNG signature followed by filler to the requested size; nothing here decodes pixels.
	private static byte[] PngBytes(int size)
	{
		byte[] bytes = new byte[size];
		byte[] magic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
		Array.Copy(magic, bytes, magic.Length);
		for (int i = magic.Length; i < size; i++)
			bytes[i] = (byte)(i * 31);
		return bytes;
	}

	private static string Png(string folder, string name, int size)
	{
		string path = Path.Combine(folder, name);
		File.WriteAllBytes(path, PngBytes(size));
		return path;
	}

	// The first request carrying media that was refused with the given status, or null. A refusal of
	// an image-free request (a server declining stream=true) is not a media refusal.
	private static FakeRequest? FirstMediaRefusal(List<FakeRequest> requests, int status)
	{
		FakeRequest? found = null;
		foreach (FakeRequest request in requests)
		{
			if (found == null && request.StatusSent == status && request.MediaCount > 0)
				found = request;
		}
		return found;
	}
}