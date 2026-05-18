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
	public static async Task<ToolResult> BashAsync(
		[Description("The shell command to execute (e.g. \"ls -la\", \"git status\").")] string command,
		[Description("Optional working directory for the command.")] string? workingDir,
		[Description("Optional timeout in seconds (default 60).")] int? timeoutSeconds,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(command))
			return new ToolResult("Error: command cannot be empty.", false);

		int timeout = timeoutSeconds ?? 60;
		if (timeout <= 0) timeout = 60;

		string cwd = string.IsNullOrWhiteSpace(workingDir) ? Directory.GetCurrentDirectory() : workingDir;

		if (!Directory.Exists(cwd))
			return new ToolResult($"Error: working directory does not exist: {cwd}", false);

		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "/bin/bash",
				Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
				WorkingDirectory = cwd,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			using var process = new Process { StartInfo = psi };
			var output = new StringBuilder();
			var error = new StringBuilder();

			process.OutputDataReceived += (_, e) =>
			{
				if (e.Data != null) output.AppendLine(e.Data);
			};
			process.ErrorDataReceived += (_, e) =>
			{
				if (e.Data != null) error.AppendLine(e.Data);
			};

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			cts.CancelAfter(TimeSpan.FromSeconds(timeout));

			try
			{
				await process.WaitForExitAsync(cts.Token);
			}
			catch (OperationCanceledException)
			{
				if (cancellationToken.IsCancellationRequested)
					throw;

				try { process.Kill(true); } catch { }
				return new ToolResult($"Error: Command timed out after {timeout}s.", false);
			}

			string result = output.ToString().TrimEnd();
			string err = error.ToString().TrimEnd();

			if (process.ExitCode != 0)
			{
				return new ToolResult(
					$"Exit Code: {process.ExitCode}\n\nStdout:\n{result}\n\nStderr:\n{err}",
					false);
			}

			string stdout = string.IsNullOrEmpty(result) ? "(no output)" : result;
			return new ToolResult($"Exit Code: 0\n\n{stdout}", false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return new ToolResult($"Error: {ex.Message}", false);
		}
	}
}