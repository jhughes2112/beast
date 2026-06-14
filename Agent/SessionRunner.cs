using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Executes a session: LLM turns, tool dispatch, and role transitions. Subagent-wrapped tool
// calls are delegated to SubagentRunner; single-turn execution lives in TurnRunner.
// Ownership rules:
//   - Session system prompt is set once at construction; never mutated during a run.
//   - RunAsync runs until cancelled or until context fills; it compacts internally when needed
//     and returns the successor runner. Returns null when the process should stop.
//   - Session is saved in the RunAsync finally block on every exit.
public class SessionRunner
{
	private readonly LlmRegistry _registry;
	private readonly RoleService _roleService;
	private readonly SettingsService _settings;
	private readonly ITransportServer _transport;
	private readonly CancellationTokenSource _cancellationTokenSource;

	// Tracks the session currently being executed; updated whenever the session changes
	// (session switch, role transition). Accessed by CompactAsync after RunAsync returns.
	private Session _currentSession;

	// Per-runner LlmService — owns a fresh ProtocolProxy so conversation state is never shared
	// with other sessions. Replaced when the model or role changes, or after a permanent failure.
	private LlmService? _service;

	// Keyed by "agent:{roleName}" or "sub:{roleName}". Populated lazily, cleared on /reload.
	// Safe because ToolFactory.BuildSubagent captures _subagent.RunSubSessionAsync, and the
	// SubagentRunner reads the live current session through its accessor, so the delegate stays
	// valid for the runner's lifetime.
	private readonly Dictionary<string, Tool[]> _toolsByRole = new(StringComparer.OrdinalIgnoreCase);

	// Handles subagent-wrapped tool calls by running ephemeral "Tools"-role sub-sessions.
	private readonly SubagentRunner _subagent;

	private bool _wantsCompact = false;

	// Set by the always-available state_transition tool's handler when the model opts to transition. Read and
	// cleared by RunAsync after each turn; non-null _pendingTruth means a transition is pending.
	private string? _pendingStatement;
	private string? _pendingContext;

	private List<(string id, string displayName, int messageCount)> _cachedSessions = new List<(string, string, int)>();

	public SessionRunner(
		Session session,
		LlmRegistry registry,
		RoleService roleService,
		SettingsService settings,
		ITransportServer transport,
		CancellationTokenSource cancellationTokenSource)
	{
		_registry = registry;
		_roleService = roleService;
		_settings = settings;
		_transport = transport;
		_cancellationTokenSource = cancellationTokenSource;
		_currentSession = session;
		_subagent = new SubagentRunner(registry, roleService, transport, () => this.CurrentSession);
	}

	// Routes inbound input to the session tree by ID.
	public void Deliver(string targetId, string text) => _currentSession.Deliver(targetId, text);

	public string ActiveSessionId => _currentSession.Id;
	public Session CurrentSession => _currentSession;

