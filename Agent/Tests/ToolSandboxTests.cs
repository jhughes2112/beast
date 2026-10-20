using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// A scripted model driving the REAL tools (bash, write_file, edit_file, read_file, ls) through the
// production turn loop, in the order a model actually uses them: shell makes a file, file tools
// rewrite it, shell changes it again, and so on — every hand-off between the shell user and the root
// agent. Run it with the working directory on a Windows bind mount (see the container check), since
// that is where the sandbox's ownership rules and the kernel's protected_regular rule actually bite;
// in the container's own /tmp, root writes behave differently and hide the failures this exists for.
// Needs bash, so it is skipped on Windows.
public static class ToolSandboxTests
{
	private static readonly string[] ToolNames = new string[] { "bash", "readonly_bash", "write_file", "edit_file", "read_file", "ls" };

	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  ToolSandboxTests");
		if (!OperatingSystem.IsLinux())
		{
			ctx.Log("    SKIP: needs bash (runs in the container)");
			return;
		}

		FakeProvider? server = FakeProvider.StartOnRandomPort();
		if (server == null)
		{
			ctx.Log("    SKIP: no free port for the fake provider");
			return;
		}

		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		Task   serverTask                 = server.RunAsync(cts.Token);
		string previous                   = Environment.CurrentDirectory;
		string root                       = Path.Combine(previous, $"beast-tooltest-{Guid.NewGuid():N}");

