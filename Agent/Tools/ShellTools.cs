using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Shell tools — all static methods.
public static class ShellTools
{
	// Most entries a single ls returns before the rest are omitted with a mention, so a huge directory
	// cannot flood the context.
	private const int MaxLsEntries = 100;

	// Upper bound on captured stdout/stderr per stream (chars, ~4 MB). Output past this is drained
	// and discarded so a runaway command cannot exhaust container memory before the token-budget
	// truncation ever sees the result.
	private const int MaxCapturedChars = 4 * 1024 * 1024;

	private static readonly System.Text.RegularExpressions.Regex kScreenshotFlag = new System.Text.RegularExpressions.Regex(@"(?<![\w-])--screenshot(?![\w-])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	private const string kScreenshotRefusal = "This command was not run because it uses --screenshot, and this seeded run has to repeat exactly. Use the browser_screenshot tool for page screenshots: give it the page, an output_path under /tmp/, a number of steps and step_ms. For example, 60 steps of 16.7 ms is one second of page time.";

	// A browser's own --screenshot captures whenever it is ready, so in a seeded run its picture would differ.
	private static bool RefusedInSeededRun(string command)
	{
		return Sandbox.CountingTime && kScreenshotFlag.IsMatch(command);
	}

	// PATH for readonly_bash: a directory of symlinks to a curated read-only command set, created in the
	// Dockerfile. Restricted bash resolves only bare command names through PATH, so this directory is the
	// entire universe of programs a locked-down role can run — everything else is simply unreachable.
	private const string ReadonlyBinDir = "/opt/agent-bins/readonly";

	// Lists a folder in `ls -1Ap` form. Empty folder lists the current directory. Implemented over the
	// shell so the output format matches `ls -al` exactly, then capped to MaxLsEntries entries.
	public static async Task<ToolResult> LsAsync(string toolCallId, string folder, CancellationToken cancellationToken)
	{
		string target  = string.IsNullOrWhiteSpace(folder) ? "." : folder;
		string escaped = target.Replace("'", "'\\''");
		string command = $"ls -1Ap -- '{escaped}' | awk '{{print ($0 ~ /\\/$/) \"\\t\" $0}}' | sort | cut -f2-";
		// The shell user's shell, but without BashAsync's dot-folder sweep or a namespace: a listing creates
		// nothing and starts nothing that could linger.
		ToolResult result = await RunBashAsync(toolCallId, command, null, false, true, false, string.Empty, cancellationToken);

		if (result.ExitCode != 0 || string.IsNullOrEmpty(result.StdOut))
		{
			return result;
		}

		return CapLsOutput(toolCallId, result);
	}

	// Keeps the leading `total N` line and the first MaxLsEntries entries, dropping the rest with a note.
	// `ls -al` emits one entry per line after the `total` header, so an entry is any other non-empty line.
	private static ToolResult CapLsOutput(string toolCallId, ToolResult result)
	{
		string[] lines = result.StdOut.Replace("\r\n", "\n").Split('\n');

		StringBuilder sb      = new StringBuilder();
		int           entries = 0;
		int           omitted = 0;
		foreach (string line in lines)
		{
			if (line.Length == 0)
			{
				continue;
			}

			bool isEntry = !line.StartsWith("total ", StringComparison.Ordinal);
			if (isEntry)
			{
				if (entries >= MaxLsEntries)
				{
					omitted++;
					continue;
				}
				entries++;
			}

			sb.Append(line);
			sb.Append('\n');
		}

		if (omitted > 0)
		{
			sb.Append($"[First {MaxLsEntries} entries shown; {omitted} more were omitted. Narrow the pattern or use bash to inspect the rest.]\n");
		}

		return new ToolResult(toolCallId, sb.ToString(), result.StdErr, result.ExitCode, result.MeasuredOutputTokens);
	}

	// A shell for anything the model can cause to run: it runs as the sandbox's shell user, which
	// cannot read the API keys or the agent's state and cannot touch the git repository. A top-level
	// dot-folder the command created is handed to root before the result goes back, and the model is
	// told so — otherwise it would only find out when its next write there failed. It runs in the session's
	// own PID namespace (SessionProcesses), and what earlier commands left running is named after the output.
	public static async Task<ToolResult> BashAsync(string toolcallid, string command, int? timeoutSeconds, string sessionId, CancellationToken cancellationToken)
	{
		if (RefusedInSeededRun(command))
		{
			return new ToolResult(toolcallid, string.Empty, kScreenshotRefusal, 1, 0);
		}

		ToolResult result     = await RunBashAsync(toolcallid, command, timeoutSeconds, false, true, false, sessionId, cancellationToken);
		string     sealedNote = await Sandbox.SealNewDotFoldersAsync(cancellationToken);
		string     stdOut     = WithStillRunning(Sandbox.SteadyShellOutput(result.StdOut), sessionId);
		string     stdErr     = Sandbox.SteadyShellOutput(result.StdErr);
		if (sealedNote.Length > 0)
		{
			stdErr = string.IsNullOrEmpty(stdErr) ? sealedNote : stdErr + "\n" + sealedNote;
		}

		return new ToolResult(toolcallid, stdOut, stdErr, result.ExitCode, result.MeasuredOutputTokens);
	}

	// A job's limit when the call names none: how long the session may wait on it before it is stopped.
	private const int kDefaultBackgroundSeconds = 3600;

	// bash with run_in_background: the command starts in the session's namespace, its output goes to a log
	// file, and the call returns at once. SessionProcesses watches it and tells the session when it ends or
	// reaches its limit, so the model can end its turn and wait instead of polling. readOnlyUser runs it as
	// the read-only roles' user. The restricted shell cannot redirect output, so it has no background jobs.
	public static async Task<ToolResult> BashInBackgroundAsync(string toolcallid, string command, int? timeoutSeconds, bool readOnlyUser, Session owner, CancellationToken cancellationToken)
	{
		ToolResult result;
		if (string.IsNullOrWhiteSpace(command))
		{
			result = new ToolResult(toolcallid, string.Empty, "Error: Command cannot be empty or whitespace.", 1, 0);
		}
		else if (RefusedInSeededRun(command))
		{
			result = new ToolResult(toolcallid, string.Empty, kScreenshotRefusal, 1, 0);
		}
		else
		{
			int    job    = SessionProcesses.NextJob(owner.Id);
			int    limit  = timeoutSeconds.HasValue && timeoutSeconds.Value > 0 ? timeoutSeconds.Value : kDefaultBackgroundSeconds;
			string log    = Path.Combine(Path.GetTempPath(), $"beast-job-{owner.Id}-{job}.log");
			string status = $"{log}.exit";

			// Job numbers start again with a new agent process, so a file a job of the same number left in an
			// earlier run would read as this one having finished.
			DeleteQuietly(log);
			DeleteQuietly(status);
			ProcessStartInfo psi = await ShellStartAsync(SessionProcesses.Wrap(command, log, status), false, true, readOnlyUser, owner.Id, cancellationToken);
			SessionProcesses.Watch(owner, job, psi, command, log, status, limit);

			string text = $"Started background job {job}. You will get a message when it finishes, or if it is still running after {limit} seconds, when it is stopped; until then you can end your turn and wait, or keep working. Its output goes to {log}.";
			result      = new ToolResult(toolcallid, text, string.Empty, 0, ToolDispatch.EstimateTokens(text));
		}
		return result;
	}

	private static void DeleteQuietly(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
		}
	}

