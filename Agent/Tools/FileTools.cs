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

	public static async Task<ToolResult> ReadFileAsync(
		string filePath,
		string offset,
		string lines,
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
							string fileContent = await File.ReadAllTextAsync(fullPath, cts.Token);

							if (offsetValue <= 1 && linesValue == 0)
							{
								result = fileContent.Length == 0
									? new ToolResult($"File is empty: {filePath}", string.Empty, 0)
									: new ToolResult(fileContent, string.Empty, 0);
							}
							else
							{
								// Windowed read: split, slice, rejoin
								string[] allLines = fileContent.Replace("\r\n", "\n").Split('\n');
								int startLine = offsetValue <= 0 ? 1 : offsetValue;
								int startIdx = startLine - 1;

								if (startIdx >= allLines.Length)
								{
									result = new ToolResult(string.Empty, $"Offset {startLine} is beyond the end of the file (file has {allLines.Length} lines).", 1);
								}
								else
								{
									int count = linesValue > 0 ? Math.Min(linesValue, allLines.Length - startIdx) : allLines.Length - startIdx;
									string[] slice = new string[count];
									Array.Copy(allLines, startIdx, slice, 0, count);
									string windowed = string.Join(Environment.NewLine, slice);
									result = new ToolResult(windowed, string.Empty, 0);
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

	public static async Task<ToolResult> EditFileAsync(
		string filePath,
		string oldText,
		string newText,
		CancellationToken cancellationToken)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult(string.Empty, "Error: Path cannot be empty", 1);
		}
		else if (string.IsNullOrEmpty(oldText))
		{
			result = new ToolResult(string.Empty, "Error: old_text cannot be empty", 1);
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
							result = new ToolResult("OK", string.Empty, 0);
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
								result = new ToolResult(string.Empty, "Error: old_text contains only whitespace.", 1);
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
									result = new ToolResult("OK", string.Empty, 0);
								}
								else
								{
									result = new ToolResult(string.Empty, "Error: old_text not found in file (exact and whitespace-normalized search both failed).", 1);
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

		return result;
	}

	public static async Task<ToolResult> WriteFileAsync(
		string filePath,
		string content,
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
