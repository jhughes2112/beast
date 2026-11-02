using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

// Loads and manages beast settings from the user profile: ~/.beast/settings.json is the one true
// file. There is deliberately no project-level settings file — endpoints, models, and web search
// are per-person configuration that every checkout and worktree should share, and a second copy
// only ever shadowed the first (a project 'auto' section silently overrode the one /config had
// just written, which read as saves doing nothing).
public class SettingsService
{
	private readonly string _homeDirSettingsPath;

	// Every write is a read-modify-write of the whole file. Two at once would each drop the other's
	// change — and a dropped change can be a rotated OAuth refresh token, which signs the user out.
	private readonly object _writeGate = new object();

	// Tool descriptions live in their own regenerable file (see ToolService), never in settings.json.
	private readonly ToolService _toolService = new ToolService();

	public BeastSettings Settings { get; private set; }

	public Dictionary<string, ToolConfig> Tools => _toolService.Tools;

	public SettingsService()
	{
		_homeDirSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast", "settings.json");

		Settings = null!;
		LoadSettings();
	}

	// Reload rollback support: settings are published by reference, so the orchestrator snapshots
	// them before a /reload and restores them if a later stage of the reload fails.
	public BeastSettings SnapshotSettings() => Settings;

	public void RestoreSettings(BeastSettings settings) => Settings = settings;

	public void LoadSettings()
	{
		// Parse fully before touching Settings: a malformed file throws out of LoadSettingsFromFile,
		// and the previous configuration must stay in effect rather than a half-swapped state. Anything
		// the file leaves out keeps BeastSettings' defaults; anything it has that is no longer a setting is ignored.
		BeastSettings? home = LoadSettingsFromFile(_homeDirSettingsPath);
		if (home == null)
		{
			home = CreateDefaultHomeSettings();
			WriteSettings(_homeDirSettingsPath, home);
		}

		_toolService.Reload();

		// Published with a single reference assignment: a concurrent reader never sees a
		// half-populated settings object.
		Settings = home;
	}

	private BeastSettings? LoadSettingsFromFile(string path)
	{
		if (!File.Exists(path))
			return null;

		try
		{
			string json = File.ReadAllText(path);
			if (string.IsNullOrWhiteSpace(json))
				return null;

			BeastSettings? result = JsonSerializer.Deserialize(json, BeastJson.Config.BeastSettings);
			return result; // may be null if JSON was empty or only null
		}
		catch (JsonException ex)
		{
			string location = ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue
				? $" (line {ex.LineNumber + 1}, column {ex.BytePositionInLine + 1})"
				: "";
			string detail = $"settings.json parse error at {path}{location}: {ex}";

			Console.Error.WriteLine($"ERROR: Failed to parse {detail}");
			Console.Error.WriteLine("Fix it, or delete the file to use defaults.");
			throw new ConfigException(detail);
		}
		catch (Exception ex)
		{
			string detail = $"settings.json load error at {path}: {ex}";

			Console.Error.WriteLine($"ERROR: Failed to load {detail}");
			Console.Error.WriteLine("Fix it, or delete the file to use defaults.");
			throw new ConfigException(detail);
		}
	}

	// Persists the auto section as edited by /config: replaces (or adds) the entry for one
	// endpoint in the USER settings file — model/endpoint configuration is user-level, set up
	// once and serving every project and worktree (Docker runs mount ~/.beast at /root/.beast,
	// so the home file is the same one true file either way). Only an explicit remove drops the
	// endpoint; one whose model list is emptied keeps its entry and key. Configuring an OpenRouter
	// key also seeds the web-search block, so internet_search works without a second setup step.
	public void SaveAutoEndpoint(string baseUrl, string apiKey, List<AutoModelConfig> models, bool remove)
	{
		lock (_writeGate)
		{
			SaveAutoEndpointLocked(baseUrl, apiKey, models, remove);
		}
	}

