using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// The Anthropic subscription endpoint. A subscription may only be driven through the Claude Code
// CLI ("claude -p"), never the raw API, so this endpoint has no URL to call: its baseUrl is the
// claude-code:// marker and its apiKey is the long-lived OAuth token from `claude setup-token`.
// The token reaches the CLI only through the environment of the child process this executable
// starts — nothing outside the agent ever sets it.
public static class ClaudeCode
{
	public const string EndpointUrl = "claude-code://subscription";

	// Model ids carry this prefix so they never collide with the same model on an API endpoint —
	// comparing the two side by side is the point of having both.
	private const string kModelPrefix = "claude-code/";

	// The CLI's model list carries no window, but its usage reports 200k for its default windows; a
	// larger one is set per model in /config.
	private const int kContextWindow = 200000;

	// The handshake the Agent SDK opens a stream-json session with. Its reply lists the models the
	// account may use, without sending a request to any model.
	private const           string   kInitializeRequest = "{\"type\":\"control_request\",\"request_id\":\"beast-init\",\"request\":{\"subtype\":\"initialize\"}}";
	private static readonly TimeSpan kCatalogTimeout    = TimeSpan.FromSeconds(60);

	// The login check is the cheapest real request: Haiku, a one-line system prompt, no tools.
	private const           string   kProbeModel        = "haiku";
	private const           string   kProbeSystemPrompt = "Reply with the single word OK.";
	private static readonly TimeSpan kProbeTimeout      = TimeSpan.FromSeconds(90);
	private static readonly TimeSpan kUpdateTimeout     = TimeSpan.FromMinutes(3);

	// Variables the CLI would honor ahead of (or instead of) the subscription token. An inherited API
	// key silently bills the API instead of the subscription, which would void any comparison.
	private static readonly string[] kShadowingVariables = { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL" };

	// `claude update` runs once per container, on the first use of the endpoint.
	private static readonly SemaphoreSlim _updateGate = new SemaphoreSlim(1, 1);
	private static          bool          _updated;

	public static bool IsEndpoint(string url)
	{
		return url.StartsWith("claude-code://", StringComparison.OrdinalIgnoreCase);
	}

	// The models this subscription offers, as the CLI's initialize handshake reports them. The list
	// is the account's own: an unauthenticated CLI answers with only its four built-in aliases.
	public static async Task<(List<DiscoveredModel> Models, string Error)> FetchCatalogAsync(string token, CancellationToken ct)
	{
		List<DiscoveredModel> models = new List<DiscoveredModel>();
		string                error;
		if (string.IsNullOrWhiteSpace(token))
		{
			error = "No OAuth token is configured for the subscription.";
		}
		else
		{
			string update = await EnsureUpdatedAsync(ct);
			if (update.Length > 0)
				Console.Error.WriteLine($"[ClaudeCode] {update}");

			List<string> args = new List<string>
			{
				"-p",
				"--input-format", "stream-json",
				"--output-format", "stream-json",
				"--verbose",
				"--no-session-persistence",
				"--tools", "",
				"--strict-mcp-config",
				"--setting-sources", ""
			};
			(bool started, int exitCode, string stdout, string stderr) = await RunAsync(args, token, kInitializeRequest, kCatalogTimeout, ct);
			if (!started)
			{
				error = "The Claude Code CLI (claude) was not found.";
			}
			else
			{
				models = ParseCatalog(stdout);
				error  = models.Count > 0 ? string.Empty : $"The CLI listed no models (exit {exitCode}): {LastLine(stderr.Length > 0 ? stderr : stdout)}";
			}
		}
		return (models, error);
	}

	// One model per pinned version, never per alias: "opus" floats to whatever is newest, and a
	// benchmark needs to know which model it ran. The alias entries still contribute the versions
	// they resolve to — the newest of each family is listed only that way.
	internal static List<DiscoveredModel> ParseCatalog(string stdout)
	{
		List<DiscoveredModel> models = new List<DiscoveredModel>();
		HashSet<string>       seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string line in stdout.Split('\n'))
		{
			JsonObject? message = null;
			try
			{
				message = JsonNode.Parse(line) as JsonObject;
			}
			catch (JsonException)
			{
			}

			JsonArray? listed = message?["response"]?["response"]?["models"] as JsonArray;
			if (message?["type"]?.ToString() != "control_response" || listed == null)
				continue;

			foreach (JsonNode? entry in listed)
			{
				string version = entry?["resolvedModel"]?.ToString() ?? string.Empty;
				string value   = entry?["value"]?.ToString() ?? string.Empty;

				// "default" is a pointer at another entry, named for the pointer, not the model.
				if (version.Length == 0 || value == "default" || !seen.Add(version))
					continue;

				models.Add(new DiscoveredModel
				{
					Id            = $"{kModelPrefix}{version}",
					Name          = $"{entry?["displayName"]?.ToString() ?? version} (subscription)",
					ContextWindow = kContextWindow,
					CostInput     = 0m,
					CostOutput    = 0m,
					CostCacheRead = 0m,
					Modalities    = new List<string> { "text", "image" }
				});
			}
		}
		return models;
	}