	// A shell for the agent's own work — creating and removing the worktree, commit_and_rebase, the
	// sandbox lockdown, apt installs — which needs root. Never hand this a command the model wrote.
	public static Task<ToolResult> AgentBashAsync(string toolcallid, string command, int? timeoutSeconds, CancellationToken cancellationToken)
	{
		return RunBashAsync(toolcallid, command, timeoutSeconds, false, false, false, string.Empty, cancellationToken);
	}

	// A program's full path, found through the agent's own PATH; a path already given is returned as it is.
	private static string FullPath(string program)
	{
		string found = program;
		if (!Path.IsPathRooted(program))
		{
			foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
			{
				string candidate = Path.Combine(dir, program);
				if (found == program && dir.Length > 0 && File.Exists(candidate))
				{
					found = candidate;
				}
			}
		}
		return found;
	}

	// A command's output with the session's still-running note after it, when there is one.
	private static string WithStillRunning(string stdOut, string sessionId)
	{
		string note = sessionId.Length > 0 ? SessionProcesses.StillRunningNote(sessionId) : string.Empty;
		return note.Length == 0 ? stdOut : (stdOut.Length > 0 ? $"{stdOut}\n{note}" : note);
	}

	// The read-only roles' bash. Where the sandbox can enforce it (Sandbox.ReadOnlyShellAvailable) this is a
	// plain bash run as the read-only user, which may read the whole workspace and run anything but change
	// nothing there: the commands models already know, redirection and cd included, simply work, and a write
	// to the workspace fails with "Permission denied". Elsewhere it falls back to the restricted shell: -r
	// (no output redirection, no cd, no running a program by an explicit path) with PATH narrowed to
	// ReadonlyBinDir, whose symlinks are a curated set of read-only programs. Both are guardrails for a
	// cooperative (or worst case, prompt-poisoned) agent, not a hardened sandbox against a determined human.
	public static async Task<ToolResult> ReadonlyBashAsync(string toolcallid, string command, int? timeoutSeconds, string sessionId, CancellationToken cancellationToken)
	{
		if (RefusedInSeededRun(command))
		{
			return new ToolResult(toolcallid, string.Empty, kScreenshotRefusal, 1, 0);
		}

		bool       readOnlyUser = Sandbox.ReadOnlyShellAvailable;
		ToolResult result       = await RunBashAsync(toolcallid, command, timeoutSeconds, !readOnlyUser, true, readOnlyUser, sessionId, cancellationToken);
		return new ToolResult(toolcallid, WithStillRunning(Sandbox.SteadyShellOutput(result.StdOut), sessionId), Sandbox.SteadyShellOutput(result.StdErr), result.ExitCode, result.MeasuredOutputTokens);
	}

