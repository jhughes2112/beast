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

	// Runs child sub-sessions on behalf of the root's delegation tool.
	private readonly SubagentRunner _subagent;

	// The Default agent's delegation tool: hands a unit of work to the Developer subagent. Appended to the
	// root's bound tools each turn; child agents never receive it, so nesting stops at one level. It targets
	// one fixed role, so unlike the old generic subagent tool it needs no rebuild on /reload. Calling it puts
	// the session in the work loop (BeginWork).
	private readonly Tool _assignWorkTool;

	// The counterpart to assign_work: exposed only while the session is in the work loop, it calls EndWork to
	// leave it. Stateless like the others, built once.
	private readonly Tool _stopWorkTool;

	// fetch_url: fetches a page and filters it through the WebFetch role. Injected for roles that declare it.
	private readonly Tool _fetchUrlTool;

	// search_web: searches the web with the OpenRouter search model and returns its answer verbatim. Null when
	// web search is not configured/enabled. Injected for roles that declare it.
	private readonly Tool? _searchWebTool;

	// read_file: a plain, raw file reader. Stateless, so a single instance is reused for every turn.
	private readonly Tool _readFileTool;

	// find_relevant_file_sections: digests a file through the Explorer role, returning a goal-focused concept map. The root
	// owns its own summarizer; each subagent run makes its own, so an agent's exploration never depends on
	// another's reads.
	private readonly Tool _summarizeFileTool;

	private bool _wantsCompact = false;

	// True for the runner that resumes a saved root at startup: it restores that root's saved child sessions
	// into the client's session list on first run. Consumed once, then cleared, so a compaction-failure
	// restart on the same runner (or a fresh runner with it false) never replays the children twice.
	private bool _restoreChildren;

	private List<(string id, string displayName, int messageCount)> _cachedSessions = new List<(string, string, int)>();

	public SessionRunner(
		Session session,
		LlmRegistry registry,
		RoleService roleService,
		SettingsService settings,
		ITransportServer transport,
		CancellationTokenSource cancellationTokenSource,
		bool restoreChildren)
	{
		_restoreChildren = restoreChildren;
		_registry = registry;
		_roleService = roleService;
		_settings = settings;
		_transport = transport;
		_cancellationTokenSource = cancellationTokenSource;
		_currentSession = session;
		_subagent = new SubagentRunner(registry, roleService, transport, () => this.CurrentSession, settings.Settings.WebSearch);
		_assignWorkTool = ToolFactory.CreateAssignWorkTool((prompt, budget, workCt) => _subagent.RunSubagentAsync("Developer", prompt, budget, workCt), () => CurrentSession.BeginWork());
		_stopWorkTool = ToolFactory.CreateStopWorkTool(() => CurrentSession.EndWork());
		_fetchUrlTool = ToolFactory.CreateFetchUrlTool(_registry, _roleService, () => this.CurrentSession);
		_searchWebTool = ToolFactory.CreateSearchWebTool(_settings.Settings.WebSearch, _roleService, () => this.CurrentSession);
		_readFileTool = ToolFactory.CreateReadFileTool();
		_summarizeFileTool = ToolFactory.CreateSummarizeFileTool(new FileSummarizer(), _registry, _roleService, () => this.CurrentSession);
	}

	// /finish: if the worktree's work is fully folded into the base branch and the tree is clean, detach the
	// worktree, delete its (merged) branch, and signal Beast to tear down and remove the host folder. Beast
	// drives the actual shutdown via its graceful /quit path, so the finish signal is delivered before the
	// socket closes. Otherwise report what is still pending and do nothing, so the user can finish or reset
	// the work first.
	private async Task FinishAsync(Session session, CancellationToken ct)
	{
		if (!Directory.Exists("/git"))
		{
			_transport.Error(session.Id, "/finish is only available inside a Beast worktree container.");
			return;
		}

		// Pending if the worktree has uncommitted changes, or feature commits not yet contained in base.
		string checkScript =
			"feat=$(git -C /workspace rev-parse --abbrev-ref HEAD)\n" +
			"base=$(git -C /git rev-parse --abbrev-ref HEAD)\n" +
			"dirty=$(git -C /workspace status --porcelain)\n" +
			"if [ -n \"$dirty\" ]; then echo PENDING; echo \"Uncommitted changes in the worktree:\"; echo \"$dirty\"; exit 0; fi\n" +
			"if ! git -C /git merge-base --is-ancestor \"$feat\" \"$base\"; then\n" +
			"  n=$(git -C /git rev-list --count \"$base\"..\"$feat\" 2>/dev/null)\n" +
			"  echo PENDING; echo \"$n commit(s) on '$feat' are not yet integrated into '$base'.\"; exit 0\n" +
			"fi\n" +
			"echo OK; echo \"$feat\"; echo \"$base\"\n";

		ToolResult check = await ShellTools.BashAsync("finish_check", checkScript, null, ct);
		string[] lines = check.StdOut.Trim().Length == 0 ? Array.Empty<string>() : check.StdOut.Trim().Split('\n');
		string verdict = lines.Length > 0 ? lines[0].Trim() : string.Empty;

		if (verdict != "OK")
		{
			string detail = lines.Length > 1 ? string.Join("\n", lines, 1, lines.Length - 1) : "Could not determine worktree status.";
			_transport.Output(session.Id,
				"Cannot finish yet — the worktree is not fully integrated:\n" + detail +
				"\n\nTell the agent that its task is to finish the work (review and approve the changes) or to reset the branch, then run /finish again.");
			return;
		}

		string baseBranch = lines.Length > 2 ? lines[2].Trim() : string.Empty;

		// The session files live under the worktree's .beast/sessions, so before folding the worktree down
		// delete this session and every child it spawned — they belong to the task that is now finishing and
		// would otherwise be orphaned when the worktree folder goes away.
		SessionService.DeleteTree(session.Id);

		// Detach the worktree and delete its merged branch. feat is re-derived in-script (never interpolated)
		// and cd /git runs first so removing /workspace does not pull the shell's CWD out from under it.
		// Success is judged by the registration being gone, NOT by exit code: /workspace is a bind mount, so
		// `git worktree remove` deletes the checkout but cannot rmdir the mount point — it exits non-zero
		// while still detaching the worktree. Beast removes the leftover (empty) host folder afterward.
		string removeScript =
			"cd /git\n" +
			"feat=$(git -C /workspace rev-parse --abbrev-ref HEAD)\n" +
			"git worktree remove --force /workspace >/dev/null 2>&1 || true\n" +
			"git worktree prune >/dev/null 2>&1 || true\n" +
			"if git worktree list --porcelain | grep -qx 'worktree /workspace'; then echo 'ERROR: worktree still registered at /workspace.'; exit 1; fi\n" +
			"git branch -d \"$feat\" >/dev/null 2>&1 || true\n" +
			"echo REMOVED\n";

		ToolResult remove = await ShellTools.BashAsync("finish_remove", removeScript, null, ct);
		if (!remove.StdOut.Contains("REMOVED"))
		{
			string detail = remove.StdErr;
			if (!string.IsNullOrEmpty(remove.StdOut))
				detail = string.IsNullOrEmpty(detail) ? remove.StdOut : detail + "\n" + remove.StdOut;
			_transport.Error(session.Id, "Failed to remove the worktree:\n" + (string.IsNullOrWhiteSpace(detail) ? "(no output)" : detail));
			return;
		}

		// The CWD (/workspace) is gone now; move to /git so the process keeps a valid working directory for
		// the brief remainder of its life before Beast's /quit arrives.
		try
		{ Directory.SetCurrentDirectory("/git"); }
		catch { }

		_transport.Output(session.Id, $"Worktree finished and integrated into '{baseBranch}'. Removing it and shutting down.");
		_transport.Status(session.Id, "worktree-finished");
	}

	// Loads a resumed root's saved child sessions (subagent tool sessions, compaction/role successors) from
	// disk and replays each to the client so the F10 tree lists them and they can be viewed. These sessions
	// are not re-run — they are inert history — so the constructed Session objects are used only to announce
	// and replay, then discarded. Called before the root's own replay so the root, replayed last, lands as
	// the active view (the client follows the most recently replayed content).
	private void RestoreChildSessions(Session root)
	{
		foreach (string childId in SessionService.ListDescendants(root.Id))
		{
			BeastSession? data = SessionService.Load(childId);
			if (data == null)
				continue;

			Session child = new Session(data, string.Empty, _transport, true);
			child.AnnounceToClient();
			child.SendStats();
			child.ReplayToTransport();
		}
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

		// On resume, surface this root's saved child sessions (subagent tool sessions, compaction/role
		// successors) FIRST so the F10 tree shows them, then replay the root below. The client follows the
		// most recently replayed content, so the root — replayed last — lands as the active view rather than
		// a child. (Replaying the root first instead leaves the view stuck on the last child restored.)
		if (_restoreChildren)
		{
			_restoreChildren = false;
			RestoreChildSessions(session);
		}

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
					// Send history to the client whenever the active session changes. Surface the switched-in
					// root's saved children first (no-op for a fresh/ephemeral session, which has no child
					// files yet), then replay the root last so the client's active view lands on the root,
					// not a child — the client follows the most recently replayed content.
					RestoreChildSessions(session);
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
					// Name and announce the root before the turn runs so the client's session tree shows
					// the inferred name immediately, not the raw ID until the turn completes and saves.
					session.EnsureNamedAndAnnounce();
					session.SendBusy();
					try
					{
						bool workToolsActive = session.WorkInProgress;
						Tool[] tools = await ToolsForTurnAsync(role, session.IsSubagent, workToolsActive, _cancellationTokenSource.Token);

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

								// Pick up steering input committed during this round. Leading plain text is injected
								// and the turn continues; a queued slash command ends the turn here so the boundary
								// drain applies it (and any input after it) with add-user-message timing. Otherwise a
								// turn that ran tools keeps going; a turn with no tool calls is reminded by the
								// role's end-of-turn prompt and keeps going, or — when the role defines no such
								// prompt — completes and idles.
								string? newUserInput = session.TryDequeueLeadingText();
								if (!string.IsNullOrEmpty(newUserInput))
									session.Bundle.OnUserMessage(newUserInput);

								if (session.HasPending)
								{
									// Head of the queue is a command — yield the turn so DrainInputAsync applies it.
									completed = true;
								}
								else if (!string.IsNullOrEmpty(newUserInput))
								{
									completed = false;
								}
								else if (!hasToolCalls)
								{
									if (session.WorkInProgress && !string.IsNullOrEmpty(role.EndOfTurnPrompt))
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

								// assign_work and stop_work flip the work flag mid-loop. Rebuild the tool set on a
								// flip so stop_work appears once work is delegated and is gone once it stops — only on
								// a change, to avoid churning the tool list (and its prompt cache) needlessly.
								if (!completed && session.WorkInProgress != workToolsActive)
								{
									workToolsActive = session.WorkInProgress;
									tools = await ToolsForTurnAsync(role, session.IsSubagent, workToolsActive, _cancellationTokenSource.Token);
								}
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

		// Carry the delegation loop across compaction: a long-running work loop can fill context and compact
		// mid-flight, and it should keep delegating (and keep exposing stop_work) on the compacted successor.
		if (_currentSession.WorkInProgress)
			newSession.BeginWork();

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

	// Drains all queued input from the session in arrival order: plain steering text is injected
	// straight into the bundle, slash commands are dispatched. Both are pulled from the one pending
	// queue so a "text, /command, text" sequence is applied in exactly the order it was typed.
	private async Task<Session> DrainInputAsync(Session session)
	{
		while (session.TryDequeuePending(out string? line))
		{
			if (!line!.StartsWith("/", StringComparison.Ordinal))
			{
				session.Bundle.OnUserMessage(line);
				continue;
			}

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
					_cancellationTokenSource.Cancel();
					break;
				case "finish":
					await FinishAsync(session, _cancellationTokenSource.Token);
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
						// Drop exactly one subagent session file from disk. Refuse to delete the live root
						// session, and refuse an empty target so a blank id can never reach the disk layer.
						string deleteId = args.Substring("delete ".Length).Trim();
						if (string.IsNullOrEmpty(deleteId))
						{
							_transport.Error(session.Id, "No session specified to delete.");
						}
						else if (string.Equals(deleteId, session.Id, StringComparison.Ordinal))
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
					try
					{
						_roleService.Reload();
						_settings.LoadSettings();
						_registry.LoadFromConfigs(_settings, _roleService);
						await _registry.ProbeEndpointsAsync(_cancellationTokenSource.Token);
						_registry.ResetAllAvailability();
						_service = null;
						_transport.Status(session.Id, "Config files reloaded.");
					}
					catch (ConfigException ex)
					{
						// Surface the file, line/column and parser message so the user can fix it without
						// digging through the agent log. The previous config stays in effect.
						_transport.Error(session.Id, $"Reload failed: {ex.Message}. Keeping the previous config.");
					}
					break;
				case "model":
					if (args != null)
					{
						// Completions append a pricing annotation after the id; keep only the id token.
						int modelArgSpace = args.IndexOf(' ');
						string modelArg = modelArgSpace >= 0 ? args.Substring(0, modelArgSpace) : args;

						Role? modelRole = _roleService.GetRole(session.Role);
						LlmModel? targetModel = modelRole != null ? _registry.GetModelForRole(modelRole, modelArg, 0) : null;
						if (targetModel == null)
							_transport.Error(session.Id, $"Unknown model: {modelArg}");
						else
						{
							session.UpdateModel(targetModel);
							_registry.ResetAvailability(modelArg);
							_service = null;  // force fresh service with new model next turn
							_transport.Status(session.Id, $"Model set to {modelArg}");
							// Reflect the new model on the client status line immediately rather than
							// waiting for the next turn's Stats frame.
							session.SendStats();
						}
					}
					break;
				case "help":
					_transport.Output(session.Id, "Commands: /compact, /clear, /reload, /model <id>, /session new, /session none, /session <id>, /session delete <id>, /finish, /test, /quit");
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
			"/session", "/finish", "/help"
		};

		Role? activeRole = _roleService.GetRole(session.Role);
		LlmModel? activeModel = activeRole != null
			? _registry.GetModelForRole(activeRole, session.Model, session.ContextLength + _settings.Settings.CompactionReserveTokens)
			: null;

		if (activeRole != null)
		{
			string currentModelId = activeModel != null ? activeModel.ConfigId : session.Model + " (not available)";
			List<string> enabledModels = _registry.GetEnabledModelsForRole(activeRole);
			// Current model first, then the rest, each annotated with its per-Mtok pricing.
			// The trailing pricing is display-only: the /model handler trims everything after the id.
			if (!string.IsNullOrEmpty(currentModelId) && enabledModels.Contains(currentModelId))
				candidates.Add("/model " + currentModelId + ModelPricingLabel(currentModelId));
			foreach (string modelId in enabledModels)
			{
				if (modelId == currentModelId)
					continue;
				candidates.Add("/model " + modelId + ModelPricingLabel(modelId));
			}
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

	// Builds the trailing pricing annotation shown after a model id in /model completions, e.g.
	// "  in:$3.00 out:$15.00 /Mtok". Display-only — the /model handler trims it before resolving.
	private string ModelPricingLabel(string modelId)
	{
		LlmModel? model = _registry.GetModel(modelId);
		if (model == null)
			return string.Empty;

		CostConfig cost = model.Config.Cost;
		return $"  in:${cost.Input:0.00} out:${cost.Output:0.00} /Mtok";
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
		await WebToolsTests.TestAsync(ctx, _settings.Settings.WebSearch, _roleService, _transport, _currentSession, _cancellationTokenSource.Token);
		await PerModelLlmTests.TestAsync(ctx, _registry, _roleService, _settings, _cancellationTokenSource.Token);
		ProtocolSwitchTests.Test(ctx);

		_transport.Output(sessionId, $"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
	}

	// ---- Helpers ----

	// Returns the tools for this turn. role.BuiltTools holds the role's regular tools; the in-code special
	// tools are injected here for the root: read_file, find_relevant_file_sections, assign_work, fetch_url, and search_web when
	// the role declares them by name. Child agents (isSubagent) get only their regular tools — SubagentRunner adds
	// the terminator and the Developer's review_work / commit_and_rebase — so they cannot spawn arbitrary subagents.
	private Task<Tool[]> ToolsForTurnAsync(Role role, bool isSubagent, bool workInProgress, CancellationToken ct)
	{
		if (isSubagent)
			return Task.FromResult(role.BuiltTools);

		List<Tool> tools = new List<Tool>(role.BuiltTools);
		if (role.Tools.Contains("read_file"))
			tools.Add(_readFileTool);
		if (role.Tools.Contains("find_relevant_file_sections"))
			tools.Add(_summarizeFileTool);
		if (role.Tools.Contains("assign_work"))
			tools.Add(_assignWorkTool);
		if (role.Tools.Contains("fetch_url"))
			tools.Add(_fetchUrlTool);
		if (role.Tools.Contains("search_web") && _searchWebTool != null)
			tools.Add(_searchWebTool);

		// stop_work exists only inside the delegation loop: assign_work sets the flag, this exposes the way
		// out. Paired with assign_work, so a role that cannot delegate never sees it.
		if (workInProgress)
			tools.Add(_stopWorkTool);

		return Task.FromResult(tools.ToArray());
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
