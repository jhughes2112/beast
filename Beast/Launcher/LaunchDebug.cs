using System;
using System.Threading;
using System.Threading.Tasks;


// Assumes the Agent is already running and listening on port 13131.
// Used when debugging: start Agent and Beast separately; Beast connects to the running agent.
public class LaunchDebug : ILauncher
{
	public Task<int> StartAsync(string name, CancellationToken cancellationToken)
	{
		return Task.FromResult(13131);
	}

	public Task StopAsync()
	{
		return Task.CompletedTask;
	}

	// The agent runs separately under the debugger, so we cannot observe its lifecycle — assume alive.
	public Task<bool> IsAliveAsync()
	{
		return Task.FromResult(true);
	}

	public Task<string> GetLogsAsync(CancellationToken cancellationToken)
	{
		return Task.FromResult(string.Empty);
	}

	public void Dispose()
	{
	}
}