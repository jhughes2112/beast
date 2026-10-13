using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// The OpenAI subscription endpoint: a ChatGPT plan (Plus/Pro/Business) signed in the way the Codex
// CLI signs in, then served from the ChatGPT backend's Responses route. Unlike the Anthropic
// subscription there is a real HTTP endpoint, so ProtocolResponses speaks to it directly in its
// stateless mode (see ProtocolResponses.Subscription).
//
// Sign-in is the device-code flow: /config shows a URL and a one-time code, the user approves it in
// any browser, and the tokens land in settings.json with every other key — the refresh token as the
// endpoint's apiKey, the access token minted from it beside it. Beast keeps its OWN sign-in rather
// than borrowing ~/.codex/auth.json: refresh tokens are single-use and rotate, so two apps sharing
// one would sign each other out.
public static class ChatGptSubscription
{
	public const string EndpointUrl = "https://chatgpt.com/backend-api/codex/responses";

	private const string kModelsUrl     = "https://chatgpt.com/backend-api/codex/models";
	private const string kIssuer        = "https://auth.openai.com";
	private const string kClientId      = "app_EMoamEEZ73f0CkXaXp7hrann";
	private const string kDeviceUrl     = "https://auth.openai.com/codex/device";
	private const string kRedirectUri   = "https://auth.openai.com/deviceauth/callback";
	private const string kAuthClaim     = "https://api.openai.com/auth";
	private const string kClientVersion = "99.0.0";

	// Third-party harnesses identify themselves rather than posing as the Codex CLI.
	private const string kOriginator = "beast";
	private const string kUserAgent  = "Beast/1.0";

	// Model ids carry this prefix so they never collide with the same model on the API endpoint —
	// comparing the two side by side is the point of having both.
	private const string kModelPrefix = "chatgpt/";

	// The ChatGPT backend's window for most models; a catalog entry that states its own wins.
	private const int kContextWindow = 272000;

	// An access token this close to expiry is refreshed before use rather than sent to fail.
	private static readonly TimeSpan kRefreshSkew    = TimeSpan.FromMinutes(2);
	private static readonly TimeSpan kDeviceTimeout  = TimeSpan.FromMinutes(15);
	private static readonly TimeSpan kLockTimeout    = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan kRequestTimeout = TimeSpan.FromSeconds(30);

	// One refresh at a time in this process; the lock file extends that to other agents sharing ~/.beast.
	private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
	private static readonly HttpClient    _http = new HttpClient { Timeout = kRequestTimeout };

	private static string LockPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast", "chatgpt-refresh.lock");

	// Where the tokens live. Set once at startup, before anything can send a request.
	private static SettingsService? _settings;

	public static void Initialize(SettingsService settings)
	{
		_settings = settings;
	}

	private const string kNotSignedIn = "Not signed in to a ChatGPT subscription: open /config and choose OpenAI Subscription.";

	// The usable credentials for one request. Error is empty on success; NeedsLogin says only a new
	// sign-in can fix it (as opposed to a network blip worth retrying).
	public record Credentials(string AccessToken, string AccountId, string Residency, string Error, bool NeedsLogin);

	public static bool IsEndpoint(string url)
	{
		return url.StartsWith("https://chatgpt.com/backend-api/codex", StringComparison.OrdinalIgnoreCase);
	}

	// The slug the backend takes: the model id without the prefix that keeps it apart from API ids.
	public static string ApiModel(string modelId)
	{
		return modelId.StartsWith(kModelPrefix, StringComparison.OrdinalIgnoreCase) ? modelId.Substring(kModelPrefix.Length) : modelId;
	}