	// Runs until cancelled. Compaction is handled inline: when context fills the current session
	// is summarized, a child session is created and announced, and the loop continues on the new session.
	public async Task<Session> RunAsync(CancellationToken ct)
	{
		Session session = _currentSession;
		session.ReplayToTransport();
		session.SendStats();

		_cachedSessions = SessionService.List();
		string lastCompletionCandidates = JsonSerializer.Serialize(BuildCompletionCandidates(session));
		_transport.Completions(session.Id, lastCompletionCandidates);

		try
		{
			while (!ct.IsCancellationRequested)
			{
				// 1. Drain all pending commands from the session queue.
				session = await DrainInputAsync(session);
				if (_currentSession != session)
				{
					// Send history to the client whenever the active session changes.
					session.ReplayToTransport();
					session.SendStats();
					SaveRoot(_currentSession);
					_currentSession = session;
				}

				// 2. Resolve role and refresh service after drain (commands may have changed role or model).
				Role? role = _roleService.GetRole(session.Role);
				_service = RefreshService(role, session);

				if (_service != null)
				{
					if (_service.Model.ConfigId != session.Model)  // if the model changed, update the session with it
					{
						session.UpdateModel(_service.Model);
						session.SendStats();
					}

					string currentCandidates = JsonSerializer.Serialize(BuildCompletionCandidates(session));
					if (currentCandidates != lastCompletionCandidates)
					{
						lastCompletionCandidates = currentCandidates;
						_transport.Completions(session.Id, currentCandidates);
					}

					if (_wantsCompact)
					{
						_wantsCompact = false;
						Session? compacted = await CompactAsync(ct);
						if (compacted == null)
							break;
						session = compacted;
						_service = null;  // compacted session starts fresh
					}
				}

				// 3. Run the LLM whenever the session has work.
				// NeedsAttention() returns false after an interrupt until AddUserMessage() is called.
				bool contextFull = false;
				if (session.NeedsAttention() && _service != null && role != null)
				{
					session.SendBusy();
					try
					{
						Tool[] tools = GetOrBuildTools(role, session.IsSubagent);

						// Tool dispatch loop: keep calling LLM until it returns without tool calls or fails.
						bool completed = false;
						while (!completed)
						{
							ProtocolResult result = await _service.RunToCompletionAsync(session, tools, null, _settings.Settings.CompactionReserveTokens, 0, _transport, _cancellationTokenSource.Token);

							if (result.Outcome == ProtocolCallOutcome.Success)
							{
								// Commit the assistant turn to canonical and protocol state before processing.  This may include thinking, output, and tool call requests all in one message.
								session.CommitAssistantTurn(result.Payload!);

								// Process the assistant response and dispatch any tool calls.
								bool hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, tools, session, _transport, _cancellationTokenSource.Token);
								if (!hasToolCalls)
								{
									// No tool calls means the assistant has finished.
									completed = true;
								}
								else
								{
									session.CommitToolResults(result.Payload!);
								}

								// Check for pending user input before next turn.  If we add something, we definitely are not done yet.
								string? newUserInput = session.TryGetPendingInput();
								if (!string.IsNullOrEmpty(newUserInput))
								{
									session.Bundle.OnUserMessage(newUserInput);
									completed = false;
								}

								session.SendStats();
							}
							else if (result.Outcome == ProtocolCallOutcome.ContextFull)
							{
								// Context budget exhausted before making the call. Caller handles compaction.
								contextFull = true;
								completed = true;
							}
							else if (result.Outcome == ProtocolCallOutcome.TooManyRetries)
							{
								// Rate-limited repeatedly; caller should try another model or abort.
								_transport.Error(session.Id, "Rate limited after too many retries");
								completed = true;
							}
							else if (result.Outcome == ProtocolCallOutcome.Interrupted)
							{
								// User cancelled the turn — do not surface as error.
								completed = true;
							}
							else
							{
								// Failed, transient, or other error.
								_transport.Error(session.Id, result.ErrorMessage);
								completed = true;
							}
						}

						// A model-initiated state_transition call records a pending transition. Apply it
						// regardless of how the turn ended — the model opted to transition, so it
						// takes precedence over context-full/failed handling for this turn.
						if (_pendingStatement != null)
						{
							string statement = _pendingStatement;
							string newContext = _pendingContext ?? string.Empty;
							_pendingStatement = null;
							_pendingContext = null;

							Session? advanced = await ApplyTransitionAsync(session, role, statement, newContext, _cancellationTokenSource.Token);
							if (advanced != null)
							{
								session = advanced;
								_service = null;  // new role may use a different model
							}
						}
						else
						{
							// The model finished without opting to transition. Confront it with the
							// role's end-of-turn question and require an explicit Answer-tool call.
							Session? advanced = await AdvanceRoleAsync(session, role, _service, _cancellationTokenSource.Token);
							if (advanced != null)
							{
								session = advanced;
								_service = null;  // new role may use a different model
							}
						}
						// Interrupted: _interruptedAndWaiting is set in EndTurn;
						// NeedsAttention() returns false until AddUserMessage() is called with new input.
					}
					finally
					{
						session.SendIdle();

						// Persist the root each turn so its on-disk record and lastSession.json stay current.
						SaveRoot(session);
					}
				}

				if (contextFull)
				{
					Session? compacted = await CompactAsync(ct);
					if (compacted == null)
						break;
					session = compacted;
					// The old service's protocol still holds the pre-compaction conversation;
					// force a fresh service so the compacted session rehydrates from its own canonical.
					_service = null;
				}

				if (role != null)
				{
					long waitMs = _registry.GetMillisecondsUntilAvailable(role);
					if (waitMs > 0)
						_transport.Status(session.Id, waitMs == long.MaxValue
							? "No Models Available"
							: $"No Models Available, waiting {(int)Math.Ceiling(waitMs / 1000.0)}s");
					// Floor of 250ms keeps the idle loop from spinning; input still wakes it
					// instantly via the session signal. The wait CTS is cancelled afterwards so
					// a losing WaitForInputAsync waiter is removed instead of accumulating.
					int delayMs = waitMs == long.MaxValue ? 30000 : Math.Clamp((int)waitMs, 250, 30000);
					using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
					Task waitTask = session.WaitForInputAsync(waitCts.Token);
					await Task.WhenAny(Task.Delay(delayMs, _cancellationTokenSource.Token), waitTask);
					waitCts.Cancel();
					try
					{ await waitTask; }
					catch (OperationCanceledException) { }
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			if (!_currentSession.Ephemeral)
				SaveRoot(_currentSession);
		}
		return _currentSession;
	}

	// Summarizes the current session and returns a new child session seeded with the compacted content.
	// Returns null if the service, role, or summary prompt is unavailable.
	private async Task<Session?> CompactAsync(CancellationToken ct)
	{
		Role? role = _roleService.GetRole(_currentSession.Role);

		if (role == null || string.IsNullOrEmpty(role.SummaryPrompt))
		{
			_transport.Status(_currentSession.Id, "[Compaction] No role or summary prompt available.");
			return null;
		}

		IReadOnlyList<CanonicalMessage> tailExchanges = ExtractTailExchanges(_currentSession.Data.Messages, 2);

		_transport.Status(_currentSession.Id, "[Compaction] Started.");
		string? summary = await Summarizer.SummarizeAsync(_currentSession, role.SummaryPrompt, Array.Empty<Tool>(), _registry, _roleService, _transport, ct);

		if (string.IsNullOrWhiteSpace(summary))
		{
			_transport.Status(_currentSession.Id, "[Compaction] Failed.");
			return null;
		}

		string newDisplayName = Session.IncrementDisplayName(_currentSession.DisplayName);
		// Allocate the child ID before saving so the updated ChildCounter is persisted.
		string newSessionId = _currentSession.AllocateChildId();

		SaveRoot(_currentSession);

		// Compaction starts a clean-slate session (no history) seeded with the summary plus the last
		// couple of exchanges — not a copy of the prior conversation.
		BeastSession freshData = new BeastSession(newSessionId, newDisplayName, _currentSession.Model, _currentSession.Role, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, _currentSession.Ephemeral, 0);

		Session newSession = new Session(freshData, role.SystemPrompt, _transport, false);
		newSession.AddUserMessage(summary);
		newSession.FlushPendingMessages();
		newSession.ReplayExchanges(tailExchanges, true);

		_currentSession.AddChild(newSession);
		newSession.AnnounceToClient();

		if (!newSession.Ephemeral)
			SaveRoot(newSession);

		_cachedSessions = SessionService.List();
		_transport.Status(_currentSession.Id, "[Compaction] Complete.");
		return newSession;
	}

	// ---- Session management ----

	// Persists a top-level session (the root the user drives, and its compaction/role successors) and
	// records it as the session to resume. Inferring the display name first matters: the root is
	// created nameless, and SessionService.Save skips nameless sessions — so without this the root
	// would never reach disk while named subagents would. Always marks the save as root so
	// lastSession.json tracks the top-level conversation, never a subagent tool session.
	private static void SaveRoot(Session session)
	{
		session.InferDisplayName();
		SessionService.Save(session.Data, true);
	}

	private Session CreateFreshSession(string roleName, bool ephemeral)
	{
		if (string.IsNullOrEmpty(roleName))
		{
			foreach (Role r in _roleService.Roles.Values)
			{
				roleName = r.Name;
				break;
			}
		}
		Role? role = _roleService.GetRole(roleName);
		string systemPrompt = role?.SystemPrompt ?? string.Empty;
		LlmModel? model = role != null ? _registry.GetModelForRole(role, string.Empty, 0) : null;
		string modelId = model?.ConfigId ?? string.Empty;
		BeastSession fresh = new BeastSession(Guid.NewGuid().ToString(), string.Empty, modelId, roleName, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, ephemeral, 0);
		return new Session(fresh, systemPrompt, _transport, false);
	}

	// ---- Input processing ----

	// Drains all queued commands from the session in arrival order and dispatches them.
	private async Task<Session> DrainInputAsync(Session session)
	{
		while (session.TryDequeueCommand(out string? line))
		{
			string trimmed = line!.TrimStart('/').Trim();
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
					_cancellationTokenSource.Cancel();
					break;
				case "compact":
					_wantsCompact = true;
					break;
				case "session":
					if (args == "new")
					{
						if (!session.Ephemeral)
							SaveRoot(session);
						session = CreateFreshSession(session.Role, false);
						_service = null;
						_cachedSessions = SessionService.List();
						_transport.Status(session.Id, "New session started.");
					}
					else if (args == "none")
					{
						if (!session.Ephemeral)
							SaveRoot(session);
						session = CreateFreshSession(session.Role, true);
						_service = null;
						_cachedSessions = SessionService.List();
						_transport.Status(session.Id, "Ephemeral session started (not saved).");
					}
					else if (args != null)
					{
						string resolvedId = args;
						foreach ((string id, string displayName, int messageCount) s in _cachedSessions)
						{
							if (string.Equals(s.displayName, args, StringComparison.Ordinal) ||
								string.Equals(s.id, args, StringComparison.Ordinal))
							{
								resolvedId = s.id;
								break;
							}
						}
						BeastSession? loaded = SessionService.Load(resolvedId);
						if (loaded != null)
						{
							if (!session.Ephemeral)
								SaveRoot(session);
							session = new Session(loaded, string.Empty, _transport, false);
							_service = null;
							_cachedSessions = SessionService.List();
							_transport.Status(session.Id, "Switched to session: " + loaded.DisplayName);
						}
						else
						{
							_transport.Error(session.Id, "Session not found: " + args);
						}
					}
					break;
				case "reload":
					_roleService.Reload();
					_settings.LoadSettings();
					_registry.LoadFromConfigs(_settings, _roleService);
					await _registry.ProbeEndpointsAsync(_cancellationTokenSource.Token);
					_registry.ResetAllAvailability();
					_service = null;
					_toolsByRole.Clear();
					_transport.Status(session.Id, "Config files reloaded.");
					break;
				case "role":
					if (args != null)
					{
						session.UpdateRole(args);
						_service = null;  // new role may use a different model
						_transport.Status(session.Id, $"Role set to {args}");
					}
					break;
				case "model":
					if (args != null)
					{
						Role? modelRole = _roleService.GetRole(session.Role);
						LlmModel? targetModel = modelRole != null ? _registry.GetModelForRole(modelRole, args, 0) : null;
						if (targetModel == null)
							_transport.Error(session.Id, $"Unknown model: {args}");
						else
						{
							session.UpdateModel(targetModel);
							_registry.ResetAvailability(args);
							_service = null;  // force fresh service with new model next turn
							_transport.Status(session.Id, $"Model set to {args}");
						}
					}
					break;
				case "help":
					_transport.Output(session.Id, "Commands: /compact, /clear, /reload, /role <id>, /model <id>, /session new, /session none, /session <id>, /test, /quit");
					break;
				case "test":
					await RunTestsAsync(session.Id, args);
					break;
				default:
					_transport.Error(session.Id, $"Unknown command reached agent: /{verb}");
					break;
			}
		}

		return session;
	}

