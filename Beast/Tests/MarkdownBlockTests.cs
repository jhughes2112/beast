using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;


// Tests for MarkdownBlock, the one renderer for markdown messages: a message pushed in pieces must render
// exactly as the same text pushed whole, at every step, and a push must cost the open block rather than the
// whole message.
public static class MarkdownBlockTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  MarkdownBlockTests");
		TestEveryPrefixMatchesWholePush(ctx);
		TestRandomChunksMatchWholePush(ctx);
		TestPushCostStaysFlat(ctx);
		TestFollowsAppendsNotRewrites(ctx);
	}

	// A render keeps following a message that is only appended to, and lets go of one that is rewritten —
	// except when the "rewrite" is the same text again (the committed copy of a finished stream).
	private static void TestFollowsAppendsNotRewrites(TestContext ctx)
	{
		DisplayMessage msg   = new DisplayMessage(0, FrameType.Output, "Hello");
		MarkdownBlock  block = new MarkdownBlock(msg, 80);
		block.Build(msg);

		msg.Append(" world");
		ctx.Assert( block.Follows(msg, 80), "MarkdownBlock: follows a message that was appended to");
		ctx.Assert(!block.Follows(msg, 81), "MarkdownBlock: a new width starts a fresh render");

		msg.Content = "Hello world";
		ctx.Assert(block.Follows(msg, 80), "MarkdownBlock: re-setting the same text is not a rewrite");

		msg.Content = "Goodbye";
		ctx.Assert(!block.Follows(msg, 80), "MarkdownBlock: a rewritten message starts a fresh render");
	}

	private static List<(string Name, string Text)> Documents()
	{
		List<(string Name, string Text)> docs = new List<(string Name, string Text)>();
		docs.Add(("prose", "First paragraph of thought.\nIt continues on a second line.\n\nA second paragraph that is long enough to wrap across the narrow width at least once or twice.\n\nThird."));
		docs.Add(("headings", "# Title\n\nIntro under the title.\n\n## Sub\n\nText under sub.\n\n### Deep\n\nMore.\n\n## Back\n\nEnd."));
		docs.Add(("setext", "Para line\n---\n\nNext para\n===\n\nText after."));
		docs.Add(("lists", "- a\n- b\n  continued\n\n- c loose\n\n1. one\n2. two\n\nPara after."));
		docs.Add(("fence", "Text before.\n\n```csharp\nint x = 1;\n\nint y = 2;\n```\n\nAfter the code."));
		docs.Add(("wideFence", "Intro.\n\n```\nshort\nthis code line runs well past the narrow viewport so the block has to widen while it streams in\n```\n\nAfter."));
		docs.Add(("openFence", "Text before.\n\n```\nnever closed\nstill code"));
		docs.Add(("table", "| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n\nAfter the table."));
		docs.Add(("quote", "> quoted\n> more\nlazy\n\nPlain."));
		docs.Add(("inline", "**bold** and *it* and `code`\n\n---\n\n\tindented code with a tab\n\nEnd."));
		docs.Add(("wide", "\"禅の庭 · Voxel Tea Garden\" ✅\n\n```\n禅の庭\n```"));
		docs.Add(("blankEdges", "\n\n  lead\n\npara\n\n\n"));
		return docs;
	}

	// Char-by-char, checked after every push: the live view is what the same text pushed whole shows.
	private static void TestEveryPrefixMatchesWholePush(TestContext ctx)
	{
		foreach ((string name, string text) in Documents())
		{
			foreach (FrameType type in new FrameType[] { FrameType.Thinking, FrameType.Output, FrameType.User })
			{
				foreach (int w in new int[] { 30, 100 })
				{
					DisplayMessage msg      = new DisplayMessage(0, type, "");
					MarkdownBlock  block    = new MarkdownBlock(msg, w);
					string         mismatch = "";
					for (int len = 1; len <= text.Length && mismatch.Length == 0; len++)
					{
						msg.Append(text.Substring(len - 1, 1));
						mismatch = Compare(block.Build(msg), msg, w);
						if (mismatch.Length > 0)
							mismatch = $"after {len} chars: {mismatch}";
					}
					ctx.Assert(mismatch.Length == 0, $"MarkdownBlock: {name} {type} w={w} every prefix — {mismatch}");
				}
			}
		}
	}

	// Random chunk sizes, as a real stream delivers them, checked at the end.
	private static void TestRandomChunksMatchWholePush(TestContext ctx)
	{
		foreach ((string name, string text) in Documents())
		{
			for (int seed = 1; seed <= 5; seed++)
			{
				Random         rng   = new Random(seed);
				DisplayMessage msg   = new DisplayMessage(0, FrameType.Thinking, "");
				MarkdownBlock  block = new MarkdownBlock(msg, 60);
				BlockLayer?    layer = null;
				int            pos   = 0;
				while (pos < text.Length)
				{
					int next = Math.Min(text.Length, pos + rng.Next(1, 25));
					msg.Append(text.Substring(pos, next - pos));
					pos   = next;
					layer = block.Build(msg);
				}
				string mismatch = Compare(layer!, msg, 60);
				ctx.Assert(mismatch.Length == 0, $"MarkdownBlock: {name} seed={seed} random chunks — {mismatch}");
			}
		}
	}

	// A ~90k-character thinking stream pushed a few characters at a time: the late pushes, deep into the
	// message, must cost about what the early ones did.
	private static void TestPushCostStaysFlat(TestContext ctx)
	{
		string text = LongThinking(90000);
		const int Chunk  = 4;
		const int Sample = 2000;

		DisplayMessage msg        = new DisplayMessage(0, FrameType.Thinking, "");
		MarkdownBlock  block      = new MarkdownBlock(msg, 120);
		int            pushes     = (text.Length + Chunk - 1) / Chunk;
		long           firstTicks = 0;
		long           lastTicks  = 0;
		Stopwatch      total      = Stopwatch.StartNew();
		for (int i = 0; i < pushes; i++)
		{
			long start = Stopwatch.GetTimestamp();
			msg.Append(text.Substring(i * Chunk, Math.Min(Chunk, text.Length - i * Chunk)));
			block.Build(msg);
			long spent = Stopwatch.GetTimestamp() - start;
			if (i < Sample)
				firstTicks += spent;
			else if (i >= pushes - Sample)
				lastTicks += spent;
		}
		total.Stop();

		Stopwatch      whole    = Stopwatch.StartNew();
		DisplayMessage wholeMsg = new DisplayMessage(0, FrameType.Thinking, text);
		new MarkdownBlock(wholeMsg, 120).Build(wholeMsg);
		whole.Stop();

		double firstMs = firstTicks * 1000.0 / Stopwatch.Frequency;
		double lastMs  = lastTicks * 1000.0 / Stopwatch.Frequency;
		ctx.Log($"    {pushes} pushes over {text.Length} chars: {total.ElapsedMilliseconds} ms total; first {Sample} {firstMs:F1} ms, last {Sample} {lastMs:F1} ms; one whole push {whole.Elapsed.TotalMilliseconds:F1} ms");
		ctx.Assert(lastMs < firstMs * 3 + 50, $"MarkdownBlock: push cost stays flat — first {firstMs:F1} ms, last {lastMs:F1} ms");
		ctx.Assert(Compare(block.Build(msg), msg, 120).Length == 0, "MarkdownBlock: long stream matches whole push");
	}

	// Paragraph-heavy text with the occasional heading and list, the shape an overthinking model produces.
	private static string LongThinking(int length)
	{
		StringBuilder sb = new StringBuilder();
		int           n  = 0;
		while (sb.Length < length)
		{
			if (n % 15 == 0)
				sb.Append($"## Step {n / 15}\n\n");
			if (n % 7 == 3)
				sb.Append("- check the first thing\n- then the second thing\n- and finally the third\n\n");
			sb.Append($"Paragraph {n}: Wait, let me reconsider. The user wants the answer, but I should verify the **assumption** about `the input` first. ");
			sb.Append("If the value is negative then the earlier reasoning does not hold, so I need to go back and look at it again more carefully.\n");
			sb.Append("Actually, that seems right. Let me continue with the next part of the problem and see whether it holds up.\n\n");
			n++;
		}
		return sb.ToString();
	}

	// Empty when the pushed layer shows what the same text pushed whole shows, in both views; otherwise the
	// first difference.
	private static string Compare(BlockLayer streamed, DisplayMessage msg, int w)
	{
		DisplayMessage whole  = new DisplayMessage(0, msg.Type, msg.Content);
		BlockLayer     want   = new MarkdownBlock(whole, w).Build(whole);
		string         result = CompareScreens("expanded", streamed.Expanded, want.Expanded);
		if (result.Length == 0)
			result = CompareScreens("collapsed", streamed.Collapsed, want.Collapsed);
		return result;
	}

	private static string CompareScreens(string view, Screen got, Screen want)
	{
		string result = "";
		if (got.H != want.H || got.W != want.W)
			result = $"{view} size {got.W}x{got.H}, want {want.W}x{want.H}; first row '{RowText(got, 0)}' want '{RowText(want, 0)}'";
		for (int y = 0; y < want.H && result.Length == 0; y++)
		{
			for (int x = 0; x < want.W && result.Length == 0; x++)
			{
				if (!SameCell(got.Get(x, y), want.Get(x, y)))
					result = $"{view} row {y} col {x}: '{RowText(got, y)}' want '{RowText(want, y)}'";
			}
		}
		return result;
	}

	private static bool SameCell(Cell a, Cell b)
	{
		return a.Ch == b.Ch && a.Style == b.Style && a.Fg.Equals(b.Fg) && a.Bg.Equals(b.Bg);
	}

	private static string RowText(Screen s, int y)
	{
		StringBuilder sb = new StringBuilder();
		for (int x = 0; x < s.W; x++)
			sb.Append(s.Get(x, y).Ch);
		return sb.ToString().TrimEnd();
	}
}