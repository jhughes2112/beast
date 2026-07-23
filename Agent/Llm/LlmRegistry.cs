using System;
using System.Collections.Generic;
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
	// Replaced wholesale by LoadFromConfigs (a reference swap, so /reload never exposes a
	// half-filled set to sessions that are reading concurrently); entries may be rewritten in
	// place by ProbeEndpointsAsync's localhost fallback.
	private Dictionary<string, LlmModel> _models = new(StringComparer.OrdinalIgnoreCase);

	// Keyed by endpoint URL. Populated by ProbeEndpointsAsync; never cleared on reload so
	// expensive probes survive /reload. Unknown entries mean the endpoint was unreachable.
	private readonly Dictionary<string, DetectedProtocol> _probeCache = new(StringComparer.OrdinalIgnoreCase);

	// Keyed by model config ID. Populated on demand; never cleared so availability state
	// (rate-limit timers, permanently-down flags) survives /reload and session restarts.
	private readonly Dictionary<string, ModelAvailability> _availability = new(StringComparer.OrdinalIgnoreCase);

	public LlmRegistry()
	{
	}

	// Loads model configs from settings. Call this first, then ProbeEndpointsAsync.
	// Models already in _availability keep their existing down/rate-limit state across reloads.
	public void LoadFromConfigs(SettingsService settings, RoleService roles)
	{
		// Build aside and publish with one reference swap, so a session picking a model during a
		// /reload sees the complete old set or the complete new one — never an empty dictionary.
		Dictionary<string, LlmModel> fresh = new(StringComparer.OrdinalIgnoreCase);

		foreach (ProviderConfig provider in settings.Settings.Providers)
		{
			string endpoint = provider.BaseUrl.TrimEnd('/');
			foreach (ModelConfig modelConfig in provider.Models)
			{
				if (!modelConfig.Enabled)
					continue;

				LlmModel model = new LlmModel(modelConfig.Id, endpoint, provider.ApiKey, modelConfig.Extras, modelConfig.Headers, modelConfig);
				fresh[modelConfig.Id] = model;
			}
		}
		_models = fresh;

		// Expand '*' in role model lists to all enabled model IDs at that position.
		List<string> allModelIds = new List<string>(fresh.Keys);
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
		foreach ((string modelId, LlmModel model) in _models)
		{
			string ep = model.Endpoint;
			if (_probeCache.ContainsKey(ep))
				continue;
			if (!unprobed.ContainsKey(ep))
				unprobed[ep] = (model.ApiKey, new List<string>());
			unprobed[ep].modelIds.Add(modelId);
		}

		// Kick off all the probes simultaneously, so they all happen in parallel.
		Dictionary<string, Task<(DetectedProtocol, string)>> detectionTasks = new Dictionary<string, Task<(DetectedProtocol, string)>>();
		foreach ((string originalEndpoint, _) in unprobed)
		{
			detectionTasks.Add(originalEndpoint, ProtocolProxy.ProbeEndpointAsync(originalEndpoint, ct));
		}
		foreach ((string originalEndpoint, (string _, List<string> modelIds)) in unprobed)
		{
			Console.WriteLine($"Probing {originalEndpoint}");
			(DetectedProtocol detected, string effectiveEndpoint) = await detectionTasks[originalEndpoint];
			Console.WriteLine($"Probed {originalEndpoint} -> protocol={detected}, effective={effectiveEndpoint}");
			_probeCache[originalEndpoint] = detected;

			if (effectiveEndpoint != originalEndpoint)
			{
				// Localhost fallback fired: rewrite stored models to the effective endpoint and
				// cache it separately so future models at the same URL match immediately.
				_probeCache[effectiveEndpoint] = detected;
				foreach (string modelId in modelIds)
				{
					if (_models.TryGetValue(modelId, out LlmModel? old))
						_models[modelId] = new LlmModel(old.ConfigId, effectiveEndpoint, old.ApiKey, old.Extras, old.Headers, old.Config);
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
		if (role == null)
			return null;

		LlmModel? model = PickModel(role, preferredModelId, minContextRequired);
		if (model == null)
			return null;

		DetectedProtocol protocol = DetectedProtocol.Unknown;
		_probeCache.TryGetValue(model.Endpoint, out protocol);
		return new LlmService(model, protocol, GetOrCreateAvailability(model.ConfigId), role.Models);
	}

	// Builds a service for the next model below the current one in its role list — the automatic equivalent
	// of the user typing /model <next> after a model is sustained-rate-limited. Resumes just past the current
	// model and skips any that is gone, too small, or itself down/backing off, so the fallback lands on one
	// that can serve now. The new service carries the same list, so it can fall back again. Returns null when
	// the list is exhausted, at which point the caller surfaces the rate-limit failure.
	public LlmService? CreateFallbackService(LlmService current, int minContextRequired)
	{
		IReadOnlyList<string> modelIds = current.RoleModelIds;
		DateTimeOffset now = DateTimeOffset.UtcNow;

		int start = 0;
		for (int i = 0; i < modelIds.Count; i++)
		{
			if (string.Equals(modelIds[i], current.Model.ConfigId, StringComparison.OrdinalIgnoreCase))
			{
				start = i + 1;
				break;
			}
		}

		for (int i = start; i < modelIds.Count; i++)
		{
			string modelId = modelIds[i];
			if (!_models.TryGetValue(modelId, out LlmModel? model))
				continue;
			if (_availability.TryGetValue(modelId, out ModelAvailability? avail) && (avail.IsDown || avail.AvailableAt > now))
				continue;
			if (model.Config.ContextWindow <= minContextRequired)
				continue;

			DetectedProtocol protocol = DetectedProtocol.Unknown;
			_probeCache.TryGetValue(model.Endpoint, out protocol);
			return new LlmService(model, protocol, GetOrCreateAvailability(modelId), modelIds);
		}

		return null;
	}

	// Creates a fresh LlmService for a specific model ID regardless of role ordering.
	// Used by tests that need to exercise a particular model directly. Its model list is just that one
	// model, so the service never falls back — the test exercises exactly that model.
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
		return new LlmService(model, protocol, avail, new List<string> { modelId });
	}

	// Returns the best available model for the role without creating a service.
	// Use when only the model config (name, context window, etc.) is needed.
	public LlmModel? GetModelForRole(Role? role, string preferredModelId, int minContextRequired)
	{
		return PickModel(role, preferredModelId, minContextRequired);
	}

	// Returns the registered model for a config ID, or null if it is not enabled/known.
	public LlmModel? GetModel(string configId)
	{
		_models.TryGetValue(configId, out LlmModel? model);
		return model;
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
			if (!_models.ContainsKey(cid))
				continue;
			if (!_availability.TryGetValue(cid, out ModelAvailability? avail))
				return 0;
			if (avail.IsDown)
				continue;
			long wait = (long)(avail.AvailableAt - now).TotalMilliseconds;
			if (wait <= 0)
				return 0;
			if (wait < best)
				best = wait;
		}
		return best;
	}

	// True when the model is registered, not permanently down, and not inside a rate-limit backoff
	// — i.e. a request to it right now would actually be attempted. Used to decide when a session
	// can return to its user-selected model after a fallback.
	public bool IsAvailableNow(string configId)
	{
		if (!_models.ContainsKey(configId))
			return false;
		if (_availability.TryGetValue(configId, out ModelAvailability? avail))
			return !avail.IsDown && avail.AvailableAt <= DateTimeOffset.UtcNow;
		return true;
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
		if (role == null)
			return null;

		// Try the preferred model first if it is in the role's list and not permanently down. A
		// temporary backoff does NOT disqualify it: an explicitly chosen model waits out its rate
		// limit rather than being silently swapped for a different one. Membership is checked
		// case-insensitively — ids come from two user-edited files (roles.json, settings.json)
		// whose casing can disagree, and an ordinal mismatch here silently discarded the choice.
		if (!string.IsNullOrEmpty(preferredModelId))
		{
			bool inRole = false;
			foreach (string id in role.Models)
			{
				if (string.Equals(id, preferredModelId, StringComparison.OrdinalIgnoreCase))
				{
					inRole = true;
					break;
				}
			}
			if (inRole && _models.TryGetValue(preferredModelId, out LlmModel? preferred))
			{
				bool down = _availability.TryGetValue(preferredModelId, out ModelAvailability? pa) && pa.IsDown;
				if (!down && preferred.Config.ContextWindow > minContextRequired)
					return preferred;
			}
		}

		// Fall through to the role's ordered list: the first model that can serve RIGHT NOW. One
		// still in a temporary backoff is remembered but passed over, so a fresh selection does not
		// park behind another session's rate limit while a lower-ranked model sits idle. Only when
		// every live model is backing off is the one that recovers SOONEST returned — the caller
		// then waits out the shortest backoff instead of the selection failing outright.
		DateTimeOffset now = DateTimeOffset.UtcNow;
		LlmModel? backingOff = null;
		DateTimeOffset backingOffAt = DateTimeOffset.MaxValue;
		foreach (string modelId in role.Models)
		{
			if (!_models.TryGetValue(modelId, out LlmModel? model))
				continue;
			if (model.Config.ContextWindow <= minContextRequired)
				continue;
			if (_availability.TryGetValue(modelId, out ModelAvailability? ma))
			{
				if (ma.IsDown)
					continue;
				if (ma.AvailableAt > now)
				{
					if (ma.AvailableAt < backingOffAt)
					{
						backingOff = model;
						backingOffAt = ma.AvailableAt;
					}
					continue;
				}
			}
			return model;
		}

		return backingOff;
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