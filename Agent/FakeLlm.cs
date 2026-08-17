using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// A zero-cost chaos model: a tiny OpenAI Chat Completions server that answers every request with
// random-but-well-formed behavior — reasoning deltas, text deltas, fabricated tool calls against
// the tools actually offered, honest usage figures, and a hard 50k-token context window enforced
// with the standard OpenAI overflow 400. Point a settings endpoint at it (baseUrl
// http://localhost:13137/v1) and it can be driven interactively like any provider, for free; the
// test harness drives it end-to-end to prove the turn loop and compaction survive a model that
// fills the window. Token accounting is a deterministic chars/4 estimate, so the same conversation
// always measures the same — the numbers it reports in usage are the numbers it enforces.
public class FakeLlm
{
	public const string ModelId       = "fake-chaos";
	public const int    ContextWindow = 50000;
	public const int    DefaultPort   = 13137;

	// Put this anywhere in a conversation and every request for it is rejected with the account
	// error below, so the "unpaid account looks exactly like an over-window prompt" case can be
	// driven on demand. Chosen to be something no real conversation would contain.
	public const string BillingSentinel  = "[[fake-llm-out-of-credit]]";
	public const string BillingRejection = "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits.\"},\"request_id\":\"req_fake\"}";

	// Ask for this model id and every request is rejected as over-window, however small it really
	// is: the provider whose real limit is nothing like the configured one, so compacting never
	// satisfies it. Keyed on the requested MODEL rather than on anything in the conversation, so a
	// caller that chops the conversation into pieces can never accidentally escape it.
	public const string AlwaysFullModelId = "fake-chaos-always-full";

	// A server that dies on a big prompt instead of complaining about it: over the threshold it drops
	// the connection mid-response, which reaches the client as a plain transient network failure with
	// no status and no "too long" text anywhere. This is what a real llama-server did, and treating
	// it as terminal killed a compaction that only needed a smaller chunk. Under the threshold it
	// answers normally, so a caller that shrinks its way down still gets its summary.
	public const string ChokesOnBigPromptModelId = "fake-chaos-chokes";
	public const int    ChokeThresholdChars      = 20000;

	// A server that counts the completion RESERVATION against the window the way llama-server does:
	// prompt plus max_tokens must fit inside -c, or the request is rejected as over-window before a
	// single token is generated. Answers normally otherwise.
	public const string ReservationModelId = "fake-chaos-reservation";

	// A server that reports usage ONLY in the trailing chunk, which is what plain OpenAI does. Every
	// other id here states the prompt size as the stream opens, so this is the only one on which the
	// caller's provisional (pre-usage) reporting is observable at all.
	public const string LateUsageModelId = "fake-chaos-late-usage";

	// Requests actually served since start. A caller that is supposed to have stopped can be checked
	// against it — "no further requests arrived" is the only honest way to assert a loop ended.
	private int _requestsServed;

	public int RequestsServed => Volatile.Read(ref _requestsServed);

	// Opt-in capture of the request bodies actually received. Off by default because the soak moves
	// millions of tokens through here and keeping them all would dwarf the test process. Turned on
	// around a single scenario, it answers the question no other assertion can: not "did the caller
	// think it sent something", but "what did the server actually get".
	private readonly List<string> _recorded   = new List<string>();
	private readonly object       _recordGate = new object();
	private          bool         _recording;

	public void StartRecording()
	{
		lock (_recordGate)
		{
			_recorded.Clear();
			_recording = true;
		}
	}

	public List<string> StopRecording()
	{
		lock (_recordGate)
		{
			_recording = false;
			return new List<string>(_recorded);
		}
	}

	// Percent chances for each behavior roll, together shaping a plausibly chatty agent model.
	private const int kReasoningChance  = 60;
	private const int kToolCallChance   = 55;
	private const int kSecondCallChance = 25;

	private readonly HttpListener _listener = new HttpListener();
	private readonly int          _port;

	public int    Port         => _port;
	public string BaseUrl      => $"http://localhost:{_port}/v1";
	public string ChatEndpoint => $"http://localhost:{_port}/v1/chat/completions";