	private void SaveAutoEndpointLocked(string baseUrl, string apiKey, List<AutoModelConfig> models, bool remove)
	{
		BeastSettings home = LoadSettingsFromFile(_homeDirSettingsPath) ?? CreateDefaultHomeSettings();

		int existing = -1;
		for (int i = 0; i < home.Auto.Count; i++)
		{
			if (string.Equals(home.Auto[i].BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
			{
				existing = i;
				break;
			}
		}

		if (remove)
		{
			if (existing >= 0)
				home.Auto.RemoveAt(existing);
		}
		else
		{
			AutoProviderConfig entry = new AutoProviderConfig { BaseUrl = baseUrl, ApiKey = apiKey, Models = models };

			// An apply that did not re-enter the key keeps the one already on disk — and the access
			// token minted from it, which belongs to that key and to no other.
			if (string.IsNullOrEmpty(entry.ApiKey) && existing >= 0)
			{
				entry.ApiKey      = home.Auto[existing].ApiKey;
				entry.AccessToken = home.Auto[existing].AccessToken;
			}

			if (existing >= 0)
				home.Auto[existing] = entry;
			else
				home.Auto.Add(entry);
		}

		WriteSettings(_homeDirSettingsPath, home);
		LoadSettings();
	}

	// The stored refresh token (apiKey) and access token of the auto endpoint the predicate picks,
	// read from disk rather than memory: another agent sharing this file may have rotated them since
	// it was loaded. Empty strings when there is no such endpoint.
	public (string ApiKey, string AccessToken) ReadAutoEndpointTokens(Func<string, bool> isEndpoint)
	{
		string apiKey = string.Empty;
		string access = string.Empty;
		lock (_writeGate)
		{
			BeastSettings? home = LoadSettingsFromFile(_homeDirSettingsPath);
			if (home != null)
			{
				foreach (AutoProviderConfig auto in home.Auto)
				{
					if (isEndpoint(auto.BaseUrl))
					{
						apiKey = auto.ApiKey;
						access = auto.AccessToken ?? string.Empty;
						break;
					}
				}
			}
		}
		return (apiKey, access);
	}

	// Persists a rotated token pair for an OAuth endpoint, creating its entry (with no models yet)
	// when a fresh sign-in arrives before /config has saved the endpoint.
	public void SaveAutoEndpointTokens(string baseUrl, Func<string, bool> isEndpoint, string apiKey, string accessToken)
	{
		lock (_writeGate)
		{
			BeastSettings       home  = LoadSettingsFromFile(_homeDirSettingsPath) ?? CreateDefaultHomeSettings();
			AutoProviderConfig? entry = null;
			foreach (AutoProviderConfig auto in home.Auto)
			{
				if (isEndpoint(auto.BaseUrl))
				{
					entry = auto;
					break;
				}
			}
			if (entry == null)
			{
				entry = new AutoProviderConfig { BaseUrl = baseUrl };
				home.Auto.Add(entry);
			}
			entry.ApiKey      = apiKey;
			entry.AccessToken = accessToken;

			WriteSettings(_homeDirSettingsPath, home);
			LoadSettings();
		}
	}

	// Persists a durable reasoning fact about one model — the effort word chosen with /effort, or a
	// capability the provider taught us mid-run (an endpoint that refuses thinking summaries, a model
	// that refuses to reason at all). Only the arguments supplied are written; null leaves that field
	// as it is. The model is looked up in both tiers, since it may be configured either way.
	// Returns false when no entry exists to write to: the fact still holds for this run (the registry
	// applies it in memory), it simply has nowhere on disk to live.
	public bool SaveModelReasoning(string modelId, string? effort, bool? summaries)
	{
		bool found = false;
		lock (_writeGate)
		{
			BeastSettings home = LoadSettingsFromFile(_homeDirSettingsPath) ?? CreateDefaultHomeSettings();

			foreach (AutoProviderConfig auto in home.Auto)
			{
				foreach (AutoModelConfig model in auto.Models)
				{
					if (!string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
						continue;
					if (effort != null)
						model.ReasoningEffort = effort;
					if (summaries.HasValue)
						model.ReasoningSummaries = summaries.Value;
					found = true;
				}
			}

			foreach (ProviderConfig provider in home.Providers)
			{
				foreach (ModelConfig model in provider.Models)
				{
					if (!string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
						continue;
					if (effort != null)
						model.ReasoningEffort = effort;
					if (summaries.HasValue)
						model.ReasoningSummaries = summaries.Value;
					found = true;
				}
			}

			if (found)
			{
				WriteSettings(_homeDirSettingsPath, home);
				LoadSettings();
			}
		}
		return found;
	}

	// Persists the image cap a provider stated when it refused a request for carrying too many. Same
	// lookup and same "false = nowhere on disk to put it" contract as SaveModelReasoning.
	public bool SaveModelMaxImages(string modelId, int maxImages)
	{
		bool found = false;
		lock (_writeGate)
		{
			BeastSettings home = LoadSettingsFromFile(_homeDirSettingsPath) ?? CreateDefaultHomeSettings();

			foreach (AutoProviderConfig auto in home.Auto)
			{
				foreach (AutoModelConfig model in auto.Models)
				{
					if (!string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
						continue;
					model.MaxImages = maxImages;
					found           = true;
				}
			}

			foreach (ProviderConfig provider in home.Providers)
			{
				foreach (ModelConfig model in provider.Models)
				{
					if (!string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
						continue;
					model.MaxImages = maxImages;
					found           = true;
				}
			}

			if (found)
			{
				WriteSettings(_homeDirSettingsPath, home);
				LoadSettings();
			}
		}
		return found;
	}

	// Persists the /config web-search provider selection into the USER settings file, alongside the
	// endpoints — one setup, every project. No API keys are written: each provider resolves its key
	// at load time from the endpoint sharing its domain.
	public void SaveWebSearchProviders(List<WebSearchProviderConfig> providers)
	{
		lock (_writeGate)
		{
			BeastSettings home = LoadSettingsFromFile(_homeDirSettingsPath) ?? CreateDefaultHomeSettings();

			if (home.WebSearch == null)
				home.WebSearch = new WebSearchConfig();
			home.WebSearch.Providers = providers;

			// The legacy single-provider block is superseded the moment the new list is written; leave
			// its key in place (it is still a valid OpenRouter key source) but stop it from acting as
			// an implicit second OpenRouter entry.
			if (home.WebSearch.Openrouter != null)
				home.WebSearch.Openrouter.Enabled = false;

			WriteSettings(_homeDirSettingsPath, home);
			LoadSettings();
		}
	}

	// Moves one auto endpoint up (delta < 0) or down (delta > 0) one place in the user settings file. A move
	// past either end, or an endpoint that is not there, leaves the file as it is.
	public void MoveAutoEndpoint(string baseUrl, int delta)
	{
		lock (_writeGate)
		{
			BeastSettings home = LoadSettingsFromFile(_homeDirSettingsPath) ?? CreateDefaultHomeSettings();
			int           from = -1;
			for (int i = 0; i < home.Auto.Count; i++)
			{
				if (from < 0 && string.Equals(home.Auto[i].BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
					from = i;
			}
			int to = from + Math.Sign(delta);
			if (from >= 0 && to >= 0 && to < home.Auto.Count)
			{
				AutoProviderConfig moved = home.Auto[from];
				home.Auto.RemoveAt(from);
				home.Auto.Insert(to, moved);
				WriteSettings(_homeDirSettingsPath, home);
				LoadSettings();
			}
		}
	}

	// Persists what HallMonitor does with an Unsure host command ("ask", "beSafe" or "beUnsafe").
	public void SaveHallMonitorUnsure(string value)
	{
		lock (_writeGate)
		{
			BeastSettings home     = LoadSettingsFromFile(_homeDirSettingsPath) ?? CreateDefaultHomeSettings();
			home.HallMonitorUnsure = value;
			WriteSettings(_homeDirSettingsPath, home);
			LoadSettings();
		}
	}

	private void WriteSettings(string path, BeastSettings settings)
	{
		try
		{
			string json = JsonSerializer.Serialize(settings, BeastJson.Persist.BeastSettings);

			string? dir = Path.GetDirectoryName(path);
			if (dir != null)
			{
				Directory.CreateDirectory(dir);
			}

			File.WriteAllText(path, json);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"WARNING: Failed to write settings.json at {path}: {ex}");
		}
	}

	// A fresh settings file: BeastSettings' defaults, plus an empty web-search block. No example providers:
	// /config is the onboarding path (endpoint presets, catalog discovery, spacebar enablement), and each
	// web-search provider borrows its API key from the endpoint sharing its domain, so there is nothing to pre-fill.
	private static BeastSettings CreateDefaultHomeSettings()
	{
		return new BeastSettings { WebSearch = new WebSearchConfig() };
	}
}