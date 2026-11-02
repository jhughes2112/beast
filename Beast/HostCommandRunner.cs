using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Runs the agent's host_command on this machine. The agent has already put the command past HallMonitor
// (and the user, when HallMonitor was unsure); this side only executes it, streams its output back a few
// lines at a time, and reports the exit code once all of it has been sent. See HostCommands in the agent
// for the wire protocol.
//
// Only a shell from the fixed set below is ever started (powershell, cmd, bash, sh), and only one found
// on this machine. Its executable is resolved here, never named by the agent: the agent picks a shell by
// name and supplies the command text, which reaches the shell as exactly one command, escaped for that
// shell's own command-line rules. Nothing else can be launched through this path.
internal sealed class HostCommandRunner
{
	// Output is sent in batches this often, so a chatty command costs a frame per interval, not per line.
	private const int FlushMs = 50;

	// A single line longer than this is cut, so one runaway line cannot become a giant frame.
	private const int MaxLineChars = 16 * 1024;

	// After the process exits, how long its pipes get to drain; a background child can hold them open.
	private const int DrainGraceMs = 500;

	private enum ShellKind
	{
		PowerShell,
		Cmd,
		Posix
	}

	private sealed class HostShell
	{
		public readonly string    Name;
		public readonly string    Path;
		public readonly ShellKind Kind;

		public HostShell(string name, string path, ShellKind kind)
		{
			Name = name;
			Path = path;
			Kind = kind;
		}
	}

	private readonly Func<string, string, Task>  _sendTo;
	private readonly string                      _cwd;
	private readonly CancellationToken           _lifetime;
	private readonly List<HostShell>             _shells;
	private readonly object                      _lock    = new object();
	private readonly Dictionary<string, Process> _running = new Dictionary<string, Process>(StringComparer.Ordinal);

	public HostCommandRunner(Func<string, string, Task> sendTo, string cwd, CancellationToken lifetime)
	{
		_sendTo   = sendTo;
		_cwd      = cwd;
		_lifetime = lifetime;
		_shells   = DetectShells();
	}

	// The shells this machine has, comma-separated, for the agent to offer: "/host-shells <list>".
	public string ShellNames
	{
		get
		{
			List<string> names = new List<string>();
			foreach (HostShell shell in _shells)
				names.Add(shell.Name);
			return string.Join(",", names);
		}
	}

	// Claims host-run and host-cancel frames; declines every other Config frame.
	public bool OnConfigFrame(string sessionId, string json)
	{
		bool claimed = false;
		if (json.Contains("\"host-run\"", StringComparison.Ordinal) || json.Contains("\"host-cancel\"", StringComparison.Ordinal))
		{
			JsonNode? root = JsonNode.Parse(json);
			string    kind = root?["kind"]?.GetValue<string>() ?? string.Empty;
			string    id   = root?["id"]?.GetValue<string>() ?? string.Empty;
			if (kind == "host-run")
			{
				string shell   = root?["shell"]?.GetValue<string>() ?? string.Empty;
				string command = root?["command"]?.GetValue<string>() ?? string.Empty;
				_              = Task.Run(() => RunAsync(sessionId, id, shell, command));
				claimed        = true;
			}
			else if (kind == "host-cancel")
			{
				Cancel(id);
				claimed = true;
			}
		}
		return claimed;
	}

	private void Cancel(string id)
	{
		Process? process;
		lock (_lock)
			_running.TryGetValue(id, out process);
		if (process != null)
			Cancel(process);
	}

	private static void Cancel(Process process)
	{
		try
		{
			process.Kill(true);
		}
		catch (Exception)
		{
			// Already gone.
		}
	}

	private HostShell? FindShell(string name)
	{
		HostShell? found = null;
		foreach (HostShell shell in _shells)
		{
			if (found == null && string.Equals(shell.Name, name, StringComparison.Ordinal))
				found = shell;
		}
		return found;
	}

