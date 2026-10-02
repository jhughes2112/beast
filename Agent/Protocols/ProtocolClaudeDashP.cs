using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

// -- Anthropic subscription through Claude Code ("claude -p") -------------------------------------
// A subscription may only be driven through the Claude Code CLI, so this protocol runs one
// long-lived `claude -p` process per conversation, speaking stream-json on stdin/stdout. Claude Code
// runs its own agent loop; Beast keeps control of it by making Beast's tools the ONLY tools that
// exist (built-ins off, strict MCP) and serving them through McpBridge. When the model calls one,
// the MCP request is held open: the assistant turn returns to LlmService as an ordinary
// ProtocolResult, Beast dispatches the tools as it does for every protocol, and the committed results
// are what finally answer the held requests. Claude Code then makes its next model call, which is
// the next ExecuteAsync.
//
// Claude Code owns the native conversation and cannot be handed one, so any break in step — a new
// conversation, a failed or interrupted turn, a changed tool set, an idle process reaped — ends the
// process, and the next turn starts a fresh one seeded from canonical: the conversation itself when
// it is still only user text, otherwise a transcript of it. Usage comes from the model's own stream
// events (the same counters the Messages API reports); cost is whatever the model's config prices,
// which for a subscription is zero.
public sealed class ProtocolClaudeDashP
{
	private const string kServerName = "beast";
	private const string kToolPrefix = "mcp__beast__";

	// A held MCP call can wait as long as any Beast tool runs — a subagent can take hours.
	private const string kToolTimeoutMs = "86400000";

	// A process nobody has driven for this long is ended; the next turn reseeds a fresh one. This is
	// what reclaims the process of a finished subagent, whose last call is never answered.
	private static readonly TimeSpan kIdleLifetime = TimeSpan.FromHours(1);

	private readonly object _lock   = new object();
	private          string _system = string.Empty;

	private          Process?             _process;
	private          Channel<JsonObject>? _events;
	private          string               _routeUrl      = string.Empty;
	private          string               _configPath    = string.Empty;
	private          string               _promptPath    = string.Empty;
	private          string               _toolSignature = string.Empty;
	private          List<ToolDefinition> _tools         = new List<ToolDefinition>();
	private readonly Queue<string>        _stderrTail    = new Queue<string>();
	private          Timer?               _idleTimer;
	private          bool                 _executing;

	// A turn that ended without tool calls is followed by Claude Code's end-of-turn result event,
	// which arrives after this protocol has already returned the turn; the next read consumes it.
	private bool _awaitingResult;

	// User content (text and image blocks) committed since the last thing sent to the process.
	private readonly List<JsonObject> _unsent = new List<JsonObject>();

	// The tool calls of the turn last returned, as the model made them (before any repair), their
	// committed results, and the held MCP requests waiting for them. Released once Beast has
	// committed every result and asked for the next turn.
	private readonly List<SemanticToolCall>         _outstanding = new List<SemanticToolCall>();
	private readonly Dictionary<string, ToolResult> _results     = new Dictionary<string, ToolResult>(StringComparer.Ordinal);
	private readonly HashSet<string>                _answered    = new HashSet<string>(StringComparer.Ordinal);
	private readonly List<(string Name, JsonObject Arguments, TaskCompletionSource<JsonObject> Reply)> _waiters = new List<(string Name, JsonObject Arguments, TaskCompletionSource<JsonObject> Reply)>();
	private readonly List<JsonObject> _steering = new List<JsonObject>();
	private          bool             _released;

