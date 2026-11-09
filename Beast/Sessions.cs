using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;


// One Beast session: an agent working on a folder, either in place (a bare session, of which a folder can
// have several, told apart by name) or on its own git worktree of a repository (one session per worktree).
// Everything a session owns lives in its own directory under
// the sessions folder (SessionStore.Root, normally ~/.beast/sessions): <id>/ holds the record (session.json), the worktree checkout, and the agent's saved
// conversations, logs and staged attachments. Nothing is written into the user's project, and finishing or deleting a session
// is removing that one directory (plus the worktree's registration and branch in the repository).
public sealed class SessionRecord
{
	public readonly string Id;
	// The folder the agent works on: the repository root for a worktree session, the folder itself for a bare one.
	public readonly string Folder;
	// The worktree's name, which is also its git branch; empty for a bare session.
	public readonly string Worktree;
	// A bare session's name in its folder; empty for a worktree session, and for an unnamed bare session.
	public readonly string   Name;
	public readonly DateTime CreatedUtc;
	public          DateTime LastUsedUtc;
	// The conversation last on screen, which the session shows again when it next starts; empty for none yet.
	public string Focus = string.Empty;
	// The session's own directory, <SessionStore.Root>/<id> (normally ~/.beast/sessions/<id>).
	public readonly string Dir;

	public SessionRecord(string id, string dir, string folder, string worktree, DateTime createdUtc, DateTime lastUsedUtc, string name = "")
	{
		Name        = name;
		Id          = id;
		Dir         = dir;
		Folder      = folder;
		Worktree    = worktree;
		CreatedUtc  = createdUtc;
		LastUsedUtc = lastUsedUtc;
	}

	public bool IsBare => Worktree.Length == 0;

	// The worktree checkout, bound to /workspace. Only meaningful for a worktree session.
	public string WorktreePath => Path.Combine(Dir, "worktree");

	// The agent's conversations, logs and staged attachments (AgentState on the agent side).
	public string StateDir => Path.Combine(Dir, "state");

	// The host folder bound to /workspace: where host_command runs.
	public string WorkspacePath => IsBare ? Folder : WorktreePath;

	public string ContainerName => "beast_" + Id;

	// How the session reads in a list: "worktree fix-login", "bare session tests" or "bare session".
	public string Describe()
	{
		return !IsBare ? $"worktree {Worktree}" : Name.Length > 0 ? $"bare session {Name}" : "bare session";
	}
}

// The sessions under the sessions folder (~/.beast/sessions, or sessionsDir in ~/.beast/settings.json):
// listing, creating and removing their directories.
public static class SessionStore
{
	// Read once at startup: the session menu and the launch both run before anything could change it.
	public static readonly string Root = ResolveRoot();

	private const string RecordFile = "session.json";

	// sessionsDir from ~/.beast/settings.json when it is set, so sessions and their worktrees can live on
	// another drive; otherwise ~/.beast/sessions. An unreadable settings file falls back to the default.
	private static string ResolveRoot()
	{
		string beastDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast");
		string root     = Path.Combine(beastDir, "sessions");
		try
		{
			string settingsPath = Path.Combine(beastDir, "settings.json");
			if (File.Exists(settingsPath))
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
				string configured      = doc.RootElement.TryGetProperty("sessionsDir", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
				if (configured.Trim().Length > 0)
					root = Path.GetFullPath(configured.Trim());
			}
		}
		catch (Exception)
		{
		}
		return root;
	}

	// Every session with a readable record, most recently used first. A directory without one is skipped.
	public static List<SessionRecord> List()
	{
		List<SessionRecord> sessions = new List<SessionRecord>();
		if (Directory.Exists(Root))
		{
			foreach (string dir in Directory.GetDirectories(Root))
			{
				SessionRecord? session = Read(dir);
				if (session != null)
					sessions.Add(session);
			}
		}
		sessions.Sort((a, b) => b.LastUsedUtc.CompareTo(a.LastUsedUtc));
		return sessions;
	}

	// The record in a session's directory, or null when it has no readable one.
	public static SessionRecord? Read(string dir)
	{
		SessionRecord? session = null;
		try
		{
			string path = Path.Combine(dir, RecordFile);
			if (File.Exists(path))
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				JsonElement root       = doc.RootElement;
				string      folder     = root.TryGetProperty("folder",    out JsonElement f) ? f.GetString() ?? string.Empty : string.Empty;
				string      worktree   = root.TryGetProperty("worktree",  out JsonElement w) ? w.GetString() ?? string.Empty : string.Empty;
				DateTime    created    = root.TryGetProperty("created",   out JsonElement c) && c.TryGetDateTime(out DateTime cv) ? cv.ToUniversalTime() : DateTime.MinValue;
				DateTime    lastUsed   = root.TryGetProperty("lastUsed",  out JsonElement l) && l.TryGetDateTime(out DateTime lv) ? lv.ToUniversalTime() : created;
				string      name       = root.TryGetProperty("name",      out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
				string      focus      = root.TryGetProperty("focus",    out JsonElement fo) ? fo.GetString() ?? string.Empty : string.Empty;
				if (folder.Length > 0)
					session = new SessionRecord(Path.GetFileName(dir), dir, folder, worktree, created, lastUsed, name) { Focus = focus };
			}
		}
		catch (Exception)
		{
		}
		return session;
	}

