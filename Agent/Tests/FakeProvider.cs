using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// One scripted reply from the fake provider: a model turn (thinking, text, tool calls) or an HTTP
// error with the exact status and body a real provider sends.
public class FakeReply
{
	public string Thinking { get; }
	public string Text     { get; }
	public List<(string Id, string Name, string Args)> Calls { get; }
	public int    ErrorStatus { get; }
	public string ErrorBody   { get; }

	// Streams "data:{...}" with no space after the colon — legal SSE, and what uzu sends.
	public bool CompactSse { get; private set; }

	private FakeReply(string thinking, string text, List<(string Id, string Name, string Args)> calls, int errorStatus, string errorBody)
	{
		Thinking    = thinking;
		Text        = text;
		Calls       = calls;
		ErrorStatus = errorStatus;
		ErrorBody   = errorBody;
	}

	public static FakeReply Say(string text)
	{
		return new FakeReply(string.Empty, text, new List<(string, string, string)>(), 0, string.Empty);
	}

	public static FakeReply Turn(string thinking, string text, List<(string Id, string Name, string Args)> calls)
	{
		return new FakeReply(thinking, text, calls, 0, string.Empty);
	}

	public static FakeReply Call(string id, string name, string args)
	{
		return new FakeReply(string.Empty, string.Empty, new List<(string, string, string)> { (id, name, args) }, 0, string.Empty);
	}

	public static FakeReply Error(int status, string body)
	{
		return new FakeReply(string.Empty, string.Empty, new List<(string, string, string)>(), status, body);
	}

	public FakeReply WithCompactSse()
	{
		CompactSse = true;
		return this;
	}
}

// One request as the fake provider received it.
public class FakeRequest
{
	// Which API it arrived on: "chat", "messages" or "responses".
	public string   Api       { get; }
	public JsonNode Body      { get; }
	public bool     Streaming { get; }
	// Media parts in THIS request's own payload.
	public int MediaCount { get; }
	// Media the model can see on this turn: the payload plus, for a chained Responses request, what the
	// server already holds in the thread it continues.
	public int VisibleMediaCount { get; }
	public int StatusSent        { get; }

	public FakeRequest(string api, JsonNode body, bool streaming, int mediaCount, int visibleMediaCount, int statusSent)
	{
		Api               = api;
		Body              = body;
		Streaming         = streaming;
		MediaCount        = mediaCount;
		VisibleMediaCount = visibleMediaCount;
		StatusSent        = statusSent;
	}
}

// A zero-cost provider that speaks all three wire APIs — Chat Completions, Anthropic Messages and
// OpenAI Responses — so each protocol can be driven end to end through real HTTP and real streaming,
// and asserted on what the server actually received. Unlike FakeLlm (a random chaos soak) it is
// scripted: each request consumes the next queued FakeReply, and every request is recorded.
//
// Provider behavior that matters to the client is modelled rather than mocked:
//   - Responses threads live server-side: a response id chains to everything before it, so a test
//     can see what the model still holds after the client stopped sending it.
//   - Media limits, as real providers word them: MaxImages refuses a request carrying more images
//     ("Too many images in request: > N", as OpenRouter relays it), MaxMediaBytes refuses too many
//     bytes of them (OpenRouter's 413). Both count the request's own payload.
//   - RejectStreaming refuses stream=true, as some local servers do, so the fallback path runs.
public class FakeProvider
{
	private readonly HttpListener _listener = new HttpListener();
	private readonly int          _port;

	private readonly object            _gate     = new object();
	private readonly Queue<FakeReply>  _script   = new Queue<FakeReply>();
	private readonly List<FakeRequest> _requests = new List<FakeRequest>();

	// Responses API threads: response id -> media the server holds for that thread.
	private readonly Dictionary<string, int> _threadMedia = new Dictionary<string, int>(StringComparer.Ordinal);
	private          int                     _nextId;

	private int  _maxImages;
	private long _maxMediaBytes;
	private bool _rejectStreaming;

	// The Responses token-count route this server answers ("/responses/input_tokens/count" as OpenAI
	// spells it, "/responses/input_tokens" as Meta does); empty answers neither. Every count request
	// is logged by path, so a test can see how often a client probes.
	private          string       _countRoute = string.Empty;
	private readonly List<string> _countPaths = new List<string>();

