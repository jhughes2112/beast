using Docker.DotNet;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;


// The Docker daemon beast talks to: whatever the docker CLI itself would use — the active context, which
// covers Docker Desktop's per-user socket on macOS (~/.docker/run/docker.sock), Colima, rootless Docker,
// and DOCKER_HOST/DOCKER_CONTEXT if the user set them. Asked once per run; when the CLI is missing or
// names something Docker.DotNet cannot reach (an ssh:// context), the library's platform default is used
// (the docker_engine named pipe on Windows, /var/run/docker.sock elsewhere).
internal static class DockerEndpoint
{
	private const int CliTimeoutMs = 5000;

	private static readonly object Gate = new object();
	private static          Uri?   _resolved;
	private static          bool   _probed;

	internal static DockerClient CreateClient()
	{
		Uri?                      endpoint = Resolve();
		DockerClientConfiguration config   = endpoint != null ? new DockerClientConfiguration(endpoint) : new DockerClientConfiguration();
		return config.CreateClient();
	}

	private static Uri? Resolve()
	{
		lock (Gate)
		{
			if (!_probed)
			{
				_resolved = Normalize(ContextHost());
				_probed   = true;
			}
			return _resolved;
		}
	}

	// The current context's endpoint, as `docker context inspect` prints it; empty when unavailable.
	private static string ContextHost()
	{
		StringBuilder output = new StringBuilder();
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName               = "docker",
				RedirectStandardInput  = true,
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				UseShellExecute        = false,
				CreateNoWindow         = true
			};
			psi.ArgumentList.Add("context");
			psi.ArgumentList.Add("inspect");
			psi.ArgumentList.Add("--format");
			psi.ArgumentList.Add("{{.Endpoints.docker.Host}}");

			using Process process       = Process.Start(psi)!;
			process.OutputDataReceived += (sender, e) =>
			{
				if (e.Data != null)
				{
					lock (output)
						output.Append(e.Data);
				}
			};
			process.ErrorDataReceived += (sender, e) => { };
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			process.StandardInput.Close();

			if (process.WaitForExit(CliTimeoutMs))
			{
				process.WaitForExit();
				if (process.ExitCode != 0)
				{
					lock (output)
						output.Clear();
				}
			}
			else
			{
				process.Kill();
				lock (output)
					output.Clear();
			}
		}
		catch (Win32Exception)
		{
			// docker is not on PATH: fall back to the library default.
		}

		string host;
		lock (output)
			host = output.ToString().Trim();
		return host;
	}

	// Turns the CLI's endpoint into a URI Docker.DotNet accepts, or null to use the library default. The CLI
	// writes named pipes as npipe:////./pipe/<name>; Docker.DotNet wants npipe://./pipe/<name>.
	private static Uri? Normalize(string host)
	{
		Uri? uri = null;
		if (host.StartsWith("npipe:////", StringComparison.OrdinalIgnoreCase))
			uri = new Uri($"npipe://{host.Substring("npipe:////".Length)}");
		else if (host.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase)
			|| host.StartsWith("unix://",  StringComparison.OrdinalIgnoreCase)
			|| host.StartsWith("tcp://",   StringComparison.OrdinalIgnoreCase)
			|| host.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)
			|| host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			uri = new Uri(host);
		return uri;
	}
}