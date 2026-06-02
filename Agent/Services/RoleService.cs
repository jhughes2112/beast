using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;


// Loads and provides LLMRole definitions from disk. Read-only at runtime.
public class RoleService
{
	public Dictionary<string, LLMRole> Roles { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

	private readonly string _projectRolesPath;
	private readonly string _globalRolesPath;

	private BeastSettings _settings;

	public RoleService(string workDir, BeastSettings settings)
	{
		_projectRolesPath = Path.Combine(workDir, ".beast", "roles.json");
		_globalRolesPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".beast", "roles.json");
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
		string? sourcePath = null;

		if (File.Exists(_projectRolesPath))
		{
			sourcePath = _projectRolesPath;
		}
		else if (File.Exists(_globalRolesPath))
		{
			sourcePath = _globalRolesPath;
		}

		if (sourcePath != null)
		{
			try
			{
				string json = File.ReadAllText(sourcePath);
				List<LLMRole>? roleList = JsonSerializer.Deserialize<List<LLMRole>>(json);
				if (roleList != null)
				{
					foreach (LLMRole role in roleList)
					{
						Roles[role.Name] = role;
					}
				}
				return;
			}
			catch (JsonException ex)
			{
				Console.Error.WriteLine($"ERROR: Failed to parse roles.json at {sourcePath}");
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
				Console.Error.WriteLine($"ERROR: Failed to load roles.json from {sourcePath}");
				Console.Error.WriteLine($"       {ex.Message}");
				Environment.Exit(1);
			}
		}

		string firstModelId = string.Empty;
		if (_settings.Providers != null)
		{
			foreach (ProviderConfig provider in _settings.Providers)
			{
				if (provider.Models != null && provider.Models.Count > 0)
				{
					firstModelId = provider.Models[0].Id ?? string.Empty;
					break;
				}
			}
		}

		Dictionary<string, Tool> tools = ToolFactory.Build(_settings.WebSearch);
		List<string> toolNames = new List<string>(tools.Keys);

		LLMRole defaultRole = LLMRole.DefaultRole(firstModelId, toolNames);
		Roles[defaultRole.Name] = defaultRole;

		JsonSerializerOptions options = new() { WriteIndented = true };
		string defaultJson = JsonSerializer.Serialize(new List<LLMRole> { defaultRole }, options);
		Directory.CreateDirectory(Path.GetDirectoryName(_projectRolesPath)!);
		File.WriteAllText(_projectRolesPath, defaultJson);
	}
}
