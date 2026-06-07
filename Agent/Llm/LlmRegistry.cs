using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;


// Flat registry of LLM models and their live service instances, keyed by config ID.
// Model metadata and service instances are kept in separate dictionaries so that
// reloading config never destroys service state (availability, down-timers, etc.).
// LlmService is downstream of LlmModel; the registry patches the reference on reload.
public class LlmRegistry
{
	private readonly Dictionary<string, LlmModel> _models = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, LlmService> _services = new(StringComparer.OrdinalIgnoreCase);

	private Dictionary<string, Tool> _tools = new(StringComparer.OrdinalIgnoreCase);
	private Dictionary<string, Tool[]> _toolsByRole = new(StringComparer.OrdinalIgnoreCase);

	public LlmRegistry()
	{
	}

	// Rebuilds the model metadata dictionary from fresh configs.
	// For each config ID, if a live service already exists it is patched with the new model
	// so availability state is preserved. New services are created for new config IDs.
	// Services for config IDs no longer present are kept so their availability state survives
	// transient config edits; they will be patched back in if the config returns.
	public void LoadFromConfigs(SettingsService settings, RoleService roles)
	{
		_tools = ToolFactory.Build(settings.Settings.WebSearch);
		_models.Clear();
		_toolsByRole.Clear();

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

		// Add missing services; existing ones are left as-is to preserve availability state.
		foreach (LlmModel model in _models.Values)
		{
			if (!_services.ContainsKey(model.ConfigId))
			{
				_services[model.ConfigId] = new LlmService(model);
			}
			else
			{
				_services[model.ConfigId].UpdateModel(model);
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

		foreach (Role role in roles.Roles.Values)
		{
			_toolsByRole[role.Name] = BuildToolsForRole(role);
		}
	}

	// Finds the first available service from the role's preferred model list, in order.
	// Skips models that are unavailable or whose context window is too small for minContextRequired.
	public LlmService? GetServiceForRole(Role? role, string preferredModelId, int minContextRequired)
	{
		LlmService? service = null;
		if (role!=null)
		{
			if (role.Models.Contains(preferredModelId))  // if the current model is in the list, continue using it
			{
				if (_services.TryGetValue(preferredModelId, out LlmService? svc) && !svc.IsDown && svc.Model.Config.ContextWindow > minContextRequired)
				{
					service = svc;
				}
			}
			if (service == null)
			{
				foreach (string modelId in role.Models)  // nope, try them in order
				{
                    service = GetServiceById(modelId, minContextRequired);
                    if (service!=null)
                        break;
                }
			}
		}
		return service;
	}

	// Returns the prebuilt Tool array for the given role.
	public Tool[] GetToolsForRole(Role role)
	{
		return _toolsByRole[role.Name];
	}

	// Returns the role's models filtered to those currently registered (enabled in settings),
	// preserving the role's preference order. Disabled or unknown models are dropped so callers
	// like the /model picker never offer a model the agent cannot actually use.
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

	// Returns the live service for a specific model ID, or null if not registered.
	public LlmService? GetServiceById(string modelId, int minContextRequired)
	{
		if (_services.TryGetValue(modelId, out LlmService? svc) && !svc.IsDown && svc.Model.Config.ContextWindow > minContextRequired)
			return svc;

		return null;
	}

	// Returns 0 if any service for the role is not permanently down; MaxValue if all are.
	// Timed backoffs are resolved inside WaitUntilReady — callers use this only to decide
	// how long to idle before retrying when no service is usable at all.
	public long GetMillisecondsUntilAvailable(Role role)
	{
		foreach (string cid in role.Models)
		{
			if (_services.TryGetValue(cid, out LlmService? svc) && !svc.IsDown)
				return 0;
		}
		return long.MaxValue;
	}


	// Call when the user signals intent to retry: /reload, /clear.
	public void ResetAllAvailability()
	{
		foreach (LlmService svc in _services.Values)
		{
			svc.ResetAvailability();
		}
	}

	// Resets availability on a single service by model config id.
	// Call when the user explicitly selects a specific model via /model.
	public void ResetAvailability(string configId)
	{
		if (_services.TryGetValue(configId, out LlmService? svc))
		{
			svc.ResetAvailability();
		}
	}

	private Tool[] BuildToolsForRole(Role role)
	{
		List<Tool> allowed = new();

		foreach (string name in role.Tools)
		{
			if (_tools.TryGetValue(name, out Tool? tool))
			{
				allowed.Add(tool);
			}
		}

		return allowed.ToArray();
	}
}