	// Brings the image's Claude Code up to date the first time this container uses the endpoint.
	// Only inside the agent's own image: on a native run the CLI is the host's install, which is
	// not ours to change. Returns a status line; a failed update is reported, never fatal.
	public static async Task<string> EnsureUpdatedAsync(CancellationToken ct)
	{
		string status = string.Empty;
		if (Sandbox.ShellUserActive)
		{
			await _updateGate.WaitAsync(ct);
			try
			{
				if (!_updated)
				{
					(bool started, int exitCode, string stdout, string stderr) = await RunAsync(new List<string> { "update" }, string.Empty, string.Empty, kUpdateTimeout, ct);
					_updated = true;
					if (!started)
						status = "Claude Code is not installed in this image.";
					else if (exitCode == 0)
						status = LastLine(stdout);
					else
						status = $"claude update failed (exit {exitCode}): {LastLine(stderr.Length > 0 ? stderr : stdout)}";
				}
			}
			finally
			{
				_updateGate.Release();
			}
		}
		return status;
	}

	// Proves the token logs in by making one real request: `claude auth status` only reports that a
	// token is present and calls a bogus one "loggedIn". Returns empty on success, else the reason.
	public static async Task<string> VerifyLoginAsync(string token, CancellationToken ct)
	{
		string error;
		if (string.IsNullOrWhiteSpace(token))
		{
			error = "A long-lived OAuth token is required: run `claude setup-token` on a machine with a browser and paste the token it prints.";
		}
		else
		{
			List<string> args = new List<string>
			{
				"-p",
				"--output-format", "json",
				"--no-session-persistence",
				"--tools", "",
				"--strict-mcp-config",
				"--setting-sources", "",
				"--system-prompt", kProbeSystemPrompt,
				"--model", kProbeModel,
				"OK?"
			};
			(bool started, int exitCode, string stdout, string stderr) = await RunAsync(args, token, string.Empty, kProbeTimeout, ct);
			if (!started)
				error = "The Claude Code CLI (claude) was not found.";
			else
				error = ParseProbeResult(stdout, exitCode, stderr);
		}
		return error;
	}

	// Reads the CLI's json result: is_error=false is a working login; otherwise the result text says
	// why (a 401 reads "Failed to authenticate. API Error: 401 OAuth access token is invalid.").
	internal static string ParseProbeResult(string stdout, int exitCode, string stderr)
	{
		string      error;
		JsonObject? result = null;
		try
		{
			result = JsonNode.Parse(stdout) as JsonObject;
		}
		catch (JsonException)
		{
		}

		if (result != null && result["is_error"] is JsonValue isError && isError.TryGetValue(out bool failed))
		{
			error = failed ? (result["result"]?.ToString() ?? "The login check failed without a reason.") : string.Empty;
		}
		else
		{
			string detail = LastLine(stderr.Length > 0 ? stderr : stdout);
			error         = $"The login check produced no result (exit {exitCode}): {detail}";
		}
		return error;
	}

	// Runs the CLI with the subscription token in its environment (and nothing that would outrank
	// it), from the temp folder so no project settings or CLAUDE.md are picked up. stdin is written
	// and then closed. started=false when there is no claude to run. A run past the timeout is
	// killed and reported as exit -1.
	private static async Task<(bool Started, int ExitCode, string StdOut, string StdErr)> RunAsync(List<string> args, string token, string stdin, TimeSpan timeout, CancellationToken ct)
	{
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName               = "claude",
			WorkingDirectory       = Path.GetTempPath(),
			RedirectStandardInput  = true,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
			CreateNoWindow         = true
		};
		foreach (string arg in args)
			psi.ArgumentList.Add(arg);
		foreach (string name in kShadowingVariables)
			psi.Environment.Remove(name);
		if (token.Length > 0)
			psi.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = token;

		bool   started  = false;
		int    exitCode = -1;
		string stdout   = string.Empty;
		string stderr   = string.Empty;

		using Process process = new Process { StartInfo = psi };
		try
		{
			started = process.Start();
		}
		catch (Win32Exception)
		{
		}

		if (started)
		{
			// -p reads a prompt (or stream-json messages) from stdin; closing it says there is no more.
			if (stdin.Length > 0)
				await process.StandardInput.WriteLineAsync(stdin.AsMemory(), ct);
			process.StandardInput.Close();

			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
			limit.CancelAfter(timeout);

			Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(limit.Token);
			Task<string> stderrTask = process.StandardError.ReadToEndAsync(limit.Token);
			try
			{
				await process.WaitForExitAsync(limit.Token);
				stdout   = await stdoutTask;
				stderr   = await stderrTask;
				exitCode = process.ExitCode;
			}
			catch (OperationCanceledException)
			{
				process.Kill(true);
				ct.ThrowIfCancellationRequested();
				stderr = $"timed out after {timeout.TotalSeconds:0}s";
			}
		}
		return (started, exitCode, stdout, stderr);
	}

	private static string LastLine(string text)
	{
		string[] lines = text.Trim().Split('\n');
		return lines[lines.Length - 1].Trim();
	}
}