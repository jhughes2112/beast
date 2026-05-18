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
public class ToolResult
{
	public string Response { get; }
	public bool MessageHandled { get; }

	public ToolResult(string response, bool messageHandled)
	{
		Response = response;
		MessageHandled = messageHandled;
	}

	public override string ToString() => Response;
}

// Internal tool representation used by LlmService's execution loop.
public class Tool
{
	public ToolDefinition Definition { get; set; } = new();
	public Func<JsonObject, CancellationToken, ITransportServer, Task<ToolResult>> Handler { get; set; } = null!;
}
