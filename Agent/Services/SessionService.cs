using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;


// Persists conversation sessions as JSON files under <workdir>/.beast/sessions/.
// Each session is one file: {sessionId}.json
// The last ROOT session ID is tracked in .beast/lastSession.json so the next startup resumes the
// top-level conversation. Subagent saves write their own file but must NOT touch lastSession.json.
public static class SessionService
{
	private static string SessionsDir => Path.Combine(Environment.CurrentDirectory, ".beast", "sessions");
	private static string LastSessionFile => Path.Combine(Environment.CurrentDirectory, ".beast", "lastSession.json");

	// isRoot: true for the top-level session the user drives (and its compaction/role successors);
	// only those update lastSession.json. Subagent sessions pass false so resume never lands on one.
	public static void Save(BeastSession data, bool isRoot)
	{
		if (data.Ephemeral)
			return;
		if (string.IsNullOrEmpty(data.DisplayName))
			return;
		try
		{
			Directory.CreateDirectory(SessionsDir);
			string path = Path.Combine(SessionsDir, data.Id + ".json");
			string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
			File.WriteAllText(path, json);
			if (isRoot)
				SaveLastSession(data.Id);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[SessionService] Save failed for {data.Id}: {ex}");
		}
	}

	private static void SaveLastSession(string sessionId)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(LastSessionFile)!);
		string lastSessionData = JsonSerializer.Serialize(new { sessionId }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
		File.WriteAllText(LastSessionFile, lastSessionData);
	}

	public static string? LoadLastSession()
	{
		if (!File.Exists(LastSessionFile))
			return null;
		try
		{
			string json = File.ReadAllText(LastSessionFile);
			JsonDocument doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("sessionId", out JsonElement sessionIdElement))
			{
				return sessionIdElement.GetString();
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[SessionService] Failed to load last session: {ex}");
		}
		return null;
	}

	public static BeastSession? Load(string sessionId)
	{
		string path = Path.Combine(SessionsDir, sessionId + ".json");
		if (!File.Exists(path))
			return null;
		try
		{
			string json = File.ReadAllText(path);
			return JsonSerializer.Deserialize<BeastSession>(json);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[SessionService] Failed to load session {sessionId}: {ex}");
			return null;
		}
	}

	public static BeastSession? LoadBySessionId(string? sessionId)
	{
		if (string.IsNullOrEmpty(sessionId))
			return null;
		return Load(sessionId);
	}

	public static List<(string id, string displayName, int messageCount)> List()
	{
		List<(string, string, int)> results = new List<(string, string, int)>();
		if (!Directory.Exists(SessionsDir))
			return results;

		foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
		{
			// Only root-level sessions belong in the list. A root id is a bare GUID; every child
			// session (subagent tool sessions, compaction/role successors) appends "_N" to its
			// parent's id, so the filename alone identifies them and child files are skipped
			// without being read.
			string id = Path.GetFileNameWithoutExtension(file);
			if (id.Contains('_'))
				continue;
			try
			{
				string json = File.ReadAllText(file);
				BeastSession? data = JsonSerializer.Deserialize<BeastSession>(json);
				if (data == null)
					continue;
				results.Add((data.Id, data.DisplayName, data.Messages.Count));
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[SessionService] Failed to list session {file}: {ex}");
			}
		}

		return results;
	}

	// Returns the ids of every descendant session of a root: the "<rootId>_..." child files (subagent tool
	// sessions, compaction/role successors) at any depth, identified by filename alone so the files are not
	// read. The root itself is excluded. Used on resume to surface a root's saved children in the session list.
	public static List<string> ListDescendants(string rootId)
	{
		List<string> ids = new List<string>();
		if (string.IsNullOrWhiteSpace(rootId) || !IsSafeSessionId(rootId) || !Directory.Exists(SessionsDir))
			return ids;

		string childPrefix = rootId + "_";
		foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
		{
			string id = Path.GetFileNameWithoutExtension(file);
			if (id.StartsWith(childPrefix, StringComparison.Ordinal))
				ids.Add(id);
		}

		return ids;
	}

	// Deletes exactly one session file: <sessionId>.json inside the sessions folder, and nothing else. It
	// never removes the folder or any other file. The id must be a bare session id (a GUID, optionally with
	// "_N" child suffixes); anything empty, or carrying a path separator or "..", is rejected so a delete can
	// never escape the folder or widen its scope. A missing file or any IO error returns false.
	public static bool Delete(string sessionId)
	{
		bool deleted;

		if (string.IsNullOrWhiteSpace(sessionId) || !IsSafeSessionId(sessionId))
		{
			deleted = false;
		}
		else
		{
			string path = Path.Combine(SessionsDir, sessionId + ".json");
			if (!File.Exists(path))
			{
				deleted = false;
			}
			else
			{
				try
				{
					File.Delete(path);
					deleted = true;
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"[SessionService] Delete failed for {sessionId}: {ex}");
					deleted = false;
				}
			}
		}

		return deleted;
	}

	// Deletes a session and every descendant session: its own file plus any "<sessionId>_..." child file
	// (subagent tool sessions, compaction/role successors). Used by /finish so a finished worktree leaves no
	// orphaned session files behind. Session ids are full GUIDs, so the "<id>_" prefix matches only this
	// session's own descendants. Each removal goes through Delete, so it stays confined to one file at a time.
	// Returns the number of files removed.
	public static int DeleteTree(string sessionId)
	{
		int removed = 0;

		if (string.IsNullOrWhiteSpace(sessionId) || !IsSafeSessionId(sessionId) || !Directory.Exists(SessionsDir))
			return removed;

		string childPrefix = sessionId + "_";
		foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
		{
			string id = Path.GetFileNameWithoutExtension(file);
			bool isThisTree = string.Equals(id, sessionId, StringComparison.Ordinal) || id.StartsWith(childPrefix, StringComparison.Ordinal);
			if (isThisTree && Delete(id))
				removed++;
		}

		return removed;
	}

	// True when the id is a bare session id safe to turn into a single filename: only the characters session
	// ids use (letters, digits, '-' from GUIDs, and '_' for child suffixes) and no path navigation. This
	// confines Delete to one file inside the sessions folder, so a malformed id can never widen the delete.
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