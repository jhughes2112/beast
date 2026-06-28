using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


// Backs the find_relevant_file_sections tool. Reads a larger, line-numbered window of a file and routes it through the
// Explorer role together with the caller's goal, returning only the Explorer's citations (file, line, line
// count) — mirroring how WebFetch filters a page through the WebFetch role. A small file is handed back whole
// instead, since digesting it costs more than just returning it. Stateless: every call summarizes, so the
// caller's plain read_file and this tool never interfere with one another.
public class FileSummarizer
{
	// The window digested by the Explorer. Larger than a raw read_file window since callers usually want a
	// concept map of the whole file.
	private const int ExploreMaxLines = 2000;

	// A file this small is returned whole instead of routed through the Explorer: digesting it costs more
	// than just handing it over. Either threshold (lines or bytes) being met is enough.
	private const int SmallFileMaxLines = 50;
	private const int SmallFileMaxBytes = 2048;

	// The Explorer digests one file window and returns its citations via return_to_caller. It has no tools to
	// work with, so there is nothing to do but cite, but flaky models/servers do not always emit the tool call
	// on the first try: HelperSession cycles the tool_choice constraint across these turns and, failing that,
	// salvages the last assistant message, so a few turns are allotted rather than a single forced one.
	private const int MaxTurns = 5;

	// Entry point for the find_relevant_file_sections tool. Reads a line-numbered window from the caller's offset and lets
	// the Explorer cite the parts relevant to the goal. A small file (or a failed read) is returned raw.
	// explorerRole and explorerService are pre-resolved by BuildForRole; registry is still passed for runtime fallback.
	public async Task<ToolResult> SummarizeAsync(
		string toolCallId,
		string filePath,
		string offset,
		string goal,
		Role explorerRole,
		LlmRegistry registry,
		ITransportServer transport,
		Session parent,
		int maxOutputTokens,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(filePath))
			return new ToolResult(toolCallId, string.Empty, "Error: Path cannot be empty", 1, 0);

		if (string.IsNullOrWhiteSpace(goal))
			return new ToolResult(toolCallId, string.Empty, "Error: goal cannot be empty", 1, 0);

		string fullPath = Path.GetFullPath(filePath);

		// Read a larger, line-numbered window from the caller's offset (ignoring any line count so the Explorer
		// can be more helpful). A failed read is returned as-is so the caller sees the error.
		ToolResult raw = await FileTools.ReadFileAsync(toolCallId, filePath, offset, string.Empty, ExploreMaxLines, true, cancellationToken);
		if (raw.ExitCode != 0)
			return ToolDispatch.MeasureRawResult(raw, maxOutputTokens);

		// A small file is cheaper handed over whole than digested: under either threshold, return the
		// line-numbered window directly with no LLM round-trip.
		long fileBytes = new FileInfo(fullPath).Length;
		if (fileBytes < SmallFileMaxBytes || CountLines(raw.StdOut) < SmallFileMaxLines)
			return ToolDispatch.MeasureRawResult(raw, maxOutputTokens);

		return await ExploreAsync(toolCallId, filePath, raw.StdOut, goal, explorerRole, registry, transport, parent, maxOutputTokens, cancellationToken);
	}

	// Counts physical lines in already-read content; used only for the small-file threshold.
	private static int CountLines(string text)
	{
		if (string.IsNullOrEmpty(text))
			return 0;

		int count = 1;
		foreach (char c in text)
		{
			if (c == '\n')
				count++;
		}
		return count;
	}

	// Interprets the line-numbered window with the Explorer role, which returns its citations (file, line,
	// line count) via return_to_caller. Cost rolls up into the calling session.
	private async Task<ToolResult> ExploreAsync(
		string toolCallId,
		string filePath,
		string content,
		string goal,
		Role explorerRole,
		LlmRegistry registry,
		ITransportServer transport,
		Session parent,
		int maxOutputTokens,
		CancellationToken cancellationToken)
	{
		// The left-margin line numbers let the Explorer cite exact locations against the goal.
		string seed = $"Goal: {goal}\nFile: {filePath}\n\nFile content (line numbers in the left margin):\n{content}";
		(bool ok, string answer, int tokens) = await HelperSession.RunAsync(parent, explorerRole, registry, $"find_relevant_file_sections {filePath}", seed, MaxTurns, maxOutputTokens, transport, cancellationToken);
		if (!ok)
		{
			string detail = string.IsNullOrEmpty(answer) ? "no reason reported" : answer;
			return new ToolResult(toolCallId, string.Empty, $"Error: the Explorer role failed to interpret {filePath}: {detail}", 1, 0);
		}

		return new ToolResult(toolCallId, answer, string.Empty, 0, Math.Max(1, tokens));
	}
}