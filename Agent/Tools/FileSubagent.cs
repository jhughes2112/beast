using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


// Backs the file_subagent tool: one file and the caller's request go to a FileSubagent job on the Summarizer's
// models, which reads the file with read-only tools and answers through return_to_caller. The caller gets that
// answer and nothing else, so a large file never enters its context. The job is seeded with the start of the
// file, so a short file or a question about its head needs no further reads.
public static class FileSubagent
{
	public const string RoleName = "FileSubagent";

	// Turns the job gets: enough to search and read a few windows, few enough that it cannot wander.
	public const int MaxTurns = 6;

	// Lines of the file in the seed.
	private const int SeedLines = 300;

	public static async Task<ToolResult> AskAsync(
		string            toolCallId,
		string            filePath,
		string            prompt,
		Role              job,
		SpawnSummarizer   spawn,
		CancellationToken cancellationToken)
	{
		ToolResult result;
		if (string.IsNullOrWhiteSpace(prompt))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: prompt cannot be empty: say what to do with the file and what to return.", 1, 0);
		}
		else
		{
			ToolResult head = await FileTools.ReadFileAsync(toolCallId, filePath, string.Empty, string.Empty, SeedLines, cancellationToken);
			if (head.ExitCode != 0)
			{
				result = head;
			}
			else if (head.StdOut.Contains('\0'))
			{
				result = new ToolResult(toolCallId, string.Empty, $"Error: {filePath} is binary.", 1, 0);
			}
			else
			{
				(string? confined, string _) = Sandbox.Confine(filePath, false, false);
				FileInfo info                = new FileInfo(confined ?? filePath);
				int      lines               = CountLines(await File.ReadAllTextAsync(info.FullName, cancellationToken));
				string   seed                = $"Request: {prompt}\n\nFile: {filePath} ({info.Length} bytes, {lines} lines)\nStart of the file:\n{head.StdOut}";

				(bool ok, string answer, int tokens) = await spawn(job.Name, $"file_subagent {filePath}", job.SystemPrompt, seed, job.Tools, job.MaxTurns, cancellationToken);
				result = ok
					? new ToolResult(toolCallId, answer, string.Empty, 0, Math.Max(1, tokens))
					: new ToolResult(toolCallId, answer, $"The helper did not finish{(answer.Length > 0 ? "; what it returned is above" : ": no answer was returned")}.", 1, Math.Max(1, tokens));
			}
		}
		return result;
	}

	private static int CountLines(string text)
	{
		int count = text.Length > 0 ? 1 : 0;
		foreach (char c in text)
		{
			if (c == '\n')
				count++;
		}
		if (text.EndsWith('\n'))
			count--;
		return count;
	}
}