using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

// -- Google subscription through the Antigravity CLI ("agy acp") ----------------------------------
// A Google AI subscription is driven through the Antigravity CLI, so this protocol runs one
// long-lived `agy acp` process per conversation, speaking the Agent Client Protocol (JSON-RPC over
// stdio). Antigravity runs its own agent loop; Beast keeps control of it the way ProtocolClaudeDashP
// does: Beast's tools are the only tools that do anything, served through McpBridge, and when the
// model calls one the MCP request is held open. The turn so far returns to LlmService as an ordinary
// ProtocolResult, Beast dispatches the tool as it does for every protocol, and the committed result
// is what finally answers the held request. Antigravity then makes its next model call, which is the
// next ExecuteAsync. It runs a model call's tool calls one after another, so each turn carries one.
//
// The CLI takes neither a system prompt nor a tool list on its command line. Both come from a custom
// agent file in the process's private state folder: its body is Beast's system prompt, `tools: []`
// leaves out the built-in tools (three housekeeping ones remain, and are refused when asked for), and
// its mcpServers entry admits Beast's server, whose address and unlimited call time are in the
// folder's mcp_config.json — a server passed in session/new is cut off after three minutes.
//
// Antigravity owns the native conversation and cannot be handed one, so any break in step — a new
// conversation, a failed or interrupted turn, a changed tool set, an idle process reaped — ends the
// process, and the next turn starts a fresh one seeded from canonical: the conversation itself when
// it is still only user text, otherwise a transcript of it.
//
// Usage is the CLI's own: after each model call it reports that call's tokens (usage_update), and a
// finished prompt reports the totals of all its calls. A turn that ends in a tool call is charged its
// call's tokens as prompt; the last turn takes whatever of the totals is left, with all the output.
public sealed class ProtocolAntigravity : IMcpToolHost
{
	private const string kServerName = "beast";
	private const string kAgentName  = "beast";
	private const string kToolPrefix = "mcp_beast_";

	// The event CallToolAsync adds to the connection's queue when the model calls a tool.
	private const string kToolCallEvent = "beastToolCall";

	// A process nobody has driven for this long is ended; the next turn reseeds a fresh one. This is
	// what reclaims the process of a finished subagent, whose last call is never answered.
	private static readonly TimeSpan kIdleLifetime = TimeSpan.FromHours(1);
	private static readonly TimeSpan kSetupTimeout = TimeSpan.FromSeconds(60);

	private readonly object _lock   = new object();
	private          string _system = string.Empty;

	private AcpConnection?       _connection;
	private string               _sessionId     = string.Empty;
	private string               _stateDir      = string.Empty;
	private string               _routeUrl      = string.Empty;
	private string               _toolSignature = string.Empty;
	private List<ToolDefinition> _tools         = new List<ToolDefinition>();
	private Timer?               _idleTimer;
	private bool                 _executing;

	// User content (text and image blocks) committed since the last thing sent to the process.
	private readonly List<JsonObject> _unsent = new List<JsonObject>();

	// The session/prompt request in flight (0 = none), the tokens of it already returned with earlier
	// turns, and the tokens of the model call that has not been returned yet.
	private int _promptId;
	private int _reported;
	private int _callUsed;

	// The held MCP requests by call id, the call of the turn last returned, and the committed results.
	private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _held    = new Dictionary<string, TaskCompletionSource<JsonObject>>(StringComparer.Ordinal);
	private readonly Dictionary<string, ToolResult>                       _results = new Dictionary<string, ToolResult>(StringComparer.Ordinal);
	private          string _outstandingId = string.Empty;

