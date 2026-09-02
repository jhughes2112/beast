using System;
using System.Collections.Generic;
using System.IO;
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

		try
		{
			TestCanonicalKeepsPathOnly(ctx, path);
			TestChatCompletionsCarrier(ctx, path, expected);
			TestAnthropicToolResultBlock(ctx, path, expected);
			TestResponsesOutputParts(ctx, path, expected);
			TestMissingFileDegradesToNote(ctx);
		}
		finally
		{
			File.Delete(path);
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
		bundle.OnToolResult(new ToolResult("call_a", "a.png attached", string.Empty, 0, 3, path, "image/png"));
		bundle.OnToolResult(new ToolResult("call_b", "a.png\nb.txt", string.Empty, 0, 3));
		return messages;
	}

	private static void TestCanonicalKeepsPathOnly(TestContext ctx, string path)
	{
		ctx.Log("    CanonicalKeepsPathOnly");
		List<CanonicalMessage> messages = Conversation(path);
		ToolResultMessage      tr       = (ToolResultMessage)messages[2];
		ctx.AssertEqual(       path,     tr.MediaPath!, "Canonical: tool result records the media path");
		ctx.AssertEqual("image/png", tr.MediaMimeType!, "Canonical: tool result records the mime type");
		ctx.Assert(((ToolResultMessage)messages[3]).MediaPath == null, "Canonical: a text result carries no media");
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
		liveCc.OnToolResult(new ToolResult("call_a", "a.png attached", string.Empty, 0, 3, path, "image/png"));
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

	private static string Role(JsonNode? msg)
	{
		return msg!["role"]!.GetValue<string>();
	}

	private static LlmModel TestModel()
	{
		return new LlmModel("test", "http://localhost", string.Empty, new List<JsonObject>(), new List<JsonObject>(), new ModelConfig());
	}
}