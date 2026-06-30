using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;


// Persists conversation sessions as JSON files under /workspace/.beast/sessions/.
// Each session is one file with a friendly name derived from the worktree branch name.
// Root session: "{sanitizedBranchName}.json"
// Child sessions: "{sanitizedBranchName}_child-{N}.json"
// Session IDs (used internally for parent-child tree logic) remain in "parentId_N" format.
// A manifest file maps friendly filenames to internal session IDs.
public static class SessionService
{
	private static string SessionsDir => Path.Combine("/workspace", ".beast", "sessions");
	private static string ManifestFile => Path.Combine(SessionsDir, ".manifest.json");

	// Manifest entry: maps friendly filename to internal session metadata
	private class ManifestEntry
	{
		[System.Text.Json.Serialization.JsonPropertyName("sessionId")]
		public string SessionId { get; set; } = string.Empty;

		[System.Text.Json.Serialization.JsonPropertyName("parentId")]
		public string? ParentId { get; set; }

		[System.Text.Json.Serialization.JsonPropertyName("displayName")]
		public string DisplayName { get; set; } = string.Empty;
	}

	// Full manifest on disk
	private class SessionManifest
	{
		[System.Text.Json.Serialization.JsonPropertyName("entries")]
		public Dictionary<string, ManifestEntry> Entries { get; set; } = new Dictionary<string, ManifestEntry>();

		[System.Text.Json.Serialization.JsonPropertyName("nextCreationOrder")]
		public long NextCreationOrder { get; set; } = 1;
	}

	// Load the manifest from disk, or return empty if not present
	private static SessionManifest LoadManifest()
	{
		if (!File.Exists(ManifestFile))
			return new SessionManifest();
		try
		{
			string json = File.ReadAllText(ManifestFile);
			var result = JsonSerializer.Deserialize<SessionManifest>(json);
			return result ?? new SessionManifest();
		}
		catch
		{
			return new SessionManifest();
		}
	}

	// Save the manifest to disk
	private static void SaveManifest(SessionManifest manifest)
	{
		Directory.CreateDirectory(SessionsDir);
		string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
		File.WriteAllText(ManifestFile, json);
	}

	// Generate a friendly filename for a root session based on worktree branch name
	private static string GetRootFriendlyName()
	{
		// Derive from the git branch name at /workspace
		string branch = GetWorktreeBranchName();
		if (string.IsNullOrEmpty(branch))
			branch = Guid.NewGuid().ToString("N").Substring(0, 8);
		return SanitizeFilename(branch);
	}

	// Generate a friendly filename for a child session
	private static string GetChildFriendlyName(string parentFriendlyName, int childNumber)
	{
		return $"{parentFriendlyName}_child-{childNumber}";
	}

	// Sanitize a name to be safe for use as a filename
	private static string SanitizeFilename(string name)
	{
		char[] chars = new char[name.Length];
		int len = 0;
		foreach (char c in name)
		{
			if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
				chars[len++] = c;
			else if (c == '/')
				chars[len++] = '-';
		}
		return new string(chars, 0, Math.Max(len, 1));
	}

	// Get the worktree branch name from git
	private static string GetWorktreeBranchName()
	{
		try
		{
			var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = "git",
				Arguments = "-C /workspace rev-parse --abbrev-ref HEAD",
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			process?.WaitForExit();
			string result = process?.StandardOutput.ReadToEnd().Trim() ?? string.Empty;
			return string.IsNullOrEmpty(result) ? string.Empty : result;
		}
		catch
		{
			return string.Empty;
		}
	}

	// Find the friendly filename for a given session ID, or null if not found
	private static string? FindFriendlyName(string sessionId)
	{
		var manifest = LoadManifest();
		foreach (var kvp in manifest.Entries)
		{
			if (kvp.Value.SessionId == sessionId)
				return kvp.Key;
		}
		return null;
	}

	// Find the session ID for a given friendly filename, or null if not found
	private static string? FindSessionId(string friendlyName)
	{
		var manifest = LoadManifest();
		if (manifest.Entries.TryGetValue(friendlyName, out var entry))
			return entry.SessionId;
		return null;
	}

