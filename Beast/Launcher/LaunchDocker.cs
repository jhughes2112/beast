using Docker.DotNet;
using Docker.DotNet.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

// Launches and manages an Agent Docker container.
// Implements IAgentContext so BeastApp can swap it for NativeContext.
public class LaunchDocker : ILauncher
{
	private readonly string              _image;
	private readonly DockerClient        _dockerClient;
	private readonly ClientLog           _log;
	private readonly Worktrees.Selection _worktree;
	private          string?             _containerId;
	private          int                 _hostPort;

	public LaunchDocker(string image, ClientLog log, Worktrees.Selection worktree)
	{
		_image        = image;
		_log          = log;
		_worktree     = worktree;
		_dockerClient = new DockerClientConfiguration().CreateClient();
	}

	public int HostPort => _hostPort;

	// Reaps a stale (exited) container with this name, then creates and starts a fresh one. A container
	// with this name that is still running means the worktree is occupied by another Beast instance —
	// that is refused rather than killed, since the deterministic name is the per-worktree lock.
	public async Task<int> StartAsync(string name, CancellationToken cancellationToken)
	{
		bool reaped = await RemoveStaleContainerByNameAsync(name);
		if (!reaped)
			throw new InvalidOperationException($"'{_worktree.Name}' is already in use by a running container ('{name}').");

		// Worktree run: the real repo is bound to /git as a pristine reference checkout and the worktree folder
		// to /workspace, where all tools operate. The agent runs `git worktree add /workspace` against /git once
		// it is up, using the branch passed as the --worktree-branch startup argument.
		// Ephemeral run: the current folder is bound straight to /workspace with no /git and no branch, so the
		// agent operates in place and skips all git worktree setup.
		string repoCwd          = _worktree.RepoCwd;
		string worktreeHostPath = _worktree.HostPath;
		string beastConfigDir   = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast");
		Directory.CreateDirectory(beastConfigDir);

		List<string> cmd;
		List<string> binds;
		if (_worktree.Ephemeral)
		{
			cmd   = new List<string>();
			binds = new List<string>
			{
				$"{repoCwd}:/workspace",
				$"{beastConfigDir}:/root/.beast"
			};
		}
		else
		{
			cmd   = new List<string> { "--worktree-branch", _worktree.Branch };
			binds = new List<string>
			{
				$"{repoCwd}:/git",
				$"{worktreeHostPath}:/workspace",
				$"{beastConfigDir}:/root/.beast"
			};
		}

		// Find an available port on the host
		_hostPort = FindAvailablePort();

		// Git identity: the host's name/email live in the user's global gitconfig, which is never bound into
		// the container, so git in there has no author and refuses to commit. These are the variables git
		// itself reads (they override every config file), and setpriv keeps the container environment, so
		// the agent's commit_and_rebase and the model's own shell both see them. Resolved from the launch
		// folder so a repo-local identity wins over the global one, exactly as the host's git would do.
		List<string> env      = new List<string>();
		string       gitName  = await HostGitConfigAsync(repoCwd, "user.name",  cancellationToken);
		string       gitEmail = await HostGitConfigAsync(repoCwd, "user.email", cancellationToken);
		if (gitName.Length > 0 && gitEmail.Length > 0)
		{
			env.Add($"GIT_AUTHOR_NAME={gitName}");
			env.Add($"GIT_AUTHOR_EMAIL={gitEmail}");
			env.Add($"GIT_COMMITTER_NAME={gitName}");
			env.Add($"GIT_COMMITTER_EMAIL={gitEmail}");
		}
		else
		{
			_log.Error("No git user.name/user.email found on the host; commits inside the container will fail until `git config --global user.name` and `user.email` are set.");
		}

		// Docker's WSL2 backend can hand the container the host GPU: /dev/dxg plus the WSL driver libraries
		// let the browser's WebGL run on the real GPU instead of software rendering.
		SystemInfoResponse  info    = await _dockerClient.System.GetSystemInfoAsync(cancellationToken);
		List<DeviceMapping> devices = new List<DeviceMapping>();
		if (IsWslBackend(info))
		{
			devices.Add(new DeviceMapping { PathOnHost = "/dev/dxg", PathInContainer = "/dev/dxg", CgroupPermissions = "rwm" });
			binds.Add("/usr/lib/wsl:/usr/lib/wsl:ro");
		}

		// The equivalent of `--gpus all`: when the host has the NVIDIA container runtime, ask it to mount the
		// driver libraries (CUDA, nvidia-smi, and with the image's NVIDIA_DRIVER_CAPABILITIES=all the video and
		// graphics families too). Requesting the "nvidia" driver on a host without that runtime fails container
		// creation outright, hence the gate. /dev/dri is not mapped: Beast's hosts are Windows, and neither
		// Docker Desktop VM (WSL2 or Hyper-V) exposes it, and a missing host device also fails creation.
		List<DeviceRequest> deviceRequests = new List<DeviceRequest>();
		if (HasNvidiaRuntime(info))
		{
			deviceRequests.Add(new DeviceRequest
			{
				Driver       = "nvidia",
				Count        = -1,
				Capabilities = new List<IList<string>> { new List<string> { "gpu" } }
			});
		}

		CreateContainerParameters createParams = new CreateContainerParameters
		{
			Image      = _image,
			Name       = name,
			WorkingDir = "/workspace",
			Env        = env,
			// Startup arguments to the agent entrypoint (config never travels as env vars; the git identity
			// above is the one exception because git reads it natively); empty for an ephemeral run.
			Cmd          = cmd,
			ExposedPorts = new Dictionary<string, EmptyStruct> { ["13131/tcp"] = default },
			HostConfig   = new HostConfig
			{
				AutoRemove   = false,
				ExtraHosts   = new List<string> { "host.docker.internal:host-gateway" },
				PortBindings = new Dictionary<string, IList<PortBinding>>
				{
					["13131/tcp"] = new List<PortBinding>
					{
						new PortBinding { HostIP = "127.0.0.1", HostPort = _hostPort.ToString() }
					}
				},
				Binds          = binds,
				Devices        = devices,
				DeviceRequests = deviceRequests
			}
		};

		CreateContainerResponse response = await _dockerClient.Containers.CreateContainerAsync(createParams, cancellationToken);
		_containerId                     = response.ID;
		_log.Verbose($"[docker] Container created: {_containerId}");

		await _dockerClient.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());
		_log.Verbose($"[docker] Container started: {name} on host port {_hostPort}");
		return _hostPort;
	}

	// True when the Docker VM is WSL2, the only backend that exposes /dev/dxg.
	private static bool IsWslBackend(SystemInfoResponse info)
	{
		return info.KernelVersion.Contains("WSL2", StringComparison.OrdinalIgnoreCase);
	}

	// True when the NVIDIA container runtime is registered with the daemon (Docker Desktop lists it once
	// GPU support is enabled; a native Linux host lists it after installing nvidia-container-toolkit).
	private static bool HasNvidiaRuntime(SystemInfoResponse info)
	{
		return info.Runtimes != null && info.Runtimes.ContainsKey("nvidia");
	}

	// One value from the host's effective git config as seen from cwd (repo-local, then global, then
	// system). Empty when unset or when git is not on the host PATH.
	private static async Task<string> HostGitConfigAsync(string cwd, string key, CancellationToken ct)
	{
		string value = string.Empty;
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName               = "git",
				WorkingDirectory       = cwd,
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				UseShellExecute        = false,
				CreateNoWindow         = true
			};
			psi.ArgumentList.Add("config");
			psi.ArgumentList.Add("--get");
			psi.ArgumentList.Add(key);
			using Process? proc = Process.Start(psi);
			if (proc != null)
			{
				string output = await proc.StandardOutput.ReadToEndAsync(ct);
				await proc.WaitForExitAsync(ct);
				if (proc.ExitCode == 0)
					value = output.Trim();
			}
		}
		catch (Win32Exception)
		{
			// git is not on the host PATH: leave the identity empty and let the launch log say so.
		}
		return value;
	}

	private static int FindAvailablePort()
	{
		const int startPort = 20000;
		const int endPort   = 21000;
		HashSet<int> usedPorts = GetUsedPorts();

		for (int port = startPort; port <= endPort; port++)
		{
			if (!usedPorts.Contains(port))
				return port;
		}

		throw new InvalidOperationException("No available port found in range.");
	}

	private static HashSet<int> GetUsedPorts()
	{
		IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();

		HashSet<int> used = new HashSet<int>();

		foreach (IPEndPoint ep in properties.GetActiveTcpListeners())
			used.Add(ep.Port);

		foreach (IPEndPoint ep in properties.GetActiveUdpListeners())
			used.Add(ep.Port);

		foreach (TcpConnectionInformation conn in properties.GetActiveTcpConnections())
			used.Add(conn.LocalEndPoint.Port);

		return used;
	}

	// True while the container is still running. False once it has exited (e.g. the agent crashed on
	// bad config), or if it cannot be inspected.
	public async Task<bool> IsAliveAsync()
	{
		if (_containerId == null)
			return false;

		try
		{
			ContainerInspectResponse info = await _dockerClient.Containers.InspectContainerAsync(_containerId);
			return info.State != null && info.State.Running;
		}
		catch (Exception ex)
		{
			_log.Verbose($"[docker] Inspect failed: {ex}");
			return false;
		}
	}

	// Returns the container's combined stdout/stderr so a startup crash is visible to the user.
	public async Task<string> GetLogsAsync(CancellationToken cancellationToken)
	{
		if (_containerId == null)
			return string.Empty;

		try
		{
			ContainerLogsParameters parameters = new ContainerLogsParameters
			{
				ShowStdout = true,
				ShowStderr = true,
				Timestamps = false
			};

			using MultiplexedStream stream = await _dockerClient.Containers.GetContainerLogsAsync(
				_containerId, false, parameters, cancellationToken);
			(string stdout, string stderr) = await stream.ReadOutputToEndAsync(cancellationToken);

			return string.IsNullOrEmpty(stderr) ? stdout : stdout + stderr;
		}
		catch (Exception ex)
		{
			return $"(failed to read container logs: {ex})";
		}
	}

	// Stops and removes the container started by StartAsync.
	public async Task StopAsync()
	{
		if (_containerId == null)
			return;
		string id = _containerId;
		try
		{
			await _dockerClient.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = 15 });
			_log.Verbose($"[docker] Container stopped: {id}");
			await _dockerClient.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters());
			_log.Verbose($"[docker] Container removed: {id}");
		}
		catch (Exception ex)
		{
			_log.Error($"[docker] Error stopping container: {ex}");
		}
	}

	// Reaps a stale (non-running) container with this name so a relaunch into the same worktree works.
	// Returns false without removing anything if a container with this name is still running — the caller
	// must not kill it, since one running container per name is the per-worktree occupancy lock. Returns
	// true when the name is free (nothing there, or a stopped container was removed).
	private async Task<bool> RemoveStaleContainerByNameAsync(string name)
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
						bool running = string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase);
						if (running)
							return false;

						await _dockerClient.Containers.RemoveContainerAsync(
							container.ID, new ContainerRemoveParameters { Force = true });
						return true;
					}
				}
			}
		}
		catch (Exception ex)
		{
			_log.Error($"[docker] Failed to inspect container {name}: {ex}");
		}

		return true;
	}

	// Worktree names whose beast_<name> container is currently running, so the launch menu can mark them
	// occupied. Best effort: if Docker is unreachable, returns an empty set (nothing shown as in use).
	public static async Task<HashSet<string>> RunningWorktreeNamesAsync()
	{
		HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			using DockerClient client               = new DockerClientConfiguration().CreateClient();
			IList<ContainerListResponse> containers = await client.Containers.ListContainersAsync(
				new ContainersListParameters { All = false });

			foreach (ContainerListResponse container in containers)
			{
				foreach (string n in container.Names)
				{
					string bare = n.TrimStart('/');
					if (bare.StartsWith("beast_", StringComparison.Ordinal))
						names.Add(bare.Substring("beast_".Length));
				}
			}
		}
		catch
		{
		}

		return names;
	}

	public void Dispose()
	{
		_dockerClient?.Dispose();
	}
}