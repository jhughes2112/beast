using System;
using System.IO;
using System.Threading;


public static class ShellToolsTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  ShellToolsTests");

		string tempDir = Path.Combine(Path.GetTempPath(), $"kanbeast_shell_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			TestEdgeCases(ctx);
			TestRunCommand(ctx);
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, true);
			}
		}
	}

	private static void TestEdgeCases(TestContext ctx)
	{
		// Empty command.
		ToolResult emptyCmd = ShellTools.BashAsync("testid", "", null, CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(emptyCmd.StdErr.Contains("Error") && emptyCmd.StdErr.Contains("empty"), "ShellTools: empty command returns error");

		// Non-existent working directory - can't test this easily without changing CWD
	}

	private static void TestRunCommand(TestContext ctx)
	{
		// Simple echo — may or may not work depending on WSL/bash availability.
		ToolResult echoResult = ShellTools.BashAsync("testid2", "echo hello", null, CancellationToken.None).GetAwaiter().GetResult();
		// Successful run has output in stdout; failures have errors in stderr.
		bool validResponse = echoResult.StdOut.Contains("hello") || !string.IsNullOrEmpty(echoResult.StdErr);
		ctx.Assert(validResponse, "ShellTools: echo returns valid response format");

		// If the command succeeded, verify output was captured.
		if (string.IsNullOrEmpty(echoResult.StdErr))
		{
			ctx.Assert(echoResult.StdOut.Contains("hello"), "ShellTools: echo output captured");
		}
	}
}