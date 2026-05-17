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

		RoleService roleService = new RoleService(Environment.CurrentDirectory);
		LlmRegistry registry = new LlmRegistry();
		TransportFramedStdio transport = new TransportFramedStdio();
		AgentOrchestrator orchestrator = new AgentOrchestrator(registry, roleService, settingsService, transport);

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

		return exitCode;
	}
}