	// Signs the request: bearer token, the account it bills, and who is calling. sessionKey ties the
	// requests of one conversation together for the backend's prompt cache. forceRefresh is the retry
	// after a 401, when the token was rejected before its stated expiry.
	public static async Task<Credentials> ApplyHeadersAsync(HttpRequestMessage request, string sessionKey, bool forceRefresh, CancellationToken ct)
	{
		Credentials creds = await GetCredentialsAsync(forceRefresh, ct);
		if (creds.Error.Length == 0)
		{
			request.Headers.TryAddWithoutValidation("Authorization",      $"Bearer {creds.AccessToken}");
			request.Headers.TryAddWithoutValidation("ChatGPT-Account-ID",               creds.AccountId);
			request.Headers.TryAddWithoutValidation("originator",                           kOriginator);
			request.Headers.TryAddWithoutValidation("User-Agent",                            kUserAgent);
			request.Headers.TryAddWithoutValidation("session-id",                            sessionKey);
			if (creds.Residency.Length > 0)
				request.Headers.TryAddWithoutValidation("x-openai-internal-codex-residency", creds.Residency);
		}
		return creds;
	}

	// Ensures there is a working sign-in, running the device-code flow when there is none (or the
	// stored one can no longer refresh). status receives the URL and code the user must act on.
	// Returns empty on success, else the reason.
	public static async Task<string> EnsureLoginAsync(Action<string> status, CancellationToken ct)
	{
		Credentials existing = await GetCredentialsAsync(false, ct);
		string      error    = existing.Error;
		if (existing.Error.Length > 0 && existing.NeedsLogin)
			error = await DeviceLoginAsync(status, ct);
		return error;
	}

	// The models this subscription offers, as the backend's own catalog lists them for this account.
	public static async Task<(List<DiscoveredModel> Models, string Error)> FetchCatalogAsync(CancellationToken ct)
	{
		List<DiscoveredModel> models = new List<DiscoveredModel>();
		string                error;

		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"{kModelsUrl}?client_version={kClientVersion}");
		Credentials creds                = await ApplyHeadersAsync(request, Guid.NewGuid().ToString(), false, ct);
		if (creds.Error.Length > 0)
		{
			error = creds.Error;
		}
		else
		{
			try
			{
				using HttpResponseMessage response = await _http.SendAsync(request, ct);
				string body                        = await response.Content.ReadAsStringAsync(ct);
				if (response.IsSuccessStatusCode)
				{
					models = ParseCatalog(body);
					error  = models.Count > 0 ? string.Empty : "The subscription listed no models.";
				}
				else
				{
					error = $"Catalog fetch failed (HTTP {(int)response.StatusCode}): {Clip(body)}";
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				error = $"Catalog fetch failed: {ex.Message}";
			}
		}
		return (models, error);
	}

	// Reads the backend's {"models":[{slug, display_name, context_window, visibility, ...}]}.
	// Hidden entries are internal or retired; the picker lists only what the account is offered.
	internal static List<DiscoveredModel> ParseCatalog(string body)
	{
		List<DiscoveredModel> models = new List<DiscoveredModel>();
		JsonArray?            listed = null;
		try
		{
			listed = JsonNode.Parse(body)?["models"] as JsonArray;
		}
		catch (JsonException)
		{
		}

		if (listed != null)
		{
			foreach (JsonNode? entry in listed)
			{
				string slug = ReadString(entry, "slug");
				if (slug.Length == 0 || ReadString(entry, "visibility") == "hide")
					continue;

				string name   = ReadString(entry, "display_name");
				int    window = ReadInt(entry, "context_window");
				models.Add(new DiscoveredModel
				{
					Id            = $"{kModelPrefix}{slug}",
					Name          = $"{(name.Length > 0 ? name : slug)} (subscription)",
					ContextWindow = window > 0 ? window : kContextWindow,
					CostInput     = 0m,
					CostOutput    = 0m,
					CostCacheRead = 0m,
					Modalities    = new List<string> { "text", "image" }
				});
			}
		}
		return models;
	}

	// ---- Usage limits ----

