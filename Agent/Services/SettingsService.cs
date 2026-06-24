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

	public void LoadSettings()
	{
		// 1. Load user profile settings first (base settings)
		Settings = LoadSettingsFromFile(_homeDirSettingsPath)!;
		if (Settings == null)
		{
			Settings = CreateDefaultHomeSettings();
			WriteSettings(_homeDirSettingsPath, Settings);
		}

		// 2. Load local project settings (overrides)
		BeastSettings? localSettings = LoadSettingsFromFile(_workDirSettingsPath);
		if (localSettings == null)
		{
			localSettings = CreateDefaultProjectSettings();
			WriteSettings(_workDirSettingsPath, localSettings);
		}

		// 3. Merge: local overrides user
		MergeSettings(localSettings);
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

			BeastSettings? result = JsonSerializer.Deserialize<BeastSettings>(json, ConfigJson.Options);
			return result; // may be null if JSON was empty or only null
		}
		catch (JsonException ex)
		{
			string location = ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue
				? $" (line {ex.LineNumber + 1}, column {ex.BytePositionInLine + 1})"
				: "";
			string detail = $"settings.json parse error at {path}{location}: {ex.Message}";

			Console.Error.WriteLine($"ERROR: Failed to parse {detail}");
			Console.Error.WriteLine("Fix it, or delete the file to use defaults.");
			throw new ConfigException(detail);
		}
		catch (Exception ex)
		{
			string detail = $"settings.json load error at {path}: {ex.Message}";

			Console.Error.WriteLine($"ERROR: Failed to load {detail}");
			Console.Error.WriteLine("Fix it, or delete the file to use defaults.");
			throw new ConfigException(detail);
		}
	}

	private void MergeSettings(BeastSettings local)
	{
		// Apply local overrides to the Settings object, which is pre-loaded with user settings.
		if (!string.IsNullOrWhiteSpace(local.IdleSoundFile))
			Settings.IdleSoundFile = local.IdleSoundFile;

		if (!string.IsNullOrWhiteSpace(local.SubagentSoundFile))
			Settings.SubagentSoundFile = local.SubagentSoundFile;

		// If the local settings file defines any providers, it replaces the entire list.
		if (local.Providers != null && local.Providers.Count > 0)
			Settings.Providers = local.Providers;

		// If the local settings file defines web search, it replaces the existing config.
		if (local.WebSearch != null)
			Settings.WebSearch = local.WebSearch;

		if (local.CompactionReserveTokens > 0)
			Settings.CompactionReserveTokens = local.CompactionReserveTokens;
	}

	private void WriteSettings(string path, BeastSettings settings)
	{
		try
		{
			JsonSerializerOptions options = new JsonSerializerOptions() { WriteIndented = true };
			string json = JsonSerializer.Serialize(settings, options);

			string? dir = Path.GetDirectoryName(path);
			if (dir != null)
			{
				Directory.CreateDirectory(dir);
			}

			File.WriteAllText(path, json);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"WARNING: Failed to write settings.json at {path}: {ex.Message}");
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
			Providers = new List<ProviderConfig>
			{
                // OpenRouter — multi-model gateway
                new ProviderConfig
				{
					BaseUrl = "https://openrouter.ai/api/v1/chat/completions",
					ApiKey = "YOUR_OPENROUTER_KEY_HERE",
					Models = new List<ModelConfig>
					{
						new ModelConfig
						{
							Id = "openrouter/free",
							Name = "Random Free Model",
							Enabled = false,
							ContextWindow = 131078,
							Cost = new CostConfig { Input = 0.0m, Output = 0.0m, CacheRead = 0.0m, CacheWrite = 0.0m },
                            // Reasoning level as a word (none/minimal/low/medium/high/max). For chat-completions
                            // backends it is sent as reasoning_effort and softly falls back to a reasoning object
                            // if the server rejects it, so the same word works across Gemini, vLLM, OpenRouter, etc.
                            ReasoningEffort = "",
                            // Steer OpenRouter routing by declaring a "provider" object here, e.g.
                            // { "provider": { "order": ["Anthropic"], "allow_fallbacks": false } }.
                            Extras = new List<JsonObject>
							{
								new JsonObject { ["temperature"] = null },
								new JsonObject { ["top_p"] = null },
								new JsonObject { ["frequency_penalty"] = null }
							}
						}
					}
				},
                // Anthropic — direct API
                new ProviderConfig
				{
					BaseUrl = "https://api.anthropic.com/v1/messages",
					ApiKey = "YOUR_ANTHROPIC_KEY_HERE",
					Models = new List<ModelConfig>
					{
						new ModelConfig
						{
							Id = "claude-3-5-sonnet-20241022",
							Name = "Claude 3.5 Sonnet",
							Enabled = false,
							ContextWindow = 200000,
							Cost = new CostConfig { Input = 3.0m, Output = 15.0m, CacheRead = 0.3m, CacheWrite = 3.75m },
                            // Extended thinking: set reasoningEffort to none/minimal/low/medium/high/max and the
                            // right thinking budget is chosen automatically. A raw "thinking" object in extras
                            // still overrides it.
                            ReasoningEffort = "medium",
							Extras = new List<JsonObject>
							{
								new JsonObject { ["temperature"] = null },
								new JsonObject { ["top_p"] = null }
							}
						}
					}
				},
                // OpenAI — direct API
                new ProviderConfig
				{
					BaseUrl = "https://api.openai.com/v1/responses",
					ApiKey = "YOUR_OPENAI_KEY_HERE",
					Models = new List<ModelConfig>
					{
						new ModelConfig
						{
							Id = "gpt-5-nano",
							Name = "GPT-5 Nano",
							Enabled = false,
							ContextWindow = 400000,
							Cost = new CostConfig { Input = 0.05m, Output = 0.40m, CacheRead = 0.005m, CacheWrite = 0.0m },
                            // Reasoning level as a word; translated to the API's reasoning.effort automatically.
                            ReasoningEffort = "medium",
							Extras = new List<JsonObject>
							{
								new JsonObject { ["temperature"] = null },
								new JsonObject { ["top_p"] = null },
								new JsonObject { ["frequency_penalty"] = null },
								new JsonObject { ["store"] = "" },
								new JsonObject { ["metadata"] = "" }
							},
							Headers = new List<JsonObject>
							{
								new JsonObject { ["OpenAI-Organization"] = "" },
								new JsonObject { ["OpenAI-Project"] = "" }
							}
						}
					}
				},
                // Ollama — local models
                new ProviderConfig
				{
					BaseUrl = "http://localhost:11434/v1/chat/completions",
					ApiKey = "ollama",
					Models = new List<ModelConfig>
					{
						new ModelConfig
						{
							Id = "qwen3:4b",
							Name = "Qwen3 4B",
							Enabled = false,
							ContextWindow = 32768,
							Cost = new CostConfig { Input = 0.0m, Output = 0.0m },
							Extras = new List<JsonObject>()
						}
					}
				}
			},
			WebSearch = new WebSearchConfig
			{
				Openrouter = new OpenrouterSearchConfig
				{
					Endpoint = "https://openrouter.ai/api/v1/chat/completions",
					ApiKey = "YOUR_OPENROUTER_KEY_HERE",
					Enabled = false,
					Model = "baidu/cobuddy:free",
					Extras = new List<JsonObject>
					{
						new JsonObject { ["plugins"] = new JsonArray(new JsonObject { ["id"] = "web" }) },
						new JsonObject { ["temperature"] = JsonValue.Create(0) },
						new JsonObject { ["max_tokens"] = JsonValue.Create(4096) }
					}
				}
			}
		};
	}
}