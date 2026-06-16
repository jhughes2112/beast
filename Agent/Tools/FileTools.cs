using System.Text;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;


// Tools for LLM to read and write files.
public static class FileTools
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
	private static readonly SemaphoreSlim FileLock = new SemaphoreSlim(1, 1);

	// A single line is never returned longer than this, regardless of caller — a lone enormous (e.g.
	// minified) line would otherwise flood the context. The line count is capped by the caller's maxLines.
	private const int MaxLineLength = 2000;

	public static async Task<ToolResult> ReadFileAsync(
		string toolCallId,
		string filePath,
		string offset,
		string lines,
		int maxLines,
		bool numberLines,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: Path cannot be empty", 1, 0);
		}
		else
		{
			string fullPath = Path.GetFullPath(filePath);

			try
			{
				if (File.Exists(fullPath))
				{
					int offsetValue = 0;
					bool offsetValid = string.IsNullOrWhiteSpace(offset) || int.TryParse(offset, out offsetValue);
					int linesValue = 0;
					bool linesValid = string.IsNullOrWhiteSpace(lines) || int.TryParse(lines, out linesValue);

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
								// Windowed read: split, slice, rejoin. The output is always bounded — at most
								// maxLines lines, each at most MaxLineLength chars — so a huge file or a single
								// enormous line can never flood the context. Limits are mentioned, never errors.
								string[] allLines = fileContent.Replace("\r\n", "\n").Split('\n');
								int startLine = offsetValue <= 0 ? 1 : offsetValue;
								int startIdx = startLine - 1;

								if (startIdx >= allLines.Length)
								{
									result = new ToolResult(toolCallId, string.Empty, $"Offset {startLine} is beyond the end of the file (file has {allLines.Length} lines).", 1, 0);
								}
								else
								{
									int available = allLines.Length - startIdx;
									int requested = linesValue > 0 ? Math.Min(linesValue, available) : available;
									int count = Math.Min(requested, maxLines);

									bool lineTruncated = false;
									StringBuilder sb = new StringBuilder();
									for (int i = 0; i < count; i++)
									{
										string line = allLines[startIdx + i];
										if (line.Length > MaxLineLength)
										{
											line = line.Substring(0, MaxLineLength) + "...truncated";
											lineTruncated = true;
										}
										if (i > 0)
											sb.Append(Environment.NewLine);
										// Line numbers let a reader cite exact locations (file, line, count).
										if (numberLines)
										{
											sb.Append((startLine + i).ToString().PadLeft(6));
											sb.Append(" | ");
										}
										sb.Append(line);
									}

									if (count < requested)
										sb.Append($"{Environment.NewLine}[Showing {count} lines from line {startLine}; output capped at {maxLines} lines. Read again from line {startLine + count} to continue.]");
									if (lineTruncated)
										sb.Append($"{Environment.NewLine}[One or more lines exceeded {MaxLineLength} characters and were truncated with \"...truncated\".]");

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
				result = new ToolResult(toolCallId, string.Empty, $"Error: Failed to read file: {ex.Message}", 1, 0);
			}
		}

		return result;
	}

	public static async Task<ToolResult> EditFileAsync(
		string toolCallId,
		string filePath,
		string oldText,
		string newText,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: Path cannot be empty", 1, 0);
		}
		else if (string.IsNullOrEmpty(oldText))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: old_text cannot be empty", 1, 0);
		}
		else
		{
			string fullPath = Path.GetFullPath(filePath);

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
							await File.WriteAllTextAsync(fullPath, newContent, cts.Token);
							result = new ToolResult(toolCallId, BuildEditEcho(newContent, exactIdx, replacement.Length), string.Empty, 0, 0);
						}
						else
						{
							// Fuzzy match: strip all whitespace from old_text and file, find the span,
							// map back to original positions and replace.
							List<int> posMap = new List<int>(fileContent.Length);
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
									int origEnd = posMap[matchIdx + strippedOld.Length - 1] + 1;
									string newContent = fileContent.Substring(0, origStart) + replacement + fileContent.Substring(origEnd);
									await File.WriteAllTextAsync(fullPath, newContent, cts.Token);
									result = new ToolResult(toolCallId, BuildEditEcho(newContent, origStart, replacement.Length), string.Empty, 0, 0);
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
				result = new ToolResult(toolCallId, string.Empty, $"Error: Failed to edit file: {ex.Message}", 1, 0);
			}
		}

		return result;
	}

	// Echoes where the edit landed: three lines before the change, a [hunk inserted]/[hunk deleted]
	// marker in place of the new text (the model already knows what it changed), then three lines after.
	// This confirms placement, which matters because the whitespace-normalized fallback can land
	// on a different region than the model pictured.
	private static string BuildEditEcho(string content, int replaceStart, int replaceLength)
	{
		const int contextLines = 3;

		string[] lines = content.Split('\n');
		int startLine = LineOfIndex(content, replaceStart);
		int lastIdx = replaceLength > 0 ? replaceStart + replaceLength - 1 : replaceStart;
		int endLine = LineOfIndex(content, lastIdx);

		int firstBefore = Math.Max(0, startLine - contextLines);
		int lastAfter = Math.Min(lines.Length - 1, endLine + contextLines);

		StringBuilder sb = new StringBuilder();
		sb.Append($"Edit applied at line {startLine + 1}:\n");

		for (int i = firstBefore; i < startLine; i++)
		{
			AppendNumbered(sb, i + 1, lines[i]);
		}

		sb.Append(replaceLength > 0 ? "      | [hunk inserted]\n" : "      | [hunk deleted]\n");

		for (int i = endLine + 1; i <= lastAfter; i++)
		{
			AppendNumbered(sb, i + 1, lines[i]);
		}

		return sb.ToString();
	}

	// Appends one right-aligned, line-numbered row matching the marker column width.
	private static void AppendNumbered(StringBuilder sb, int lineNumber, string text)
	{
		sb.Append(lineNumber.ToString().PadLeft(5));
		sb.Append(" | ");
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
	string toolCallId,
	string filePath,
	string content,
	CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: Path cannot be empty", 1, 0);
		}
		else
		{
			string fullPath = Path.GetFullPath(filePath);

			try
			{
				string? directory = Path.GetDirectoryName(fullPath);
				if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
				{
					Directory.CreateDirectory(directory);
				}

				using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				cts.CancelAfter(DefaultTimeout);

				await FileLock.WaitAsync(cts.Token);
				try
				{
					await File.WriteAllTextAsync(fullPath, content ?? string.Empty, cts.Token);
				}
				finally
				{
					FileLock.Release();
				}

				result = new ToolResult(toolCallId, "OK", string.Empty, 0, 0);
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult(toolCallId, string.Empty, $"Error: Timed out or interrupted writing: {filePath}", 1, 0);
			}
			catch (Exception ex)
			{
				result = new ToolResult(toolCallId, string.Empty, $"Error: Failed to write file: {ex.Message}", 1, 0);
			}
		}

		return result;
	}
}
