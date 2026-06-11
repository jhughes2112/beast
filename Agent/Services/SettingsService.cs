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

            BeastSettings? result = JsonSerializer.Deserialize<BeastSettings>(json);
            return result; // may be null if JSON was empty or only null
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to parse settings.json at {path}");
            Console.Error.WriteLine($"       {ex.Message}");
            if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
            {
                Console.Error.WriteLine($"       Line {ex.LineNumber}, column {ex.BytePositionInLine}");
            }
            Console.Error.WriteLine("Fix it, or delete the file to use defaults.");
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to load settings.json from {path}");
            Console.Error.WriteLine($"       {ex.Message}");
            Console.Error.WriteLine("Fix it, or delete the file to use defaults.");
            throw;
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

        if (local.CompactionReserveTokens>0)
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
        return new BeastSettings {};
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
                            Extras = new Dictionary<string, JsonNode?>
                            {
                                { "temperature", null },
                                { "top_p", null },
                                { "frequency_penalty", null }
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
                        { "or_models", JsonValue.Create("") }
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
                            Extras = new Dictionary<string, JsonNode?>
                            {
                                { "temperature", null },
                                { "top_p", null }
                            }
                        }
                    },
                    Extras = new Dictionary<string, JsonNode?>
                    {
                        { "anthropic_version", JsonValue.Create("2023-06-01") }
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
                            Extras = new Dictionary<string, JsonNode?>
                            {
                                { "temperature", null },
                                { "top_p", null },
                                { "frequency_penalty", null }
                            }
                        }
                    },
                    Extras = new Dictionary<string, JsonNode?>
                    {
                        { "header_OpenAI-Organization", JsonValue.Create("") },
                        { "header_OpenAI-Project", JsonValue.Create("") },
                        { "store", JsonValue.Create("") },
                        { "metadata", JsonValue.Create("") }
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