	// Save a session to disk. Ephemeral sessions are not persisted.
	// The session is saved with a friendly filename.
	public static void Save(BeastSession data)
	{
		if (data.Ephemeral)
			return;
		if (string.IsNullOrEmpty(data.DisplayName))
			return;
		try
		{
			Directory.CreateDirectory(SessionsDir);

			var manifest = LoadManifest();
			string friendlyName;

			// Check if this session already has a friendly name in the manifest
			string? existingFriendly = FindFriendlyName(data.Id);
			if (!string.IsNullOrEmpty(existingFriendly))
			{
				friendlyName = existingFriendly;
			}
			else
			{
				// Determine if this is a root or child session
				if (IsRootSessionId(data.Id))
				{
					// New root session: assign creation order from manifest counter
					if (data.CreationOrder == 0)
					{
						data.CreationOrder = manifest.NextCreationOrder++;
					}
					friendlyName = GetRootFriendlyName();
					// If the name is already taken by another session, add a suffix
					if (manifest.Entries.ContainsKey(friendlyName) &&
						manifest.Entries[friendlyName].SessionId != data.Id)
					{
						int suffix = 1;
						while (manifest.Entries.ContainsKey($"{friendlyName}-{suffix}"))
							suffix++;
						friendlyName = $"{friendlyName}-{suffix}";
					}
				}
				else
				{
					// Child session: extract parent ID and find parent's friendly name
					string parentId = ExtractParentId(data.Id);
					string? parentFriendly = FindFriendlyName(parentId);
					if (string.IsNullOrEmpty(parentFriendly))
					{
						// Fallback: use the root friendly name
						parentFriendly = GetRootFriendlyName();
					}
					// Extract child number from session ID (parentId_N)
					int childNum = ExtractChildNumber(data.Id);
					// Child's creation order is its child number
					if (data.CreationOrder == 0)
					{
						data.CreationOrder = childNum;
					}
					friendlyName = GetChildFriendlyName(parentFriendly, childNum);
				}

				// Register in manifest
				manifest.Entries[friendlyName] = new ManifestEntry
				{
					SessionId = data.Id,
					ParentId = IsRootSessionId(data.Id) ? null : ExtractParentId(data.Id),
					DisplayName = data.DisplayName
				};
			}

			string path = Path.Combine(SessionsDir, friendlyName + ".json");
			string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
			File.WriteAllText(path, json);
			SaveManifest(manifest);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[SessionService] Save failed for {data.Id}: {ex}");
		}
	}

	// Check if a session ID is a root (no underscore, i.e., a bare GUID)
	private static bool IsRootSessionId(string sessionId)
	{
		return !sessionId.Contains('_');
	}

	// Extract the parent ID from a child session ID (e.g., "parentId_1" -> "parentId", "parentId_1_2" -> "parentId_1")
	private static string ExtractParentId(string sessionId)
	{
		int lastUnderscore = sessionId.LastIndexOf('_');
		if (lastUnderscore <= 0)
			return sessionId;
		return sessionId.Substring(0, lastUnderscore);
	}

	// Extract the child number from a session ID (e.g., "parentId_5" -> 5)
	private static int ExtractChildNumber(string sessionId)
	{
		int lastUnderscore = sessionId.LastIndexOf('_');
		if (lastUnderscore < 0 || lastUnderscore == sessionId.Length - 1)
			return 0;
		if (int.TryParse(sessionId.Substring(lastUnderscore + 1), out int num))
			return num;
		return 0;
	}

	// Load a session by its internal session ID
	public static BeastSession? Load(string sessionId)
	{
		// First check the manifest for the friendly filename
		var manifest = LoadManifest();
		string? friendlyName = FindFriendlyName(sessionId);

		if (!string.IsNullOrEmpty(friendlyName))
		{
			string path = Path.Combine(SessionsDir, friendlyName + ".json");
			if (File.Exists(path))
			{
				try
				{
					string json = File.ReadAllText(path);
					return JsonSerializer.Deserialize<BeastSession>(json);
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"[SessionService] Failed to load session {sessionId}: {ex}");
				}
			}
		}

		// Fallback: try loading by session ID as filename (backward compatibility with old GUID-based files)
		string oldPath = Path.Combine(SessionsDir, sessionId + ".json");
		if (File.Exists(oldPath))
		{
			try
			{
				string json = File.ReadAllText(oldPath);
				return JsonSerializer.Deserialize<BeastSession>(json);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[SessionService] Failed to load session {sessionId}: {ex}");
			}
		}

