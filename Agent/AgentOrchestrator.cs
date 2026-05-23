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
		ITransportServer transport)
	{
		_registry = registry;
		_roleService = roleService;
		_settings = settings;
		_transport = transport;
		_registry.LoadFromConfigs(_settings, _roleService);
	}

	public async Task<int> RunAsync(CancellationToken cancellationToken)
	{
		BeastSession conversation = LoadOrCreateConversation(out ListenerBundle bundle);
		bool wantsExit = false;
		int exitCode = 0;

		ConcurrentQueue<string> inputQueue = new();

		// Cooperative async reader: awaits input and feeds the queue; no thread pool thread involved.
		async Task ReadInputAsync()
		{
			while (!cancellationToken.IsCancellationRequested && !wantsExit)
			{
				string? line = await _transport.TryReadAsync(100, cancellationToken);
				if (line == null) break;
				if (line.Length > 0)
				{
					_transport.Debug($"[orchestrator] Received: '{line}'");
					inputQueue.Enqueue(line);
				}
			}
		}

		Task readerTask = ReadInputAsync();

		// Signal to Beast that stdin is open and we are ready to receive input.
		_transport.Status("ready");
		SendCompletionCandidates(conversation);

		while (!cancellationToken.IsCancellationRequested && !wantsExit)
		{
			// 1. Resolve role and service.
			LLMRole? role = _roleService.GetRole(conversation.Role);
			LlmService? service = null;
			if (role != null)
			{
				service = _registry.GetServiceForRole(role, conversation.Model);
			}

			bool canRunLLM = role != null && service != null;

			// 2. Drain all pending input: run commands in order, accumulate text into one user message.
			string accumulatedText = string.Empty;
			while (inputQueue.TryDequeue(out string? line))
			{
				if (line.StartsWith("/"))
				{
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
							wantsExit = true;
							break;
						case "compact":
							if (canRunLLM)
							{
								_transport.Status("Running compaction...");
								LlmResult compactResult = await CompactAsync(conversation, bundle, service!, role!, cancellationToken);
								canRunLLM = compactResult.Success;
							}
							break;
						case "session":
							if (args == "new")
									{
										SessionService.Save(conversation);
										conversation = CreateFreshConversation(conversation.Role, out bundle);
								  SendCompletionCandidates(conversation);
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
									SendCompletionCandidates(conversation);
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
							conversation.NeedsLlmAttention = false;
							_transport.Status("Session cleared.");
							break;
						case "reload":
							_roleService.Reload();
							ReloadRegistry();
							canRunLLM = false;
                            SendCompletionCandidates(conversation);
							_transport.Status("Config files reloaded.");
							break;
						case "role":
							if (args != null)
							{
								conversation.Role = args;
								canRunLLM = false;
                               SendCompletionCandidates(conversation);
								_transport.Status($"Role set to {args}");
							}
							break;
						case "model":
							if (args != null)
							{
								conversation.Model = args;
								canRunLLM = false;
							  SendCompletionCandidates(conversation);
								_transport.Status($"Model set to {args}");
								// Push a Stats frame immediately so Beast's status bar reflects the new model
								// without waiting for the next LLM turn.
								LLMRole? modelRole = _roleService.GetRole(conversation.Role);
								LlmService? modelService = modelRole != null ? _registry.GetServiceForRole(modelRole, conversation.Model) : null;
								SendStats(conversation, modelService?.Model.Config.ContextWindow ?? 0);
							}
							break;
						case "help":
							_transport.Output("Commands: /compact, /clear, /reload, /role <id>, /model <id>, /session <id>, /test, /quit");
							break;
						case "test":
							await RunTestsAsync(args, cancellationToken);
							break;
						default:
							_transport.Error($"Unknown command reached agent: /{verb}");
							break;
					}
				}
				else
				{
					accumulatedText = string.IsNullOrEmpty(accumulatedText) ? line : accumulatedText + "\n" + line;
				}
			}

         // Re-resolve after commands so queued user text uses the current role/model immediately.
			role = _roleService.GetRole(conversation.Role);
			service = null;
			if (role != null)
			{
				service = _registry.GetServiceForRole(role, conversation.Model);
			}

			canRunLLM = role != null && service != null;
				if (canRunLLM)
				{
					if (role != null && role.SystemPrompt != conversation.GetSystemPrompt())
					{
						if (!string.IsNullOrEmpty(role.SystemPrompt))
							bundle.OnSystemMessage(null!, role.SystemPrompt);
					}

					if (!string.IsNullOrEmpty(accumulatedText))
					{
						bundle.OnUserMessage(null!, accumulatedText);
						conversation.NeedsLlmAttention = true;
					}
				}

			// 3. Run the LLM whenever the conversation has work; yield briefly if there is nothing to do.
			if (!cancellationToken.IsCancellationRequested && canRunLLM && conversation.NeedsLlmAttention)
			{
				LlmResult result = await RunLlmTurnAsync(conversation, bundle, role!, service!, cancellationToken);
				if (result.ExitReason == LlmExitReason.Failed)
				{
					_transport.Error(result.ErrorMessage + ": Change to a different /model and manually /compact, or /clear or start a /session new.");
					exitCode = 1;
				}
				else exitCode = 0;
			}
			else
			{
				await Task.Delay(10, cancellationToken);
			}
		}

		SessionService.Save(conversation);
		_settings.Settings.LastSessionId = conversation.Id;
		_settings.SaveSettings();
		return exitCode;
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
	// If the context is full, compaction is attempted immediately and the result reflects that outcome.
	public async Task<LlmResult> RunLlmTurnAsync(BeastSession conversation, ListenerBundle bundle, LLMRole role, LlmService service, CancellationToken cancellationToken)
	{
		conversation.Model = service!.Model.ConfigId;

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
		if (result.Success)
		{
			conversation.NeedsLlmAttention = false;
			SendStats(conversation, service.Model.Config.ContextWindow);
		}
		if (result.ExitReason == LlmExitReason.ContextFull)
		{
			_transport.Status("Running compaction...");
			result = await CompactAsync(conversation, bundle, service, role, cancellationToken);
		}

		return result;
	}

	// ---- Compaction ----

	private async Task<LlmResult> CompactAsync(BeastSession conversation, ListenerBundle bundle, LlmService service, LLMRole role, CancellationToken cancellationToken)
	{
		// Snapshot all protocol state before any modification so we can restore it exactly on failure.
		StateSnapshot snapshot = conversation.Snapshot();

		// If there is a pending user message, cache it and replace it with the compaction prompt
		// so the model sees the prompt at the end. On success it gets appended to the fresh context.
		string? pendingUserMessage = bundle.PopLastUserMessage();
		bundle.OnUserMessage(null!, _settings.Settings.CompactionPrompt);

		// Single-shot: stop as soon as the model responds once.
		_transport.Status("[Compaction] Started.");
		LlmResult result = await service.RunToCompletionAsync(conversation, bundle, Array.Empty<Tool>(), 0, _transport, cancellationToken);

	 string? compactedContent = bundle.GetLastAssistantText();

		if (result.Success && !string.IsNullOrWhiteSpace(compactedContent))
		{
			// Wipe every protocol state and rebuild from the compacted summary as an assistant turn.
			bundle.OnClear();
			if (!string.IsNullOrEmpty(role.SystemPrompt))
				bundle.OnSystemMessage(null!, role.SystemPrompt);
			bundle.OnAssistantTurn(null!, compactedContent, string.Empty, Array.Empty<SemanticToolCall>());
			_transport.Status("[Compaction] Complete.");

			if (pendingUserMessage != null)
			{
				bundle.OnUserMessage(null!, pendingUserMessage);
			}
		}
		else
		{
			conversation.RestoreSnapshot(snapshot);

			if (pendingUserMessage != null)
			{
				bundle.OnUserMessage(null!, pendingUserMessage);
			}

			_transport.Status("[Compaction] Failed.");
		}

		return result;
	}

   // Pushes the current completion candidates to Beast as a JSON array.
	private void SendCompletionCandidates(BeastSession conversation)
	{
		List<string> candidates = BuildCompletionCandidates(conversation);
		string json = JsonSerializer.Serialize(candidates);
		_transport.Completions(json);
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

		List<(string id, string displayName, int messageCount)> sessions = SessionService.List();
		candidates.Add("/session new");
		foreach ((string id, string displayName, int messageCount) s in sessions)
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

	private async Task RunTestsAsync(string? filter, CancellationToken cancellationToken)
	{
		_transport.Status("Running tests...");

		TestContext ctx = new TestContext(_transport);

		LlmServiceTests.Test(ctx);
		await FileToolsTests.TestAsync(ctx);
		ShellToolsTests.Test(ctx);
		await WebToolsTests.TestAsync(ctx, _settings.Settings.WebSearch);
		await SearchToolsTests.TestAsync(ctx);
		await PerModelLlmTests.TestAsync(ctx, _registry, _roleService, _settings, cancellationToken);

		_transport.Output($"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
	}

	// ---- Helpers ----

	private void ReloadRegistry()
	{
		_settings.LoadSettings();
		_registry.LoadFromConfigs(_settings, _roleService);
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