using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Tracks availability (rate-limit, down-timer) for a single model config ID.
// Shared across all per-session LlmService instances for the same model so that a
// rate-limit discovered by one session is immediately visible to others.
public class ModelAvailability
{
    public DateTimeOffset AvailableAt = DateTimeOffset.MinValue;
    public bool IsDown => AvailableAt == DateTimeOffset.MaxValue;
}

// Registry of model configs, per-endpoint protocol detection, and per-model availability.
// Creates fresh per-session LlmService instances — protocol conversation state is NOT shared.
// Only availability (rate-limits, down-timers) is shared across sessions using the same model.
public class LlmRegistry
{
	private readonly Dictionary<string, LlmModel> _models = new(StringComparer.OrdinalIgnoreCase);

	// Keyed by endpoint URL. Populated by ProbeEndpointsAsync; never cleared on reload so
	// expensive probes survive /reload. Unknown entries mean the endpoint was unreachable.
	private readonly Dictionary<string, DetectedProtocol> _probeCache = new(StringComparer.OrdinalIgnoreCase);

	// Keyed by model config ID. Populated on demand; never cleared so availability state
	// (rate-limit timers, permanently-down flags) survives /reload and session restarts.
	private readonly Dictionary<string, ModelAvailability> _availability = new(StringComparer.OrdinalIgnoreCase);

	// Stored at load time so GetToolsForRole can rebuild the tool set on demand.
	private WebSearchConfig? _webSearch;

	public LlmRegistry()
	{
	}

	// Loads model configs from settings. Call this first, then ProbeEndpointsAsync.
	// Models already in _availability keep their existing down/rate-limit state across reloads.
	public void LoadFromConfigs(SettingsService settings, RoleService roles)
	{
		_webSearch = settings.Settings.WebSearch;
		_models.Clear();

		foreach (ProviderConfig provider in settings.Settings.Providers)
		{
			string endpoint = provider.BaseUrl.TrimEnd('/');
			foreach (ModelConfig modelConfig in provider.Models)
			{
				if (!modelConfig.Enabled)
					continue;

				Dictionary<string, JsonNode?> extras = new(provider.Extras);
				foreach (KeyValuePair<string, JsonNode?> kv in modelConfig.Extras)
				{
					extras[kv.Key] = kv.Value;
				}
				LlmModel model = new LlmModel(modelConfig.Id, endpoint, provider.ApiKey, extras, modelConfig);
				_models[modelConfig.Id] = model;
			}
		}

		// Expand '*' in role model lists to all enabled model IDs at that position.
		List<string> allModelIds = new List<string>(_models.Keys);
		foreach (Role role in roles.Roles.Values)
		{
			int starIdx = role.Models.IndexOf("*");
			if (starIdx >= 0)
			{
				role.Models.RemoveAt(starIdx);
				foreach (string id in allModelIds)
				{
					if (!role.Models.Contains(id))
						role.Models.Insert(starIdx++, id);
				}
			}
		}
	}

	// Probes any endpoint not yet in _probeCache to detect its protocol.
	// On the docker-internal→localhost fallback, rewrites the stored LlmModel endpoints so
	// subsequent service creation uses the effective URL without re-probing.
	// Safe to call multiple times: already-probed endpoints are skipped.
	public async Task ProbeEndpointsAsync(CancellationToken ct)
	{
		// Collect unique endpoints that haven't been probed yet.
		Dictionary<string, (string apiKey, List<string> modelIds)> unprobed = new(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, LlmModel> kv in _models)
		{
			string ep = kv.Value.Endpoint;
			if (_probeCache.ContainsKey(ep)) continue;
			if (!unprobed.ContainsKey(ep))
				unprobed[ep] = (kv.Value.ApiKey, new List<string>());
			unprobed[ep].modelIds.Add(kv.Key);
		}

		// Kick off all the probes simultaneously, so they all happen in parallel.
		Dictionary<string, Task<(DetectedProtocol, string)>> detectionTasks = new Dictionary<string, Task<(DetectedProtocol, string)>>();
		foreach (KeyValuePair<string, (string apiKey, List<string> modelIds)> entry in unprobed)
		{
			string originalEndpoint = entry.Key;
			string apiKey = entry.Value.apiKey;

			detectionTasks.Add(originalEndpoint, ProtocolProxy.ProbeEndpointAsync(originalEndpoint, ct));
		}
		foreach (KeyValuePair<string, (string apiKey, List<string> modelIds)> entry in unprobed)
		{
			string originalEndpoint = entry.Key;

			Console.WriteLine($"Probing {originalEndpoint}");
			(DetectedProtocol detected, string effectiveEndpoint) = await detectionTasks[originalEndpoint];
			Console.WriteLine($"Probed {originalEndpoint} -> protocol={detected}, effective={effectiveEndpoint}");
			_probeCache[originalEndpoint] = detected;

			if (effectiveEndpoint != originalEndpoint)
			{
				// Localhost fallback fired: rewrite stored models to the effective endpoint and
				// cache it separately so future models at the same URL match immediately.
				_probeCache[effectiveEndpoint] = detected;
				foreach (string modelId in entry.Value.modelIds)
				{
					if (_models.TryGetValue(modelId, out LlmModel? old))
						_models[modelId] = new LlmModel(old.ConfigId, effectiveEndpoint, old.ApiKey, old.Extras, old.Config);
				}
			}
		}
	}