	// Spells out a refused request's usage: the plan, how much of each window is used and when it
	// resets, and whether credits remain. The plan's allowance is shared with Codex and ChatGPT, so
	// the limit can be spent before Beast sends anything. Without the x-codex-* headers (a limit
	// reported inside the stream) the body's plan and reset time are all there is to go on.
	internal static string DescribeLimit(HttpResponseHeaders headers, string errorBody, DateTimeOffset now)
	{
		JsonNode? error = ParseObject(errorBody)?["error"];
		string    plan  = FirstNonEmpty(Header(headers, "x-codex-plan-type"), ReadString(error, "plan_type"));

		StringBuilder text      = new StringBuilder($"ChatGPT {(plan.Length > 0 ? $"{plan} " : "")}usage limit reached (the allowance is shared with Codex and ChatGPT).");
		string        primary   = DescribeWindow(headers, "primary",   now);
		string        secondary = DescribeWindow(headers, "secondary", now);
		if (primary.Length > 0)
			text.Append($" {primary}.");
		if (secondary.Length > 0)
			text.Append($" {secondary}.");

		DateTimeOffset? reset = ProtocolHelpers.ResetTimeFromBody(errorBody);
		if (primary.Length == 0 && secondary.Length == 0 && reset.HasValue)
			text.Append($" Resets {ProtocolHelpers.DescribeReset(reset.Value, now)}.");

		if (Header(headers, "x-codex-credits-unlimited") == "True")
			text.Append(" Credits: unlimited.");
		else if (Header(headers, "x-codex-credits-has-credits") == "True")
			text.Append($" Credits balance: {Header(headers, "x-codex-credits-balance")}.");
		else if (Header(headers, "x-codex-credits-has-credits") == "False")
			text.Append(" No credits to draw on.");
		return text.ToString();
	}

