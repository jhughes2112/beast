using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

// Loads and manages beast settings from multiple locations with priority.
// Priority: user profile (homeDir) as base, workDir (local project) as overrides
public class SettingsService
{
	private readonly string _workDirSettingsPath;
	private readonly string _homeDirSettingsPath;

	// The merged settings (user + local overrides)
	public BeastSettings Settings { get; private set; }

	public SettingsService(string workDir)
	{
		_workDirSettingsPath = Path.Combine(workDir, ".beast", "settings.json");
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
		// Load BOTH files before touching Settings: a malformed file throws out of
		// LoadSettingsFromFile, and the previous merged configuration must stay in effect rather
		// than a half-swapped state (home settings applied, project overrides lost).

		// 1. Load user profile settings first (base settings)
		BeastSettings? home = LoadSettingsFromFile(_homeDirSettingsPath);
		if (home == null)
		{
			home = CreateDefaultHomeSettings();
			WriteSettings(_homeDirSettingsPath, home);
		}

		// 2. Load local project settings (overrides)
		BeastSettings? localSettings = LoadSettingsFromFile(_workDirSettingsPath);
		if (localSettings == null)
		{
			localSettings = CreateDefaultProjectSettings();
			WriteSettings(_workDirSettingsPath, localSettings);
		}

		// 3. Both parsed: merge local overrides into the base, THEN publish with a single reference
		// assignment — a concurrent reader never sees a half-merged settings object.
		MergeSettings(home, localSettings);
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

	private static void MergeSettings(BeastSettings target, BeastSettings local)
	{
		// Apply local overrides to the target object, which is pre-loaded with user settings.
		if (!string.IsNullOrWhiteSpace(local.IdleSoundFile))
			target.IdleSoundFile = local.IdleSoundFile;

		if (!string.IsNullOrWhiteSpace(local.SubagentSoundFile))
			target.SubagentSoundFile = local.SubagentSoundFile;

		// If the local settings file defines any providers, it replaces the entire list.
		if (local.Providers != null && local.Providers.Count > 0)
			target.Providers = local.Providers;

		// Same wholesale-replacement rule for the auto-configured endpoints.
		if (local.Auto != null && local.Auto.Count > 0)
			target.Auto = local.Auto;

		// If the local settings file defines web search, it replaces the existing config.
		if (local.WebSearch != null)
			target.WebSearch = local.WebSearch;

		if (local.CompactionReserveTokens > 0)
			target.CompactionReserveTokens = local.CompactionReserveTokens;
	}

	// Persists the auto section as edited by /config: replaces (or adds) the entry for one
	// endpoint in the USER settings file — model/endpoint configuration is user-level, set up
	// once and serving every project and worktree (Docker runs mount ~/.beast at /root/.beast,
	// so the home file is the same one true file either way). An endpoint whose model list is
	// emptied is removed outright. Configuring an OpenRouter key also seeds the web-search
	// block, so internet_search works without a second setup step.
	public void SaveAutoEndpoint(string baseUrl, string apiKey, List<AutoModelConfig> models)
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

		if (models.Count == 0)
		{
			if (existing >= 0)
				home.Auto.RemoveAt(existing);
		}
		else
		{
			AutoProviderConfig entry = new AutoProviderConfig { BaseUrl = baseUrl, ApiKey = apiKey, Models = models };

			// An apply that did not re-enter the key keeps the one already on disk.
			if (string.IsNullOrEmpty(entry.ApiKey) && existing >= 0)
				entry.ApiKey = home.Auto[existing].ApiKey;

			if (existing >= 0)
				home.Auto[existing] = entry;
			else
				home.Auto.Add(entry);

			InjectWebSearchKey(home, entry.BaseUrl, entry.ApiKey);
		}

		WriteSettings(_homeDirSettingsPath, home);

		// A leftover auto section in the PROJECT file would shadow the home one (local settings
		// replace the list wholesale) and make picker saves look like no-ops. Strip it.
		BeastSettings? local = LoadSettingsFromFile(_workDirSettingsPath);
		if (local != null && local.Auto.Count > 0)
		{
			local.Auto = new List<AutoProviderConfig>();
			WriteSettings(_workDirSettingsPath, local);
		}

		LoadSettings();
	}

