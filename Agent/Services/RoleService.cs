using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

// Loads and provides LLMRole definitions from disk with merging: user profile as base, project as overrides.
public class RoleService
{
    public Dictionary<string, Role> Roles { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

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

    public Role? GetRole(string name)
    {
        Roles.TryGetValue(name, out Role? role);
        return role;
    }

    public void Reload()
    {
        LoadRoles();
    }

    private void LoadRoles()
    {
        Roles.Clear();

        Dictionary<string, Role>? userRoles = LoadRolesFromFile(_userRolesPath);
        if (userRoles == null)
        {
            userRoles = CreateDefaultRoles();
            WriteRoles(_userRolesPath, userRoles.Values);
        }

        Dictionary<string, Role>? projectRoles = LoadRolesFromFile(_projectRolesPath);
        if (projectRoles == null)
        {
            projectRoles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
            WriteRoles(_projectRolesPath, projectRoles.Values);
        }

        foreach (KeyValuePair<string, Role> kv in userRoles)
        {
            Roles[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, Role> kv in projectRoles)
        {
            Roles[kv.Key] = kv.Value;
        }

        ValidateStatements();
    }

    // Warns at startup if any truth value references a role that doesn't exist.
    private void ValidateStatements()
    {
        foreach (Role role in Roles.Values)
        {
            foreach (KeyValuePair<string, string> truth in role.Statements)
            {
                if (!string.IsNullOrEmpty(truth.Value) && !Roles.ContainsKey(truth.Value))
                {
                    Console.Error.WriteLine($"WARNING: Role '{role.Name}' truth '{truth.Key}' references unknown role '{truth.Value}'");
                }
            }
        }
    }

    private Dictionary<string, Role>? LoadRolesFromFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);
            List<Role>? roleList = JsonSerializer.Deserialize<List<Role>>(json);
            if (roleList != null)
            {
                Dictionary<string, Role> dict = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
                foreach (Role role in roleList)
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

    private Dictionary<string, Role> CreateDefaultRoles()
    {
        Dictionary<string, Tool> tools = ToolFactory.Build(_settings.WebSearch);
        List<string> toolNames = new List<string>(tools.Keys);

        Role defaultRole = Role.DefaultRole(toolNames);
        Role taskRole = Role.TaskRole(toolNames);
        Role toolsRole = Role.ToolsRole(toolNames);
        Dictionary<string, Role> dict = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        dict[defaultRole.Name] = defaultRole;
        dict[taskRole.Name] = taskRole;
        dict[toolsRole.Name] = toolsRole;
        return dict;
    }

    private void WriteRoles(string path, IEnumerable<Role> roles)
    {
        try
        {
            JsonSerializerOptions options = new JsonSerializerOptions() { WriteIndented = true };
            string json = JsonSerializer.Serialize(new List<Role>(roles), options);

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
