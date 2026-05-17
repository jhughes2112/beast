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

	public RoleService(string workDir)
	{
		_projectRolesPath = Path.Combine(workDir, ".beast", "roles.json");
		_globalRolesPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".beast", "roles.json");

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

		LLMRole defaultRole = LLMRole.DefaultRole();
		Roles[defaultRole.Name] = defaultRole;

		JsonSerializerOptions options = new() { WriteIndented = true };
		string defaultJson = JsonSerializer.Serialize(new List<LLMRole> { defaultRole }, options);
		Directory.CreateDirectory(Path.GetDirectoryName(_projectRolesPath)!);
		File.WriteAllText(_projectRolesPath, defaultJson);
	}
}
