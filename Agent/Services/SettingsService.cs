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
			SaveSettings();
		}
		Settings = bs;
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
				new ProviderConfig
					{
						BaseUrl = "https://openrouter.ai/api/v1",
						ApiKey = "sk-or-v1-YOUR_API_KEY_HERE",
						Models = new List<ModelConfig>
						{
							new ModelConfig
							{
								Id = "openai/gpt-4o-mini",
								Name = "GPT-4o Mini",
								ContextWindow = 128000,
								Cost = new CostConfig { Input = 0.15m, Output = 0.60m },
								Extras = new Dictionary<string, string>
								{
									{ "temperature", "0.7" },
									// { "top_p", "1.0" },
									// { "frequency_penalty", "0.1" },
								}
							},
							new ModelConfig
							{
								Id = "anthropic/claude-sonnet-4-5",
								Name = "Claude Sonnet 4.5",
								ContextWindow = 200000,
								Cost = new CostConfig { Input = 3.00m, Output = 15.00m },
								Extras = new Dictionary<string, string>
								{
									{ "temperature", "0.7" },
									// { "top_p", "1.0" },
									// { "frequency_penalty", "0.1" },
								}
							}
						},
						Extras = new Dictionary<string, string>
						{
							// { "or_provider_order", "Anthropic,OpenAI" },
							// { "or_provider_only", "Anthropic" },
							// { "or_provider_ignore", "Together" },
							// { "or_provider_sort", "price" },
							// { "or_provider_allow_fallbacks", "true" },
							// { "or_provider_require_parameters", "true" },
							// { "or_provider_data_collection", "deny" },
							// { "or_provider_zdr", "true" },
							// { "or_user", "user-123" },
							// { "or_models", "openai/gpt-4o,anthropic/claude-3-5-haiku" },
						}
					},
					new ProviderConfig
					{
						BaseUrl = "https://api.openai.com/v1",
						ApiKey = "sk-YOUR_OPENAI_KEY_HERE",
						Models = new List<ModelConfig>
						{
							new ModelConfig
							{
								Id = "gpt-4o-mini",
								Name = "GPT-4o Mini",
								ContextWindow = 128000,
								Cost = new CostConfig { Input = 0.15m, Output = 0.60m },
								Extras = new Dictionary<string, string>
								{
									{ "temperature", "0.7" },
									// { "top_p", "1.0" },
									// { "frequency_penalty", "0.1" },
								}
							}
						},
						Extras = new Dictionary<string, string>
						{
							// Arbitrary HTTP headers — prefix with header_ and the suffix becomes the header name.
							// { "header_OpenAI-Organization", "org-YOUR_ORG_ID" },
							// { "header_OpenAI-Project", "proj-YOUR_PROJECT_ID" },
							// Any other key is injected verbatim as a top-level JSON payload field.
							// { "store", "true" },
							// { "metadata", "{\"session\":\"abc\"}" },
						}
					}
			},
			WebSearch = new WebSearchConfig
			{
				Openrouter = new OpenrouterSearchConfig
				{
					Endpoint = "https://openrouter.ai/api/v1",
					ApiKey = "sk-or-v1-YOUR_API_KEY_HERE",
					Model = "openai/gpt-4o-mini"
				}
			}
		};
	}
}

