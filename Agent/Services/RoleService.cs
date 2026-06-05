using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

// Loads and provides LLMRole definitions from disk with merging: user profile as base, project as overrides.
public class RoleService
{
    public Dictionary<string, LLMRole> Roles { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _projectRolesPath;
    private readonly string _userRolesPath;
    private readonly BeastSettings _settings;

    public RoleService(string workDir, BeastSettings settings)
    {
        _projectRolesPath = Path.Combine(workDir, ".beast", "roles.json");
        _userRolesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast", "roles.json");
        _settings = settings;

        LoadRoles();
    }

    public LLMRole? GetRole(string name)
    {
        Roles.TryGetValue(name, out LLMRole? role);
        return role;
    }

    public void Reload()
    {
        LoadRoles();
    }

    private void LoadRoles()
    {
        Roles.Clear();

        Dictionary<string, LLMRole>? userRoles = LoadRolesFromFile(_userRolesPath);
        if (userRoles == null)
        {
            userRoles = CreateDefaultRoles();
            WriteRoles(_userRolesPath, userRoles.Values);
        }

        Dictionary<string, LLMRole>? projectRoles = LoadRolesFromFile(_projectRolesPath);
        if (projectRoles == null)
        {
            projectRoles = new Dictionary<string, LLMRole>(StringComparer.OrdinalIgnoreCase);
            WriteRoles(_projectRolesPath, projectRoles.Values);
        }

        foreach (KeyValuePair<string, LLMRole> kv in userRoles)
        {
            Roles[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, LLMRole> kv in projectRoles)
        {
            Roles[kv.Key] = kv.Value;
        }
    }

    private Dictionary<string, LLMRole>? LoadRolesFromFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);
            List<LLMRole>? roleList = JsonSerializer.Deserialize<List<LLMRole>>(json);
            if (roleList != null)
            {
                Dictionary<string, LLMRole> dict = new Dictionary<string, LLMRole>(StringComparer.OrdinalIgnoreCase);
                foreach (LLMRole role in roleList)
                {
                    dict[role.Name] = role;
                }
                return dict;
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to parse roles.json at {path}");
            Console.Error.WriteLine($"       {ex.Message}");
            if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
            {
                Console.Error.WriteLine($"       Line {ex.LineNumber}, column {ex.BytePositionInLine}");
            }
            Console.Error.WriteLine("Fix the JSON syntax or delete the file to use defaults.");
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to load roles.json from {path}");
            Console.Error.WriteLine($"       {ex.Message}");
            Environment.Exit(1);
        }

        return null;
    }

    private Dictionary<string, LLMRole> CreateDefaultRoles()
    {
        List<string> modelIds = new List<string>();
        if (_settings.Providers != null)
        {
            foreach (ProviderConfig provider in _settings.Providers)
            {
                if (provider.Models != null)
                {
                    foreach (ModelConfig model in provider.Models)
                    {
                        if (!string.IsNullOrEmpty(model.Id))
                        {
                            modelIds.Add(model.Id);
                        }
                    }
                }
            }
        }
        modelIds.Add("*"); // wildcard to allow any model, for future-proofing and flexibility

        Dictionary<string, Tool> tools = ToolFactory.Build(_settings.WebSearch);
        List<string> toolNames = new List<string>(tools.Keys);

        LLMRole defaultRole = LLMRole.DefaultRole(modelIds, toolNames);
        Dictionary<string, LLMRole> dict = new Dictionary<string, LLMRole>(StringComparer.OrdinalIgnoreCase);
        dict[defaultRole.Name] = defaultRole;
        return dict;
    }

    private void WriteRoles(string path, IEnumerable<LLMRole> roles)
    {
        try
        {
            JsonSerializerOptions options = new JsonSerializerOptions() { WriteIndented = true };
            string json = JsonSerializer.Serialize(new List<LLMRole>(roles), options);

            string? dir = Path.GetDirectoryName(path);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: Failed to write roles.json at {path}: {ex.Message}");
        }
    }
}
