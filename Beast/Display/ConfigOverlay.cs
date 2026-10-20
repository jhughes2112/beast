using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;


// The /config modal overlay: pick an endpoint (or add one), then toggle models from its
// discovered catalog with spacebar. Enter on a model opens one menu of every editable field. Fields the catalog could not determine (context window,
// pricing, modalities) are prompted for at the moment a model is enabled — the one moment the
// user is demonstrably paying attention — and only those values are persisted; everything
// discoverable is re-discovered at every load. All state is owned here; DisplayScreen routes
// keys while open, composites Build() over the frame, and feeds Config frames in.
internal class ConfigOverlay
{
	private enum Mode { Closed, Endpoints, AddPreset, AddUrl, AddKey, Loading, Models, Details, Applying, SearchModel }

	// One web-search provider as reported by the agent.
	private class SearchRow
	{
		public string  Id     = string.Empty;
		public string  Name   = string.Empty;
		public string  Domain = string.Empty;
		public decimal Price;
		public bool    Configured;
		public bool    Enabled;
		// False when no configured endpoint carries the provider's domain: the provider cannot
		// run regardless of Enabled, and the row says so rather than pretending to work.
		public bool   HasKey;
		public string Model = string.Empty;
	}

	// The home screen is one navigable list with two sections; the header row is skipped by
	// navigation so arrow keys never land on something that cannot be actioned.
	private enum HomeRowKind { Endpoint, AddEndpoint, Spacer, SearchHeader, Search }

	// Well-known endpoints offered by "+ Add endpoint": local servers first, then the cloud
	// providers. URLs are FULL request endpoints (the form NormalizeRequestEndpoint passes
	// through untouched), because a few providers (Gemini, Novita) hang their OpenAI-compatible
	// API off a path the bare-host heuristic would guess wrong. Stable constants, no keys — the
	// list costs nothing to carry and turns adding a provider into pick + paste key.
	// Ordered by likelihood of being the one the user came for: the everyday picks first, then
	// the remaining local servers, then the rest roughly by popularity.
	private const string kChatGptSubscriptionUrl = "https://chatgpt.com/backend-api/codex/responses";