	// Returns all completable tokens: slash commands, role names, model names, and session ids.
	private List<string> BuildCompletionCandidates(Session session)
	{
		List<string> candidates = new List<string>
		{
			"/compact", "/reload", "/role", "/model",
			"/session", "/help"
		};

		Role? activeRole = _roleService.GetRole(session.Role);
		LlmModel? activeModel = activeRole != null
			? _registry.GetModelForRole(activeRole, session.Model, session.ContextLength + _settings.Settings.CompactionReserveTokens)
			: null;

		string currentRoleName = activeRole != null ? activeRole.Name : session.Role;
		AddCurrentFirst(candidates, "/role ", currentRoleName, _roleService.Roles.Keys);

		if (activeRole != null)
		{
			string currentModelId = activeModel != null ? activeModel.ConfigId : session.Model + " (not available)";
			List<string> enabledModels = _registry.GetEnabledModelsForRole(activeRole);
			AddCurrentFirst(candidates, "/model ", currentModelId, enabledModels);
		}

		candidates.Add("/session new");
		candidates.Add("/session none");
		foreach ((string id, string displayName, int messageCount) s in _cachedSessions)
		{
			if (!string.IsNullOrEmpty(s.displayName))
				candidates.Add("/session " + s.displayName);
			else
				candidates.Add("/session " + s.id);
		}

		return candidates;
	}

