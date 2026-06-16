using System;
using System.Collections.Generic;
using System.IO;
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

	// Runs child sub-sessions on behalf of the root's subagent tool.
	private readonly SubagentRunner _subagent;

	// The root agent's delegation tool. Appended to the root's bound tools each turn; child agents never
	// receive it, so nesting stops at one level. Rebuilt on /reload so its role-name list stays current.
	private Tool _subagentTool;

	// The Task agent's termination tool. Its handler sets _taskCompleteCalled, which stops the end-of-turn
	// reminder loop so the root idles and waits for the user instead of being kept on task.
	private readonly Tool _taskCompleteTool;
	private bool _taskCompleteCalled = false;

	// fetch_url: fetches a page and filters it through the Web role. Injected for roles that declare it.
	private readonly Tool _fetchUrlTool;

	// Set by start_task (via StartTaskAsync) once its hooks succeed; after the turn the root switches to a
	// fresh Task session seeded with it. Null = no pending start. The start_task tool is built per turn
	// (in ToolsForTurnAsync) so its branch-argument description carries live git worktree context.
	private string? _pendingTaskObjective;

	private bool _wantsCompact = false;

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
		_subagentTool = ToolFactory.CreateSubagentTool(_roleService.SubagentRoles(), _subagent.RunSubagentAsync);
		_fetchUrlTool = ToolFactory.CreateFetchUrlTool(_registry, _roleService, () => this.CurrentSession);
		_taskCompleteTool = ToolFactory.CreateTaskCompleteTool(status =>
		{
			_taskCompleteCalled = true;
			if (!string.IsNullOrEmpty(status))
				_transport.Output(_currentSession.Id, status);
		});
	}

	// start_task callback: create (or attach to) the git worktree for the chosen branch and switch the
	// working directory into it, ready for the Task role. Any step failing aborts the switch and its
	// message becomes the start_task tool result, so the Default session sees why. On success the
	// objective is queued for the role switch after the turn.
	private async Task<string> StartTaskAsync(string objective, string branch, CancellationToken ct)
	{
		if (!IsValidBranchName(branch))
			return $"Invalid branch name '{branch}'. Use only letters, digits, '.', '_', '-', and '/'.";

		Role? taskRole = _roleService.GetRole("Task");
		if (taskRole == null)
			return "Error: Task role is not defined.";

		(string? worktreePath, string? worktreeError) = await CreateOrAttachWorktreeAsync(branch, ct);
		if (worktreeError != null)
			return worktreeError;

		// A git worktree is just a separate checkout directory; "switching" to it means working there.
		// The process CWD drives every tool's working directory, so the Task session and its subagents
		// now operate inside the worktree.
		try
		{
			Directory.SetCurrentDirectory(worktreePath!);
		}
		catch (Exception ex)
		{
			return $"Failed to switch to worktree '{worktreePath}': {ex.Message}";
		}

		_pendingTaskObjective = objective;
		return string.Empty;
	}

	// Creates a git worktree for the branch under the bind-mounted config dir, namespaced by repo
	// (~/.beast/worktrees/<repo>/<branch>) so it persists to the host and never nests in /workspace. The
	// repo name is derived from git (remote URL, else the root-commit SHA) inside the script, which then
	// echoes the chosen path as its only stdout line. An existing worktree for that branch — or an existing
	// directory at the target path — is accepted as-is rather than treated as an error. Returns (path, null)
	// on success or (null, error) with the git output.
	private static async Task<(string? path, string? error)> CreateOrAttachWorktreeAsync(string branch, CancellationToken ct)
	{
		string dirName = branch.Replace('/', '-');

		// branch and dirName are validated to a shell-safe charset before this runs, so single-quoting is
		// sufficient. git output goes to stderr (1>&2); the final path is the only thing on stdout.
		string script =
			"branch='" + branch + "'\n" +
			"dir='" + dirName + "'\n" +
			"repo=$(basename -s .git \"$(git config --get remote.origin.url 2>/dev/null)\" 2>/dev/null)\n" +
			"if [ -z \"$repo\" ]; then repo=$(git rev-list --max-parents=0 HEAD 2>/dev/null | head -n1 | cut -c1-12); fi\n" +
			"if [ -z \"$repo\" ]; then repo=repo; fi\n" +
			"path=\"$HOME/.beast/worktrees/$repo/$dir\"\n" +
			"existing=$(git worktree list --porcelain | awk -v b=\"refs/heads/$branch\" '$1==\"worktree\"{p=$2} $1==\"branch\"&&$2==b{print p}')\n" +
			"if [ -n \"$existing\" ]; then echo \"$existing\"; exit 0; fi\n" +
			"if [ -d \"$path\" ]; then echo \"$path\"; exit 0; fi\n" +
			"mkdir -p \"$(dirname \"$path\")\"\n" +
			"if git show-ref --verify --quiet \"refs/heads/$branch\"; then\n" +
			"  git worktree add \"$path\" \"$branch\" 1>&2 || exit 1\n" +
			"else\n" +
			"  git worktree add \"$path\" -b \"$branch\" 1>&2 || exit 1\n" +
			"fi\n" +
			"echo \"$path\"\n";

		ToolResult result = await ShellTools.BashAsync("start_task_worktree", script, null, ct);
		if (result.ExitCode != 0)
		{
			string detail = result.StdErr;
			if (!string.IsNullOrEmpty(result.StdOut))
				detail = string.IsNullOrEmpty(detail) ? result.StdOut : detail + "\n" + result.StdOut;
			return (null, $"Failed to create git worktree for branch '{branch}':\n{detail}");
		}

		string path = result.StdOut.Trim();
		if (string.IsNullOrEmpty(path))
			return (null, $"Worktree creation produced no path for branch '{branch}'.");
		return (path, null);
	}

	// Branch names are restricted to a shell-safe charset so they can be embedded in the worktree script.
	private static bool IsValidBranchName(string branch)
	{
		if (string.IsNullOrWhiteSpace(branch))
			return false;
		foreach (char c in branch)
		{
			bool ok = char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '/';
			if (!ok)
				return false;
		}
		return true;
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
		// A resumed or switched-in session already carries its display name; announce it so the
		// client's session list and status show the name rather than the raw ID.
		session.AnnounceToClient();

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
					session.AnnounceToClient();
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
						Tool[] tools = await ToolsForTurnAsync(role, session.IsSubagent, _cancellationTokenSource.Token);
						_taskCompleteCalled = false;
						_pendingTaskObjective = null;

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
								if (hasToolCalls)
								{
									session.CommitToolResults(result.Payload!);
								}

								// Decide whether to keep going. User input always continues the turn. Otherwise
								// task_complete or start_task stops this turn (start_task then switches roles below);
								// a turn that ran other tools keeps going; a turn with no tool calls is reminded by
								// the role's end-of-turn prompt and keeps going, or — when the role defines no such
								// prompt — completes and idles.
								string? newUserInput = session.TryGetPendingInput();
								if (!string.IsNullOrEmpty(newUserInput))
								{
									session.Bundle.OnUserMessage(newUserInput);
									completed = false;
								}
								else if (_taskCompleteCalled || _pendingTaskObjective != null)
								{
									completed = true;
								}
								else if (!hasToolCalls)
								{
									if (!string.IsNullOrEmpty(role.EndOfTurnPrompt))
									{
										session.AddUserMessage(role.EndOfTurnPrompt);
										completed = false;
									}
									else
									{
										completed = true;
									}
								}
								else
								{
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

					// start_task switches the root into a fresh Task session seeded with the objective. The
					// loop restarts immediately on the new session (the top of the loop announces the switch).
					if (_pendingTaskObjective != null)
					{
						string objective = _pendingTaskObjective;
						_pendingTaskObjective = null;

						if (!session.Ephemeral)
							SaveRoot(session);

						session = CreateFreshSession("Task", session.Ephemeral);
						session.AddUserMessage(objective);
						_service = null;  // the Task role may use a different model
						_transport.Status(session.Id, "Started task.");
						continue;
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
		// The root is created nameless; the first user message gives it a name here. Announce the
		// instant it is assigned so the client switches from showing the raw ID to the display name.
		if (session.InferDisplayName())
			session.AnnounceToClient();
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
					else if (args != null && args.StartsWith("delete ", StringComparison.OrdinalIgnoreCase))
					{
						// Drop a subagent session file from disk. Refuse to delete the live root session.
						string deleteId = args.Substring("delete ".Length).Trim();
						if (string.Equals(deleteId, session.Id, StringComparison.Ordinal))
						{
							_transport.Error(session.Id, "Cannot delete the active session.");
						}
						else if (SessionService.Delete(deleteId))
						{
							_cachedSessions = SessionService.List();
							_transport.Status(session.Id, "Deleted session: " + deleteId);
						}
						else
						{
							_transport.Error(session.Id, "Session file not found: " + deleteId);
						}
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
					_subagentTool = ToolFactory.CreateSubagentTool(_roleService.SubagentRoles(), _subagent.RunSubagentAsync);
					_transport.Status(session.Id, "Config files reloaded.");
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
							// Reflect the new model on the client status line immediately rather than
							// waiting for the next turn's Stats frame.
							session.SendStats();
						}
					}
					break;
				case "help":
					_transport.Output(session.Id, "Commands: /compact, /clear, /reload, /model <id>, /session new, /session none, /session <id>, /session delete <id>, /test, /quit");
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

	// Returns all completable tokens: slash commands, model names, and session ids.
	private List<string> BuildCompletionCandidates(Session session)
	{
		List<string> candidates = new List<string>
		{
			"/compact", "/reload", "/model",
			"/session", "/help"
		};

		Role? activeRole = _roleService.GetRole(session.Role);
		LlmModel? activeModel = activeRole != null
			? _registry.GetModelForRole(activeRole, session.Model, session.ContextLength + _settings.Settings.CompactionReserveTokens)
			: null;

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

	// ---- Helpers ----

	// Returns the tools for this turn. role.BuiltTools holds the role's regular tools; the in-code special
	// tools are injected here for the root: start_task and subagent when the role declares them by name,
	// and task_complete for any Agent role kept on task by an end-of-turn prompt. Child agents
	// (isSubagent) get only their regular tools — SubagentRunner adds return_to_caller — so they cannot
	// spawn subagents or start tasks. start_task is built per call so its branch-argument description
	// carries the current git branch and worktrees.
	private async Task<Tool[]> ToolsForTurnAsync(Role role, bool isSubagent, CancellationToken ct)
	{
		if (isSubagent)
			return role.BuiltTools;

		List<Tool> tools = new List<Tool>(role.BuiltTools);
		if (role.Tools.Contains("start_task"))
		{
			string branchContext = await GitWorktreeContextAsync(ct);
			tools.Add(ToolFactory.CreateStartTaskTool(branchContext, StartTaskAsync));
		}
		if (role.Tools.Contains("subagent"))
			tools.Add(_subagentTool);
		if (role.Tools.Contains("fetch_url"))
			tools.Add(_fetchUrlTool);
		if (!string.IsNullOrEmpty(role.EndOfTurnPrompt))
			tools.Add(_taskCompleteTool);
		return tools.ToArray();
	}

	// Current branch and existing worktrees, for the start_task branch-argument description so the model
	// picks a name that does not collide. Best-effort: any stderr is appended; failure yields a notice.
	private static async Task<string> GitWorktreeContextAsync(CancellationToken ct)
	{
		string command = "echo \"Current branch: $(git rev-parse --abbrev-ref HEAD)\"; echo \"Existing worktrees:\"; git worktree list";
		ToolResult result = await ShellTools.BashAsync("git_worktree_context", command, null, ct);

		string text = result.StdOut;
		if (!string.IsNullOrEmpty(result.StdErr))
			text = string.IsNullOrEmpty(text) ? result.StdErr : text + "\n" + result.StdErr;
		return string.IsNullOrEmpty(text) ? "(git worktree information unavailable)" : text;
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
