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

		RoleService roleService = new RoleService(Environment.CurrentDirectory, settingsService.Settings);
		Console.Error.WriteLine($"[agent] Roles loaded: {roleService.Roles.Count}");

		string? beastHost = Environment.GetEnvironmentVariable("BEAST_HOST");
		if (!string.IsNullOrEmpty(beastHost))
		{
			foreach (ProviderConfig provider in settingsService.Settings.Providers)
			{
				if (provider.BaseUrl.Contains("localhost") || provider.BaseUrl.Contains("127.0.0.1"))
				{
					string rewritten = provider.BaseUrl.Replace("localhost", beastHost).Replace("127.0.0.1", beastHost);
					Console.Error.WriteLine($"[agent] Rewriting provider URL: {provider.BaseUrl} -> {rewritten}");
					provider.BaseUrl = rewritten;
				}
			}
		}

		LlmRegistry registry = new LlmRegistry();
		ITransportServer transport;
		if (debug)
		{
			transport = new TransportConsoleDebug();
		}
		else
		{
			TransportWebSocketServer wsServer = new TransportWebSocketServer(13131);
			await wsServer.AcceptAsync(cts.Token);
			transport = wsServer;
		}
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