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

	// True while the agent backend is still running. False once it has exited/crashed, which means its
	// WebSocket server will never come up and the client should stop waiting to connect.
	Task<bool> IsAliveAsync();

	// Returns the agent backend's captured output for diagnostics when it failed to start. May be empty.
	Task<string> GetLogsAsync(CancellationToken cancellationToken);
}