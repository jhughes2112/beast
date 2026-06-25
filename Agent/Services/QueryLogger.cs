using System;
using System.IO;
using System.Text;


// Per-session query logger. One instance per Session, one log file per session.
// Each LLM call appends a timestamped entry containing the exact wire payload
// to .beast/logs/{sessionId}.log.
public class QueryLogger
{
	private static string LogsDir => Path.Combine(Environment.CurrentDirectory, ".beast", "logs");

	private readonly string _path;

	public QueryLogger(string sessionId)
	{
		try
		{ Directory.CreateDirectory(LogsDir); }
		catch { }
		_path = Path.Combine(LogsDir, $"{sessionId}.log");
	}

	// Appends one LLM request entry. json is the exact wire payload sent to the provider.
	public void Write(string modelName, string endpoint, string json)
	{
		try
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("==========================================================");
			sb.AppendLine($"time:     {DateTimeOffset.UtcNow:u}");
			sb.AppendLine($"model:    {modelName}");
			sb.AppendLine($"endpoint: {endpoint}");
			sb.AppendLine();
			sb.AppendLine(json);
			sb.AppendLine();

			File.AppendAllText(_path, sb.ToString());
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[QueryLogger] Write failed: {ex}");
		}
	}
}