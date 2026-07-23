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

	// PATH for readonly_bash: a directory of symlinks to a curated read-only command set, created in the
	// Dockerfile. Restricted bash resolves only bare command names through PATH, so this directory is the
	// entire universe of programs a locked-down role can run — everything else is simply unreachable.
	private const string ReadonlyBinDir = "/opt/agent-bins/readonly";

	// Lists a folder in `ls -1Ap` form. Empty folder lists the current directory. Implemented over the
	// shell so the output format matches `ls -al` exactly, then capped to MaxLsEntries entries.
	public static async Task<ToolResult> LsAsync(string toolCallId, string folder, CancellationToken cancellationToken)
	{
		string target = string.IsNullOrWhiteSpace(folder) ? "." : folder;
		string escaped = target.Replace("'", "'\\''");
		string command = $"ls -1Ap -- '{escaped}' | awk '{{print ($0 ~ /\\/$/) \"\\t\" $0}}' | sort | cut -f2-";
		ToolResult result = await BashAsync(toolCallId, command, null, cancellationToken);

		if (result.ExitCode != 0 || string.IsNullOrEmpty(result.StdOut))
			return result;

		return CapLsOutput(toolCallId, result);
	}

	// Keeps the leading `total N` line and the first MaxLsEntries entries, dropping the rest with a note.
	// `ls -al` emits one entry per line after the `total` header, so an entry is any other non-empty line.
	private static ToolResult CapLsOutput(string toolCallId, ToolResult result)
	{
		string[] lines = result.StdOut.Replace("\r\n", "\n").Split('\n');

		StringBuilder sb = new StringBuilder();
		int entries = 0;
		int omitted = 0;
		foreach (string line in lines)
		{
			if (line.Length == 0)
				continue;

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
			sb.Append($"[First {MaxLsEntries} entries shown; {omitted} more were omitted. Narrow the pattern or use bash to inspect the rest.]\n");

		return new ToolResult(toolCallId, sb.ToString(), result.StdErr, result.ExitCode, result.MeasuredOutputTokens);
	}

	public static Task<ToolResult> BashAsync(string toolcallid, string command, int? timeoutSeconds, CancellationToken cancellationToken)
	{
		return RunBashAsync(toolcallid, command, timeoutSeconds, false, cancellationToken);
	}

	// Restricted, read-only variant of bash. Launches the shell with -r (restricted mode: no output
	// redirection, no cd, no running a program by an explicit path — only bare names resolved through PATH)
	// and narrows PATH to ReadonlyBinDir, whose symlinks are a curated set of read-only programs. Everything
	// outside that directory is unreachable, so the command universe is exactly the allowlist. This is a
	// guardrail for a cooperative (or worst case, prompt-poisoned) agent, not a hardened sandbox against a
	// determined human attacker.
	public static Task<ToolResult> ReadonlyBashAsync(string toolcallid, string command, int? timeoutSeconds, CancellationToken cancellationToken)
	{
		return RunBashAsync(toolcallid, command, timeoutSeconds, true, cancellationToken);
	}

	// Appends a line to a capped capture buffer. Returns false once the cap is reached — including
	// when a SINGLE oversized line would blow past it, in which case only the room that remains is
	// kept, so the buffer never exceeds the cap by more than a newline.
	private static bool AppendCapped(StringBuilder captured, string line)
	{
		bool appended;
		int room = MaxCapturedChars - captured.Length;
		if (room <= 0)
		{
			appended = false;
		}
		else if (line.Length > room)
		{
			captured.Append(line, 0, room).AppendLine();
			appended = false;
		}
		else
		{
			captured.AppendLine(line);
			appended = true;
		}
		return appended;
	}

	// Renders a captured stream, appending a truncation notice when the capture cap was hit so the
	// model knows the tail is missing rather than the command having produced nothing further.
	private static string CappedText(StringBuilder captured, bool capped, string streamName)
	{
		string text = captured.ToString().TrimEnd();
		if (capped)
			text = text + $"\n[{streamName} truncated: exceeded the {MaxCapturedChars / (1024 * 1024)} MB capture limit]";
		return text;
	}

	private static async Task<ToolResult> RunBashAsync(string toolcallid, string command, int? timeoutSeconds, bool restricted, CancellationToken cancellationToken)
	{
		ToolResult finalResult;

		if (!string.IsNullOrWhiteSpace(command))
		{
			int timeout = timeoutSeconds ?? 0;
			if (timeout <= 0)
			{
				timeout = 120;
			}

			string cwd = Directory.GetCurrentDirectory();

			try
			{
				// ArgumentList passes the command as a single argv entry — no quote escaping,
				// so commands containing quotes, backslashes, or $ arrive at bash verbatim.
				ProcessStartInfo psi = new ProcessStartInfo
				{
					FileName = "/bin/bash",
					WorkingDirectory = cwd,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};
				// -r puts bash in restricted mode; PATH is narrowed so only the read-only allowlist resolves.
				if (restricted)
				{
					psi.ArgumentList.Add("-r");
					psi.Environment["PATH"] = ReadonlyBinDir;
				}

				psi.ArgumentList.Add("-c");
				psi.ArgumentList.Add(command);

				using (Process process = new Process { StartInfo = psi })
				{
					StringBuilder output = new StringBuilder();
					StringBuilder error = new StringBuilder();
					bool outputCapped = false;
					bool errorCapped = false;

					// Completed when the reader delivers its EOF sentinel (a null Data). Process exit
					// alone does not mean the readers are done: without waiting for these, the result
					// could be read while a callback is still appending (a torn read, occasionally
					// losing the tail of the output).
					TaskCompletionSource<bool> outputEof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
					TaskCompletionSource<bool> errorEof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

					// Past the cap the pipes are still drained (so the child never blocks on a full
					// pipe) but the data is discarded — a runaway command like `yes` must not grow
					// the buffers until the container runs out of memory. The token-budget truncation
					// downstream would clip the result anyway; this bounds what is ever held.
					process.OutputDataReceived += (_, e) =>
					{
						if (e.Data == null)
							outputEof.TrySetResult(true);
						else if (!AppendCapped(output, e.Data))
							outputCapped = true;
					};
					process.ErrorDataReceived += (_, e) =>
					{
						if (e.Data == null)
							errorEof.TrySetResult(true);
						else if (!AppendCapped(error, e.Data))
							errorCapped = true;
					};

					process.Start();
					process.BeginOutputReadLine();
					process.BeginErrorReadLine();

					using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
					{
						cts.CancelAfter(TimeSpan.FromSeconds(timeout));

						bool processCompleted = false;
						bool timedOut = false;

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
								throw;
							}
							else
							{
								timedOut = true;
							}
						}

						// Wait for the readers' EOF sentinels so the builders are complete before they are
						// read. Bounded: a backgrounded grandchild can inherit the pipe and hold it open
						// long after the command itself exited, and its future output is rightly lost.
						// Cancelling the reads afterwards stops any still-open reader from appending.
						await Task.WhenAny(Task.WhenAll(outputEof.Task, errorEof.Task), Task.Delay(500));
						try
						{
							process.CancelOutputRead();
							process.CancelErrorRead();
						}
						catch (InvalidOperationException)
						{
						}

						if (processCompleted)
						{
							string result = CappedText(output, outputCapped, "stdout");
							string err = CappedText(error, errorCapped, "stderr");

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
							string err = CappedText(error, errorCapped, "stderr");
							string stdOut = string.IsNullOrEmpty(result) ? string.Empty : result;
							string stdErr = string.IsNullOrEmpty(err) ? string.Empty : err;

							string timeoutMessage = $"Error: Command timed out after {timeout} seconds.";
							finalResult = new ToolResult(toolcallid, stdOut, stdErr + (string.IsNullOrEmpty(stdErr) ? "" : "\n") + timeoutMessage, 1, 0);
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
			string note = ReadonlyBoundaryNote();
			string mergedErr = string.IsNullOrEmpty(finalResult.StdErr) ? note : finalResult.StdErr + "\n" + note;
			finalResult = new ToolResult(toolcallid, finalResult.StdOut, mergedErr, finalResult.ExitCode, finalResult.MeasuredOutputTokens);
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
			boundary = true;
		else
			boundary = result.StdErr.Contains("command not found", StringComparison.Ordinal) || result.StdErr.Contains("restricted", StringComparison.Ordinal);

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
				names.Add(Path.GetFileName(path));
		}
		catch
		{
		}

		names.Sort(StringComparer.Ordinal);
		string list = names.Count > 0 ? string.Join(" ", names) : "(none found)";
		return $"[readonly_bash: restricted, read-only shell. No redirection, cd, or running programs with full paths allowed. These are the only commands allowed: {list}]";
	}
}