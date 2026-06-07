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
			LlmServiceTests.Test(ctx);
			FixJsonTests.Test(ctx);
			await FileToolsTests.TestAsync(ctx);
			ShellToolsTests.Test(ctx);
			Console.WriteLine($"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
			return ctx.Failed > 0 ? 1 : 0;
		}

		SettingsService settingsService;
		try
		{
			settingsService = new SettingsService(Environment.CurrentDirectory);
		}
		catch (InvalidOperationException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}

		RoleService roleService = new RoleService(Environment.CurrentDirectory, settingsService.Settings);
		LlmRegistry registry = new LlmRegistry();
		TransportWebSocketServer wsServer = new TransportWebSocketServer(13131);
		await wsServer.AcceptAsync(cts.Token);
		ITransportServer transport = wsServer;
		AgentOrchestrator orchestrator = new AgentOrchestrator(registry, roleService, settingsService, transport, cts);

		try
		{
			await orchestrator.RunAsync();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Agent terminated with error: {ex.Message}");
			return 1;
		}

		return 0;
	}
}
