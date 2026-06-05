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