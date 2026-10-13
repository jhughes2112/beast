using System;
using System.Collections.Generic;
using System.IO;
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
				await TestMediaAcrossToolTurnsAsync(ctx, server, api, folder, cts.Token);
				await TestTwoImagesInOneInspectAsync(ctx, server, api, folder, cts.Token);
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

	// Several inspects in a row with nothing said between them keep every image in view; the first
	// turn with real output answers them, and from then on they are gone from the request (except on
	// Responses, whose thread the server keeps).
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
		if (api.ElidesAnswered)
		{
			ctx.AssertEqual(0, sent[3].MediaCount, $"{api.Name} MediaAcrossTools: once answered, neither is sent again");
			ctx.Assert(sent[3].Body.ToJsonString().Contains(AnsweredMarker, StringComparison.Ordinal), $"{api.Name} MediaAcrossTools: a note stands in for each");
		}
		else
		{
			ctx.AssertEqual(1,        sent[2].MediaCount, $"{api.Name} MediaAcrossTools: chaining sends only the new image");
			ctx.AssertEqual(2, sent[3].VisibleMediaCount, $"{api.Name} MediaAcrossTools: the server-side thread is left holding both");
		}
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

	// Thinking is an answer where it survives on the wire (Messages replays signed thinking). Chat
	// Completions strips it from replay, so a thinking-plus-tool-call turn leaves nothing behind but
	// the image, and the image stays.
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

		FakeRequest third    = server.Requests()[2];
		int         expected = api.Protocol == DetectedProtocol.Anthropic ? 0 : 1;
		ctx.AssertEqual(expected, third.VisibleMediaCount, $"{api.Name} ThinkingAnswers: media after a thinking turn");
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
		ctx.AssertEqual(1, sent[0].MediaCount, $"{api.Name} DroppedAttachment: sent with the question");
		ctx.AssertEqual(api.ElidesAnswered ? 0 : 1, sent[1].VisibleMediaCount, $"{api.Name} DroppedAttachment: after the answer");
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
		server.Reset();

		ModelConfig config = new ModelConfig
		{
			Id              = $"fake-{api.Name.ToLowerInvariant()}",
			Name            = $"Fake {api.Name}",
			ContextWindow   = 200000,
			MaxOutputTokens = 4096,
			Input           = new List<string> { "text", "image" }
		};
		LlmModel model = new LlmModel(config.Id, api.Endpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);

		LlmRegistry                  registry = new LlmRegistry();
		Dictionary<string, LlmModel> models   = new Dictionary<string, LlmModel>(StringComparer.OrdinalIgnoreCase);
		models[config.Id]                     = model;
		registry.RestoreModels(models);

		List<int>           learned = new List<int>();
		ModelCapabilitySink sink    = (id, effort, summaries, maxImages) =>
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