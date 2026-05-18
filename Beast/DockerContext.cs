using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

// Launches and manages Docker containers on the host.
// Transport is now WebSocket; this class only handles container lifecycle.
public class DockerContext : IDisposable
{
    private readonly DockerClient _dockerClient;

    public DockerContext()
    {
        _dockerClient = new DockerClientConfiguration().CreateClient();
    }

    // Launches a container from the given image, maps port 13131, and mounts workspace/config volumes.
    public async Task<string> LaunchContainerAsync(
        string image, string name, IList<string> entrypoint)
    {
        string cwd = Directory.GetCurrentDirectory();
        string beastConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast");
        Directory.CreateDirectory(beastConfigDir);

        CreateContainerParameters createParams = new CreateContainerParameters
        {
            Image = image,
            Name = name,
            WorkingDir = "/workspace",
            ExposedPorts = new Dictionary<string, EmptyStruct> { ["13131/tcp"] = default },
            HostConfig = new HostConfig
            {
                NetworkMode = "bridge",
                AutoRemove = false,
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

        if (entrypoint.Count > 0)
        {
            createParams.Entrypoint = entrypoint;
        }

        Console.Error.WriteLine($"[docker] Creating container {name} from image {image}");
        CreateContainerResponse response = await _dockerClient.Containers.CreateContainerAsync(createParams);
        string containerId = response.ID;
        Console.Error.WriteLine($"[docker] Container created: {containerId}");

        await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
        Console.Error.WriteLine($"[docker] Container started: {name}");

        return containerId;
    }

    // Stops a container gracefully then removes it. Tolerates containers that are already gone.
    public async Task StopAndRemoveContainerAsync(string containerId)
    {
        try
        {
            await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone.
        }

        try
        {
            await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone.
        }
    }

    // Removes a named container if it exists, regardless of state. Used to clean up before relaunching.
    public async Task RemoveContainerByNameAsync(string name)
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
            Console.Error.WriteLine($"[docker] Failed to remove container {name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _dockerClient.Dispose();
    }
}
