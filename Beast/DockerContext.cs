using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

// Launches and manages Docker containers on the host, communicating via stdio.
public class DockerContext : IDisposable
{
    private readonly DockerClient _dockerClient;
    private MultiplexedStream? _readStream;   // stdout+stderr only — kept open for ReadOutputAsync
    private MultiplexedStream? _writeStream;  // stdin only — kept open for WriteAsync

    public DockerContext()
    {
        _dockerClient = new DockerClientConfiguration().CreateClient();
    }

    // Launches a container from the given image with the given entrypoint, communicating via stdio.
    // Attaches before starting so no early output is lost. Returns the container ID.
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
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            OpenStdin = true,
            StdinOnce = false,
            HostConfig = new HostConfig
            {
                NetworkMode = "bridge",
                AutoRemove = false,
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

        // Attach before starting to avoid missing output written at startup.
        // Two separate connections so read and write never share a stream and cannot deadlock.
        _readStream = await _dockerClient.Containers.AttachContainerAsync(containerId, false, new ContainerAttachParameters
        {
            Stdin = false,
            Stdout = true,
            Stderr = true,
            Stream = true
        });

        _writeStream = await _dockerClient.Containers.AttachContainerAsync(containerId, false, new ContainerAttachParameters
        {
            Stdin = true,
            Stdout = false,
            Stderr = false,
            Stream = true
        });

        await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
        Console.Error.WriteLine($"[docker] Container started: {name}");

        return containerId;
    }

    // Sends a framed message to the container's stdin.
    // Wire format: [type,length]content---
    public async Task SendAsync(string text)
    {
        if (_writeStream == null) return;
        byte[] contentBytes = System.Text.Encoding.UTF8.GetBytes(text);
        string frame = $"[{(byte)FrameType.Output},{contentBytes.Length}]{text}---";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(frame);
        await _writeStream.WriteAsync(bytes, 0, bytes.Length, default);
    }

    // Reads raw bytes from the container's stdout/stderr. Returns (count, eof).
    public async Task<(int Count, bool EOF)> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        if (_readStream == null) return (0, true);
        MultiplexedStream.ReadResult result = await _readStream.ReadOutputAsync(buffer, offset, count, token);
        return (result.Count, result.EOF);
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
        _readStream?.Dispose();
        _writeStream?.Dispose();
        _dockerClient.Dispose();
    }
}