	private static void AddCurrentFirst(List<string> candidates, string prefix, string currentValue, ICollection<string> values)
	{
		if (!string.IsNullOrEmpty(currentValue) && values.Contains(currentValue))
			candidates.Add(prefix + currentValue);
		foreach (string value in values)
		{
			if (value == currentValue)
				continue;
			candidates.Add(prefix + value);
		}
	}

	// ---- Tests ----

	private async Task RunTestsAsync(string sessionId, string? filter)
	{
		_transport.Status(sessionId, "Running tests...");
		TestContext ctx = new TestContext(_transport);

		LlmServiceTests.Test(ctx);
		ContextBudgetTests.Test(ctx);
		FixJsonTests.Test(ctx);
		await FileToolsTests.TestAsync(ctx);
		ShellToolsTests.Test(ctx);
		await WebToolsTests.TestAsync(ctx, _settings.Settings.WebSearch);
		await PerModelLlmTests.TestAsync(ctx, _registry, _roleService, _settings, _cancellationTokenSource.Token);
		ProtocolSwitchTests.Test(ctx);

		_transport.Output(sessionId, $"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
	}

	// ---- Role transition ----

	// Applies a model-initiated role transition. The model called the always-available Answer tool
	// mid-turn, selecting one of the role's truth statements; selectedAnswer is the full briefing
	// that becomes the next phase's first prompt. Runs end-of-turn bookkeeping, then maps the truth.
	private Task<Session?> ApplyTransitionAsync(Session session, Role currentRole, string selectedTruth, string selectedAnswer, CancellationToken ct)
	{
		// The model already provided a full briefing via the Answer tool's answer argument.
		// No summary needed: selectedAnswer IS the handoff context for the next phase.
		return Task.FromResult(CreateNextRoleSession(session, currentRole, selectedTruth, selectedAnswer));
	}

	// Called after a completed worker turn when the model did NOT already opt to transition via the
	// Answer tool. Confronts the model in the SAME session: appends a user prompt requiring an explicit
	// Answer-tool call and exposes only the Answer tool, so it cannot keep working. No summary or
	// compaction is needed — the full conversation is already in context, so the model writes its
	// briefing from its own working memory. The Answer call lands in the current conversation;
	// CreateNextRoleSession then continues this session (same role) or hands off to a fresh one
	// (different role). If the role defines no EndOfTurnPrompt + Statements, there is nothing to confront.
	private async Task<Session?> AdvanceRoleAsync(Session session, Role currentRole, LlmService service, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(currentRole.EndOfTurnPrompt) || currentRole.Statements.Count == 0)
			return null;

		string? selectedStatement = null;
		string? selectedContext = null;
		Tool answerTool = BuildStateTransitionTool(currentRole.EndOfTurnPrompt, currentRole.Statements.Keys, (statement, context) => { selectedStatement = statement; selectedContext = context; });

		// Run the confrontation in the current session with only the Answer tool, forcing the model to
		// call it, and reusing the session's service so the conversation continues uninterrupted. Retry
		// on a bad answer (the tool rejects an answer that does not begin with a truth statement).
		session.AddUserMessage(currentRole.EndOfTurnPrompt);

		// Cap the answer to whatever space remains in the window so the briefing always fits.
		int answerCap = session.Budget.MaxCompletionTokens() ?? 0;
		ProtocolResult evalResult = await service.RunToCompletionAsync(session, new Tool[] { answerTool }, "state_transition", 0, answerCap, _transport, ct);
		if (selectedStatement == null)
		{
			_transport.Error(session.Id, "[Role] Completed session failed to call Answer tool; staying in current role.");
			return null;
		}

		return CreateNextRoleSession(session, currentRole, selectedStatement, selectedContext!);
	}