	// Seeds the web-search block with a freshly configured OpenRouter key. Only a missing or
	// placeholder key is replaced — a deliberate different key is never overwritten — and the
	// block is enabled alongside, since its default model is the free router.
	private static void InjectWebSearchKey(BeastSettings home, string baseUrl, string apiKey)
	{
		if (!baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(apiKey))
			return;

		OpenrouterSearchConfig? search = home.WebSearch?.Openrouter;
		if (search == null)
		{
			home.WebSearch = CreateDefaultWebSearch(apiKey, true);
		}
		else if (string.IsNullOrEmpty(search.ApiKey) || search.ApiKey.StartsWith("YOUR_", StringComparison.Ordinal))
		{
			search.ApiKey = apiKey;
			search.Enabled = true;
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

	private static BeastSettings CreateDefaultProjectSettings()
	{
		return new BeastSettings { };
	}

	private static BeastSettings CreateDefaultHomeSettings()
	{
		return new BeastSettings
		{
			IdleSoundFile = "C:/Windows/media/Windows Background.wav",
			SubagentSoundFile = "C:/Windows/media/Windows Hardware Fail.wav",
			CompactionReserveTokens = 4096,
			// No example providers: /config is the onboarding path now (endpoint presets, catalog
			// discovery, spacebar enablement). The manual section still works for hand-tuned
			// configs — full ModelConfig with extras/headers/reasoning — it just starts empty
			// instead of shipping placeholder stubs that showed up as phantom endpoints.
			Providers = new List<ProviderConfig>(),
			Tools = new Dictionary<string, ToolConfig>()
			{
				{ "bash", new ToolConfig() {
				  Description = "Standard bash command. CWD is at the root of the repo at /workspace/",
				  Parameters = new Dictionary<string,string>() {
					  { "command", "Shell command to execute" },
					  { "timeout_seconds", "Timeout in seconds (default 120)." }
					},
				} },
			  { "readonly_bash", new ToolConfig() {
				  Description = "Read-only bash for inspecting the repo without changing it. Runs in a restricted bash shell. CWD is the repo root at /workspace/.",
				  Parameters = new Dictionary<string,string>() {
					  { "command", "Safe shell command to execute. Do not use stream redirection, do not use find or cd, no running programs by explicit path, and no modifying the PATH env var. A small set of safe commands are available; in the event of an error, they will be listed for you" },
					  { "timeout_seconds", "Timeout in seconds (default 120)." },
				  },
			  } },
			  { "write_file", new ToolConfig() {
				  Description = "Create a new file or overwrite an existing one (if you used read_file already). CWD is /workspace/ but temporary files should go in /tmp/",
				  Parameters = new Dictionary<string,string>() {
					  { "file_path", "File path" },
					  { "content", "Complete file contents" },
				  },
			  } },
				{ "edit_file", new ToolConfig() {
				  Description = "Replace old_text with new_text in a file. Tries exact match first; if not found, retries ignoring all whitespace. CWD is at the root of the repo at /workspace/",
				  Parameters = new Dictionary<string,string>() {
					  { "file_path", "File path" },
					  { "old_text", "Text to find and replace" },
					  { "new_text", "Replacement text" },
				  },
			  } },
				{ "ls", new ToolConfig() {
				  Description = "List a folder's contents. CWD is the repo root at /workspace/.",
				  Parameters = new Dictionary<string,string>() {
					  { "folder", "Folder to list" }
				  },
			  } },
				{ "read_file", new ToolConfig() {
				  Description = "Read a file's raw contents. Returns up to 500 lines starting at offset. CWD is the repo root at /workspace/.",
				  Parameters = new Dictionary<string,string>() {
					{ "file_path", "File path" },
					{ "offset", "Starting line number (1 based). Omit for the beginning of the file." },
					{ "lines",  "Number of lines to read. Omit to read to the end of the file (capped at 500)." },
				  },
			  } },
				{ "find_relevant_file_sections", new ToolConfig() {
				  Description = "Find the sections of a file relevant to a goal: returns where different concepts can be found in the indicated file, citations for different sections so you can target follow-up read_file calls better. Small files are returned immediately without interpretation. CWD is the repo root at /workspace/.",
				  Parameters = new Dictionary<string,string>() {
					  { "file_path", "File path" },
					{ "goal", "What kind of content is relevant to you in this file. Used to focus the citations returned." },
					{ "offset", "Starting line number (1 based) for the window to digest. Omit for the beginning of the file." },
				  },
			  } },
				{ "inspect_media", new ToolConfig() {
				  Description = "Interpret an image or audio file with a media-capable model and get back only what the goal asks for. Use for screenshots, diagrams, photos, and recordings. CWD is the repo root at /workspace/.",
				  Parameters = new Dictionary<string,string>() {
					  { "file_path", "Path to the media file (png, jpg, gif, webp, bmp, wav, mp3, m4a, ogg, flac)." },
					  { "goal", "Exactly what to extract or answer from the media; only this is returned." },
				  },
			  } },
				{ "fetch_url", new ToolConfig() {
				  Description = "Fetch a web page and get back only the information you ask for, filtered by an LLM to just what your goal describes.",
				  Parameters = new Dictionary<string,string>() {
					  { "url", "The fully-formed URL to fetch content from." },
					{ "goal", "Explain exactly what you are looking for and how that information will be used, so only that is returned." },
				  },
			  } },
			{ "internet_search", new ToolConfig() {
				  Description = "Search the internet using a natural language query. This costs real money, so only use when it is necessary to discover documentation or current information that cannot be gained through local file accesses. Up to five links and summaries will be returned.",
				  Parameters = new Dictionary<string,string>() {
					  { "query", "Use clear natural language to describe what should be retrieved from Google" },
					  { "goal", "Provide a clear prompt to an agent so that it can appropriately filter down the results to what is relevant, not the complete content of the page. If you know exactly what you're looking for ask for it here." },
				  },
			  } },
			  { "assign_work", new ToolConfig() {
				  Description = "Hand a concrete unit of work to the Developer subagent. It works in a git worktree, gets the change reviewed and integrated, and returns a report. After this, you stay in a work loop — you are re-prompted each turn to assign the next unit of work — until you call stop_work.",
				  Parameters = new Dictionary<string,string>() {
					  { "prompt", "The task for the Developer, written in natural language as if you were the user instructing it." },
				  },
			  } },
			  { "stop_work", new ToolConfig() {
				  Description = "End the work loop and hand control back to the user. Call this once all delegated work is complete (or should not continue). Until you call it, you are re-prompted after each turn to assign the next unit of work.",
				  Parameters = new Dictionary<string,string>() {
					  { "summary", "A brief summary of what was accomplished across the delegated work, or why the work is stopping." },
				  },
			  } },
			  { "review_work", new ToolConfig() {
				  Description = "Ask a fresh Reviewer session with no prior context to inspect your changes for quality and correctness. The verdict will be approved or rejected, but in either case may contain comments that need to be addressed. The review does not commit anything. Once approved, address any requested issues, then integrate the work with commit_and_rebase.",
				  Parameters = new Dictionary<string,string>() {
					  { "prompt", "Provide sufficient context for a reviewer to understand the nature of the work, what changes were made, how to view the diffs, if all the changes being reviewed relate to the task or if other changes are present as well that should be disregarded. Do not be convincing, be informative to a peer unfamiliar with the codebase." },
				  },
			  } },
			  { "commit_and_rebase", new ToolConfig() {
				  Description = "Runs a script to check in all changes in the worktree and integrate them. The workflow is always to rebase commits on the current branch onto the parent branch (linear history, no merge commit), and fast-forward the base onto this branch. Call this after an approved review. On a conflict, the rebase stops with the conflicted files listed; resolve them then run 'git rebase --continue' with bash, then call this again to finish",
				  Parameters = new Dictionary<string,string>() {
					  { "message", "A brief git commit message describing the changes" },
				  },
			  } },
			  { "task_complete", new ToolConfig() {
				  Description = "Declare this task fully finished and return a detailed message to the agent that delegated it to you. Before calling this, you should first get feedback with review_work then after approval check it in with commit_and_rebase. If you cannot complete the work, note that.",
				  Parameters = new Dictionary<string,string>() {
					  { "results_of_review_work", "Describe the work completed, any unfinished of follow-on tasks that the managing agent should add to the task list, and final integration status. This string is the entire response the agent receives, so be precise and maximally useful." },
					  { "success", "True if the task was completed successfully; false if it failed or was blocked." },
				  },
			  } },
			  { "finish_review", new ToolConfig() {
				  Description = "Call this to finish the review of the indicated work. The developer that requested this review will perform all corrective actions and manage the flow of content into the git repo. Your response here is all the developer will receive in response to requesting this review. Read your instructions carefully to understand the acceptance criteria, but realize that the developer can respond to minor change requests if criteria have been met, so provide comments that lead to maximum quality results.",
				  Parameters = new Dictionary<string,string>() {
					  { "approved", "True to accept these changes, false to require the developer to rework" },
					{ "comments", "Without being overly conversational, indicate what was reviewed, describe the rationale for acceptance or rejection, what could be improved. This is the entire response the developer agent will receive." },
				  },
			  } },
			  { "return_to_caller", new ToolConfig() {
				  Description = "Call this to declare your task finished.",
				  Parameters = new Dictionary<string,string>() {
					  { "output", "This is the entire response the calling agent receives. Carefully follow the instructions provided, use an appropriate level of detail to be maximally helpful and maintain high signal to noise ratio. Include all critical information that someone receiving your response would want to know about to be fully informed." },
					  { "success", "True if the task could be considered a success; false if it failed, was blocked, or unable to complete." },
				  },
			  } },
			},
			WebSearch = CreateDefaultWebSearch("YOUR_OPENROUTER_KEY_HERE", false)
		};
	}

	// The web-search block template: OpenRouter's web plugin on the free-model router. Used for
	// the generated defaults (placeholder key, disabled) and by the /config OpenRouter-key
	// injection (real key, enabled).
	private static WebSearchConfig CreateDefaultWebSearch(string apiKey, bool enabled)
	{
		return new WebSearchConfig
		{
			Openrouter = new OpenrouterSearchConfig
			{
				Endpoint = "https://openrouter.ai/api/v1/chat/completions",
				ApiKey = apiKey,
				Enabled = enabled,
				// The free-model router: always resolves to SOME zero-cost model, so the
				// default never rots when an individual free model is renamed or retired.
				Model = "openrouter/free",
				Extras = new List<JsonObject>
				{
					new JsonObject { ["plugins"] = new JsonArray(new JsonObject { ["id"] = "web" }) },
					new JsonObject { ["temperature"] = JsonValue.Create(0) },
					new JsonObject { ["max_tokens"] = JsonValue.Create(4096) }
				}
			}
		};
	}
}