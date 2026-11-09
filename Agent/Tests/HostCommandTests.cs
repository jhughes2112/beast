using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// host_command's agent side, with the test playing the Beast client: it reads the Config frames the
// agent sends and answers through HandleInbound exactly as the client's messages arrive. No decision
// model is configured, so every new command is Unsure and the hallMonitorUnsure setting decides.
public static class HostCommandTests
{
	public static async Task TestAsync(TestContext ctx)
	{
		string memoryPath                 = Path.Combine(Path.GetTempPath(), $"beast-host-decisions-{Guid.NewGuid():N}.json");
		string calibrationPath            = Path.Combine(Path.GetTempPath(), $"beast-host-calibration-{Guid.NewGuid():N}.json");
		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		CancellationToken ct              = cts.Token;
		try
		{
			TestCaptureTransport transport = new TestCaptureTransport();
			SettingsService      settings  = new SettingsService();
			DecisionMemory       memory    = new DecisionMemory(memoryPath);
			HallMonitor          monitor   = new HallMonitor(new RoleService(Environment.CurrentDirectory), new LlmRegistry(), memory, new HallMonitorCalibration(calibrationPath, memory, _ => { }, ct));
			HostCommands         host      = new HostCommands(monitor, settings, transport);

			ctx.Assert(HostCommands.ParsePolicy("beSafe") == UnsurePolicy.BeSafe && HostCommands.ParsePolicy("BEUNSAFE") == UnsurePolicy.BeUnsafe && HostCommands.ParsePolicy("whatever") == UnsurePolicy.Ask,
				"host_command: the Unsure setting parses, and anything unrecognised asks");

			// Roles with host_command are told what the host is; others, and runs with no host given, are not.
			RoleService withHost = new RoleService(Environment.CurrentDirectory, "Test OS 1.0, X64");
			RoleService noHost   = new RoleService(Environment.CurrentDirectory);
			ctx.Assert(  withHost.GetRole("Default")!.SystemPrompt.Contains("(Test OS 1.0, X64)"), "host_command: Default is told what the host is");
			ctx.Assert(withHost.GetRole("Developer")!.SystemPrompt.Contains("(Test OS 1.0, X64)"), "host_command: Developer is told what the host is");
			ctx.Assert(           !withHost.GetRole("Reviewer")!.SystemPrompt.Contains("Test OS"), "host_command: a role without the tool is not");
			ctx.Assert(         !noHost.GetRole("Default")!.SystemPrompt.Contains("two machines"), "host_command: no note when no host was given");
			ctx.Assert(               !withHost.Roles["Default"].SystemPrompt.Contains("Test OS"), "host_command: the note never reaches the stored role");

			// No role's own prompt says anything about seeded runs: that is added only while one is under way.
			foreach (string name in new[] { "Default", "Developer", "Reviewer", FileSubagent.RoleName, "WebSearch", "MediaReader", ReasoningSummarizer.RoleName })
				ctx.Assert(!noHost.GetRole(name)!.SystemPrompt.Contains("seeded", StringComparison.OrdinalIgnoreCase), $"Seeded run: {name}'s own prompt leaves it out");
			foreach (string name in new[] { "Default", "Developer", "Reviewer" })
				ctx.Assert(noHost.GetRole(name)!.Tools.Contains("browser_screenshot"), $"Screenshot: {name} has the tool");

			Session seededSession = new Session(new BeastSession("s", "s", "m", "r", string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true), "Be helpful.", transport, false);
			string  SystemText() => seededSession.Bundle.Canonical.Messages[0] is SystemMessage message ? message.Text : string.Empty;
			seededSession.SetSeededRun(false);
			ctx.AssertEqual("Be helpful.", SystemText(), "Seeded run: without a seed the prompt is untouched");
			seededSession.SetSeededRun(true);
			seededSession.SetSeededRun(true);
			ctx.AssertEqual("Be helpful." + RoleService.SeededRunNote, SystemText(), "Seeded run: with a seed the note ends the prompt, once");
			ctx.Assert(SystemText().Contains("Other methods will be refused", StringComparison.Ordinal), "Seeded run: the note warns that screenshots are refused");
			seededSession.SetSeededRun(false);
			ctx.AssertEqual("Be helpful.", SystemText(), "Seeded run: moving to a model with no seed takes the note away");


			// Only the shells the client announced are accepted, and cmd only takes one line.
			ctx.Assert(host.Shells.Count == 0, "host_command: no shells until the client says which it has");
			ctx.Assert(host.HandleInbound("/host-shells bash,cmd,bash"), "host_command: the client's shell list is claimed");
			ctx.Assert(host.Shells.Count == 2 && host.Shells[0] == "bash" && host.Shells[1] == "cmd", "host_command: the shell list is kept, without repeats");
			settings.Settings.HallMonitorUnsure = "beUnsafe";
			ToolResult wrongShell               = await host.RunAsync("s1", "c0", "python", "print(1)", null, ct);
			ctx.Assert(wrongShell.ExitCode != 0 && wrongShell.StdErr.Contains("not a shell available") && wrongShell.StdErr.Contains("bash, cmd"), "host_command: a shell the host does not have is refused, naming the ones it has");
			ToolResult twoLines = await host.RunAsync("s1", "c0", "cmd", "dir\ndir", null, ct);
			ctx.Assert(twoLines.ExitCode != 0 && twoLines.StdErr.Contains("single line"), "host_command: cmd refuses a multi-line command");
			ctx.Assert(Count(transport, "host-run") == 0, "host_command: refused shells never reach the client");
			// BeSafe: refused, and nothing is sent to the client to run.
			settings.Settings.HallMonitorUnsure = "beSafe";
			ToolResult refused                  = await host.RunAsync("s1", "c1", "bash", "make build", null, ct);
			ctx.Assert(refused.ExitCode != 0 && refused.StdErr.Contains("could not confirm"), "host_command: an Unsure command is refused under beSafe");
			ctx.Assert(Count(transport, "host-run") == 0, "host_command: a refused command never reaches the client");

			// BeUnsafe: it runs. Output arrives batched, stderr keeps its leading space, and the exit code is the command's.
			settings.Settings.HallMonitorUnsure = "beUnsafe";
			Task<ToolResult> running            = host.RunAsync("s1", "c2", "bash", "make build", null, ct);
			string           runId              = await WaitForFrameAsync(transport, "host-run", 0, ct);
			ctx.Assert(host.HandleInbound($"/host-output {runId} o line one\nline two"), "host_command: output is claimed");
			host.HandleInbound($"/host-output {runId} e  warning: spaced");
			host.HandleInbound($"/host-exit {runId} 3");
			ToolResult ran = await running;
			ctx.Assert(ran.ExitCode == 3 && ran.StdOut == "line one\nline two" && ran.StdErr == " warning: spaced", $"host_command: the result carries the client's output and exit code (got {ran.ExitCode}, '{ran.StdOut}', '{ran.StdErr}')");
			List<string> stream = HostStream(transport);
			ctx.Assert(stream.Count == 5 && stream[0] == "start" && stream[1] == "make build" && stream[2] == "\nline one\nline two" && stream[3] == "\n warning: spaced" && stream[4] == "end",
				$"host_command: the session streams HOST: the command, then each batch of output (got {string.Join(" | ", stream)})");
			ctx.Assert(!host.HandleInbound("/hostile"), "host_command: other text is left alone");

			// Ask, answered Never: refused, remembered, and not asked again.
			settings.Settings.HallMonitorUnsure = "ask";
			Task<ToolResult> asking             = host.RunAsync("s1", "c3", "bash", "rm -rf build", null, ct);
			string           askId              = await WaitForFrameAsync(transport, "host-ask", 0, ct);
			host.HandleInbound($"/host-answer {askId} never");
			ToolResult declined = await asking;
			ctx.Assert(declined.ExitCode != 0 && declined.StdErr.Contains("refused this command"), "host_command: Never refuses the command");
			ToolResult again = await host.RunAsync("s1", "c4", "bash", "rm -rf build", null, ct);
			ctx.Assert(again.ExitCode != 0 && Count(transport, "host-ask") == 1, "host_command: a command answered Never is refused without asking again");

			// Ask, answered Always: it runs, and next time it runs without asking.
			Task<ToolResult> asking2 = host.RunAsync("s1", "c5", "bash", "npm test", null, ct);
			string           askId2  = await WaitForFrameAsync(transport, "host-ask", 1, ct);
			host.HandleInbound($"/host-answer {askId2} always");
			string runId2 = await WaitForFrameAsync(transport, "host-run", 1, ct);
			host.HandleInbound($"/host-exit {runId2} 0");
			ToolResult allowed = await asking2;
			ctx.Assert(allowed.ExitCode == 0, "host_command: Always runs the command");
			Task<ToolResult> repeat = host.RunAsync("s1", "c6", "bash", "npm test", null, ct);
			string           runId3 = await WaitForFrameAsync(transport, "host-run", 2, ct);
			host.HandleInbound($"/host-exit {runId3} 0");
			await repeat;
			ctx.Assert(Count(transport, "host-ask") == 2, "host_command: a command answered Always runs without asking again");

			// Timeout: the agent tells the client to stop it, and the result says it timed out.
			settings.Settings.HallMonitorUnsure = "beUnsafe";
			Task<ToolResult> slow               = host.RunAsync("s1", "c7", "bash", "sleep 100", 1, ct);
			string           slowId             = await WaitForFrameAsync(transport, "host-run",    3, ct);
			string           stopId             = await WaitForFrameAsync(transport, "host-cancel", 0, ct);
			host.HandleInbound($"/host-exit {slowId} 137");
			ToolResult timedOut = await slow;
			ctx.Assert(stopId == slowId && timedOut.ExitCode != 0 && timedOut.StdErr.Contains("timed out"), "host_command: a timeout stops the command on the client");

			// A cancelled question is taken back off the user's screen.
			settings.Settings.HallMonitorUnsure     = "ask";
			using CancellationTokenSource cancelAsk = CancellationTokenSource.CreateLinkedTokenSource(ct);
			Task<ToolResult> abandoned              = host.RunAsync("s1", "c8", "bash", "make deploy", null, cancelAsk.Token);
			string           askId3                 = await WaitForFrameAsync(transport, "host-ask", 2, ct);
			cancelAsk.Cancel();
			bool threw = false;
			try
			{
				await abandoned;
			}
			catch (OperationCanceledException)
			{
				threw = true;
			}
			ctx.Assert(threw && await WaitForFrameAsync(transport, "host-ask-cancel", 0, ct) == askId3, "host_command: a cancelled call withdraws its question");
		}
		finally
		{
			File.Delete(memoryPath);
			File.Delete(calibrationPath);
		}
	}

