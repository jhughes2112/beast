using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// The Google subscription endpoint (Google AI Pro / Ultra), driven through the Antigravity CLI's Agent
// Client Protocol server ("agy acp"). Like the Anthropic subscription there is no URL to call: the
// baseUrl is the antigravity:// marker and the apiKey in settings.json is the Google OAuth refresh
// token from Beast's own sign-in.
//
// The CLI keeps its credentials in a token file under its state folder (it falls back to a file when
// there is no OS keyring, which is always the case in the container). Beast never shares a state
// folder with anything: every process it starts gets a fresh private one, seeded with a token file
// built from the refresh token, and the CLI mints its own access token from that. Google's refresh
// tokens do not rotate, so nothing has to be written back.
//
// Sign-in is the CLI's own ("agy acp --login"): it prints a Google URL and waits on stdin for the
// authorization code the browser ends on. /config opens the URL on the host and relays the code.
public static class Antigravity
{
	public const string EndpointUrl = "antigravity://subscription";

	// Model ids carry this prefix so they never collide with the same model on an API endpoint —
	// comparing the two side by side is the point of having both.
	private const string kModelPrefix = "antigravity/";

	// The CLI reports 1,048,576 for its Gemini models and 1,000,000 for the Claude ones; a different
	// window is set per model in /config.
	private const int kContextWindow = 1000000;

	// JSON-RPC error the CLI answers session requests with until it is signed in.
	private const int kAuthRequired = -32000;

	private const string kTokenFile = "antigravity-oauth-token";

	private static readonly TimeSpan kHandshakeTimeout = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan kLoginUrlTimeout  = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan kLoginExchange    = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan kLoginLifetime    = TimeSpan.FromMinutes(15);

	// Variables that would switch the CLI to another way of signing in, billed somewhere else.
	private static readonly string[] kShadowingVariables = { "GEMINI_API_KEY", "GOOGLE_GEMINI_BASE_URL", "AGY_ADC_AUTH" };

	// A sign-in waiting for its authorization code: the code only fits the process that issued the URL.
	private sealed class PendingLogin
	{
		public Process Process  { get; }
		public string  StateDir { get; }
		public Timer   Expiry   { get; }

		public PendingLogin(Process process, string stateDir, Timer expiry)
		{
			Process  = process;
			StateDir = stateDir;
			Expiry   = expiry;
		}
	}

	private static readonly object        _loginLock = new object();
	private static          PendingLogin? _login;

	public static bool IsEndpoint(string url)
	{
		return url.StartsWith("antigravity://", StringComparison.OrdinalIgnoreCase);
	}

	// The name the CLI knows the model by: the model id without the prefix that keeps it apart from API ids.
	public static string CliModel(string modelId)
	{
		return modelId.StartsWith(kModelPrefix, StringComparison.OrdinalIgnoreCase) ? modelId.Substring(kModelPrefix.Length) : modelId;
	}

	public static bool LoginPending
	{
		get
		{
			lock (_loginLock)
			{
				return _login != null;
			}
		}
	}

	// ---- State folder ----

	// A fresh private state folder for one CLI process. With a refresh token it is signed in: the token
	// file carries no access token and an expiry long past, so the CLI refreshes before its first request.
	public static string CreateStateDir(string refreshToken)
	{
		string dir = Path.Combine(Path.GetTempPath(), $"beast-agy-{Guid.NewGuid():N}");
		CreatePrivateDirectory(dir);
		if (refreshToken.Length > 0)
		{
			string cliDir = Path.Combine(dir, "antigravity-cli");
			CreatePrivateDirectory(cliDir);

			JsonObject file = new JsonObject
			{
				["token"] = new JsonObject
				{
					["access_token"]  = string.Empty,
					["token_type"]    = "Bearer",
					["refresh_token"] = refreshToken,
					["expiry"]        = "2000-01-01T00:00:00Z"
				},
				["auth_method"] = "consumer"
			};
			WritePrivateFile(Path.Combine(cliDir, kTokenFile), file.ToJsonString());
		}
		return dir;
	}

	public static void DeleteStateDir(string dir)
	{
		try
		{
			if (dir.Length > 0 && Directory.Exists(dir))
			{
				Directory.Delete(dir, true);
			}
		}
		catch (Exception)
		{
		}
	}

	// These files hold the sign-in and the tool bridge's secret route: only the agent's own user may read them.
	public static void WritePrivateFile(string path, string text)
	{
		FileStreamOptions options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		using StreamWriter writer = new StreamWriter(path, new UTF8Encoding(false), options);
		writer.Write(text);
	}