	private async Task RunAsync(string sessionId, string id, string shellName, string command)
	{
		int        exitCode = 127;
		string     message  = string.Empty;
		HostShell? shell    = FindShell(shellName);
		if (shell == null)
		{
			message = $"Refused: '{shellName}' is not a shell available on this machine. Available: {ShellNames}.";
		}
		else if (!Directory.Exists(_cwd))
		{
			message = $"Refused: the workspace folder {_cwd} does not exist on this machine.";
		}
		else if (shell.Kind == ShellKind.Cmd && (command.Contains('\n') || command.Contains('\r')))
		{
			message = "Refused: cmd runs a single line. Join the steps with & or &&, or use another shell.";
		}
		else
		{
			try
			{
				exitCode = await ExecuteAsync(sessionId, id, StartInfo(shell, command));
			}
			catch (Exception ex)
			{
				exitCode = 1;
				message  = $"Could not run the command on the host: {ex.Message}";
			}
		}

		try
		{
			await _sendTo(sessionId, $"/host-exit {id} {exitCode} {message.Replace('\n', ' ')}".TrimEnd());
		}
		catch (Exception)
		{
			// The agent is gone; nobody is waiting for the result.
		}
	}

	// How each shell is told to run exactly one command, with output in UTF-8:
	//   bash, sh    -c <command>, passed as one argument (ArgumentList does the quoting).
	//   powershell  -EncodedCommand <base64 of UTF-16LE>, so no quoting rule can split or alter it. The
	//               prefix only silences progress records (which would otherwise arrive as CLIXML on stderr)
	//               and sets UTF-8 output.
	//   cmd         /d /s /c "<command>", written raw: /s strips exactly the outer quotes and runs the rest
	//               as typed, which is cmd's own rule (ArgumentList's backslash escaping is not). /d skips
	//               AutoRun; chcp switches its output to UTF-8.
	private ProcessStartInfo StartInfo(HostShell shell, string command)
	{
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName               = shell.Path,
			WorkingDirectory       = _cwd,
			RedirectStandardInput  = true,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding  = Encoding.UTF8,
			UseShellExecute        = false,
			CreateNoWindow         = true
		};
		if (shell.Kind == ShellKind.Posix)
		{
			psi.ArgumentList.Add("-c");
			psi.ArgumentList.Add(command);
		}
		else if (shell.Kind == ShellKind.PowerShell)
		{
			string script = "$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n" + command;
			psi.ArgumentList.Add("-NoLogo");
			psi.ArgumentList.Add("-NoProfile");
			psi.ArgumentList.Add("-NonInteractive");
			psi.ArgumentList.Add("-EncodedCommand");
			psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
		}
		else
		{
			psi.Arguments = "/d /s /c \"chcp 65001 >nul & " + command + "\"";
		}
		return psi;
	}

	private async Task<int> ExecuteAsync(string sessionId, string id, ProcessStartInfo psi)
	{
		ConcurrentQueue<(bool IsError, string Line)> pending = new ConcurrentQueue<(bool IsError, string Line)>();
		using Process process = new Process { StartInfo = psi };
		process.Start();
		lock (_lock)
			_running[id] = process;

		int exitCode;
		try
		{
			// Nothing can type into it: the keyboard belongs to the TUI, so a command that reads input gets EOF.
			process.StandardInput.Close();

			using CancellationTokenSource drain = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
			Task outReader                      = ReadLinesAsync(process.StandardOutput, false, pending, drain.Token);
			Task errReader                      = ReadLinesAsync( process.StandardError,  true, pending, drain.Token);
			Task exited                         = process.WaitForExitAsync(_lifetime);

			while (!exited.IsCompleted)
			{
				await Task.WhenAny(exited, Task.Delay(FlushMs, _lifetime));
				await FlushAsync(sessionId, id, pending);
			}
			await exited;

			await Task.WhenAny(Task.WhenAll(outReader, errReader), Task.Delay(DrainGraceMs, _lifetime));
			drain.Cancel();
			await FlushAsync(sessionId, id, pending);
			exitCode = process.ExitCode;
		}
		finally
		{
			lock (_lock)
				_running.Remove(id);
			// Beast is closing: the command does not outlive it.
			if (_lifetime.IsCancellationRequested)
				Cancel(process);
		}
		return exitCode;
	}

	private static async Task ReadLinesAsync(StreamReader reader, bool isError, ConcurrentQueue<(bool IsError, string Line)> pending, CancellationToken ct)
	{
		try
		{
			for (; ; )
			{
				string? line = await reader.ReadLineAsync(ct);
				if (line == null)
					break;
				pending.Enqueue((isError, line.Length > MaxLineChars ? line.Substring(0, MaxLineChars) + " …[line cut]" : line));
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (IOException)
		{
			// Pipe torn down by a kill; whatever was read has been queued.
		}
		catch (ObjectDisposedException)
		{
		}
	}

	// Sends what has accumulated, one message per run of lines from the same stream, in arrival order.
	private async Task FlushAsync(string sessionId, string id, ConcurrentQueue<(bool IsError, string Line)> pending)
	{
		StringBuilder batch   = new StringBuilder();
		bool          isError = false;
		while (pending.TryDequeue(out (bool IsError, string Line) item))
		{
			if (batch.Length > 0 && item.IsError != isError)
			{
				await SendBatchAsync(sessionId, id, isError, batch);
				batch.Clear();
			}
			if (batch.Length > 0)
				batch.Append('\n');
			batch.Append(item.Line);
			isError = item.IsError;
		}
		if (batch.Length > 0)
			await SendBatchAsync(sessionId, id, isError, batch);
	}

	private Task SendBatchAsync(string sessionId, string id, bool isError, StringBuilder batch)
	{
		return _sendTo(sessionId, $"/host-output {id} {(isError ? "e" : "o")} {batch}");
	}

	// The shells this machine has, each resolved to a fixed executable. On Windows: PowerShell 7 (pwsh) or
	// else Windows PowerShell, cmd, and Git for Windows' bash and sh. Never C:\Windows\System32\bash.exe,
	// which is WSL and would run the command in a different machine's filesystem. Elsewhere: pwsh when
	// installed, /bin/bash and /bin/sh.
	private static List<HostShell> DetectShells()
	{
		List<HostShell> shells = new List<HostShell>();
		if (OperatingSystem.IsWindows())
		{
			string system     = Environment.GetFolderPath(Environment.SpecialFolder.System);
			string pwsh       = OnPath("pwsh.exe");
			string powershell = pwsh.Length > 0 ? pwsh : FirstExisting(new List<string> { Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe") });
			AddIf(shells, "powershell", powershell, ShellKind.PowerShell);
			AddIf(shells, "cmd", FirstExisting(new List<string> { Path.Combine(system, "cmd.exe") }), ShellKind.Cmd);

			string git = GitRoot();
			if (git.Length > 0)
			{
				AddIf(shells, "bash", FirstExisting(new List<string> { Path.Combine(git, "bin", "bash.exe") }), ShellKind.Posix);
				AddIf(shells, "sh",     FirstExisting(new List<string> { Path.Combine(git, "bin", "sh.exe") }), ShellKind.Posix);
			}
		}
		else
		{
			AddIf(shells, "powershell", OnPath("pwsh"), ShellKind.PowerShell);
			AddIf(shells, "bash", FirstExisting(new List<string> { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash", "/opt/homebrew/bin/bash" }), ShellKind.Posix);
			AddIf(shells, "sh", FirstExisting(new List<string> { "/bin/sh", "/usr/bin/sh" }), ShellKind.Posix);
		}
		return shells;
	}

	private static void AddIf(List<HostShell> shells, string name, string path, ShellKind kind)
	{
		if (path.Length > 0)
			shells.Add(new HostShell(name, path, kind));
	}

	private static string FirstExisting(List<string> candidates)
	{
		string found = string.Empty;
		foreach (string candidate in candidates)
		{
			if (found.Length == 0 && File.Exists(candidate))
				found = candidate;
		}
		return found;
	}

	// The full path of an executable on PATH, or empty.
	private static string OnPath(string fileName)
	{
		List<string> candidates = new List<string>();
		foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			try
			{
				if (Path.IsPathFullyQualified(dir))
					candidates.Add(Path.Combine(dir, fileName));
			}
			catch (ArgumentException)
			{
				// A malformed PATH entry.
			}
		}
		return FirstExisting(candidates);
	}

	// Git for Windows' install folder: a standard location, or wherever the git.exe on PATH lives
	// (<Git>\cmd\git.exe or <Git>\bin\git.exe).
	private static string GitRoot()
	{
		List<string> roots = new List<string>();
		foreach (string variable in new string[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
		{
			string root = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
			if (root.Length > 0)
				roots.Add(Path.Combine(root, "Git"));
		}
		roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git"));
		string gitExe = OnPath("git.exe");
		if (gitExe.Length > 0)
			roots.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitExe) ?? string.Empty, "..")));

		string found = string.Empty;
		foreach (string root in roots)
		{
			if (found.Length == 0 && File.Exists(Path.Combine(root, "bin", "bash.exe")))
				found = root;
		}
		return found;
	}
}