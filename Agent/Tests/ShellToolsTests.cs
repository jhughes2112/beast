using System;
using System.IO;
using System.Threading;


public static class ShellToolsTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  ShellToolsTests");

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
		ToolResult emptyCmd = ShellTools.BashAsync("", null, null, CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(emptyCmd.Response.Contains("Error") && emptyCmd.Response.Contains("empty"), "ShellTools: empty command returns error");

		// Non-existent working directory.
		ToolResult badDir = ShellTools.BashAsync("echo test", "/nonexistent/path/that/does/not/exist", null, CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(badDir.Response.Contains("Error"), "ShellTools: non-existent workDir returns error");
	}

	private static void TestRunCommand(TestContext ctx)
	{
		// Simple echo — may or may not work depending on WSL/bash availability.
		ToolResult echoResult = ShellTools.BashAsync("echo hello", null, null, CancellationToken.None).GetAwaiter().GetResult();
		bool validResponse = echoResult.Response.Contains("Exit Code:") || echoResult.Response.Contains("Error:");
		ctx.Assert(validResponse, "ShellTools: echo returns valid response format");

		// If the command succeeded, verify output was captured.
		if (echoResult.Response.Contains("Exit Code: 0"))
		{
			ctx.Assert(echoResult.Response.Contains("hello"), "ShellTools: echo output captured");
		}
	}
}
