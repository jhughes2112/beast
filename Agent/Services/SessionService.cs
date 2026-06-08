using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;


// Persists conversation sessions as JSON files under <workdir>/.beast/sessions/.
// Each session is one file: {sessionId}.json
// The last session ID is tracked in .beast/lastSession.json
public static class SessionService
{
    private static string SessionsDir => Path.Combine(Environment.CurrentDirectory, ".beast", "sessions");
    private static string LastSessionFile => Path.Combine(Environment.CurrentDirectory, ".beast", "lastSession.json");

    public static void Save(BeastSession data)
    {
        if (data.Ephemeral)
            return;
        if (string.IsNullOrEmpty(data.DisplayName))
            return;
        Directory.CreateDirectory(SessionsDir);
        string path = Path.Combine(SessionsDir, data.Id + ".json");
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(path, json);
        SaveLastSession(data.Id);
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
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BeastSession>(json);
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