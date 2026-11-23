using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// A conversation whose tools the bridge serves: the subscription protocols that drive a vendor's CLI.
public interface IMcpToolHost
{
	JsonArray McpToolList();

	// Held until Beast has committed this call's result and asked for the next turn.
	Task<JsonObject> CallToolAsync(string name, JsonObject arguments);
}

// Serves Beast's tools to the subscription CLIs (Claude Code, Antigravity) over MCP (the streamable-HTTP
// transport, answering every request with a plain JSON body — no server-initiated stream). One listener
// for the whole agent, bound to localhost; each CLI session gets its own route whose key is a random
// secret. The key reaches the CLI only inside a root-only config file, never on a command line,
// because the model's shell user can read every process's arguments and must not be able to call
// another session's tools.
public sealed class McpBridge
{
	private static readonly object     _gate = new object();
	private static          McpBridge? _shared;

	private readonly HttpListener _listener;
	private readonly int          _port;

	private readonly ConcurrentDictionary<string, IMcpToolHost> _routes = new ConcurrentDictionary<string, IMcpToolHost>(StringComparer.Ordinal);

	private McpBridge(HttpListener listener, int port)
	{
		_listener = listener;
		_port     = port;
	}

	// The agent's one bridge, started on first use and kept for the life of the process.
	public static McpBridge Shared()
	{
		lock (_gate)
		{
			if (_shared == null)
			{
				_shared = Start();
				_       = _shared.AcceptLoopAsync();
			}
			return _shared;
		}
	}

	// Binds a free port; the address only has to be known to the CLI processes this agent starts.
	private static McpBridge Start()
	{
		McpBridge? bridge = null;
		for (int attempt = 0; attempt < 20 && bridge == null; attempt++)
		{
			int          port     = 40000 + Random.Shared.Next(20000);
			HttpListener listener = new HttpListener();
			listener.Prefixes.Add($"http://localhost:{port}/");
			try
			{
				listener.Start();
				bridge = new McpBridge(listener, port);
			}
			catch (HttpListenerException)
			{
				listener.Close();
			}
		}
		if (bridge == null)
		{
			throw new InvalidOperationException("The MCP bridge could not bind a localhost port.");
		}

		return bridge;
	}

	// Returns the route URL for this session's tools; Unregister with the same URL when it ends.
	public string Register(IMcpToolHost protocol)
	{
		string key   = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
		_routes[key] = protocol;
		return $"http://localhost:{_port}/mcp/{key}";
	}

	public void Unregister(string url)
	{
		int slash = url.LastIndexOf('/');
		if (slash >= 0)
		{
			_routes.TryRemove(url.Substring(slash + 1), out IMcpToolHost? _);
		}
	}

	private async Task AcceptLoopAsync()
	{
		for (; ; )
		{
			HttpListenerContext context = await _listener.GetContextAsync();
			_ = HandleSafeAsync(context);
		}
	}

	private async Task HandleSafeAsync(HttpListenerContext context)
	{
		try
		{
			await HandleAsync(context);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[McpBridge] Request failed: {ex.Message}");
			try
			{
				context.Response.StatusCode = 500;
				context.Response.Close();
			}
			catch (Exception)
			{
			}
		}
	}

	private async Task HandleAsync(HttpListenerContext context)
	{
		string path = context.Request.Url?.AbsolutePath ?? string.Empty;
		string key  = path.StartsWith("/mcp/", StringComparison.Ordinal) ? path.Substring("/mcp/".Length) : string.Empty;

		if (!_routes.TryGetValue(key, out IMcpToolHost? protocol))
		{
			context.Response.StatusCode = 404;
			context.Response.Close();
		}
		else if (context.Request.HttpMethod != "POST")
		{
			// No server-initiated stream is offered; the transport allows refusing the GET.
			context.Response.StatusCode = 405;
			context.Response.Close();
		}
		else
		{
			string body;
			using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
			{
				body = await reader.ReadToEndAsync();
			}

			JsonNode? reply = await DispatchAsync(protocol, body);
			if (reply == null)
			{
				context.Response.StatusCode = 202;
				context.Response.Close();
			}
			else
			{
				byte[] bytes                     = Encoding.UTF8.GetBytes(reply.ToJsonString());
				context.Response.StatusCode      = 200;
				context.Response.ContentType     = "application/json";
				context.Response.ContentLength64 = bytes.Length;
				await context.Response.OutputStream.WriteAsync(bytes);
				context.Response.Close();
			}
		}
	}