	public FakeLlm(int port)
	{
		_port = port;
		_listener.Prefixes.Add($"http://localhost:{_port}/");
	}

	// Binds the port. False when it is already taken (e.g. another agent instance hosts it).
	public bool TryStart()
	{
		bool started = false;
		try
		{
			_listener.Start();
			started = true;
		}
		catch (HttpListenerException)
		{
		}
		return started;
	}

	// For tests: binds an arbitrary free port instead of the well-known one.
	public static FakeLlm? StartOnRandomPort()
	{
		FakeLlm? server = null;
		for (int attempt = 0; attempt < 10 && server == null; attempt++)
		{
			FakeLlm candidate = new FakeLlm(20000 + Random.Shared.Next(20000));
			if (candidate.TryStart())
				server = candidate;
		}
		return server;
	}

	// Accept loop; runs until the token cancels (which stops the listener and unblocks the wait).
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
			// Stop() lands here; shutdown, not an error.
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private async Task HandleSafeAsync(HttpListenerContext context, CancellationToken ct)
	{
		try
		{
			string path = context.Request.Url?.AbsolutePath ?? string.Empty;
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
			{
				await HandleChatAsync(context, ct);
			}
			else if (context.Request.HttpMethod == "GET" && path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
			{
				JsonObject entry   = new JsonObject { ["id"] = ModelId, ["object"] = "model", ["owned_by"] = "beast", ["context_length"] = ContextWindow, ["max_tokens"] = 8192 };
				JsonObject listing = new JsonObject { ["object"] = "list", ["data"] = new JsonArray(entry) };
				await WriteJsonAsync(context.Response, 200, listing.ToJsonString(), ct);
			}
			else
			{
				await WriteJsonAsync(context.Response, 404, "{\"error\":{\"message\":\"not found\"}}", ct);
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[FakeLlm] request failed: {ex.Message}");
			try
			{
				context.Response.Abort();
			}
			catch (Exception)
			{
			}
		}
	}

	// ---- Chat completions ----

	private async Task HandleChatAsync(HttpListenerContext context, CancellationToken ct)
	{
		string bodyText;
		using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
			bodyText = await reader.ReadToEndAsync(ct);

		JsonNode? body = null;
		try
		{
			body = JsonNode.Parse(bodyText);
		}
		catch (JsonException)
		{
		}
		if (body == null)
		{
			await WriteJsonAsync(context.Response, 400, "{\"error\":{\"message\":\"malformed request body\",\"type\":\"invalid_request_error\"}}", ct);
			return;
		}

		Interlocked.Increment(ref _requestsServed);

		lock (_recordGate)
		{
			if (_recording)
				_recorded.Add(bodyText);
		}

		// A conversation carrying the sentinel gets the account rejection a provider sends when the
		// bill is unpaid: a plain 400 whose body — not its status — is the only thing separating it
		// from an over-window rejection. Verbatim from a real Anthropic response.
		if (bodyText.Contains(BillingSentinel, StringComparison.Ordinal))
		{
			await WriteJsonAsync(context.Response, 400, BillingRejection, ct);
			return;
		}

		int    promptTokens   = EstimatePromptTokens(body);
		string requestedModel = body["model"]?.GetValue<string>() ?? string.Empty;

		// Choke: fail the oversized request with a bare server error. No status the client can read as
		// an overflow, no "too long" anywhere in the body — the shape of failure a local server gives
		// when a prompt kills it, where nothing but trying something smaller tells you what was wrong.
		if (string.Equals(requestedModel, ChokesOnBigPromptModelId, StringComparison.Ordinal) && bodyText.Length > ChokeThresholdChars)
		{
			await WriteJsonAsync(context.Response, 500, "{\"error\":{\"message\":\"internal server error\"}}", ct);
			return;
		}

		int maxTokens = ReadInt(body, "max_completion_tokens");
		if (maxTokens <= 0)
			maxTokens = ReadInt(body, "max_tokens");
		if (maxTokens <= 0)
			maxTokens = 4096;

		// llama-server charges the RESERVATION, not just the prompt: it refuses when prompt plus
		// max_tokens overruns -c, even though the prompt on its own fits with room to spare. A caller
		// that sizes its ceiling from a context length it has not measured yet trips this on the very
		// first request of every session, and the rejection is worded as overflow — which compacts,
		// which starts another unmeasured session, which trips it again.
		if (string.Equals(requestedModel, ReservationModelId, StringComparison.Ordinal) && promptTokens + maxTokens > ContextWindow)
		{
			JsonObject reject = new JsonObject
			{
				["error"] = new JsonObject
				{
					["code"]            = 400,
					["message"]         = $"request ({promptTokens + maxTokens} tokens) exceeds the available context size ({ContextWindow} tokens), try increasing it",
					["type"]            = "exceed_context_size_error",
					["n_prompt_tokens"] = promptTokens + maxTokens,
					["n_ctx"]           = ContextWindow,
				}
			};
			await WriteJsonAsync(context.Response, 400, reject.ToJsonString(), ct);
			return;
		}

		if (promptTokens >= ContextWindow || string.Equals(requestedModel, AlwaysFullModelId, StringComparison.Ordinal))
		{
			// The standard OpenAI overflow phrasing — ProtocolHelpers.IsContextOverflow matches
			// "maximum context length" and routes the caller into compaction, exactly as a real
			// provider's rejection would.
			JsonObject error = new JsonObject
			{
				["error"] = new JsonObject
				{
					["message"] = $"This model's maximum context length is {ContextWindow} tokens. However, your messages resulted in {promptTokens} tokens. Please reduce the length of the messages.",
					["type"]    = "invalid_request_error",
					["param"]   = "messages",
					["code"]    = "context_length_exceeded",
				}
			};
			await WriteJsonAsync(context.Response, 400, error.ToJsonString(), ct);
			return;
		}

		Turn turn   = BuildTurn(body, maxTokens);
		bool stream = body["stream"]?.GetValue<bool?>() ?? false;
		if (stream)
			await WriteSseAsync(context.Response, turn, promptTokens, !string.Equals(requestedModel, LateUsageModelId, StringComparison.Ordinal), ct);
		else
			await WriteCompletionAsync(context.Response, turn, promptTokens, ct);
	}

	// One generated response: what the "model" decided to say, think, and call this turn.
	private sealed class Turn
	{
		public string Reasoning = string.Empty;
		public string Text      = string.Empty;
		public List<(string Id, string Name, string Args)> Calls = new List<(string, string, string)>();
		public string FinishReason = "stop";

		public int CompletionTokens
		{
			get
			{
				int chars = Reasoning.Length + Text.Length;
				foreach ((string _, string _, string args) in Calls)
					chars += args.Length;
				return chars / 4 + 1;
			}
		}
	}

	// Rolls this turn's behavior inside the caller's output budget: maybe reasoning, then either
	// tool calls against the offered tools (always when tool_choice forces one) or plain text.
	private Turn BuildTurn(JsonNode body, int maxTokens)
	{
		Turn turn       = new Turn();
		int  charBudget = maxTokens * 4;

		if (charBudget <= 8)
		{
			// A tracer probe (max tokens 1): the answer does not matter, only the usage does.
			turn.Text = ".";
		}
		else
		{
			// The tools actually offered this turn, so fabricated calls always name a real one.
			List<(string Name, JsonObject? Schema)> tools = new List<(string, JsonObject?)>();
			JsonArray? toolsArr = body["tools"]?.AsArray();
			if (toolsArr != null)
			{
				foreach (JsonNode? entry in toolsArr)
				{
					JsonNode? fn   = entry?["function"];
					string?   name = fn?["name"]?.GetValue<string>();
					if (!string.IsNullOrEmpty(name))
						tools.Add((name!, fn!["parameters"] as JsonObject));
				}
			}

			// tool_choice: a named function must be called; "required" means some tool must be.
			string?   forcedName = body["tool_choice"]?["function"]?["name"]?.GetValue<string>();
			bool      mustCall   = false;
			JsonNode? choice     = body["tool_choice"];
			if (choice is JsonValue v)
			{
				string word = v.ToString();
				mustCall    = word == "required" || word == "any";
			}

			if (Random.Shared.Next(100) < kReasoningChance)
			{
				int length     = Math.Min(200 + Random.Shared.Next(2800), charBudget / 2);
				turn.Reasoning = RandomText(length);
				charBudget    -= length;
			}

			bool callTools = forcedName != null || mustCall || (tools.Count > 0 && Random.Shared.Next(100) < kToolCallChance);
			if (callTools && tools.Count > 0)
			{
				int count = forcedName != null ? 1 : (Random.Shared.Next(100) < kSecondCallChance ? 2 : 1);
				for (int i = 0; i < count && charBudget > 64; i++)
				{
					(string name, JsonObject? schema) = forcedName != null
						? FindTool(tools, forcedName)
						: tools[Random.Shared.Next(tools.Count)];
					string args = BuildArgs(schema, charBudget / 2);
					charBudget -= args.Length;
					turn.Calls.Add(($"call_{Guid.NewGuid():N}", name, args));
				}
				turn.FinishReason = "tool_calls";
			}
			else
			{
				int length = Math.Min(100 + Random.Shared.Next(1400), charBudget);
				turn.Text  = RandomText(length);
				if (length >= charBudget)
					turn.FinishReason = "length";
			}
		}
		return turn;
	}

	private static (string Name, JsonObject? Schema) FindTool(List<(string Name, JsonObject? Schema)> tools, string name)
	{
		(string, JsonObject?) found = tools[0];
		foreach ((string toolName, JsonObject? schema) in tools)
		{
			if (string.Equals(toolName, name, StringComparison.Ordinal))
			{
				found = (toolName, schema);
				break;
			}
		}
		return found;
	}

	// Fills a call's arguments from its JSON schema: every required property, plus occasional
	// optional ones. Strings get chunky random text (bloating the context is the point), except
	// path-ish names which get a plausible fake path; integers and booleans get small randoms.
	private static string BuildArgs(JsonObject? schema, int charBudget)
	{
		JsonObject  args       = new JsonObject();
		JsonObject? properties = schema?["properties"] as JsonObject;
		if (properties != null)
		{
			HashSet<string> required    = new HashSet<string>(StringComparer.Ordinal);
			JsonArray?      requiredArr = schema!["required"] as JsonArray;
			if (requiredArr != null)
			{
				foreach (JsonNode? name in requiredArr)
				{
					if (name != null)
						required.Add(name.ToString());
				}
			}

			foreach ((string name, JsonNode? prop) in properties)
			{
				if (!required.Contains(name) && Random.Shared.Next(100) >= 30)
					continue;

				string type = prop?["type"]?.GetValue<string>() ?? "string";
				if (type == "integer" || type == "number")
				{
					args[name] = Random.Shared.Next(1, 500);
				}
				else if (type == "boolean")
				{
					args[name] = Random.Shared.Next(2) == 0;
				}
				else if (name.Contains("path", StringComparison.OrdinalIgnoreCase) || name.Contains("folder", StringComparison.OrdinalIgnoreCase) || name.Contains("url", StringComparison.OrdinalIgnoreCase))
				{
					args[name] = $"fake/chaos_{Random.Shared.Next(1000)}.txt";
				}
				else
				{
					int length = Math.Min(200 + Random.Shared.Next(1000), Math.Max(16, charBudget / 4));
					args[name] = RandomText(length);
				}
			}
		}
		return args.ToJsonString();
	}

	// ---- Response writers ----

	private static async Task WriteSseAsync(HttpListenerResponse response, Turn turn, int promptTokens, bool openingUsage, CancellationToken ct)
	{
		response.StatusCode       = 200;
		response.ContentType      = "text/event-stream";
		response.SendChunked      = true;
		using StreamWriter writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false));

		await WriteChunkAsync(writer, Chunk(new JsonObject { ["role"] = "assistant" }, null), ct);

		// Usage split across the stream, the way real providers do it and the way that broke the
		// client's accounting: the prompt size is stated up front here, and the trailing usage chunk
		// below reports only what it learned at the end. A reader that keeps just the LAST usage
		// object it saw loses the prompt count entirely — so this shape is the regression test.
		// Plain OpenAI does NOT do this — it reports usage only in the trailing chunk — so a caller
		// that needs to show something during the stream has nothing but its own last measurement to
		// show. LateUsageModelId is that server, and it is the only one the provisional path is
		// visible on.
		if (openingUsage)
		{
			JsonObject opening = new JsonObject
			{
				["id"]      = "chatcmpl-fake",
				["object"]  = "chat.completion.chunk",
				["choices"] = new JsonArray(),
				["usage"]   = new JsonObject { ["prompt_tokens"] = promptTokens, ["total_tokens"] = promptTokens },
			};
			await writer.WriteAsync($"data: {opening.ToJsonString()}\n\n");
		}

		foreach (string slice in Slices(turn.Reasoning))
			await WriteChunkAsync(writer, Chunk(new JsonObject { ["reasoning_content"] = slice }, null), ct);

		foreach (string slice in Slices(turn.Text))
			await WriteChunkAsync(writer, Chunk(new JsonObject { ["content"] = slice }, null), ct);

		for (int i = 0; i < turn.Calls.Count; i++)
		{
			(string id, string name, string args) = turn.Calls[i];
			JsonObject open                       = new JsonObject
			{
				["tool_calls"] = new JsonArray(new JsonObject
				{
					["index"]    = i,
					["id"]       = id,
					["type"]     = "function",
					["function"] = new JsonObject { ["name"] = name, ["arguments"] = string.Empty }
				})
			};
			await WriteChunkAsync(writer, Chunk(open, null), ct);
			foreach (string slice in Slices(args))
			{
				JsonObject part = new JsonObject
				{
					["tool_calls"] = new JsonArray(new JsonObject
					{
						["index"]    = i,
						["function"] = new JsonObject { ["arguments"] = slice }
					})
				};
				await WriteChunkAsync(writer, Chunk(part, null), ct);
			}
		}

		await WriteChunkAsync(writer, Chunk(new JsonObject(), turn.FinishReason), ct);

		// Closing usage chunk in the stream_options.include_usage shape, carrying only the completion
		// count — the prompt was already reported above and is deliberately NOT restated here.
		JsonObject usageChunk = new JsonObject
		{
			["id"]      = "chatcmpl-fake",
			["object"]  = "chat.completion.chunk",
			["choices"] = new JsonArray(),
			["usage"]   = new JsonObject { ["completion_tokens"] = turn.CompletionTokens, ["total_tokens"] = promptTokens + turn.CompletionTokens },
		};
		await writer.WriteAsync($"data: {usageChunk.ToJsonString()}\n\n");
		await writer.WriteAsync("data: [DONE]\n\n");
		await writer.FlushAsync(ct);
	}