		try
		{
			Directory.CreateDirectory(Path.Combine(root, ".github"));
			Directory.CreateDirectory(Path.Combine(root, ".beast"));
			File.WriteAllText(   Path.Combine(root, ".github", "ci.yml"), "on: push\n");
			File.WriteAllText(Path.Combine(root, ".beast", "secret.txt"), "TOP-SECRET\n");
			File.WriteAllText(           Path.Combine(root, "README.md"), "hello\n");

			Directory.SetCurrentDirectory(root);
			Sandbox.Initialize(root);
			string lockdown = await Sandbox.LockDownAsync(cts.Token);
			ctx.Log($"    lockdown: {lockdown}");

			await RunScriptAsync(ctx, server, root, cts.Token);
		}
		finally
		{
			Directory.SetCurrentDirectory(previous);
			Sandbox.Initialize(previous);
			// As root: the lockdown left parts of the tree root-owned and read-only.
			await ShellTools.AgentBashAsync("cleanup", $"rm -rf '{root}'", null, CancellationToken.None);
			cts.Cancel();
			try
			{
				await serverTask;
			}
			catch (OperationCanceledException)
			{
			}
		}
	}

	private static async Task RunScriptAsync(TestContext ctx, FakeProvider server, string root, CancellationToken ct)
	{
		bool sandboxed = Sandbox.ShellUserActive;

		// Built first: Build resets the server, which clears any script already queued.
		Rig rig = Build(server);

		server.Enqueue(FakeReply.Call("c1", "bash", Args(("command", "echo hello > shell_made.txt && id -un"))));
		server.Enqueue(FakeReply.Call("c2", "write_file", Args(("file_path", "shell_made.txt"), ("content", "overwritten by write_file\n"))));
		server.Enqueue(FakeReply.Call("c3", "edit_file", Args(("file_path", "shell_made.txt"), ("old_text", "overwritten"), ("new_text", "edited"))));
		server.Enqueue(FakeReply.Call("c4", "bash", Args(("command", "echo appended >> shell_made.txt && cat shell_made.txt"))));
		server.Enqueue(FakeReply.Call("c5", "write_file", Args(("file_path", "sub/dir/new.txt"), ("content", "fresh\n"))));
		server.Enqueue(FakeReply.Call("c6", "edit_file", Args(("file_path", "README.md"), ("old_text", "hello"), ("new_text", "hello again"))));
		server.Enqueue(FakeReply.Call("c7", "bash", Args(("command", "rm -r sub && echo removed && cat README.md"))));
		server.Enqueue(FakeReply.Call("c8", "read_file", Args(("file_path", ".beast/secret.txt"))));
		server.Enqueue(FakeReply.Call("c9", "write_file", Args(("file_path", ".github/ci.yml"), ("content", "evil\n"))));
		server.Enqueue(FakeReply.Call("c10", "bash", Args(("command", "cat .beast/secret.txt; echo evil >> .github/ci.yml; cat /root/.beast/settings.json"))));
		server.Enqueue(FakeReply.Call("c11", "readonly_bash", Args(("command", "cat shell_made.txt"))));
		// Any program the shell runs has the shell user's permissions — a script is no way around them.
		server.Enqueue(FakeReply.Call("c12", "bash", Args(("command",
			"python3 - <<'EOF'\n" +
			"import os\n" +
			"for path, text in [('.github/ci.yml', 'evil'), ('/root/.beast/settings.json', 'evil'), ('.beast/secret.txt', 'evil')]:\n" +
			"    try:\n" +
			"        open(path, 'a').write(text); print('WROTE', path)\n" +
			"    except OSError as e:\n" +
			"        print('refused', path, e.strerror)\n" +
			"try:\n" +
			"    print('READ', open('.beast/secret.txt').read())\n" +
			"except OSError as e:\n" +
			"    print('refused read', e.strerror)\n" +
			"open('python_made.txt', 'w').write('fine')\n" +
			"print('workspace write ok')\n" +
			"EOF"))));
		// A dot-folder the shell creates is the agent's once the command ends; a dot-symlink is removed.
		server.Enqueue(FakeReply.Call("c13", "bash", Args(("command", "mkdir .sneaky && echo x > .sneaky/a && ln -s /tmp .linkdot && echo made"))));
		server.Enqueue(FakeReply.Call("c14", "bash", Args(("command", "echo y >> .sneaky/a; ls -a"))));
		server.Enqueue(FakeReply.Say("all done"));

		rig.Session.Bundle.OnUserMessage("exercise the tools");

		ProtocolResult last = ProtocolResult.Failed("not run");
		for (int i = 0; i < 16 && last.Payload?.AssistantText != "all done"; i++)
			last = await StepAsync(rig, ct);
		ctx.AssertEqual("all done", last.Payload?.AssistantText ?? last.ErrorMessage ?? string.Empty, "Tools: the scripted session runs to the end");

		Dictionary<string, string> results = Results(rig.Session);

		if (sandboxed)
			ctx.Assert(Get(results, "c1").Contains(Sandbox.ShellUser, StringComparison.Ordinal), $"Tools: bash runs as the shell user — {Get(results, "c1")}");
		ctx.Assert(!Get(results, "c2").Contains("Error", StringComparison.Ordinal), $"Tools: write_file overwrites a file the shell made — {Get(results, "c2")}");
		ctx.Assert(!Get(results, "c3").Contains("Error", StringComparison.Ordinal), $"Tools: edit_file edits it — {Get(results, "c3")}");
		ctx.Assert(Get(results, "c4").Contains("edited by write_file", StringComparison.Ordinal) && Get(results, "c4").Contains("appended", StringComparison.Ordinal),
			$"Tools: the shell can still change it after the agent did — {Get(results, "c4")}");
		ctx.Assert(!Get(results, "c5").Contains("Error", StringComparison.Ordinal), $"Tools: write_file creates new folders and files — {Get(results, "c5")}");
		ctx.Assert(!Get(results, "c6").Contains("Error", StringComparison.Ordinal), $"Tools: edit_file edits a file the lockdown handed to the shell user — {Get(results, "c6")}");
		ctx.Assert(Get(results, "c7").Contains("removed", StringComparison.Ordinal) && Get(results, "c7").Contains("hello again", StringComparison.Ordinal),
			$"Tools: the shell can delete what write_file made — {Get(results, "c7")}");
		ctx.Assert(Get(results, "c8").Contains("off limits", StringComparison.Ordinal) && !Get(results, "c8").Contains("TOP-SECRET", StringComparison.Ordinal),
			$"Tools: read_file refuses .beast — {Get(results, "c8")}");
		if (sandboxed)
		{
			ctx.Assert(Get(results, "c9").Contains("protected folder", StringComparison.Ordinal), $"Tools: write_file refuses a locked dot-folder — {Get(results, "c9")}");
			ctx.Assert(!Get(results, "c10").Contains("TOP-SECRET", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(root, ".github", "ci.yml")) == "on: push\n",
				$"Tools: the shell can neither read .beast nor change a locked dot-folder — {Get(results, "c10")}");
			ctx.AssertEqual(Sandbox.ShellUser, (await ShellTools.AgentBashAsync("own", $"stat -c %U '{Path.Combine(root, "shell_made.txt")}'", null, ct)).StdOut.Trim(),
				"Tools: the agent's writes leave the file with the shell user");
		}
		ctx.Assert(Get(results, "c11").Contains("appended", StringComparison.Ordinal), $"Tools: readonly_bash reads it — {Get(results, "c11")}");
		if (sandboxed)
		{
			string script = Get(results, "c12");
			ctx.Assert(!script.Contains("WROTE", StringComparison.Ordinal) && !script.Contains("TOP-SECRET", StringComparison.Ordinal) && script.Contains("workspace write ok", StringComparison.Ordinal),
				$"Tools: a python script in bash writes the workspace but nothing protected — {script}");

			string made13 = Get(results, "c13");
			ctx.Assert(made13.Contains("made", StringComparison.Ordinal) && made13.Contains(".sneaky now belongs to the agent", StringComparison.Ordinal) && made13.Contains("symlink .linkdot was removed", StringComparison.Ordinal),
				$"Tools: a new dot-folder is taken and a dot-symlink removed, and the model is told — {made13}");
			string after14 = Get(results, "c14");
			ctx.Assert(after14.Contains("Permission denied", StringComparison.Ordinal) && !after14.Contains(".linkdot", StringComparison.Ordinal),
				$"Tools: the taken folder is read-only for the shell and the link is gone — {after14}");
		}

		string madePath = Path.Combine(root, "shell_made.txt");
		string made     = File.Exists(madePath) ? File.ReadAllText(madePath) : "(missing)";
		ctx.AssertEqual("edited by write_file\nappended\n", made, "Tools: the file holds every step's change, in order");
	}

	private sealed class Rig
	{
		public Session              Session   { get; }
		public LlmService           Service   { get; }
		public Tool[]               Tools     { get; }
		public TestCaptureTransport Transport { get; }

		public Rig(Session session, LlmService service, Tool[] tools, TestCaptureTransport transport)
		{
			Session   = session;
			Service   = service;
			Tools     = tools;
			Transport = transport;
		}
	}

	// The production tool set for a role holding the tools under test, built by ToolFactory from the
	// default tool configuration, against a ChatCompletions fake.
	private static Rig Build(FakeProvider server)
	{
		server.Reset();
		ModelConfig config  = new ModelConfig { Id = "fake-tools", Name = "Fake Tools", ContextWindow = 200000, MaxOutputTokens = 4096 };
		LlmModel    model   = new LlmModel(config.Id, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);
		LlmService  service = new LlmService(model, DetectedProtocol.ChatCompletions, new ModelAvailability(), new EndpointFacts(), new List<string> { config.Id }, (id, effort, summaries, maxImages) => { });

		TestCaptureTransport transport = new TestCaptureTransport();
		BeastSession         data      = new BeastSession(Guid.NewGuid().ToString("N"), "tools", config.Id, "ToolTester",
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a test model.", transport, false);
		session.UpdateModel(model);

		// The default tool configuration overlays whatever tools.json holds, in memory only, so the
		// rig drives the in-code tool definitions regardless of the local file.
		SettingsService                settings = new SettingsService(Environment.CurrentDirectory);
		Dictionary<string, ToolConfig> defaults = ToolService.Defaults();
		foreach (string name in ToolNames)
			settings.Tools[name] = defaults[name];
		Role   role  = new Role("ToolTester", "drives the tools", RoleKind.Subagent, new List<string> { config.Id }, new List<string>(ToolNames), "test", string.Empty, string.Empty);
		Tool[] tools = ToolFactory.BuildForRole(settings, role, null, null, session, false, null, null, null, null);
		return new Rig(session, service, tools, transport);
	}

	private static async Task<ProtocolResult> StepAsync(Rig rig, CancellationToken ct)
	{
		ProtocolResult result = await rig.Service.RunToCompletionAsync(rig.Session, rig.Tools, null, 0, 0, false, rig.Transport, ct);
		if (result.Outcome == ProtocolCallOutcome.Success)
		{
			rig.Session.CommitAssistantTurn(result.Payload!);
			bool hasCalls = await ToolDispatch.DispatchAsync(result.Payload!, rig.Tools, rig.Session, rig.Transport, ct);
			if (hasCalls)
				rig.Session.CommitToolResults(result.Payload!);
		}
		return result;
	}

	private static string Args(params (string Name, string Value)[] args)
	{
		JsonObject obj = new JsonObject();
		foreach ((string name, string value) in args)
			obj[name] = value;
		return obj.ToJsonString();
	}

	private static Dictionary<string, string> Results(Session session)
	{
		Dictionary<string, string> results = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (CanonicalMessage message in session.Data.Messages)
		{
			if (message is ToolResultMessage tr)
				results[tr.ToolCallId] = tr.Content;
		}
		return results;
	}

	private static string Get(Dictionary<string, string> results, string id)
	{
		return results.TryGetValue(id, out string? content) ? content : "(no result)";
	}
}