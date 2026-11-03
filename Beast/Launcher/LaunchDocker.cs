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
	private readonly string          _image;
	private readonly DockerClient    _dockerClient;
	private readonly ClientLog       _log;
	private readonly SessionRecord   _session;
	private readonly AgentContainer? _existing;
	private          string?         _containerId;
	private          int             _hostPort;

	// Every agent container carries the id of the session it serves, read back to pair sessions with
	// their containers. Containers from earlier Beasts carry the old folder label, or none.
	public const  string SessionLabel   = "beast.session";
	private const string OldFolderLabel = "beast.folder";

	// One agent container on this machine. SessionId is empty for a container from an earlier Beast; such a
	// container serves no session and is only listed so it can be deleted.
	public sealed class AgentContainer
	{
		public readonly string Id;
		public readonly string Name;
		public readonly string SessionId;
		public readonly bool   Running;
		public readonly bool   CurrentImage;

		public AgentContainer(string id, string name, string sessionId, bool running, bool currentImage)
		{
			Id           = id;
			Name         = name;
			SessionId    = sessionId;
			Running      = running;
			CurrentImage = currentImage;
		}
	}

	// existing: the session's container, to start again; null to create one.
	public LaunchDocker(string image, ClientLog log, SessionRecord session, AgentContainer? existing)
	{
		_image        = image;
		_log          = log;
		_session      = session;
		_existing     = existing;
		_dockerClient = DockerEndpoint.CreateClient();
	}

	public int HostPort => _hostPort;

	// Every Beast agent container on this machine: those labelled with a session, and older ones (the
	// previous folder label, or an unlabelled beast_* name). Best effort: Docker unreachable reads as none.
	public static async Task<List<AgentContainer>> ListAgentsAsync(string image)
	{
		List<AgentContainer> agents = new List<AgentContainer>();
		try
		{
			using DockerClient client = DockerEndpoint.CreateClient();
			string current            = string.Empty;
			try
			{
				ImageInspectResponse img = await client.Images.InspectImageAsync(image);
				current                  = img.ID;
			}
			catch (DockerImageNotFoundException)
			{
			}

			IList<ContainerListResponse> containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true });
			foreach (ContainerListResponse container in containers)
			{
				string  name      = container.Names.Count > 0 ? container.Names[0].TrimStart('/') : string.Empty;
				string? sessionId = null;
				bool    labelled  = container.Labels != null && container.Labels.TryGetValue(SessionLabel, out sessionId);
				bool    older     = container.Labels != null && container.Labels.ContainsKey(OldFolderLabel);
				if (labelled || older || name.StartsWith("beast_", StringComparison.Ordinal))
					agents.Add(new AgentContainer(container.ID, name, labelled ? sessionId ?? string.Empty : string.Empty,
						string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase), current.Length > 0 && container.ImageID == current));
			}
		}
		catch (Exception)
		{
		}
		return agents;
	}

	// Deletes a stopped agent container. Nothing of the user's is in it (the workspace, the worktree and the
	// session's conversations are host folders); only what was installed inside goes. Returns why not, or empty.
	public static async Task<string> RemoveStoppedAsync(AgentContainer agent)
	{
		string error = string.Empty;
		if (agent.Running)
		{
			error = "it is running; stop it first";
		}
		else
		{
			try
			{
				using DockerClient client = DockerEndpoint.CreateClient();
				await client.Containers.RemoveContainerAsync(agent.Id, new ContainerRemoveParameters());
			}
			catch (Exception ex)
			{
				error = ex.Message;
			}
		}
		return error;
	}

	// Stops a running agent container. The Beast window connected to it loses its connection. Returns why
	// it could not, or empty.
	public static async Task<string> StopAgentAsync(AgentContainer agent)
	{
		string error = string.Empty;
		try
		{
			using DockerClient client = DockerEndpoint.CreateClient();
			await client.Containers.StopContainerAsync(agent.Id, new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
		}
		catch (Exception ex)
		{
			error = ex.Message;
		}
		return error;
	}

	// Starts the session's container again when it has one made from the current agent image, keeping
	// everything inside it. A container from an older image is replaced, as is having none: the new one is
	// created under name. The caller never hands over a running container: that session is busy elsewhere.
	public async Task<int> StartAsync(string name, CancellationToken cancellationToken)
	{
		int port;
		if (_existing != null && _existing.CurrentImage)
		{
			port = await ResumeAsync(_existing, cancellationToken);
		}
		else
		{
			if (_existing != null)
				await _dockerClient.Containers.RemoveContainerAsync(_existing.Id, new ContainerRemoveParameters { Force = true }, cancellationToken);
			port = await CreateAsync(name, cancellationToken);
		}
		return port;
	}

	// /etc/hosts entries for the container: host.docker.internal, plus every name this machine's own DNS gives
	// it, all pointing at Docker's host gateway (which reaches the host's own ports under Docker Desktop). The
	// names are the hostname, the canonical name and aliases the resolver returns for it, and, for an
	// unqualified hostname, the hostname with each DNS suffix the network configured, as a resolver's search
	// list would. A container can't use the host's resolver, so without these the machine's name is unknown inside.
	private static async Task<List<string>> HostAliasesAsync(CancellationToken cancellationToken)
	{
		List<string> names    = new List<string>();
		string       hostname = Dns.GetHostName();
		AddName(names, hostname);
		try
		{
			IPHostEntry entry = await Dns.GetHostEntryAsync(hostname, cancellationToken);
			AddName(names, entry.HostName);
			foreach (string alias in entry.Aliases)
				AddName(names, alias);
		}
		catch (SocketException)
		{
		}

		if (!hostname.Contains('.'))
		{
			AddName(names, $"{hostname}.{IPGlobalProperties.GetIPGlobalProperties().DomainName}");
			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
			{
				try
				{
					if (nic.OperationalStatus == OperationalStatus.Up)
						AddName(names, $"{hostname}.{nic.GetIPProperties().DnsSuffix}");
				}
				catch (PlatformNotSupportedException)
				{
				}
			}
		}

		List<string> hosts = new List<string> { "host.docker.internal:host-gateway" };
		foreach (string name in names)
			hosts.Add($"{name}:host-gateway");
		return hosts;
	}

	// Adds a host name once (names are case-insensitive), without a trailing dot. Blank names, a hostname
	// with an empty suffix ("omegasaur."), and localhost are skipped.
	private static void AddName(List<string> names, string name)
	{
		string clean = name.Trim().TrimEnd('.').ToLowerInvariant();
		if (clean.Length > 0 && clean != "localhost" && !names.Contains(clean))
			names.Add(clean);
	}

	private async Task<int> ResumeAsync(AgentContainer existing, CancellationToken cancellationToken)
	{
		_containerId = existing.Id;
		await _dockerClient.Containers.StartContainerAsync(existing.Id, new ContainerStartParameters(), cancellationToken);

		_hostPort = await PublishedPortAsync(existing.Id, cancellationToken);
		_log.Verbose($"[docker] Started container {existing.Id} again on host port {_hostPort}");
		return _hostPort;
	}

	// The host port Docker published the agent's 13131 on, read from the running container. Docker picks it
	// (and picks again on every start), so two sessions launched together can never claim the same port.
	private async Task<int> PublishedPortAsync(string containerId, CancellationToken cancellationToken)
	{
		ContainerInspectResponse info = await _dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);
		int                      port = 0;
		if (info.NetworkSettings?.Ports != null && info.NetworkSettings.Ports.TryGetValue("13131/tcp", out IList<PortBinding>? bindings) && bindings != null)
		{
			foreach (PortBinding binding in bindings)
			{
				if (port == 0)
					int.TryParse(binding.HostPort, out port);
			}
		}
		if (port == 0)
			throw new InvalidOperationException("The session's agent container has no published port; delete its agent from the session menu and start again.");
		return port;
	}

	private async Task<int> CreateAsync(string name, CancellationToken cancellationToken)
	{
		// Worktree session: the repository is bound to /git as the reference checkout and the session's
		// worktree folder to /workspace, where all tools operate. The agent runs `git worktree add /workspace`
		// against /git once it is up, using the branch passed as the --worktree-branch startup argument.
		// Bare session: the folder is bound straight to /workspace with no /git and no branch, so the agent
		// operates in place and skips all git worktree setup.
		// Either way ~/.beast is bound to /root/.beast, and the agent keeps its conversations and logs in the
		// session's own state folder (--state-dir), out of the user's project. That folder is bound on its own
		// at /root/.beast-state, since the sessions folder may live outside ~/.beast (sessionsDir); /root is
		// root's alone, so the sandboxed shell cannot reach it any more than /root/.beast.
		string beastConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast");
		Directory.CreateDirectory(beastConfigDir);
		Directory.CreateDirectory(_session.StateDir);

		List<string> cmd   = new List<string> { "--state-dir", "/root/.beast-state" };
		List<string> binds = new List<string>();
		if (_session.IsBare)
		{
			binds.Add($"{_session.Folder}:/workspace");
		}
		else
		{
			Directory.CreateDirectory(_session.WorktreePath);
			cmd.Add("--worktree-branch");
			cmd.Add(_session.Worktree);
			binds.Add($"{_session.Folder}:/git");
			binds.Add($"{_session.WorktreePath}:/workspace");
		}
		binds.Add($"{beastConfigDir}:/root/.beast");
		binds.Add($"{_session.StateDir}:/root/.beast-state");

		// Git identity: the host's name/email live in the user's global gitconfig, which is never bound into
		// the container, so git in there has no author and refuses to commit. These are the variables git
		// itself reads (they override every config file), and setpriv keeps the container environment, so
		// the agent's commit_and_rebase and the model's own shell both see them. Resolved from the launch
		// folder so a repo-local identity wins over the global one, exactly as the host's git would do.
		List<string> env      = new List<string>();
		string       gitName  = await HostGitConfigAsync(_session.Folder, "user.name",  cancellationToken);
		string       gitEmail = await HostGitConfigAsync(_session.Folder, "user.email", cancellationToken);
		if (gitName.Length > 0 && gitEmail.Length > 0)
		{
			env.Add($"GIT_AUTHOR_NAME={gitName}");
			env.Add($"GIT_AUTHOR_EMAIL={gitEmail}");
			env.Add($"GIT_COMMITTER_NAME={gitName}");
			env.Add($"GIT_COMMITTER_EMAIL={gitEmail}");
		}
		else if (!_session.IsBare)
		{
			// Only a worktree session commits (commit_and_rebase integrates its branch), so only it needs warning.
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
		List<string>        extraHosts     = await HostAliasesAsync(cancellationToken);
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
			// Which session this is, read back by every launch (ListAgentsAsync) to pair it with its record.
			Labels = new Dictionary<string, string> { [SessionLabel] = _session.Id },
			// Startup arguments to the agent entrypoint (config never travels as env vars; the git identity
			// above is the one exception because git reads it natively).
			Cmd          = cmd,
			ExposedPorts = new Dictionary<string, EmptyStruct> { ["13131/tcp"] = default },
			HostConfig   = new HostConfig
			{
				AutoRemove   = false,
				ExtraHosts   = extraHosts,
				PortBindings = new Dictionary<string, IList<PortBinding>>
				{
					["13131/tcp"] = new List<PortBinding>
					{
						// No HostPort: Docker assigns a free one when the container starts.
						new PortBinding { HostIP = "127.0.0.1", HostPort = string.Empty }
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

		await _dockerClient.Containers.StartContainerAsync(_containerId, new ContainerStartParameters(), cancellationToken);
		_hostPort = await PublishedPortAsync(_containerId, cancellationToken);
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

	// Stops the container started or reconnected by StartAsync. It is kept, with whatever was installed
	// in it, for the next launch here to reconnect to; remove deletes it too (after /finish, when the
	// worktree it served is gone).
	public async Task StopAsync(bool remove)
	{
		if (_containerId != null)
		{
			string id = _containerId;
			try
			{
				await _dockerClient.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = 15 });
				_log.Verbose($"[docker] Container stopped: {id}");
				if (remove)
				{
					await _dockerClient.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters());
					_log.Verbose($"[docker] Container removed: {id}");
				}
			}
			catch (Exception ex)
			{
				_log.Error($"[docker] Error stopping container: {ex}");
			}
		}
	}

	public void Dispose()
	{
		_dockerClient?.Dispose();
	}
}