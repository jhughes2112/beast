using System;
using System.IO;
using System.Text;


// What a compaction took out of the conversation and the successor may want back: every call it folded into
// a digest, with its arguments and output, and the reasoning traces it kept a summary of, as plain text with
// a divider between chunks. One file per compacted session, never changed once the session has moved on, so
// the line ranges handed to the successor stay true through later compactions. Built in memory: a pass the
// caller ends up not using leaves nothing behind.
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

	// Where the model is pointed: under the sandbox, a root-owned copy outside the agent's state that its shell
	// may read (grep, sed) but never change. The state folder holds the original, and it cannot be opened up
	// in its place: it sits under /root beside the keys, on a host mount whose files all read as writable.
	private const string kShellReadableDir = "/var/lib/beast/compaction";

	public static string ReadableDir
	{
		get
		{
			return Sandbox.ShellUserActive ? kShellReadableDir : AgentState.CompactionLogsDir;
		}
	}

	// The model reads this path in its prompt. A session ID is the same on every run and its own, whichever order
	// conversations compact in, and a session keeps its file through retries and resumes.
	public static string PathFor(string sessionId)
	{
		return Path.Combine(ReadableDir, $"compaction_{sessionId}.log");
	}

	// Appends a chunk and returns the lines it occupies, as read_file counts them. The range covers the
	// heading and the body, not the divider.
	public (int First, int Last) Add(string heading, string body)
	{
		if (_lines > 0)
		{
			_text.Append(kDivider).Append('\n');
			_lines++;
		}

		int first = _lines + 1;
		_text.Append(heading).Append('\n').Append(body).Append('\n');
		_lines += CountLines(heading) + CountLines(body);
		return (first, _lines);
	}

	// Writes the file the model was pointed at. When that is the sandbox's readable copy, the original is written
	// to the state folder as well, since the copy does not outlive the container.
	public void Save()
	{
		if (_lines > 0)
		{
			string text = _text.ToString();
			if (OperatingSystem.IsLinux() && string.Equals(Path.GetDirectoryName(FilePath), kShellReadableDir, StringComparison.Ordinal))
			{
				EnsureReadableDir();
				File.WriteAllText(FilePath, text);
				File.SetUnixFileMode(FilePath, ReadableFileMode);
				Directory.CreateDirectory(AgentState.CompactionLogsDir);
				File.WriteAllText(Path.Combine(AgentState.CompactionLogsDir, Path.GetFileName(FilePath)), text);
			}
			else
			{
				string? dir = Path.GetDirectoryName(FilePath);
				if (dir != null)
				{
					Directory.CreateDirectory(dir);
				}

				File.WriteAllText(FilePath, text);
			}
		}
	}

	// Rebuilds the readable copies from the state folder at startup: the copy is container-local, so a new
	// container starts without one, and the successors it serves point into it.
	public static void MirrorAll()
	{
		if (Sandbox.ShellUserActive && OperatingSystem.IsLinux() && Directory.Exists(AgentState.CompactionLogsDir))
		{
			EnsureReadableDir();
			foreach (string original in Directory.GetFiles(AgentState.CompactionLogsDir, "compaction_*.log"))
			{
				string copy = Path.Combine(kShellReadableDir, Path.GetFileName(original));
				File.Copy(original, copy, true);
				File.SetUnixFileMode(copy, ReadableFileMode);
			}
		}
	}

	private const UnixFileMode ReadableFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	private static void EnsureReadableDir()
	{
		Directory.CreateDirectory(kShellReadableDir);
		File.SetUnixFileMode(kShellReadableDir, ReadableFileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
	}

	private static int CountLines(string text)
	{
		int lines = 1;
		foreach (char c in text)
		{
			if (c == '\n')
			{
				lines++;
			}
		}
		return lines;
	}
}