	// One window's usage and reset, or empty when the headers say nothing about this window.
	private static string DescribeWindow(HttpResponseHeaders headers, string window, DateTimeOffset now)
	{
		string description = string.Empty;
		if (double.TryParse(Header(headers, $"x-codex-{window}-used-percent"), NumberStyles.Float, CultureInfo.InvariantCulture, out double used))
		{
			int.TryParse(Header(headers, $"x-codex-{window}-window-minutes"), out int minutes);
			DateTimeOffset? reset = long.TryParse(Header(headers, $"x-codex-{window}-reset-at"), out long epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;
			description           = ProtocolHelpers.DescribeUsageWindow(WindowName(minutes), used, reset, now);
		}
		return description;
	}

	private static string WindowName(int minutes)
	{
		string name;
		if (minutes == 10080)
			name = "Weekly";
		else if (minutes > 0 && minutes % 1440 == 0)
			name = $"{minutes / 1440}-day";
		else if (minutes > 0 && minutes % 60 == 0)
			name = $"{minutes / 60}-hour";
		else if (minutes > 0)
			name = $"{minutes}-minute";
		else
			name = "Usage";
		return name;
	}

	private static string Header(HttpResponseHeaders headers, string name)
	{
		string value = string.Empty;
		if (headers.TryGetValues(name, out IEnumerable<string>? values))
		{
			foreach (string v in values)
			{
				value = v;
				break;
			}
		}
		return value;
	}

	// ---- Credentials ----

	// Returns a usable access token: the one in settings while it is good, else a refreshed one. A
	// sign-in this process has not loaded yet (made by another agent) is looked for on disk.
	private static async Task<Credentials> GetCredentialsAsync(bool forceRefresh, CancellationToken ct)
	{
		Credentials result;
		await _gate.WaitAsync(ct);
		try
		{
			(string refresh, string access) = LoadedTokens();
			if (refresh.Length == 0)
				(refresh, access) = _settings!.ReadAutoEndpointTokens(IsEndpoint);

			if (refresh.Length == 0)
				result = Failure(kNotSignedIn, true);
			else if (!forceRefresh && !IsExpiring(access))
				result = FromAccessToken(access);
			else
				result = await RefreshLockedAsync(access, ct);
		}
		finally
		{
			_gate.Release();
		}
		return result;
	}

	// Refreshes under the cross-process lock. Inside it the tokens are read from disk again: when
	// another agent already refreshed, the refresh token this process loaded is spent — sending it
	// would revoke the sign-in — so the access token that agent minted is adopted instead.
	private static async Task<Credentials> RefreshLockedAsync(string heldAccessToken, CancellationToken ct)
	{
		Credentials result;
		using FileStream? fileLock = await AcquireLockAsync(ct);
		if (fileLock == null)
		{
			result = Failure("Timed out waiting for another agent to refresh the ChatGPT sign-in.", false);
		}
		else
		{
			(string refresh, string access) = _settings!.ReadAutoEndpointTokens(IsEndpoint);
			if (access != heldAccessToken && !IsExpiring(access))
			{
				_settings.LoadSettings();
				result = FromAccessToken(access);
			}
			else if (refresh.Length == 0)
			{
				result = Failure(kNotSignedIn, true);
			}
			else
			{
				result = await RefreshAsync(refresh, ct);
			}
		}
		return result;
	}

	// The tokens as this process last loaded settings.json (its own saves reload it).
	private static (string RefreshToken, string AccessToken) LoadedTokens()
	{
		string refresh = string.Empty;
		string access  = string.Empty;
		foreach (AutoProviderConfig auto in _settings!.Settings.Auto)
		{
			if (IsEndpoint(auto.BaseUrl))
			{
				refresh = auto.ApiKey;
				access  = auto.AccessToken ?? string.Empty;
				break;
			}
		}
		return (refresh, access);
	}

	private static async Task<Credentials> RefreshAsync(string refreshToken, CancellationToken ct)
	{
		Credentials result;
		// The Codex CLI refreshes with a JSON body (the code exchange below is form-encoded).
		JsonObject request = new JsonObject
		{
			["client_id"]     = kClientId,
			["grant_type"]    = "refresh_token",
			["refresh_token"] = refreshToken
		};
		(int status, string body, string transportError) = await PostJsonAsync($"{kIssuer}/oauth/token", request, ct);

		JsonObject? reply = ParseObject(body);
		string      token = reply != null ? ReadString(reply, "access_token") : string.Empty;
		if (transportError.Length > 0)
		{
			result = Failure($"Could not reach the ChatGPT sign-in service: {transportError}", false);
		}
		else if (status >= 200 && status < 300 && token.Length > 0)
		{
			// Rotation: a reply without a new refresh token leaves the old one valid.
			string rotated = ReadString(reply!, "refresh_token");
			result         = Save(token, rotated.Length > 0 ? rotated : refreshToken);
		}
		else if (status == 429 || status >= 500)
		{
			result = Failure($"The ChatGPT sign-in service is unavailable (HTTP {status}); retrying later.", false);
		}
		else
		{
			// invalid_grant, refresh_token_reused, a 401/403: the sign-in is gone and only a person can renew it.
			result = Failure($"The ChatGPT sign-in has expired or was revoked (HTTP {status}: {Clip(body)}). Open /config and choose OpenAI Subscription to sign in again.", true);
		}
		return result;
	}

	// ---- Device-code sign-in ----

	private static async Task<string> DeviceLoginAsync(Action<string> status, CancellationToken ct)
	{
		string error;
		(int codeStatus, string codeBody, string codeTransport) = await PostJsonAsync($"{kIssuer}/api/accounts/deviceauth/usercode",
			new JsonObject { ["client_id"] = kClientId }, ct);

		JsonObject? code         = ParseObject(codeBody);
		string      deviceAuthId = code != null ? ReadString(code, "device_auth_id") : string.Empty;
		string      userCode     = code != null ? FirstNonEmpty(ReadString(code, "user_code"), ReadString(code, "usercode")) : string.Empty;
		if (codeTransport.Length > 0 || codeStatus < 200 || codeStatus >= 300 || deviceAuthId.Length == 0 || userCode.Length == 0)
		{
			error = $"Could not start the ChatGPT sign-in: {(codeTransport.Length > 0 ? codeTransport : $"HTTP {codeStatus}: {Clip(codeBody)}")}";
		}
		else
		{
			int interval = Math.Max(3, ReadInt(code, "interval"));
			// Code first, so a narrow screen that truncates the line still shows it.
			status($"Code {userCode} — enter it at {kDeviceUrl} (expires in 15 min)");
			error = await PollDeviceAsync(deviceAuthId, userCode, interval, ct);
		}
		return error;
	}

	// Polls until the user approves the code. 403/404 mean "not yet"; approval returns the
	// authorization code and the server-generated PKCE verifier to exchange for tokens.
	private static async Task<string> PollDeviceAsync(string deviceAuthId, string userCode, int intervalSeconds, CancellationToken ct)
	{
		string         error    = "The ChatGPT sign-in code expired before it was approved.";
		DateTimeOffset deadline = DateTimeOffset.UtcNow + kDeviceTimeout;
		bool           done     = false;
		while (!done && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
			(int status, string body, string transport) = await PostJsonAsync($"{kIssuer}/api/accounts/deviceauth/token",
				new JsonObject { ["device_auth_id"] = deviceAuthId, ["user_code"] = userCode }, ct);

			if (transport.Length > 0 || status == 403 || status == 404)
				continue;

			done                 = true;
			JsonObject? approved = ParseObject(body);
			if (status >= 200 && status < 300 && approved != null)
				error = await ExchangeCodeAsync(ReadString(approved, "authorization_code"), ReadString(approved, "code_verifier"), ct);
			else
				error = $"The ChatGPT sign-in was refused (HTTP {status}): {Clip(body)}";
		}
		return error;
	}

	private static async Task<string> ExchangeCodeAsync(string authorizationCode, string codeVerifier, CancellationToken ct)
	{
		string                     error;
		Dictionary<string, string> form = new Dictionary<string, string>
		{
			["grant_type"]    = "authorization_code",
			["code"]          = authorizationCode,
			["redirect_uri"]  = kRedirectUri,
			["client_id"]     = kClientId,
			["code_verifier"] = codeVerifier
		};
		(int status, string body, string transport) = await PostFormAsync($"{kIssuer}/oauth/token", form, ct);

		JsonObject? tokens  = ParseObject(body);
		string      access  = tokens != null ? ReadString(tokens, "access_token") : string.Empty;
		string      refresh = tokens != null ? ReadString(tokens, "refresh_token") : string.Empty;
		if (transport.Length == 0 && status >= 200 && status < 300 && access.Length > 0 && refresh.Length > 0)
		{
			await _gate.WaitAsync(ct);
			try
			{
				using FileStream? fileLock = await AcquireLockAsync(ct);
				error                      = Save(access, refresh).Error;
			}
			finally
			{
				_gate.Release();
			}
		}
		else
		{
			error = $"The ChatGPT sign-in could not be completed: {(transport.Length > 0 ? transport : $"HTTP {status}: {Clip(body)}")}";
		}
		return error;
	}

	// ---- Store ----

	// Writes the pair into the endpoint's settings.json entry (creating it on a first sign-in), as
	// soon as it exists: a rotated refresh token that is not saved is the only valid one lost.
	private static Credentials Save(string accessToken, string refreshToken)
	{
		Credentials result = FromAccessToken(accessToken);
		if (result.Error.Length == 0)
			_settings!.SaveAutoEndpointTokens(EndpointUrl, IsEndpoint, refreshToken, accessToken);
		return result;
	}

	// The account to bill and its data residency ride in the access token's auth claim.
	private static Credentials FromAccessToken(string access)
	{
		JsonNode? auth    = JwtPayload(access)?[kAuthClaim];
		string    account = ReadString(auth, "chatgpt_account_id");
		string    region  = FirstNonEmpty(ReadString(auth, "chatgpt_data_residency"), ReadString(auth, "chatgpt_compute_residency"));
		return account.Length > 0
			? new Credentials(access, account, region, string.Empty, false)
			: Failure("The ChatGPT sign-in carries no account id; sign in again from /config.", true);
	}

	private static Credentials Failure(string error, bool needsLogin)
	{
		return new Credentials(string.Empty, string.Empty, string.Empty, error, needsLogin);
	}

	private static bool IsExpiring(string accessToken)
	{
		JsonObject? payload = JwtPayload(accessToken);
		long        exp     = 0;
		if (payload?["exp"] is JsonValue value)
			value.TryGetValue(out exp);
		return DateTimeOffset.FromUnixTimeSeconds(exp) - kRefreshSkew <= DateTimeOffset.UtcNow;
	}

	// The token's claims, decoded without verification — the backend verifies it; this only reads it.
	private static JsonObject? JwtPayload(string jwt)
	{
		JsonObject? payload = null;
		string[]    parts   = jwt.Split('.');
		if (parts.Length == 3)
		{
			string segment = parts[1].Replace('-', '+').Replace('_', '/');
			segment        = segment.PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=');
			try
			{
				payload = ParseObject(Encoding.UTF8.GetString(Convert.FromBase64String(segment)));
			}
			catch (FormatException)
			{
			}
		}
		return payload;
	}

	// Holds the cross-process lock file open exclusively; null when it could not be had in time.
	private static async Task<FileStream?> AcquireLockAsync(CancellationToken ct)
	{
		FileStream?    held     = null;
		DateTimeOffset deadline = DateTimeOffset.UtcNow + kLockTimeout;
		Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
		while (held == null && DateTimeOffset.UtcNow < deadline)
		{
			try
			{
				held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException)
			{
				await Task.Delay(250, ct);
			}
		}
		return held;
	}

	// ---- HTTP ----

	private static async Task<(int Status, string Body, string TransportError)> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
		request.Content                  = new FormUrlEncodedContent(form);
		return await SendAsync(request, ct);
	}

