using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Tools for LLM to read and write files.
public static class FileTools
{
	private static readonly TimeSpan      DefaultTimeout = TimeSpan.FromSeconds(30);
	private static readonly SemaphoreSlim FileLock       = new SemaphoreSlim(1, 1);

	// A single line is never returned longer than this, regardless of caller — a lone enormous (e.g.
	// minified) line would otherwise flood the context. The line count is capped by the caller's maxLines.
	private const int MaxLineLength = 2000;

	public static async Task<ToolResult> ReadFileAsync(
		string            toolCallId,
		string            filePath,
		string            offset,
		string            lines,
		int               maxLines,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		// The resolved path, symlinks followed, is the one read — never the argument itself.
		(string? confined, string denied) = Sandbox.Confine(filePath, false, false);

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: Path cannot be empty", 1, 0);
		}
		else if (confined == null)
		{
			result = new ToolResult(toolCallId, string.Empty, denied, 1, 0);
		}
		else
		{
			string fullPath = confined;

			try
			{
				if (File.Exists(fullPath))
				{
					int  offsetValue = 0;
					bool offsetValid = string.IsNullOrWhiteSpace(offset) || int.TryParse(offset, out offsetValue);
					int  linesValue  = 0;
					bool linesValid  = string.IsNullOrWhiteSpace(lines) || int.TryParse(lines, out linesValue);

					if (offsetValid && linesValid && offsetValue >= 0 && linesValue >= 0)
					{
						using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
						cts.CancelAfter(DefaultTimeout);

						await FileLock.WaitAsync(cts.Token);
						try
						{
							string fileContent = await File.ReadAllTextAsync(fullPath, cts.Token);

							if (fileContent.Length == 0)
							{
								result = new ToolResult(toolCallId, $"File is empty: {filePath}", string.Empty, 0, 0);
							}
							else
							{
								// Windowed read of the file's lines as they are: no line numbers, since no tool edits by them
								// and they would only take up context, and no reflow, so offset always names a real line.
								// The output is always bounded: at most maxLines lines, each cut at MaxLineLength characters.
								// Limits are mentioned, never errors.
								string   text     = fileContent.Replace("\r\n", "\n");
								string[] allLines = text.Split('\n');
								// As grep counts them: a final newline ends the last line rather than starting another.
								int total = text.EndsWith('\n') ? allLines.Length - 1 : allLines.Length;

								int startLine = offsetValue <= 0 ? 1 : offsetValue;
								int startIdx  = startLine - 1;

								if (startIdx >= total)
								{
									result = new ToolResult(toolCallId, string.Empty, $"Offset {startLine} is beyond the end of the file (file has {total} lines).", 1, 0);
								}
								else
								{
									int available = total - startIdx;
									int requested = linesValue > 0 ? Math.Min(linesValue, available) : available;
									int count     = Math.Min(requested, maxLines);

									bool          lineTruncated = false;
									StringBuilder sb            = new StringBuilder();
									for (int i = 0; i < count; i++)
									{
										string line = allLines[startIdx + i];
										if (line.Length > MaxLineLength)
										{
											line          = line.Substring(0, MaxLineLength) + "...truncated";
											lineTruncated = true;
										}
										if (i > 0)
											sb.Append('\n');
										sb.Append(line);
									}

									if (count < requested)
										sb.Append($"\n[Lines {startLine}-{startLine + count - 1} of {total}; at most {maxLines} lines are shown at once.]");
									if (lineTruncated)
										sb.Append($"\n[Lines over {MaxLineLength} characters are cut short and end in \"...truncated\".]");

									result = new ToolResult(toolCallId, sb.ToString(), string.Empty, 0, 0);
								}
							}
						}
						finally
						{
							FileLock.Release();
						}
					}
					else
					{
						if (!offsetValid)
						{
							result = new ToolResult(toolCallId, string.Empty, $"Error: Invalid offset value: {offset}", 1, 0);
						}
						else if (!linesValid)
						{
							result = new ToolResult(toolCallId, string.Empty, $"Error: Invalid lines value: {lines}", 1, 0);
						}
						else if (offsetValue < 0)
						{
							result = new ToolResult(toolCallId, string.Empty, "Error: offset must be >= 0", 1, 0);
						}
						else
						{
							result = new ToolResult(toolCallId, string.Empty, "Error: lines must be >= 0", 1, 0);
						}
					}
				}
				else
				{
					result = new ToolResult(toolCallId, string.Empty, $"Error: File not found: {filePath}", 1, 0);
				}
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult(toolCallId, string.Empty, $"Error: Timed out or cancelled reading file: {filePath}", 1, 0);
			}
			catch (Exception ex)
			{
				result = new ToolResult(toolCallId, string.Empty, $"Error: Failed to read file: {ex}", 1, 0);
			}
		}

