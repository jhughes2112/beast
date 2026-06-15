using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Shell tools — all static methods.
public static class ShellTools
{
	// Lists a folder in `ls -al` form. Empty folder lists the current directory. Implemented over the
	// shell so the output format matches `ls -al` exactly.
	public static Task<ToolResult> LsAsync(string toolCallId, string folder, CancellationToken cancellationToken)
	{
		string target = string.IsNullOrWhiteSpace(folder) ? "." : folder;
		string escaped = target.Replace("'", "'\\''");
		string command = $"ls -al -- '{escaped}'";
		return BashAsync(toolCallId, command, null, cancellationToken);
	}

	[Description("""
		Standard bash command. CWD is /workspace/
		""")]
	public static async Task<ToolResult> BashAsync(
		string toolcallid,
		[Description("Shell command to execute")] string command,
		[Description("Timeout in seconds (default 120).")] int? timeoutSeconds,
		CancellationToken cancellationToken)
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
				psi.ArgumentList.Add("-c");
				psi.ArgumentList.Add(command);

				using (Process process = new Process { StartInfo = psi })
				{
					StringBuilder output = new StringBuilder();
					StringBuilder error = new StringBuilder();

					process.OutputDataReceived += (_, e) =>
					{
						if (e.Data != null)
						{
							output.AppendLine(e.Data);
						}
					};
					process.ErrorDataReceived += (_, e) =>
					{
						if (e.Data != null)
						{
							error.AppendLine(e.Data);
						}
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
							if (cancellationToken.IsCancellationRequested)
							{
								throw;
							}
							else
							{
								timedOut = true;
								try
								{
									process.Kill(true);
								}
								catch
								{
								}
							}
						}

						if (processCompleted)
						{
							string result = output.ToString().TrimEnd();
							string err = error.ToString().TrimEnd();

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
							string result = output.ToString().TrimEnd();
							string err = error.ToString().TrimEnd();
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
				finalResult = new ToolResult(toolcallid, string.Empty, $"Error: Failed to execute command. {ex.GetType().Name}: {ex.Message}", 1, 0);
			}
		}
		else
		{
			finalResult = new ToolResult(toolcallid, string.Empty, "Error: Command cannot be empty or whitespace.", 1, 0);
		}

		return finalResult;
	}
}