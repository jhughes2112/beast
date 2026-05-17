using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;


// Loads and manages beast settings from multiple locations with priority.
// Priority: workDir > homeDir
public class SettingsService
{
	private readonly string _workDirSettingsPath;
	private readonly string _homeDirSettingsPath;

	private static string kCompactionPrompt = "Your task is to create a detailed summary of the conversation so far, paying close attention to the user's explicit requests and your previous actions. This summary should be thorough in capturing technical details, code patterns, and architectural decisions that would be essential for continuing development work without losing context.";
	private static string kContinueMessage = "Are you done? If finished, respond accordingly.";

	public BeastSettings Settings { get; private set; }

	public SettingsService(string workDir)
	{
		_workDirSettingsPath = Path.Combine(workDir, ".beast", "settings.json");
		_homeDirSettingsPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".beast", "settings.json");

		Settings = null!;  // shut up the warning, LoadSettings always assigns it.
		LoadSettings();
	}

	public void LoadSettings()
	{
		BeastSettings? bs = null;
		if (File.Exists(_workDirSettingsPath))
		{
			string json = File.ReadAllText(_workDirSettingsPath);
			bs = JsonSerializer.Deserialize<BeastSettings>(json);
		}
		if (bs==null && File.Exists(_homeDirSettingsPath))
		{
			string json = File.ReadAllText(_homeDirSettingsPath);
			bs = JsonSerializer.Deserialize<BeastSettings>(json);
		}

		if (bs==null)
		{
			bs = CreateDefaultSettings();
			Settings = bs;
			SaveSettings();
		}
		else
		{
			Settings = bs;
		}
		if (string.IsNullOrWhiteSpace(bs.CompactionPrompt))
			bs.CompactionPrompt = kCompactionPrompt;
		if (string.IsNullOrWhiteSpace(bs.ContinueMessage))
			bs.ContinueMessage = kContinueMessage;
	}

	public void SaveSettings()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_workDirSettingsPath)!);
		JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
		string json = JsonSerializer.Serialize(Settings, options);
		File.WriteAllText(_workDirSettingsPath, json);
	}

	private static BeastSettings CreateDefaultSettings()
	{
		return new BeastSettings
		{
			CompactionPrompt = kCompactionPrompt,
			ContinueMessage = kContinueMessage,
			LastSessionId = null,
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
							Id = "baidu/cobuddy:free",
							Name = "baidu/cobuddy:free",
							Enabled = false,
							ContextWindow = 131078,
							Cost = new CostConfig { Input = 0.0m, Output = 0.0m, CacheRead = 0.0m, CacheWrite = 0.0m },
							Extras = new Dictionary<string, string>
							{
								{ "temperature", "" },
								{ "top_p", "" },
								{ "frequency_penalty", "" },
							}
						}
					},
					Extras = new Dictionary<string, string>
					{
						{ "or_provider_order", "" },
						{ "or_provider_only", "" },
						{ "or_provider_ignore", "" },
						{ "or_provider_sort", "" },
						{ "or_provider_allow_fallbacks", "" },
						{ "or_provider_require_parameters", "" },
						{ "or_provider_data_collection", "" },
						{ "or_provider_zdr", "" },
						{ "or_user", "" },
						{ "or_models", "" },
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
							Id = "claude-sonnet-4-5",
							Name = "Claude Sonnet 4.5",
							Enabled = false,
							ContextWindow = 200000,
							Cost = new CostConfig { Input = 3.00m, Output = 15.00m, CacheWrite = 0.0m, CacheRead = 0.0m },
							Extras = new Dictionary<string, string>
							{
							}
						}
					},
					Extras = new Dictionary<string, string>()
				},
				// OpenAI — direct API (Responses protocol)
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
							Extras = new Dictionary<string, string>
							{
								{ "temperature", "" },
								{ "top_p", "" },
								{ "frequency_penalty", "" },
							}
						}
					},
					Extras = new Dictionary<string, string>
					{
						{ "header_OpenAI-Organization", "" },
						{ "header_OpenAI-Project", "" },
						{ "store", "" },
						{ "metadata", "" },
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
							Extras = new Dictionary<string, string>()
						}
					},
					Extras = new Dictionary<string, string>()
				}
			},
			WebSearch = new WebSearchConfig
			{
				Openrouter = new OpenrouterSearchConfig
				{
					Endpoint = "https://openrouter.ai/api/v1/chat/completions",
					ApiKey = "YOUR_OPENROUTER_KEY_HERE",
					Enabled = false,
					Model = "baidu/cobuddy:free"
				}
			}
		};
	}
}