	// Drains a redirected stream into a capped builder in fixed-size chunks; returns true when the
	// cap was hit and later output discarded. Chunking (not line events) bounds peak memory even
	// for a stream with no newlines — a line reader materializes the entire line before any cap
	// can apply — and makes the cap exact. Draining always continues to EOF or cancellation so the
	// child never blocks on a full pipe. Cancellation is swallowed: it is the bounded stop after
	// process exit, and whatever was captured up to that point is the result.
	private static async Task<bool> DrainCappedAsync(StreamReader reader, StringBuilder sink, CancellationToken ct)
	{
		bool   capped = false;
		char[] buffer = new char[8192];
		try
		{
			for (; ; )
			{
				int count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
				if (count <= 0)
				{
					break;
				}

				int room = MaxCapturedChars - sink.Length;
				if (room > 0)
				{
					sink.Append(buffer, 0, Math.Min(count, room));
				}

				if (count > room)
				{
					capped = true;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (IOException)
		{
			// Pipe torn down by a process kill: keep what was captured.
		}
		catch (ObjectDisposedException)
		{
			// An abandoned drain (user cancel unwound the tool) can race process disposal.
		}
		return capped;
	}

	// Renders a captured stream, appending a truncation notice when the capture cap was hit so the
	// model knows the tail is missing rather than the command having produced nothing further.
	private static string CappedText(StringBuilder captured, bool capped, string streamName)
	{
		string text = captured.ToString().TrimEnd();
		if (capped)
		{
			text = text + $"\n[{streamName} truncated: exceeded the {MaxCapturedChars / (1024 * 1024)} MB capture limit]";
		}

		return text;
	}

	// How a shell command is started: bash (restricted or not) as the right user, inside the session's PID
	// namespace when it has one. sessionId names that session; "" runs the command outside any.
	private static async Task<ProcessStartInfo> ShellStartAsync(string command, bool restricted, bool asShellUser, bool readOnlyUser, string sessionId, CancellationToken cancellationToken)
	{
		// ArgumentList passes the command as a single argv entry — no quote escaping,
		// so commands containing quotes, backslashes, or $ arrive at bash verbatim.
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName               = "/bin/bash",
			WorkingDirectory       = Directory.GetCurrentDirectory(),
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
			CreateNoWindow         = true
		};
		// -r puts bash in restricted mode; PATH is narrowed so only the read-only allowlist resolves.
		if (restricted)
		{
			psi.ArgumentList.Add("-r");
			psi.Environment["PATH"] = ReadonlyBinDir;
		}

		psi.ArgumentList.Add("-c");
		psi.ArgumentList.Add(command);

		if (asShellUser)
		{
			Sandbox.RunAsShellUser(psi, restricted, readOnlyUser);

			// Inside the session's own PID namespace, so nothing it starts outlives the session's work. The
			// program nsenter runs is named by its full path: nsenter looks it up through the command's own
			// PATH, which the restricted shell narrows to its allowlist.
			int namespaceInit = await SessionProcesses.EnterAsync(sessionId, cancellationToken);
			if (namespaceInit > 0)
			{
				psi.ArgumentList.Insert(0,      FullPath(psi.FileName));
				psi.ArgumentList.Insert(0,                        "--");
				psi.ArgumentList.Insert(0,                     "--pid");
				psi.ArgumentList.Insert(0, $"--target={namespaceInit}");
				psi.FileName = "nsenter";
			}
		}
		return psi;
	}

	// sessionId names the session whose namespace the command runs in; "" runs it outside any (SessionProcesses).
	private static async Task<ToolResult> RunBashAsync(string toolcallid, string command, int? timeoutSeconds, bool restricted, bool asShellUser, bool readOnlyUser, string sessionId, CancellationToken cancellationToken)
	{
		ToolResult finalResult;

		if (!string.IsNullOrWhiteSpace(command))
		{
			int timeout = timeoutSeconds ?? 0;
			if (timeout <= 0)
			{
				timeout = 120;
			}

			try
			{
				ProcessStartInfo psi = await ShellStartAsync(command, restricted, asShellUser, readOnlyUser, sessionId, cancellationToken);
				using (Process process = new Process { StartInfo = psi })
				{
					StringBuilder output = new StringBuilder();
					StringBuilder error  = new StringBuilder();

					process.Start();

					// Each stream gets its own chunked drain task; a builder is written only by its
					// own task, and both tasks are awaited before the builders are read.
					using CancellationTokenSource drainCts = new CancellationTokenSource();
					Task<bool> outputDrain                 = DrainCappedAsync(process.StandardOutput, output, drainCts.Token);
					Task<bool> errorDrain                  = DrainCappedAsync( process.StandardError,  error, drainCts.Token);

					using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
					{
						cts.CancelAfter(TimeSpan.FromSeconds(timeout));

						bool processCompleted = false;
						bool timedOut         = false;

						try
						{
							await process.WaitForExitAsync(cts.Token);
							processCompleted = true;
						}
						catch (OperationCanceledException)
						{
							// Kill the process tree whether this was a user /cancel or a timeout — otherwise the
							// command keeps running orphaned after we stop waiting on it.
							try
							{
								process.Kill(true);
							}
							catch
							{
							}

							if (cancellationToken.IsCancellationRequested)
							{
								drainCts.Cancel();
								throw;
							}
							else
							{
								timedOut = true;
							}
						}

						// Give the readers a moment to reach EOF after exit/kill, then stop and await
						// them. Bounded: a backgrounded grandchild can inherit the pipe and hold it open
						// long after the command itself exited, and its future output is rightly lost.
						// Awaiting the drain tasks guarantees nothing is appending to the builders when
						// they are read below.
						await Task.WhenAny(Task.WhenAll(outputDrain, errorDrain), Task.Delay(500));
						drainCts.Cancel();
						bool outputCapped = await outputDrain;
						bool errorCapped  = await errorDrain;

						if (processCompleted)
						{
							string result = CappedText(output, outputCapped, "stdout");
							string err    = CappedText( error,  errorCapped, "stderr");

							// Use string.Empty for no output to avoid nulls.
							string stdOut = string.IsNullOrEmpty(result) ? string.Empty : result;
							string stdErr = string.IsNullOrEmpty(err) ? string.Empty : err;

							if (process.ExitCode == 0)
							{
								finalResult = new ToolResult(toolcallid, stdOut, stdErr, 0, 0);
							}
							else
							{
								finalResult = new ToolResult(toolcallid, stdOut, stdErr, process.ExitCode, 0);
							}
						}
						else if (timedOut)
						{
							string result = CappedText(output, outputCapped, "stdout");
							string err    = CappedText( error,  errorCapped, "stderr");
							string stdOut = string.IsNullOrEmpty(result) ? string.Empty : result;
							string stdErr = string.IsNullOrEmpty(err) ? string.Empty : err;

							string timeoutMessage = $"Error: Command timed out after {timeout} seconds.";
							finalResult           = new ToolResult(toolcallid, stdOut, stdErr + (string.IsNullOrEmpty(stdErr) ? "" : "\n") + timeoutMessage, 1, 0);
						}
						else
						{
							finalResult = new ToolResult(toolcallid, string.Empty, "Error: Process termination failed for unknown reason.", 1, 0);
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				finalResult = new ToolResult(toolcallid, string.Empty, $"Error: Failed to execute command. {ex.GetType().Name}: {ex}", 1, 0);
			}
		}
		else
		{
			finalResult = new ToolResult(toolcallid, string.Empty, "Error: Command cannot be empty or whitespace.", 1, 0);
		}

		// In restricted mode, a command not found or rejected by the shell means the model walked into the
		// allowlist boundary. Tell it the full set up front so it adapts instead of probing to find the edges.
		if (restricted && IsBoundaryFailure(finalResult))
		{
			string note      = ReadonlyBoundaryNote();
			string mergedErr = string.IsNullOrEmpty(finalResult.StdErr) ? note : finalResult.StdErr + "\n" + note;
			finalResult      = new ToolResult(toolcallid, finalResult.StdOut, mergedErr, finalResult.ExitCode, finalResult.MeasuredOutputTokens);
		}

		return finalResult;
	}

	// A restricted-shell failure is the boundary case (unknown command, or one rbash refused: cd, redirection,
	// a path-qualified name) rather than an ordinary nonzero exit like grep finding no match. Exit 127 is
	// "command not found"; "restricted" is what rbash prints when it blocks cd/redirection/path execution.
	// The stderr markers are checked regardless of exit code: a pipeline ("dotnet ... | tail") exits with the
	// last stage's status, so a missing command or rejected redirection upstream leaves the marker in stderr
	// while the overall exit code is 0 — without this the boundary note would never reach the model.
	private static bool IsBoundaryFailure(ToolResult result)
	{
		bool boundary;
		if (result.ExitCode == 127)
		{
			boundary = true;
		}
		else
		{
			boundary = result.StdErr.Contains("command not found", StringComparison.Ordinal) || result.StdErr.Contains("restricted", StringComparison.Ordinal);
		}

		return boundary;
	}

	// Lists the readonly_bash allowlist by reading the symlink directory directly (no extra shell spawn), so
	// the boundary message names exactly what is runnable on this image.
	private static string ReadonlyBoundaryNote()
	{
		List<string> names = new List<string>();
		try
		{
			foreach (string path in Directory.GetFileSystemEntries(ReadonlyBinDir))
			{
				names.Add(Path.GetFileName(path));
			}
		}
		catch
		{
		}

		names.Sort(StringComparer.Ordinal);
		string list = names.Count > 0 ? string.Join(" ", names) : "(none found)";
		return $"[readonly_bash: restricted, read-only shell. No redirection, cd, or running programs with full paths allowed. These are the only commands allowed: {list}]";
	}
}