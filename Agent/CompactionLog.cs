using System.IO;
using System.Text;


// What a compaction took out of the conversation and the successor may want back: the original commands,
// their output, and reasoning traces, as plain text with a divider between chunks. One file per compacted
// session, never changed once the session has moved on, so the line ranges handed to the successor stay
// true through later compactions. Built in memory: a pass the caller ends up not using leaves nothing behind.
public sealed class CompactionLog
{
	private const string kDivider = "----------------------------------------";

	private readonly StringBuilder _text = new StringBuilder();
	private          int           _lines;

	public string FilePath { get; }

	public CompactionLog(string filePath)
	{
		FilePath = filePath;
	}

	// The model reads this path in its prompt. A session ID is the same on every run and its own, whichever order
	// conversations compact in, and a session keeps its file through retries and resumes.
	public static string PathFor(string sessionId)
	{
		return Path.Combine(AgentState.CompactionLogsDir, $"compaction_{sessionId}.log");
	}

	// Where this chunk would land if added now, so a caller can weigh the reference against the text.
	public string ReferenceTo(string heading, string body)
	{
		int first = _lines + (_lines > 0 ? 2 : 1);
		return $"{FilePath} lines {first} to {first + CountLines(heading) + CountLines(body) - 1}";
	}

	// Appends a chunk and returns the reference to it. The range covers the heading and the body, not the divider.
	public string Add(string heading, string body)
	{
		string reference = ReferenceTo(heading, body);
		if (_lines > 0)
		{
			_text.Append(kDivider).Append('\n');
			_lines++;
		}
		_text.Append(heading).Append('\n').Append(body).Append('\n');
		_lines += CountLines(heading) + CountLines(body);
		return reference;
	}

	public void Save()
	{
		if (_lines > 0)
		{
			Directory.CreateDirectory(AgentState.CompactionLogsDir);
			File.WriteAllText(FilePath, _text.ToString());
		}
	}

	private static int CountLines(string text)
	{
		int lines = 1;
		foreach (char c in text)
		{
			if (c == '\n')
				lines++;
		}
		return lines;
	}
}