	public static void CreatePrivateDirectory(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			Directory.CreateDirectory(path);
		}
		else
		{
			Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	// The CLI's ACP server on the given state folder, started inside it so no project's AGENTS.md,
	// rules or MCP servers are picked up.
	public static ProcessStartInfo StartInfo(string stateDir, List<string> extraArgs)
	{
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName               = "agy",
			WorkingDirectory       = stateDir,
			RedirectStandardInput  = true,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
			CreateNoWindow         = true
		};
		psi.ArgumentList.Add("acp");
		psi.ArgumentList.Add("--gemini-dir");
		psi.ArgumentList.Add(stateDir);
		foreach (string arg in extraArgs)
		{
			psi.ArgumentList.Add(arg);
		}

		foreach (string name in kShadowingVariables)
		{
			psi.Environment.Remove(name);
		}

		return psi;
	}

	// The handshake every use of the server opens with. The session/new reply lists the account's
	// models; Code is the JSON-RPC error code of a refusal.
	public static async Task<(JsonObject? Session, int Code, string Error)> OpenSessionAsync(AcpConnection connection, string cwd, CancellationToken ct)
	{
		JsonObject hello = new JsonObject
		{
			["protocolVersion"]    = 1,
			["clientCapabilities"] = new JsonObject
			{
				["fs"]       = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
				["terminal"] = false
			},
			["clientInfo"] = new JsonObject { ["name"] = "beast", ["version"] = "1.0" }
		};
		(JsonObject? session, int code, string error) = await connection.RequestAsync("initialize", hello, kHandshakeTimeout, ct);
		if (session != null)
		{
			JsonObject open        = new JsonObject { ["cwd"] = cwd, ["mcpServers"] = new JsonArray() };
			(session, code, error) = await connection.RequestAsync("session/new", open, kHandshakeTimeout, ct);
		}
		return (session, code, error);
	}

	// ---- Catalog ----

	// The models this subscription offers, as a new session lists them. Opening one is also what proves
	// the sign-in: NeedsLogin says the stored one is missing or no longer accepted.
	public static async Task<(List<DiscoveredModel> Models, string Error, bool NeedsLogin)> FetchCatalogAsync(string refreshToken, CancellationToken ct)
	{
		List<DiscoveredModel> models = new List<DiscoveredModel>();
		string                error;
		bool                  needsLogin = false;
		if (string.IsNullOrWhiteSpace(refreshToken))
		{
			error      = "Not signed in to a Google subscription: open /config and choose Google Subscription.";
			needsLogin = true;
		}
		else
		{
			string stateDir = CreateStateDir(refreshToken);
			(AcpConnection? connection, string startError) = AcpConnection.Start(StartInfo(stateDir, new List<string>()));
			if (connection == null)
			{
				error = $"The Antigravity CLI (agy) could not be started: {startError}";
			}
			else
			{
				try
				{
					(JsonObject? session, int code, string sessionError) = await OpenSessionAsync(connection, stateDir, ct);
					if (session != null)
					{
						models = ParseCatalog(session);
						error  = models.Count > 0 ? string.Empty : "The subscription listed no models.";
					}
					else
					{
						needsLogin = code == kAuthRequired;
						error      = needsLogin ? "The Google sign-in has expired or was revoked." : $"The Antigravity CLI refused the session: {sessionError}";
					}
				}
				finally
				{
					connection.Kill();
				}
			}
			DeleteStateDir(stateDir);
		}
		return (models, error, needsLogin);
	}

	// One model per entry of the session's model picker. The CLI spells each thinking level as its own
	// model id (gemini-4-neon-high), so a model's effort options are the suffixes its ids come in; a
	// model with a single id has none to choose.
	internal static List<DiscoveredModel> ParseCatalog(JsonObject session)
	{
		List<string> variants = new List<string>();
		if (session["models"]?["availableModels"] is JsonArray available)
		{
			foreach (JsonNode? entry in available)
			{
				string id = entry?["modelId"]?.ToString() ?? string.Empty;
				if (id.Length > 0)
				{
					variants.Add(id);
				}
			}
		}

		List<DiscoveredModel> models = new List<DiscoveredModel>();
		if (session["configOptions"] is JsonArray options)
		{
			foreach (JsonNode? option in options)
			{
				if (option?["id"]?.ToString() != "model" || option["options"] is not JsonArray choices)
				{
					continue;
				}

				foreach (JsonNode? choice in choices)
				{
					string value = choice?["value"]?.ToString() ?? string.Empty;
					if (value.Length == 0)
					{
						continue;
					}

					List<string> efforts = new List<string>();
					foreach (string variant in variants)
					{
						if (variant.Length > value.Length + 1 && variant.StartsWith($"{value}-", StringComparison.Ordinal))
						{
							efforts.Add(variant.Substring(value.Length + 1));
						}
					}

					string name = choice?["name"]?.ToString() ?? value;
					models.Add(new DiscoveredModel
					{
						Id            = $"{kModelPrefix}{value}",
						Name          = $"{name} (subscription)",
						ContextWindow = kContextWindow,
						CostInput     = 0m,
						CostOutput    = 0m,
						CostCacheRead = 0m,
						Modalities    = new List<string> { "text", "image" },
						Efforts       = ReasoningEffort.InAnthropicOrder(efforts),
						EffortsListed = true,
						// A level is part of the model's id: there is no id for the model without one.
						ThinkingRequired = efforts.Count > 0
					});
				}
			}
		}
		return models;
	}

