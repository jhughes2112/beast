using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Runs real commands through HostCommandRunner on this machine, in every shell it finds, with the test
// standing in for the agent: it sends host-run and host-cancel frames and reads back what the runner sends.
public static class HostCommandRunnerTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  HostCommandRunnerTests");
		Task.Run(() => TestAsync(ctx)).GetAwaiter().GetResult();
	}

	// Per shell: a command whose quotes, spaces, backslash and dollar sign must all arrive intact, the text
	// it should print, and one that writes to stderr and exits 4.
	private static readonly Dictionary<string, (string Quoting, string Expected, string Failing)> Cases = new Dictionary<string, (string Quoting, string Expected, string Failing)>
	{
		["bash"]       = ("echo \"a  \\\"b\\\" c\\\\d \\$e\" 'f g'",    "a  \"b\" c\\d $e f g", "echo oops >&2; exit 4"),
		["sh"]         = ("echo \"a  \\\"b\\\" c\\\\d \\$e\" 'f g'",    "a  \"b\" c\\d $e f g", "echo oops >&2; exit 4"),
		["powershell"] = ("Write-Output ('a  \"b\" c\\d $e' + ' f g')", "a  \"b\" c\\d $e f g", "[Console]::Error.WriteLine('oops'); exit 4"),
		["cmd"]        = ("echo a  \"b\" c\\d $e f g",                  "a  \"b\" c\\d $e f g", "echo oops 1>&2 & exit /b 4")
	};

	private static async Task TestAsync(TestContext ctx)
	{
		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
		ConcurrentQueue<string> sent      = new ConcurrentQueue<string>();
		string                  cwd       = Path.Combine(Path.GetTempPath(), $"beast-host-{Guid.NewGuid():N}");
		Directory.CreateDirectory(cwd);
		try
		{
			HostCommandRunner runner = new HostCommandRunner((sessionId, text) => { sent.Enqueue(sessionId + "|" + text); return Task.CompletedTask; }, cwd, cts.Token);
			string[]          shells = runner.ShellNames.Split(',', StringSplitOptions.RemoveEmptyEntries);
			ctx.Log($"    shells found: {runner.ShellNames}");
			ctx.Assert(shells.Length > 0, "host runner: finds at least one shell");
			foreach (string shell in shells)
				ctx.Assert(Cases.ContainsKey(shell), $"host runner: '{shell}' is one of the four allowed shells");

			ctx.Assert(!runner.OnConfigFrame("s1", "{\"kind\":\"catalog\"}"), "host runner: other Config frames are left for the display");

			int n = 0;
			foreach (string shell in shells)
			{
				(string quoting, string expected, string failing) = Cases[shell];

				string       id  = $"q{n++}";
				List<string> run = await RunAsync(runner, sent, id, shell, quoting, cts.Token);
				ctx.Assert(run.Contains($"s1|/host-output {id} o {expected}") && run[run.Count - 1] == $"s1|/host-exit {id} 0", $"host runner: {shell} receives the command intact ({string.Join(" / ", run)})");

				id  = $"f{n++}";
				run = await RunAsync(runner, sent, id, shell, failing, cts.Token);
				ctx.Assert(Contains(run, $"/host-output {id} e oops") && run[run.Count - 1] == $"s1|/host-exit {id} 4", $"host runner: {shell} reports stderr and the exit code ({string.Join(" / ", run)})");
			}

			// The working folder, and stdin closed (a read gets end-of-file instead of waiting), in a POSIX shell.
			string posix = Array.IndexOf(shells, "bash") >= 0 ? "bash" : Array.IndexOf(shells, "sh") >= 0 ? "sh" : string.Empty;
			if (posix.Length > 0)
			{
				List<string> run = await RunAsync(runner, sent, "w1", posix, "touch made-here; read x; echo got:$x", cts.Token);
				ctx.Assert(File.Exists(Path.Combine(cwd, "made-here")) && Contains(run, "got:"), $"host runner: runs in the workspace folder with stdin closed ({string.Join(" / ", run)})");
			}

			// Anything but a detected shell is refused without starting a process.
			List<string> refused = await RunAsync(runner, sent, "x1", "python", "print(1)", cts.Token);
			ctx.Assert(refused.Count == 1 && refused[0].StartsWith("s1|/host-exit x1 127 Refused", StringComparison.Ordinal), $"host runner: an unknown shell is refused ({string.Join(" / ", refused)})");
			if (Array.IndexOf(shells, "cmd") >= 0)
			{
				List<string> twoLines = await RunAsync(runner, sent, "x2", "cmd", "echo a\necho b", cts.Token);
				ctx.Assert(twoLines.Count == 1 && twoLines[0].Contains("single line"), "host runner: cmd refuses a multi-line command");
			}

			// Cancel stops it promptly.
			if (posix.Length > 0)
			{
				Stopwatch watch = Stopwatch.StartNew();
				runner.OnConfigFrame("s1", RunFrame("c1", posix, "echo start; sleep 30"));
				await WaitForAsync(sent, "/host-output c1 o start", cts.Token);
				ctx.Assert(runner.OnConfigFrame("s1", "{\"kind\":\"host-cancel\",\"id\":\"c1\"}"), "host runner: claims host-cancel");
				List<string> cancelled = await WaitForExitAsync(sent, "c1", cts.Token);
				ctx.Assert(watch.Elapsed < TimeSpan.FromSeconds(10) && !cancelled[cancelled.Count - 1].EndsWith(" 0", StringComparison.Ordinal), $"host runner: a cancelled command stops and reports a failure ({cancelled[cancelled.Count - 1]}, {watch.Elapsed.TotalSeconds:F1}s)");
			}
		}
		finally
		{
			try
			{
				Directory.Delete(cwd, true);
			}
			catch (IOException)
			{
			}
		}
	}

	private static string RunFrame(string id, string shell, string command)
	{
		return new JsonObject { ["kind"] = "host-run", ["id"] = id, ["shell"] = shell, ["command"] = command }.ToJsonString();
	}

	private static async Task<List<string>> RunAsync(HostCommandRunner runner, ConcurrentQueue<string> sent, string id, string shell, string command, CancellationToken ct)
	{
		runner.OnConfigFrame("s1", RunFrame(id, shell, command));
		return await WaitForExitAsync(sent, id, ct);
	}

	private static bool Contains(List<string> messages, string fragment)
	{
		bool found = false;
		foreach (string message in messages)
		{
			if (message.Contains(fragment, StringComparison.Ordinal))
				found = true;
		}
		return found;
	}

	private static async Task WaitForAsync(ConcurrentQueue<string> sent, string fragment, CancellationToken ct)
	{
		while (!Contains(new List<string>(sent), fragment))
			await Task.Delay(20, ct);
	}

	// Every message for this run, once its exit has arrived.
	private static async Task<List<string>> WaitForExitAsync(ConcurrentQueue<string> sent, string id, CancellationToken ct)
	{
		await WaitForAsync(sent, $"/host-exit {id} ", ct);
		List<string> run = new List<string>();
		foreach (string message in sent)
		{
			if (message.Contains($" {id} ", StringComparison.Ordinal))
				run.Add(message);
		}
		return run;
	}
}