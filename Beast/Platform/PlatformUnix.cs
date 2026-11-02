using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TextCopy;


// macOS and Linux. Input is raw stdin (UnixTerminalInput), so it behaves the same in a local terminal and
// over SSH. The rest depends on where the user's desktop is: run locally, the clipboard and speakers are
// this machine's; over SSH they belong to the machine the user is sitting at, which beast cannot reach
// except through the terminal itself.
internal sealed class PlatformUnix : IPlatform
{
	private const string EnableBracketedPaste  = "\x1b[?2004h";
	private const string DisableBracketedPaste = "\x1b[?2004l";

	// Long enough for any local helper (osascript, a sound player's launch); short enough to never hang a paste.
	private const int HelperTimeoutMs = 5000;

	private readonly UnixTerminalInput _input   = new UnixTerminalInput();
	private readonly bool              _overSsh = IsSshSession();

	// Over SSH the user's clipboard cannot be read back, so this stands in for it: the text beast itself
	// last copied (what Shift+click appends to, and what right-click pastes).
	private string _lastCopied = string.Empty;

	// sshd sets these for every session it starts; their presence is what "over SSH" means here.
	private static bool IsSshSession()
	{
		return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION"))
			|| !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"));
	}

	public void EnterTerminalModes()
	{
		_input.EnterRawMode();
		Console.Out.Write(EnableBracketedPaste);
		Console.Out.Flush();
	}

	public void ReapplyTerminalModes()
	{
		EnterTerminalModes();
	}

	public void RestoreTerminalModes()
	{
		Console.Out.Write(DisableBracketedPaste);
		Console.Out.Flush();
		_input.Restore();
	}

	public ConsoleInputEvent? ReadInput(int timeoutMs)
	{
		return _input.Read(timeoutMs);
	}

	public void DiscardPendingInput()
	{
		_input.DiscardPending();
	}

	public int ReadCursorColumn(long deadline)
	{
		return _input.ReadCursorColumn(deadline);
	}

	public string GetClipboardText()
	{
		string text = _lastCopied;
		if (!_overSsh)
		{
			try
			{
				text = ClipboardService.GetText() ?? string.Empty;
			}
			catch (Exception)
			{
				// No clipboard tool on this machine (a Linux box without xclip/xsel/wl-clipboard).
			}
		}
		return text;
	}

	// Locally the system clipboard is set directly (pbcopy on macOS). Over SSH an OSC 52 sequence asks the
	// user's own terminal to set its clipboard — supported by Windows Terminal, iTerm2 (once allowed in its
	// settings), kitty, WezTerm and most modern terminals.
	public void SetClipboardText(string text)
	{
		_lastCopied = text;
		if (_overSsh)
		{
			string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
			Console.Out.Write($"\x1b]52;c;{encoded}\x07");
			Console.Out.Flush();
		}
		else
		{
			try
			{
				ClipboardService.SetText(text);
			}
			catch (Exception)
			{
				// No clipboard tool on this machine; the copy still reaches _lastCopied.
			}
		}
	}

	// A file copied in Finder, on a Mac at its own keyboard. Over SSH, or on Linux, there is no file manager
	// clipboard beast can see; a file dragged onto the terminal still arrives as its pasted path.
	public List<string> GetClipboardFiles()
	{
		List<string> files = new List<string>();
		if (OperatingSystem.IsMacOS() && !_overSsh)
		{
			(int exitCode, string output) = RunHelper("osascript", new string[] { "-e", "POSIX path of (the clipboard as «class furl»)" });
			string path                   = output.Trim();
			if (exitCode == 0 && path.Length > 0 && File.Exists(path))
				files.Add(path);
		}
		return files;
	}

	// A screenshot or copied image (Cmd+Ctrl+Shift+4), on a Mac at its own keyboard, written out as PNG by
	// AppleScript. Empty when the clipboard holds no image, over SSH, and on Linux.
	public string SaveClipboardImage(string folder)
	{
		string saved = string.Empty;
		if (OperatingSystem.IsMacOS() && !_overSsh)
		{
			Directory.CreateDirectory(folder);
			string   path    = Path.Combine(folder, $"clipboard-{Guid.NewGuid():N}.png");
			string   escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
			string[] args    = new string[]
			{
				"-e", "set imageData to the clipboard as «class PNGf»",
				"-e", $"set fileRef to open for access (POSIX file \"{escaped}\") with write permission",
				"-e", "write imageData to fileRef",
				"-e", "close access fileRef"
			};
			(int exitCode, string _) = RunHelper("osascript", args);
			if (exitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0)
				saved = path;
			else if (File.Exists(path))
				File.Delete(path);
		}
		return saved;
	}

	// Sounds play on the machine beast runs on, so only when that is the machine the user is at: over SSH they
	// would sound in an empty room. afplay ships with macOS; on Linux the first available of PulseAudio's,
	// PipeWire's and ALSA's players is used.
	public void PlaySound(IReadOnlyList<string> candidates)
	{
		if (_overSsh)
			return;

		string soundFile = SoundCandidates.FirstExisting(candidates);
		if (soundFile.Length == 0)
			return;

		string[] players = OperatingSystem.IsMacOS()
			? new string[] { "afplay" }
			: new string[] { "paplay", "pw-play", "aplay" };
		foreach (string player in players)
		{
			if (StartDetached(player, soundFile))
				break;
		}
	}

	// Starts a player and lets it run to completion in the background. False when the program is not installed.
	private static bool StartDetached(string fileName, string argument)
	{
		bool started = false;
		try
		{
			Process process = Process.Start(HelperStartInfo(fileName, new string[] { argument }))!;
			process.StandardInput.Close();
			started = true;
			_       = Task.Run(async () =>
			{
				using (process)
				{
					await process.StandardOutput.ReadToEndAsync();
					await process.StandardError.ReadToEndAsync();
					await process.WaitForExitAsync();
				}
			});
		}
		catch (Win32Exception)
		{
			// Not installed: the caller tries the next player.
		}
		return started;
	}

	// Runs a short-lived helper and returns its exit code and stdout; -1 when it cannot be started or overruns.
	private static (int ExitCode, string Output) RunHelper(string fileName, string[] arguments)
	{
		int           exitCode = -1;
		StringBuilder output   = new StringBuilder();
		try
		{
			using Process process       = Process.Start(HelperStartInfo(fileName, arguments))!;
			process.OutputDataReceived += (sender, e) =>
			{
				if (e.Data != null)
				{
					lock (output)
						output.AppendLine(e.Data);
				}
			};
			// stderr is drained (so a chatty helper cannot block on a full pipe) and discarded.
			process.ErrorDataReceived += (sender, e) => { };
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			process.StandardInput.Close();

			if (process.WaitForExit(HelperTimeoutMs))
			{
				// The parameterless wait also waits for the output events to finish arriving.
				process.WaitForExit();
				exitCode = process.ExitCode;
			}
			else
			{
				process.Kill();
			}
		}
		catch (Win32Exception)
		{
		}

		string text;
		lock (output)
			text = output.ToString();
		return (exitCode, text);
	}

	// Every stream is redirected: a child that shares the terminal makes .NET swap the terminal's settings
	// around it, which would knock the input out of raw mode.
	private static ProcessStartInfo HelperStartInfo(string fileName, string[] arguments)
	{
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName               = fileName,
			RedirectStandardInput  = true,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
			CreateNoWindow         = true
		};
		foreach (string argument in arguments)
			psi.ArgumentList.Add(argument);
		return psi;
	}
}