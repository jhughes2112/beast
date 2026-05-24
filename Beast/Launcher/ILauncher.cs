using System;
using System.Threading;
using System.Threading.Tasks;


// Abstracts the lifecycle of whatever is running the Agent on the other end of the transport.
// Implementations handle Docker containers (DockerContext) or local processes (NativeContext).
public interface ILauncher : IDisposable
{
    // Starts the agent backend. name is a unique identifier used for cleanup/logging.
    // Must complete before RetryConnectAsync is called by BeastApp.
    Task StartAsync(string name, CancellationToken cancellationToken);

    // Stops the agent backend gracefully. Called during BeastApp dispose.
    Task StopAsync();
}
