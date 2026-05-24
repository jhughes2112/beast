using System;
using System.Threading;
using System.Threading.Tasks;


// Assumes the Agent is already running and listening on port 13131.
// Used when debugging: start Agent and Beast separately; Beast connects to the running agent.
public class LaunchDebug : ILauncher
{
    public Task StartAsync(string name, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