	private static async Task WriteCompletionAsync(HttpListenerResponse response, Turn turn, int promptTokens, CancellationToken ct)
	{
		JsonObject message = new JsonObject { ["role"] = "assistant", ["content"] = turn.Text };
		if (turn.Reasoning.Length > 0)
			message["reasoning_content"] = turn.Reasoning;
		if (turn.Calls.Count > 0)
		{
			JsonArray calls = new JsonArray();
			foreach ((string id, string name, string args) in turn.Calls)
			{
				calls.Add((JsonNode)new JsonObject
				{
					["id"]       = id,
					["type"]     = "function",
					["function"] = new JsonObject { ["name"] = name, ["arguments"] = args }
				});
			}
			message["tool_calls"] = calls;
		}

		JsonObject completion = new JsonObject
		{
			["id"]      = "chatcmpl-fake",
			["object"]  = "chat.completion",
			["model"]   = ModelId,
			["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = turn.FinishReason }),
			["usage"]   = Usage(promptTokens, turn.CompletionTokens),
		};
		await WriteJsonAsync(response, 200, completion.ToJsonString(), ct);
	}

	private static JsonObject Usage(int promptTokens, int completionTokens)
	{
		return new JsonObject
		{
			["prompt_tokens"]     = promptTokens,
			["completion_tokens"] = completionTokens,
			["total_tokens"]      = promptTokens + completionTokens,
		};
	}

	// Reads an int-valued body field, tolerating absence and non-numeric values as 0.
	private static int ReadInt(JsonNode body, string name)
	{
		int       value = 0;
		JsonNode? node  = body[name];
		if (node != null && int.TryParse(node.ToString(), out int parsed))
			value = parsed;
		return value;
	}

	// Splits text into small delta-sized slices so the client exercises real incremental streaming.
	private static List<string> Slices(string text)
	{
		List<string> slices = new List<string>();
		for (int i = 0; i < text.Length; i += 80)
			slices.Add(text.Substring(i, Math.Min(80, text.Length - i)));
		return slices;
	}

	private static string Chunk(JsonObject delta, string? finishReason)
	{
		JsonObject choice = new JsonObject { ["index"] = 0, ["delta"] = delta };
		if (finishReason != null)
			choice["finish_reason"] = finishReason;
		JsonObject chunk = new JsonObject
		{
			["id"]      = "chatcmpl-fake",
			["object"]  = "chat.completion.chunk",
			["choices"] = new JsonArray(choice),
		};
		return chunk.ToJsonString();
	}

	private static async Task WriteChunkAsync(StreamWriter writer, string json, CancellationToken ct)
	{
		await writer.WriteAsync($"data: {json}\n\n");
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

	// ---- Accounting and text generation ----

	// Deterministic chars/4 estimate over everything the request carries back to the "model":
	// message content (string or typed parts), retained reasoning, tool call arguments, and the
	// serialized tool definitions, plus a small per-message overhead. The same conversation always
	// measures the same, which is what makes the 50k window enforceable and the usage honest.
	private static int EstimatePromptTokens(JsonNode body)
	{
		int        chars    = 0;
		JsonArray? messages = body["messages"]?.AsArray();
		if (messages != null)
		{
			foreach (JsonNode? msg in messages)
			{
				if (msg == null)
					continue;
				chars += 16;

				JsonNode? content = msg["content"];
				if (content is JsonValue)
				{
					chars += content.ToString().Length;
				}
				else if (content is JsonArray parts)
				{
					foreach (JsonNode? part in parts)
						chars += part?["text"]?.ToString().Length ?? 0;
				}

				chars += msg["reasoning_content"]?.ToString().Length ?? 0;

				JsonArray? calls = msg["tool_calls"]?.AsArray();
				if (calls != null)
				{
					foreach (JsonNode? call in calls)
					{
						chars += call?["function"]?["name"]?.ToString().Length ?? 0;
						chars += call?["function"]?["arguments"]?.ToString().Length ?? 0;
					}
				}
			}
		}

		JsonArray? tools = body["tools"]?.AsArray();
		if (tools != null)
			chars += tools.ToJsonString().Length;

		return chars / 4;
	}

	private static readonly string[] kWords = new string[]
	{
		"chaos", "widget", "flux", "gadget", "random", "beast", "signal", "vector", "token", "stream",
		"branch", "kernel", "socket", "buffer", "cursor", "ledger", "window", "context", "compact", "turn",
	};

	private static string RandomText(int length)
	{
		StringBuilder sb = new StringBuilder(length + 16);
		while (sb.Length < length)
		{
			if (sb.Length > 0)
				sb.Append(Random.Shared.Next(12) == 0 ? ".\n" : " ");
			sb.Append(kWords[Random.Shared.Next(kWords.Length)]);
			if (Random.Shared.Next(6) == 0)
				sb.Append('-').Append(Random.Shared.Next(10000));
		}
		return sb.ToString(0, Math.Min(sb.Length, length));
	}
}