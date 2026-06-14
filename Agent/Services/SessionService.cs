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
            Console.Error.WriteLine($"[SessionService] Save failed for {data.Id}: {ex.Message}");
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
            Console.Error.WriteLine($"[SessionService] Failed to load last session: {ex.Message}");
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
            Console.Error.WriteLine($"[SessionService] Failed to load session {sessionId}: {ex.Message}");
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
                Console.Error.WriteLine($"[SessionService] Failed to list session {file}: {ex.Message}");
            }
        }

        return results;
    }

    public static bool Delete(string sessionId)
    {
        string path = Path.Combine(SessionsDir, sessionId + ".json");
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }
}