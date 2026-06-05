using System;
using System.Threading;
using System.Threading.Tasks;


// Abstracts the lifecycle of whatever is running the Agent on the other end of the transport.
// Implementations handle Docker containers (DockerContext) or local processes (NativeContext).
public interface ILauncher : IDisposable
{
	// Starts the agent backend. name is a unique identifier used for cleanup/logging.
	// Returns the host port that the agent's WebSocket server is listening on.
	// Must complete before RetryConnectAsync is called by BeastApp.
	Task<int> StartAsync(string name, CancellationToken cancellationToken);

	// Stops the agent backend gracefully. Called during BeastApp dispose.
	Task StopAsync();
}