		return null;
	}

	public static BeastSession? LoadBySessionId(string? sessionId)
	{
		if (string.IsNullOrEmpty(sessionId))
			return null;
		return Load(sessionId);
	}

	// List ALL sessions (roots and children) with full metadata including parentId
	public static List<(string id, string displayName, int messageCount, string? parentId)> List()
	{
		List<(string, string, int, string?)> results = new List<(string, string, int, string?)>();
		if (!Directory.Exists(SessionsDir))
			return results;

		var manifest = LoadManifest();

		foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
		{
			string fileName = Path.GetFileNameWithoutExtension(file);
			// Skip manifest file
			if (fileName == ".manifest")
				continue;

			try
			{
				string json = File.ReadAllText(file);
				BeastSession? data = JsonSerializer.Deserialize<BeastSession>(json);
				if (data == null)
					continue;

				// Get parent ID from manifest if available
				string? parentId = null;
				if (manifest.Entries.TryGetValue(fileName, out var entry))
				{
					parentId = entry.ParentId;
				}
				else
				{
					// Derive from session ID format: if ID contains '_', it's a child
					if (!IsRootSessionId(data.Id))
					{
						parentId = ExtractParentId(data.Id);
					}
				}

				results.Add((data.Id, data.DisplayName, data.Messages.Count, parentId));
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[SessionService] Failed to list session {file}: {ex}");
			}
		}

		return results;
	}

	// Session info with creation order for sorting
	public class SessionFileInfo
	{
		public BeastSession Session { get; set; } = null!;
		public long CreationOrder { get; set; }
	}

	// Load ALL sessions for the worktree (both roots and children), returning fully populated BeastSession objects
	// with creation order for sorting. Used on startup to announce all sessions to the client.
	public static List<SessionFileInfo> LoadAll()
	{
		List<SessionFileInfo> results = new List<SessionFileInfo>();
		if (!Directory.Exists(SessionsDir))
			return results;

		foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
		{
			string fileName = Path.GetFileNameWithoutExtension(file);
			// Skip manifest file
			if (fileName == ".manifest")
				continue;

			try
			{
				string json = File.ReadAllText(file);
				BeastSession? data = JsonSerializer.Deserialize<BeastSession>(json);
				if (data != null)
				{
					results.Add(new SessionFileInfo { Session = data, CreationOrder = data.CreationOrder });
				}
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[SessionService] Failed to load session {file}: {ex}");
			}
		}

		return results;
	}

	// List ALL sessions that belong to a specific root session tree (including the root itself).
	// Returns tuples of (sessionId, displayName, messageCount, parentId).
	// This allows the Beast F10 menu to build the full session tree for a worktree.
	public static List<(string id, string displayName, int messageCount, string? parentId)> ListAll(string rootId)
	{
		List<(string, string, int, string?)> results = new List<(string, string, int, string?)>();
		if (!Directory.Exists(SessionsDir))
			return results;

		var manifest = LoadManifest();
		string childPrefix = rootId + "_";

		foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
		{
			string fileName = Path.GetFileNameWithoutExtension(file);
			// Skip manifest file
			if (fileName == ".manifest")
				continue;

			try
			{
				string json = File.ReadAllText(file);
				BeastSession? data = JsonSerializer.Deserialize<BeastSession>(json);
				if (data == null)
					continue;

				// Only include sessions that belong to this root's tree
				if (!string.Equals(data.Id, rootId, StringComparison.Ordinal) && !data.Id.StartsWith(childPrefix, StringComparison.Ordinal))
					continue;

				// Get parent ID from manifest if available
				string? parentId = null;
				if (manifest.Entries.TryGetValue(fileName, out var entry))
				{
					parentId = entry.ParentId;
				}
				else
				{
					// Derive from session ID format
					if (!IsRootSessionId(data.Id))
					{
						parentId = ExtractParentId(data.Id);
					}
				}

				results.Add((data.Id, data.DisplayName, data.Messages.Count, parentId));
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[SessionService] Failed to list session {file}: {ex}");
			}
		}

		return results;
	}

	// Returns the session IDs of every descendant session of a root: child files at any depth.
	// The root itself is excluded. Used on resume to surface a root's saved children in the session list.
	public static List<string> ListDescendants(string rootId)
	{
		List<string> ids = new List<string>();
		if (string.IsNullOrWhiteSpace(rootId) || !IsSafeSessionId(rootId) || !Directory.Exists(SessionsDir))
			return ids;

		string childPrefix = rootId + "_";
		foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
		{
			string fileName = Path.GetFileNameWithoutExtension(file);
			if (fileName == ".manifest")
				continue;

			// Check both the filename and the session data
			var manifest = LoadManifest();
			string? sessionId = null;
			if (manifest.Entries.TryGetValue(fileName, out var entry))
				sessionId = entry.SessionId;

			if (string.IsNullOrEmpty(sessionId))
			{
				// Fallback: try to load the file to get the session ID
				try
				{
					string json = File.ReadAllText(file);
					var data = JsonSerializer.Deserialize<BeastSession>(json);
					if (data != null)
						sessionId = data.Id;
				}
				catch { }
			}

			if (!string.IsNullOrEmpty(sessionId) && sessionId.StartsWith(childPrefix, StringComparison.Ordinal))
				ids.Add(sessionId);
		}

		return ids;
	}

	// Deletes exactly one session file by its internal session ID, and nothing else.
	public static bool Delete(string sessionId)
	{
		if (string.IsNullOrWhiteSpace(sessionId) || !IsSafeSessionId(sessionId))
			return false;

		var manifest = LoadManifest();
		string? friendlyName = FindFriendlyName(sessionId);

		if (string.IsNullOrEmpty(friendlyName))
		{
			// Fallback: try the old GUID-based filename
			string oldPath = Path.Combine(SessionsDir, sessionId + ".json");
			if (!File.Exists(oldPath))
				return false;
			try
			{
				File.Delete(oldPath);
				return true;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[SessionService] Delete failed for {sessionId}: {ex}");
				return false;
			}
		}

		string path = Path.Combine(SessionsDir, friendlyName + ".json");
		if (!File.Exists(path))
		{
			// Clean up manifest entry even if file is missing
			manifest.Entries.Remove(friendlyName);
			SaveManifest(manifest);
			return false;
		}

		try
		{
			File.Delete(path);
			manifest.Entries.Remove(friendlyName);
			SaveManifest(manifest);
			return true;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[SessionService] Delete failed for {sessionId}: {ex}");
			return false;
		}
	}

	// Deletes a session and every descendant session: its own file plus any child files.
	// Returns the number of files removed.
	public static int DeleteTree(string sessionId)
	{
		int removed = 0;

		if (string.IsNullOrWhiteSpace(sessionId) || !IsSafeSessionId(sessionId) || !Directory.Exists(SessionsDir))
			return removed;

		string childPrefix = sessionId + "_";
		var manifest = LoadManifest();
		List<string> toRemove = new List<string>();

		foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
		{
			string fileName = Path.GetFileNameWithoutExtension(file);
			if (fileName == ".manifest")
				continue;

			string? fileSessionId = null;
			if (manifest.Entries.TryGetValue(fileName, out var entry))
				fileSessionId = entry.SessionId;

			if (string.IsNullOrEmpty(fileSessionId))
			{
				// Fallback: try loading the file
				try
				{
					string json = File.ReadAllText(file);
					var data = JsonSerializer.Deserialize<BeastSession>(json);
					if (data != null)
						fileSessionId = data.Id;
				}
				catch { }
			}

			if (string.IsNullOrEmpty(fileSessionId))
				continue;

			bool isThisTree = string.Equals(fileSessionId, sessionId, StringComparison.Ordinal) ||
							   fileSessionId.StartsWith(childPrefix, StringComparison.Ordinal);

			if (isThisTree)
			{
				toRemove.Add(fileName);
			}
		}

		foreach (string friendlyName in toRemove)
		{
			string path = Path.Combine(SessionsDir, friendlyName + ".json");
			try
			{
				if (File.Exists(path))
					File.Delete(path);
				manifest.Entries.Remove(friendlyName);
				removed++;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[SessionService] Delete failed for {friendlyName}: {ex}");
			}
		}

		if (removed > 0)
			SaveManifest(manifest);

		return removed;
	}

	// True when the id is a bare session id safe to turn into a single filename
	private static bool IsSafeSessionId(string sessionId)
	{
		if (sessionId.Contains("..") || sessionId.Contains('/') || sessionId.Contains('\\'))
			return false;

		foreach (char c in sessionId)
		{
			bool ok = char.IsLetterOrDigit(c) || c == '-' || c == '_';
			if (!ok)
				return false;
		}

		return true;
	}
}