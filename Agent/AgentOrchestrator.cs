using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Manages the agent loop: read input, run LLM turns, compact, save sessions.
//
// Architecture:
//   - Conversation is a LOCAL VARIABLE in RunAsync. No field holds it.
//   - Startup is silent: no upfront validation. Errors fire at interaction time.
//   - A cooperative async reader task feeds a ConcurrentQueue; the main loop drains it each iteration.
//   - The LLM runs whenever the conversation has pending work; it is not gated on input arrival.
//   - /compact calls CompactAsync directly; no flag needed since role/service are resolved first.
//   - Auto-compaction on ContextFull is handled inside RunLlmTurnAsync, not the loop.
//   - Sessions are loaded from disk on start (last session resumed by default)
//     and saved after every turn and on exit.
//   - RunLlmTurnAsync applies the role (system prompt, model) before running.
//   - Model changes are transparent — the service swaps and the turn continues.
public class AgentOrchestrator
{
	private const int CompactionReserveTokens = 4096;

	private readonly LlmRegistry _registry;
	private readonly RoleService _roleService;
	private readonly SettingsService _settings;
	private readonly ITransportServer _transport;

	public AgentOrchestrator(
		LlmRegistry registry,
		RoleService roleService,
		SettingsService settings,
		ITransportServer transport, CancellationToken cancellationToken)
	{
		_registry = registry;
		_roleService = roleService;
		_settings = settings;
		_transport = transport;
		_registry.LoadFromConfigs(_settings, _roleService);
		_cancellationToken = cancellationToken;
	}

	// Cancels only the current LLM turn (not the whole process). Not linked to the process token
	// so there is zero ambiguity about which cancel fired: if _turnCts fires, it was a user /cancel;
	// if cancellationToken fires, the whole process is shutting down.
	private CancellationTokenSource? _turnCts;
	private CancellationToken _cancellationToken;
	private bool _wantsExit = false;
	private bool _wantsCompact = false;
	private List<(string id, string displayName, int messageCount)> _cachedSessions = new List<(string, string, int)>();