	public void Rehydrate(IReadOnlyList<CanonicalMessage> messages)
	{
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is SystemMessage sm)
			{
				_system = sm.Text;
			}
		}
	}

	public void OnSystemMessage(string text)
	{
		_system = text;
	}

	public void OnUserMessage(string text)
	{
		lock (_lock)
		{
			AddUserContent(_unsent, text, null);
		}
	}

	public void OnUserMessage(string text, IReadOnlyList<MediaAttachment> attachments)
	{
		lock (_lock)
		{
			AddUserContent(_unsent, text, attachments);
		}
	}

	// Antigravity already holds the turn it produced; there is nothing to mirror.
	public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
	{
	}

	public void OnToolResult(ToolResult result)
	{
		lock (_lock)
		{
			_results[result.Id] = result;
		}
	}

	// Ends the process for good. Called when the proxy discards this protocol.
	public void Dispose()
	{
		lock (_lock)
		{
			StopLocked("protocol discarded");
		}
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
			string         startError = string.Empty;
			int            startCode  = 0;
			AcpConnection? fresh      = null;
			lock (_lock)
			{
				string signature = ToolSignature(tools);
				if (_connection != null && (_connection.HasExited || signature != _toolSignature))
				{
					StopLocked(_connection.HasExited ? "process exited" : "tool set changed");
				}

				if (_connection == null)
				{
					startError = StartLocked(model, tools, signature, bundle.Canonical.Messages, logger);
					fresh      = _connection;
				}
			}

			if (fresh != null)
			{
				(startError, startCode) = await OpenAsync(fresh, model, logger, cancellationToken);
			}

			if (startError.Length > 0)
			{
				lock (_lock)
				{
					StopLocked(startError);
				}

				result = Fail(startError, startCode, "StartFailed", model, logger, null);
			}
			else
			{
				lock (_lock)
				{
					DeliverLocked(model, logger);
				}

				result = await ReadTurnAsync(model, bundle, onProgress, logger, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
			// An interrupted turn leaves Antigravity mid-flight; the next turn starts over from canonical.
			lock (_lock)
			{
				StopLocked("turn interrupted");
			}

			throw;
		}
		finally
		{
			lock (_lock)
			{
				_executing = false;
				if (_connection != null)
				{
					_idleTimer = new Timer(_ => ReapIdle(), null, kIdleLifetime, Timeout.InfiniteTimeSpan);
				}
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

	// Held until Beast has committed this call's result and asked for the next turn. The call reaches
	// the turn being read as an event on the connection's own queue, in step with the model's output.
	public async Task<JsonObject> CallToolAsync(string name, JsonObject arguments)
	{
		TaskCompletionSource<JsonObject> reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_lock)
		{
			if (_connection == null)
			{
				reply.TrySetResult(McpBridge.TextReply($"[the {kServerName} session ended before this call was answered]", true));
			}
			else
			{
				string id = $"call_{Guid.NewGuid():N}";
				_held[id] = reply;
				_connection.Inject(new JsonObject
				{
					[kToolCallEvent] = new JsonObject { ["id"] = id, ["name"] = name, ["arguments"] = arguments.DeepClone() }
				});
			}
		}
		return await reply.Task;
	}

	// ---- Process lifecycle ----

	// Builds the process's private state folder and starts it. Returns the reason when it cannot start.
	private string StartLocked(LlmModel model, List<ToolDefinition> tools, string signature, IReadOnlyList<CanonicalMessage> canonical, SessionLogger logger)
	{
		string error = string.Empty;

		// A fresh process knows nothing, so everything it needs comes from canonical: the system
		// prompt, and the conversation as the first user message.
		_unsent.Clear();
		ResetPromptLocked();
		foreach (CanonicalMessage msg in canonical)
		{
			if (msg is SystemMessage sm)
			{
				_system = sm.Text;
			}
		}
		SeedFromCanonical(canonical, _unsent);

		_tools         = new List<ToolDefinition>(tools);
		_toolSignature = signature;
		_routeUrl      = McpBridge.Shared().Register(this);
		_stateDir      = Antigravity.CreateStateDir(model.ApiKey);

		string configDir = Path.Combine(_stateDir, "config");
		string agentsDir = Path.Combine(configDir, "agents");
		Antigravity.CreatePrivateDirectory(configDir);
		Antigravity.CreatePrivateDirectory(agentsDir);
		Antigravity.WritePrivateFile( Path.Combine(configDir, "mcp_config.json"), McpConfig(tools, _routeUrl));
		Antigravity.WritePrivateFile(Path.Combine(agentsDir, $"{kAgentName}.md"),   AgentFile(tools, _system));

		List<string> args = new List<string> { "--agent", kAgentName };
		(AcpConnection? connection, string startError) = AcpConnection.Start(Antigravity.StartInfo(_stateDir, args));
		if (connection != null)
		{
			_connection = connection;
			logger.Write(model.Config.Name, model.Endpoint, $"[antigravity] started pid {connection.Pid}: agy acp --agent {kAgentName}");
		}
		else
		{
			error = $"The Antigravity CLI (agy) could not be started: {startError}";
		}
		return error;
	}

	// Opens the session on a fresh process and puts it on the configured model. A thinking level is
	// part of the model id the CLI takes; without one the model is picked by its family name.
	private async Task<(string Error, int Code)> OpenAsync(AcpConnection connection, LlmModel model, SessionLogger logger, CancellationToken ct)
	{
		string stateDir;
		lock (_lock)
		{
			stateDir = _stateDir;
		}

		(JsonObject? session, int code, string error) = await Antigravity.OpenSessionAsync(connection, stateDir, ct);
		if (session != null)
		{
			string sessionId = session["sessionId"]?.ToString() ?? string.Empty;
			lock (_lock)
			{
				_sessionId = sessionId;
			}

			string      cliModel = Antigravity.CliModel(model.ConfigId);
			string?     effort   = ReasoningEffort.Sent(model.Config.ReasoningEffort);
			JsonObject? chosen;
			if (effort != null)
			{
				(chosen, code, error) = await connection.RequestAsync("session/set_model", new JsonObject { ["sessionId"] = sessionId, ["modelId"] = $"{cliModel}-{effort}" }, kSetupTimeout, ct);
			}
			else
			{
				(chosen, code, error) = await connection.RequestAsync("session/set_config_option", new JsonObject { ["sessionId"] = sessionId, ["configId"] = "model", ["value"] = cliModel }, kSetupTimeout, ct);
			}

			if (chosen != null)
			{
				logger.Note($"[antigravity] session {sessionId} on {cliModel}{ReasoningEffort.DisplaySuffix(model.Config.ReasoningEffort)}");
			}
			else
			{
				error = $"Antigravity does not offer {cliModel}{ReasoningEffort.DisplaySuffix(model.Config.ReasoningEffort)}: {error}";
			}
		}
		return (error, code);
	}

	// Kills the process (if any) and releases everything tied to it. Held MCP requests are answered
	// so no bridge request outlives the process it belonged to.
	private void StopLocked(string reason)
	{
		if (_connection != null)
		{
			Console.Error.WriteLine($"[Antigravity] Ending the CLI process: {reason}.");
			_connection.Kill();
			_connection = null;
		}

		if (_routeUrl.Length > 0)
		{
			McpBridge.Shared().Unregister(_routeUrl);
			_routeUrl = string.Empty;
		}
		Antigravity.DeleteStateDir(_stateDir);
		_stateDir  = string.Empty;
		_sessionId = string.Empty;

		foreach ((string _, TaskCompletionSource<JsonObject> reply) in _held)
		{
			reply.TrySetResult(McpBridge.TextReply($"[the {kServerName} session ended before this call was answered]", true));
		}

		_held.Clear();
		_results.Clear();
		_outstandingId = string.Empty;
		ResetPromptLocked();
		_idleTimer?.Dispose();
		_idleTimer = null;
	}

	private void ReapIdle()
	{
		lock (_lock)
		{
			if (!_executing && _connection != null)
			{
				StopLocked($"idle for {kIdleLifetime.TotalMinutes:0} minutes");
			}
		}
	}

	private void ResetPromptLocked()
	{
		_promptId = 0;
		_reported = 0;
		_callUsed = 0;
	}

	// ---- Sending ----

	// Hands the process what the next turn needs: the result of the last turn's tool call (through
	// the held MCP request), or a new prompt when the model is waiting on the user.
	private void DeliverLocked(LlmModel model, SessionLogger logger)
	{
		if (_outstandingId.Length > 0)
		{
			JsonObject answer = _results.TryGetValue(_outstandingId, out ToolResult? result)
				? McpBridge.ToolReply(result)
				: McpBridge.TextReply("[the call was not run]", true);

			// User text committed alongside the result has no prompt of its own to go in — Antigravity
			// is mid-turn — so it rides on the result.
			JsonArray content = (JsonArray)answer["content"]!;
			foreach (JsonObject block in _unsent)
			{
				content.Add((JsonNode)McpContent(block));
			}

			_unsent.Clear();

			if (_held.Remove(_outstandingId, out TaskCompletionSource<JsonObject>? reply))
			{
				reply.TrySetResult(answer);
			}

			_outstandingId = string.Empty;
			_results.Clear();
		}
		else
		{
			// Nothing new would leave the model with no turn to take; only a retry lands here.
			if (_unsent.Count == 0)
			{
				AddUserContent(_unsent, "Continue.", null);
			}

			JsonArray prompt = new JsonArray();
			foreach (JsonObject block in _unsent)
			{
				prompt.Add((JsonNode)block.DeepClone());
			}

			_unsent.Clear();

			JsonObject parameters = new JsonObject { ["sessionId"] = _sessionId, ["prompt"] = prompt };
			logger.Write(model.Config.Name, model.Endpoint, parameters.ToJsonString());
			ResetPromptLocked();
			_promptId = _connection!.SendRequest("session/prompt", parameters);
		}
	}

	// ---- Reading a turn ----

	// Reads events until the model calls a tool (its result is delivered on the next ExecuteAsync) or
	// the prompt finishes.
	private async Task<ProtocolResult> ReadTurnAsync(LlmModel model, ListenerBundle bundle, LiveUsageProgress onProgress, SessionLogger logger, CancellationToken cancellationToken)
	{
		AcpConnection connection;
		int           promptId;
		lock (_lock)
		{
			connection = _connection!;
			promptId   = _promptId;
		}

		TimeSpan        idleTimeout   = TimeSpan.FromSeconds(ProtocolHelpers.RequestTimeoutSeconds(model));
		StringBuilder   text          = new StringBuilder();
		StringBuilder   thinking      = new StringBuilder();
		string?         openTag       = null;
		int             streamedChars = 0;
		ProtocolResult? result        = null;
		WorkMeter?      meter         = WorkMeter.Current;
		long            meterMark     = 0;

		using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		try
		{
			while (result == null)
			{
				idle.CancelAfter(idleTimeout);
				JsonObject? evt = null;
				try
				{
					if (await connection.Events.WaitToReadAsync(idle.Token))
					{
						connection.Events.TryRead(out evt);
					}
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					lock (_lock)
					{
						StopLocked("no output within the request timeout");
					}

					result = logger.ProtocolFailure(ProtocolResult.Transient($"Antigravity produced nothing for {idleTimeout.TotalSeconds:0}s", null),
						model, DetectedProtocol.Antigravity, "Timeout", null, "idle timeout", null, null);
					break;
				}

				if (evt == null)
				{
					string tail = connection.StderrTail();
					lock (_lock)
					{
						StopLocked("process exited");
					}

					result = logger.ProtocolFailure(ProtocolResult.Transient($"Antigravity exited mid-turn: {tail}", null),
						model, DetectedProtocol.Antigravity, "ProcessExited", null, tail, null, null);
					break;
				}

				meter?.Received(ref meterMark);

				string method = evt["method"]?.ToString() ?? string.Empty;
				if (evt[kToolCallEvent] is JsonObject call)
				{
					result = CompleteToolCall(call, text, thinking, model);
				}
				else if (method == "session/update" && evt["params"]?["update"] is JsonObject update)
				{
					string kind  = update["sessionUpdate"]?.ToString() ?? string.Empty;
					string chunk = update["content"]?["text"]?.ToString() ?? string.Empty;
					if (kind == "agent_message_chunk")
					{
						text.Append(chunk);
						streamedChars += chunk.Length;
						if (openTag == StreamTag.Assistant || !string.IsNullOrWhiteSpace(chunk))
						{
							openTag = StreamChunk(bundle, openTag, StreamTag.Assistant, chunk);
						}
					}
					else if (kind == "agent_thought_chunk")
					{
						thinking.Append(chunk);
						streamedChars += chunk.Length;
						openTag        = StreamChunk(bundle, openTag, StreamTag.Thinking, chunk);
					}
					else if (kind == "usage_update" && update["used"] is JsonValue used && used.TryGetValue(out int tokens) && tokens > 0)
					{
						lock (_lock)
						{
							_callUsed = tokens;
						}
					}

					int prompt;
					lock (_lock)
					{
						prompt = _callUsed;
					}

					onProgress(prompt, streamedChars / 4, Cost(model, prompt, 0, streamedChars / 4), 0);
				}
				else if (method == "session/request_permission")
				{
					AnswerPermission(connection, evt);
				}
				else if (method.Length > 0 && evt["id"] != null)
				{
					// Beast offers the CLI no file or terminal services of its own.
					connection.Send(new JsonObject
					{
						["jsonrpc"] = "2.0",
						["id"]      = evt["id"]!.DeepClone(),
						["error"]   = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" }
					});
				}
				else if (AcpConnection.IsResponseTo(evt, promptId))
				{
					result = CompletePrompt(evt, text, thinking, model, logger);
				}
			}
		}
		finally
		{
			if (openTag != null)
			{
				bundle.Transport?.OnStreamEnd(openTag);
			}
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
			{
				bundle.Transport?.OnStreamEnd(openTag);
			}

			bundle.Transport?.OnStreamStart(tag);
		}
		bundle.Transport?.OnStreamChunk(tag, chunk);
		return tag;
	}

	// The model called one of Beast's tools: the turn so far, with that call, goes back to Beast. The
	// tokens of the model call that made it are known already; a second call from the same model call
	// has none of its own.
	private ProtocolResult CompleteToolCall(JsonObject call, StringBuilder text, StringBuilder thinking, LlmModel model)
	{
		SemanticToolCall toolCall = new SemanticToolCall
		{
			Id            = call["id"]?.ToString() ?? string.Empty,
			Name          = call["name"]?.ToString() ?? string.Empty,
			ArgumentsJson = call["arguments"]?.ToJsonString() ?? "{}"
		};

		int prompt;
		lock (_lock)
		{
			_outstandingId = toolCall.Id;
			prompt         = _callUsed;
			_reported     += _callUsed;
			_callUsed      = 0;
		}

		TokenUsageInfo usage = new TokenUsageInfo { PromptTokens = prompt, CompletionTokens = 0, CachedTokens = 0 };
		return ProtocolResult.Succeeded(new ProtocolCallPayload(text.ToString(), thinking.ToString(), new List<SemanticToolCall> { toolCall }, new List<ToolResult>(), "tool_calls", usage, Cost(model, prompt, 0, 0)));
	}

	// The prompt's own response: the turn is over. Its usage totals every model call of the prompt, so
	// this turn takes what earlier turns have not already been charged. A response with no usage made
	// no model call at all — the CLI reports its own failures as message text — and is a failed turn.
	private ProtocolResult CompletePrompt(JsonObject response, StringBuilder text, StringBuilder thinking, LlmModel model, SessionLogger logger)
	{
		ProtocolResult result;
		int            reported;
		lock (_lock)
		{
			reported = _reported;
			ResetPromptLocked();
		}

		JsonObject? usage = response["result"]?["usage"] as JsonObject;
		if (response["error"] is JsonObject failure)
		{
			int    code    = failure["code"] is JsonValue value && value.TryGetValue(out int number) ? number : 0;
			string message = failure["message"]?.ToString() ?? "the prompt failed";
			lock (_lock)
			{
				StopLocked($"prompt failed: {message}");
			}

			result = Fail(message, code, "TurnFailed", model, logger, response.ToJsonString());
		}
		else if (usage == null)
		{
			string message = text.ToString().Trim();
			if (message.Length == 0)
			{
				message = "Antigravity ended the turn without calling the model.";
			}

			lock (_lock)
			{
				StopLocked($"turn ended without a model call: {message}");
			}

			result = Fail(message, 0, "TurnFailed", model, logger, response.ToJsonString());
		}
		else
		{
			int    total   = ReadInt(usage, "totalTokens");
			int    output  = ReadInt(usage, "outputTokens");
			int    prompt  = Math.Max(0, total - reported - output);
			int    cached  = Math.Min(ReadInt(usage, "cachedReadTokens"), prompt);
			string stopped = response["result"]?["stopReason"]?.ToString() ?? "end_turn";

			TokenUsageInfo info = new TokenUsageInfo { PromptTokens = prompt, CompletionTokens = output, CachedTokens = cached };
			result              = ProtocolResult.Succeeded(new ProtocolCallPayload(text.ToString(), thinking.ToString(), new List<SemanticToolCall>(), new List<ToolResult>(),
				ProtocolHelpers.TurnFinishReason(stopped, false), info, Cost(model, prompt - cached, cached, output)));
		}
		return result;
	}

	// Only Beast's own tools may run. What is left of Antigravity's built-ins is refused, which the
	// model sees as a denied call.
	private static void AnswerPermission(AcpConnection connection, JsonObject request)
	{
		string name   = request["params"]?["toolCall"]?["name"]?.ToString() ?? string.Empty;
		string wanted = name.StartsWith(kToolPrefix, StringComparison.Ordinal) ? "allow" : "reject";
		string option = wanted == "allow" ? "allow" : "deny";
		if (request["params"]?["options"] is JsonArray options)
		{
			foreach (JsonNode? offered in options)
			{
				if ((offered?["kind"]?.ToString() ?? string.Empty).StartsWith(wanted, StringComparison.Ordinal))
				{
					option = offered?["optionId"]?.ToString() ?? option;
					break;
				}
			}
		}

		connection.Send(new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"]      = request["id"]?.DeepClone(),
			["result"]  = new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = option } }
		});
	}

	// Sorts a failure the CLI reported in words. A lapsed sign-in needs a person; a usage limit is
	// fallen back from; anything else is worth another try on a fresh process.
	private static ProtocolResult Fail(string message, int code, string kind, LlmModel model, SessionLogger logger, string? body)
	{
		ProtocolResult failure;
		string         lower = message.ToLowerInvariant();
		if (lower.Contains("authentication required") || lower.Contains("sign in") || lower.Contains("unauthenticated"))
		{
			failure = ProtocolResult.FailedHttp($"{message} Open /config and choose Google Subscription to sign in again.", 401);
			kind    = "ClientError";
		}
		else if (lower.Contains("quota") || lower.Contains("rate limit") || lower.Contains("resource_exhausted") || lower.Contains("resource exhausted") || lower.Contains("429"))
		{
			failure = ProtocolResult.RateLimited(null, $"Google subscription usage limit reached: {message}");
			kind    = "RateLimited";
		}
		else if (ProtocolHelpers.IsContextOverflow(message))
		{
			failure = ProtocolResult.ContextFull(message);
			kind    = "ContextOverflow";
		}
		else if (kind == "StartFailed")
		{
			failure = ProtocolResult.Failed(message);
		}
		else
		{
			failure = ProtocolResult.Transient(message, null);
		}
		return logger.ProtocolFailure(failure, model, DetectedProtocol.Antigravity, kind, code != 0 ? code : null, message, body, null);
	}

	// ---- Files the CLI reads ----

	// Beast's tool server for this conversation: no time limit on a call (a subagent can run for
	// hours), and every tool declared to the model directly rather than behind the CLI's lookup tool.
	private static string McpConfig(List<ToolDefinition> tools, string routeUrl)
	{
		JsonObject eager = new JsonObject();
		foreach (ToolDefinition tool in tools)
		{
			eager[tool.Function.Name] = new JsonObject { ["eager"] = true };
		}

		JsonObject server = new JsonObject { ["serverUrl"] = routeUrl, ["timeoutSeconds"] = -1, ["tools"] = eager };
		return new JsonObject { ["mcpServers"] = new JsonObject { [kServerName] = server } }.ToJsonString();
	}

	// The custom agent this conversation runs as: no built-in tools, Beast's server admitted, and
	// Beast's system prompt as its instructions.
	private static string AgentFile(List<ToolDefinition> tools, string system)
	{
		StringBuilder file = new StringBuilder();
		file.Append("---\n");
		file.Append($"name: {kAgentName}\n");
		file.Append("description: Beast conversation\n");
		file.Append("tools: []\n");
		file.Append("commandExecutionPolicy: off\n");
		file.Append("mcpServers:\n");
		file.Append($"  - name: {kServerName}\n");
		if (tools.Count > 0)
		{
			file.Append("    tools:\n");
			foreach (ToolDefinition tool in tools)
			{
				file.Append($"      {tool.Function.Name}:\n        eager: true\n");
			}
		}
		file.Append("---\n");
		file.Append(system);
		file.Append('\n');
		return file.ToString();
	}

	// ---- Content shapes ----

	// The first prompt of a fresh process. A conversation that is still only user turns goes over as
	// itself; anything further along goes as a transcript, since Antigravity cannot be handed
	// assistant turns or tool results it did not produce.
	private static void SeedFromCanonical(IReadOnlyList<CanonicalMessage> canonical, List<JsonObject> into)
	{
		bool onlyUser = true;
		foreach (CanonicalMessage msg in canonical)
		{
			if (msg is AssistantMessage || msg is ToolResultMessage)
			{
				onlyUser = false;
			}
		}

		if (onlyUser)
		{
			foreach (CanonicalMessage msg in canonical)
			{
				if (msg is UserMessage um)
				{
					AddUserContent(into, um.Text, um.Attachments);
				}
			}
		}
		else
		{
			string transcript = string.Concat(Summarizer.RenderTranscript(canonical));
			AddUserContent(into, $"[This conversation continues in a new session. Everything so far follows as a transcript; carry on from where it ends.]\n\n{transcript}", null);
		}
	}

	// ACP content blocks: text, and images as base64 data with their mime type.
	private static void AddUserContent(List<JsonObject> into, string text, IReadOnlyList<MediaAttachment>? attachments)
	{
		if (!string.IsNullOrWhiteSpace(text))
		{
			into.Add(new JsonObject { ["type"] = "text", ["text"] = text });
		}

		if (attachments != null)
		{
			foreach (MediaAttachment att in attachments)
			{
				if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
				{
					into.Add(new JsonObject { ["type"] = "image", ["mimeType"] = att.MimeType, ["data"] = att.Base64Data });
				}
				else
				{
					into.Add(new JsonObject { ["type"] = "text", ["text"] = $"[A {att.MimeType} attachment was supplied, but this provider does not accept that input type.]" });
				}
			}
		}
	}

	// A user content block as it rides on a tool result (an ACP image block is already MCP's shape).
	private static JsonObject McpContent(JsonObject block)
	{
		JsonObject content;
		if (block["type"]?.ToString() == "image")
		{
			content = (JsonObject)block.DeepClone();
		}
		else
		{
			content = new JsonObject { ["type"] = "text", ["text"] = $"[user message]\n{block["text"]}" };
		}

		return content;
	}

	// ---- Helpers ----

	private static string ToolSignature(List<ToolDefinition> tools)
	{
		StringBuilder sb = new StringBuilder();
		foreach (ToolDefinition tool in tools)
		{
			sb.Append(tool.Function.Name).Append('\n');
		}

		return sb.ToString();
	}

	private static decimal Cost(LlmModel model, int freshInput, int cacheRead, int output)
	{
		return (freshInput / 1_000_000m) * model.Config.Cost.Input
			 + (cacheRead / 1_000_000m) * model.Config.Cost.CacheRead
			 + (output / 1_000_000m) * model.Config.Cost.Output;
	}

	private static int ReadInt(JsonObject node, string key)
	{
		return node[key] is JsonValue value && value.TryGetValue(out int number) ? number : 0;
	}
}