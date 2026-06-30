using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

	private bool _wantsCompact = false;

	// Set when the active root is deleted: the outgoing session's files are gone, so the session-switch in
	// RunAsync must NOT re-save (resurrect) it. Consumed and cleared by that switch.
	private bool _currentSessionDeleted = false;

	// True for the runner that resumes a saved root at startup: it restores ALL saved sessions in the worktree
	// into the client's session list on first run. Consumed once, then cleared, so a compaction-failure
	// restart on the same runner (or a fresh runner with it false) never replays the sessions twice.
	private bool _restoreChildren;


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
	}

	// Bash script to check whether the worktree's feature branch is fully integrated into the base branch
	// and has no uncommitted changes. Reports PENDING with details or OK with both branch names.
	private const string FinishCheckScript =
		"feat=$(git -C /workspace rev-parse --abbrev-ref HEAD)\n" +
		"base=$(git -C /git rev-parse --abbrev-ref HEAD)\n" +
		"dirty=$(git -C /workspace status --porcelain)\n" +
		"if [ -n \"$dirty\" ]; then echo PENDING; echo \"Uncommitted changes in the worktree:\"; echo \"$dirty\"; exit 0; fi\n" +
		"if ! git -C /git merge-base --is-ancestor \"$feat\" \"$base\"; then\n" +
		"  n=$(git -C /git rev-list --count \"$base\"..\"$feat\" 2>/dev/null)\n" +
		"  echo PENDING; echo \"$n commit(s) on '$feat' are not yet integrated into '$base'.\"; exit 0\n" +
		"fi\n" +
		"echo OK; echo \"$feat\"; echo \"$base\"\n";

	// Bash script to detach the worktree, prune its registration, and delete its merged branch. feat is
	// re-derived in-script (never interpolated). cd /git runs first so removing /workspace does not pull
	// the shell's CWD out from under it. Success is judged by the registration being gone, NOT by exit
	// code: /workspace is a bind mount, so `git worktree remove` deletes the checkout but cannot rmdir the
	// mount point — it exits non-zero while still detaching the worktree. Beast removes the leftover
	// (empty) host folder afterward.
	private const string FinishRemoveScript =
		"cd /git\n" +
		"feat=$(git -C /workspace rev-parse --abbrev-ref HEAD)\n" +
		"git worktree remove --force /workspace >/dev/null 2>&1 || true\n" +
		"git worktree prune >/dev/null 2>&1 || true\n" +
		"if git worktree list --porcelain | grep -qx 'worktree /workspace'; then echo 'ERROR: worktree still registered at /workspace.'; exit 1; fi\n" +
		"git branch -d \"$feat\" >/dev/null 2>&1 || true\n" +
		"echo REMOVED\n";

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
		ToolResult check = await ShellTools.BashAsync("finish_check", FinishCheckScript, null, ct);
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

		ToolResult remove = await ShellTools.BashAsync("finish_remove", FinishRemoveScript, null, ct);
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

	// Loads ALL saved sessions from the worktree and replays each to the client so the F10 menu shows the complete tree.
	// Roots are sorted by CreationOrder descending (newest first = highest CreationOrder first). 
	// Children are sorted under their parent by CreationOrder descending (newest first = highest child number first).
	// The root session (the one being resumed) is replayed LAST so it becomes the active view (the client follows the most recently replayed content).
	// Called only on the first run of the runner that resumes a saved root (_restoreChildren == true).
	private void RestoreAllSessions(Session root)
	{
		List<SessionService.SessionFileInfo> allSessions = SessionService.LoadAll();
		if (allSessions.Count == 0)
			return;

		// Build parent -> children map
		Dictionary<string, List<SessionService.SessionFileInfo>> childrenMap = new Dictionary<string, List<SessionService.SessionFileInfo>>(StringComparer.Ordinal);
		HashSet<string> hasParent = new HashSet<string>(StringComparer.Ordinal);

		foreach (var info in allSessions)
		{
			string? parentId = GetParentId(info.Session.Id);
			if (parentId != null && allSessions.Any(s => s.Session.Id == parentId))
			{
				if (!childrenMap.TryGetValue(parentId, out var kids))
				{
					kids = new List<SessionService.SessionFileInfo>();
					childrenMap[parentId] = kids;
				}
				kids.Add(info);
				hasParent.Add(info.Session.Id);
			}
		}

		// Sort children by CreationOrder descending (newest first = highest CreationOrder first)
		foreach (var kvp in childrenMap)
		{
			kvp.Value.Sort((a, b) => b.CreationOrder.CompareTo(a.CreationOrder));
		}

		// Roots: sessions with no parent (or parent not in the loaded set)
		List<SessionService.SessionFileInfo> roots = allSessions.Where(s => !hasParent.Contains(s.Session.Id)).ToList();

		// Sort roots by CreationOrder descending (newest first = highest CreationOrder first)
		roots.Sort((a, b) => b.CreationOrder.CompareTo(a.CreationOrder));

		// Find the root being resumed (the one matching our current session's ID)
		SessionService.SessionFileInfo? resumedRoot = roots.FirstOrDefault(r => r.Session.Id == root.Id);

		// Remove it from roots list so we can replay it LAST
		if (resumedRoot != null)
			roots.Remove(resumedRoot);

		// DFS replay: roots first (sorted by CreationOrder desc), children sorted by CreationOrder desc
		// The resumed root is replayed LAST so it becomes the active view
		List<SessionService.SessionFileInfo> ordered = new List<SessionService.SessionFileInfo>();
		foreach (var rootInfo in roots)
			DfsAddToOrdered(rootInfo, childrenMap, ordered);

		if (resumedRoot != null)
			DfsAddToOrdered(resumedRoot, childrenMap, ordered);

		foreach (var info in ordered)
		{
			Session session = new Session(info.Session, string.Empty, _transport, true);
			session.AnnounceToClient();
			session.SendStats();
			session.ReplayToTransport();
		}
	}

	// DFS helper to add sessions to ordered list (pre-order: parent before children)
	private void DfsAddToOrdered(SessionService.SessionFileInfo root, Dictionary<string, List<SessionService.SessionFileInfo>> childrenMap, List<SessionService.SessionFileInfo> ordered)
	{
		ordered.Add(root);
		if (childrenMap.TryGetValue(root.Session.Id, out var children))
		{
			foreach (var child in children)
				DfsAddToOrdered(child, childrenMap, ordered);
		}
	}

	// Returns the parent session ID for a given session ID, or null if it is a root.
	// Parent-child relationship is encoded as "parentId_N" where N is a positive integer.
	private static string? GetParentId(string id)
	{
		int last = id.LastIndexOf('_');
		if (last < 0)
			return null;
		string suffix = id.Substring(last + 1);
		if (!int.TryParse(suffix, out _))
			return null;
		return id.Substring(0, last);
	}

	// Extract child number from session ID (e.g., "parentId_5" -> 5)
	private static int ExtractChildNumber(string sessionId)
	{
		int lastUnderscore = sessionId.LastIndexOf('_');
		if (lastUnderscore < 0 || lastUnderscore == sessionId.Length - 1)
			return 0;
		if (int.TryParse(sessionId.Substring(lastUnderscore + 1), out int num))
			return num;
		return 0;
	}

	// Loads a resumed root's saved child sessions (subagent tool sessions, compaction/role successors) from
	// disk and replays each to the client so the F10 tree lists them and they can be viewed. These sessions
	// are not re-run — they are inert history — so the constructed Session objects are used only to announce
	// and replay, then discarded. Called before the root's own replay so the root, replayed last, lands as
	// the active view (the client follows the most recently replayed content).
	// KEPT for session switching during runtime (when user switches to a different root via F10).
	private void RestoreChildSessions(Session root)
	{
		foreach (var (id, displayName, messageCount, parentId) in SessionService.ListAll(root.Id))
		{
			if (string.Equals(id, root.Id, StringComparison.Ordinal))
				continue; // skip the root itself

			BeastSession? data = SessionService.Load(id);
			if (data == null)
				continue;

			Session child = new Session(data, string.Empty, _transport, true);
			child.AnnounceToClient();
			child.SendStats();
			child.ReplayToTransport();
		}
	}

	// Routes inbound input to the session tree by ID. Global commands (/reload, /finish, /help,
	// /delete-session, /quit) act on the whole runner rather than a single session, so they must run no
	// matter which session the client addressed them to — route them to the active root's pending queue,
	// which is drained only by this runner (DrainInputAsync). Everything else — plain steering text and
	// per-session slash commands (/model, /compact, /test, /clear, /cancel, …) — is delivered to the
	// addressed session so it processes in that session's normal turn-boundary flow. /cancel in particular
	// stays per-session: Session.Deliver interrupts the addressed (sub)session immediately.
	private static readonly HashSet<string> GlobalCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"reload", "finish", "help", "delete-session", "quit"
	};

	private static bool IsGlobalCommand(string text)
	{
		// Caller guarantees text starts with '/'.
		string trimmed = text.Substring(1).Trim();
		int spaceIdx = trimmed.IndexOf(' ');
		string verb = spaceIdx >= 0 ? trimmed.Substring(0, spaceIdx) : trimmed;
		return GlobalCommands.Contains(verb);
	}

	public void Deliver(string targetId, string text)
	{
		if (text.StartsWith("/", StringComparison.Ordinal) && IsGlobalCommand(text))
		{
			_currentSession.Deliver(_currentSession.Id, text);
			return;
		}
		_currentSession.Deliver(targetId, text);
	}

	public string ActiveSessionId => _currentSession.Id;
	public Session CurrentSession => _currentSession;

	// Runs until cancelled. Compaction is handled inline: when context fills the current session
	// is summarized, a new independent root session is created and announced, and the loop continues on it.
	public async Task<Session> RunAsync(CancellationToken ct)
	{
		Session session = _currentSession;

		// On resume (first run with _restoreChildren == true), load and replay ALL sessions in the worktree.
		// Roots first (sorted by mod time, newest first), then children under parents (sorted by numeric suffix, newest first).
		// The root being resumed is replayed LAST so it becomes the active view (client follows most recently replayed).
		if (_restoreChildren)
		{
			_restoreChildren = false;
			RestoreAllSessions(session);
		}

		session.ReplayToTransport();
		session.SendStats();
		// A resumed or switched-in session already carries its display name; announce it so the
		// client's session list and status show the name rather than the raw ID.
		session.AnnounceToClient();

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
					// Persist the session we are leaving — unless it was just deleted, in which case saving
					// would re-create the file we removed.
					if (!_currentSessionDeleted)
						SaveRoot(_currentSession);
					_currentSessionDeleted = false;
					_currentSession = session;
				}

				// 2. Resolve role and refresh service after drain (commands may have changed role or model).
				Role? role = _roleService.GetRole(session.Role);
				_service = RefreshService(role, session);

				if (_service != null)
				{
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

					// One cancellation scope for the whole turn — both the LLM calls and the tool-dispatch
					// rounds between them run on this token, and it is registered with the session so a
					// /cancel interrupts a running tool, not just a streaming LLM call.
					using CancellationTokenSource turnScope = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
					session.SetDispatchScope(turnScope);
					try
					{
						bool workToolsActive = session.WorkInProgress;
						Tool[] tools = ToolsForTurn(role, workToolsActive);

						// Tool dispatch loop: keep calling LLM until it returns without tool calls or fails.
						bool completed = false;
						while (!completed)
						{
							// Tracer call: before the real request, probe the provider with max_output_tokens=0
							// to get accurate token counts and detect context overflow early.
							int compactionReserve = GetCompactionReserve(session);
							int compactionThreshold = session.ContextWindow - compactionReserve;
							int lastContextSize = session.ContextLength;

							// Pessimistic estimate: last known context + rough estimate of new content growth.
							// New content bytes are estimated from pending messages not yet reflected in context length.
							// We use bytes / 3 as a conservative token estimate (UTF-8 avg ~3 bytes per token).
							// Pending user messages are in session.Bundle.Canonical.Messages but not yet committed.
							// Also include any pending tool response reservations from the ContextBudget.
							int pendingBytes = 0;
							foreach (var msg in session.Bundle.Canonical.Messages)
							{
								if (msg is UserMessage um)
									pendingBytes += System.Text.Encoding.UTF8.GetByteCount(um.Text);
							}
							int pessimisticEstimate = lastContextSize + (pendingBytes / 3) + session.Budget.PendingReserve;

							// Trigger tracer if pessimistic estimate is near or above the compaction threshold.
							// This gives us an accurate measurement before the real call.
							bool shouldTracer = pessimisticEstimate >= compactionThreshold;

							if (shouldTracer)
							{
								TracerResult tracer = await _service.RunTracerAsync(session, tools, null, turnScope.Token);

								if (tracer.Succeeded)
								{
									// Update the budget measurement with the accurate count from the tracer
									session.Budget.RecordMeasurement(tracer.InputTokens);

									// Send accurate token counts to client for status line update:
									// status line should show {tokens}%/{model_context} using accurate counts
									// from the tracer (or last real call).
									int cachedTokens = tracer.CachedTokens;
									_transport.Stats(session.Id, session.Model + ReasoningEffort.DisplaySuffix(_service.Model.Config.ReasoningEffort), session.Role,
										session.CumulativeInputTokens, session.CumulativeOutputTokens,
										session.TotalCost, session.ContextWindow, tracer.InputTokens, cachedTokens);

									// Decide: if input_tokens + cached_tokens >= threshold → compact; else proceed
									if (tracer.InputTokens + tracer.CachedTokens >= compactionThreshold)
									{
										_transport.Status(session.Id, $"Context full ({tracer.InputTokens + tracer.CachedTokens}/{session.ContextWindow}), compacting...");
										contextFull = true;
										completed = true;
										continue;
									}
								}
								else if (tracer.ContextBlown)
								{
									// 4xx error from tracer = context blown past limit, compaction mandatory
									_transport.Status(session.Id, $"Context exceeds limit ({tracer.ErrorMessage}), compacting...");
									contextFull = true;
									completed = true;
									continue;
								}
								// If tracer failed for other reasons (rate limit, network), proceed with real call
								// and let the normal retry/fallback logic handle it
							}

							ProtocolResult result = await _service.RunToCompletionAsync(session, tools, null, compactionReserve, 0, _transport,
turnScope.Token);

							if (result.Outcome == ProtocolCallOutcome.Success)
							{
								// Commit the assistant turn to canonical and protocol state before processing.  This may include thinking, output, and tool call requests all in one message.
								session.CommitAssistantTurn(result.Payload!);

								// Process the assistant response and dispatch any tool calls.
								bool hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, tools, session, _transport, turnScope.Token);
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
									tools = ToolsForTurn(role, workToolsActive);
								}
							}
							else if (result.Outcome == ProtocolCallOutcome.ContextFull)
							{
								// Context budget exhausted before making the call. Caller handles compaction.
								contextFull = true;
								completed = true;
							}
							else if (result.Outcome == ProtocolCallOutcome.Interrupted)
							{
								// User cancelled the turn — do not surface as error.
								completed = true;
							}
							else
							{
								// Any model failure — sustained rate limiting (TooManyRetries), a permanent failure
								// (Failed: auth/unknown protocol, which already marked the model down), or any other
								// non-success — falls back to the next model in the role's priority list, exactly like the
								// user typing /model <next>, and keeps the turn going. Tools are role-based, so the existing
								// tool set still applies. Only when the list is exhausted do we surface the error and end
								// the turn so the user can intervene.
								int minCtx = session.ContextLength + GetCompactionReserve(session);
								LlmService? fallback = _registry.CreateFallbackService(_service, minCtx);
								if (fallback != null)
								{
									string reason = result.Outcome == ProtocolCallOutcome.TooManyRetries ? "Rate limited after retries" : "Model failed";
									session.QueryLog.FallbackTransition(_service, fallback, reason, result.Outcome == ProtocolCallOutcome.TooManyRetries ? 10 : 5); // approximate
									_service = fallback;
									session.UpdateModel(fallback.Model);
									session.SendStats();
									_transport.Status(session.Id, $"{reason}; falling back to {fallback.Model.Config.Name}");
									// completed stays false so the loop retries this turn on the fallback model.
								}
								else
								{
									string detail = result.Outcome == ProtocolCallOutcome.TooManyRetries
										? "Rate limited after too many retries, and no fallback model is available."
										: (string.IsNullOrEmpty(result.ErrorMessage) ? "Model failed and no fallback model is available." : result.ErrorMessage);
									session.QueryLog.SessionFailure(session, _service, detail, _service.RoleModelIds.Count);
									_transport.Error(session.Id, detail);
									completed = true;
								}
							}
						}
						// Interrupted: _interruptedAndWaiting is set in EndTurn;
						// NeedsAttention() returns false until AddUserMessage() is called with new input.
					}
					catch (OperationCanceledException) when (turnScope.IsCancellationRequested && !_cancellationTokenSource.IsCancellationRequested)
					{
						// A /cancel landed while a tool was running between LLM calls — there was no active LLM
						// call to turn it into an Interrupted result, so mark the wait state here. The dangling
						// tool calls left by the aborted round are repaired on the next BeginTurn. Logged so a
						// turn that ends here is visible server-side rather than vanishing silently.
						Console.Error.WriteLine($"[SessionRunner] Session {session.Id} turn interrupted between tool calls (turn scope cancelled).");
						session.MarkInterrupted();
					}
					finally
					{
						session.SetDispatchScope(null);
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
			// A cancel that escaped the per-turn handling unwinds the whole run loop. When the root token
			// is down this is a normal shutdown; otherwise it is unexpected (the loop will be restarted on
			// the same session by the orchestrator), so log it server-side rather than exiting silently.
			if (!_cancellationTokenSource.IsCancellationRequested)
				Console.Error.WriteLine($"[SessionRunner] Run loop for session {_currentSession.Id} exited on an OperationCanceledException without a root cancel; restarting on the same session.");
		}
		finally
		{
			if (!_currentSession.Ephemeral)
				SaveRoot(_currentSession);
		}
		return _currentSession;
	}

	// Summarizes the current session and returns a new ROOT session seeded with the compacted content.
	// The new session is independent — not a child of the old one — so the client keeps all its existing
	// sessions. The old session stays in history; the new one starts fresh with just the summary.
	// Returns null if the service, role, or summary prompt is unavailable.
	private async Task<Session?> CompactAsync(CancellationToken ct)
	{
		Role? role = _roleService.GetRole(_currentSession.Role);

		if (role == null || string.IsNullOrEmpty(role.SummaryPrompt))
		{
			_transport.Status(_currentSession.Id, "[Compaction] No role or summary prompt available.");
			return null;
		}

		_transport.Status(_currentSession.Id, "[Compaction] Started.");
		string? summary = await Summarizer.SummarizeAsync(_currentSession, role.SummaryPrompt, Array.Empty<Tool>(), _registry, _roleService, _transport, ct);

		if (string.IsNullOrWhiteSpace(summary))
		{
			_transport.Status(_currentSession.Id, "[Compaction] Failed.");
			return null;
		}

		string newDisplayName = Session.IncrementDisplayName(_currentSession.DisplayName);
		// Generate a brand new root session ID (GUID) — not a child ID — so the compacted session
		// is an independent root, not a child of the old one. The client keeps all its sessions.
		string newSessionId = Guid.NewGuid().ToString();

		SaveRoot(_currentSession);

		// Compaction starts a clean-slate session (no history) seeded with the summary plus the last
		// couple of exchanges — not a copy of the prior conversation.
		BeastSession freshData = new BeastSession(newSessionId, newDisplayName, _currentSession.Model, _currentSession.Role, new List<CanonicalMessage>(), null, 0m,
0, 0, 0, _currentSession.Ephemeral);

		Session newSession = new Session(freshData, role.SystemPrompt, _transport, false);

		// Carry the delegation loop across compaction: a long-running work loop can fill context and compact
		// mid-flight, and it should keep delegating (and keep exposing stop_work) on the compacted successor.
		if (_currentSession.WorkInProgress)
			newSession.BeginWork();

		newSession.AddUserMessage(summary);
		newSession.FlushPendingMessages();

		// Announce the new root session to the client (adds it to the session list; does NOT wipe existing sessions).
		newSession.AnnounceToClient();

		if (!newSession.Ephemeral)
			SaveRoot(newSession);

		_transport.Status(_currentSession.Id, "[Compaction] Complete.");
		return newSession;
	}

	// ---- Session management ----

	// Persists a top-level session (the root the user drives, and its compaction/role successors).
	// Inferring the display name first matters: the root is created nameless, and SessionService.Save
	// skips nameless sessions — so without this the root would never reach disk while named subagents would.
	private static void SaveRoot(Session session)
	{
		// The root is created nameless; the first user message gives it a name here. Announce the
		// instant it is assigned so the client switches from showing the raw ID to the display name.
		if (session.InferDisplayName())
			session.AnnounceToClient();
		SessionService.Save(session.Data);
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
		BeastSession fresh = new BeastSession(Guid.NewGuid().ToString(), string.Empty, modelId, roleName, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, ephemeral);
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

			// Global commands act on the whole runner regardless of which session they were sent to
			// (Deliver already routed them to the root queue). They touch shared configuration / process
			// lifetime, not the turn of the targeted session.
			switch (verb)
			{
				case "quit":
					_cancellationTokenSource.Cancel();
					continue;
				case "finish":
					await FinishAsync(session, _cancellationTokenSource.Token);
					continue;
				case "delete-session":
					// Internal command from the F10 session tree (not a user-facing command): delete a session
					// and its whole descendant subtree from disk. An empty target is refused so a blank id can
					// never reach the disk layer. Deleting the active root tears its tree down and stands up a
					// fresh session in its place (keeping the launch's ephemeral mode), mirrored to the client
					// via SessionReset.
					if (string.IsNullOrEmpty(args))
					{
						_transport.Error(session.Id, "No session specified to delete.");
					}
					else if (string.Equals(args, session.Id, StringComparison.Ordinal))
					{
						// The outgoing session's files are gone; flag it so the RunAsync switch does not
						// re-save it, then stand up a fresh session and reset the client to it.
						SessionService.DeleteTree(session.Id);
						_currentSessionDeleted = true;
						session = CreateFreshSession(session.Role, session.Ephemeral);
						_service = null;
						_transport.SessionReset(session.Id);
						_transport.Status(session.Id, "Deleted session and started a new one.");
					}
					else
					{
						// Idempotent: whether or not files existed, the session is gone afterward.
						SessionService.DeleteTree(args);
						_transport.Status(session.Id, "Deleted session: " + args);
					}
					continue;
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
					catch (Exception ex)
					{
						// Surface the error so the user knows reload failed. The previous config stays in effect.
						_transport.Error(session.Id, $"Reload failed: {ex}. Keeping the previous config.");
					}
					continue;
				case "help":
					_transport.Output(session.Id, "Commands: /compact, /clear, /reload, /model <id>, /finish, /test, /quit");
					continue;
			}

			// Session-local commands apply to the targeted session's own turn / model / display.
			switch (verb)
			{
				case "compact":
					_wantsCompact = true;
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
							session.MarkModelUserSelected(modelArg);
							_registry.ResetAvailability(modelArg);
							_service = null;  // force fresh service with new model next turn
							_transport.Status(session.Id, $"Model set to {modelArg}");
							// Reflect the new model on the client status line immediately rather than
							// waiting for the next turn's Stats frame.
							session.SendStats();
						}
					}
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

	// Returns all completable tokens: slash commands and model names.
	private List<string> BuildCompletionCandidates(Session session)
	{
		List<string> candidates = new List<string>
		{
			"/compact", "/reload", "/model",
			"/finish", "/help"
		};

		Role? activeRole = _roleService.GetRole(session.Role);
		LlmModel? activeModel = activeRole != null
			? _registry.GetModelForRole(activeRole, session.Model, session.ContextLength + GetCompactionReserve(session))
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

	// Returns the tools for this turn. All tool construction is handled by BuildForRole; the live callbacks
	// are closures over this runner so they capture the current session at call time (not construction time).
	private Tool[] ToolsForTurn(Role role, bool workInProgress)
	{
		return ToolFactory.BuildForRole(
			_settings.Settings,
			role,
			_registry,
			_roleService,
			this.CurrentSession,
			_settings.Settings.WebSearch,
			workInProgress,
			(prompt, budget, workCt) => _subagent.RunSubagentAsync(_settings.Settings, "Developer", prompt, budget, workCt),
			() => CurrentSession.BeginWork(),
			() => CurrentSession.EndWork(),
			null,   // no review_work at the root
			null,   // no task_complete at the root
			null,   // no finish_review at the root
			null);  // no return_to_caller at the root
	}

	// Replaces _service when the model or role has changed, or when the service has permanently
	// failed. Also updates session.Model if the registry selected a different model as fallback.
	private LlmService? RefreshService(Role? role, Session session)
	{
		if (_service != null && !_service.IsDown && _service.Model.ConfigId == session.Model && session == _currentSession)
			return null;

		int minCtx = session.ContextLength + GetCompactionReserve(session);
		LlmService? newService = _registry.CreateService(role, session.Model, minCtx);
		return newService;
	}

	// Returns the number of tokens to reserve for compaction: 10% of the current model's context
	// window, capped at 7500. This ensures there is always enough room for a response regardless of model size.
	private static int GetCompactionReserve(Session session)
	{
		return Math.Min((int)(session.ContextWindow * 0.1), 7500);
	}

}