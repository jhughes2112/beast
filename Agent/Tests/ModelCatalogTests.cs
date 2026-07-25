using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;


// Unit tests for auto-config building blocks: endpoint normalization, lenient catalog entry
// parsing across server families, media attachment persistence, and the multimodal user-message
// wire conversion.
public static class ModelCatalogTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  ModelCatalogTests");

		TestNormalizeRequestEndpoint(ctx);
		TestParseEntryShapes(ctx);
		TestLmStudioEnrichment(ctx);
		TestOllamaEnrichment(ctx);
		TestXaiEnrichment(ctx);
		TestAttachmentRoundTrip(ctx);
		TestChatCompletionsAttachmentParts(ctx);
		TestMediaKinds(ctx);
		TestAnthropicThinkingShapes(ctx);
		TestPathMarkerStripping(ctx);
		TestUnansweredToolCallRepair(ctx);
		TestAttachmentsSurviveFlush(ctx);
		TestTextAfterMediaMerge(ctx);
		TestSelectiveMarkerStrip(ctx);
		TestStagedNameTraversal(ctx);
	}

	// A staged name is a bare file name; /attach is user-reachable, so a rooted or ".."-bearing
	// name must never resolve outside the staging folder — DiscardStaged would otherwise DELETE
	// whatever file it escaped to.
	private static void TestStagedNameTraversal(TestContext ctx)
	{
		string staging = MediaIntake.EnsureStagingFolder(Environment.CurrentDirectory);
		string victim  = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(staging)!, "victim.txt");
		System.IO.File.WriteAllText(victim, "do not delete");
		try
		{
			MediaIntake.DiscardStaged("../victim.txt");
			MediaIntake.DiscardStaged("..\\victim.txt");
			ctx.Assert(System.IO.File.Exists(victim), "Traversal: a ..-name never deletes outside the staging folder");

			MediaIntake.DiscardStaged(victim);
			ctx.Assert(System.IO.File.Exists(victim), "Traversal: a rooted name never deletes outside the staging folder");

			string inside = System.IO.Path.Combine(staging, "legit.txt");
			System.IO.File.WriteAllText(inside, "staged");
			MediaIntake.DiscardStaged("legit.txt");
			ctx.Assert(!System.IO.File.Exists(inside), "Traversal: a bare name inside the folder still discards");
		}
		finally
		{
			if (System.IO.File.Exists(victim))
				System.IO.File.Delete(victim);
		}
	}

	// Plain text merged into a media-bearing user message must fold into the part array, not throw
	// — GetValue<string> on array content killed the session handler when a second line was
	// drained before an assistant reply.
	private static void TestTextAfterMediaMerge(TestContext ctx)
	{
		ProtocolChatCompletions protocol = new ProtocolChatCompletions();
		protocol.OnUserMessage("look at this", new List<MediaAttachment> { new MediaAttachment("image/png", "QUJD") });
		protocol.OnUserMessage("and tell me more");

		JsonArray native = (JsonArray)Reflect.GetField(protocol, "_native")!;
		ctx.AssertEqual(1, native.Count, "MediaMerge: the follow-up text merges rather than appending a second user message");
		JsonArray? parts = native[0]?["content"] as JsonArray;
		ctx.AssertNotNull(parts, "MediaMerge: content stays a part array");
		string mergedText = parts![0]?["text"]?.GetValue<string>() ?? string.Empty;
		ctx.AssertContains(mergedText, "look at this", "MediaMerge: the original text survives");
		ctx.AssertContains(mergedText, "and tell me more", "MediaMerge: the follow-up joins it");
		ctx.AssertEqual("image_url", parts[1]?["type"]?.GetValue<string>(), "MediaMerge: the image part is untouched");

		// A media-only message gains a text part at the front when text merges in later.
		ProtocolChatCompletions wordless = new ProtocolChatCompletions();
		wordless.OnUserMessage(string.Empty, new List<MediaAttachment> { new MediaAttachment("image/png", "QUJD") });
		wordless.OnUserMessage("what is this?");
		JsonArray wordlessNative = (JsonArray)Reflect.GetField(wordless, "_native")!;
		JsonArray? wordlessParts = wordlessNative[0]?["content"] as JsonArray;
		ctx.AssertEqual("text", wordlessParts![0]?["type"]?.GetValue<string>(), "MediaMerge: a wordless media turn gains a leading text part");
		ctx.AssertEqual("what is this?", wordlessParts[0]?["text"]?.GetValue<string>(), "MediaMerge: with the merged text");
	}

	// Only markers whose file actually staged are stripped; one that failed to stage stays in the
	// text so the request for it is never silently erased.
	private static void TestSelectiveMarkerStrip(TestContext ctx)
	{
		HashSet<string> staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "D:\\shots\\ok.png" };
		string result = MediaIntake.StripPathMarkers("[ path D:\\shots\\ok.png ] compare with [ path D:\\shots\\failed.png ]", staged);
		ctx.Assert(!result.Contains("ok.png"), "SelectiveStrip: the staged file's marker is removed");
		ctx.AssertContains(result, "[ path D:\\shots\\failed.png ]", "SelectiveStrip: the unstaged file's marker survives");

		ctx.AssertEqual("just words",
			MediaIntake.StripPathMarkers("[ path a\\x.png ] just words", null), "SelectiveStrip: null set strips everything (the unconditional form)");
	}

	// The pending queue has two consumers: the handler's drain (which resolves media) and
	// FlushPendingMessages (which does not). When the staged files lived on the handler, the flush
	// could commit the text with no media and leave the attachment to ride along with the NEXT
	// message — the file was owned by the wrong object. Text with media waiting must stay queued.
	private static void TestAttachmentsSurviveFlush(TestContext ctx)
	{
		Session session = NewSession();
		session.AddPendingAttachment("staged.png\x01D:\\shots\\a.png");
		session.AddUserMessage("what is this?");

		session.FlushPendingMessages();

		ctx.Assert(session.HasPending, "Flush: text with media waiting stays queued for the drain");
		foreach (CanonicalMessage msg in session.Bundle.Canonical.Messages)
			ctx.Assert(!(msg is UserMessage), "Flush: no message is committed while its media is unresolved");

		// Once the drain has taken the attachments, the flush behaves normally again.
		ctx.AssertEqual(1, session.TakePendingAttachments().Count, "Flush: the drain takes the staged files");
		ctx.Assert(!session.HasPendingAttachments, "Flush: taking clears them, so they cannot reach a second message");
		session.FlushPendingMessages();
		ctx.Assert(!session.HasPending, "Flush: plain text flushes once nothing is waiting on it");
	}

	// An unanswered tool call makes the whole conversation invalid to the strict providers, so the
	// repair pass has to close every gap — and leave everything else exactly as it was.
	private static void TestUnansweredToolCallRepair(TestContext ctx)
	{
		// Two calls, one answered: only the orphan gets a placeholder, and it lands at the end.
		Session session = NewSession();
		List<SemanticToolCall> calls = new List<SemanticToolCall>
		{
			new SemanticToolCall { Id = "call_a", Name = "assign_work", ArgumentsJson = "{}" },
			new SemanticToolCall { Id = "call_b", Name = "read_file", ArgumentsJson = "{}" }
		};
		session.Bundle.OnUserMessage("do the thing");
		session.Bundle.OnAssistantTurn(string.Empty, string.Empty, calls);
		session.Bundle.OnToolResult(new ToolResult("call_b", "file contents", string.Empty, 0, 3));

		session.CompleteDanglingToolCalls();

		IReadOnlyList<CanonicalMessage> messages = session.Bundle.Canonical.Messages;
		ToolResultMessage last = (ToolResultMessage)messages[messages.Count - 1];
		ctx.AssertEqual("call_a", last.ToolCallId, "Repair: the unanswered call is the one filled in");
		ctx.AssertContains(last.Content, "interrupted by user", "Repair: the placeholder says why");

		// Running it again must not stack up duplicates.
		int before = messages.Count;
		session.CompleteDanglingToolCalls();
		ctx.AssertEqual(before, session.Bundle.Canonical.Messages.Count, "Repair: idempotent once every call is answered");

		// A fully answered round is left completely alone.
		Session clean = NewSession();
		clean.Bundle.OnUserMessage("hi");
		clean.Bundle.OnAssistantTurn(string.Empty, string.Empty, new List<SemanticToolCall> { new SemanticToolCall { Id = "c1", Name = "ls", ArgumentsJson = "{}" } });
		clean.Bundle.OnToolResult(new ToolResult("c1", "files", string.Empty, 0, 2));
		int cleanBefore = clean.Bundle.Canonical.Messages.Count;
		clean.CompleteDanglingToolCalls();
		ctx.AssertEqual(cleanBefore, clean.Bundle.Canonical.Messages.Count, "Repair: an answered round is untouched");

		// A plain conversation with no tool calls at all is untouched.
		Session plain = NewSession();
		plain.Bundle.OnUserMessage("hello");
		plain.Bundle.OnAssistantTurn("hi there", string.Empty, new List<SemanticToolCall>());
		int plainBefore = plain.Bundle.Canonical.Messages.Count;
		plain.CompleteDanglingToolCalls();
		ctx.AssertEqual(plainBefore, plain.Bundle.Canonical.Messages.Count, "Repair: a conversation without tool calls is untouched");
	}

	private static Session NewSession()
	{
		BeastSession data = new BeastSession("repair", "repair", "model", "role", string.Empty, 0,
			new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		return new Session(data, string.Empty, new TestCaptureTransport(), false);
	}

	// The client's path markers must not reach the model once the file is attached: the image is
	// already in the message, and repeating its path is noise that also biases on the file name.
	private static void TestPathMarkerStripping(TestContext ctx)
	{
		ctx.AssertEqual("Tell me about her shape.",
			MediaIntake.StripPathMarkers("[ path D:\\ai\\out\\ComfyUI_00009_.png ]  Tell me about her shape."),
			"StripPathMarkers: leading marker and its spacing removed");
		ctx.AssertEqual("before after",
			MediaIntake.StripPathMarkers("before [ path a\\one.png ] after"), "StripPathMarkers: no doubled gap where the marker was");
		ctx.AssertEqual(string.Empty,
			MediaIntake.StripPathMarkers("[ path a\\one.png ]"), "StripPathMarkers: an image with no words leaves empty text");
		ctx.AssertEqual("compare these",
			MediaIntake.StripPathMarkers("[ path a\\one.png ] compare these [ path b\\two.png ]"), "StripPathMarkers: several markers in one line");
		ctx.AssertEqual("plain text", MediaIntake.StripPathMarkers("plain text"), "StripPathMarkers: unmarked text untouched");

		// An image dropped with no words must not produce an empty text block beside the media.
		UserMessage wordless = new UserMessage(string.Empty, new List<MediaAttachment> { new MediaAttachment("image/png", "QUJD") });
		JsonObject native = (JsonObject)Reflect.Static(typeof(ProtocolChatCompletions), "ToNativeMessage", new[] { typeof(CanonicalMessage) }, new object[] { wordless })!;
		JsonArray? parts = native["content"] as JsonArray;
		ctx.AssertNotNull(parts, "Wordless attachment: content is a part array");
		ctx.AssertEqual(1, parts!.Count, "Wordless attachment: only the image part, no empty text block");
		ctx.AssertEqual("image_url", parts[0]?["type"]?.GetValue<string>(), "Wordless attachment: the part is the image");
	}

	// Anthropic changed its thinking contract: the budgeted form is rejected outright by newer
	// models, which want adaptive + an effort word. Both shapes have to be producible, and the
	// 400 that says so has to be recognized.
	private static void TestAnthropicThinkingShapes(TestContext ctx)
	{
		ctx.AssertEqual("low", ReasoningEffort.AnthropicEffort("minimal"), "AnthropicEffort: minimal collapses to low");
		ctx.AssertEqual("medium", ReasoningEffort.AnthropicEffort("medium"), "AnthropicEffort: medium");
		ctx.AssertEqual("max", ReasoningEffort.AnthropicEffort("max"), "AnthropicEffort: max survives (unlike the OpenAI ladder)");
		ctx.AssertNull(ReasoningEffort.AnthropicEffort(""), "AnthropicEffort: no effort configured yields no field");
		ctx.AssertNull(ReasoningEffort.AnthropicEffort("none"), "AnthropicEffort: none yields no field");

		// The budgeted form must still be produced for models that take it.
		ctx.Assert(ReasoningEffort.AnthropicBudget("medium", 16384) > 0, "AnthropicBudget: budgeted form still available");

		string real = "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"\\\"thinking.type.enabled\\\" is not supported for this model. Use \\\"thinking.type.adaptive\\\" and \\\"output_config.effort\\\" to control thinking behavior.\"}}";
		bool wants = (bool)Reflect.Static(typeof(ProtocolAnthropic), "WantsAdaptiveThinking", new[] { typeof(string) }, new object[] { real })!;
		ctx.Assert(wants, "WantsAdaptiveThinking: the real Anthropic 400 is recognized");

		bool unrelated = (bool)Reflect.Static(typeof(ProtocolAnthropic), "WantsAdaptiveThinking", new[] { typeof(string) }, new object[] { "{\"error\":{\"message\":\"credit balance is too low\"}}" })!;
		ctx.Assert(!unrelated, "WantsAdaptiveThinking: an unrelated 400 does not trigger the retry");
	}

	// Extension classification and capability matching drive the whole drag-and-drop routing
	// decision, so both need to be exact.
	private static void TestMediaKinds(TestContext ctx)
	{
		ctx.AssertEqual(MediaKind.Image, MediaKinds.Classify("shot.PNG").Kind, "MediaKinds: image by extension, case-insensitive");
		ctx.AssertEqual("image/png", MediaKinds.Classify("shot.png").MimeType, "MediaKinds: png mime");
		ctx.AssertEqual(MediaKind.Audio, MediaKinds.Classify("clip.mp3").Kind, "MediaKinds: audio");
		ctx.AssertEqual(MediaKind.Video, MediaKinds.Classify("clip.mp4").Kind, "MediaKinds: video");
		ctx.AssertEqual(MediaKind.Text, MediaKinds.Classify("Program.cs").Kind, "MediaKinds: source is text");
		ctx.AssertEqual(MediaKind.Text, MediaKinds.Classify("Dockerfile").Kind, "MediaKinds: extensionless treated as text");
		ctx.AssertEqual(MediaKind.Unknown, MediaKinds.Classify("bundle.zip").Kind, "MediaKinds: unknown binary");
		ctx.AssertEqual(string.Empty, MediaKinds.Classify("notes.txt").MimeType, "MediaKinds: text carries no wire mime");

		ctx.AssertEqual("image", MediaKinds.Modality(MediaKind.Image), "MediaKinds: image modality");
		ctx.AssertEqual(string.Empty, MediaKinds.Modality(MediaKind.Text), "MediaKinds: text needs no modality");

		// A model must DECLARE the modality; declaring nothing means text-only, never assumed capable.
		ModelConfig vision = new ModelConfig { Id = "v", Input = new List<string> { "text", "image" } };
		ModelConfig plain = new ModelConfig { Id = "p" };
		ctx.Assert(MediaKinds.Supports(vision, MediaKind.Image), "MediaKinds: declared image input supports images");
		ctx.Assert(!MediaKinds.Supports(vision, MediaKind.Audio), "MediaKinds: undeclared audio is unsupported");
		ctx.Assert(!MediaKinds.Supports(plain, MediaKind.Image), "MediaKinds: a model declaring nothing is text-only");
		ctx.Assert(!MediaKinds.Supports(vision, MediaKind.Text), "MediaKinds: text is never a model capability question");

	}

	private static void TestNormalizeRequestEndpoint(TestContext ctx)
	{
		ctx.AssertEqual("http://host:8080/v1/chat/completions",
			ModelCatalog.NormalizeRequestEndpoint("http://host:8080"), "Normalize: bare host gains /v1/chat/completions");
		ctx.AssertEqual("http://host:8080/v1/chat/completions",
			ModelCatalog.NormalizeRequestEndpoint("http://host:8080/v1/"), "Normalize: /v1 root gains /chat/completions");
		ctx.AssertEqual("https://openrouter.ai/api/v1/chat/completions",
			ModelCatalog.NormalizeRequestEndpoint("https://openrouter.ai/api/v1/chat/completions"), "Normalize: full URL unchanged");
		ctx.AssertEqual("https://api.anthropic.com/v1/messages",
			ModelCatalog.NormalizeRequestEndpoint("https://api.anthropic.com"), "Normalize: Anthropic gains /v1/messages");
		ctx.AssertEqual("https://api.openai.com/v1/responses",
			ModelCatalog.NormalizeRequestEndpoint("https://api.openai.com/v1/responses"), "Normalize: responses endpoint unchanged");
	}

	private static void TestParseEntryShapes(TestContext ctx)
	{
		// OpenRouter: rich metadata, per-token string pricing, declared modalities.
		JsonNode openrouter = JsonNode.Parse("""
			{"id":"anthropic/claude-sonnet-5","name":"Claude Sonnet 5","context_length":200000,
			 "pricing":{"prompt":"0.000003","completion":"0.000015"},
			 "architecture":{"input_modalities":["text","image"]},
			 "top_provider":{"max_completion_tokens":64000}}
			""")!;
		DiscoveredModel rich = (DiscoveredModel)Reflect.Static(typeof(ModelCatalog), "ParseEntry", new[] { typeof(JsonNode), typeof(bool) }, new object[] { openrouter, false })!;
		ctx.AssertEqual(200000, rich.ContextWindow, "ParseEntry: OpenRouter context_length");
		ctx.AssertEqual(64000, rich.MaxOutputTokens, "ParseEntry: OpenRouter max_completion_tokens");
		ctx.AssertEqual(3m, rich.CostInput, "ParseEntry: OpenRouter prompt pricing normalized to per-Mtok");
		ctx.AssertEqual(15m, rich.CostOutput, "ParseEntry: OpenRouter completion pricing normalized to per-Mtok");
		ctx.AssertNotNull(rich.Modalities, "ParseEntry: OpenRouter modalities declared");
		ctx.Assert(rich.Modalities!.Contains("image"), "ParseEntry: OpenRouter image modality");

		// Release date from OpenAI-style epoch seconds.
		JsonNode dated = JsonNode.Parse("""{"id":"m1","created":1750000000}""")!;
		DiscoveredModel epoch = (DiscoveredModel)Reflect.Static(typeof(ModelCatalog), "ParseEntry", new[] { typeof(JsonNode), typeof(bool) }, new object[] { dated, false })!;
		ctx.AssertEqual(1750000000L, epoch.Created, "ParseEntry: epoch created passes through");

		// Release date from Anthropic's RFC3339 created_at.
		JsonNode rfc = JsonNode.Parse("""{"id":"m2","created_at":"2026-01-15T00:00:00Z"}""")!;
		DiscoveredModel stamped = (DiscoveredModel)Reflect.Static(typeof(ModelCatalog), "ParseEntry", new[] { typeof(JsonNode), typeof(bool) }, new object[] { rfc, true })!;
		ctx.AssertEqual(new System.DateTimeOffset(2026, 1, 15, 0, 0, 0, System.TimeSpan.Zero).ToUnixTimeSeconds(), stamped.Created, "ParseEntry: RFC3339 created_at parsed");

		// vLLM: window under max_model_len, everything else unknown.
		JsonNode vllm = JsonNode.Parse("""{"id":"qwen3-32b","max_model_len":32768}""")!;
		DiscoveredModel bare = (DiscoveredModel)Reflect.Static(typeof(ModelCatalog), "ParseEntry", new[] { typeof(JsonNode), typeof(bool) }, new object[] { vllm, false })!;
		ctx.AssertEqual(32768, bare.ContextWindow, "ParseEntry: vLLM max_model_len");
		ctx.AssertEqual(-1m, bare.CostInput, "ParseEntry: unknown pricing stays -1");
		ctx.AssertNull(bare.Modalities, "ParseEntry: unknown modalities stay null");

		// Shape-shifted fields must never throw: capabilities as an OBJECT (not array), pricing
		// as a bool, architecture as a string. The parser reads leniently or ignores.
		JsonNode hostile = JsonNode.Parse("""
			{"id":"weird","capabilities":{"vision":true},"pricing":true,"architecture":"transformer","top_provider":"none"}
			""")!;
		DiscoveredModel weird = (DiscoveredModel)Reflect.Static(typeof(ModelCatalog), "ParseEntry", new[] { typeof(JsonNode), typeof(bool) }, new object[] { hostile, false })!;
		ctx.AssertEqual("weird", weird.Id, "ParseEntry: hostile shapes parse without throwing");
		ctx.AssertNull(weird.Modalities, "ParseEntry: object-shaped capabilities ignored");

		// Anthropic: names-only catalog gets the versioned-in-code capability defaults.
		JsonNode anthropic = JsonNode.Parse("""{"id":"claude-sonnet-5","display_name":"Claude Sonnet 5"}""")!;
		DiscoveredModel claude = (DiscoveredModel)Reflect.Static(typeof(ModelCatalog), "ParseEntry", new[] { typeof(JsonNode), typeof(bool) }, new object[] { anthropic, true })!;
		ctx.AssertEqual(200000, claude.ContextWindow, "ParseEntry: Anthropic default window");
		ctx.Assert(claude.Modalities != null && claude.Modalities.Contains("image"), "ParseEntry: Anthropic default image modality");
		ctx.AssertEqual("Claude Sonnet 5", claude.Name, "ParseEntry: display_name used as name");

		// The real 2026 Anthropic shape: capabilities as nested {feature: {supported}} objects,
		// zero-valued max_input_tokens/max_tokens placeholders, RFC3339 created_at.
		JsonNode anthropic2026 = JsonNode.Parse("""
			{"id":"claude-opus-4-6","display_name":"Claude Opus 4.6","type":"model",
			 "created_at":"2026-02-04T00:00:00Z","max_input_tokens":0,"max_tokens":0,
			 "capabilities":{
			   "image_input":{"supported":true},
			   "pdf_input":{"supported":true},
			   "thinking":{"supported":true,"types":{"adaptive":{"supported":true}}},
			   "effort":{"supported":true,"high":{"supported":true}}}}
			""")!;
		DiscoveredModel opus = (DiscoveredModel)Reflect.Static(typeof(ModelCatalog), "ParseEntry", new[] { typeof(JsonNode), typeof(bool) }, new object[] { anthropic2026, true })!;
		ctx.Assert(opus.Modalities != null && opus.Modalities.Contains("image"), "ParseEntry: Anthropic image_input.supported → image");
		ctx.Assert(opus.Modalities != null && opus.Modalities.Contains("file"), "ParseEntry: Anthropic pdf_input.supported → file");
		ctx.Assert(opus.Modalities != null && !opus.Modalities.Contains("audio"), "ParseEntry: absent audio_input stays off");
		ctx.AssertEqual(200000, opus.ContextWindow, "ParseEntry: zero max_input_tokens placeholder falls to the default");
		ctx.AssertEqual(0, opus.MaxOutputTokens, "ParseEntry: zero max_tokens placeholder stays unknown");
		ctx.Assert(opus.Created > 0, "ParseEntry: created_at parsed on the 2026 shape");
	}

	private static void TestLmStudioEnrichment(TestContext ctx)
	{
		List<DiscoveredModel> models = new List<DiscoveredModel>
		{
			new DiscoveredModel { Id = "qwen2.5-vl-7b" },
			new DiscoveredModel { Id = "untouched", ContextWindow = 4096, Modalities = new List<string> { "text" } }
		};
		JsonArray data = (JsonArray)JsonNode.Parse("""
			[{"id":"qwen2.5-vl-7b","type":"vlm","max_context_length":128000,"loaded_context_length":32768,"capabilities":["tool_use","vision"]}]
			""")!;
		ModelCatalog.ParseLmStudioList(data, models);

		ctx.AssertEqual(32768, models[0].ContextWindow, "LmStudio: loaded_context_length beats max");
		ctx.Assert(models[0].Modalities != null && models[0].Modalities!.Contains("image"), "LmStudio: vlm/vision maps to image");
		ctx.AssertEqual(0m, models[0].CostInput, "LmStudio: local pricing resolves to 0");
		ctx.AssertEqual(4096, models[1].ContextWindow, "LmStudio: known values untouched");
	}

	private static void TestOllamaEnrichment(TestContext ctx)
	{
		// Pinned num_ctx: window taken; vision capability maps to image.
		DiscoveredModel pinned = new DiscoveredModel { Id = "llava" };
		JsonNode showPinned = JsonNode.Parse("""
			{"capabilities":["completion","vision"],"parameters":"num_ctx                        16384\ntemperature                    0.7",
			 "model_info":{"llama.context_length":131072}}
			""")!;
		ModelCatalog.ParseOllamaShow(showPinned, pinned);
		ctx.AssertEqual(16384, pinned.ContextWindow, "Ollama: pinned num_ctx becomes the window");
		ctx.Assert(pinned.Modalities != null && pinned.Modalities!.Contains("image"), "Ollama: vision capability maps to image");

		// No num_ctx: the trained context_length must NOT be trusted as the served window.
		DiscoveredModel unpinned = new DiscoveredModel { Id = "qwen3" };
		JsonNode showUnpinned = JsonNode.Parse("""
			{"capabilities":["completion"],"parameters":"","model_info":{"qwen3.context_length":131072}}
			""")!;
		ModelCatalog.ParseOllamaShow(showUnpinned, unpinned);
		ctx.AssertEqual(0, unpinned.ContextWindow, "Ollama: unpinned window stays unknown (trained max is not the served window)");
		ctx.Assert(unpinned.Modalities != null && !unpinned.Modalities!.Contains("image"), "Ollama: text-only capabilities");
		ctx.AssertEqual(0m, unpinned.CostInput, "Ollama: local pricing resolves to 0");
	}

	private static void TestXaiEnrichment(TestContext ctx)
	{
		List<DiscoveredModel> models = new List<DiscoveredModel>
		{
			new DiscoveredModel { Id = "grok-4" },
			new DiscoveredModel { Id = "grok-4-latest" }
		};
		// Prices are USD cents per 100M tokens: 30000 → $3/Mtok, 150000 → $15/Mtok, 7500 → $0.75/Mtok.
		JsonArray list = (JsonArray)JsonNode.Parse("""
			[{"id":"grok-4","aliases":["grok-4-latest"],
			  "input_modalities":["text","image"],"output_modalities":["text"],
			  "prompt_text_token_price":30000,"completion_text_token_price":150000,
			  "cached_prompt_text_token_price":7500,"long_context_threshold":0}]
			""")!;
		ModelCatalog.ParseXaiList(list, models);

		ctx.AssertEqual(3m, models[0].CostInput, "Xai: prompt price cents/100M normalized to per-Mtok");
		ctx.AssertEqual(15m, models[0].CostOutput, "Xai: completion price normalized");
		ctx.AssertEqual(0.75m, models[0].CostCacheRead, "Xai: cached prompt price normalized");
		ctx.Assert(models[0].Modalities != null && models[0].Modalities!.Contains("image"), "Xai: input modalities declared");
		ctx.AssertEqual(3m, models[1].CostInput, "Xai: alias match enriches too");
		ctx.AssertEqual(0, models[0].ContextWindow, "Xai: window stays unknown (not reported by the API)");
	}

	private static void TestAttachmentRoundTrip(TestContext ctx)
	{
		// A user message with an attachment must survive session persistence, and plain user
		// messages must not grow an attachments property.
		List<CanonicalMessage> messages = new List<CanonicalMessage>
		{
			new UserMessage("plain"),
			new UserMessage("with media", new List<MediaAttachment> { new MediaAttachment("image/png", "QUJD") })
		};
		BeastSession session = new BeastSession("id", "name", "model", "role", "", 0, messages, null, 0m, 0, 0, 0, false);

		string json = JsonSerializer.Serialize(session, BeastJson.Persist.BeastSession);
		BeastSession? loaded = JsonSerializer.Deserialize(json, BeastJson.Persist.BeastSession);

		ctx.AssertNotNull(loaded, "AttachmentRoundTrip: session deserializes");
		UserMessage plain = (UserMessage)loaded!.Messages[0];
		UserMessage media = (UserMessage)loaded.Messages[1];
		ctx.AssertNull(plain.Attachments, "AttachmentRoundTrip: plain message has no attachments");
		ctx.AssertNotNull(media.Attachments, "AttachmentRoundTrip: attachments survive persistence");
		ctx.AssertEqual("image/png", media.Attachments![0].MimeType, "AttachmentRoundTrip: mime survives");
		ctx.AssertEqual("QUJD", media.Attachments[0].Base64Data, "AttachmentRoundTrip: data survives");
	}

	private static void TestChatCompletionsAttachmentParts(TestContext ctx)
	{
		UserMessage media = new UserMessage("look at this", new List<MediaAttachment>
		{
			new MediaAttachment("image/png", "QUJD"),
			new MediaAttachment("audio/wav", "REVG")
		});
		JsonObject native = (JsonObject)Reflect.Static(typeof(ProtocolChatCompletions), "ToNativeMessage", new[] { typeof(CanonicalMessage) }, new object[] { media })!;

		ctx.AssertEqual("user", native["role"]?.GetValue<string>(), "AttachmentParts: role");
		JsonArray? parts = native["content"] as JsonArray;
		ctx.AssertNotNull(parts, "AttachmentParts: content is a part array");
		ctx.AssertEqual(3, parts!.Count, "AttachmentParts: text + image + audio parts");
		ctx.AssertEqual("text", parts[0]?["type"]?.GetValue<string>(), "AttachmentParts: text part first");
		ctx.AssertEqual("image_url", parts[1]?["type"]?.GetValue<string>(), "AttachmentParts: image part");
		ctx.AssertContains(parts[1]?["image_url"]?["url"]?.GetValue<string>() ?? "", "data:image/png;base64,QUJD", "AttachmentParts: data URI");
		ctx.AssertEqual("input_audio", parts[2]?["type"]?.GetValue<string>(), "AttachmentParts: audio part");
		ctx.AssertEqual("wav", parts[2]?["input_audio"]?["format"]?.GetValue<string>(), "AttachmentParts: audio format from mime");

		// A plain user message keeps the simple string content shape.
		JsonObject plain = (JsonObject)Reflect.Static(typeof(ProtocolChatCompletions), "ToNativeMessage", new[] { typeof(CanonicalMessage) }, new object[] { new UserMessage("hi") })!;
		ctx.Assert(plain["content"] is JsonValue, "AttachmentParts: plain message stays string content");
	}
}