	// Creates a fresh LlmService for the best available model in the role.
	// Each call returns a new instance with its own ProtocolProxy — protocol conversation state
	// is never shared between sessions. The returned service's availability object IS shared
	// so rate-limits discovered by one session affect model selection for others.
	// Returns null if no suitable model is available (all down, context too small, or no role).
	public LlmService? CreateService(Role? role, string preferredModelId, int minContextRequired)
	{
		LlmModel? model = PickModel(role, preferredModelId, minContextRequired);
		if (model == null)
			return null;

		DetectedProtocol protocol = DetectedProtocol.Unknown;
		_probeCache.TryGetValue(model.Endpoint, out protocol);

		ModelAvailability avail = GetOrCreateAvailability(model.ConfigId);
		return new LlmService(model, protocol, avail);
	}

	// Creates a fresh LlmService for a specific model ID regardless of role ordering.
	// Used by tests that need to exercise a particular model directly.
	public LlmService? CreateServiceById(string modelId, int minContextRequired)
	{
		if (!_models.TryGetValue(modelId, out LlmModel? model))
			return null;
		if (model.Config.ContextWindow <= minContextRequired)
			return null;

		ModelAvailability avail = GetOrCreateAvailability(modelId);
		if (avail.IsDown)
			return null;

		DetectedProtocol protocol = DetectedProtocol.Unknown;
		_probeCache.TryGetValue(model.Endpoint, out protocol);

		return new LlmService(model, protocol, avail);
	}

	// Returns the best available model for the role without creating a service.
	// Use when only the model config (name, context window, etc.) is needed.
	public LlmModel? GetModelForRole(Role? role, string preferredModelId, int minContextRequired)
	{
		return PickModel(role, preferredModelId, minContextRequired);
	}

	// Builds a fresh Tool array for the role. Output limits are dynamic per call (LlmService
	// passes each tool its token budget), so no context window scaling is needed here.
	// Called per sub-session turn, not per tool call, so rebuilding on demand is cheap.
	public Tool[] GetToolsForRole(Role role)
	{
		Dictionary<string, Tool> allTools = ToolFactory.Build(_webSearch);
		List<Tool> allowed = new List<Tool>();
		foreach (string name in role.Tools)
		{
			if (allTools.TryGetValue(name, out Tool? tool))
				allowed.Add(tool);
		}
		return allowed.ToArray();
	}

	// Returns the role's models filtered to those currently registered (enabled in settings).
	public List<string> GetEnabledModelsForRole(Role role)
	{
		List<string> enabled = new List<string>();
		foreach (string configId in role.Models)
		{
			if (_models.ContainsKey(configId))
			{
				enabled.Add(configId);
			}
		}
		return enabled;
	}

	// Returns 0 if any model in the role is available right now, the smallest remaining
	// rate-limit wait when all live models are backing off, and long.MaxValue only when
	// every model in the role is permanently down.
	public long GetMillisecondsUntilAvailable(Role role)
	{
		long best = long.MaxValue;
		DateTimeOffset now = DateTimeOffset.UtcNow;
		foreach (string cid in role.Models)
		{
			if (!_models.ContainsKey(cid)) continue;
			if (!_availability.TryGetValue(cid, out ModelAvailability? avail)) return 0;
			if (avail.IsDown) continue;
			long wait = (long)(avail.AvailableAt - now).TotalMilliseconds;
			if (wait <= 0) return 0;
			if (wait < best) best = wait;
		}
		return best;
	}

	// Resets all availability state — call on /reload or /clear so transient failures are retried.
	public void ResetAllAvailability()
	{
		foreach (ModelAvailability avail in _availability.Values)
		{
			avail.AvailableAt = DateTimeOffset.MinValue;
		}
	}

	// Resets availability for a single model — call on /model so the user can force a retry.
	public void ResetAvailability(string configId)
	{
		if (_availability.TryGetValue(configId, out ModelAvailability? avail))
		{
			avail.AvailableAt = DateTimeOffset.MinValue;
		}
	}

	private LlmModel? PickModel(Role? role, string preferredModelId, int minContextRequired)
	{
		if (role == null) return null;

		// Try the preferred model first if it is in the role's list and not permanently down.
		if (!string.IsNullOrEmpty(preferredModelId) && role.Models.Contains(preferredModelId))
		{
			if (_models.TryGetValue(preferredModelId, out LlmModel? preferred))
			{
				bool down = _availability.TryGetValue(preferredModelId, out ModelAvailability? pa) && pa.IsDown;
				if (!down && preferred.Config.ContextWindow > minContextRequired)
					return preferred;
			}
		}

		// Fall through to the role's ordered list.
		foreach (string modelId in role.Models)
		{
			if (!_models.TryGetValue(modelId, out LlmModel? model)) continue;
			bool down = _availability.TryGetValue(modelId, out ModelAvailability? ma) && ma.IsDown;
			if (down) continue;
			if (model.Config.ContextWindow <= minContextRequired) continue;
			return model;
		}

		return null;
	}

	private ModelAvailability GetOrCreateAvailability(string configId)
	{
		if (!_availability.TryGetValue(configId, out ModelAvailability? avail))
		{
			avail = new ModelAvailability();
			_availability[configId] = avail;
		}
		return avail;
	}

}