	// Maps the chosen truth label to the next role and returns the successor session. The Answer tool
	// handler only accepts answers beginning with one of the Statement keys, so the lookup always hits.
	// Empty next-role = task finished; returns null and control goes back to the user. Same role =
	// continue in place: the answer becomes the next user prompt and null is returned so the caller
	// keeps the current session. Different role = save the current session and start a fresh one whose
	// first user prompt is the model's full answer.
	private Session? CreateNextRoleSession(Session session, Role currentRole, string selectedStatement, string newContext)
	{
		currentRole.Statements.TryGetValue(selectedStatement, out string? nextRoleName);

		if (string.IsNullOrEmpty(nextRoleName))
		{
			_transport.Error(session.Id, $"[Role] {selectedStatement} → done.");
			return null;
		}

		Role nextRole = _roleService.GetRole(nextRoleName) ?? currentRole;

		// Same role: stay in the current conversation. The briefing becomes the next user prompt so the
		// model keeps working in place; no new session is created and the caller keeps this session.
		if (string.Equals(nextRole.Name, currentRole.Name, StringComparison.OrdinalIgnoreCase))
		{
			session.AddUserMessage(newContext);
			_transport.Status(session.Id, $"[Role] {selectedStatement} → continue ({currentRole.Name}).");
			return null;
		}

		if (!session.Ephemeral)
			SaveRoot(session);

		BeastSession freshData = new BeastSession(session.AllocateChildId(), session.DisplayName, session.Model, nextRole.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, session.Ephemeral, 0);
		Session next = new Session(freshData, nextRole.SystemPrompt, _transport, false);

		next.AddUserMessage(newContext);

		_transport.Error(session.Id, $"[Role] {selectedStatement} → {nextRole.Name}");
		return next;
	}

