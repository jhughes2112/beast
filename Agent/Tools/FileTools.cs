using System.ComponentModel;
using System.Text;
using System.Globalization;
using System.Text.Json.Nodes;
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

	// Compute a simple non-cryptographic hash for a line after stripping all whitespace.
	// Returns the low-order byte of an FNV-1a 32-bit hash as the anchor byte.
	internal static byte ComputeLineHashByte(string? line)
	{
		byte result = 0;

		if (line != null)
		{
			uint hash = 2166136261u;
			for (int i = 0; i < line.Length; i++)
			{
				char c = line[i];
				if (!char.IsWhiteSpace(c))
				{
					byte low = (byte)(c & 0xFF);
					hash ^= low;
					hash *= 16777619u;
					byte high = (byte)(c >> 8);
					hash ^= high;
					hash *= 16777619u;
				}
			}
			result = (byte)(hash & 0xFFu);
		}

		return result;
	}

	private static bool TryParseAnchor(string anchor, out int lineNumber, out byte hashByte, out string hexString)
	{
		bool result = false;
		lineNumber = 0;
		hashByte = 0;
		hexString = string.Empty;

		if (!string.IsNullOrEmpty(anchor))
		{
			int colon = anchor.IndexOf(':');
			if (colon > 0)
			{
				string linePart = anchor.Substring(0, colon);
				string hexPart = anchor.Substring(colon + 1, 2);  // only accept 2 characters for the hex part
				if (int.TryParse(linePart, NumberStyles.None, CultureInfo.InvariantCulture, out lineNumber))
				{
					if (hexPart.Length == 2 && byte.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hashByte))
					{
						hexString = hexPart.ToLowerInvariant();
						result = true;
					}
				}
			}
		}

		return result;
	}

	private static string[] SplitLinesPreserveEmpty(string text)
	{
		string[] result;

		if (text == null)
		{
			result = new string[0];
		}
		else
		{
			result = text.Replace("\r\n", "\n").Split('\n');
		}

		return result;
	}

	[Description("""
		Reads a file in modified cat -n format with hash anchors per line. CWD is /workspace/
		""")]
	public static async Task<ToolResult> ReadFileAsync(
		[Description("File path")] string filePath,
		[Description("Starting line number (1 based)")] string offset,
		[Description("Number of lines to read. Empty means to the end of the file.")] string lines,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(string.Empty, "Error: Path cannot be empty", 1);
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
							using FileStream fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
							using StreamReader sr = new StreamReader(fs);

							// Offset is 1-based; 0 is treated as 1.
							if (offsetValue <= 0)
							{
								offsetValue = 1;
							}

							int startLine = offsetValue;
							int linesToRead = linesValue > 0 ? linesValue : int.MaxValue;

							List<string> readLines = new List<string>();
							int currentLine = 0;

							for (; ; )
							{
								currentLine++;

								string? line = await sr.ReadLineAsync(cts.Token);
								if (line == null)
								{
									break;
								}

								if (currentLine >= startLine && readLines.Count < linesToRead)
								{
									readLines.Add(line);
								}

								if (readLines.Count >= linesToRead)
								{
									break;
								}
							}

							if (readLines.Count > 0)
							{
								int endLine = startLine + readLines.Count - 1;
								bool isWindowed = offsetValue > 1 || linesValue > 0;

								StringBuilder sb = new StringBuilder();

								if (isWindowed)
								{
									sb.AppendLine($"Showing lines {startLine}-{endLine}:");
								}

								for (int i = 0; i < readLines.Count; i++)
								{
									byte hash = ComputeLineHashByte(readLines[i]);
									sb.AppendLine($"{startLine + i,6}:{hash:x2}\t{readLines[i]}");
								}

								result = new ToolResult(sb.ToString(), string.Empty, 0);
							}
							else
							{
								if (currentLine == 0)
								{
									result = new ToolResult($"File is empty: {filePath}", string.Empty, 0);
								}
								else
								{
									result = new ToolResult(string.Empty, $"Offset {startLine} is beyond the end of the file (file has {currentLine} lines).", 1);
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
							result = new ToolResult(string.Empty, $"Error: Invalid offset value: {offset}", 1);
						}
						else if (!linesValid)
						{
							result = new ToolResult(string.Empty, $"Error: Invalid lines value: {lines}", 1);
						}
						else if (offsetValue < 0)
						{
							result = new ToolResult(string.Empty, "Error: offset must be >= 0", 1);
						}
						else
						{
							result = new ToolResult(string.Empty, "Error: lines must be >= 0", 1);
						}
					}
				}
				else
				{
					result = new ToolResult(string.Empty, $"Error: File not found: {filePath}", 1);
				}
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult(string.Empty, $"Error: Timed out or cancelled reading file: {filePath}", 1);
			}
			catch (Exception ex)
			{
				result = new ToolResult(string.Empty, $"Error: Failed to read file: {ex.Message}", 1);
			}
		}

		return result;
	}

	[Description("""
		Replace a block of text defined by the start and end line:hash anchors. CWD is /workspace/
		""")]
	public static async Task<ToolResult> EditFileReplaceAsync(
		[Description("File path")] string filePath,
		[Description("Start anchor is only the line:hash")] string startAnchor,
		[Description("End anchor is only the line:hash")] string endAnchor,
		[Description("Replacement text")] string newText,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(string.Empty, "Error: Path cannot be empty", 1);
		}
		else if (string.IsNullOrWhiteSpace(startAnchor))
		{
			result = new ToolResult(string.Empty, "Error: start_anchor cannot be empty", 1);
		}
		else if (string.IsNullOrWhiteSpace(endAnchor))
		{
			result = new ToolResult(string.Empty, "Error: end_anchor cannot be empty", 1);
		}
		else if (TryParseAnchor(startAnchor, out int startLine, out byte startHash, out string startHex))
		{
			if (TryParseAnchor(endAnchor, out int endLine, out byte endHash, out string endHex))
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
							bool trailingNewline = fileContent.EndsWith("\n");
							string[] fileLines = SplitLinesPreserveEmpty(fileContent);
							int lineCount = (trailingNewline && fileLines.Length > 0) ? fileLines.Length - 1 : fileLines.Length;
							List<string> linesList = new List<string>(lineCount);
							for (int li = 0; li < lineCount; li++)
							{
								linesList.Add(fileLines[li]);
							}

							if (startLine >= 1 && endLine >= startLine && endLine <= linesList.Count)
							{
								byte actualStart = ComputeLineHashByte(linesList[startLine - 1]);
								byte actualEnd = ComputeLineHashByte(linesList[endLine - 1]);

								if (actualStart == startHash && actualEnd == endHash)
								{
									int start = startLine - 1;
									int removeCount = endLine - startLine + 1;
									linesList.RemoveRange(start, removeCount);
									if (!string.IsNullOrEmpty(newText))
									{
										string[] newLines = SplitLinesPreserveEmpty(newText);
										linesList.InsertRange(start, newLines);
									}

									string newWorking = string.Join(Environment.NewLine, linesList);
									if (trailingNewline && !newWorking.EndsWith("\n"))
									{
										newWorking += Environment.NewLine;
									}
									await File.WriteAllTextAsync(fullPath, newWorking, cts.Token);
									result = new ToolResult("OK", string.Empty, 0);
								}
								else
								{
									StringBuilder sbErr = new StringBuilder();
									sbErr.AppendLine("Error: One or more anchor hashes did not match. No edits were applied.");
									if (actualStart != startHash)
											{
												sbErr.AppendLine($"{startLine,6}:{actualStart:x2}\t{linesList[startLine - 1]}");
											}
											if (actualEnd != endHash)
											{
												sbErr.AppendLine($"{endLine,6}:{actualEnd:x2}\t{linesList[endLine - 1]}");
											}
											result = new ToolResult(string.Empty, sbErr.ToString(), 1);
										}
									}
									else
									{
										result = new ToolResult(string.Empty, "Error: Anchor line numbers out of range.", 1);
									}
						}
						finally
						{
							FileLock.Release();
						}
					}
					else
					{
						result = new ToolResult(string.Empty, $"Error: File not found: {filePath}", 1);
					}
				}
				catch (OperationCanceledException)
				{
					result = new ToolResult(string.Empty, $"Error: Timed out or cancelled editing file: {filePath}", 1);
				}
				catch (Exception ex)
				{
					result = new ToolResult(string.Empty, $"Error: Failed to edit file: {ex.Message}", 1);
				}
			}
			else
			{
				result = new ToolResult(string.Empty, $"Error: Invalid end_anchor format: {endAnchor}", 1);
			}
		}
		else
		{
			result = new ToolResult(string.Empty, $"Error: Invalid start_anchor format: {startAnchor}", 1);
		}

		return result;
	}

	[Description("""
		Insert a line of text AFTER the indicated line:hash anchor. CWD is /workspace/
		""")]
	public static async Task<ToolResult> EditFileInsertAsync(
		[Description("File path")] string filePath,
		[Description("Line anchor is only the line:hash")] string anchor,
		[Description("Text to insert")] string newText,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(string.Empty, "Error: Path cannot be empty", 1);
		}
		else if (string.IsNullOrWhiteSpace(anchor))
		{
			result = new ToolResult(string.Empty, "Error: anchor cannot be empty", 1);
		}
		else if (TryParseAnchor(anchor, out int anchorLine, out byte anchorHash, out string anchorHex))
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
						bool trailingNewline = fileContent.EndsWith("\n");
						string[] fileLines = SplitLinesPreserveEmpty(fileContent);
						int lineCount = (trailingNewline && fileLines.Length > 0) ? fileLines.Length - 1 : fileLines.Length;
						List<string> linesList = new List<string>(lineCount);
						for (int li = 0; li < lineCount; li++)
						{
							linesList.Add(fileLines[li]);
						}

						if (anchorLine >= 1 && anchorLine <= linesList.Count)
						{
							byte actual = ComputeLineHashByte(linesList[anchorLine - 1]);
							if (actual == anchorHash)
							{
								int insertIndex = anchorLine;
								if (!string.IsNullOrEmpty(newText))
								{
									string[] newLines = SplitLinesPreserveEmpty(newText);
									linesList.InsertRange(insertIndex, newLines);
								}

								string newWorking = string.Join(Environment.NewLine, linesList);
								if (trailingNewline && !newWorking.EndsWith("\n"))
								{
									newWorking += Environment.NewLine;
								}
								await File.WriteAllTextAsync(fullPath, newWorking, cts.Token);
								result = new ToolResult("OK", string.Empty, 0);
							}
								else
								{
									StringBuilder sbErr = new StringBuilder();
									sbErr.AppendLine("Error: Anchor hash did not match. No edits were applied.");
									sbErr.AppendLine($"{anchorLine,6}:{actual:x2}\t{linesList[anchorLine - 1]}");
									result = new ToolResult(string.Empty, sbErr.ToString(), 1);
								}
							}
							else
							{
								result = new ToolResult(string.Empty, "Error: Anchor line number out of range.", 1);
							}
					}
					finally
					{
						FileLock.Release();
					}
				}
				else
				{
					result = new ToolResult(string.Empty, $"Error: File not found: {filePath}", 1);
				}
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult(string.Empty, $"Error: Timed out or cancelled editing file: {filePath}", 1);
			}
			catch (Exception ex)
			{
				result = new ToolResult(string.Empty, $"Error: Failed to edit file: {ex.Message}", 1);
			}
		}
		else
		{
			result = new ToolResult(string.Empty, $"Error: Invalid anchor format: {anchor}", 1);
		}

		return result;
	}

	[Description("""
		Create a new file or overwrite an existing one.
		If the file already exists, you must read_file first. Prefer edit_file for partial changes.
		Only create files required by the task. Temporary files should go in /tmp/
		""")]
	public static async Task<ToolResult> WriteFileAsync(
		[Description("File path")] string filePath,
		[Description("Complete file contents")] string content,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(string.Empty, "Error: Path cannot be empty", 1);
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

				result = new ToolResult("OK", string.Empty, 0);
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult(string.Empty, $"Error: Timed out or cancelled writing file: {filePath}", 1);
			}
			catch (Exception ex)
			{
				result = new ToolResult(string.Empty, $"Error: Failed to write file: {ex.Message}", 1);
			}
		}

		return result;
	}
}





