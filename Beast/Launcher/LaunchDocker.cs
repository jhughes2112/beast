using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

// Launches and manages an Agent Docker container.
// Implements IAgentContext so BeastApp can swap it for NativeContext.
public class LaunchDocker : ILauncher
{
    private readonly string _image;
    private readonly DockerClient _dockerClient;
    private readonly Log _log;
    private string? _containerId;
    private int _hostPort;

    public LaunchDocker(string image, Log log)
    {
        _image = image;
        _log = log;
        _dockerClient = new DockerClientConfiguration().CreateClient();
    }

    public int HostPort => _hostPort;

    // Removes any stale container with the same name, then creates and starts a fresh one.
    public async Task<int> StartAsync(string name, CancellationToken cancellationToken)
    {
        await RemoveContainerByNameAsync(name);

        string cwd = Directory.GetCurrentDirectory();
        string beastConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast");
        Directory.CreateDirectory(beastConfigDir);

        // Find an available port on the host
        _hostPort = FindAvailablePort();

        CreateContainerParameters createParams = new CreateContainerParameters
        {
            Image = _image,
            Name = name,
            WorkingDir = "/workspace",
            Env = new List<string> { "BEAST_HOST=host.docker.internal", $"AGENT_PORT={_hostPort}" },
            ExposedPorts = new Dictionary<string, EmptyStruct> { ["13131/tcp"] = default },
            HostConfig = new HostConfig
            {
                AutoRemove = false,
                ExtraHosts = new List<string> { "host.docker.internal:host-gateway" },
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    ["13131/tcp"] = new List<PortBinding>
                    {
                        new PortBinding { HostIP = "127.0.0.1", HostPort = _hostPort.ToString() }
                    }
                },
                Binds = new List<string>
                {
                    $"{cwd}:/workspace",
                    $"{beastConfigDir}:/root/.beast"
                }
            }
        };

        CreateContainerResponse response = await _dockerClient.Containers.CreateContainerAsync(createParams, cancellationToken);
        _containerId = response.ID;
        _log.Verbose($"[docker] Container created: {_containerId}");

        await _dockerClient.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());
        _log.Verbose($"[docker] Container started: {name} on host port {_hostPort}");
        return _hostPort;
    }

    private static int FindAvailablePort()
    {
        using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Stops and removes the container started by StartAsync.
    public async Task StopAsync()
    {
        if (_containerId == null)
            return;
        string id = _containerId;
        try
        {
            await _dockerClient.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKill = 15 });
            _log.Verbose($"[docker] Container stopped: {id}");
            await _dockerClient.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters());
            _log.Verbose($"[docker] Container removed: {id}");
        }
        catch (Exception ex)
        {
            _log.Error($"[docker] Error stopping container: {ex.Message}");
        }
    }

    // Removes a named container if it exists, regardless of state. Used to clean up before relaunching.
    private async Task RemoveContainerByNameAsync(string name)
    {
        try
        {
            IList<ContainerListResponse> containers = await _dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters { All = true });

            foreach (ContainerListResponse container in containers)
            {
                foreach (string n in container.Names)
                {
                    if (n == $"/{name}" || n == name)
                    {
                        await _dockerClient.Containers.RemoveContainerAsync(
                            container.ID, new ContainerRemoveParameters { Force = true });
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[docker] Failed to remove container {name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _dockerClient?.Dispose();
    }
}
