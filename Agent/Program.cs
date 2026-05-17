using System;
using System.Threading;
using System.Threading.Tasks;


public class Options
{
	public string? Prompt { get; set; }
	public bool Test { get; set; }
}

public class Program
{
	public static async Task<int> Main(string[] args)
	{
		Options opts = ParseArgs(args);

		using CancellationTokenSource cts = new CancellationTokenSource();
		Console.CancelKeyPress += (sender, e) =>
		{
			e.Cancel = true;
			cts.Cancel();
		};

		if (opts.Test)
		{
			return TestRunner.RunAll();
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

		RoleService roleService = new RoleService(Environment.CurrentDirectory);
		LlmRegistry registry = new LlmRegistry();
		TransportFramedStdio transport = new TransportFramedStdio();
		AgentOrchestrator orchestrator = new AgentOrchestrator(registry, roleService, settingsService, transport);

		int exitCode;
		try
		{
			exitCode = await orchestrator.RunAsync(opts.Prompt, cts.Token);
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

	private static Options ParseArgs(string[] args)
	{
		Options opts = new();

		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];

			switch (arg)
			{
				case "-p":
				case "--prompt":
					if (i + 1 < args.Length)
					{
						// Collect everything after -p as the prompt.
						string prompt = args[++i];
						opts.Prompt = prompt;
					}
					break;
				case "-t":
				case "--test":
					opts.Test = true;
					break;
				default:
					// If no flag, treat as prompt text.
					if (!arg.StartsWith("-"))
					{
						opts.Prompt = arg;
					}
					else
					{
						Console.Error.WriteLine("Unknown argument: " + arg);
						return new Options();
					}
					break;
			}
		}

		return opts;
	}
}