	// One JSON-RPC message or a batch of them. Null when nothing needs an answer (notifications).
	private static async Task<JsonNode?> DispatchAsync(IMcpToolHost protocol, string body)
	{
		JsonNode? reply = null;
		JsonNode? parsed;
		try
		{
			parsed = JsonNode.Parse(body);
		}
		catch (JsonException)
		{
			parsed = null;
		}

		if (parsed is JsonArray batch)
		{
			JsonArray replies = new JsonArray();
			foreach (JsonNode? message in batch)
			{
				JsonObject? answer = message is JsonObject obj ? await AnswerAsync(protocol, obj) : null;
				if (answer != null)
				{
					replies.Add((JsonNode)answer);
				}
			}
			reply = replies.Count > 0 ? replies : null;
		}
		else if (parsed is JsonObject single)
		{
			reply = await AnswerAsync(protocol, single);
		}
		else
		{
			reply = Error(null, -32700, "Parse error");
		}
		return reply;
	}

	private static async Task<JsonObject?> AnswerAsync(IMcpToolHost protocol, JsonObject message)
	{
		JsonNode?   id     = message["id"]?.DeepClone();
		string      method = message["method"]?.ToString() ?? string.Empty;
		JsonObject? answer = null;

		// A message without an id is a notification (initialized, cancelled): nothing to answer.
		if (id != null)
		{
			JsonObject? parameters = message["params"] as JsonObject;
			if (method == "initialize")
			{
				answer = Result(id, new JsonObject
				{
					["protocolVersion"] = parameters?["protocolVersion"]?.ToString() ?? "2025-06-18",
					["capabilities"]    = new JsonObject { ["tools"] = new JsonObject() },
					["serverInfo"]      = new JsonObject { ["name"] = "beast", ["version"] = "1.0" }
				});
			}
			else if (method == "ping")
			{
				answer = Result(id, new JsonObject());
			}
			else if (method == "tools/list")
			{
				answer = Result(id, new JsonObject { ["tools"] = protocol.McpToolList() });
			}
			else if (method == "tools/call")
			{
				string     name      = parameters?["name"]?.ToString() ?? string.Empty;
				JsonObject arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
				answer               = Result(id, await protocol.CallToolAsync(name, arguments));
			}
			else
			{
				answer = Error(id, -32601, $"Method not found: {method}");
			}
		}
		return answer;
	}

	// Beast's tool result as an MCP result: the same text every protocol sends, plus each image the
	// result carries.
	public static JsonObject ToolReply(ToolResult result)
	{
		string text = result.StdOut;
		if (!string.IsNullOrEmpty(result.StdErr))
		{
			text = $"{text}\nstderr: {result.StdErr}";
		}

		JsonObject reply   = TextReply(text, false);
		JsonArray  content = (JsonArray)reply["content"]!;
		foreach (MediaFile file in result.Media)
		{
			if (file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && File.Exists(file.Path))
			{
				content.Add((JsonNode)new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(File.ReadAllBytes(file.Path)), ["mimeType"] = file.MimeType });
			}
		}
		return reply;
	}

	public static JsonObject TextReply(string text, bool isError)
	{
		return new JsonObject
		{
			["content"] = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text }),
			["isError"] = isError
		};
	}

	private static JsonObject Result(JsonNode id, JsonObject result)
	{
		return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
	}

	private static JsonObject Error(JsonNode? id, int code, string message)
	{
		return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
	}
}