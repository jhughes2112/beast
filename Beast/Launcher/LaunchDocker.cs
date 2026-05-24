using System;
using System.Collections.Generic;
using System.IO;
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

    public LaunchDocker(string image, Log log)
    {
        _image = image;
        _log = log;
        _dockerClient = new DockerClientConfiguration().CreateClient();
    }

    // Removes any stale container with the same name, then creates and starts a fresh one.
    public async Task StartAsync(string name, CancellationToken cancellationToken)
    {
        await RemoveContainerByNameAsync(name);

        string cwd = Directory.GetCurrentDirectory();
        string beastConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast");
        Directory.CreateDirectory(beastConfigDir);

        CreateContainerParameters createParams = new CreateContainerParameters
        {
            Image = _image,
            Name = name,
            WorkingDir = "/workspace",
            Env = new List<string> { "BEAST_HOST=host.docker.internal" },
            ExposedPorts = new Dictionary<string, EmptyStruct> { ["13131/tcp"] = default },
            HostConfig = new HostConfig
            {
                AutoRemove = false,
                ExtraHosts = new List<string> { "host.docker.internal:host-gateway" },  // for linux to pick this up
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    ["13131/tcp"] = new List<PortBinding>
                    {
                        new PortBinding { HostIP = "127.0.0.1", HostPort = "13131" }
                    }
                },
                Binds = new List<string>
                {
                    $"{cwd}:/workspace",
                    $"{beastConfigDir}:/root/.beast"
                }
            }
        };

        _log.Verbose($"[docker] Creating container {name} from image {_image}");
        CreateContainerResponse response = await _dockerClient.Containers.CreateContainerAsync(createParams);
        _containerId = response.ID;
        _log.Verbose($"[docker] Container created: {_containerId}");

        await _dockerClient.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());
        _log.Verbose($"[docker] Container started: {name}");
    }

    // Stops and removes the container started by StartAsync.
    public async Task StopAsync()
    {
        if (_containerId == null) return;
        string id = _containerId;
        _containerId = null;

        try
        {
            await _dockerClient.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
        }
        catch (DockerContainerNotFoundException) { }

        try
        {
            await _dockerClient.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true });
        }
        catch (DockerContainerNotFoundException) { }
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
        _dockerClient.Dispose();
    }
}