	private static int Count(TestCaptureTransport transport, string kind)
	{
		int count = 0;
		foreach ((FrameType Type, string Text) frame in transport.Snapshot())
		{
			if (frame.Type == FrameType.Config && KindOf(frame.Text) == kind)
				count++;
		}
		return count;
	}

	// Waits for the agent to send the (skip + 1)th Config frame of this kind and returns its id.
	private static async Task<string> WaitForFrameAsync(TestCaptureTransport transport, string kind, int skip, CancellationToken ct)
	{
		string id = string.Empty;
		while (id.Length == 0)
		{
			int seen = 0;
			foreach ((FrameType Type, string Text) frame in transport.Snapshot())
			{
				if (id.Length == 0 && frame.Type == FrameType.Config && KindOf(frame.Text) == kind)
				{
					if (seen == skip)
						id = JsonNode.Parse(frame.Text)?["id"]?.GetValue<string>() ?? string.Empty;
					seen++;
				}
			}
			if (id.Length == 0)
				await Task.Delay(10, ct);
		}
		return id;
	}

	private static string KindOf(string json)
	{
		return JsonNode.Parse(json)?["kind"]?.GetValue<string>() ?? string.Empty;
	}

	// The first Host stream's frames: "start", each chunk, "end".
	private static List<string> HostStream(TestCaptureTransport transport)
	{
		List<string> stream = new List<string>();
		bool         open   = false;
		bool         done   = false;
		foreach ((FrameType Type, string Text) frame in transport.Snapshot())
		{
			if (done)
				continue;
			if (frame.Type == FrameType.StreamStart && frame.Text == StreamTag.Host)
			{
				open = true;
				stream.Add("start");
			}
			else if (open && frame.Type == FrameType.StreamChunk)
			{
				stream.Add(frame.Text);
			}
			else if (open && frame.Type == FrameType.StreamEnd)
			{
				stream.Add("end");
				done = true;
			}
		}
		return stream;
	}
}