	// Writes the session's record, creating its directory.
	public static void Save(SessionRecord session)
	{
		Directory.CreateDirectory(session.Dir);
		using MemoryStream stream = new MemoryStream();
		using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			writer.WriteStartObject();
			writer.WriteString("folder",        session.Folder);
			writer.WriteString("worktree",    session.Worktree);
			writer.WriteString("name",            session.Name);
			writer.WriteString("created",   session.CreatedUtc);
			writer.WriteString("lastUsed", session.LastUsedUtc);
			writer.WriteString("focus",          session.Focus);
			writer.WriteEndObject();
		}
		string path = Path.Combine(session.Dir, RecordFile);
		File.WriteAllText(path + ".tmp", Encoding.UTF8.GetString(stream.ToArray()));
		File.Move(path + ".tmp", path, true);
	}

	// Creates a new session for folder (worktree empty for a bare one). Its id names its directory and its
	// container (beast_<id>): the worktree's name, or the folder's name plus a random tag for a bare session,
	// with a random tag added whenever the plain name is already taken by a session or a container. name is
	// a bare session's name in its folder.
	public static SessionRecord Create(string folder, string worktree, HashSet<string> takenContainerNames, string name = "")
	{
		string leaf     = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
		string baseName = NameSafe(worktree.Length > 0 ? worktree : leaf);
		string id       = worktree.Length > 0 ? baseName : $"{baseName}_{RandomTag()}";
		while (Directory.Exists(Path.Combine(Root, id)) || takenContainerNames.Contains("beast_" + id))
			id = $"{baseName}_{RandomTag()}";

		DateTime      now     = DateTime.UtcNow;
		SessionRecord session = new SessionRecord(id, Path.Combine(Root, id), NormalizeFolder(folder), worktree, now, now, name);
		Save(session);
		Directory.CreateDirectory(session.StateDir);
		if (!session.IsBare)
			Directory.CreateDirectory(session.WorktreePath);
		return session;
	}

	// Marks the session as just used, so it sorts first next time.
	public static void Touch(SessionRecord session)
	{
		session.LastUsedUtc = DateTime.UtcNow;
		try
		{ Save(session); }
		catch (Exception)
		{
		}
	}

	// Removes the session's directory and everything in it. Returns why not, or empty.
	public static string DeleteDir(SessionRecord session)
	{
		return DeleteTree(session.Dir);
	}

	// Deletes a folder tree, clearing read-only flags that would otherwise stop it on Windows. Returns why
	// it could not, or empty (also when there was nothing to delete).
	public static string DeleteTree(string path)
	{
		string error = string.Empty;
		try
		{
			if (Directory.Exists(path))
			{
				foreach (string file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
				{
					FileAttributes attrs = File.GetAttributes(file);
					if ((attrs & FileAttributes.ReadOnly) != 0)
						File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
				}
				Directory.Delete(path, true);
			}
		}
		catch (Exception ex)
		{
			error = ex.Message;
		}
		return error;
	}

	// The folder's first bare session in the list, which List sorts most recently used first; or null.
	public static SessionRecord? FindBare(List<SessionRecord> sessions, string folder)
	{
		SessionRecord? found = null;
		foreach (SessionRecord session in sessions)
		{
			if (found == null && session.IsBare && SameFolder(session.Folder, folder))
				found = session;
		}
		return found;
	}

	// The folder's bare session of that name, or null.
	public static SessionRecord? FindBareNamed(List<SessionRecord> sessions, string folder, string name)
	{
		SessionRecord? found = null;
		foreach (SessionRecord session in sessions)
		{
			if (found == null && session.IsBare && SameFolder(session.Folder, folder) && string.Equals(session.Name, name, StringComparison.Ordinal))
				found = session;
		}
		return found;
	}

	// A name no bare session of the folder has yet: session-1, session-2, and so on.
	public static string FreeBareName(List<SessionRecord> sessions, string folder)
	{
		int number = 1;
		while (FindBareNamed(sessions, folder, $"session-{number}") != null)
			number++;
		return $"session-{number}";
	}

	// The repository's worktree session of that name, or null.
	public static SessionRecord? FindWorktree(List<SessionRecord> sessions, string repo, string worktree)
	{
		SessionRecord? found = null;
		foreach (SessionRecord session in sessions)
		{
			if (found == null && !session.IsBare && SameFolder(session.Folder, repo) && string.Equals(session.Worktree, worktree, StringComparison.Ordinal))
				found = session;
		}
		return found;
	}

	public static bool SameFolder(string a, string b)
	{
		return string.Equals(NormalizeFolder(a), NormalizeFolder(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	public static string NormalizeFolder(string folder)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
	}

	// Restricts a user-entered session name to a folder- and branch-safe charset (letters, digits, '.', '_',
	// '-'), collapsing other runs to '-'. A worktree's name also names its git branch, so '/' is not allowed.
	// Empty input becomes "work".
	public static string SanitizeWorktreeName(string name)
	{
		StringBuilder sb       = new StringBuilder(name.Length);
		bool          lastDash = false;
		foreach (char c in name.Trim())
		{
			bool ok = char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-';
			if (ok)
			{
				sb.Append(c);
				lastDash = false;
			}
			else if (!lastDash)
			{
				sb.Append('-');
				lastDash = true;
			}
		}

		string s = sb.ToString().Trim('-', '.');
		return s.Length == 0 ? "work" : s;
	}

	// An id usable as a folder name and in a Docker container name: ASCII letters, digits, '_', '.', '-'.
	private static string NameSafe(string text)
	{
		char[] chars = text.ToCharArray();
		for (int i = 0; i < chars.Length; i++)
		{
			bool ok = (chars[i] >= 'a' && chars[i] <= 'z') || (chars[i] >= 'A' && chars[i] <= 'Z') || (chars[i] >= '0' && chars[i] <= '9') || chars[i] == '_' || chars[i] == '.' || chars[i] == '-';
			if (!ok)
				chars[i] = '-';
		}
		string safe = new string(chars).Trim('-', '.', '_');
		return safe.Length > 0 ? safe : "session";
	}

	private static string RandomTag()
	{
		return Guid.NewGuid().ToString("N").Substring(0, 6);
	}
}