	// Builds the always-available Answer tool. Its description is the role's EndOfTurnPrompt, so the
	// model knows the question that, once answerable, lets it transition at any point in a turn.
	// The answer must begin with one of the truth statements verbatim and continues with a briefing
	// for the next phase; onAnswer receives (matched statement, full answer). A non-matching answer
	// returns an error result so the model can correct itself within the same turn.
	private static Tool BuildStateTransitionTool(string endOfTurnPrompt, IEnumerable<string> truthLabels, Action<string, string> onAnswer)
	{
		List<string> labels = new List<string>(truthLabels);
		string optionList = string.Join("\n", labels);

		JsonObject statementProp = new JsonObject();
		statementProp["type"] = "string";
		statementProp["description"] = $"Your selection either ends this conversation, continues it, or sends it to another worker to continue. Must be exactly one of these statements, verbatim:\n{optionList}";

		JsonObject contextProp = new JsonObject();
		contextProp["type"] = "string";
		contextProp["description"] = $"Provide any context the next phase needs to start working: what has been accomplished, what remains, and any constraints or decisions it must know about. This becomes the next model's first prompt, so be informative and provide actionable direction as the final statement.";

		JsonObject properties = new JsonObject();
		properties["statement"] = statementProp;
		properties["context"] = contextProp;  // optional

		JsonArray required = new JsonArray();
		required.Add(JsonValue.Create("statement"));

		JsonObject parameters = new JsonObject();
		parameters["type"] = "object";
		parameters["properties"] = properties;
		parameters["required"] = required;

		return new Tool
		{
			Definition = new ToolDefinition
			{
				Function = new FunctionDefinition
				{
					Name = "state_transition",
					Description = $"{endOfTurnPrompt} Use this tool to end the conversation or transition the task to another worker.",
					Parameters = parameters
				}
			},
			Handler = (JsonObject args, string toolCallId, CancellationToken ct, ITransportServer transport, string sessionId, int maxOutputTokens) =>
			{
				string statement = (args["statement"]?.GetValue<string>() ?? string.Empty).TrimStart();
				string context = (args["context"]?.GetValue<string>() ?? string.Empty).TrimStart();

				// Try to find the closest match.
				string? matched= FixJson.FuzzyMatchToolName(statement, labels.ToArray(), 5, null);

				ToolResult result;
				if (matched != null)
				{
					onAnswer(matched, context);
					result = new ToolResult(toolCallId, "OK.", string.Empty, 0, 0);
				}
				else
				{
					result = new ToolResult(toolCallId, string.Empty, $"Your statement must be exactly one of these statements, verbatim: {optionList}", 1, 0);
				}
				return Task.FromResult(result);
			}
		};
	}