		return result;
	}

	public static async Task<ToolResult> EditFileAsync(
		string            toolCallId,
		string            filePath,
		string            oldText,
		string            newText,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		// The resolved path, symlinks followed, is the one edited — never the argument itself.
		(string? confined, string denied) = Sandbox.Confine(filePath, true, false);

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: Path cannot be empty", 1, 0);
		}
		else if (string.IsNullOrEmpty(oldText))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: old_text cannot be empty", 1, 0);
		}
		else if (confined == null)
		{
			result = new ToolResult(toolCallId, string.Empty, denied, 1, 0);
		}
		else
		{
			string fullPath = confined;

			try
			{
				if (File.Exists(fullPath))
				{
					using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					cts.CancelAfter(DefaultTimeout);

					await FileLock.WaitAsync(cts.Token);
					try
					{
						string fileContent = await File.ReadAllTextAsync(fullPath, cts.Token);
						string replacement = newText ?? string.Empty;

						// Exact match first
						int exactIdx = fileContent.IndexOf(oldText, StringComparison.Ordinal);
						if (exactIdx >= 0)
						{
							string newContent = fileContent.Substring(0, exactIdx) + replacement + fileContent.Substring(exactIdx + oldText.Length);
							await WriteTextAsync(fullPath, newContent, cts.Token);
							result = new ToolResult(toolCallId, BuildEditEcho(newContent, exactIdx, oldText, replacement), string.Empty, 0, 0);
						}
						else
						{
							// Fuzzy match: strip all whitespace from old_text and file, find the span,
							// map back to original positions and replace.
							List<int>     posMap              = new List<int>(fileContent.Length);
							StringBuilder strippedFileBuilder = new StringBuilder(fileContent.Length);
							for (int i = 0; i < fileContent.Length; i++)
							{
								if (!char.IsWhiteSpace(fileContent[i]))
								{
									posMap.Add(i);
									strippedFileBuilder.Append(fileContent[i]);
								}
							}
							string strippedFile = strippedFileBuilder.ToString();

							StringBuilder strippedOldBuilder = new StringBuilder(oldText.Length);
							foreach (char c in oldText)
							{
								if (!char.IsWhiteSpace(c))
								{
									strippedOldBuilder.Append(c);
								}
							}
							string strippedOld = strippedOldBuilder.ToString();

							if (strippedOld.Length == 0)
							{
								result = new ToolResult(toolCallId, string.Empty, "Error: old_text contains only whitespace.", 1, 0);
							}
							else
							{
								int matchIdx = strippedFile.IndexOf(strippedOld, StringComparison.Ordinal);
								if (matchIdx >= 0)
								{
									int origStart = posMap[matchIdx];
									int origEnd   = posMap[matchIdx + strippedOld.Length - 1] + 1;
									// Fuzzy match ignores whitespace, so the span actually removed from the file can
									// differ from the model's old_text. Diff against what was really there.
									string removedText = fileContent.Substring(origStart, origEnd - origStart);
									string newContent  = fileContent.Substring(        0,           origStart) + replacement + fileContent.Substring(origEnd);
									await WriteTextAsync(fullPath, newContent, cts.Token);
									result = new ToolResult(toolCallId, BuildEditEcho(newContent, origStart, removedText, replacement), string.Empty, 0, 0);
								}
								else
								{
									result = new ToolResult(toolCallId, string.Empty, "Error: old_text not found in file (exact and whitespace-normalized search both failed).", 1, 0);
								}
							}
						}
					}
					finally
					{
						FileLock.Release();
					}
				}
				else
				{
					result = new ToolResult(toolCallId, string.Empty, $"Error: File not found: {filePath}", 1, 0);
				}
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult(toolCallId, string.Empty, $"Error: Timed out or cancelled editing file: {filePath}", 1, 0);
			}
			catch (Exception ex)
			{
				result = new ToolResult(toolCallId, string.Empty, $"Error: Failed to edit file: {ex}", 1, 0);
			}
		}

		return result;
	}

	// Echoes the edit as a unified diff: a few context lines before, the changed lines, then context
	// after. Lines identical at the top and bottom of the replaced span are folded back into context,
	// and the middle is diffed line-by-line (LCS) so interior lines shared between old and new render
	// as context too — an edit that touches a few scattered lines reads in-situ, interleaved where the
	// changes actually sit, instead of one removed block followed by one added block. This confirms
	// placement (the whitespace-normalized fallback can land on a different region than the model
	// pictured) and reads cleanly both for the human display and the model.
	private static string BuildEditEcho(string newContent, int replaceStart, string oldText, string newText)
	{
		const int contextLines = 3;

		string[] allLines = newContent.Split('\n');
		string[] oldLines = SplitOrEmpty(oldText);
		string[] newLines = SplitOrEmpty(newText);

		// Fold away identical leading/trailing lines of the replaced span.
		int prefix = 0;
		while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
			prefix++;

		int suffix = 0;
		while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
			&& oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix])
			suffix++;

		// Everything before the change is identical in both files, so the first changed line has the
		// same number in each. Numbers track the new file; removed lines carry the number of the
		// position they were removed from.
		int changeStartLine = LineOfIndex(newContent, replaceStart);
		int realStart       = changeStartLine + prefix;
		int removedCount    = oldLines.Length - prefix - suffix;
		int addedCount      = newLines.Length - prefix - suffix;

		int firstBefore = Math.Max(0, realStart - contextLines);
		int afterStart  = realStart + addedCount;
		int lastAfter   = Math.Min(allLines.Length - 1, afterStart - 1 + contextLines);

		StringBuilder sb = new StringBuilder();
		sb.Append($"Edit applied at line {realStart + 1}:\n");

		for (int i = firstBefore; i < realStart; i++)
			AppendDiffLine(sb, i + 1, '|', allLines[i]);

		List<(char Marker, string Text)> ops = DiffLines(oldLines, prefix, removedCount, newLines, prefix, addedCount);
		int newLineNo = realStart;
		foreach ((char marker, string text) in ops)
		{
			if (marker == '-')
			{
				AppendDiffLine(sb, newLineNo + 1, '-', text);
			}
			else
			{
				AppendDiffLine(sb, newLineNo + 1, marker, text);
				newLineNo++;
			}
		}

		for (int i = afterStart; i <= lastAfter; i++)
			AppendDiffLine(sb, i + 1, '|', allLines[i]);

		return sb.ToString();
	}

	// Line-level LCS diff of the changed middle of the replaced span. Lines shared between old and new
	// come back as '|' context; each changed region is grouped as its '-' lines followed by its '+'
	// lines so the display can pair equal-length runs for intra-line highlighting. A span too large for
	// the quadratic LCS table falls back to the plain removed-block/added-block form.
	private static List<(char Marker, string Text)> DiffLines(string[] oldLines, int oldStart, int oldCount, string[] newLines, int newStart, int newCount)
	{
		List<(char Marker, string Text)> ops = new List<(char Marker, string Text)>(oldCount + newCount);

		if ((long)oldCount * newCount > 1_000_000)
		{
			for (int i = 0; i < oldCount; i++)
				ops.Add((Marker: '-', Text: oldLines[oldStart + i]));
			for (int j = 0; j < newCount; j++)
				ops.Add((Marker: '+', Text: newLines[newStart + j]));
		}
		else
		{
			// lcs[i, j] = length of the longest common subsequence of old[i..] and new[j..].
			int[,] lcs = new int[oldCount + 1, newCount + 1];
			for (int i = oldCount - 1; i >= 0; i--)
			{
				for (int j = newCount - 1; j >= 0; j--)
				{
					if (oldLines[oldStart + i] == newLines[newStart + j])
						lcs[i, j] = lcs[i + 1, j + 1] + 1;
					else
						lcs[i, j] = Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
				}
			}

			// Walk forward, buffering '+' lines so each changed region emits its '-' lines first.
			List<(char Marker, string Text)> pendingAdds = new List<(char Marker, string Text)>();
			int a = 0;
			int b = 0;
			while (a < oldCount || b < newCount)
			{
				if (a < oldCount && b < newCount && oldLines[oldStart + a] == newLines[newStart + b])
				{
					ops.AddRange(pendingAdds);
					pendingAdds.Clear();
					ops.Add((Marker: '|', Text: newLines[newStart + b]));
					a++;
					b++;
				}
				else if (b >= newCount || (a < oldCount && lcs[a + 1, b] >= lcs[a, b + 1]))
				{
					ops.Add((Marker: '-', Text: oldLines[oldStart + a]));
					a++;
				}
				else
				{
					pendingAdds.Add((Marker: '+', Text: newLines[newStart + b]));
					b++;
				}
			}
			ops.AddRange(pendingAdds);
		}

		return ops;
	}

	// Splits into lines, treating empty input as no lines (so a pure deletion adds no phantom '+' row).
	private static string[] SplitOrEmpty(string text)
	{
		return text.Length == 0 ? Array.Empty<string>() : text.Split('\n');
	}

	// Appends one diff row: a right-aligned line number, a single-char gutter ('|' context, '-' removed,
	// '+' added) at a fixed column, then the line text. The display layer colors rows by their gutter.
	private static void AppendDiffLine(StringBuilder sb, int lineNumber, char marker, string text)
	{
		sb.Append(lineNumber.ToString().PadLeft(5));
		sb.Append(' ');
		sb.Append(marker);
		sb.Append(' ');
		sb.Append(text.TrimEnd('\r'));
		sb.Append('\n');
	}

	// Counts the lines preceding a character offset (the 0-based line the offset falls on).
	private static int LineOfIndex(string content, int index)
	{
		int count = 0;
		int limit = Math.Min(index, content.Length);
		for (int i = 0; i < limit; i++)
		{
			if (content[i] == '\n')
				count++;
		}
		return count;
	}

	public static async Task<ToolResult> WriteFileAsync(
	string            toolCallId,
	string            filePath,
	string            content,
	CancellationToken cancellationToken)
	{
		ToolResult result;

		// The resolved path, symlinks followed, is the one written — never the argument itself.
		(string? confined, string denied) = Sandbox.Confine(filePath, true, false);

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: Path cannot be empty", 1, 0);
		}
		else if (confined == null)
		{
			result = new ToolResult(toolCallId, string.Empty, denied, 1, 0);
		}
		else
		{
			string fullPath = confined;

			try
			{
				string? directory = Path.GetDirectoryName(fullPath);
				if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
				{
					CreateDirectoriesForShellUser(directory);
				}

				using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				cts.CancelAfter(DefaultTimeout);

				await FileLock.WaitAsync(cts.Token);
				try
				{
					await WriteTextAsync(fullPath, content ?? string.Empty, cts.Token);
				}
				finally
				{
					FileLock.Release();
				}

				// Written by the root agent; the model's shell must still be able to change it.
				Sandbox.GiveToShellUser(fullPath);
				result = new ToolResult(toolCallId, "OK", string.Empty, 0, 0);
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult(toolCallId, string.Empty, $"Error: Timed out or interrupted writing: {filePath}", 1, 0);
			}
			catch (Exception ex)
			{
				result = new ToolResult(toolCallId, string.Empty, $"Error: Failed to write file: {ex}", 1, 0);
			}
		}

		return result;
	}

	// Writes text as UTF-8 (no BOM, like File.WriteAllText) without ever opening an existing file with
	// O_CREAT. The workspace root is a sticky, world-writable folder (Sandbox), and with the kernel's
	// fs.protected_regular set — as Docker Desktop's VM sets it — an O_CREAT open of a file someone
	// else owns there is refused even to root. File.WriteAllText always passes O_CREAT, so the root
	// agent could not overwrite anything the model's shell had made. An existing file is truncated in
	// place instead (no O_CREAT), and a new one is created exclusively (O_CREAT|O_EXCL on a name that
	// does not exist, which the rule allows). Truncating in place also keeps the file's owner.
	private static async Task WriteTextAsync(string path, string content, CancellationToken ct)
	{
		FileMode mode                   = File.Exists(path) ? FileMode.Truncate : FileMode.CreateNew;
		await using FileStream stream   = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
		await using StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false));
		await writer.WriteAsync(content.AsMemory(), ct);
	}

	// Creates a folder and any missing parents, handing each new one to the sandbox's shell user so
	// the model's shell can write into what the root agent created.
	private static void CreateDirectoriesForShellUser(string directory)
	{
		List<string> missing = new List<string>();
		string?      current = directory;
		while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
		{
			missing.Add(current);
			current = Path.GetDirectoryName(current);
		}

		Directory.CreateDirectory(directory);
		foreach (string created in missing)
			Sandbox.GiveToShellUser(created);
	}
}