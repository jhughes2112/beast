using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;


// Persists conversation sessions as JSON files under <workdir>/.beast/sessions/.
// Each session is one file: {sessionId}.json
// No "last" marker file — the last session ID is tracked in BeastSettings.
public static class SessionService
{
	private static string SessionsDir => Path.Combine(Environment.CurrentDirectory, ".beast", "sessions");

	public static void Save(BeastSession data)
	{
		Directory.CreateDirectory(SessionsDir);
		string path = Path.Combine(SessionsDir, data.Id + ".json");
		string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
		File.WriteAllText(path, json);
	}

	public static BeastSession? Load(string sessionId)
	{
		string path = Path.Combine(SessionsDir, sessionId + ".json");
		if (!File.Exists(path)) return null;
		string json = File.ReadAllText(path);
		return JsonSerializer.Deserialize<BeastSession>(json);
	}

	public static BeastSession? LoadBySessionId(string? sessionId)
	{
		if (string.IsNullOrEmpty(sessionId)) return null;
		return Load(sessionId);
	}

	public static List<(string id, string displayName, int messageCount)> List()
	{
		List<(string, string, int)> results = new List<(string, string, int)>();
		if (!Directory.Exists(SessionsDir)) return results;

		foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
		{
			try
			{
				string json = File.ReadAllText(file);
				BeastSession? data = JsonSerializer.Deserialize<BeastSession>(json);
				if (data == null) continue;
				results.Add((data.Id, data.DisplayName, data.Messages.Count));
			}
			catch { }
		}

		return results;
	}

	public static bool Delete(string sessionId)
	{
		string path = Path.Combine(SessionsDir, sessionId + ".json");
		if (!File.Exists(path)) return false;
		File.Delete(path);
		return true;
	}
}