	// ---- Helpers ----

	// Returns the cached Tool[] for the role+mode combination, building on first call.
	// Cleared on /reload so config changes (new tools, webSearch) take effect immediately.
	private Tool[] GetOrBuildTools(Role role, bool isSubagent)
	{
		string key = isSubagent ? $"sub:{role.Name}" : $"agent:{role.Name}";
		if (_toolsByRole.TryGetValue(key, out Tool[]? cached))
			return cached;

		bool hasToolsRole = _roleService.GetRole("Tools") != null;
		Dictionary<string, Tool> allTools = (!isSubagent && hasToolsRole)
			? ToolFactory.BuildSubagent(_settings.Settings.WebSearch, _subagent.RunSubSessionAsync)
			: ToolFactory.Build(_settings.Settings.WebSearch);

		List<Tool> toolList = new List<Tool>();
		foreach (string toolName in role.Tools)
		{
			if (allTools.TryGetValue(toolName, out Tool? tool))
				toolList.Add(tool);
		}

		// The root agent always carries the Answer tool when its role defines transitions, so the
		// model can opt to transition at any point in a turn rather than only after it completes.
		// Its handler records the decision into _pendingTruth/_pendingAnswer for RunAsync to apply.
		if (!isSubagent && !string.IsNullOrEmpty(role.EndOfTurnPrompt) && role.Statements.Count > 0)
		{
			Tool stateTransitionTool = BuildStateTransitionTool(role.EndOfTurnPrompt, role.Statements.Keys, (statement, context) =>
			{
				_pendingStatement = statement;
				_pendingContext = context;
			});
			toolList.Add(stateTransitionTool);
		}

		Tool[] tools = toolList.ToArray();
		_toolsByRole[key] = tools;
		return tools;
	}

	// Replaces _service when the model or role has changed, or when the service has permanently
	// failed. Also updates session.Model if the registry selected a different model as fallback.
	private LlmService? RefreshService(Role? role, Session session)
	{
		if (_service != null && !_service.IsDown && _service.Model.ConfigId == session.Model && session == _currentSession)
			return null;

		int minCtx = session.ContextLength + _settings.Settings.CompactionReserveTokens;
		LlmService? newService = _registry.CreateService(role, session.Model, minCtx);
		return newService;
	}

	// Returns the last `count` user-exchange groups from the canonical message list, oldest-first.
	private static IReadOnlyList<CanonicalMessage> ExtractTailExchanges(IReadOnlyList<CanonicalMessage> messages, int count)
	{
		List<int> userStarts = new List<int>();
		for (int i = 0; i < messages.Count; i++)
		{
			if (messages[i] is UserMessage)
				userStarts.Add(i);
		}

		if (userStarts.Count == 0)
			return new List<CanonicalMessage>();

		int startGroup = userStarts.Count > count ? userStarts.Count - count : 0;
		int startIndex = userStarts[startGroup];

		List<CanonicalMessage> result = new List<CanonicalMessage>();
		for (int i = startIndex; i < messages.Count; i++)
			result.Add(messages[i]);
		return result;
	}
}
