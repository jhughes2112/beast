using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


// Manages the agent loop: read input, run LLM turns, compact, save sessions.
//
// Architecture:
//   - Conversation is a LOCAL VARIABLE in RunAsync. No field holds it.
//   - Startup is silent: no upfront validation. Errors fire at interaction time.
//   - A single while-loop reads input, then runs any pending LLM attention.
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
	private readonly IFramedTransport _transport;

	public AgentOrchestrator(
		LlmRegistry registry,
		RoleService roleService,
		SettingsService settings,
		IFramedTransport transport)
	{
		_registry = registry;
		_roleService = roleService;
		_settings = settings;
		_transport = transport;
		_registry.LoadFromConfigs(_settings, _roleService);
	}

	public async Task<int> RunAsync(string? prompt, CancellationToken cancellationToken)
	{
		bool interactive = string.IsNullOrEmpty(prompt);
		Queue<string> pendingInput = new();

		if (!string.IsNullOrEmpty(prompt))
		{
			pendingInput.Enqueue(prompt);
		}

		BeastSession conversation = LoadOrCreateConversation();
		bool wantsExit = false;
		int exitCode = 0;

		do
		{
			// 1. Determine if we can run the LLM this turn.
			LLMRole? role = _roleService.GetRole(conversation.Role);
			LlmService? service = null;
			if (role != null)
			{
				service = _registry.GetServiceForRole(role, conversation.Model);
			}

			bool canRunLLM = role != null && service != null;

			// 2. Fetch input from transport if interactive.
			if (interactive)
			{
				string? incoming = await _transport.TryReadAsync(100, cancellationToken);
				if (incoming != null && incoming.Length > 0)
				{
					pendingInput.Enqueue(incoming);
				}
			}

			// 3. Process the entire input queue: run commands in order, accumulate text into one user message.
			string accumulatedText = string.Empty;
			while (pendingInput.Count > 0)
			{
				string line = pendingInput.Dequeue();
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
						case "quit":
							wantsExit = true;
							break;
						case "compact":
							if (canRunLLM)
							{
								_transport.Status("Running compaction...");
								LlmResult compactResult = await CompactAsync(conversation, service!, cancellationToken);
								canRunLLM = compactResult.Success;
							}
							break;
						case "session":
							if (args == "new")
							{
								SessionService.Save(conversation);
								conversation = CreateFreshConversation(conversation.Role);
								_transport.Status("New session started.");
							}
							else if (args != null)
							{
								BeastSession? loaded = SessionService.Load(args);
								if (loaded != null)
								{
									SessionService.Save(conversation);
									conversation = loaded;
									_transport.Status("Switched to session: " + loaded.DisplayName);
								}
								else
								{
									_transport.Error("Session not found: " + args);
								}
							}
							break;
						case "clear":
							conversation.Messages.RemoveRange(1, conversation.Messages.Count - 1);
							_transport.Status("Session cleared.");
							break;
						case "reload":
							_roleService.Reload();
							ReloadRegistry();
							canRunLLM = false;
							_transport.Status("Config files reloaded.");
							break;
						case "role":
							if (args != null)
							{
								conversation.Role = args;
								canRunLLM = false;
								_transport.Status($"Role set to {args}");
							}
							break;
						case "model":
							if (args != null)
							{
								conversation.Model = args;
								canRunLLM = false;
								_transport.Status($"Model set to {args}");
							}
							break;
						case "complete":
							HandleComplete(args, conversation);
							break;
						case "help":
							_transport.Output("Commands: /compact, /clear, /reload, /role <id>, /model <id>, /session <id>, /test, /quit");
							break;
						case "test":
							RunTests(args);
							break;
					}
				}
				else
				{
					accumulatedText = string.IsNullOrEmpty(accumulatedText) ? line : accumulatedText + "\n" + line;
				}
			}

			if (!string.IsNullOrEmpty(accumulatedText))
			{
				conversation.AddUserMessage(accumulatedText);
			}

			// 4. Run the LLM if we can and it needs attention.
			if (canRunLLM && NeedsLlmAttention(conversation))
			{
				LlmResult result = await RunLlmTurnAsync(conversation, role!, service!, cancellationToken);
				if (result.ExitReason == LlmExitReason.Failed)
				{
					_transport.Error(result.ErrorMessage + ": Change to a different /model and manually /compact, or /clear or start a /session new.");
					exitCode = 1;
				}
				else exitCode = 0;
			}
		} while (!cancellationToken.IsCancellationRequested && !wantsExit);

		SessionService.Save(conversation);
		return exitCode;
	}

	// ---- Session management ----

	private BeastSession LoadOrCreateConversation()
	{
		string? lastSessionId = _settings.Settings?.LastSessionId;
		BeastSession? lastData = SessionService.LoadBySessionId(lastSessionId);
		if (lastData != null)
		{
			_transport.Status("Resumed session: " + lastData.DisplayName);
			return lastData;
		}

		return CreateFreshConversation(string.Empty);
	}

	private BeastSession CreateFreshConversation(string roleName)
	{
		if (string.IsNullOrEmpty(roleName))
		{
			foreach (LLMRole r in _roleService.Roles.Values)
			{
				roleName = r.Name;
				break;
			}
		}

		return BeastSession.CreateNew(Guid.NewGuid().ToString(), roleName, string.Empty);
	}

	// ---- LLM execution ----

	// Runs one LLM turn. Applies the role (system prompt) before calling the LLM.
	// If the context is full, compaction is attempted immediately and the result reflects that outcome.
	public async Task<LlmResult> RunLlmTurnAsync(BeastSession conversation, LLMRole role, LlmService service, CancellationToken cancellationToken)
	{
		conversation.Model = service!.Model.ConfigId;
		conversation.SetSystemPrompt(role.SystemPrompt);

		if (string.IsNullOrEmpty(conversation.DisplayName))  // grab a friendly name for the conversation if we do not have one yet
		{
			foreach (ConversationMessage m in conversation.Messages)
			{
				if (m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content))
				{
					string name = m.Content.Trim();
					conversation.DisplayName = name.Length > 50 ? name.Substring(0, 50) : name;
					break;
				}
			}
		}

		Tool[] tools = _registry.GetToolsForRole(role);
		LlmResult result = await service.RunToCompletionAsync(conversation, tools, CompactionReserveTokens, _transport, cancellationToken);
		if (result.Success)
		{
			conversation.MarkMessagesSent();
		}
		if (result.ExitReason == LlmExitReason.ContextFull)
		{
			_transport.Status("Running compaction...");
			result = await CompactAsync(conversation, service, cancellationToken);
		}

		return result;
	}

	// ---- Compaction ----

	private async Task<LlmResult> CompactAsync(BeastSession conversation, LlmService service, CancellationToken cancellationToken)
	{
		// Snapshot the message list before any modification so we can restore it exactly on failure.
		int snapshotCount = conversation.Messages.Count;

		// If there is a pending user message, cache it and replace it with the compaction prompt
		// so the model sees the prompt at the end. On success it gets appended to the fresh context.
		// On failure everything is restored and nothing has changed.
		string? pendingUserMessage = null;
		int lastIdx = conversation.Messages.Count - 1;
		if (lastIdx >= 1 && conversation.Messages[lastIdx].Role == "user")
		{
			pendingUserMessage = conversation.Messages[lastIdx].Content;
			conversation.Messages.RemoveAt(conversation.Messages.Count - 1);
		}
		conversation.AddUserMessage(_settings.Settings.CompactionPrompt);

		// Single-shot: stop as soon as the model responds once.
		_transport.Status("[Compaction] Started.");
		LlmResult result = await service.RunToCompletionAsync(conversation, Array.Empty<Tool>(), 0, _transport, cancellationToken);

		// The compaction response is already committed to conversation by LlmService.
		// Grab it from the last assistant message rather than from LlmResult.Content.
		string? compactedContent = null;
		for (int i = conversation.Messages.Count - 1; i >= 0; i--)
		{
			if (conversation.Messages[i].Role == "assistant")
			{
				compactedContent = conversation.Messages[i].Content;
				break;
			}
		}

		if (result.Success && !string.IsNullOrWhiteSpace(compactedContent))
		{
			// Clear everything, restore the system prompt slot at index 0, then add the compacted summary and pending user message.
			conversation.Messages.Clear();
			conversation.AddSystemMessage(string.Empty);
			conversation.AddAssistantMessage(compactedContent);
			_transport.Status("[Compaction] Complete.");

			if (pendingUserMessage != null)
			{
				conversation.AddUserMessage(pendingUserMessage);
			}
		}
		else
		{
			// Truncate back to the exact pre-compaction state, including any LLM responses that were committed.
			// snapshotCount must be at least 1 to preserve the system prompt at index 0.
			int restoreCount = snapshotCount >= 1 ? snapshotCount : 1;
			if (conversation.Messages.Count > restoreCount)
			{
				conversation.Messages.RemoveRange(restoreCount, conversation.Messages.Count - restoreCount);
			}

			if (pendingUserMessage != null)
			{
				conversation.AddUserMessage(pendingUserMessage);
			}

			_transport.Status("[Compaction] Failed.");
		}

		return result;
	}

	// Replies to a /complete request
	// a JSON array of strings that match the given prefix.
	private void HandleComplete(string? prefix, BeastSession conversation)
	{
		List<string> candidates = BuildCompletionCandidates(conversation);

		List<string> matches = new List<string>();
		foreach (string candidate in candidates)
		{
			if (string.IsNullOrEmpty(prefix) || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				matches.Add(candidate);
			}
		}

		string json = JsonSerializer.Serialize(matches);
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
				"/complete",
				"/help",
				"/test"
		};

		foreach (string roleName in _roleService.Roles.Keys)
		{
			candidates.Add("/role " + roleName);
		}

		LLMRole? activeRole = string.IsNullOrEmpty(conversation.Role) ? null : _roleService.GetRole(conversation.Role);
		if (activeRole != null)
		{
			foreach (string modelId in activeRole.Models)
			{
				candidates.Add("/model " + modelId);
			}
		}

		List<(string id, string displayName, int messageCount)> sessions = SessionService.List();
		candidates.Add("/session new");
		foreach ((string id, string displayName, int messageCount) s in sessions)
		{
			candidates.Add("/session " + s.id);
		}

		return candidates;
	}

	// ---- Tests ----

	private void RunTests(string? reportPath)
	{
		string outputPath = string.IsNullOrWhiteSpace(reportPath) ? "/workspace/test.txt" : reportPath.Trim();

		_transport.Status("Running tests...");

		TestContext ctx = new TestContext();
		TestCaptureTransport capture = new TestCaptureTransport();

		LlmServiceTests.Test(ctx);
		FileToolsTests.Test(ctx);
		ShellToolsTests.Test(ctx);
		WebToolsTests.Test(ctx);
		SearchToolsTests.Test(ctx);
		PerModelLlmTests.Test(ctx, _registry, _roleService, _settings, capture);

		string report = capture.BuildReport(ctx);

		try
		{
			String dir = System.IO.Path.GetDirectoryName(outputPath) ?? string.Empty;
			if (!string.IsNullOrEmpty(dir))
			{
				System.IO.Directory.CreateDirectory(dir);
			}
			System.IO.File.WriteAllText(outputPath, report);
			_transport.Output($"Test report written to {outputPath}\n\n" + report);
		}
		catch (Exception ex)
		{
			_transport.Error($"Could not write report to {outputPath}: {ex.Message}");
			_transport.Output(report);
		}
	}

	// ---- Helpers ----

	private bool NeedsLlmAttention(BeastSession conversation)
	{
		if (conversation.Messages.Count == 0) return false;
		ConversationMessage last = conversation.Messages[^1];
		if (last.Role == "user" || last.Role == "tool") return true;
		if (last.Role == "assistant" && last.ToolCalls != null && last.ToolCalls.Count > 0) return true;
		return false;
	}

	private void ReloadRegistry()
	{
		_settings.LoadSettings();
		_registry.LoadFromConfigs(_settings, _roleService);
	}
}