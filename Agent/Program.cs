using System;
using System.Threading;
using System.Threading.Tasks;


public class Program
{
	public static async Task<int> Main(string[] args)
	{
		bool debug = false;
		foreach (string arg in args)
		{
			if (arg == "--debug" || arg == "/debug")
				debug = true;
		}

		using CancellationTokenSource cts = new CancellationTokenSource();
		Console.CancelKeyPress += (sender, e) =>
		{
			e.Cancel = true;
			cts.Cancel();
		};

		Console.Error.WriteLine($"[agent] Starting (debug={debug}, cwd={Environment.CurrentDirectory})");

		SettingsService settingsService;
		try
		{
			settingsService = new SettingsService(Environment.CurrentDirectory);
			Console.Error.WriteLine($"[agent] Settings loaded.");
		}
		catch (InvalidOperationException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}

		RoleService roleService = new RoleService(Environment.CurrentDirectory);
		Console.Error.WriteLine($"[agent] Roles loaded: {roleService.Roles.Count}");

		LlmRegistry registry = new LlmRegistry();
		IFramedTransport transport = debug ? new TransportConsoleDebug() : new TransportFramedStdio();
		AgentOrchestrator orchestrator = new AgentOrchestrator(registry, roleService, settingsService, transport);
		Console.Error.WriteLine("[agent] Orchestrator ready, entering loop.");

		int exitCode;
		try
		{
			exitCode = await orchestrator.RunAsync(cts.Token);
		}
		catch (OperationCanceledException)
		{
			exitCode = 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Agent terminated with error: {ex.Message}");
			exitCode = 1;
		}
		finally
		{
		}

		Console.Error.WriteLine($"[agent] Exiting with code {exitCode}.");
		return exitCode;
	}
}