	public string ChatEndpoint      => $"http://localhost:{_port}/v1/chat/completions";
	public string MessagesEndpoint  => $"http://localhost:{_port}/v1/messages";
	public string ResponsesEndpoint => $"http://localhost:{_port}/v1/responses";

	private FakeProvider(int port)
	{
		_port = port;
		_listener.Prefixes.Add($"http://localhost:{_port}/");
	}

	public static FakeProvider? StartOnRandomPort()
	{
		FakeProvider? server = null;
		for (int attempt = 0; attempt < 10 && server == null; attempt++)
		{
			FakeProvider candidate = new FakeProvider(40000 + Random.Shared.Next(20000));
			try
			{
				candidate._listener.Start();
				server = candidate;
			}
			catch (HttpListenerException)
			{
			}
		}
		return server;
	}

	// Clears the script, the log, the threads and every limit, ready for the next scenario.
	public void Reset()
	{
		lock (_gate)
		{
			_script.Clear();
			_requests.Clear();
			_threadMedia.Clear();
			_maxImages       = 0;
			_maxMediaBytes   = 0;
			_rejectStreaming = false;
			_countRoute      = string.Empty;
			_countPaths.Clear();
		}
	}

	public void ServeCount(string route)
	{
		lock (_gate)
			_countRoute = route;
	}

	public List<string> CountRequests()
	{
		lock (_gate)
			return new List<string>(_countPaths);
	}

	public void Enqueue(FakeReply reply)
	{
		lock (_gate)
			_script.Enqueue(reply);
	}

	public void Limit(int maxImages, long maxMediaBytes, bool rejectStreaming)
	{
		lock (_gate)
		{
			_maxImages       = maxImages;
			_maxMediaBytes   = maxMediaBytes;
			_rejectStreaming = rejectStreaming;
		}
	}

	public List<FakeRequest> Requests()
	{
		lock (_gate)
			return new List<FakeRequest>(_requests);
	}

