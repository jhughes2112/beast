using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;


// A tool result that carries a media file must reach every protocol's wire shape with the file's
// bytes in it, whether the result arrived live through the commit fan-out or by rehydrating the
// canonical record after a protocol switch. The canonical record itself holds only the path.
public static class MediaToolResultTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  MediaToolResultTests");

		string path = Path.Combine(Path.GetTempPath(), $"beast-media-{Guid.NewGuid():N}.png");
		File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 });
		string expected = Convert.ToBase64String(File.ReadAllBytes(path));
		string second   = Path.Combine(Path.GetTempPath(), $"beast-media-{Guid.NewGuid():N}.png");
		File.WriteAllBytes(second, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7, 6 });

		try
		{
			TestCanonicalKeepsPathOnly(ctx, path);
			TestLegacySingleMediaLoads(ctx, path);
			TestSeveralMediaInOneResult(ctx, path, second);
			TestChatCompletionsCarrier(ctx, path, expected);
			TestAnthropicToolResultBlock(ctx, path, expected);
			TestResponsesOutputParts(ctx, path, expected);
			TestMissingFileDegradesToNote(ctx);
			TestMediaSentOnce(ctx, path);
			TestMediaBudgetKeepsNewest(ctx);
			TestMediaBudgetLearnsImageCount(ctx);
			TestMediaBudgetDropsOldest(ctx);
		}
		finally
		{
			File.Delete(path);
			File.Delete(second);
		}
	}

	private static List<CanonicalMessage> Conversation(string path)
	{
		List<CanonicalMessage> messages = new List<CanonicalMessage>();
		ListenerBundle         bundle   = new ListenerBundle(new CanonicalConversation(messages), null);
		bundle.OnUserMessage("look at the screenshot");
		bundle.OnAssistantTurn(string.Empty, string.Empty, new List<SemanticToolCall>
		{
			new SemanticToolCall { Id = "call_a", Name = "inspect_media", ArgumentsJson = "{\"file_path\":\"a.png\"}" },
			new SemanticToolCall { Id = "call_b", Name = "ls",            ArgumentsJson = "{\"folder\":\".\"}" }
		});
		bundle.OnToolResult(new ToolResult("call_a", "a.png attached", string.Empty, 0, 3, new List<MediaFile> { new MediaFile(path, "image/png") }));
		bundle.OnToolResult(new ToolResult("call_b", "a.png\nb.txt", string.Empty, 0, 3));
		return messages;
	}

	private static void TestCanonicalKeepsPathOnly(TestContext ctx, string path)
	{
		ctx.Log("    CanonicalKeepsPathOnly");
		List<CanonicalMessage> messages = Conversation(path);
		ToolResultMessage      tr       = (ToolResultMessage)messages[2];
		ctx.AssertEqual(          1,      tr.Media!.Count, "Canonical: tool result records its one media file");
		ctx.AssertEqual(       path,     tr.Media[0].Path, "Canonical: tool result records the media path");
		ctx.AssertEqual("image/png", tr.Media[0].MimeType, "Canonical: tool result records the mime type");
		ctx.Assert(((ToolResultMessage)messages[3]).Media == null, "Canonical: a text result carries no media");
	}

	// Session files from before results could carry several files hold one as mediaPath/mediaMimeType.
	// They load into Media, and are written back only in the new shape.
	private static void TestLegacySingleMediaLoads(TestContext ctx, string path)
	{
		ctx.Log("    LegacySingleMediaLoads");
		string             legacy = $"{{\"$type\":\"tool\",\"ToolCallId\":\"call_a\",\"Content\":\"a.png attached\",\"MediaPath\":{JsonValue.Create(path)!.ToJsonString()},\"MediaMimeType\":\"image/png\"}}";
		CanonicalMessage?  loaded = JsonSerializer.Deserialize(legacy, BeastJsonContext.Default.CanonicalMessage);
		ToolResultMessage? tr     = loaded as ToolResultMessage;
		ctx.Assert(tr != null && tr.Media != null && tr.Media.Count == 1 && tr.Media[0].Path == path && tr.Media[0].MimeType == "image/png", "Legacy: a single mediaPath loads as one media file");

		string written = tr == null ? string.Empty : JsonSerializer.Serialize<CanonicalMessage>(tr, BeastJsonContext.Default.CanonicalMessage);
		ctx.Assert(written.Contains("\"Media\"", StringComparison.Ordinal) && !written.Contains("MediaPath", StringComparison.Ordinal), $"Legacy: it is saved back as a media list only ({written})");
	}

	// Several files in one result — a side-by-side comparison — reach every wire shape, in order.
	private static void TestSeveralMediaInOneResult(TestContext ctx, string path, string second)
	{
		ctx.Log("    SeveralMediaInOneResult");
		List<CanonicalMessage> messages = new List<CanonicalMessage>();
		ListenerBundle         bundle   = new ListenerBundle(new CanonicalConversation(messages), null);
		bundle.OnUserMessage("compare these");
		bundle.OnAssistantTurn(string.Empty, string.Empty, new List<SemanticToolCall> { new SemanticToolCall { Id = "call_a", Name = "inspect_media", ArgumentsJson = "{}" } });
		bundle.OnToolResult(new ToolResult("call_a", "two attached", string.Empty, 0, 3, new List<MediaFile> { new MediaFile(path, "image/png"), new MediaFile(second, "image/png") }));
		string firstData  = Convert.ToBase64String(File.ReadAllBytes(path));
		string secondData = Convert.ToBase64String(File.ReadAllBytes(second));

		JsonArray carrier = (JsonArray)((JsonArray)Reflect.GetField(new ProtocolProxy(TestModel()).EnsureProtocolChatCompletions(messages), "_native")!)[3]!["content"]!;
		ctx.AssertEqual(4, carrier.Count, "Several: ChatCompletions carrier holds a note and an image per file");
		ctx.Assert(carrier[1]!["image_url"]!["url"]!.GetValue<string>().EndsWith(firstData, StringComparison.Ordinal)
			&& carrier[3]!["image_url"]!["url"]!.GetValue<string>().EndsWith(secondData, StringComparison.Ordinal), "Several: ChatCompletions keeps the files in order");

		JsonArray inner = (JsonArray)((JsonArray)((JsonArray)Reflect.GetField(new ProtocolProxy(TestModel()).EnsureProtocolAnthropic(messages), "_native")!)[2]!["content"]!)[0]!["content"]!;
		ctx.AssertEqual(3, inner.Count, "Several: Anthropic tool_result holds the text and both images");
		ctx.Assert(inner[1]!["source"]!["data"]!.GetValue<string>() == firstData && inner[2]!["source"]!["data"]!.GetValue<string>() == secondData, "Several: Anthropic keeps the files in order");

		JsonArray parts = (JsonArray)((JsonArray)Reflect.GetField(new ProtocolProxy(TestModel()).EnsureProtocolResponses(messages), "_rehydratedInput")!)[2]!["output"]!;
		ctx.AssertEqual(3, parts.Count, "Several: Responses output holds the text and an input_image per file");
		ctx.Assert(parts[2]!["image_url"]!.GetValue<string>().EndsWith(secondData, StringComparison.Ordinal), "Several: Responses keeps the files in order");
	}

	// The tool message stays text; the image rides in a user message after BOTH tool replies, so
	// the replies still sit directly after the assistant turn that called them.
	private static void TestChatCompletionsCarrier(TestContext ctx, string path, string expected)
	{
		ctx.Log("    ChatCompletionsCarrier");
		List<CanonicalMessage>  messages = Conversation(path);
		ProtocolChatCompletions cc       = new ProtocolProxy(TestModel()).EnsureProtocolChatCompletions(messages);
		JsonArray               native   = (JsonArray)Reflect.GetField(cc, "_native")!;

		ctx.AssertEqual(     5,    native.Count, "ChatCompletions: user, assistant, tool, tool, carrier");
		ctx.AssertEqual("tool", Role(native[2]), "ChatCompletions: first tool reply follows the assistant turn");
		ctx.AssertEqual("tool", Role(native[3]), "ChatCompletions: second tool reply follows the first");
		ctx.AssertEqual("user", Role(native[4]), "ChatCompletions: media carrier trails the run");
		ctx.Assert(native[2]!["content"] is JsonValue, "ChatCompletions: the tool message itself stays a string");

		JsonArray parts = (JsonArray)native[4]!["content"]!;
		ctx.AssertEqual("image_url", parts[1]!["type"]!.GetValue<string>(), "ChatCompletions: carrier holds an image part");
		ctx.Assert(parts[1]!["image_url"]!["url"]!.GetValue<string>().EndsWith(expected, StringComparison.Ordinal), "ChatCompletions: image bytes come from disk");

		// Live fan-out must produce the same shape as the rehydrate above.
		List<CanonicalMessage>  live       = new List<CanonicalMessage>();
		ListenerBundle          bundle     = new ListenerBundle(new CanonicalConversation(live), null);
		ProtocolChatCompletions liveCc     = new ProtocolProxy(TestModel()).EnsureProtocolChatCompletions(live);
		JsonArray               liveNative = (JsonArray)Reflect.GetField(liveCc, "_native")!;
		liveCc.OnUserMessage("look at the screenshot");
		liveCc.OnAssistantTurn(string.Empty, string.Empty, new List<SemanticToolCall> { new SemanticToolCall { Id = "call_a", Name = "inspect_media", ArgumentsJson = "{}" } });
		liveCc.OnToolResult(new ToolResult("call_a", "a.png attached", string.Empty, 0, 3, new List<MediaFile> { new MediaFile(path, "image/png") }));
		liveCc.OnToolResult(new ToolResult("call_b", "a.png\nb.txt", string.Empty, 0, 3));
		ctx.AssertEqual(5, liveNative.Count, "ChatCompletions live: same message count");
		ctx.AssertEqual("tool", Role(liveNative[3]), "ChatCompletions live: later tool reply inserted ahead of the carrier");
		ctx.AssertEqual("user", Role(liveNative[4]), "ChatCompletions live: carrier still trails");

		// User text arriving mid-turn (the drain after a tool round) must not fold into the carrier:
		// Rehydrate emits it as its own message, and the live shape has to match.
		liveCc.OnUserMessage("also check the footer");
		ctx.AssertEqual(6, liveNative.Count, "ChatCompletions live: user text after the carrier is a separate message");
		ctx.AssertEqual(2, ((JsonArray)liveNative[4]!["content"]!).Count, "ChatCompletions live: carrier keeps only its note and image");
		ctx.Assert(liveNative[5]!["content"] is JsonValue, "ChatCompletions live: the new user message is plain text");
	}

	private static void TestAnthropicToolResultBlock(TestContext ctx, string path, string expected)
	{
		ctx.Log("    AnthropicToolResultBlock");
		List<CanonicalMessage> messages  = Conversation(path);
		ProtocolAnthropic      anthropic = new ProtocolProxy(TestModel()).EnsureProtocolAnthropic(messages);
		JsonArray              native    = (JsonArray)Reflect.GetField(anthropic, "_native")!;

		// user, assistant, user (both tool_result blocks collapse into one user message).
		ctx.AssertEqual(3, native.Count, "Anthropic: alternation preserved");
		JsonArray blocks = (JsonArray)native[2]!["content"]!;
		JsonArray inner  = (JsonArray)blocks[0]!["content"]!;
		ctx.AssertEqual("tool_result", blocks[0]!["type"]!.GetValue<string>(), "Anthropic: first block is the tool_result");
		ctx.AssertEqual(2, inner.Count, "Anthropic: tool_result content is text plus image");
		ctx.AssertEqual( "image",            inner[1]!["type"]!.GetValue<string>(), "Anthropic: image block inside the tool_result");
		ctx.AssertEqual(expected, inner[1]!["source"]!["data"]!.GetValue<string>(), "Anthropic: image bytes come from disk");
		ctx.AssertEqual(       1,        ((JsonArray)blocks[1]!["content"]!).Count, "Anthropic: text-only tool_result has just its text");
	}

	private static void TestResponsesOutputParts(TestContext ctx, string path, string expected)
	{
		ctx.Log("    ResponsesOutputParts");
		List<CanonicalMessage> messages  = Conversation(path);
		ProtocolResponses      responses = new ProtocolProxy(TestModel()).EnsureProtocolResponses(messages);
		JsonArray              input     = (JsonArray)Reflect.GetField(responses, "_rehydratedInput")!;

		// user, function_call, function_call, output, output.
		ctx.AssertEqual(5, input.Count, "Responses: one item per canonical element");
		JsonArray parts = (JsonArray)input[3]!["output"]!;
		ctx.AssertEqual("input_image", parts[1]!["type"]!.GetValue<string>(), "Responses: output carries an input_image part");
		ctx.Assert(parts[1]!["image_url"]!.GetValue<string>().EndsWith(expected, StringComparison.Ordinal), "Responses: image bytes come from disk");
		ctx.Assert(input[4]!["output"] is JsonValue, "Responses: text-only output stays a string");
	}

	private static void TestMissingFileDegradesToNote(TestContext ctx)
	{
		ctx.Log("    MissingFileDegradesToNote");
		string                 gone      = Path.Combine(Path.GetTempPath(), $"beast-missing-{Guid.NewGuid():N}.png");
		List<CanonicalMessage> messages  = Conversation(gone);
		ProtocolAnthropic      anthropic = new ProtocolProxy(TestModel()).EnsureProtocolAnthropic(messages);
		JsonArray              native    = (JsonArray)Reflect.GetField(anthropic, "_native")!;
		JsonArray              inner     = (JsonArray)((JsonArray)native[2]!["content"]!)[0]!["content"]!;
		ctx.AssertEqual("text", inner[1]!["type"]!.GetValue<string>(), "Missing file: a text note replaces the image");
		ctx.Assert(inner[1]!["text"]!.GetValue<string>().Contains(gone, StringComparison.Ordinal), "Missing file: the note names the path");
	}

	// A ChatCompletions-shaped history with one image per user message, oldest first; each image's
	// payload is `chars` base64 characters tagged with its index so the survivors can be identified.
	private static JsonArray ImageHistory(int count, int chars)
	{
		JsonArray messages = new JsonArray();
		for (int i = 0; i < count; i++)
		{
			string    data  = $"{i}".PadRight(chars, 'A');
			JsonArray parts = new JsonArray();
			parts.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = $"shot {i}" });
			parts.Add((JsonNode)new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:image/png;base64,{data}" } });
			messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = parts });
		}
		return messages;
	}

	private static int ImagesIn(JsonArray messages)
	{
		int images = 0;
		foreach (JsonNode? msg in messages)
		{
			if (msg!["content"] is JsonArray parts && parts[1]!["type"]!.GetValue<string>() == "image_url")
				images++;
		}
		return images;
	}

	// Media rides in requests until the model has answered it with text or thinking AND newer media
	// has arrived; after that it is a note. A turn of nothing but tool calls is not an answer. The
	// history itself keeps the media, so a retry still carries it.
	private static void TestMediaSentOnce(TestContext ctx, string path)
	{
		ctx.Log("    MediaSentOnce");

		JsonArray pending = ImageHistory(1, 100);
		new MediaBudget().Apply(pending, 0, "text", true);
		ctx.AssertEqual(1, ImagesIn(pending), "SentOnce: unanswered media is sent");

		JsonArray answered = ImageHistory(2, 100);
		answered.Insert(1, new JsonObject { ["role"] = "assistant", ["content"] = "it is a cat" });
		new MediaBudget().Apply(answered, 0, "text", true);
		ctx.AssertEqual("text",      answered[0]!["content"]![1]!["type"]!.GetValue<string>(), "SentOnce: answered media becomes a note once newer media follows");
		ctx.AssertEqual("image_url", answered[2]!["content"]![1]!["type"]!.GetValue<string>(), "SentOnce: media after the last answer is still sent");

		// The newest batch outlives the model's reply: with no newer media, answered media stays in view.
		JsonArray lingering = ImageHistory(1, 100);
		lingering.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = "it is a cat" });
		new MediaBudget().Apply(lingering, 0, "text", true);
		ctx.AssertEqual(1, ImagesIn(lingering), "SentOnce: answered media stays until the next media arrives");

		// Several inspects in a row with nothing said between them: the model still sees them all.
		JsonArray toolsOnly = ImageHistory(2, 100);
		toolsOnly.Insert(1, new JsonObject { ["role"] = "assistant", ["content"] = string.Empty, ["tool_calls"] = new JsonArray() });
		new MediaBudget().Apply(toolsOnly, 0, "text", true);
		ctx.AssertEqual(2, ImagesIn(toolsOnly), "SentOnce: a tool-call-only turn does not answer the media before it");

		// Anthropic: a thinking block is an answer even with no text beside it.
		JsonArray thought = ImageHistory(1, 100);
		thought.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = "a cat", ["signature"] = "s" }, new JsonObject { ["type"] = "tool_use", ["id"] = "t", ["name"] = "ls", ["input"] = new JsonObject() }) });
		new MediaBudget().Apply(thought, 0, "text", true);
		thought.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "next" }, new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAAA" } }) });
		new MediaBudget().Apply(thought, 0, "text", true);
		ctx.AssertEqual(1, ImagesIn(thought), "SentOnce: thinking answers the media before it, and only the newer image remains");

		// Responses keeps its history server-side and is left alone.
		JsonArray responses = ImageHistory(1, 100);
		responses.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = "it is a cat" });
		new MediaBudget().Apply(responses, 0, "input_text", false);
		ctx.AssertEqual(1, ImagesIn(responses), "SentOnce: nothing is removed when answered media is kept");

		// Through a real protocol: the inspect result is sent on the next request, kept after the
		// model has replied, while the native history keeps it.
		List<CanonicalMessage>  messages = Conversation(path);
		ProtocolChatCompletions cc       = new ProtocolProxy(TestModel()).EnsureProtocolChatCompletions(messages);
		Type[]                  types    = new Type[] { typeof(LlmModel), typeof(List<ToolDefinition>), typeof(string), typeof(int?) };
		object[]                args     = new object[] { TestModel(), new List<ToolDefinition>(), null!, null! };

		JsonObject first = (JsonObject)Reflect.Instance(cc, "BuildRequestBody", types, args)!;
		ctx.Assert(first.ToJsonString().Contains("\"image_url\"", StringComparison.Ordinal), "SentOnce: the inspect result is sent to the model");

		cc.OnAssistantTurn("the button is misaligned", string.Empty, new List<SemanticToolCall>());
		JsonObject second = (JsonObject)Reflect.Instance(cc, "BuildRequestBody", types, args)!;
		ctx.Assert(second.ToJsonString().Contains("\"image_url\"", StringComparison.Ordinal), "SentOnce: and still sent after the model answered, until newer media arrives");
		JsonArray native = (JsonArray)Reflect.GetField(cc, "_native")!;
		ctx.Assert(native.ToJsonString().Contains("\"image_url\"", StringComparison.Ordinal), "SentOnce: native history is untouched");
	}

	private static void TestMediaBudgetKeepsNewest(TestContext ctx)
	{
		ctx.Log("    MediaBudgetKeepsNewest");
		MediaBudget budget   = new MediaBudget();
		JsonArray   messages = ImageHistory(6, 100);
		budget.Apply(messages, 4, "text", true);

		ctx.AssertEqual(4, ImagesIn(messages), "MediaBudget: a 4-image cap keeps four");
		ctx.AssertEqual("text",      messages[0]!["content"]![1]!["type"]!.GetValue<string>(), "MediaBudget: the oldest image is the one dropped");
		ctx.AssertEqual("text",      messages[1]!["content"]![1]!["type"]!.GetValue<string>(), "MediaBudget: and the next oldest");
		ctx.AssertEqual("image_url", messages[5]!["content"]![1]!["type"]!.GetValue<string>(), "MediaBudget: the newest survives");
		ctx.AssertNotNull(budget.TakeNote(), "MediaBudget: the trim is reported");
		ctx.Assert(budget.TakeNote() == null, "MediaBudget: and reported once, not per request");

		// A tool call's arguments are the model's JSON; one that says "type":"image" is not media.
		JsonArray anthropic = new JsonArray();
		JsonArray blocks    = new JsonArray();
		blocks.Add((JsonNode)new JsonObject { ["type"] = "tool_use", ["id"] = "t1", ["name"] = "x", ["input"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["type"] = "image" }) } });
		anthropic.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = blocks });
		new MediaBudget().Apply(anthropic, 0, "text", true);
		ctx.AssertEqual("image", anthropic[0]!["content"]![0]!["input"]!["items"]![0]!["type"]!.GetValue<string>(), "MediaBudget: tool_use input is never rewritten");
	}

	private static void TestMediaBudgetLearnsImageCount(TestContext ctx)
	{
		ctx.Log("    MediaBudgetLearnsImageCount");
		List<(string Id, int? MaxImages)> learned = new List<(string, int?)>();
		ModelCapabilitySink               sink    = (id, effort, summaries, maxImages) => learned.Add((id, maxImages));
		SessionLogger                     logger  = new SessionLogger("media-budget-test");
		MediaBudget                       budget  = new MediaBudget();

		budget.Apply(ImageHistory(6, 100), 0, "text", true);
		bool retry = budget.LearnFromRefusal(TestModel(), 400, "{\"error\":{\"message\":\"Too many images in request: > 4\",\"code\":400}}", sink, logger);
		ctx.Assert(retry, "MediaBudget: an image-count refusal is re-sent");
		ctx.AssertEqual(1,             learned.Count, "MediaBudget: and reported");
		ctx.AssertEqual(4, learned[0].MaxImages ?? 0, "MediaBudget: at the cap the provider stated");

		JsonArray resent = ImageHistory(6, 100);
		budget.Apply(resent, 0, "text", true);
		ctx.AssertEqual(4, ImagesIn(resent), "MediaBudget: the re-sent request carries the stated cap");

		// No number in the message: drop the oldest, and never read the neighbouring "code":400 as the cap.
		MediaBudget unnumbered = new MediaBudget();
		unnumbered.Apply(ImageHistory(6, 100), 0, "text", true);
		int reported = learned.Count;
		ctx.Assert(unnumbered.LearnFromRefusal(TestModel(), 400, "{\"error\":{\"message\":\"Too many images provided\",\"code\":400}}", sink, logger), "MediaBudget: an unnumbered refusal is re-sent");
		JsonArray fewer = ImageHistory(6, 100);
		unnumbered.Apply(fewer, 0, "text", true);
		ctx.AssertEqual(5, ImagesIn(fewer), "MediaBudget: without the oldest");
		ctx.AssertEqual(reported, learned.Count, "MediaBudget: a derived cap is not persisted as a model fact");

		MediaBudget vllm = new MediaBudget();
		vllm.Apply(ImageHistory(3, 100), 0, "text", true);
		vllm.LearnFromRefusal(TestModel(), 400, "{\"message\":\"At most 1 image(s) may be provided in one request.\"}", sink, logger);
		ctx.AssertEqual(1, learned[learned.Count - 1].MaxImages ?? 0, "MediaBudget: vLLM's wording is understood");

		MediaBudget unrelated = new MediaBudget();
		unrelated.Apply(ImageHistory(3, 100), 0, "text", true);
		ctx.Assert(!unrelated.LearnFromRefusal(TestModel(), 400, "{\"error\":{\"message\":\"Invalid tool schema\"}}", sink, logger), "MediaBudget: an unrelated 400 is not a media refusal");
		ctx.Assert(!unrelated.LearnFromRefusal(TestModel(), 404, "{\"error\":{\"message\":\"No endpoints found that support image input\"}}", sink, logger), "MediaBudget: a model that takes no images is the human's call, not trimmed away");
	}

	private static void TestMediaBudgetDropsOldest(TestContext ctx)
	{
		ctx.Log("    MediaBudgetDropsOldest");
		SessionLogger logger = new SessionLogger("media-budget-test");
		MediaBudget   budget = new MediaBudget();
		string        tooBig = "{\"error\":{\"message\":\"Downloaded image content cannot exceed 30MB\",\"code\":413}}";

		// Nothing is trimmed up front: the provider decides what is too much.
		JsonArray first = ImageHistory(10, 100);
		budget.Apply(first, 0, "text", true);
		ctx.AssertEqual(10, ImagesIn(first), "MediaBudget: everything unanswered is sent until refused");

		ctx.Assert(budget.LearnFromRefusal(TestModel(), 413, tooBig, null, logger), "MediaBudget: a size refusal is re-sent");
		JsonArray second = ImageHistory(10, 100);
		budget.Apply(second, 0, "text", true);
		ctx.AssertEqual(9, ImagesIn(second), "MediaBudget: without the oldest");

		// The learned cap holds, so a request with more media is trimmed without another refusal.
		JsonArray later = ImageHistory(12, 100);
		budget.Apply(later, 0, "text", true);
		ctx.AssertEqual(9, ImagesIn(later), "MediaBudget: the cap holds for later requests");

		// Down to one, the refusal is not about how many: it goes to the ordinary failure path.
		MediaBudget single = new MediaBudget();
		single.Apply(ImageHistory(1, 100), 0, "text", true);
		ctx.Assert(!single.LearnFromRefusal(TestModel(), 413, tooBig, null, logger), "MediaBudget: a lone item is never trimmed to nothing");

		MediaBudget textOnly = new MediaBudget();
		textOnly.Apply(new JsonArray(), 0, "text", true);
		ctx.Assert(!textOnly.LearnFromRefusal(TestModel(), 413, "{\"error\":\"too big\"}", null, logger), "MediaBudget: a 413 on a request with no media is not ours to fix");
	}

	private static string Role(JsonNode? msg)
	{
		return msg!["role"]!.GetValue<string>();
	}

	private static LlmModel TestModel()
	{
		return new LlmModel("test", "http://localhost", string.Empty, new List<JsonObject>(), new List<JsonObject>(), new ModelConfig());
	}
}