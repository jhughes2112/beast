using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;


// The commands a person answered "always" or "never" when asked whether to run them, kept in
// ~/.beast/decisions.json as two arrays: {"always": [...], "never": [...]}. An exact repeat is decided
// from here without asking a model; every other command is judged with both lists in front of the
// decision model, so it learns the person's line. New entries are appended, so the lists' text — the
// start of every decision request — stays the same between calls and the server's prefix cache holds.
// The file is shared by every session and may change outside this one, so it is re-read whenever it
// changed on disk (no /reload needed) and every change is a load, modify, save.
public class DecisionMemory
{
	private readonly string _path;
	private readonly object _lock = new object();

	private List<string> _always          = new List<string>();
	private List<string> _never           = new List<string>();
	private DateTime     _loadedWriteTime = DateTime.MinValue;

	public DecisionMemory(string path)
	{
		_path = path;
	}

	public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast", "decisions.json");

	// The current lists, re-read first if the file changed. Copies, so a caller cannot race a write.
	public (List<string> Always, List<string> Never) Snapshot()
	{
		lock (_lock)
		{
			ReloadIfChanged();
			return (new List<string>(_always), new List<string>(_never));
		}
	}

	// Records an "always" or "never" answer at the end of its list. A command already in either list is
	// decided from it and never put to the person, so an answer only ever adds. The file is re-read just
	// before the write, so whatever another session or a hand edit added meanwhile is kept.
	public void Remember(string command, bool always)
	{
		string normalized = Normalize(command);
		if (normalized.Length > 0)
		{
			lock (_lock)
			{
				ReloadIfChanged();
				List<string> into = always ? _always : _never;
				if (!into.Contains(normalized))
					into.Add(normalized);
				Save();
			}
		}
	}

	// Commands are compared trimmed and otherwise exactly: a flag or a path that differs is a different command.
	public static string Normalize(string command)
	{
		return command.Trim();
	}

	private void ReloadIfChanged()
	{
		DateTime stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
		if (stamp != _loadedWriteTime)
		{
			List<string> always = new List<string>();
			List<string> never  = new List<string>();
			bool         read   = true;
			try
			{
				if (stamp != DateTime.MinValue)
				{
					JsonNode? root = JsonNode.Parse(File.ReadAllText(_path));
					ReadList(root?["always"], always);
					ReadList( root?["never"],  never);
				}
			}
			catch (Exception ex)
			{
				// A broken hand edit must not take decisions down; the lists stay as last read until it is fixed.
				Console.Error.WriteLine($"[DecisionMemory] Could not read {_path}: {ex.Message}");
				read = false;
			}

			if (read)
			{
				_always          = always;
				_never           = never;
				_loadedWriteTime = stamp;
			}
		}
	}

	private static void ReadList(JsonNode? node, List<string> into)
	{
		if (node is JsonArray array)
		{
			foreach (JsonNode? item in array)
			{
				string command = item is JsonValue v && v.TryGetValue(out string? s) ? Normalize(s) : string.Empty;
				if (command.Length > 0 && !into.Contains(command))
					into.Add(command);
			}
		}
	}

	private void Save()
	{
		// Built from JsonValue.Create and written by hand: the Agent runs without reflection-based JSON, so
		// the generic JsonArray.Add<T> and serializer options would need type metadata it does not carry.
		JsonArray always = new JsonArray();
		foreach (string c in _always)
			always.Add((JsonNode?)JsonValue.Create(c));
		JsonArray never = new JsonArray();
		foreach (string c in _never)
			never.Add((JsonNode?)JsonValue.Create(c));
		JsonObject root = new JsonObject { ["always"] = always, ["never"] = never };

		try
		{
			string? dir = Path.GetDirectoryName(_path);
			if (dir != null)
				Directory.CreateDirectory(dir);

			using (FileStream stream = File.Create(_path))
			using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
				root.WriteTo(writer);
			_loadedWriteTime = File.GetLastWriteTimeUtc(_path);
		}
		catch (Exception ex)
		{
			// The answer still holds in memory for this run; only its persistence failed.
			Console.Error.WriteLine($"[DecisionMemory] Could not save {_path}: {ex.Message}");
		}
	}
}