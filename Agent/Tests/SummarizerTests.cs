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
		TestChunkBudgetIsModelSized(ctx);
		TestElidesBeforeSummarizing(ctx);
		TestContextOverflowDetection(ctx);
	}

	// The property compaction depends on: a stage's chunk budget is a function of the SUMMARIZING
	// MODEL's window and nothing else. It does not shrink as the conversation grows, and it does not
	// shrink as the running summary accumulates — the summary is charged at a fixed allowance the
	// summarizer then keeps it inside. So "the context got too big to compact" cannot happen; a
	// bigger conversation only means more stages.
	private static void TestChunkBudgetIsModelSized(TestContext ctx)
	{
		int[] windows = new int[] { 4096, 8192, 32768, 131072, 1000000 };
		foreach (int window in windows)
		{
			LlmService service = BuildServiceWithWindow(window);

			int budget = Summarizer.ChunkCharBudget(service, 400);
			ctx.Assert(budget >= 256, $"ChunkCharBudget: a {window}-token model gets a usable chunk ({budget} chars)");

			// At most 75% of the window may be transcript, so the rest of the stage — its output,
			// its scaffolding, and the running summary it carries — always has somewhere to live.
			int ceiling = (int)((long)window * 75 / 100 * 3);
			ctx.Assert(budget <= ceiling, $"ChunkCharBudget: a {window}-token model keeps the chunk within its share of the window ({budget} <= {ceiling})");

			// The room reserved for the running summary is real room, not a hope.
			ctx.Assert(Summarizer.RunningSummaryAllowance(service) > 0, $"ChunkCharBudget: a {window}-token model reserves space for the running summary");
		}
	}

	// Summarization folds the MECHANICALLY ELIDED history, not the raw one. The two summarize to the
	// same thing — a 40k-char file dump and the one-line note naming the file it came from say the
	// same thing about what happened — but the elided transcript is a fraction of the size, so it
	// costs a fraction of the stages and the provider spend to get there. The recent turns the
	// elision protects still arrive verbatim, so nothing fresh is summarized away.
	private static void TestElidesBeforeSummarizing(TestContext ctx)
	{
		List<CanonicalMessage> messages = new List<CanonicalMessage>();
		messages.Add(new UserMessage("Find where the retry logic lives."));
		for (int i = 0; i < 12; i++)
		{
			string id = $"call{i}";
			messages.Add(new AssistantMessage($"Reading file {i}.", string.Empty,
				new List<SemanticToolCall> { new SemanticToolCall { Id = id, Name = "read_file", ArgumentsJson = $"{{\"file_path\":\"src/file{i}.cs\"}}" } }));
			messages.Add(new ToolResultMessage(id, new string('x', 40000)));
		}

		int raw    = TranscriptChars(Summarizer.RenderTranscript(messages));
		int elided = TranscriptChars(Summarizer.RenderTranscript(MechanicalCompaction.Elide(messages, string.Empty)));

		// Twelve 40k results collapse to their notes; what is left is essentially the protected tail.
		ctx.Assert(elided * 3 < raw, $"Elide-before-summarize: the transcript sent to the model shrinks dramatically ({raw} -> {elided} chars)");

		// What the elision keeps, it keeps intact: the user's own words and the newest turns.
		List<string> blocks = Summarizer.RenderTranscript(MechanicalCompaction.Elide(messages, string.Empty));
		ctx.AssertContains(               blocks[0], "Find where the retry logic lives.", "Elide-before-summarize: user text survives verbatim");
		ctx.AssertContains(blocks[blocks.Count - 1],              new string('x', 40000), "Elide-before-summarize: the most recent tool result is not elided");
	}

	private static int TranscriptChars(List<string> blocks)
	{
		int total = 0;
		foreach (string block in blocks)
			total += block.Length;
		return total;
	}

	private static LlmService BuildServiceWithWindow(int window)
	{
		ModelConfig config = new ModelConfig
		{
			Id            = "budget-probe",
			Name          = "Budget Probe",
			ContextWindow = window,
		};
		LlmModel model = new LlmModel("budget-probe", "http://localhost/v1/chat/completions", "k",
			new List<System.Text.Json.Nodes.JsonObject>(), new List<System.Text.Json.Nodes.JsonObject>(), config);
		return new LlmService(model, DetectedProtocol.ChatCompletions, new ModelAvailability(), new List<string> { "budget-probe" }, (id, effort, summaries) => { });
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
		ctx.AssertContains(blocks[0], "[user]\nhello",                    "RenderTranscript: user block");
		ctx.AssertContains(blocks[1], "[assistant]\nhi there",            "RenderTranscript: assistant text");
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
		ctx.AssertEqual("aaaabbbb",      chunk, "BuildChunk: packs whole blocks to the budget");
		ctx.AssertEqual(         2,  nextIndex, "BuildChunk: next index after packed blocks");
		ctx.AssertEqual(         0, nextOffset, "BuildChunk: no offset at a block boundary");

		// The follow-up call picks up exactly where the first left off and drains the rest.
		(string rest, int endIndex, int endOffset) = Summarizer.BuildChunk(blocks, nextIndex, nextOffset, 100);
		ctx.AssertEqual("cccc",      rest, "BuildChunk: continuation drains the remainder");
		ctx.AssertEqual(     3,  endIndex, "BuildChunk: end index past the last block");
		ctx.AssertEqual(     0, endOffset, "BuildChunk: end offset zero when fully consumed");
	}

	private static void TestBuildChunkSplitsOversizedBlock(TestContext ctx)
	{
		// One block far larger than the budget: it must flow through in budget-sized pieces and
		// concatenate back to the original content.
		List<string>  blocks      = new List<string> { new string('x', 25) };
		StringBuilder reassembled = new StringBuilder();
		int           index       = 0;
		int           offset      = 0;
		int           pieces      = 0;

		while (index < blocks.Count)
		{
			(string chunk, int nextIndex, int nextOffset) = Summarizer.BuildChunk(blocks, index, offset, 10);
			reassembled.Append(chunk);
			index  = nextIndex;
			offset = nextOffset;
			pieces++;
		}

		ctx.AssertEqual(        3,                 pieces, "BuildChunk: oversized block splits into ceil(size/budget) pieces");
		ctx.AssertEqual(blocks[0], reassembled.ToString(), "BuildChunk: split pieces reassemble losslessly");
	}

	private static void TestBuildStagePrompt(TestContext ctx)
	{
		// Single-stage: the whole transcript with the role's summary prompt applied directly.
		string only = Summarizer.BuildStagePrompt(string.Empty, "the transcript", "SUMMARize now", true, true);
		ctx.AssertContains(only, "complete transcript", "BuildStagePrompt: single stage framing");
		ctx.AssertContains(only, "the transcript",      "BuildStagePrompt: single stage carries the chunk");
		ctx.AssertContains(only, "SUMMARize now",       "BuildStagePrompt: single stage carries the role prompt");

		// Intermediate stage: running summary plus an update-only instruction, no role prompt.
		string middle = Summarizer.BuildStagePrompt("so far so good", "segment two", "SUMMARize now", false, false);
		ctx.AssertContains(middle, "so far so good",                   "BuildStagePrompt: intermediate carries the running summary");
		ctx.AssertContains(middle, "segment two",                      "BuildStagePrompt: intermediate carries the chunk");
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
		ctx.Assert( ProtocolHelpers.IsOverflowStatusCandidate(400), "IsOverflowStatusCandidate: 400");
		ctx.Assert( ProtocolHelpers.IsOverflowStatusCandidate(413), "IsOverflowStatusCandidate: 413");
		ctx.Assert( ProtocolHelpers.IsOverflowStatusCandidate(422), "IsOverflowStatusCandidate: 422");
		ctx.Assert(!ProtocolHelpers.IsOverflowStatusCandidate(401), "IsOverflowStatusCandidate: 401 excluded");
		ctx.Assert(!ProtocolHelpers.IsOverflowStatusCandidate(404), "IsOverflowStatusCandidate: 404 excluded");
		ctx.Assert(  !ProtocolHelpers.IsOverflowStatusCandidate(0), "IsOverflowStatusCandidate: no status excluded");

		// Account rejections arrive as the same 400 the structural signal treats as overflow, and
		// the body is the only thing that tells them apart. Missing one costs a compact-and-retry
		// spin against the provider, so the real wire texts are pinned here verbatim.
		ctx.Assert(ProtocolHelpers.IsAccountError("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits.\"}}"),
			"IsAccountError: Anthropic out-of-credit");
		ctx.Assert(ProtocolHelpers.IsAccountError("{\"error\":{\"message\":\"You exceeded your current quota, please check your plan and billing details.\",\"type\":\"insufficient_quota\"}}"),
			"IsAccountError: OpenAI insufficient quota");
		ctx.Assert(ProtocolHelpers.IsAccountError("{\"error\":{\"message\":\"Incorrect API key provided\",\"code\":\"invalid_api_key\"}}"),
			"IsAccountError: bad key");

		// The separation must hold both ways: a real overflow must never be read as an account
		// problem (it would stop compacting), and an account error must never be read as overflow.
		ctx.Assert(!ProtocolHelpers.IsAccountError("prompt is too long: 210000 tokens > 200000 maximum"),
			"IsAccountError: Anthropic overflow is not an account error");
		ctx.Assert(!ProtocolHelpers.IsAccountError("This model's maximum context length is 32768 tokens."),
			"IsAccountError: OpenAI overflow is not an account error");
		ctx.Assert(!ProtocolHelpers.IsContextOverflow("Your credit balance is too low to access the Anthropic API."),
			"IsContextOverflow: out-of-credit is not an overflow");
	}
}