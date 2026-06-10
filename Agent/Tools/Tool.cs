using System;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;


// Wire types
public class ToolDefinition
{
	[JsonPropertyName("type")]
	public string Type { get; set; } = "function";

	[JsonPropertyName("function")]
	public FunctionDefinition Function { get; set; } = new();
}

public class FunctionDefinition
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("description")]
	public string Description { get; set; } = "";

	[JsonPropertyName("parameters")]
	public JsonObject Parameters { get; set; } = new();
}

// Result returned by a tool after execution.
// ExitCode: 0 = success, non-zero = error
// StdOut: Response content when successful (or both stdout and stderr may be present)
// StdErr: Error message when exitCode is non-zero (or both stdout and stderr may be present)
public class ToolResult
{
	public string StdOut { get; }
	public string StdErr { get; }
	public int ExitCode { get; }

	public ToolResult(string stdOut, string stdErr, int exitCode)
	{
		StdOut = stdOut;
		StdErr = stdErr;
		ExitCode = exitCode;
	}
}

// Internal tool representation used by LlmService's execution loop.
// The trailing int is maxOutputTokens: the token budget this call's output must fit into, set by
// LlmService from the remaining context space divided among the round's parallel tool calls.
public class Tool
{
	public ToolDefinition Definition { get; set; } = new();
	public Func<JsonObject, CancellationToken, ITransportServer, string, int, Task<ToolResult>> Handler { get; set; } = null!;
}
