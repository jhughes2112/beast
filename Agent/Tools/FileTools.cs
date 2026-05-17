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
		if (line == null) return 0;

		uint hash = 2166136261u;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			if (char.IsWhiteSpace(c)) continue;
			byte low = (byte)(c & 0xFF);
			hash ^= low;
			hash *= 16777619u;
			byte high = (byte)(c >> 8);
			hash ^= high;
			hash *= 16777619u;
		}
		return (byte)(hash & 0xFFu);
	}

	private enum OpType { ReplaceLines, InsertAfter }

	private class Operation
	{
		public OpType Type { get; set; }
		// For ReplaceLines
		public int StartLine { get; set; }
		public int EndLine { get; set; }
		public byte StartHash { get; set; }
		public byte EndHash { get; set; }
		// For InsertAfter
		public int AnchorLine { get; set; }
		public byte AnchorHash { get; set; }
		public string NewText { get; set; } = string.Empty;
		public int OriginalIndex { get; set; }

		public int PrimaryLine
		{
			get
			{
				if (Type == OpType.ReplaceLines) return StartLine;
				return AnchorLine;
			}
		}
	}

	private static bool TryParseAnchor(string anchor, out int lineNumber, out byte hashByte, out string hexString)
	{
		lineNumber = 0;
		hashByte = 0;
		hexString = string.Empty;
		if (string.IsNullOrEmpty(anchor))
		{
			return false;
		}

		int colon = anchor.IndexOf(':');
		if (colon <= 0)
		{
			return false;
		}
		string linePart = anchor.Substring(0, colon);
		string hexPart = anchor.Substring(colon + 1);
		if (!int.TryParse(linePart, NumberStyles.None, CultureInfo.InvariantCulture, out lineNumber))
		{
			return false;
		}
		if (hexPart.Length == 0 || hexPart.Length > 2)
		{
			return false;
		}
		if (!byte.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hashByte))
		{
			return false;
		}
		hexString = hexPart.ToLowerInvariant();
		return true;
	}

	private static string[] SplitLinesPreserveEmpty(string text)
	{
		if (text == null)
		{
			return new string[0];
		}
		return text.Replace("\r\n", "\n").Split('\n');
	}

	[Description("""
		Reads a file from the local filesystem. You can access any file directly by using this tool.
		Assume this tool is able to read all files on the machine. It is okay to read a file that does not exist; an error will be returned.

		Usage:
		- The file_path parameter must be an absolute path, not a relative path
		- By default, it reads up to 160k characters starting from the beginning of the file
		- You can optionally specify a line offset and limit (especially handy for long files), but it's recommended to read the whole file by not providing these parameters
		- If the output is truncated, it returns the first and last 80k and a warning in the middle that it was truncated and by how much
		- Results are returned using cat -n format, with line numbers starting at 1
		Use offset and lines to page through large files. Do not use cat, head, or tail via bash; use this tool instead.
		""")]
	public static async Task<ToolResult> ReadFileAsync(
		[Description("Absolute path to the file to read.")] string filePath,
		[Description("The line number to start reading from (1 based). Only provide if the file is too large to read at once")] string offset,
		[Description("The number of lines to read. Only provide if the file is too large to read at once")] string lines,
		CancellationToken cancellationToken)
	{
		return await ReadFileContentAsync(filePath, offset, lines, cancellationToken);
	}

	private static async Task<ToolResult> ReadFileContentAsync(string filePath, string offset, string lines, CancellationToken cancellationToken)
	{
		const int MaxLines = 2000;

		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult("Error: Path cannot be empty", false);
		}
		else if (!Path.IsPathRooted(filePath))
		{
			result = new ToolResult($"Error: Path must be absolute: {filePath}", false);
		}
		else
		{
			string fullPath = Path.GetFullPath(filePath);

			try
			{
				if (!File.Exists(fullPath))
				{
					result = new ToolResult($"Error: File not found: {filePath}", false);
				}
				else
				{
					int offsetValue = 0;
					bool offsetValid = string.IsNullOrWhiteSpace(offset) || int.TryParse(offset, out offsetValue);

					int linesValue = 0;
					bool linesValid = string.IsNullOrWhiteSpace(lines) || int.TryParse(lines, out linesValue);

					if (!offsetValid)
					{
						result = new ToolResult($"Error: Invalid offset value: {offset}", false);
					}
					else if (!linesValid)
					{
						result = new ToolResult($"Error: Invalid lines value: {lines}", false);
					}
					else if (offsetValue < 0)
					{
						result = new ToolResult("Error: offset must be >= 0", false);
					}
					else if (linesValue < 0)
					{
						result = new ToolResult("Error: lines must be >= 0", false);
					}
					else
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
							int requestedLines = linesValue > 0 ? linesValue : MaxLines;
							int linesToRead = Math.Min(requestedLines, MaxLines);

							List<string> readLines = new List<string>();
							int currentLine = 0;
							bool hitMaxLines = false;

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

								if (currentLine >= startLine + linesToRead)
								{
									break;
								}

								if (currentLine >= MaxLines && startLine == 1 && linesValue == 0)
								{
									hitMaxLines = true;
									break;
								}
							}

							if (readLines.Count == 0)
							{
								if (currentLine == 0)
								{
									result = new ToolResult($"File is empty: {filePath}", false);
								}
								else
								{
									result = new ToolResult($"Offset {startLine} is beyond the end of the file (file has {currentLine} lines).", false);
								}
							}
							else
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

								if (hitMaxLines)
								{
									sb.AppendLine();
									sb.AppendLine($"[Output truncated at {MaxLines} lines. Use offset and lines parameters to read more.]");
								}

								result = new ToolResult(sb.ToString(), false);
							}
						}
						finally
						{
							FileLock.Release();
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult($"Error: Timed out or cancelled reading file: {filePath}", false);
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Failed to read file: {ex.Message}", false);
			}
		}

		return result;
	}


	[Description("""
		Apply a single replace_lines edit to a file. This is a simpler wrapper around edit_file that accepts exactly one replacement operation instead of a JSON array.
		Parameters:
		- startAnchor: anchor string in the form "<line>:<hh>" as produced by read_file
		- endAnchor: anchor string in the form "<line>:<hh>" as produced by read_file
		- newText: replacement text to place between the anchors
		""")]
	public static async Task<ToolResult> EditFileReplaceAsync(
		[Description("Absolute path to the file to modify.")] string filePath,
		[Description("Start anchor in the form '<line>:<hh>'")] string startAnchor,
		[Description("End anchor in the form '<line>:<hh>'")] string endAnchor,
		[Description("Replacement text to insert between the anchors.")] string newText,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(startAnchor))
		{
			return new ToolResult("Error: start_anchor cannot be empty", false);
		}
		if (string.IsNullOrWhiteSpace(endAnchor))
		{
			return new ToolResult("Error: end_anchor cannot be empty", false);
		}

		JsonArray ops = new JsonArray();
		JsonObject rep = new JsonObject();
		JsonObject repParams = new JsonObject();
		repParams["start_anchor"] = startAnchor ?? string.Empty;
		repParams["end_anchor"] = endAnchor ?? string.Empty;
		repParams["new_text"] = newText ?? string.Empty;
		rep["replace_lines"] = repParams;
		ops.Add(rep);

		string editsJson = ops.ToJsonString();
		return await EditFileAsync(filePath, editsJson, cancellationToken);
	}

	[Description("""
		Apply a single insert_after edit to a file.
		Parameters:
		- anchor: anchor string in the form "<line>:<hh>" as produced by read_file
		- newText: text to insert after the anchor
		""")]
	public static async Task<ToolResult> EditFileInsertAsync(
		[Description("Absolute path to the file to modify.")] string filePath,
		[Description("Anchor in the form '<line>:<hh>'")] string anchor,
		[Description("Text to insert after the anchor.")] string newText,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(anchor))
		{
			return new ToolResult("Error: anchor cannot be empty", false);
		}

		JsonArray ops = new JsonArray();
		JsonObject ins = new JsonObject();
		JsonObject insParams = new JsonObject();
		insParams["anchor"] = anchor ?? string.Empty;
		insParams["new_text"] = newText ?? string.Empty;
		ins["insert_after"] = insParams;
		ops.Add(ins);

		string editsJson = ops.ToJsonString();
		return await EditFileAsync(filePath, editsJson, cancellationToken);
	}

	[Description("""
		Create a new file or overwrite an existing one.
		If the file already exists, you must read_file first. Prefer edit_file for partial changes.
		Never create files not required by the task.
		""")]
	public static async Task<ToolResult> WriteFileAsync(
		[Description("The exact full path to the file to create or overwrite. Absolute paths only.")] string filePath,
		[Description("The complete content to write to the file. This replaces the entire file contents.")] string content,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult("Error: Path cannot be empty", false);
		}
		else if (!Path.IsPathRooted(filePath))
		{
			result = new ToolResult($"Error: Path must be absolute: {filePath}", false);
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

				result = new ToolResult($"File written: {filePath}", false);
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult($"Error: Timed out or cancelled writing file: {filePath}", false);
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Failed to write file: {ex.Message}", false);
			}
		}

		return result;
	}



	[Description("""
		Apply multiple line-anchored edits to a file. The edits parameter is a JSON string representing an ordered array of operations. Each operation is one of:
		- { "replace_lines": { "start_anchor": "<line>:<hh>", "end_anchor": "<line>:<hh>", "new_text": "..." } }
		- { "insert_after": { "anchor": "<line>:<hh>", "new_text": "..." } }

		Anchors are produced by the read_file tool and are of the form "<line>:<hh>" where hh is the low-order byte of a fast non-cryptographic hash (two-digit lowercase hex) computed over the line with all whitespace removed.

		Behavior:
		- The edits array must be provided as a JSON string.
		- Operations are applied in the provided order conceptually, but to avoid disturbing subsequent anchors they are validated and then executed in reverse order (last-to-first).
		- Before applying any changes, all anchors mentioned are verified against the current file. If any anchor does not match, no edits are applied and the tool returns an error. The error output includes the non-matching lines in the same format as read_file (with anchors).
		- Replacements and inserts are treated uniformly as operations addressing line ranges or insertion points and are applied last-to-first by line number.
		""")]
	public static async Task<ToolResult> EditFileAsync(
		[Description("Absolute path to the file to modify.")] string filePath,
		[Description("A JSON string encoding an ordered array of edit operations (see tool description).")] string edits,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult("Error: Path cannot be empty", false);
		}
		else if (!Path.IsPathRooted(filePath))
		{
			result = new ToolResult($"Error: Path must be absolute: {filePath}", false);
		}
		else if (string.IsNullOrWhiteSpace(edits))
		{
			result = new ToolResult("Error: edits JSON cannot be empty", false);
		}
		else
		{
			string fullPath = Path.GetFullPath(filePath);

			try
			{
				if (!File.Exists(fullPath))
				{
					result = new ToolResult($"Error: File not found: {filePath}", false);
				}
				else
				{
					using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					cts.CancelAfter(DefaultTimeout);

					await FileLock.WaitAsync(cts.Token);
					try
					{
						string fileContent = await File.ReadAllTextAsync(fullPath, cts.Token);
						bool trailingNewline = fileContent.EndsWith("\n");
						string[] fileLines = SplitLinesPreserveEmpty(fileContent);
						// SplitLinesPreserveEmpty leaves a trailing "" for files ending with \n; strip it so line
						// numbers match expectations and we re-add the newline on write via trailingNewline.
						int lineCount = (trailingNewline && fileLines.Length > 0) ? fileLines.Length - 1 : fileLines.Length;
						List<string> linesList = new List<string>(lineCount);
						for (int li = 0; li < lineCount; li++) linesList.Add(fileLines[li]);

						JsonNode? rootNode;
						try
						{
							rootNode = JsonNode.Parse(edits);
						}
						catch (Exception ex)
						{
							result = new ToolResult($"Error: Failed to parse edits JSON: {ex.Message}", false);
							return result;
						}

						if (rootNode is not JsonArray opsArray)
						{
							result = new ToolResult("Error: edits JSON must be an array of operations.", false);
							return result;
						}

						if (opsArray.Count == 0)
						{
							result = new ToolResult("Error: edits array is empty.", false);
							return result;
						}

						// Build operations list preserving input order. Collect parse errors instead of returning early.
						List<Operation> operations = new List<Operation>();
						List<string> parseErrors = new List<string>();
						int idx = 0;
						foreach (JsonNode? node in opsArray)
						{
							idx++;
							if (node is not JsonObject obj)
							{
								parseErrors.Add($"Operation {idx} is not an object.");
								continue;
							}

							if (obj["replace_lines"] is JsonObject rep)
							{
								string? startAnchor = rep["start_anchor"]?.ToString() ?? rep["startAnchor"]?.ToString();
								string? endAnchor = rep["end_anchor"]?.ToString() ?? rep["endAnchor"]?.ToString();
								string? newText = rep["new_text"]?.ToString() ?? rep["newText"]?.ToString();
								if (string.IsNullOrEmpty(startAnchor) || string.IsNullOrEmpty(endAnchor))
								{
									parseErrors.Add($"Operation {idx}: replace_lines requires start_anchor and end_anchor.");
									continue;
								}
								if (!TryParseAnchor(startAnchor, out int sLine, out byte sHash, out string sHex))
								{
									parseErrors.Add($"Operation {idx}: invalid start_anchor format: {startAnchor}");
									continue;
								}
								if (!TryParseAnchor(endAnchor, out int eLine, out byte eHash, out string eHex))
								{
									parseErrors.Add($"Operation {idx}: invalid end_anchor format: {endAnchor}");
									continue;
								}
								Operation op = new Operation();
								op.Type = OpType.ReplaceLines;
								op.StartLine = sLine;
								op.EndLine = eLine;
								op.StartHash = sHash;
								op.EndHash = eHash;
								op.NewText = newText ?? string.Empty;
								op.OriginalIndex = idx;
								operations.Add(op);
							}
							else if (obj["insert_after"] is JsonObject ins)
							{
								string? anchor = ins["anchor"]?.ToString();
								string? newText = ins["new_text"]?.ToString() ?? ins["newText"]?.ToString();
								if (string.IsNullOrEmpty(anchor))
								{
									parseErrors.Add($"Operation {idx}: insert_after requires anchor.");
									continue;
								}
								if (!TryParseAnchor(anchor, out int aLine, out byte aHash, out string aHex))
								{
									parseErrors.Add($"Operation {idx}: invalid anchor format: {anchor}");
									continue;
								}
								Operation op = new Operation();
								op.Type = OpType.InsertAfter;
								op.AnchorLine = aLine;
								op.AnchorHash = aHash;
								op.NewText = newText ?? string.Empty;
								op.OriginalIndex = idx;
								operations.Add(op);
							}
							else
							{
								parseErrors.Add($"Operation {idx}: unknown operation type.");
								continue;
							}
						}

						if (parseErrors.Count > 0)
						{
							StringBuilder sbErrors = new StringBuilder();
							sbErrors.AppendLine("Error: Failed to parse one or more operations:");
							foreach (string pe in parseErrors)
							{
								sbErrors.AppendLine(pe);
							}
							result = new ToolResult(sbErrors.ToString(), false);
							return result;
						}

						// Error if no operations were provided
						if (operations.Count == 0)
						{
							result = new ToolResult("Error: edits array contains no valid operations.", false);
							return result;
						}

						// Order operations by smallest relevant line number ascending
						operations.Sort((Operation a, Operation b) => a.PrimaryLine.CompareTo(b.PrimaryLine));

						// Verify all anchors against the original file lines by iterating operations in reverse order
						List<int> mismatchedLines = new List<int>();
						for (int v = operations.Count - 1; v >= 0; v--)
						{
							Operation op = operations[v];
							if (op.Type == OpType.ReplaceLines)
							{
								if (op.StartLine < 1 || op.EndLine < op.StartLine || op.EndLine > linesList.Count)
								{
									result = new ToolResult($"Error: Operation {op.OriginalIndex}: anchor line numbers out of range.", false);
									return result;
								}
								byte actualStart = ComputeLineHashByte(linesList[op.StartLine - 1]);
								byte actualEnd = ComputeLineHashByte(linesList[op.EndLine - 1]);
								if (actualStart != op.StartHash)
								{
									mismatchedLines.Add(op.StartLine);
								}
								if (actualEnd != op.EndHash)
								{
									mismatchedLines.Add(op.EndLine);
								}
							}
							else if (op.Type == OpType.InsertAfter)
							{
								if (op.AnchorLine < 1 || op.AnchorLine > linesList.Count)
								{
									result = new ToolResult($"Error: Operation {op.OriginalIndex}: anchor line number out of range.", false);
									return result;
								}
								byte actual = ComputeLineHashByte(linesList[op.AnchorLine - 1]);
								if (actual != op.AnchorHash)
								{
									mismatchedLines.Add(op.AnchorLine);
								}
							}
						}

						if (mismatchedLines.Count > 0)
						{
							// Build error output listing mismatched lines in read_file format
							StringBuilder sbErr = new StringBuilder();
							sbErr.AppendLine("Error: One or more anchor hashes did not match. No edits were applied.");
							mismatchedLines.Sort();
							foreach (int lineNo in mismatchedLines)
							{
								int idx0 = lineNo - 1;
								if (idx0 >= 0 && idx0 < linesList.Count)
								{
									byte h = ComputeLineHashByte(linesList[idx0]);
									sbErr.AppendLine($"{lineNo,6}:{h:x2}\t{linesList[idx0]}");
								}
							}
							result = new ToolResult(sbErr.ToString(), false);
							return result;
						}

						// No mismatches: apply operations in reverse order (last-to-first)
						for (int opIndex = operations.Count - 1; opIndex >= 0; opIndex--)
						{
							Operation op = operations[opIndex];
							if (op.Type == OpType.ReplaceLines)
							{
								int start = op.StartLine - 1;
								int removeCount = op.EndLine - op.StartLine + 1;
								if (start >= 0 && start + removeCount <= linesList.Count)
								{
									linesList.RemoveRange(start, removeCount);
									if (!string.IsNullOrEmpty(op.NewText))
									{
										string[] newLines = SplitLinesPreserveEmpty(op.NewText);
										linesList.InsertRange(start, newLines);
									}
								}
							}
							else if (op.Type == OpType.InsertAfter)
							{
								int insertIndex = op.AnchorLine; // after anchor
								if (insertIndex < 0) insertIndex = 0;
								if (insertIndex > linesList.Count) insertIndex = linesList.Count;
								if (!string.IsNullOrEmpty(op.NewText))
								{
									string[] newLines = SplitLinesPreserveEmpty(op.NewText);
									linesList.InsertRange(insertIndex, newLines);
								}
							}
						}

						// Rebuild and write
						string newWorking = string.Join(Environment.NewLine, linesList);
						if (trailingNewline && !newWorking.EndsWith("\n")) newWorking += Environment.NewLine;
						await File.WriteAllTextAsync(fullPath, newWorking, cts.Token);
						result = new ToolResult($"File edited: {filePath} ({operations.Count} operation(s) applied)", false);
					}
					finally
					{
						FileLock.Release();
					}
				}
			}
			catch (OperationCanceledException)
			{
				result = new ToolResult($"Error: Timed out or cancelled editing file: {filePath}", false);
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Failed to edit file: {ex.Message}", false);
			}
		}

		return result;
	}
}