	public async Task RunAsync()
	{
		BeastSession conversation = LoadOrCreateConversation(out ListenerBundle bundle);
		ConcurrentQueue<string> inputQueue = new();

		// Cooperative async reader: awaits input and feeds the queue; no thread pool thread involved.
		async Task ReadInputAsync()
		{
			while (!_cancellationToken.IsCancellationRequested && !_wantsExit)
			{
				string? line = await _transport.TryReadAsync(100, _cancellationToken);
				if (line == null) break;
				if (line.Length > 0)
				{
					_transport.Debug($"[orchestrator] Received: '{line}'");
					// /cancel is intercepted here so it signals the current turn's token immediately,
					// without waiting for the queue to drain between turns.
					if (line.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
						_turnCts?.Cancel();
					else
						inputQueue.Enqueue(line);
				}
			}
		}

		Task readerTask = ReadInputAsync();

		// Signal to Beast that stdin is open and we are ready to receive input.
		_transport.Status("ready");
		// Clear any busy state Beast may have set while replaying history.
		_transport.Idle();

		_cachedSessions = SessionService.List();
		string lastCompletionCandidates = JsonSerializer.Serialize(BuildCompletionCandidates(conversation));
		_transport.Completions(lastCompletionCandidates);

		while (!_cancellationToken.IsCancellationRequested && !_wantsExit)
		{
			// 1. Resolve role and service.
			LLMRole? role = _roleService.GetRole(conversation.Role);
			LlmService? service = _registry.GetServiceForRole(role, conversation.Model, conversation.GetUsedTokenCount());

			// 2. Drain all pending input: run commands in order, accumulate text into one user message.
			// Returns null accumulatedText if nothing was dequeued; empty string if only commands ran.
			string? accumulatedText = null;
			if (inputQueue.TryPeek(out _))
			{
				(accumulatedText, conversation, bundle) = await DrainInputAsync(inputQueue, conversation, bundle, service, role);
			}

			// Re-resolve after commands so queued user text uses the current role/model immediately.
			role = _roleService.GetRole(conversation.Role);
			service = _registry.GetServiceForRole(role, conversation.Model, conversation.GetUsedTokenCount());
			if (service!=null)
			{
				string currentCandidates = JsonSerializer.Serialize(BuildCompletionCandidates(conversation));
				if (currentCandidates != lastCompletionCandidates)
				{
					lastCompletionCandidates = currentCandidates;
					_transport.Completions(currentCandidates);
				}

				if (service.Model.ConfigId != conversation.Model)
				{
					conversation.Model = service.Model.ConfigId;
					SendStats(conversation, service.Model.Config.ContextWindow);
				}

				if (string.IsNullOrEmpty(role!.SystemPrompt)==false && role.SystemPrompt != conversation.GetSystemPrompt())
				{
					bundle.OnSystemMessage(null!, role.SystemPrompt);
				}

				if (_wantsCompact)
				{
					_wantsCompact = false;
					_transport.Status("Running compaction...");
					(LlmResult compactResult, BeastSession compactedSession, ListenerBundle compactedBundle) = await CompactAsync(conversation, bundle, service!, role!);
					conversation = compactedSession;
					bundle = compactedBundle;

					// Re-resolve after compaction because the service may have changed
					service = _registry.GetServiceForRole(role, conversation.Model, conversation.GetUsedTokenCount());
				}
			}

			if (string.IsNullOrEmpty(accumulatedText)==false)
			{
				bundle.OnUserMessage(null!, accumulatedText);
			}

			// 3. Run the LLM whenever the conversation has work; yield briefly if there is nothing to do.
			if (!_cancellationToken.IsCancellationRequested && conversation.NeedsLlmAttention() && service!=null)
			{
				_turnCts = new CancellationTokenSource();
				try
				{
					LlmResult result = await RunLlmTurnAsync(conversation, bundle, role!, service!, _turnCts.Token);
					if (result.ExitReason == LlmExitReason.Failed)
						_transport.Error(result.ErrorMessage);
				}
				catch (OperationCanceledException) when (_turnCts.IsCancellationRequested && !_cancellationToken.IsCancellationRequested)
				{
					// User pressed Escape (sent /cancel) — abandon this turn and return to idle.
				}
				finally
				{
					_turnCts.Dispose();
					_turnCts = null;
				}

				if (inputQueue.IsEmpty)  // if a new message is inbound, do not tell the client it is idle.  Still race condition, but let's make an effort.
					_transport.Idle();
			}

			if (role != null)
			{
				long waitMs = _registry.GetMillisecondsUntilAvailable(role);
				int delayMs = waitMs <= 0 || waitMs == long.MaxValue ? 10 : (int)Math.Min(waitMs, 30000);
				await Task.Delay(delayMs, _cancellationToken);
			}
		}

		SessionService.Save(conversation);
		_settings.Settings.LastSessionId = conversation.Id;
		_settings.SaveSettings();
	}

	// ---- Session management ----

	private BeastSession LoadOrCreateConversation(out ListenerBundle bundle)
	{
		string? lastSessionId = _settings.Settings?.LastSessionId;
		BeastSession? lastData = SessionService.LoadBySessionId(lastSessionId);
		if (lastData != null)
		{
			bundle = BuildBundle(lastData);
			_transport.Status("Resumed session: " + lastData.DisplayName);
			return lastData;
		}

		return CreateFreshConversation(string.Empty, out bundle);
	}

	private BeastSession CreateFreshConversation(string roleName, out ListenerBundle bundle)
	{
		if (string.IsNullOrEmpty(roleName))
		{
			foreach (LLMRole r in _roleService.Roles.Values)
			{
				roleName = r.Name;
				break;
			}
		}

		BeastSession fresh = BeastSession.CreateNew(Guid.NewGuid().ToString(), roleName, string.Empty);
		bundle = BuildBundle(fresh);
		return fresh;
	}

	// Builds a fresh ListenerBundle wired to the session's native state arrays and the transport.
	private ListenerBundle BuildBundle(BeastSession session)
	{
		ListenerBundle b = new ListenerBundle();
		b.Add(new ListenerChatCompletions(session.ChatCompletionsState));
		b.Add(new ListenerResponses(session.ResponsesState));
		b.Add(new ListenerAnthropic(session.AnthropicState));
		b.Add(new ListenerTransport(_transport));
		return b;
	}

	// ---- LLM execution ----

	// Runs one LLM turn. Applies the role (system prompt) before calling the LLM.
	// Sets _wantsCompact if the context is full so the main loop handles it uniformly.
	private async Task<LlmResult> RunLlmTurnAsync(BeastSession conversation, ListenerBundle bundle, LLMRole role, LlmService service, CancellationToken cancellationToken)
	{
		conversation.Model = service.Model.ConfigId;

		if (string.IsNullOrEmpty(conversation.DisplayName))
		{
			string? first = conversation.GetFirstUserText();
			if (!string.IsNullOrWhiteSpace(first))
			{
				string name = first.Trim();
				conversation.DisplayName = name.Length > 50 ? name.Substring(0, 50) : name;
			}
		}

		Tool[] tools = _registry.GetToolsForRole(role);
		LlmResult result = await service.RunToCompletionAsync(conversation, bundle, tools, CompactionReserveTokens, _transport, cancellationToken);
		SendStats(conversation, service.Model.Config.ContextWindow);
		if (result.ExitReason == LlmExitReason.ContextFull)
		{
			_wantsCompact = true;
		}

		return result;
	}

	// ---- Compaction ----

	private async Task<(LlmResult result, BeastSession newConversation, ListenerBundle newBundle)> CompactAsync(BeastSession conversation, ListenerBundle bundle, LlmService service, LLMRole role)
	{
		// Snapshot all protocol state so we can restore on failure.
		StateSnapshot snapshot = conversation.Snapshot();

		// Capture the last 2 user-exchange groups before we modify anything.
		List<JsonNode> tailExchanges = ExtractTailExchanges(conversation.ChatCompletionsState, 2);

		// Append the compaction prompt as a coalesced user message (preserves prompt cache).
		bundle.OnUserMessage(null!, _settings.Settings.CompactionPrompt);

		// Single-shot: stop as soon as the model responds once.
		_transport.Status("[Compaction] Started.");
		LlmResult result = await service.RunToCompletionAsync(conversation, bundle, Array.Empty<Tool>(), 0, _transport, _cancellationToken);

		string? summary = bundle.GetLastAssistantText();

		if (result.ExitReason == LlmExitReason.Completed && !string.IsNullOrWhiteSpace(summary))
		{
			// Save the old session before we switch away from it.
			SessionService.Save(conversation);

			// Build the new session: new id, incremented display name, same role/model.
			string newDisplayName = BeastSession.IncrementDisplayName(conversation.DisplayName);
			BeastSession fresh = BeastSession.CreateNew(Guid.NewGuid().ToString(), conversation.Role, newDisplayName);
			fresh.Model = conversation.Model;
			ListenerBundle freshBundle = BuildBundle(fresh);

			// System prompt first.
			if (!string.IsNullOrEmpty(role.SystemPrompt))
				freshBundle.OnSystemMessage(null!, role.SystemPrompt);

			// Summary becomes the first user message in the new session.
			freshBundle.OnUserMessage(null!, summary);

			// Replay the captured tail exchanges directly into the new session's ChatCompletions state
			// so all protocol listeners reflect the same exchanges via the semantic fan-out path.
			foreach (JsonNode tailNode in tailExchanges)
			{
				string nodeRole = tailNode["role"]?.GetValue<string>() ?? string.Empty;
				string content = tailNode["content"]?.GetValue<string>() ?? string.Empty;

				if (nodeRole == "user")
				{
					freshBundle.OnUserMessage(null!, content);
				}
				else if (nodeRole == "assistant")
				{
					List<SemanticToolCall> toolCalls = new List<SemanticToolCall>();
					JsonArray? tcs = tailNode["tool_calls"]?.AsArray();
					if (tcs != null)
					{
						foreach (JsonNode? tc in tcs)
						{
							if (tc == null) continue;
							toolCalls.Add(new SemanticToolCall
							{
								Id = tc["id"]?.GetValue<string>() ?? string.Empty,
								Name = tc["function"]?["name"]?.GetValue<string>() ?? string.Empty,
								ArgumentsJson = tc["function"]?["arguments"]?.GetValue<string>() ?? string.Empty
							});
						}
					}
					string thinking = tailNode["reasoning_content"]?.GetValue<string>() ?? string.Empty;
					freshBundle.OnAssistantTurn(null!, content, thinking, toolCalls);
				}
				else if (nodeRole == "tool")
				{
					string toolCallId = tailNode["tool_call_id"]?.GetValue<string>() ?? string.Empty;
					freshBundle.OnToolResult(null!, toolCallId, content);
				}
			}

			_settings.Settings.LastSessionId = fresh.Id;
			_settings.SaveSettings();
			_cachedSessions = SessionService.List();

			_transport.Status("[Compaction] Complete.");
			return (result, fresh, freshBundle);
		}

		// One way or another, either we are still at context full, we were interrupted, or the call failed.
		conversation.RestoreSnapshot(snapshot);
		_transport.Status("[Compaction] Failed.");
		return (result, conversation, bundle);
	}

	// ---- Input processing ----

	// Drains all queued input lines. Commands are dispatched immediately; plain text is accumulated.
	// Returns null for accumulatedText if the queue was empty; empty string if only commands ran.
	private async Task<(string? accumulatedText, BeastSession conversation, ListenerBundle bundle)> DrainInputAsync(
		ConcurrentQueue<string> inputQueue,
		BeastSession conversation,
		ListenerBundle bundle,
		LlmService? service,
		LLMRole? role)
	{
		if (!inputQueue.TryPeek(out _))
			return (null, conversation, bundle);

		string accumulatedText = string.Empty;

		while (inputQueue.TryDequeue(out string? line))
		{
			string? text;
			(text, conversation, bundle) = await HandleInputLineAsync(line, conversation, bundle, service, role);
			if (text != null)
				accumulatedText = string.IsNullOrEmpty(accumulatedText) ? text : accumulatedText + "\n" + text;
		}

		return (accumulatedText, conversation, bundle);
	}

	// Dispatches a single input line. Returns the line as plain text if it is not a command, null otherwise.
	private async Task<(string? text, BeastSession conversation, ListenerBundle bundle)> HandleInputLineAsync(
		string line,
		BeastSession conversation,
		ListenerBundle bundle,
		LlmService? service,
		LLMRole? role)
	{
		if (!line.StartsWith("/"))
			return (line, conversation, bundle);

		string trimmed = line.TrimStart('/').Trim();
		string verb;
		string? args = null;

		int spaceIdx = trimmed.IndexOf(' ');
		if (spaceIdx >= 0)
		{
			verb = trimmed.Substring(0, spaceIdx).ToLowerInvariant();
			args = trimmed.Substring(spaceIdx + 1).Trim();
		}
		else
		{
			verb = trimmed.ToLowerInvariant();
		}

		switch (verb)
		{
			case "ping":
				_transport.Status($"pong {args}");
				break;
			case "history":
				ReplayHistory(conversation);
				SendStats(conversation, service?.Model.Config.ContextWindow ?? 0);
				break;
			case "quit":
				_wantsExit = true;
				break;
			case "compact":
				_wantsCompact = true;
				break;
			case "session":
				if (args == "new")
				{
					SessionService.Save(conversation);
					conversation = CreateFreshConversation(conversation.Role, out bundle);
					_cachedSessions = SessionService.List();
					_transport.Status("New session started.");
				}
				else if (args != null)
				{
					BeastSession? loaded = SessionService.Load(args);
					if (loaded != null)
					{
						SessionService.Save(conversation);
						conversation = loaded;
						bundle = BuildBundle(conversation);
						_cachedSessions = SessionService.List();
						_transport.Status("Switched to session: " + loaded.DisplayName);
					}
					else
					{
						_transport.Error("Session not found: " + args);
					}
				}
				break;
			case "clear":
				bundle.OnClear();
				_registry.ResetAllAvailability();
				_transport.Status("Session cleared.");
				break;
			case "reload":
				_roleService.Reload();
				ReloadRegistry();
				_registry.ResetAllAvailability();
				_transport.Status("Config files reloaded.");
				break;
			case "role":
				if (args != null)
				{
					conversation.Role = args;
					_transport.Status($"Role set to {args}");
				}
				break;
			case "model":
				if (args != null)
				{
					conversation.Model = args;
					_registry.ResetAvailability(args);
					_transport.Status($"Model set to {args}");
					// Push a Stats frame immediately so Beast's status bar reflects the new model
					// without waiting for the next LLM turn.
					LLMRole? modelRole = _roleService.GetRole(conversation.Role);
					LlmService? modelService = modelRole != null ? _registry.GetServiceForRole(modelRole, conversation.Model, conversation.GetUsedTokenCount()) : null;
					SendStats(conversation, modelService?.Model.Config.ContextWindow ?? 0);
				}
				break;
			case "help":
				_transport.Output("Commands: /compact, /clear, /reload, /role <id>, /model <id>, /session <id>, /test, /quit");
				break;
			case "test":
				await RunTestsAsync(args);
				break;
			default:
				_transport.Error($"Unknown command reached agent: /{verb}");
				break;
		}

		return (null, conversation, bundle);
	}

	// Returns all completable tokens: slash commands and session ids.
	private List<string> BuildCompletionCandidates(BeastSession conversation)
	{
		List<string> candidates = new List<string>
		{
			"/compact",
				"/clear",
				"/reload",
				"/role",
				"/model",
				"/session",
				"/help",
				"/test"
		};

		LLMRole? activeRole = string.IsNullOrEmpty(conversation.Role) ? null : _roleService.GetRole(conversation.Role);
		LlmService? activeService = null;
		if (activeRole != null)
		{
			activeService = _registry.GetServiceForRole(activeRole, conversation.Model);
		}

		string currentRoleName = activeRole != null ? activeRole.Name : conversation.Role;
		AddCurrentFirst(candidates, "/role ", currentRoleName, _roleService.Roles.Keys);

		if (activeRole != null)
		{
			string currentModelId = activeService != null ? activeService.Model.ConfigId : conversation.Model;
			AddCurrentFirst(candidates, "/model ", currentModelId, activeRole.Models);
		}

		candidates.Add("/session new");
		foreach ((string id, string displayName, int messageCount) s in _cachedSessions)
		{
			candidates.Add("/session " + s.id);
		}

		return candidates;
	}

	private static void AddCurrentFirst(List<string> candidates, string prefix, string currentValue, ICollection<string> values)
	{
		if (!string.IsNullOrEmpty(currentValue) && values.Contains(currentValue))
		{
			candidates.Add(prefix + currentValue);
		}

		foreach (string value in values)
		{
			if (value == currentValue) continue;
			candidates.Add(prefix + value);
		}
	}

	// ---- Tests ----

	private async Task RunTestsAsync(string? filter)
	{
		_transport.Status("Running tests...");

		TestContext ctx = new TestContext(_transport);

		LlmServiceTests.Test(ctx);
		await FileToolsTests.TestAsync(ctx);
		ShellToolsTests.Test(ctx);
		await WebToolsTests.TestAsync(ctx, _settings.Settings.WebSearch);
		await SearchToolsTests.TestAsync(ctx);
		await PerModelLlmTests.TestAsync(ctx, _registry, _roleService, _settings, _cancellationToken);

		_transport.Output($"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
	}

	// ---- Helpers ----

	private void ReloadRegistry()
	{
		_settings.LoadSettings();
		_registry.LoadFromConfigs(_settings, _roleService);
	}

	// Returns the last `count` user-exchange groups from the ChatCompletions state.
	// Each group is: the user message node followed by all assistant/tool nodes that follow it
	// up to (but not including) the next user message. The result is ordered oldest-first.
	private static List<JsonNode> ExtractTailExchanges(JsonArray state, int count)
	{
		// Locate the start indices of each user message.
		List<int> userStarts = new List<int>();
		for (int i = 0; i < state.Count; i++)
		{
			JsonNode? n = state[i];
			if (n != null && n["role"]?.GetValue<string>() == "user")
				userStarts.Add(i);
		}

		if (userStarts.Count == 0)
			return new List<JsonNode>();

		int startGroup = userStarts.Count > count ? userStarts.Count - count : 0;
		int startIndex = userStarts[startGroup];

		List<JsonNode> result = new List<JsonNode>();
		for (int i = startIndex; i < state.Count; i++)
		{
			JsonNode? n = state[i];
			if (n != null)
				result.Add((JsonNode)n.DeepClone());
		}
		return result;
	}

	// Sends a Stats frame to Beast with model, token counts, total cost, and context window.
	private void SendStats(BeastSession conversation, int maxContext)
	{
		TokenUsageInfo? usage = conversation.LastTokenUsage;
		int prompt = usage != null ? usage.PromptTokens : 0;
		int completion = usage != null ? usage.CompletionTokens : 0;
		int contextTokens = conversation.GetUsedTokenCount();
		string json = JsonSerializer.Serialize(new
		{
			model = conversation.Model,
			promptTokens = prompt,
			completionTokens = completion,
			totalCost = conversation.TotalCost,
			maxContext,
			contextTokens
		});
		_transport.Stats(json);
	}

	// Replays the committed conversation history to the transport so a freshly connected Beast
	// client can reconstruct its display. Skips streaming scaffolds; sends everything else.
	private void ReplayHistory(BeastSession conversation)
	{
		foreach (JsonNode? node in conversation.ChatCompletionsState)
		{
			if (node == null) continue;
			string role = node["role"]?.GetValue<string>() ?? string.Empty;
			string content = node["content"]?.GetValue<string>() ?? string.Empty;

			if (role == "system")
			{
				if (!string.IsNullOrEmpty(content))
					_transport.System(content);
			}
			else if (role == "user")
			{
				if (!string.IsNullOrEmpty(content))
					_transport.User(content);
			}
			else if (role == "assistant")
			{
				string thinking = node["reasoning_content"]?.GetValue<string>() ?? string.Empty;
				if (!string.IsNullOrEmpty(thinking))
					_transport.Thinking(thinking);

				if (!string.IsNullOrEmpty(content))
					_transport.Output(content);

				JsonArray? toolCalls = node["tool_calls"]?.AsArray();
				if (toolCalls != null)
				{
					foreach (JsonNode? tc in toolCalls)
					{
						if (tc == null) continue;
						string name = tc["function"]?["name"]?.GetValue<string>() ?? string.Empty;
						string args = tc["function"]?["arguments"]?.GetValue<string>() ?? string.Empty;
						string tcId = tc["id"]?.GetValue<string>() ?? string.Empty;
						_transport.ToolCallWithId(tcId, $"{name}({args})");
					}
				}
			}
			else if (role == "tool")
			{
				if (!string.IsNullOrEmpty(content))
				{
					string toolCallId = node["tool_call_id"]?.GetValue<string>() ?? string.Empty;
					_transport.ToolResponseWithId(toolCallId, content);
				}
			}
		}
	}
}