	public async Task RunAsync(CancellationToken ct)
	{
		using CancellationTokenRegistration stop = ct.Register(() => _listener.Stop());
		try
		{
			while (!ct.IsCancellationRequested)
			{
				HttpListenerContext context = await _listener.GetContextAsync();
				_ = HandleSafeAsync(context, ct);
			}
		}
		catch (HttpListenerException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private async Task HandleSafeAsync(HttpListenerContext context, CancellationToken ct)
	{
		try
		{
			await HandleAsync(context, ct);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[FakeProvider] request failed: {ex.Message}");
			try
			{
				context.Response.Abort();
			}
			catch (Exception)
			{
			}
		}
	}

	private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
	{
		string path = context.Request.Url?.AbsolutePath ?? string.Empty;
		string api  = path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? "chat"
			: path.EndsWith("/messages",  StringComparison.OrdinalIgnoreCase) ? "messages"
			: path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ? "responses"
			: string.Empty;

		if (context.Request.HttpMethod == "POST" && path.Contains("/responses/input_tokens", StringComparison.OrdinalIgnoreCase))
			await HandleCountAsync(context, path, ct);
		else if (context.Request.HttpMethod == "POST" && api.Length > 0)
			await HandleApiAsync(context, api, ct);
		else
			await WriteJsonAsync(context.Response, 404, "{\"error\":{\"message\":\"not found\"}}", ct);
	}

	// A count on the route this server serves answers a fixed figure; any other spelling is a 404.
	private async Task HandleCountAsync(HttpListenerContext context, string path, CancellationToken ct)
	{
		bool served;
		lock (_gate)
		{
			_countPaths.Add(path);
			served = _countRoute.Length > 0 && path.EndsWith(_countRoute, StringComparison.OrdinalIgnoreCase);
		}
		if (served)
			await WriteJsonAsync(context.Response, 200, "{\"object\":\"response.input_tokens\",\"input_tokens\":321}", ct);
		else
			await WriteJsonAsync(context.Response, 404, "{\"error\":{\"message\":\"not found\"}}", ct);
	}

	private async Task HandleApiAsync(HttpListenerContext context, string api, CancellationToken ct)
	{
		string bodyText;
		using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
			bodyText = await reader.ReadToEndAsync(ct);
		JsonNode body      = JsonNode.Parse(bodyText) ?? new JsonObject();
		bool     streaming = body["stream"]?.GetValue<bool>() ?? false;

		(int media, long mediaBytes) = CountMedia(body);

		FakeReply reply;
		int       visible;
		string    responseId = string.Empty;
		lock (_gate)
		{
			// Server-side refusals come before the script, as a real provider rejects the request
			// before any model sees it.
			if (_rejectStreaming && streaming)
				reply = FakeReply.Error(400, "{\"error\":{\"message\":\"stream is not supported by this server\"}}");
			else if (_maxImages > 0 && media > _maxImages)
				reply = FakeReply.Error(400, $"{{\"error\":{{\"message\":\"Too many images in request: > {_maxImages}\",\"code\":400,\"metadata\":{{\"provider_name\":null}}}}}}");
			else if (_maxMediaBytes > 0 && mediaBytes > _maxMediaBytes)
				reply = FakeReply.Error(413, $"{{\"error\":{{\"message\":\"Downloaded image content cannot exceed {_maxMediaBytes / 1024}KB\",\"code\":413,\"metadata\":{{\"provider_name\":null}}}}}}");
			else
				reply = _script.Count > 0 ? _script.Dequeue() : FakeReply.Say("ok");

			visible = media;
			if (api == "responses")
			{
				string? previous = body["previous_response_id"]?.GetValue<string>();
				if (previous != null && _threadMedia.TryGetValue(previous, out int held))
					visible += held;
				if (reply.ErrorStatus == 0)
				{
					_nextId++;
					responseId               = $"resp_fake_{_nextId}";
					_threadMedia[responseId] = visible;
				}
			}
			_requests.Add(new FakeRequest(api, body, streaming, media, visible, reply.ErrorStatus == 0 ? 200 : reply.ErrorStatus));
		}

		if (reply.ErrorStatus != 0)
			await WriteJsonAsync(context.Response, reply.ErrorStatus, reply.ErrorBody, ct);
		else if (api == "chat")
			await WriteChatAsync(context.Response, reply, streaming, ct);
		else if (api == "messages")
			await WriteMessagesAsync(context.Response, reply, ct);
		else
			await WriteResponsesAsync(context.Response, reply, responseId, streaming, ct);
	}

	// Media parts anywhere in the payload, and their decoded size (three quarters of the base64).
	// The tool definitions are skipped: their schemas are not content.
	private static (int Count, long Bytes) CountMedia(JsonNode body)
	{
		int  count = 0;
		long bytes = 0;
		foreach (string field in new string[] { "messages", "input" })
		{
			JsonNode? node = body[field];
			if (node != null)
				Walk(node, ref count, ref bytes);
		}
		return (count, bytes);
	}

	private static void Walk(JsonNode node, ref int count, ref long bytes)
	{
		if (node is JsonArray array)
		{
			foreach (JsonNode? child in array)
			{
				if (child != null)
					Walk(child, ref count, ref bytes);
			}
		}
		else if (node is JsonObject obj)
		{
			string type = obj["type"] is JsonValue v && v.TryGetValue<string>(out string? s) ? s : string.Empty;
			if (type == "image_url" || type == "image" || type == "input_image" || type == "input_audio" || type == "video_url")
			{
				count++;
				bytes += StringChars(obj) * 3 / 4;
			}
			else
			{
				foreach ((string _, JsonNode? child) in obj)
				{
					if (child != null)
						Walk(child, ref count, ref bytes);
				}
			}
		}
	}

	private static long StringChars(JsonNode? node)
	{
		long total = 0;
		if (node is JsonValue value && value.TryGetValue<string>(out string? s))
			total = s.Length;
		else if (node is JsonArray array)
			foreach (JsonNode? child in array)
				total += StringChars(child);
		else if (node is JsonObject obj)
			foreach ((string _, JsonNode? child) in obj)
				total += StringChars(child);
		return total;
	}

	// ---- Chat Completions ----

	private static async Task WriteChatAsync(HttpListenerResponse response, FakeReply reply, bool streaming, CancellationToken ct)
	{
		if (streaming)
			await WriteChatSseAsync(response, reply, ct);
		else
			await WriteChatJsonAsync(response, reply, ct);
	}

	private static async Task WriteChatJsonAsync(HttpListenerResponse response, FakeReply reply, CancellationToken ct)
	{
		string     finish  = reply.Calls.Count > 0 ? "tool_calls" : "stop";
		JsonObject message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Text };
		if (reply.Thinking.Length > 0)
			message["reasoning_content"] = reply.Thinking;
		if (reply.Calls.Count > 0)
		{
			JsonArray calls = new JsonArray();
			foreach ((string id, string name, string args) in reply.Calls)
				calls.Add((JsonNode)new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } });
			message["tool_calls"] = calls;
		}
		JsonObject completion = new JsonObject
		{
			["id"]      = "chatcmpl-fake",
			["object"]  = "chat.completion",
			["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finish }),
			["usage"]   = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 10, ["total_tokens"] = 110 }
		};
		await WriteJsonAsync(response, 200, completion.ToJsonString(), ct);
	}

	private static async Task WriteChatSseAsync(HttpListenerResponse response, FakeReply reply, CancellationToken ct)
	{
		string finish             = reply.Calls.Count > 0 ? "tool_calls" : "stop";
		using StreamWriter writer = OpenSse(response, reply.CompactSse);
		await DataAsync(writer, ChatChunk(new JsonObject { ["role"] = "assistant" }, null), ct);
		if (reply.Thinking.Length > 0)
			await DataAsync(writer, ChatChunk(new JsonObject { ["reasoning_content"] = reply.Thinking }, null), ct);
		if (reply.Text.Length > 0)
			await DataAsync(writer, ChatChunk(new JsonObject { ["content"] = reply.Text }, null), ct);
		for (int i = 0; i < reply.Calls.Count; i++)
		{
			(string id, string name, string args) = reply.Calls[i];
			JsonObject call                       = new JsonObject { ["index"] = i, ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } };
			await DataAsync(writer, ChatChunk(new JsonObject { ["tool_calls"] = new JsonArray(call) }, null), ct);
		}
		await DataAsync(writer, ChatChunk(new JsonObject(), finish), ct);
		JsonObject usage = new JsonObject
		{
			["id"]      = "chatcmpl-fake",
			["object"]  = "chat.completion.chunk",
			["choices"] = new JsonArray(),
			["usage"]   = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 10, ["total_tokens"] = 110 }
		};
		await DataAsync(writer, usage.ToJsonString(), ct);
		await DataAsync(writer,             "[DONE]", ct);
	}

	private static string ChatChunk(JsonObject delta, string? finish)
	{
		JsonObject choice = new JsonObject { ["index"] = 0, ["delta"] = delta };
		if (finish != null)
			choice["finish_reason"] = finish;
		return new JsonObject { ["id"] = "chatcmpl-fake", ["object"] = "chat.completion.chunk", ["choices"] = new JsonArray(choice) }.ToJsonString();
	}

	// ---- Anthropic Messages (always streamed by the client) ----

	private static async Task WriteMessagesAsync(HttpListenerResponse response, FakeReply reply, CancellationToken ct)
	{
		using StreamWriter writer = OpenSse(response, reply.CompactSse);
		JsonObject start          = new JsonObject
		{
			["type"]    = "message_start",
			["message"] = new JsonObject { ["id"] = "msg_fake", ["role"] = "assistant", ["content"] = new JsonArray(), ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 1 } }
		};
		await DataAsync(writer, start.ToJsonString(), ct);

		int index = 0;
		if (reply.Thinking.Length > 0)
		{
			await BlockAsync(writer, index, new JsonObject { ["type"] = "thinking", ["thinking"] = string.Empty },
				new JsonObject { ["type"] = "thinking_delta", ["thinking"] = reply.Thinking }, ct);
			await DataAsync(writer, new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "sig_fake" } }.ToJsonString(), ct);
			await DataAsync(writer, new JsonObject { ["type"] = "content_block_stop", ["index"] = index }.ToJsonString(), ct);
			index++;
		}
		if (reply.Text.Length > 0)
		{
			await BlockAsync(writer, index, new JsonObject { ["type"] = "text", ["text"] = string.Empty },
				new JsonObject { ["type"] = "text_delta", ["text"] = reply.Text }, ct);
			await DataAsync(writer, new JsonObject { ["type"] = "content_block_stop", ["index"] = index }.ToJsonString(), ct);
			index++;
		}
		foreach ((string id, string name, string args) in reply.Calls)
		{
			await BlockAsync(writer, index, new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() },
				new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = args }, ct);
			await DataAsync(writer, new JsonObject { ["type"] = "content_block_stop", ["index"] = index }.ToJsonString(), ct);
			index++;
		}

		string stop = reply.Calls.Count > 0 ? "tool_use" : "end_turn";
		await DataAsync(writer, new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = stop }, ["usage"] = new JsonObject { ["output_tokens"] = 10 } }.ToJsonString(), ct);
		await DataAsync(writer, new JsonObject { ["type"] = "message_stop" }.ToJsonString(), ct);
		await writer.FlushAsync(ct);
	}

	private static async Task BlockAsync(StreamWriter writer, int index, JsonObject block, JsonObject delta, CancellationToken ct)
	{
		await DataAsync(writer, new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block }.ToJsonString(), ct);
		await DataAsync(writer,         new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta }.ToJsonString(), ct);
	}

	// ---- OpenAI Responses ----

	private static async Task WriteResponsesAsync(HttpListenerResponse response, FakeReply reply, string responseId, bool streaming, CancellationToken ct)
	{
		JsonArray output = new JsonArray();
		if (reply.Thinking.Length > 0)
			output.Add((JsonNode)new JsonObject { ["type"] = "reasoning", ["id"] = "rs_fake", ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = reply.Thinking }) });
		if (reply.Text.Length > 0)
			output.Add((JsonNode)new JsonObject { ["type"] = "message", ["id"] = "msg_fake", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = reply.Text }) });
		foreach ((string id, string name, string args) in reply.Calls)
			output.Add((JsonNode)new JsonObject { ["type"] = "function_call", ["id"] = $"fc_{id}", ["call_id"] = id, ["name"] = name, ["arguments"] = args });

		JsonObject final = new JsonObject
		{
			["id"]     = responseId,
			["object"] = "response",
			["status"] = "completed",
			["output"] = output,
			["usage"]  = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 10, ["total_tokens"] = 110 }
		};

		if (streaming)
			await WriteResponsesSseAsync(response, reply, responseId, final, ct);
		else
			await WriteJsonAsync(response, 200, final.ToJsonString(), ct);
	}

	private static async Task WriteResponsesSseAsync(HttpListenerResponse response, FakeReply reply, string responseId, JsonObject final, CancellationToken ct)
	{
		using StreamWriter writer = OpenSse(response, reply.CompactSse);
		JsonObject created        = new JsonObject { ["id"] = responseId, ["object"] = "response", ["status"] = "in_progress", ["usage"] = new JsonObject { ["input_tokens"] = 100 } };
		await DataAsync(writer, new JsonObject { ["type"] = "response.created", ["response"] = created }.ToJsonString(), ct);
		if (reply.Thinking.Length > 0)
			await DataAsync(writer, new JsonObject { ["type"] = "response.reasoning_summary_text.delta", ["delta"] = reply.Thinking }.ToJsonString(), ct);
		if (reply.Text.Length > 0)
			await DataAsync(writer, new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = reply.Text }.ToJsonString(), ct);
		await DataAsync(writer, new JsonObject { ["type"] = "response.completed", ["response"] = final }.ToJsonString(), ct);
		await writer.FlushAsync(ct);
	}

	// ---- Plumbing ----

	// A stream writer that remembers how this reply frames its data lines.
	private sealed class SseWriter : StreamWriter
	{
		public bool Compact { get; }

		public SseWriter(Stream stream, bool compact) : base(stream, new UTF8Encoding(false))
		{
			Compact = compact;
		}
	}

	private static StreamWriter OpenSse(HttpListenerResponse response, bool compact)
	{
		response.StatusCode  = 200;
		response.ContentType = "text/event-stream";
		response.SendChunked = true;
		return new SseWriter(response.OutputStream, compact);
	}

	private static async Task DataAsync(StreamWriter writer, string json, CancellationToken ct)
	{
		string prefix = writer is SseWriter { Compact: true } ? "data:" : "data: ";
		await writer.WriteAsync($"{prefix}{json}\n\n");
		await writer.FlushAsync(ct);
	}

	private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, string json, CancellationToken ct)
	{
		byte[] bytes             = Encoding.UTF8.GetBytes(json);
		response.StatusCode      = statusCode;
		response.ContentType     = "application/json";
		response.ContentLength64 = bytes.Length;
		await response.OutputStream.WriteAsync(bytes, ct);
		response.OutputStream.Close();
	}
}