	private static readonly (string Name, string Url)[] kPresets =
	{
		("Custom URL…", ""),
		("llama-server (local)", "http://localhost:8080/v1/chat/completions"),
		("OpenAI",               "https://api.openai.com/v1/responses"),
		("Anthropic",            "https://api.anthropic.com/v1/messages"),
		// Not an HTTP endpoint: the agent drives a Claude subscription through the Claude Code CLI.
		("Anthropic Subscription", "claude-code://subscription"),
		// A ChatGPT plan signed in with a device code (no key to paste); served by the Codex backend.
		// The agent shows the sign-in URL and code in the loading screen.
		("OpenAI Subscription",                         kChatGptSubscriptionUrl),
		("OpenRouter",          "https://openrouter.ai/api/v1/chat/completions"),
		("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"),
		("xAI (Grok)",        "https://api.x.ai/v1/chat/completions"),
		// Meta recommends its Responses API (reasoning carried across turns, web search grounding).
		("Meta",              "https://api.meta.ai/v1/responses"),
		("Ollama (local)",    "http://localhost:11434/v1/chat/completions"),
		("LM Studio (local)", "http://localhost:1234/v1/chat/completions"),
		("vLLM (local)",      "http://localhost:8000/v1/chat/completions"),
		("KoboldCpp (local)", "http://localhost:5001/v1/chat/completions"),
		("Jan (local)",       "http://localhost:1337/v1/chat/completions"),
		("DeepSeek",          "https://api.deepseek.com/v1/chat/completions"),
		("Groq",              "https://api.groq.com/openai/v1/chat/completions"),
		("Mistral",           "https://api.mistral.ai/v1/chat/completions"),
		("Together AI",       "https://api.together.xyz/v1/chat/completions"),
		("Fireworks",         "https://api.fireworks.ai/inference/v1/chat/completions"),
		("Perplexity",        "https://api.perplexity.ai/chat/completions"),
		("Cerebras",          "https://api.cerebras.ai/v1/chat/completions"),
		("Moonshot (Kimi)",   "https://api.moonshot.ai/v1/chat/completions"),
		("Qwen (DashScope)", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions"),
		("Zhipu GLM",  "https://api.z.ai/api/paas/v4/chat/completions"),
		("MiniMax",    "https://api.minimax.io/v1/chat/completions"),
		("NVIDIA NIM", "https://integrate.api.nvidia.com/v1/chat/completions"),
		("Novita",     "https://api.novita.ai/v3/openai/chat/completions"),
		("Hyperbolic", "https://api.hyperbolic.xyz/v1/chat/completions"),
		("SambaNova",  "https://api.sambanova.ai/v1/chat/completions")
	};

	private int _presetSelected;
	private int _presetScroll;

	private class ModelRow
	{
		public string Id   = string.Empty;
		public string Name = string.Empty;

		// Discovered values exactly as the endpoint reported them (0 / -1 / null = unknown).
		public int           DiscWindow;
		public decimal       DiscCostIn  = -1m;
		public decimal       DiscCostOut = -1m;
		public List<string>? DiscModalities;
		// Unix epoch seconds the model was released; 0 = unknown (sorts to the alphabetical tail).
		public long Created;

		// User overrides (0 / -1 / null / empty = none — discovery rules). Blank edits clear these.
		public int           OvrWindow;
		public decimal       OvrCostIn  = -1m;
		public decimal       OvrCostOut = -1m;
		public List<string>? OvrModalities;
		public string        OvrEffort = string.Empty;
		// Send the model its own reasoning back on later turns. Nothing discovers this, so unlike
		// the fields above there is no "auto" state behind the blank — off is off.
		public bool OvrRetainReasoning;
		// Ask the provider to summarize this model's thinking. On unless turned off here, or until an
		// endpoint refuses and the agent writes it back as false.
		public bool OvrSummaries = true;
		// Most images per request, learned by the agent from a provider's refusal (0 = none known).
		// Not edited here — only carried, so an apply does not erase what the provider taught.
		public int OvrMaxImages;
		// Silent-request timeout in seconds (0 = the default), and extra request fields / headers as a
		// compact JSON array of objects (empty = none) — the same shapes the settings file stores.
		public int    OvrTimeout;
		public string OvrExtras  = string.Empty;
		public string OvrHeaders = string.Empty;

		public bool Enabled;
		// True when a settings entry exists (even disabled) — disabling never forgets overrides.
		public bool Configured;
		// True when the entry is configured but the endpoint's catalog no longer lists it: it
		// cannot be enabled, so space and Del both forget it.
		public bool Missing;

		// Effective values: override beats discovered.
		public int           Window     => OvrWindow > 0 ? OvrWindow : DiscWindow;
		public decimal       CostIn     => OvrCostIn >= 0 ? OvrCostIn : DiscCostIn;
		public decimal       CostOut    => OvrCostOut >= 0 ? OvrCostOut : DiscCostOut;
		public List<string>? Modalities => OvrModalities ?? DiscModalities;

		// Unknown means neither discovered nor overridden — the fields that MUST be supplied
		// before the model can be enabled.
		public bool UnknownWindow     => Window <= 0;
		public bool UnknownCost       => CostIn < 0 || CostOut < 0;
		public bool UnknownModalities => Modalities == null;
	}

	private          Mode _mode = Mode.Closed;
	private readonly List<(string BaseUrl, string Source, int EnabledCount)> _endpoints = new();
	// Every provider the agent supports, in the order it sent them (cheapest first).
	private readonly List<SearchRow>                     _search   = new();
	private readonly List<(HomeRowKind Kind, int Index)> _homeRows = new();
	private          int _homeSelected;
	private          int _homeScroll;
	// The provider whose model is being edited (SearchModel mode).
	private SearchRow? _searchModelRow;
	private string     _baseUrl = string.Empty;
	private string     _apiKey  = string.Empty;
	// Shared text-entry buffer for the AddUrl/AddKey/Details modes.
	private          string         _entry   = string.Empty;
	private          string         _status  = string.Empty;
	private readonly List<ModelRow> _models  = new();
	private readonly List<int>      _visible = new();
	private          int            _modelSelected;
	private          int            _modelScroll;
	private          string         _filter = string.Empty;

	private ModelRow? _detailRow;
	private int       _detailScroll;
	// True while the selected field is being typed into (_entry); otherwise the menu is navigating.
	private bool _detailEditing;
	private int  _detailIndex;
	// True when the editor was opened by an enable (space on a row with unknowns): finishing it
	// completes the enable. False when opened by Enter as a plain edit.
	private bool _detailEnableOnFinish;

	// Set by any toggle or committed edit; Esc from the model list applies when dirty and just
	// returns otherwise, so an untouched visit can never rewrite the settings file.
	private bool _dirty;

	// Sends a global command line to the agent (the host prefixes the session routing).
	private readonly Action<string> _sendCommand;

	public ConfigOverlay(Action<string> sendCommand)
	{
		_sendCommand = sendCommand;
	}

	public bool IsOpen => _mode != Mode.Closed;

	public void Open()
	{
		_mode = Mode.Endpoints;
		_endpoints.Clear();
		_search.Clear();
		_homeRows.Clear();
		_homeSelected = 0;
		_homeScroll   = 0;
		_models.Clear();
		_filter = string.Empty;
		_status = "Loading endpoints…";
		_sendCommand("/config-endpoints");
	}

	public void Close()
	{
		_mode = Mode.Closed;
	}

	// Feeds one Config frame payload; returns true when it was consumed (overlay open).
	public bool OnConfigFrame(string json)
	{
		if (_mode == Mode.Closed)
			return false;

		try
		{
			JsonNode? root = JsonNode.Parse(json);
			string    kind = root?["kind"]?.GetValue<string>() ?? string.Empty;
			if (kind == "endpoints")
			{
				_endpoints.Clear();
				JsonArray? list = root?["endpoints"]?.AsArray();
				if (list != null)
				{
					foreach (JsonNode? e in list)
					{
						if (e == null)
							continue;
						_endpoints.Add((
							e["baseUrl"]?.GetValue<string>() ?? string.Empty,
							e["source"]?.GetValue<string>() ?? "auto",
							e["enabledCount"]?.GetValue<int>() ?? 0));
					}
				}

				_search.Clear();
				JsonArray? searchList = root?["search"] as JsonArray;
				if (searchList != null)
				{
					foreach (JsonNode? entry in searchList)
					{
						if (entry == null)
							continue;
						_search.Add(new SearchRow
						{
							Id         = entry["id"]?.GetValue<string>() ?? string.Empty,
							Name       = entry["name"]?.GetValue<string>() ?? string.Empty,
							Domain     = entry["domain"]?.GetValue<string>() ?? string.Empty,
							Price      = entry["price"]?.GetValue<decimal>() ?? 0m,
							Configured = entry["configured"]?.GetValue<bool>() ?? false,
							Enabled    = entry["enabled"]?.GetValue<bool>() ?? false,
							HasKey     = entry["hasKey"]?.GetValue<bool>() ?? false,
							Model      = entry["model"]?.GetValue<string>() ?? string.Empty
						});
					}
				}
				RebuildHomeRows();
				// Only clear the loading placeholder — a "Saved …" notice from an apply that
				// triggered this refresh stays visible on the endpoint screen.
				if (_status.StartsWith("Loading endpoints", StringComparison.Ordinal))
					_status = string.Empty;
			}
			else if (kind == "progress")
			{
				// A step the user must act on while the catalog loads — the ChatGPT sign-in URL and
				// code — shown in the modal itself rather than left to the status bar.
				if (_mode == Mode.Loading)
					_status = root?["text"]?.GetValue<string>() ?? string.Empty;
			}
			else if (kind == "catalog")
			{
				// Adopt the endpoint the agent actually reached — it may have swapped localhost
				// for host.docker.internal (or back) — so apply persists the working URL.
				string effective = root?["baseUrl"]?.GetValue<string>() ?? string.Empty;
				if (effective.Length > 0)
					_baseUrl = effective;

				string error = root?["error"]?.GetValue<string>() ?? string.Empty;

				// A failed fetch still carries the endpoint's CONFIGURED models, so an unreachable
				// endpoint's list opens anyway — disabling a model must never require the provider
				// to be up. Only a failure with nothing configured has no list to show.
				_models.Clear();
				JsonArray? list = root?["models"]?.AsArray();
				if (list != null)
				{
					foreach (JsonNode? m in list)
					{
						if (m == null)
							continue;
						ModelRow row = new ModelRow
						{
							Id             = m["id"]?.GetValue<string>() ?? string.Empty,
							Name           = m["name"]?.GetValue<string>() ?? string.Empty,
							DiscWindow     = m["contextWindow"]?.GetValue<int>() ?? 0,
							DiscCostIn     = m["costInput"]?.GetValue<decimal>() ?? -1m,
							DiscCostOut    = m["costOutput"]?.GetValue<decimal>() ?? -1m,
							DiscModalities = ReadStringList(m["modalities"]),
							Enabled        = m["enabled"]?.GetValue<bool>() ?? false,
							Configured     = m["configured"]?.GetValue<bool>() ?? false,
							Missing        = m["missing"]?.GetValue<bool>() ?? false,
							Created        = m["created"]?.GetValue<long>() ?? 0
						};
						JsonNode? over = m["override"];
						if (over != null)
						{
							row.OvrWindow  = over["contextWindow"]?.GetValue<int>() ?? 0;
							JsonNode? cost = over["cost"];
							if (cost != null)
							{
								row.OvrCostIn  = cost["input"]?.GetValue<decimal>() ?? -1m;
								row.OvrCostOut = cost["output"]?.GetValue<decimal>() ?? -1m;
							}
							row.OvrModalities      = ReadStringList(over["modalities"]);
							row.OvrEffort          = over["reasoningEffort"]?.GetValue<string>() ?? string.Empty;
							row.OvrRetainReasoning = over["retainReasoning"]?.GetValue<bool>() ?? false;
							row.OvrSummaries       = over["reasoningSummaries"]?.GetValue<bool>() ?? true;
							row.OvrMaxImages       = over["maxImages"]?.GetValue<int>() ?? 0;
							row.OvrTimeout         = over["requestTimeoutSeconds"]?.GetValue<int>() ?? 0;
							row.OvrExtras          = ReadObjects(over["extras"]);
							row.OvrHeaders         = ReadObjects(over["headers"]);
						}
						_models.Add(row);
					}
				}

				if (_models.Count == 0 && error.Length > 0)
				{
					_status = error;
					_mode   = Mode.Endpoints;
				}
				else
				{
					_dirty         = false;
					_filter        = string.Empty;
					_modelSelected = 0;
					_modelScroll   = 0;
					RebuildVisible();
					_status = error.Length > 0
						? $"Catalog fetch failed ({error}) — showing configured models; space or Del removes one."
						: string.Empty;
					_mode = Mode.Models;
				}
			}
			else if (kind == "applied")
			{
				// Back to a refreshed endpoint list rather than closing: configuring several
				// endpoints in one sitting is the common case, and Esc leaves when done.
				_status       = $"Saved {_baseUrl}.";
				_homeSelected = 0;
				_mode         = Mode.Endpoints;
				_sendCommand("/config-endpoints");
			}
			else if (kind == "search-applied")
			{
				// Refresh in place: the reload may have changed which providers resolve a key.
				_sendCommand("/config-endpoints");
			}
			else if (kind == "apply-failed")
			{
				_status = "Apply failed — see the error above. Esc to close.";
				_mode   = Mode.Models;
			}
		}
		catch (Exception ex)
		{
			_status = $"Bad config payload: {ex.Message}";
		}
		return true;
	}

	// A stored extras/headers value as the compact array of objects the editor works in; empty when
	// absent or malformed.
	private static string ReadObjects(JsonNode? node)
	{
		string normalized = string.Empty;
		if (node != null)
			TryNormalizeObjects(node.ToJsonString(), out normalized);
		return normalized;
	}

	private static List<string>? ReadStringList(JsonNode? node)
	{
		// Type-checked rather than AsArray(), which throws on any other shape.
		JsonArray? array = node as JsonArray;
		if (array == null)
			return null;
		List<string> values = new List<string>();
		foreach (JsonNode? item in array)
		{
			string? v = item?.GetValue<string>();
			if (!string.IsNullOrEmpty(v))
				values.Add(v);
		}
		return values;
	}

	// Surfaces an agent-side error while the overlay is waiting on a reply that will now never
	// come (e.g. an older agent build rejecting the /config commands). Shows the error in the
	// modal and unblocks the waiting mode; returns false when the overlay is closed.
	public bool ShowAgentError(string text)
	{
		if (_mode == Mode.Closed)
			return false;
		_status = text;
		if (_mode == Mode.Loading)
			_mode = Mode.Endpoints;
		else if (_mode == Mode.Applying)
			_mode = Mode.Models;
		return true;
	}

	// Routes pasted text into whichever text field is active. Every field here is a single-line
	// value (URL, API key, number, modality list) or the filter, so newlines and surrounding
	// whitespace are stripped. Always returns true while open: the overlay is modal, and a paste
	// in a non-text mode must be swallowed, never leaked into the chat input behind it.
	public bool HandlePaste(string text)
	{
		if (_mode == Mode.Closed)
			return false;

		string clean = text.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
		if (clean.Length > 0)
		{
			if (_mode == Mode.AddUrl || _mode == Mode.AddKey || (_mode == Mode.Details && _detailEditing) || _mode == Mode.SearchModel)
			{
				_entry += clean;
			}
			else if (_mode == Mode.Models)
			{
				_filter       += clean;
				_modelSelected = 0;
				RebuildVisible();
			}
		}
		return true;
	}

	// Handles one key while open. Returns true always — the overlay is modal.
	public bool HandleKey(ConsoleKeyInfo key)
	{
		switch (_mode)
		{
			case Mode.Endpoints:
				HandleEndpointsKey(key);
				break;
			case Mode.AddPreset:
				HandlePresetKey(key);
				break;
			case Mode.SearchModel:
				HandleSearchModelKey(key);
				break;
			case Mode.AddUrl:
			case Mode.AddKey:
				HandleEntryKey(key);
				break;
			case Mode.Loading:
			case Mode.Applying:
				if (key.Key == ConsoleKey.Escape)
					_mode = Mode.Endpoints;
				break;
			case Mode.Models:
				HandleModelsKey(key);
				break;
			case Mode.Details:
				HandleDetailsKey(key);
				break;
		}
		return true;
	}

	// Rebuilds the home list: endpoints, the add-endpoint row, then EVERY supported web-search
	// provider. Listing them all — dimmed when no endpoint supplies their key — makes enabling one
	// a single spacebar rather than a trip through a chooser, and makes the roster (and what each
	// one costs) visible without going looking for it.
	private void RebuildHomeRows()
	{
		_homeRows.Clear();
		for (int i = 0; i < _endpoints.Count; i++)
			_homeRows.Add((HomeRowKind.Endpoint, i));
		_homeRows.Add((HomeRowKind.AddEndpoint, 0));

		_homeRows.Add((HomeRowKind.Spacer, 0));
		_homeRows.Add((HomeRowKind.SearchHeader, 0));
		for (int i = 0; i < _search.Count; i++)
			_homeRows.Add((HomeRowKind.Search, i));

		if (_homeSelected >= _homeRows.Count)
			_homeSelected = _homeRows.Count - 1;
		if (_homeSelected < 0)
			_homeSelected = 0;
		if (_homeRows.Count > 0 && !IsSelectable(_homeRows[_homeSelected].Kind))
			MoveHome(1);
	}

	// The section header and the blank separator are decoration, never selection targets.
	private static bool IsSelectable(HomeRowKind kind)
	{
		return kind != HomeRowKind.SearchHeader && kind != HomeRowKind.Spacer;
	}

	// Moves the selection by step, skipping the non-actionable decoration rows.
	private void MoveHome(int step)
	{
		int next = _homeSelected;
		while (true)
		{
			next += step;
			if (next < 0 || next >= _homeRows.Count)
				return;
			if (IsSelectable(_homeRows[next].Kind))
			{
				_homeSelected = next;
				return;
			}
		}
	}

	private void HandleEndpointsKey(ConsoleKeyInfo key)
	{
		if (_homeRows.Count == 0)
		{
			if (key.Key == ConsoleKey.Escape)
				Close();
			return;
		}

		(HomeRowKind kind, int index) = _homeRows[_homeSelected];

		if (key.Key == ConsoleKey.UpArrow)
		{
			MoveHome(-1);
		}
		else if (key.Key == ConsoleKey.DownArrow)
		{
			MoveHome(1);
		}
		else if (key.Key == ConsoleKey.Spacebar || key.KeyChar == ' ')
		{
			// Space toggles a search provider on or off, configuring it on first enable. Disabling
			// keeps the entry and any model override, as it does for models — disabling is not
			// forgetting. Allowed without a key too: adding the endpoint afterwards then just works.
			if (kind == HomeRowKind.Search)
			{
				SearchRow provider = _search[index];
				provider.Enabled   = !provider.Enabled;
				if (provider.Enabled)
					provider.Configured = true;
				if (provider.Enabled && !provider.HasKey)
					_status = $"{provider.Name} needs an endpoint at {provider.Domain} carrying an API key.";
				ApplySearch();
			}
		}
		else if (key.Key == ConsoleKey.Delete)
		{
			// Delete forgets a provider's settings entry outright, model override and all.
			if (kind == HomeRowKind.Search)
			{
				_search[index].Configured = false;
				_search[index].Enabled    = false;
				_search[index].Model      = string.Empty;
				ApplySearch();
			}
			else if (kind == HomeRowKind.Endpoint)
			{
				DeleteEndpoint(index);
			}
		}
		else if (key.Key == ConsoleKey.Enter)
		{
			if (kind == HomeRowKind.AddEndpoint)
			{
				_presetSelected = 0;
				_presetScroll   = 0;
				_mode           = Mode.AddPreset;
			}
			else if (kind == HomeRowKind.Endpoint)
			{
				_baseUrl = _endpoints[index].BaseUrl;
				_apiKey  = string.Empty;
				RequestCatalog();
			}
			else if (kind == HomeRowKind.Search)
			{
				// Enter edits the search model, matching Enter-edits-values on the model list.
				_searchModelRow = _search[index];
				_entry          = _searchModelRow.Model;
				_mode           = Mode.SearchModel;
			}
		}
		else if (key.Key == ConsoleKey.Escape)
		{
			Close();
		}
	}

	// Removes an endpoint outright, models and all — an unreachable endpoint must never be
	// undeletable just because models are still attached; the agent drops its models and cleans
	// them out of the role orderings. A manual endpoint is hand-written config this picker does
	// not own.
	private void DeleteEndpoint(int index)
	{
		(string baseUrl, string source, int _) = _endpoints[index];

		if (source == "manual")
		{
			_status = "Manual endpoints are edited in settings.json, not here.";
			return;
		}

		// Removal is explicit: an apply that merely carries no models keeps the endpoint and its key.
		JsonObject payload = new JsonObject
		{
			["baseUrl"] = baseUrl,
			["apiKey"]  = string.Empty,
			["models"]  = new JsonArray(),
			["remove"]  = true
		};
		_status  = $"Removing {baseUrl}…";
		_baseUrl = baseUrl;
		_mode    = Mode.Applying;
		_sendCommand("/config-apply " + payload.ToJsonString());
	}

	private void HandleSearchModelKey(ConsoleKeyInfo key)
	{
		if (key.Key == ConsoleKey.Escape)
		{
			_searchModelRow = null;
			_mode           = Mode.Endpoints;
		}
		else if (key.Key == ConsoleKey.Backspace)
		{
			if (_entry.Length > 0)
				_entry = _entry.Substring(0, _entry.Length - 1);
		}
		else if (key.Key == ConsoleKey.Enter)
		{
			if (_searchModelRow != null)
			{
				// Blank restores the provider's built-in default, matching blank-means-auto on the
				// model editor; the agent resolves the default and echoes it back.
				_searchModelRow.Model = _entry.Trim();
				_searchModelRow       = null;
				_mode                 = Mode.Endpoints;
				ApplySearch();
			}
		}
		else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
		{
			_entry += key.KeyChar;
		}
	}

	// Sends the whole desired provider list. Toggles are cheap and instantly persisted, so there
	// is no dirty state to lose track of on this screen.
	private void ApplySearch()
	{
		JsonArray providers = new JsonArray();
		foreach (SearchRow row in _search)
		{
			if (!row.Configured)
				continue;
			providers.Add((JsonNode)new JsonObject
			{
				["provider"] = row.Id,
				["enabled"]  = row.Enabled,
				["model"]    = row.Model
			});
		}
		_sendCommand("/config-search-apply " + new JsonObject { ["providers"] = providers }.ToJsonString());
	}

	private void HandlePresetKey(ConsoleKeyInfo key)
	{
		if (key.Key == ConsoleKey.Escape)
		{
			_mode = Mode.Endpoints;
		}
		else if (key.Key == ConsoleKey.UpArrow)
		{
			if (_presetSelected > 0)
				_presetSelected--;
		}
		else if (key.Key == ConsoleKey.DownArrow)
		{
			if (_presetSelected < kPresets.Length - 1)
				_presetSelected++;
		}
		else if (key.Key == ConsoleKey.Enter)
		{
			if (_presetSelected == 0)
			{
				_entry = string.Empty;
				_mode  = Mode.AddUrl;
			}
			else if (kPresets[_presetSelected].Url == kChatGptSubscriptionUrl)
			{
				// No key to ask for: the agent signs in with a device code, shown in the status bar.
				_baseUrl = kChatGptSubscriptionUrl;
				_apiKey  = string.Empty;
				RequestCatalog();
				_status = "Signing in to ChatGPT…";
			}
			else
			{
				_baseUrl = kPresets[_presetSelected].Url;
				_entry   = string.Empty;
				_mode    = Mode.AddKey;
			}
		}
	}

	private void HandleEntryKey(ConsoleKeyInfo key)
	{
		if (key.Key == ConsoleKey.Escape)
		{
			_mode = Mode.Endpoints;
		}
		else if (key.Key == ConsoleKey.Backspace)
		{
			if (_entry.Length > 0)
				_entry = _entry.Substring(0, _entry.Length - 1);
		}
		else if (key.Key == ConsoleKey.Enter)
		{
			if (_mode == Mode.AddUrl)
			{
				if (_entry.Trim().Length > 0)
				{
					_baseUrl = _entry.Trim();
					_entry   = string.Empty;
					_mode    = Mode.AddKey;
				}
			}
			else
			{
				_apiKey = _entry.Trim();
				_entry  = string.Empty;
				RequestCatalog();
			}
		}
		else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
		{
			_entry += key.KeyChar;
		}
	}

	private void HandleModelsKey(ConsoleKeyInfo key)
	{
		if (key.Key == ConsoleKey.Escape)
		{
			// Leaving the list is the save point: apply when anything changed, else just back.
			if (_dirty)
			{
				Apply();
			}
			else
			{
				_mode   = Mode.Endpoints;
				_status = string.Empty;
			}
		}
		else if (key.Key == ConsoleKey.UpArrow)
		{
			if (_modelSelected > 0)
				_modelSelected--;
		}
		else if (key.Key == ConsoleKey.DownArrow)
		{
			if (_modelSelected < _visible.Count - 1)
				_modelSelected++;
		}
		else if (key.Key == ConsoleKey.Spacebar || key.KeyChar == ' ')
		{
			// Matched by character as well as key code: console input events do not always carry
			// ConsoleKey.Spacebar for the space bar, and the filter branch below ignores spaces —
			// so without the KeyChar check the toggle key can be silently swallowed.
			if (_modelSelected >= 0 && _modelSelected < _visible.Count)
			{
				ModelRow row = _models[_visible[_modelSelected]];
				if (row.Missing)
				{
					// A model the endpoint no longer serves cannot be enabled — off IS forget.
					ForgetModel(_visible[_modelSelected]);
				}
				else if (row.Enabled)
				{
					// Disable keeps the entry and its overrides — disabling is not forgetting.
					row.Enabled = false;
					_dirty      = true;
				}
				else if (row.UnknownWindow || row.UnknownCost || row.UnknownModalities)
				{
					// Enabling with unknowns: those fields are required now, while the user is here.
					OpenEditor(row, enableOnFinish: true);
				}
				else
				{
					row.Enabled = true;
					_dirty      = true;
				}
			}
		}
		else if (key.Key == ConsoleKey.Delete)
		{
			// Del forgets the entry outright whatever its state — enabled, disabled, or gone from
			// the catalog — so a dead model can always be cleared out (and, on apply, swept from
			// the role orderings by the agent).
			if (_modelSelected >= 0 && _modelSelected < _visible.Count)
				ForgetModel(_visible[_modelSelected]);
		}
		else if (key.Key == ConsoleKey.Enter)
		{
			// Enter on a row edits its values — every overridable field, prefilled with the
			// current override (blank = auto-discover).
			if (_modelSelected >= 0 && _modelSelected < _visible.Count)
				OpenEditor(_models[_visible[_modelSelected]], enableOnFinish: false);
		}
		else if (key.Key == ConsoleKey.Backspace)
		{
			if (_filter.Length > 0)
			{
				_filter        = _filter.Substring(0, _filter.Length - 1);
				_modelSelected = 0;
				RebuildVisible();
			}
		}
		else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) && key.KeyChar != ' ')
		{
			_filter       += key.KeyChar;
			_modelSelected = 0;
			RebuildVisible();
		}
	}

	// Forgets a model's settings entry outright — enabled or not. The apply drops the entry and
	// the agent sweeps the id out of the role orderings. A row the catalog still lists stays
	// visible as unconfigured; a vanished one has nothing left to show and leaves the list.
	private void ForgetModel(int modelIdx)
	{
		ModelRow row           = _models[modelIdx];
		row.Enabled            = false;
		row.Configured         = false;
		row.OvrWindow          = 0;
		row.OvrCostIn          = -1m;
		row.OvrCostOut         = -1m;
		row.OvrModalities      = null;
		row.OvrEffort          = string.Empty;
		row.OvrRetainReasoning = false;
		row.OvrSummaries       = true;
		row.OvrMaxImages       = 0;
		_dirty                 = true;

		if (row.Missing)
		{
			_models.RemoveAt(modelIdx);
			RebuildVisible();
			if (_modelSelected >= _visible.Count)
				_modelSelected = Math.Max(0, _visible.Count - 1);
		}
	}

	// The editable fields of one model, in menu order.
	private static readonly string[] kDetailFields =
	{
		"window", "costIn", "costOut", "modalities", "effort", "summaries", "retain", "timeout", "extras", "headers"
	};

	// Opens the settings menu for one row. enableOnFinish completes a pending space-toggle: Esc then
	// enables the model once every field marked * (unknown to discovery, so it must be supplied) is set.
	private void OpenEditor(ModelRow row, bool enableOnFinish)
	{
		_detailRow            = row;
		_detailEnableOnFinish = enableOnFinish;
		_detailIndex          = 0;
		_detailScroll         = 0;
		_detailEditing        = false;
		_entry                = string.Empty;
		_status               = enableOnFinish ? "Fill the fields marked * — Esc then enables the model." : string.Empty;
		_mode                 = Mode.Details;
	}

	private void HandleDetailsKey(ConsoleKeyInfo key)
	{
		ModelRow? row = _detailRow;
		if (row == null)
		{
			_mode = Mode.Models;
			return;
		}

		string field = kDetailFields[_detailIndex];
		if (_detailEditing)
		{
			if (key.Key == ConsoleKey.Escape)
			{
				_detailEditing = false;
				_status        = string.Empty;
			}
			else if (key.Key == ConsoleKey.Backspace)
			{
				if (_entry.Length > 0)
					_entry = _entry.Substring(0, _entry.Length - 1);
			}
			else if (key.Key == ConsoleKey.Enter)
			{
				if (CommitDetail(field, _entry.Trim(), row, out string error))
				{
					_detailEditing = false;
					_status        = string.Empty;
				}
				else
				{
					_status = error;
				}
			}
			else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
			{
				_entry += key.KeyChar;
			}
		}
		else if (key.Key == ConsoleKey.Escape)
		{
			FinishDetails(row);
		}
		else if (key.Key == ConsoleKey.UpArrow)
		{
			if (_detailIndex > 0)
				_detailIndex--;
		}
		else if (key.Key == ConsoleKey.DownArrow)
		{
			if (_detailIndex < kDetailFields.Length - 1)
				_detailIndex++;
		}
		else if (key.Key == ConsoleKey.Delete)
		{
			// Back to auto: the override is dropped and discovery (or the default) rules again.
			CommitDetail(field, string.Empty, row, out string _);
			_status = string.Empty;
		}
		else if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar || key.KeyChar == ' ')
		{
			if (field == "summaries")
			{
				row.OvrSummaries = !row.OvrSummaries;
				_dirty           = true;
			}
			else if (field == "retain")
			{
				row.OvrRetainReasoning = !row.OvrRetainReasoning;
				_dirty                 = true;
			}
			else if (key.Key == ConsoleKey.Enter)
			{
				_detailEditing = true;
				_entry         = DetailPrefill(field, row);
				_status        = string.Empty;
			}
		}
	}

	// Leaving the menu. A pending enable completes only when nothing required is still unknown;
	// otherwise the row stays off and says what is missing.
	private void FinishDetails(ModelRow row)
	{
		if (_detailEnableOnFinish)
		{
			List<string> missing = new List<string>();
			foreach (string field in kDetailFields)
			{
				if (DetailRequired(field, row))
					missing.Add(DetailName(field));
			}

			if (missing.Count == 0)
			{
				row.Enabled = true;
				_dirty      = true;
				_status     = string.Empty;
			}
			else
			{
				_status = $"Not enabled — still needed: {string.Join(", ", missing)}.";
			}
		}
		else
		{
			_status = string.Empty;
		}
		_detailRow = null;
		_mode      = Mode.Models;
	}

	// A field is required when discovery cannot supply it and no override has: the model cannot run
	// (or be priced) without it.
	private static bool DetailRequired(string field, ModelRow row)
	{
		switch (field)
		{
			case "window":
				return row.UnknownWindow;
			case "costIn":
			case "costOut":
				return row.UnknownCost;
			case "modalities":
				return row.UnknownModalities;
			default:
				return false;
		}
	}

	private static string DetailName(string field)
	{
		switch (field)
		{
			case "window":
				return "Context window (tokens)";
			case "costIn":
				return "Input cost ($ per Mtok)";
			case "costOut":
				return "Output cost ($ per Mtok)";
			case "modalities":
				return "Input modalities";
			case "effort":
				return "Reasoning effort";
			case "summaries":
				return "Thinking summaries";
			case "retain":
				return "Send reasoning back";
			case "timeout":
				return "Silent-request timeout (s)";
			case "extras":
				return "Extra request fields (JSON)";
			default:
				return "Extra HTTP headers (JSON)";
		}
	}

	// What the row shows when not being edited: the override, or what rules in its absence.
	private static string DetailValue(string field, ModelRow row)
	{
		switch (field)
		{
			case "window":
				return row.OvrWindow > 0 ? row.OvrWindow.ToString() : row.DiscWindow > 0 ? $"{row.DiscWindow} (discovered)" : "unknown";
			case "costIn":
				return row.OvrCostIn >= 0 ? $"${row.OvrCostIn:0.####}" : row.DiscCostIn >= 0 ? $"${row.DiscCostIn:0.00} (discovered)" : "unknown";
			case "costOut":
				return row.OvrCostOut >= 0 ? $"${row.OvrCostOut:0.####}" : row.DiscCostOut >= 0 ? $"${row.DiscCostOut:0.00} (discovered)" : "unknown";
			case "modalities":
				return row.OvrModalities != null ? string.Join(",", row.OvrModalities)
					: row.DiscModalities != null ? $"{string.Join(",", row.DiscModalities)} (discovered)" : "unknown";
			case "effort":
				return row.OvrEffort.Length > 0 ? row.OvrEffort : "medium (default)";
			case "summaries":
				return row.OvrSummaries ? "on" : "off";
			case "retain":
				return row.OvrRetainReasoning ? "on" : "off";
			case "timeout":
				return row.OvrTimeout > 0 ? row.OvrTimeout.ToString() : "300 (default)";
			case "extras":
				return row.OvrExtras.Length > 0 ? DetailJsonText(row.OvrExtras) : "none";
			default:
				return row.OvrHeaders.Length > 0 ? DetailJsonText(row.OvrHeaders) : "none";
		}
	}

	// One line of help for the selected row: what blank means for THIS field and when to touch it.
	private static string DetailHelp(string field)
	{
		switch (field)
		{
			case "window":
			case "costIn":
			case "costOut":
				return "Del = back to the discovered value. * means the endpoint does not report it, so it must be set.";
			case "modalities":
				return "Comma list of text,image,audio,video,file. * means undiscovered: say what the model accepts.";
			case "effort":
				return "none/minimal/low/medium/high/max. Blank = medium; 'none' turns thinking off.";
			case "summaries":
				return "Ask the provider for thinking summaries. Turn off to stop paying for summary tokens.";
			case "retain":
				return "Replay the model's own thinking back to it. Only for models trained on preserved thinking.";
			case "timeout":
				return "How long one request may stay silent before it is retried. Raise for slow local models.";
			case "extras":
				return "A JSON object merged into every request body, e.g. {\"provider\":{\"order\":[\"Anthropic\"]}} on OpenRouter.";
			default:
				return "A JSON object of header names to values added to every request, e.g. {\"anthropic-beta\":\"...\"}.";
		}
	}

	// A stored extras/headers array shown as the single object it almost always is.
	private static string DetailJsonText(string array)
	{
		JsonNode? node = JsonNode.Parse(array);
		return node is JsonArray list && list.Count == 1 && list[0] != null ? list[0]!.ToJsonString() : array;
	}

	// Prefill is the CURRENT OVERRIDE only — a blank field means "auto", so a field without an override
	// starts blank rather than pre-baking the discovered value into one.
	private static string DetailPrefill(string field, ModelRow row)
	{
		switch (field)
		{
			case "window":
				return row.OvrWindow > 0 ? row.OvrWindow.ToString() : string.Empty;
			case "costIn":
				return row.OvrCostIn >= 0 ? row.OvrCostIn.ToString("0.####") : string.Empty;
			case "costOut":
				return row.OvrCostOut >= 0 ? row.OvrCostOut.ToString("0.####") : string.Empty;
			case "modalities":
				return row.OvrModalities != null ? string.Join(",", row.OvrModalities) : string.Empty;
			case "timeout":
				return row.OvrTimeout > 0 ? row.OvrTimeout.ToString() : string.Empty;
			case "extras":
				return row.OvrExtras.Length > 0 ? DetailJsonText(row.OvrExtras) : string.Empty;
			case "headers":
				return row.OvrHeaders.Length > 0 ? DetailJsonText(row.OvrHeaders) : string.Empty;
			default:
				return row.OvrEffort;
		}
	}

	// Commits one text field; blank clears the override (back to auto). Returns false with a message
	// when the value does not parse, holding the editor on the field.
	private bool CommitDetail(string field, string value, ModelRow row, out string error)
	{
		bool ok = true;
		error   = string.Empty;
		if (value.Length == 0)
		{
			switch (field)
			{
				case "window":
					row.OvrWindow = 0;
					break;
				case "costIn":
					row.OvrCostIn = -1m;
					break;
				case "costOut":
					row.OvrCostOut = -1m;
					break;
				case "modalities":
					row.OvrModalities = null;
					break;
				case "summaries":
					row.OvrSummaries = true;
					break;
				case "retain":
					row.OvrRetainReasoning = false;
					break;
				case "timeout":
					row.OvrTimeout = 0;
					break;
				case "extras":
					row.OvrExtras = string.Empty;
					break;
				case "headers":
					row.OvrHeaders = string.Empty;
					break;
				default:
					row.OvrEffort = string.Empty;
					break;
			}
		}
		else if (field == "window" || field == "timeout")
		{
			ok = int.TryParse(value, out int number) && number > 0;
			if (ok && field == "window")
				row.OvrWindow = number;
			else if (ok)
				row.OvrTimeout = number;
			else
				error = "Enter a whole number greater than zero.";
		}
		else if (field == "costIn" || field == "costOut")
		{
			ok = decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal cost) && cost >= 0;
			if (ok && field == "costIn")
				row.OvrCostIn = cost;
			else if (ok)
				row.OvrCostOut = cost;
			else
				error = "Enter a price in dollars per million tokens, e.g. 3 or 0.15.";
		}
		else if (field == "modalities")
		{
			List<string> modalities = new List<string>();
			foreach (string part in value.Split(','))
			{
				string trimmed = part.Trim().ToLowerInvariant();
				if (trimmed.Length > 0)
					modalities.Add(trimmed);
			}
			ok = modalities.Count > 0;
			if (ok)
				row.OvrModalities = modalities;
			else
				error = "Enter at least one of text,image,audio,video,file.";
		}
		else if (field == "extras" || field == "headers")
		{
			ok = TryNormalizeObjects(value, out string normalized);
			if (ok && field == "extras")
				row.OvrExtras = normalized;
			else if (ok)
				row.OvrHeaders = normalized;
			else
				error = "That is not a JSON object. Example: {\"key\": \"value\"}";
		}
		else
		{
			row.OvrEffort = value.ToLowerInvariant();
		}

		if (ok)
			_dirty = true;
		return ok;
	}

	// Accepts one JSON object or an array of objects and returns them as a compact array — the shape the
	// settings file stores. False for anything else, including malformed JSON.
	private static bool TryNormalizeObjects(string text, out string normalized)
	{
		normalized = string.Empty;
		bool ok    = false;
		try
		{
			JsonNode?  node  = JsonNode.Parse(text);
			JsonArray? array = null;
			if (node is JsonObject single)
			{
				array = new JsonArray();
				array.Add(single);
				ok = true;
			}
			else if (node is JsonArray list)
			{
				array = list;
				ok    = true;
				foreach (JsonNode? item in list)
				{
					if (item is not JsonObject)
						ok = false;
				}
			}

			if (ok && array != null)
				normalized = array.ToJsonString();
		}
		catch (System.Text.Json.JsonException)
		{
			ok = false;
		}
		return ok;
	}

	private void RequestCatalog()
	{
		JsonObject request = new JsonObject
		{
			["baseUrl"] = _baseUrl,
			["apiKey"]  = _apiKey
		};
		_status = $"Fetching catalog from {_baseUrl}…";
		_mode   = Mode.Loading;
		_sendCommand("/config-catalog " + request.ToJsonString());
	}

	// Builds the apply payload: every configured model (enabled OR disabled — disabling never
	// forgets), each entry carrying only its enabled flag and the user's overrides. Everything
	// without an override is re-discovered at every load.
	private void Apply()
	{
		JsonArray models = new JsonArray();
		foreach (ModelRow row in _models)
		{
			if (!row.Enabled && !row.Configured)
				continue;

			JsonObject entry = new JsonObject { ["id"] = row.Id, ["enabled"] = row.Enabled };
			if (row.OvrWindow > 0)
				entry["contextWindow"] = row.OvrWindow;
			if (row.OvrCostIn >= 0 || row.OvrCostOut >= 0)
			{
				// A half-overridden pair is completed from discovery so the persisted cost object
				// is always whole; a genuinely unknown side was required at enable time anyway.
				entry["cost"] = new JsonObject
				{
					["input"]  = row.OvrCostIn >= 0 ? row.OvrCostIn : row.DiscCostIn >= 0 ? row.DiscCostIn : 0m,
					["output"] = row.OvrCostOut >= 0 ? row.OvrCostOut : row.DiscCostOut >= 0 ? row.DiscCostOut : 0m
				};
			}
			if (row.OvrModalities != null)
			{
				JsonArray modalities = new JsonArray();
				foreach (string m in row.OvrModalities)
					modalities.Add(m);
				entry["modalities"] = modalities;
			}
			if (!string.IsNullOrEmpty(row.OvrEffort))
				entry["reasoningEffort"] = row.OvrEffort;
			if (row.OvrRetainReasoning)
				entry["retainReasoning"] = true;
			// Written only when OFF: on is the default, and an absent field is what the agent
			// rewrites when an endpoint refuses summaries mid-run.
			if (!row.OvrSummaries)
				entry["reasoningSummaries"] = false;
			if (row.OvrMaxImages > 0)
				entry["maxImages"] = row.OvrMaxImages;
			if (row.OvrTimeout > 0)
				entry["requestTimeoutSeconds"] = row.OvrTimeout;
			if (row.OvrExtras.Length > 0)
				entry["extras"] = JsonNode.Parse(row.OvrExtras);
			if (row.OvrHeaders.Length > 0)
				entry["headers"] = JsonNode.Parse(row.OvrHeaders);
			models.Add(entry);
		}

		JsonObject payload = new JsonObject
		{
			["baseUrl"] = _baseUrl,
			["apiKey"]  = _apiKey,
			["models"]  = models
		};
		_status = "Applying…";
		_mode   = Mode.Applying;
		_sendCommand("/config-apply " + payload.ToJsonString());
	}

	// Filtered view: enabled models sort to the top, then by id; filter is a case-insensitive
	// substring over id and name.
	private void RebuildVisible()
	{
		_visible.Clear();
		for (int i = 0; i < _models.Count; i++)
		{
			ModelRow row = _models[i];
			if (_filter.Length > 0
				&& !row.Id.Contains(_filter, StringComparison.OrdinalIgnoreCase)
				&& !row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
				continue;
			_visible.Add(i);
		}
		_visible.Sort((a, b) =>
		{
			ModelRow ra = _models[a];
			ModelRow rb = _models[b];
			if (ra.Enabled != rb.Enabled)
				return ra.Enabled ? -1 : 1;
			// Configured-but-disabled models sit just under the enabled group — they are "yours".
			if (ra.Configured != rb.Configured)
				return ra.Configured ? -1 : 1;
			// Newest first — the latest models are almost always the ones being reached for.
			// Unknown release dates (0) fall to an alphabetical tail below all dated models.
			if (ra.Created != rb.Created)
				return rb.Created.CompareTo(ra.Created);
			return string.Compare(ra.Id, rb.Id, StringComparison.OrdinalIgnoreCase);
		});
	}

	// ---- Rendering ----

	public Screen Build(int w, int h)
	{
		int bw = Math.Min(Math.Max(60, w - 8), 100);
		int bh = Math.Min(Math.Max(12, h - 4),  32);

		Rgb bg        = new Rgb( 24,  24,  30);
		Rgb borderFg  = new Rgb( 90,  90, 110);
		Rgb titleFg   = new Rgb(200, 200, 210);
		Rgb textFg    = new Rgb(160, 160, 165);
		Rgb dimFg     = new Rgb(105, 105, 110);
		Rgb selBg     = new Rgb( 52,  52,  66);
		Rgb onFg      = new Rgb(130, 190, 140);
		Rgb unknownFg = new Rgb(206, 178, 108);
		Rgb statusFg  = new Rgb(206, 178, 108);

		Screen s = new Screen(bw, bh, new Cell(' ', textFg, bg, CellStyle.None));

		// Border + title.
		for (int x = 0; x < bw; x++)
		{
			s.Set(x,      0, new Cell('─', borderFg, bg, CellStyle.None));
			s.Set(x, bh - 1, new Cell('─', borderFg, bg, CellStyle.None));
		}
		for (int y = 0; y < bh; y++)
		{
			s.Set(     0, y, new Cell('│', borderFg, bg, CellStyle.None));
			s.Set(bw - 1, y, new Cell('│', borderFg, bg, CellStyle.None));
		}
		s.Set(     0,      0, new Cell('┌', borderFg, bg, CellStyle.None));
		s.Set(bw - 1,      0, new Cell('┐', borderFg, bg, CellStyle.None));
		s.Set(     0, bh - 1, new Cell('└', borderFg, bg, CellStyle.None));
		s.Set(bw - 1, bh - 1, new Cell('┘', borderFg, bg, CellStyle.None));
		AnsiToScreen.WriteLine(s, 2, 0, " Model Configuration ", titleFg, bg);

		int innerW = bw - 4;
		switch (_mode)
		{
			case Mode.Endpoints:
			{
				AnsiToScreen.WriteLine(s, 2, 1, "↑↓ move · Enter open/edit · space toggle · Del remove · Esc close", dimFg, bg);

				int visRows = bh - 4;
				if (_homeSelected < _homeScroll)
					_homeScroll = _homeSelected;
				if (_homeSelected >= _homeScroll + visRows)
					_homeScroll = _homeSelected - visRows + 1;

				for (int r = 0; r < visRows; r++)
				{
					int idx = _homeScroll + r;
					if (idx >= _homeRows.Count)
						break;

					(HomeRowKind kind, int index) = _homeRows[idx];
					int  row                      = r + 3;
					bool sel                      = idx == _homeSelected;
					Rgb  rowBg                    = sel ? selBg : bg;

					if (kind == HomeRowKind.Spacer)
						continue;

					if (kind == HomeRowKind.SearchHeader)
					{
						AnsiToScreen.WriteLine(s, 2, row, Truncate("Web search  (the cheapest enabled provider is used)", innerW), dimFg, bg);
						continue;
					}

					Rgb rowFg = kind == HomeRowKind.AddEndpoint ? onFg : textFg;
					// An endpoint contributing no models is dimmed: nothing depends on it.
					if (kind == HomeRowKind.Endpoint && (_endpoints[index].Source == "manual" || _endpoints[index].EnabledCount == 0))
						rowFg = dimFg;
					s.Fill(new Rect(1, row, bw - 2, 1), new Cell(' ', rowFg, rowBg, CellStyle.None));

					string line;
					if (kind == HomeRowKind.Endpoint)
					{
						string tag = _endpoints[index].Source == "manual" ? " [manual]" : string.Empty;
						line       = $"{_endpoints[index].BaseUrl}{tag}  ({_endpoints[index].EnabledCount} enabled)";
					}
					else if (kind == HomeRowKind.AddEndpoint)
					{
						line = "+ Add endpoint";
					}
					else
					{
						SearchRow provider = _search[index];
						// Brightness tracks whether the provider CAN run: a row with no resolvable
						// key is dimmed whatever its flag says, so the ones ready to use stand out
						// and the rest still show what they would cost and what they are waiting on.
						bool   live = provider.Enabled && provider.HasKey;
						string mark = live ? "[x]" : provider.Enabled ? "[!]" : "[ ]";
						string note = provider.HasKey ? provider.Model : $"needs an api key on {provider.Domain}";
						line        = $"{mark} {provider.Name,-16} {$"${provider.Price:0.00}/1k",11}  {note}";
						rowFg       = !provider.HasKey ? dimFg : live ? onFg : textFg;
					}

					AnsiToScreen.WriteLine(s, 2, row, Truncate(line, innerW), rowFg, rowBg);
				}
				break;
			}
			case Mode.SearchModel:
			{
				AnsiToScreen.WriteLine(s, 2, 1,  Truncate($"Search model for {_searchModelRow?.Name}", innerW), titleFg, bg);
				AnsiToScreen.WriteLine(s, 2, 3, Truncate("Model id  [blank = the provider's default]", innerW),  textFg, bg);
				AnsiToScreen.WriteLine(s, 2,      5, Truncate("> " + _entry + "▏", innerW), titleFg, bg);
				AnsiToScreen.WriteLine(s, 2, bh - 2,           "Enter accept · Esc cancel",   dimFg, bg);
				break;
			}
			case Mode.AddPreset:
			{
				AnsiToScreen.WriteLine(s, 2, 1, "Add endpoint  (↑↓ move · Enter pick · Esc back)", dimFg, bg);

				int visRows = bh - 4;
				if (_presetSelected < _presetScroll)
					_presetScroll = _presetSelected;
				if (_presetSelected >= _presetScroll + visRows)
					_presetScroll = _presetSelected - visRows + 1;

				for (int r = 0; r < visRows; r++)
				{
					int idx = _presetScroll + r;
					if (idx >= kPresets.Length)
						break;
					bool sel   = idx == _presetSelected;
					Rgb  rowBg = sel ? selBg : bg;
					Rgb  rowFg = idx == 0 ? onFg : textFg;
					s.Fill(new Rect(1, r + 3, bw - 2, 1), new Cell(' ', rowFg, rowBg, CellStyle.None));

					const int nameWidth = 22;
					string name = kPresets[idx].Name.Length > nameWidth ? kPresets[idx].Name.Substring(0, nameWidth) : kPresets[idx].Name.PadRight(nameWidth);
					AnsiToScreen.WriteLine(s, 2, r + 3, name, rowFg, rowBg);
					if (kPresets[idx].Url.Length > 0)
						AnsiToScreen.WriteLine(s, 2 + nameWidth + 1, r + 3, Truncate(kPresets[idx].Url, innerW - nameWidth - 1), dimFg, rowBg);
				}
				break;
			}
			case Mode.AddUrl:
			case Mode.AddKey:
			{
				string label;
				if (_mode == Mode.AddUrl)
					label = "Endpoint URL (base or full, e.g. http://host:8080/v1):";
				else if (_baseUrl.StartsWith("claude-code://", StringComparison.OrdinalIgnoreCase))
					label = "Long-lived OAuth token (run `claude setup-token` on the host and paste what it prints):";
				else
					label = $"API key for {_baseUrl} (blank for none):";
				AnsiToScreen.WriteLine(s, 2, 2, label, textFg, bg);
				string shown = _mode == Mode.AddKey && _entry.Length > 0 ? new string('•', _entry.Length) : _entry;
				AnsiToScreen.WriteLine(s, 2,      4, Truncate("> " + shown + "▏", innerW), titleFg, bg);
				AnsiToScreen.WriteLine(s, 2, bh - 2,          "Enter continue · Esc back",   dimFg, bg);
				break;
			}
			case Mode.Loading:
			case Mode.Applying:
			{
				AnsiToScreen.WriteLine(s, 2, bh / 2, Truncate(_status, innerW), statusFg, bg);
				AnsiToScreen.WriteLine(s, 2, bh - 2,           "Esc to cancel",    dimFg, bg);
				break;
			}
			case Mode.Models:
			{
				string header = $"{_baseUrl}  —  space toggle · Enter edit · Del forget · type filter · Esc save+back";
				AnsiToScreen.WriteLine(s, 2, 1, Truncate(header, innerW), dimFg, bg);
				AnsiToScreen.WriteLine(s, 2, 2, Truncate($"Filter: {_filter}▏   ({CountEnabled()} enabled / {_models.Count} total)", innerW), textFg, bg);

				int visRows = bh - 5;
				if (_modelSelected < _modelScroll)
					_modelScroll = _modelSelected;
				if (_modelSelected >= _modelScroll + visRows)
					_modelScroll = _modelSelected - visRows + 1;

				for (int r = 0; r < visRows; r++)
				{
					int idx = _modelScroll + r;
					if (idx >= _visible.Count)
						break;
					ModelRow row   = _models[_visible[idx]];
					bool     sel   = idx == _modelSelected;
					Rgb      rowBg = sel ? selBg : bg;
					Rgb      rowFg = row.Enabled ? onFg : row.Configured ? unknownFg : textFg;
					s.Fill(new Rect(1, r + 3, bw - 2, 1), new Cell(' ', rowFg, rowBg, CellStyle.None));

					// [x] enabled · [-] configured but disabled (overrides kept) · [ ] unconfigured.
					string mark   = row.Enabled ? "[x]" : row.Configured ? "[-]" : "[ ]";
					string age    = FormatAge(row.Created);
					string window = row.Window > 0 ? FormatK(row.Window) : "?ctx";
					// Normalized $#.## with each price right-aligned in its own sub-column, so the
					// slash and both decimal points line up down the whole list.
					string cost = row.CostIn >= 0 && row.CostOut >= 0
						? $"{$"${row.CostIn:0.00}",7}/{$"${row.CostOut:0.00}",7}"
						: "$?".PadLeft(15);
					string modalities = row.Modalities != null ? ModalityTag(row.Modalities) : "  ?  ";
					// A vanished model has no live stats worth faking — say what happened instead.
					string right   = row.Missing ? "gone from endpoint" : $"{age,5} {window,7} {cost} {modalities}";
					int    idWidth = Math.Max(8, innerW - right.Length - 5);
					string line    = $"{mark} {Truncate(row.Id, idWidth).PadRight(idWidth)} {right}";
					AnsiToScreen.WriteLine(s, 2, r + 3, Truncate(line, innerW), rowFg, rowBg);
					if (sel && row.Missing)
						AnsiToScreen.WriteLine(s, 2, bh - 2, "No longer in the endpoint's catalog — space or Del removes it from your list.", unknownFg, bg);
					else if (sel && (row.UnknownWindow || row.UnknownCost || row.UnknownModalities) && !row.Enabled)
						AnsiToScreen.WriteLine(s, 2, bh - 2, "Some fields are unknown — enabling will ask for them.", unknownFg, bg);
				}
				break;
			}
			case Mode.Details:
			{
				ModelRow? row = _detailRow;
				AnsiToScreen.WriteLine(s, 2, 1, Truncate($"{(_detailEnableOnFinish ? "Enable" : "Settings for")} {row?.Id}", innerW), titleFg, bg);
				if (row != null)
				{
					int visRows = Math.Max(1, bh - 6);
					if (_detailIndex < _detailScroll)
						_detailScroll = _detailIndex;
					if (_detailIndex >= _detailScroll + visRows)
						_detailScroll = _detailIndex - visRows + 1;

					const int labelWidth = 34;
					for (int r = 0; r < visRows; r++)
					{
						int idx = _detailScroll + r;
						if (idx >= kDetailFields.Length)
							break;

						string field    = kDetailFields[idx];
						bool   sel      = idx == _detailIndex;
						bool   required = DetailRequired(field, row);
						Rgb    rowBg    = sel ? selBg : bg;
						Rgb    rowFg    = required ? unknownFg : textFg;
						s.Fill(new Rect(1, r + 3, bw - 2, 1), new Cell(' ', rowFg, rowBg, CellStyle.None));

						string name  = (required ? "* " : "  ") + DetailName(field);
						string value = sel && _detailEditing ? _entry + "▏" : DetailValue(field, row);
						AnsiToScreen.WriteLine(s, 2, r + 3, Truncate(name.PadRight(labelWidth) + value, innerW), sel && _detailEditing ? titleFg : rowFg, rowBg);
					}

					AnsiToScreen.WriteLine(s, 2, bh - 3, Truncate(DetailHelp(kDetailFields[_detailIndex]), innerW), dimFg, bg);
					if (_status.Length == 0)
					{
						string hint = _detailEditing
							? "Enter accept · Esc cancel"
							: "↑↓ move · Enter edit/toggle · Del reset to auto · Esc done" + (_detailEnableOnFinish ? " (enables)" : string.Empty);
						AnsiToScreen.WriteLine(s, 2, bh - 2, Truncate(hint, innerW), dimFg, bg);
					}
				}
				break;
			}
		}

		if (_status.Length > 0 && _mode != Mode.Loading && _mode != Mode.Applying)
			AnsiToScreen.WriteLine(s, 2, bh - 2, Truncate(_status, innerW), statusFg, bg);

		return s;
	}

	private int CountEnabled()
	{
		int count = 0;
		foreach (ModelRow row in _models)
		{
			if (row.Enabled)
				count++;
		}
		return count;
	}

	// Fixed-order, fixed-width capability strip: one column per known modality — Text, Image,
	// Audio, Video, File — with '·' filling absent slots. Providers list modalities in whatever
	// order they please (FITAV, TIFAV, …); pinning each letter to its own column makes the same
	// capability set always read identically and keeps every row the same width.
	private static string ModalityTag(List<string> modalities)
	{
		char[] slots = { '·', '·', '·', '·', '·' };
		foreach (string m in modalities)
		{
			switch (m.ToLowerInvariant())
			{
				case "text":
					slots[0] = 'T';
					break;
				case "image":
					slots[1] = 'I';
					break;
				case "audio":
					slots[2] = 'A';
					break;
				case "video":
					slots[3] = 'V';
					break;
				case "file":
				case "document":
					slots[4] = 'F';
					break;
			}
		}
		return new string(slots);
	}

	// Days since release, e.g. "5d" / "55d" / "350d"; blank when the release date is unknown.
	private static string FormatAge(long createdEpochSeconds)
	{
		if (createdEpochSeconds <= 0)
			return string.Empty;
		long days = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - createdEpochSeconds) / 86400;
		if (days < 0)
			days = 0;
		return $"{days}d";
	}

	private static string FormatK(int tokens)
	{
		return tokens >= 1000 ? $"{tokens / 1000}k" : tokens.ToString();
	}

	private static string Truncate(string text, int max)
	{
		if (text.Length <= max)
			return text;
		return max > 1 ? text.Substring(0, max - 1) + "…" : text.Substring(0, Math.Max(0, max));
	}
}