using System;
using System.Threading;
using System.Threading.Tasks;


public class Program
{
	public static async Task<int> Main(string[] args)
	{
		using CancellationTokenSource cts = new CancellationTokenSource();
		Console.CancelKeyPress += (sender, e) =>
		{
			e.Cancel = true;
			cts.Cancel();
		};

		// --test: run unit tests using the console transport and exit without starting the WebSocket server.
		if (args.Length > 0 && args[0] == "--test")
		{
			ITransportServer consoleTransport = new TransportConsoleDebug();
			TestContext ctx = new TestContext(consoleTransport);
			FixJson.ResetCounters();
			LlmServiceTests.Test(ctx);
			ContextBudgetTests.Test(ctx);
			FixJsonTests.Test(ctx);
			await FileToolsTests.TestAsync(ctx);
			ShellToolsTests.Test(ctx);
			Console.WriteLine($"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
			return ctx.Failed > 0 ? 1 : 0;
		}

		// Create/attach the git worktree at /workspace before anything reads the working tree. The branch
		// comes from the --worktree-branch startup argument (never an env var). No-op for debug/native runs
		// without the /git mount. A failure here means /workspace is unusable, so abort.
		string worktreeBranch = ArgValue(args, "--worktree-branch");
		// No worktree branch means an ephemeral, current-folder launch: the session is not saved and no git
		// worktree is set up. A named worktree launch passes its branch and gets a persisted session.
		bool ephemeral = string.IsNullOrEmpty(worktreeBranch);
		(bool wtOk, string wtDetail) = await WorktreeBootstrap.EnsureAsync(worktreeBranch, cts.Token);
		if (!wtOk)
		{
			Console.Error.WriteLine("Failed to set up git worktree at /workspace:");
			Console.Error.WriteLine(string.IsNullOrWhiteSpace(wtDetail) ? "(no output)" : wtDetail);
			return 1;
		}

		SettingsService settingsService = new SettingsService(Environment.CurrentDirectory);
		RoleService roleService = new RoleService(Environment.CurrentDirectory);

		LlmRegistry registry = new LlmRegistry();
		await using TransportWebSocketServer wsServer = new TransportWebSocketServer(13131);
		await wsServer.AcceptAsync(cts.Token);
		ITransportServer transport = wsServer;
		AgentOrchestrator orchestrator = new AgentOrchestrator(registry, roleService, settingsService, transport, cts, ephemeral);

		try
		{
			await orchestrator.RunAsync();
		}
		catch (OperationCanceledException)
		{
			// Normal shutdown path (Ctrl+C or /quit cancels the root token). Logged so an unexpected
			// top-level unwind is distinguishable from a clean exit in the container logs.
			Console.Error.WriteLine($"[Program] Orchestrator stopped (root cancellation requested: {cts.IsCancellationRequested}).");
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Agent terminated with error: {ex}");
			return 1;
		}

		return 0;
	}

	// Returns the value following a "--flag value" startup argument, or empty if the flag is absent or last.
	private static string ArgValue(string[] args, string flag)
	{
		for (int i = 0; i < args.Length - 1; i++)
		{
			if (args[i] == flag)
				return args[i + 1];
		}
		return string.Empty;
	}
}