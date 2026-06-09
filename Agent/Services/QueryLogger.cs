using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;


// Per-session query logger. One instance per Session, one log file per session.
// Each LLM call appends a timestamped entry to .beast/logs/{sessionId}.log.
public class QueryLogger
{
    private static string LogsDir => Path.Combine(Environment.CurrentDirectory, ".beast", "logs");

    private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;

    public QueryLogger(string sessionId)
    {
        try { Directory.CreateDirectory(LogsDir); } catch { }
        _path = Path.Combine(LogsDir, $"{sessionId}.log");
    }

    // Appends one LLM query entry to this session's log file.
    // Called once per ExecuteAsync call, including follow-up calls after tool results.
    public void Write(
        string modelName,
        IReadOnlyList<CanonicalMessage> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("==========================================================");
            sb.AppendLine($"time:    {DateTimeOffset.UtcNow:u}");
            sb.AppendLine($"model:   {modelName}");

            StringBuilder toolNames = new StringBuilder();
            foreach (ToolDefinition t in tools)
            {
                if (toolNames.Length > 0) toolNames.Append(", ");
                toolNames.Append(t.Function.Name);
            }
            sb.AppendLine($"tools:   {toolNames}");
            sb.AppendLine();
            sb.AppendLine(JsonSerializer.Serialize(messages, _jsonOpts));
            sb.AppendLine();

            File.AppendAllText(_path, sb.ToString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[QueryLogger] Write failed: {ex.Message}");
        }
    }
}