	private static async Task<(int Status, string Body, string TransportError)> PostJsonAsync(string url, JsonObject body, CancellationToken ct)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
		request.Content                  = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		return await SendAsync(request, ct);
	}

	private static async Task<(int Status, string Body, string TransportError)> SendAsync(HttpRequestMessage request, CancellationToken ct)
	{
		int    status    = 0;
		string body      = string.Empty;
		string transport = string.Empty;
		request.Headers.TryAddWithoutValidation("Accept",     "application/json");
		request.Headers.TryAddWithoutValidation("User-Agent",         kUserAgent);
		try
		{
			using HttpResponseMessage response = await _http.SendAsync(request, ct);
			status = (int)response.StatusCode;
			body   = await response.Content.ReadAsStringAsync(ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			transport = ex.Message;
		}
		return (status, body, transport);
	}

	// ---- JSON ----

	private static JsonObject? ParseObject(string text)
	{
		JsonObject? parsed = null;
		try
		{
			parsed = JsonNode.Parse(text) as JsonObject;
		}
		catch (JsonException)
		{
		}
		return parsed;
	}

	private static string ReadString(JsonNode? node, string key)
	{
		string value = string.Empty;
		if (node is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue(out string? text) && text != null)
			value = text;
		return value;
	}

	// Accepts a number or a numeric string ("interval" arrives as the latter).
	private static int ReadInt(JsonNode? node, string key)
	{
		int value = 0;
		if (node is JsonObject obj && obj[key] is JsonValue v && !v.TryGetValue(out value) && v.TryGetValue(out string? text))
			int.TryParse(text, out value);
		return value;
	}

	private static string FirstNonEmpty(string first, string second)
	{
		return first.Length > 0 ? first : second;
	}

	private static string Clip(string text)
	{
		string trimmed = text.Trim();
		return trimmed.Length > 300 ? $"{trimmed.Substring(0, 300)}…" : trimmed;
	}
}