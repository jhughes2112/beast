using System.Collections.Generic;
using System.Text;


// Unit tests for the staged-compaction building blocks: transcript rendering, chunk assembly
// (including mid-block splits), stage prompt construction, and the context-overflow phrasings
// that route provider rejections into compaction instead of model fallback.
public static class SummarizerTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  SummarizerTests");

		TestRenderTranscript(ctx);
		TestBuildChunk(ctx);
		TestBuildChunkSplitsOversizedBlock(ctx);
		TestBuildStagePrompt(ctx);
		TestContextOverflowDetection(ctx);
	}

	private static void TestRenderTranscript(TestContext ctx)
	{
		List<CanonicalMessage> messages = new List<CanonicalMessage>
		{
			new SystemMessage("system prompt"),
			new UserMessage("hello"),
			new AssistantMessage("hi there", "secret thinking", new List<SemanticToolCall> { new SemanticToolCall { Id = "id1", Name = "read_file", ArgumentsJson = "{\"path\":\"a.cs\"}" } }),
			new ToolResultMessage("id1", "file contents"),
			new AssistantMessage("", "", null),
			new UserMessage("   ")
		};

		List<string> blocks = Summarizer.RenderTranscript(messages);

		// System prompt, empty assistant turn, and whitespace user message are all skipped.
		ctx.AssertEqual(3, blocks.Count, "RenderTranscript: block count");
		ctx.AssertContains(blocks[0], "[user]\nhello", "RenderTranscript: user block");
		ctx.AssertContains(blocks[1], "[assistant]\nhi there", "RenderTranscript: assistant text");
		ctx.AssertContains(blocks[1], "[assistant tool call: read_file]", "RenderTranscript: tool call label");
		ctx.AssertContains(blocks[1], "a.cs", "RenderTranscript: tool call args");
		ctx.Assert(!blocks[1].Contains("secret thinking"), "RenderTranscript: thinking skipped");
		ctx.AssertContains(blocks[2], "[tool result]\nfile contents", "RenderTranscript: tool result block");
	}

	private static void TestBuildChunk(TestContext ctx)
	{
		List<string> blocks = new List<string> { "aaaa", "bbbb", "cccc" };

		// Budget holds the first two whole blocks but not the third.
		(string chunk, int nextIndex, int nextOffset) = Summarizer.BuildChunk(blocks, 0, 0, 8);
		ctx.AssertEqual("aaaabbbb", chunk, "BuildChunk: packs whole blocks to the budget");
		ctx.AssertEqual(2, nextIndex, "BuildChunk: next index after packed blocks");
		ctx.AssertEqual(0, nextOffset, "BuildChunk: no offset at a block boundary");

		// The follow-up call picks up exactly where the first left off and drains the rest.
		(string rest, int endIndex, int endOffset) = Summarizer.BuildChunk(blocks, nextIndex, nextOffset, 100);
		ctx.AssertEqual("cccc", rest, "BuildChunk: continuation drains the remainder");
		ctx.AssertEqual(3, endIndex, "BuildChunk: end index past the last block");
		ctx.AssertEqual(0, endOffset, "BuildChunk: end offset zero when fully consumed");
	}

	private static void TestBuildChunkSplitsOversizedBlock(TestContext ctx)
	{
		// One block far larger than the budget: it must flow through in budget-sized pieces and
		// concatenate back to the original content.
		List<string> blocks = new List<string> { new string('x', 25) };
		StringBuilder reassembled = new StringBuilder();
		int index = 0;
		int offset = 0;
		int pieces = 0;

		while (index < blocks.Count)
		{
			(string chunk, int nextIndex, int nextOffset) = Summarizer.BuildChunk(blocks, index, offset, 10);
			reassembled.Append(chunk);
			index = nextIndex;
			offset = nextOffset;
			pieces++;
		}

		ctx.AssertEqual(3, pieces, "BuildChunk: oversized block splits into ceil(size/budget) pieces");
		ctx.AssertEqual(blocks[0], reassembled.ToString(), "BuildChunk: split pieces reassemble losslessly");
	}

	private static void TestBuildStagePrompt(TestContext ctx)
	{
		// Single-stage: the whole transcript with the role's summary prompt applied directly.
		string only = Summarizer.BuildStagePrompt(string.Empty, "the transcript", "SUMMARize now", true, true);
		ctx.AssertContains(only, "complete transcript", "BuildStagePrompt: single stage framing");
		ctx.AssertContains(only, "the transcript", "BuildStagePrompt: single stage carries the chunk");
		ctx.AssertContains(only, "SUMMARize now", "BuildStagePrompt: single stage carries the role prompt");

		// Intermediate stage: running summary plus an update-only instruction, no role prompt.
		string middle = Summarizer.BuildStagePrompt("so far so good", "segment two", "SUMMARize now", false, false);
		ctx.AssertContains(middle, "so far so good", "BuildStagePrompt: intermediate carries the running summary");
		ctx.AssertContains(middle, "segment two", "BuildStagePrompt: intermediate carries the chunk");
		ctx.AssertContains(middle, "ONLY the updated running summary", "BuildStagePrompt: intermediate asks for an updated summary");
		ctx.Assert(!middle.Contains("SUMMARize now"), "BuildStagePrompt: intermediate omits the role prompt");

		// Final stage of a staged run: running summary plus the role's summary prompt.
		string final = Summarizer.BuildStagePrompt("so far so good", "last segment", "SUMMARize now", true, false);
		ctx.AssertContains(final, "Final segment", "BuildStagePrompt: final stage framing");
		ctx.AssertContains(final, "SUMMARize now", "BuildStagePrompt: final stage carries the role prompt");
	}

	private static void TestContextOverflowDetection(TestContext ctx)
	{
		// llama-server's phrasing — the one that previously fell through to model fallback.
		ctx.Assert(ProtocolHelpers.IsContextOverflow("the request exceeds the available context size. try increasing the context size or enable context shift"),
			"IsContextOverflow: llama-server phrasing");
		ctx.Assert(ProtocolHelpers.IsContextOverflow("This model's maximum context length is 32768 tokens."),
			"IsContextOverflow: OpenAI/vLLM phrasing");
		ctx.Assert(ProtocolHelpers.IsContextOverflow("prompt is too long: 210000 tokens > 200000 maximum"),
			"IsContextOverflow: Anthropic phrasing");
		ctx.Assert(ProtocolHelpers.IsContextOverflow("Input is too long for requested model."),
			"IsContextOverflow: Bedrock phrasing");
		ctx.Assert(!ProtocolHelpers.IsContextOverflow("invalid api key"),
			"IsContextOverflow: unrelated error not matched");

		// Structural signal: statuses providers use for over-window rejections, and never the
		// auth/not-found statuses that must keep routing to the failure path.
		ctx.Assert(ProtocolHelpers.IsOverflowStatusCandidate(400), "IsOverflowStatusCandidate: 400");
		ctx.Assert(ProtocolHelpers.IsOverflowStatusCandidate(413), "IsOverflowStatusCandidate: 413");
		ctx.Assert(ProtocolHelpers.IsOverflowStatusCandidate(422), "IsOverflowStatusCandidate: 422");
		ctx.Assert(!ProtocolHelpers.IsOverflowStatusCandidate(401), "IsOverflowStatusCandidate: 401 excluded");
		ctx.Assert(!ProtocolHelpers.IsOverflowStatusCandidate(404), "IsOverflowStatusCandidate: 404 excluded");
		ctx.Assert(!ProtocolHelpers.IsOverflowStatusCandidate(0), "IsOverflowStatusCandidate: no status excluded");
	}
}