	// ---- Sign-in ----

	// Starts the CLI's sign-in and returns the Google URL to open. The process stays alive, waiting
	// for the authorization code, until CompleteLoginAsync or the lifetime runs out.
	public static async Task<(string Url, string Error)> BeginLoginAsync(CancellationToken ct)
	{
		AbandonLogin();

		string           url      = string.Empty;
		string           error    = string.Empty;
		string           stateDir = CreateStateDir(string.Empty);
		ProcessStartInfo psi      = StartInfo(stateDir, new List<string> { "--login" });
		Process          process  = new Process { StartInfo = psi };
		bool             started  = false;
		try
		{
			started = process.Start();
		}
		catch (Win32Exception ex)
		{
			error = $"The Antigravity CLI (agy) could not be started: {ex.Message}";
		}

		if (started)
		{
			process.BeginErrorReadLine();

			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
			limit.CancelAfter(kLoginUrlTimeout);
			try
			{
				string? line = string.Empty;
				while (url.Length == 0 && line != null)
				{
					line = await process.StandardOutput.ReadLineAsync(limit.Token);
					if (line != null && line.Trim().StartsWith("https://", StringComparison.Ordinal))
					{
						url = line.Trim();
					}
				}
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
			}

			if (url.Length > 0)
			{
				PendingLogin login = new PendingLogin(process, stateDir, new Timer(_ => AbandonLogin(), null, kLoginLifetime, Timeout.InfiniteTimeSpan));
				lock (_loginLock)
				{
					_login = login;
				}
			}
			else
			{
				error = "The Antigravity CLI did not offer a sign-in link.";
				EndLogin(process, stateDir);
			}
		}
		else
		{
			process.Dispose();
			DeleteStateDir(stateDir);
			if (error.Length == 0)
			{
				error = "The Antigravity CLI (agy) could not be started.";
			}
		}
		return (url, error);
	}

	// Hands the waiting sign-in its authorization code (or the whole URL the browser landed on) and
	// returns the refresh token the CLI obtained with it.
	public static async Task<(string RefreshToken, string Error)> CompleteLoginAsync(string code, CancellationToken ct)
	{
		string        refreshToken = string.Empty;
		string        error;
		PendingLogin? login;
		lock (_loginLock)
		{
			login  = _login;
			_login = null;
		}

		if (login == null)
		{
			error = "The Google sign-in is no longer waiting for a code; start it again.";
		}
		else
		{
			login.Expiry.Dispose();
			string output                       = string.Empty;
			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
			limit.CancelAfter(kLoginExchange);
			try
			{
				await login.Process.StandardInput.WriteLineAsync(code.Trim().AsMemory(), limit.Token);
				login.Process.StandardInput.Close();
				output = await login.Process.StandardOutput.ReadToEndAsync(limit.Token);
				await login.Process.WaitForExitAsync(limit.Token);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				output = "the sign-in did not finish in time";
			}

			string tokenPath = Path.Combine(login.StateDir, "antigravity-cli", kTokenFile);
			if (login.Process.HasExited && login.Process.ExitCode == 0 && File.Exists(tokenPath))
			{
				refreshToken = ReadRefreshToken(await File.ReadAllTextAsync(tokenPath, ct));
			}

			error = refreshToken.Length > 0 ? string.Empty : $"Google sign-in failed: {LastLine(output)}";
			EndLogin(login.Process, login.StateDir);
		}
		return (refreshToken, error);
	}

	internal static string ReadRefreshToken(string tokenFile)
	{
		string token = string.Empty;
		try
		{
			token = JsonNode.Parse(tokenFile)?["token"]?["refresh_token"]?.ToString() ?? string.Empty;
		}
		catch (JsonException)
		{
		}
		return token;
	}

	private static void AbandonLogin()
	{
		PendingLogin? login;
		lock (_loginLock)
		{
			login  = _login;
			_login = null;
		}
		if (login != null)
		{
			login.Expiry.Dispose();
			EndLogin(login.Process, login.StateDir);
		}
	}

	private static void EndLogin(Process process, string stateDir)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(true);
			}
		}
		catch (Exception)
		{
		}
		process.Dispose();
		DeleteStateDir(stateDir);
	}

	private static string LastLine(string text)
	{
		string[] lines = text.Trim().Split('\n');
		return lines[lines.Length - 1].Trim();
	}
}