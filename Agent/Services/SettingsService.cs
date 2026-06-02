using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;


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
			try
			{
				string json = File.ReadAllText(_workDirSettingsPath);
				bs = JsonSerializer.Deserialize<BeastSettings>(json);
			}
			catch (JsonException ex)
			{
				Console.Error.WriteLine($"ERROR: Failed to parse settings.json at {_workDirSettingsPath}");
				Console.Error.WriteLine($"       {ex.Message}");
				if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
				{
					Console.Error.WriteLine($"       Line {ex.LineNumber}, column {ex.BytePositionInLine}");
				}
				Console.Error.WriteLine("       Fix the JSON syntax or delete the file to use defaults.");
				Environment.Exit(1);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"ERROR: Failed to load settings.json from {_workDirSettingsPath}");
				Console.Error.WriteLine($"       {ex.Message}");
				Environment.Exit(1);
			}
		}
		if (bs == null && File.Exists(_homeDirSettingsPath))
		{
			try
			{
				string json = File.ReadAllText(_homeDirSettingsPath);
				bs = JsonSerializer.Deserialize<BeastSettings>(json);
			}
			catch (JsonException ex)
			{
				Console.Error.WriteLine($"ERROR: Failed to parse settings.json at {_homeDirSettingsPath}");
				Console.Error.WriteLine($"       {ex.Message}");
				if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
				{
					Console.Error.WriteLine($"       Line {ex.LineNumber}, column {ex.BytePositionInLine}");
				}
				Console.Error.WriteLine("       Fix the JSON syntax or delete the file to use defaults.");
				Environment.Exit(1);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"ERROR: Failed to load settings.json from {_homeDirSettingsPath}");
				Console.Error.WriteLine($"       {ex.Message}");
				Environment.Exit(1);
			}
		}

		if (bs == null)
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
		JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
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
							Id = "openrouter/free",
							Name = "Random Free Model",
							Enabled = false,
							ContextWindow = 131078,
							Cost = new CostConfig { Input = 0.0m, Output = 0.0m, CacheRead = 0.0m, CacheWrite = 0.0m },
							Extras = new Dictionary<string, JsonNode?>
							{
								{ "temperature", null },
								{ "top_p", null },
								{ "frequency_penalty", null },
							}
						}
					},
					Extras = new Dictionary<string, JsonNode?>
					{
						{ "or_provider_order", JsonValue.Create("") },
						{ "or_provider_only", JsonValue.Create("") },
						{ "or_provider_ignore", JsonValue.Create("") },
						{ "or_provider_sort", JsonValue.Create("") },
						{ "or_provider_allow_fallbacks", JsonValue.Create("") },
						{ "or_provider_require_parameters", JsonValue.Create("") },
						{ "or_provider_data_collection", JsonValue.Create("") },
						{ "or_provider_zdr", JsonValue.Create("") },
						{ "or_user", JsonValue.Create("") },
						{ "or_models", JsonValue.Create("") },
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
							MaxOutputTokens = 64000,
							Cost = new CostConfig { Input = 3.00m, Output = 15.00m, CacheWrite = 0.0m, CacheRead = 0.0m },
							Extras = new Dictionary<string, JsonNode?>
							{
							}
						}
					},
					Extras = new Dictionary<string, JsonNode?>()
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
							Extras = new Dictionary<string, JsonNode?>
							{
								{ "temperature", null },
								{ "top_p", null },
								{ "frequency_penalty", null },
							}
						}
					},
					Extras = new Dictionary<string, JsonNode?>
					{
						{ "header_OpenAI-Organization", JsonValue.Create("") },
						{ "header_OpenAI-Project", JsonValue.Create("") },
						{ "store", JsonValue.Create("") },
						{ "metadata", JsonValue.Create("") },
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
							Extras = new Dictionary<string, JsonNode?>()
						}
					},
					Extras = new Dictionary<string, JsonNode?>()
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
					Extras = new Dictionary<string, JsonNode?>
					{
						{ "plugins", new JsonArray(new JsonObject { ["id"] = "web" }) },
						{ "temperature", JsonValue.Create(0) },
						{ "max_tokens", JsonValue.Create(4096) }
					}
				}
			}
		};
	}
}
