using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


// Backs one agent's read_file tool. Every read goes through FileTools.ReadFileAsync, so the long-line
// truncation always applies. The first time the agent reads a given file, a larger, line-numbered window is
// handed to a throwaway Explorer sub-session along with the caller's goal, and only the Explorer's citations
// (file, line, line count) come back — mirroring how WebFetch filters a page through the Web role. Later
// reads return the window raw at the caller's request. One instance per agent: an agent's reads never depend
// on another's.
public class ReadFileExplorer
{
	// A subsequent (raw) read is capped tighter so it cannot flood the calling agent's context; the first
	// read goes to the Explorer, which digests a larger window since callers often want the whole file.
	private const int RawMaxLines = 500;
	private const int ExploreMaxLines = 2000;

	// The Explorer digests one file window and returns its citations; a working turn at most before finishing.
	private const int MaxTurns = 2;

	// The full paths this agent has already read. A turn's tool calls run in parallel, so TryAdd is the
	// first-read decision: it returns true exactly once per path even under concurrent reads.
	private readonly ConcurrentDictionary<string, byte> _read = new(StringComparer.OrdinalIgnoreCase);

	// Entry point for the read_file tool. The first read of a file reads a larger, line-numbered window and
	// routes it through the Explorer role, returning its citations; subsequent reads honor the caller's
	// offset/lines and return the window raw. The goal is required and only used for the first read.
	public async Task<ToolResult> ReadAsync(
		string toolCallId,
		string filePath,
		string offset,
		string lines,
		string goal,
		LlmRegistry registry,
		RoleService roleService,
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

		// Already read this file: return the raw window the caller asked for, fit to its budget.
		if (_read.ContainsKey(fullPath))
		{
			ToolResult subsequent = await FileTools.ReadFileAsync(toolCallId, filePath, offset, lines, RawMaxLines, false, cancellationToken);
			return ToolDispatch.MeasureRawResult(subsequent, maxOutputTokens);
		}

		// First read: read a larger, line-numbered window from the caller's offset (ignoring the caller's
		// line count so the Explorer can be more helpful), and let the Explorer cite the relevant parts. A
		// failed read is returned as-is and not counted, so it can be retried as a first read.
		ToolResult raw = await FileTools.ReadFileAsync(toolCallId, filePath, offset, string.Empty, ExploreMaxLines, true, cancellationToken);
		if (raw.ExitCode != 0)
			return ToolDispatch.MeasureRawResult(raw, maxOutputTokens);

		_read.TryAdd(fullPath, 0);
		return await ExploreAsync(toolCallId, filePath, raw.StdOut, goal, registry, roleService, transport, parent, maxOutputTokens, cancellationToken);
	}

	// First read of the file: interpret the line-numbered window with the Explorer role, which returns its
	// citations (file, line, line count) via return_to_caller. Cost rolls up into the calling session.
	private async Task<ToolResult> ExploreAsync(
		string toolCallId,
		string filePath,
		string content,
		string goal,
		LlmRegistry registry,
		RoleService roleService,
		ITransportServer transport,
		Session parent,
		int maxOutputTokens,
		CancellationToken cancellationToken)
	{
		Role? explorerRole = roleService.GetRole("Explorer");
		if (explorerRole == null)
			return new ToolResult(toolCallId, string.Empty, "Error: Explorer role is not defined.", 1, 0);

		LlmService? service = registry.CreateService(explorerRole, string.Empty, 0);
		if (service == null)
			return new ToolResult(toolCallId, string.Empty, "Error: no model available for the Explorer role.", 1, 0);

		// The left-margin line numbers let the Explorer cite exact locations against the goal.
		string seed = $"Goal: {goal}\nFile: {filePath}\n\nFile content (line numbers in the left margin):\n{content}";
		(bool ok, string answer, int tokens) = await HelperSession.RunAsync(parent, explorerRole, service, "read_file", seed, MaxTurns, maxOutputTokens, transport, cancellationToken);
		if (!ok)
			return new ToolResult(toolCallId, string.Empty, "Error: the Explorer role failed to interpret " + filePath, 1, 0);

		return new ToolResult(toolCallId, answer, string.Empty, 0, Math.Max(1, tokens));
	}
}