	public void Rehydrate(IReadOnlyList<CanonicalMessage> messages)
	{
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is SystemMessage sm)
				_system = sm.Text;
		}
	}

	public void OnSystemMessage(string text)
	{
		_system = text;
	}

	public void OnUserMessage(string text)
	{
		lock (_lock)
			AddUserContent(_unsent, text, null);
	}

	public void OnUserMessage(string text, IReadOnlyList<MediaAttachment> attachments)
	{
		lock (_lock)
			AddUserContent(_unsent, text, attachments);
	}

	// Claude Code already holds the turn it produced; there is nothing to mirror.
	public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
	{
	}

	public void OnToolResult(ToolResult result)
	{
		lock (_lock)
			_results[result.Id] = result;
	}

	// Ends the process for good. Called when the proxy discards this protocol.
	public void Dispose()
	{
		lock (_lock)
			StopLocked("protocol discarded");
	}

	public async Task<ProtocolResult> ExecuteAsync(LlmModel model, ListenerBundle bundle, List<ToolDefinition> tools, LiveUsageProgress onProgress, SessionLogger logger, CancellationToken cancellationToken)
	{
		ProtocolResult result;
		lock (_lock)
		{
			_executing = true;
			_idleTimer?.Dispose();
			_idleTimer = null;
		}

		try
		{
			string? startError = null;
			lock (_lock)
			{
				string signature = ToolSignature(tools);
				if (_process != null && (_process.HasExited || signature != _toolSignature))
					StopLocked(_process.HasExited ? "process exited" : "tool set changed");
				if (_process == null)
					startError = StartLocked(model, tools, signature, bundle.Canonical.Messages, logger);
				if (startError == null)
					DeliverLocked(model, logger);
			}

			if (startError != null)
				result = logger.ProtocolFailure(ProtocolResult.Failed(startError), model, DetectedProtocol.ClaudeDashP, "StartFailed", null, startError, null, null);
			else
				result = await ReadTurnAsync(model, bundle, onProgress, logger, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			// An interrupted turn leaves Claude Code mid-flight; the next turn starts over from canonical.
			lock (_lock)
				StopLocked("turn interrupted");
			throw;
		}
		finally
		{
			lock (_lock)
			{
				_executing = false;
				if (_process != null)
					_idleTimer = new Timer(_ => ReapIdle(), null, kIdleLifetime, Timeout.InfiniteTimeSpan);
			}
		}
		return result;
	}

	// ---- MCP side (called from McpBridge) ----

	public JsonArray McpToolList()
	{
		JsonArray list = new JsonArray();
		lock (_lock)
		{
			foreach (ToolDefinition tool in _tools)
			{
				list.Add((JsonNode)new JsonObject
				{
					["name"]        = tool.Function.Name,
					["description"] = tool.Function.Description,
					["inputSchema"] = tool.Function.Parameters.DeepClone()
				});
			}
		}
		return list;
	}

	// Held until Beast has committed this call's result and asked for the next turn.
	public async Task<JsonObject> CallToolAsync(string name, JsonObject arguments)
	{
		TaskCompletionSource<JsonObject> reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_lock)
		{
			_waiters.Add((name, arguments, reply));
			AnswerWaitersLocked();
		}
		return await reply.Task;
	}

	// ---- Process lifecycle ----

	private string? StartLocked(LlmModel model, List<ToolDefinition> tools, string signature, IReadOnlyList<CanonicalMessage> canonical, SessionLogger logger)
	{
		string? error = null;

		// A fresh process knows nothing, so everything it needs comes from canonical: the system
		// prompt, and the conversation as the first user message.
		_unsent.Clear();
		ResetTurnStateLocked();
		_awaitingResult = false;
		foreach (CanonicalMessage msg in canonical)
		{
			if (msg is SystemMessage sm)
				_system = sm.Text;
		}
		SeedFromCanonical(canonical, _unsent);

		_tools         = new List<ToolDefinition>(tools);
		_toolSignature = signature;
		_routeUrl      = McpBridge.Shared().Register(this);

		string stem       = Path.Combine(Path.GetTempPath(), $"beast-claude-{Guid.NewGuid():N}");
		_configPath       = $"{stem}-mcp.json";
		_promptPath       = $"{stem}-system.txt";
		JsonObject config = new JsonObject
		{
			["mcpServers"] = new JsonObject { [kServerName] = new JsonObject { ["type"] = "http", ["url"] = _routeUrl } }
		};
		WritePrivateFile(_configPath, config.ToJsonString());
		WritePrivateFile(_promptPath,               _system);

		List<string> args = new List<string>
		{
			"-p",
			"--input-format", "stream-json",
			"--output-format", "stream-json",
			"--verbose",
			"--include-partial-messages",
			"--no-session-persistence",
			"--tools", "",
			"--strict-mcp-config",
			"--mcp-config", _configPath,
			"--allowedTools=mcp__beast",
			"--permission-prompts", "none",
			"--setting-sources", "",
			"--disable-slash-commands",
			"--system-prompt-file", _promptPath,
			"--model", ClaudeCode.CliModel(model.ConfigId),
			// Beast compacts on its own measurements; a window this large keeps Claude Code's own
			// compaction from ever rewriting the conversation underneath it.
			"--autocompact", "1M"
		};
		string? effort = ReasoningEffort.AnthropicEffort(model.Config.ReasoningEffort);
		if (effort != null)
		{
			args.Add("--effort");
			args.Add(effort);
		}

		ProcessStartInfo psi                = ClaudeCode.StartInfo(args, model.ApiKey);
		psi.Environment["MCP_TOOL_TIMEOUT"] = kToolTimeoutMs;

		Process process = new Process { StartInfo = psi };
		bool    started = false;
		try
		{
			started = process.Start();
		}
		catch (Win32Exception ex)
		{
			error = $"The Claude Code CLI (claude) could not be started: {ex.Message}";
		}

		if (started)
		{
			process.StandardInput.AutoFlush = true;
			_stderrTail.Clear();
			process.ErrorDataReceived += (_, e) => RememberStderr(e.Data);
			process.BeginErrorReadLine();

			Channel<JsonObject> events = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
			_events                    = events;
			_process                   = process;
			_ = PumpStdoutAsync(process, events);
			logger.Write(model.Config.Name, model.Endpoint, $"[claude-code] started pid {process.Id}: claude {string.Join(' ', args)}");
		}
		else
		{
			process.Dispose();
			McpBridge.Shared().Unregister(_routeUrl);
			DeleteQuietly(_configPath);
			DeleteQuietly(_promptPath);
			if (error == null)
				error = "The Claude Code CLI (claude) could not be started.";
		}
		return error;
	}

	// Kills the process (if any) and releases everything tied to it. Held MCP requests are answered
	// so no bridge request outlives the process it belonged to.
	private void StopLocked(string reason)
	{
		if (_process != null)
		{
			Console.Error.WriteLine($"[ClaudeDashP] Ending Claude Code pid {SafePid(_process)}: {reason}.");
			try
			{
				if (!_process.HasExited)
					_process.Kill(true);
			}
			catch (Exception)
			{
			}
			_process.Dispose();
			_process = null;
		}
		_events?.Writer.TryComplete();
		_events = null;

		if (_routeUrl.Length > 0)
		{
			McpBridge.Shared().Unregister(_routeUrl);
			_routeUrl = string.Empty;
		}
		DeleteQuietly(_configPath);
		DeleteQuietly(_promptPath);
		_configPath = string.Empty;
		_promptPath = string.Empty;

		foreach ((string _, JsonObject _, TaskCompletionSource<JsonObject> reply) in _waiters)
			reply.TrySetResult(TextReply($"[the {kServerName} session ended before this call was answered]", true));
		_waiters.Clear();
		ResetTurnStateLocked();
		_awaitingResult = false;
		_idleTimer?.Dispose();
		_idleTimer = null;
	}

	private void ReapIdle()
	{
		lock (_lock)
		{
			if (!_executing && _process != null)
				StopLocked($"idle for {kIdleLifetime.TotalMinutes:0} minutes");
		}
	}

	private async Task PumpStdoutAsync(Process process, Channel<JsonObject> events)
	{
		try
		{
			for (; ; )
			{
				string? line = await process.StandardOutput.ReadLineAsync();
				if (line == null)
					break;
				JsonObject? evt = null;
				try
				{
					evt = JsonNode.Parse(line) as JsonObject;
				}
				catch (JsonException)
				{
					RememberStderr(line);
				}
				if (evt != null)
					events.Writer.TryWrite(evt);
			}
		}
		catch (Exception ex)
		{
			RememberStderr($"stdout read failed: {ex.Message}");
		}
		events.Writer.TryComplete();
	}

	private void RememberStderr(string? line)
	{
		if (!string.IsNullOrWhiteSpace(line))
		{
			lock (_stderrTail)
			{
				_stderrTail.Enqueue(line);
				while (_stderrTail.Count > 20)
					_stderrTail.Dequeue();
			}
		}
	}

	private string StderrTail()
	{
		lock (_stderrTail)
			return _stderrTail.Count > 0 ? string.Join("\n", _stderrTail) : "(no output)";
	}

	// ---- Sending ----

	// Hands the process what the next turn needs: the results of the last turn's tool calls (through
	// the held MCP requests), or a new user message when the model is waiting on the user.
	private void DeliverLocked(LlmModel model, SessionLogger logger)
	{
		if (_outstanding.Count > 0)
		{
			// User text committed alongside the results has no message of its own to go in — Claude
			// Code is mid-turn — so it rides on the last result, as the Messages API would merge it
			// into the same user turn.
			_steering.AddRange(_unsent);
			_unsent.Clear();
			_released = true;
			AnswerWaitersLocked();
		}
		else
		{
			// Nothing new would leave the model with no turn to take; only a retry lands here.
			if (_unsent.Count == 0)
				AddUserContent(_unsent, "Continue.", null);

			JsonArray content = new JsonArray();
			foreach (JsonObject block in _unsent)
				content.Add((JsonNode)block.DeepClone());
			_unsent.Clear();

			JsonObject line = new JsonObject
			{
				["type"]               = "user",
				["message"]            = new JsonObject { ["role"] = "user", ["content"] = content },
				["parent_tool_use_id"] = null
			};
			string text = line.ToJsonString();
			logger.Write(model.Config.Name, model.Endpoint, text);
			_process!.StandardInput.WriteLine(text);
		}
	}

	// Answers every held request whose call is now settled. A call Beast never ran (the turn was
	// dropped, or it was cancelled) is answered as not run, so Claude Code is never left waiting.
	private void AnswerWaitersLocked()
	{
		if (_released)
		{
			string lastId = _outstanding.Count > 0 ? _outstanding[_outstanding.Count - 1].Id : string.Empty;
			foreach ((string name, JsonObject arguments, TaskCompletionSource<JsonObject> reply) in _waiters)
			{
				SemanticToolCall? call = MatchCall(name, arguments);
				JsonObject        answer;
				if (call == null)
				{
					answer = TextReply($"[{name} was not among the calls of this turn]", true);
				}
				else
				{
					_answered.Add(call.Id);
					answer = _results.TryGetValue(call.Id, out ToolResult? result) ? ToolReply(result) : TextReply($"[the {name} call was not run]", true);
					if (call.Id == lastId && _steering.Count > 0)
					{
						JsonArray content = (JsonArray)answer["content"]!;
						foreach (JsonObject block in _steering)
							content.Add((JsonNode)McpContent(block));
						_steering.Clear();
					}
				}
				reply.TrySetResult(answer);
			}
			_waiters.Clear();
		}
	}

	// The unanswered call this request is for: same name and same arguments, or failing an exact
	// match, the first unanswered call of that name (Claude Code runs them in order).
	private SemanticToolCall? MatchCall(string name, JsonObject arguments)
	{
		SemanticToolCall? match = null;
		foreach (SemanticToolCall call in _outstanding)
		{
			if (_answered.Contains(call.Id) || call.Name != name)
				continue;
			if (match == null)
				match = call;
			JsonNode? parsed = null;
			try
			{
				parsed = JsonNode.Parse(call.ArgumentsJson);
			}
			catch (JsonException)
			{
			}
			if (JsonNode.DeepEquals(parsed, arguments))
			{
				match = call;
				break;
			}
		}
		return match;
	}

	private void ResetTurnStateLocked()
	{
		_outstanding.Clear();
		_results.Clear();
		_answered.Clear();
		_steering.Clear();
		_released = false;
	}

	// ---- Reading a turn ----

	private sealed class Block
	{
		public          string        Type  = string.Empty;
		public          string        Id    = string.Empty;
		public          string        Name  = string.Empty;
		public readonly StringBuilder Text  = new StringBuilder();
		public readonly StringBuilder Input = new StringBuilder();
	}

	// Reads events until the model finishes one message: a message with tool calls returns them
	// (their results are delivered on the next ExecuteAsync), and one without ends the turn.
	private async Task<ProtocolResult> ReadTurnAsync(LlmModel model, ListenerBundle bundle, LiveUsageProgress onProgress, SessionLogger logger, CancellationToken cancellationToken)
	{
		ChannelReader<JsonObject> reader;
		lock (_lock)
			reader = _events!.Reader;

		TimeSpan                     idleTimeout = TimeSpan.FromSeconds(ProtocolHelpers.RequestTimeoutSeconds(model));
		SortedDictionary<int, Block> blocks      = new SortedDictionary<int, Block>();
		string?                      openTag     = null;
		int             freshInput    = 0;
		int             cacheWrite    = 0;
		int             cacheRead     = 0;
		int             output        = 0;
		int             streamedChars = 0;
		string          stopReason    = "end_turn";
		ProtocolResult? result        = null;

		using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		try
		{
			while (result == null)
			{
				idle.CancelAfter(idleTimeout);
				JsonObject? evt = null;
				try
				{
					if (await reader.WaitToReadAsync(idle.Token))
						reader.TryRead(out evt);
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					lock (_lock)
						StopLocked("no output within the request timeout");
					result = logger.ProtocolFailure(ProtocolResult.Transient($"Claude Code produced nothing for {idleTimeout.TotalSeconds:0}s", null),
						model, DetectedProtocol.ClaudeDashP, "Timeout", null, "idle timeout", null, null);
					break;
				}

				if (evt == null)
				{
					string tail = StderrTail();
					lock (_lock)
						StopLocked("process exited");
					result = logger.ProtocolFailure(ProtocolResult.Transient($"Claude Code exited mid-turn: {tail}", null),
						model, DetectedProtocol.ClaudeDashP, "ProcessExited", null, tail, null, null);
					break;
				}

				string type = evt["type"]?.ToString() ?? string.Empty;
				if (type == "stream_event" && evt["event"] is JsonObject inner)
				{
					string innerType = inner["type"]?.ToString() ?? string.Empty;
					if (innerType == "message_start")
					{
						// The model is answering again, so every held call of the previous turn has been answered.
						lock (_lock)
							ResetTurnStateLocked();
						JsonNode? usage = inner["message"]?["usage"];
						freshInput      = ReadInt(usage, "input_tokens",                freshInput);
						cacheWrite      = ReadInt(usage, "cache_creation_input_tokens", cacheWrite);
						cacheRead       = ReadInt(usage, "cache_read_input_tokens",      cacheRead);
					}
					else if (innerType == "content_block_start")
					{
						int        index = inner["index"]?.GetValue<int>() ?? blocks.Count;
						JsonObject raw   = inner["content_block"] as JsonObject ?? new JsonObject();
						blocks[index]    = new Block
						{
							Type = raw["type"]?.ToString() ?? "text",
							Id   = raw["id"]?.ToString() ?? string.Empty,
							Name = raw["name"]?.ToString() ?? string.Empty
						};
					}
					else if (innerType == "content_block_delta" && blocks.TryGetValue(inner["index"]?.GetValue<int>() ?? -1, out Block? block))
					{
						JsonNode? delta     = inner["delta"];
						string    deltaType = delta?["type"]?.ToString() ?? string.Empty;
						if (deltaType == "text_delta")
						{
							string text = delta?["text"]?.ToString() ?? string.Empty;
							block.Text.Append(text);
							streamedChars += text.Length;
							if (openTag == StreamTag.Assistant || !string.IsNullOrWhiteSpace(text))
								openTag = StreamChunk(bundle, openTag, StreamTag.Assistant, text);
						}
						else if (deltaType == "thinking_delta")
						{
							string thinking = delta?["thinking"]?.ToString() ?? string.Empty;
							block.Text.Append(thinking);
							streamedChars += thinking.Length;
							openTag        = StreamChunk(bundle, openTag, StreamTag.Thinking, thinking);
						}
						else if (deltaType == "input_json_delta")
						{
							string partial = delta?["partial_json"]?.ToString() ?? string.Empty;
							block.Input.Append(partial);
							streamedChars += partial.Length;
						}
					}
					else if (innerType == "message_delta")
					{
						stopReason      = inner["delta"]?["stop_reason"]?.ToString() ?? stopReason;
						JsonNode? usage = inner["usage"];
						output          = ReadInt(usage, "output_tokens",                   output);
						freshInput      = ReadInt(usage, "input_tokens",                freshInput);
						cacheWrite      = ReadInt(usage, "cache_creation_input_tokens", cacheWrite);
						cacheRead       = ReadInt(usage, "cache_read_input_tokens",      cacheRead);
					}
					else if (innerType == "message_stop")
					{
						result = CompleteMessage(model, blocks, freshInput, cacheWrite, cacheRead, output, stopReason);
					}

					int liveOutput = output > 0 ? output : streamedChars / 4;
					onProgress(freshInput + cacheWrite + cacheRead, liveOutput, Cost(model, freshInput, cacheWrite, cacheRead, liveOutput), cacheWrite + cacheRead);
				}
				else if (type == "result")
				{
					result = OnResultEvent(evt, model, logger);
				}
				else if (type == "system")
				{
					result = OnSystemEvent(evt, model, bundle, logger);
				}
				// assistant / user echoes restate what the stream events already carried.
			}
		}
		finally
		{
			if (openTag != null)
				bundle.Transport?.OnStreamEnd(openTag);
		}

		// The loop only ends once a result is set.
		return result!;
	}

	// Opens (or switches to) the stream block for tag and sends one chunk; returns the open tag.
	private static string StreamChunk(ListenerBundle bundle, string? openTag, string tag, string chunk)
	{
		if (openTag != tag)
		{
			if (openTag != null)
				bundle.Transport?.OnStreamEnd(openTag);
			bundle.Transport?.OnStreamStart(tag);
		}
		bundle.Transport?.OnStreamChunk(tag, chunk);
		return tag;
	}

	private ProtocolResult CompleteMessage(LlmModel model, SortedDictionary<int, Block> blocks, int freshInput, int cacheWrite, int cacheRead, int output, string stopReason)
	{
		StringBuilder          text      = new StringBuilder();
		StringBuilder          thinking  = new StringBuilder();
		List<SemanticToolCall> toolCalls = new List<SemanticToolCall>();
		foreach ((int _, Block block) in blocks)
		{
			if (block.Type == "text")
			{
				text.Append(block.Text);
			}
			else if (block.Type == "thinking")
			{
				thinking.Append(block.Text);
			}
			else if (block.Type == "tool_use")
			{
				string name = block.Name.StartsWith(kToolPrefix, StringComparison.Ordinal) ? block.Name.Substring(kToolPrefix.Length) : block.Name;
				toolCalls.Add(new SemanticToolCall { Id = block.Id, Name = name, ArgumentsJson = block.Input.Length > 0 ? block.Input.ToString() : "{}" });
			}
		}

		lock (_lock)
		{
			// Copies: the payload's calls may be repaired in place before commit, but Claude Code
			// will ask for them by the names and arguments the model actually used.
			foreach (SemanticToolCall call in toolCalls)
				_outstanding.Add(new SemanticToolCall { Id = call.Id, Name = call.Name, ArgumentsJson = call.ArgumentsJson });
			_awaitingResult = toolCalls.Count == 0;
		}

		TokenUsageInfo usage = new TokenUsageInfo
		{
			PromptTokens     = freshInput + cacheWrite + cacheRead,
			CompletionTokens = output,
			CachedTokens     = cacheWrite + cacheRead
		};
		string finishReason = toolCalls.Count > 0 ? "tool_calls" : stopReason;
		return ProtocolResult.Succeeded(new ProtocolCallPayload(text.ToString(), thinking.ToString(), toolCalls, new List<ToolResult>(), finishReason, usage, Cost(model, freshInput, cacheWrite, cacheRead, output)));
	}

	// Claude Code's end-of-turn summary. After a turn this protocol already returned, it is only the
	// marker that the turn closed. Arriving instead of a message, it is the reason there was none.
	private ProtocolResult? OnResultEvent(JsonObject evt, LlmModel model, SessionLogger logger)
	{
		ProtocolResult? result = null;
		bool            expected;
		lock (_lock)
		{
			expected        = _awaitingResult;
			_awaitingResult = false;
		}

		bool   isError = evt["is_error"] is JsonValue flag && flag.TryGetValue(out bool failed) && failed;
		string message = evt["result"]?.ToString() ?? evt["subtype"]?.ToString() ?? "no result";
		if (!expected || isError)
		{
			int status = evt["api_error_status"] is JsonValue code && code.TryGetValue(out int value) ? value : 0;
			lock (_lock)
				StopLocked($"turn ended without a message: {message}");

			ProtocolResult failure;
			string         kind;
			if (status == 429)
			{
				failure = ProtocolResult.RateLimited(null);
				kind    = "RateLimited";
			}
			else if (ProtocolHelpers.IsContextOverflow(message))
			{
				failure = ProtocolResult.ContextFull(message);
				kind    = "ContextOverflow";
			}
			else if (status == 401 || status == 403 || (status >= 400 && status < 500 && status != 408))
			{
				failure = ProtocolResult.FailedHttp(message, status);
				kind    = "ClientError";
			}
			else
			{
				failure = ProtocolResult.Transient(message, null);
				kind    = "TurnFailed";
			}
			result = logger.ProtocolFailure(failure, model, DetectedProtocol.ClaudeDashP, kind, status > 0 ? status : null, message, evt.ToJsonString(), null);
		}
		return result;
	}

	// The init event confirms the bridge connected and states the model that will actually answer;
	// a retry event is Claude Code backing off on its own and is worth showing.
	private ProtocolResult? OnSystemEvent(JsonObject evt, LlmModel model, ListenerBundle bundle, SessionLogger logger)
	{
		ProtocolResult? result  = null;
		string          subtype = evt["subtype"]?.ToString() ?? string.Empty;
		if (subtype == "init")
		{
			string status = "missing";
			if (evt["mcp_servers"] is JsonArray servers)
			{
				foreach (JsonNode? server in servers)
				{
					if (server?["name"]?.ToString() == kServerName)
						status = server["status"]?.ToString() ?? "unknown";
				}
			}
			logger.Note($"[claude-code] session {evt["session_id"]} on {evt["model"]}; tool server {status}");
			if (status != "connected")
			{
				string message = $"Claude Code could not reach Beast's tool server (status: {status}).";
				lock (_lock)
					StopLocked(message);
				result = logger.ProtocolFailure(ProtocolResult.Failed(message), model, DetectedProtocol.ClaudeDashP, "ToolServer", null, message, evt.ToJsonString(), null);
			}
		}
		else if (subtype == "api_retry")
		{
			bundle.Transport?.Status($"Claude Code retrying: {evt["error"]?.ToString() ?? evt.ToJsonString()}");
		}
		return result;
	}

	// ---- Content shapes ----

	// The first message of a fresh process. A conversation that is still only user turns goes over
	// as itself; anything further along goes as a transcript, since Claude Code cannot be handed
	// assistant turns or tool results it did not produce.
	private static void SeedFromCanonical(IReadOnlyList<CanonicalMessage> canonical, List<JsonObject> into)
	{
		bool onlyUser = true;
		foreach (CanonicalMessage msg in canonical)
		{
			if (msg is AssistantMessage || msg is ToolResultMessage)
				onlyUser = false;
		}

		if (onlyUser)
		{
			foreach (CanonicalMessage msg in canonical)
			{
				if (msg is UserMessage um)
					AddUserContent(into, um.Text, um.Attachments);
			}
		}
		else
		{
			string transcript = string.Concat(Summarizer.RenderTranscript(canonical));
			AddUserContent(into, $"[This conversation continues in a new session. Everything so far follows as a transcript; carry on from where it ends.]\n\n{transcript}", null);
		}
	}

	private static void AddUserContent(List<JsonObject> into, string text, IReadOnlyList<MediaAttachment>? attachments)
	{
		if (!string.IsNullOrWhiteSpace(text))
			into.Add(new JsonObject { ["type"] = "text", ["text"] = text });
		if (attachments != null)
		{
			foreach (MediaAttachment att in attachments)
			{
				if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
					into.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = att.MimeType, ["data"] = att.Base64Data } });
				else
					into.Add(new JsonObject { ["type"] = "text", ["text"] = $"[A {att.MimeType} attachment was supplied, but this provider does not accept that input type.]" });
			}
		}
	}

	// A message content block in MCP's shape (images carry data/mimeType rather than a source).
	private static JsonObject McpContent(JsonObject block)
	{
		JsonObject content;
		if (block["type"]?.ToString() == "image")
			content = new JsonObject { ["type"] = "image", ["data"] = block["source"]?["data"]?.ToString() ?? string.Empty, ["mimeType"] = block["source"]?["media_type"]?.ToString() ?? string.Empty };
		else
			content = new JsonObject { ["type"] = "text", ["text"] = $"[user message]\n{block["text"]}" };
		return content;
	}

	// Beast's tool result as an MCP result: the same text every protocol sends, plus the media file
	// when the result carries one.
	private static JsonObject ToolReply(ToolResult result)
	{
		string text = result.StdOut;
		if (!string.IsNullOrEmpty(result.StdErr))
			text = $"{text}\nstderr: {result.StdErr}";

		JsonObject reply   = TextReply(text, false);
		JsonArray  content = (JsonArray)reply["content"]!;
		if (result.MediaPath != null && result.MediaMimeType != null && result.MediaMimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && File.Exists(result.MediaPath))
			content.Add((JsonNode)new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(File.ReadAllBytes(result.MediaPath)), ["mimeType"] = result.MediaMimeType });
		return reply;
	}

	private static JsonObject TextReply(string text, bool isError)
	{
		return new JsonObject
		{
			["content"] = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text }),
			["isError"] = isError
		};
	}

	// ---- Helpers ----

	private static string ToolSignature(List<ToolDefinition> tools)
	{
		StringBuilder sb = new StringBuilder();
		foreach (ToolDefinition tool in tools)
			sb.Append(tool.Function.Name).Append('\n');
		return sb.ToString();
	}

	private static decimal Cost(LlmModel model, int freshInput, int cacheWrite, int cacheRead, int output)
	{
		return (freshInput / 1_000_000m) * model.Config.Cost.Input
			 + (cacheWrite / 1_000_000m) * model.Config.Cost.CacheWrite
			 + (cacheRead / 1_000_000m) * model.Config.Cost.CacheRead
			 + (output / 1_000_000m) * model.Config.Cost.Output;
	}

	// A count kept only when actually reported, so a thin restatement never erases a real one.
	private static int ReadInt(JsonNode? node, string key, int current)
	{
		int value = current;
		if (node is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue(out int reported) && reported > 0)
			value = reported;
		return value;
	}

	// The config file holds the bridge route's secret: only the agent's own user may read it.
	private static void WritePrivateFile(string path, string text)
	{
		FileStreamOptions options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		using StreamWriter writer = new StreamWriter(path, Encoding.UTF8, options);
		writer.Write(text);
	}

	private static void DeleteQuietly(string path)
	{
		try
		{
			if (path.Length > 0 && File.Exists(path))
				File.Delete(path);
		}
		catch (Exception)
		{
		}
	}

	private static string SafePid(Process process)
	{
		string pid;
		try
		{
			pid = process.Id.ToString();
		}
		catch (Exception)
		{
			pid = "?";
		}
		return pid;
	}
}