using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


// Drives a set of SessionHandlers concurrently, one per root conversation.
// Routes inbound messages to any active session in the tree; spawns child sessions on behalf of
// SessionHandlers. Global commands (/quit, /finish, /reload, /help) are handled here; all
// per-session commands are delivered to the target session's queue via Deliver.
public class AgentOrchestrator : ISessionOrchestrator
{
	private readonly ITransportServer _transport;
	private readonly CancellationTokenSource _cancellationTokenSource;
	private readonly LlmRegistry _registry;
	private readonly RoleService _roleService;
	private readonly SettingsService _settings;
	// True for a current-folder launch with no worktree: the root session is ephemeral and nothing is resumed.
	private readonly bool _ephemeral;

	// All live sessions tracked by the orchestrator, keyed by session ID.
	private readonly object _sessionLock = new object();
	private readonly Dictionary<string, Session> _allSessions = new Dictionary<string, Session>(StringComparer.Ordinal);

	// Live completion callbacks, keyed by the session that owes the reply: each is a caller in this
	// process awaiting SpawnChildAsync's result. Deliberately not persisted — a session loaded from
	// disk can have no waiting caller, so it reconstitutes with no callback and just chats.
	// Compaction re-keys an entry to the successor that inherited the obligation. Guarded by _sessionLock.
	private readonly Dictionary<string, Action<bool, string, int>> _completions = new Dictionary<string, Action<bool, string, int>>(StringComparer.Ordinal);

	// The in-flight /test run, if any. Single-flight: overlapping suite runs would share the
	// registry and live sessions and interleave their output.
	private Task? _testTask;

	public AgentOrchestrator(
		LlmRegistry registry,
		RoleService roleService,
		SettingsService settings,
		ITransportServer transport,
		CancellationTokenSource cancellationTokenSource,
		bool ephemeral)
	{
		_registry = registry;
		_roleService = roleService;
		_settings = settings;
		_transport = transport;
		_cancellationTokenSource = cancellationTokenSource;
		_ephemeral = ephemeral;
	}

	public async Task RunAsync()
	{
		CancellationToken ct = _cancellationTokenSource.Token;

		_roleService.Reload();
		_settings.LoadSettings();
		_registry.LoadFromConfigs(_settings, _roleService);
		await _registry.LoadAutoProvidersAsync(_settings, ct);
		_registry.ExpandRoleWildcards(_roleService);
		await _registry.ProbeEndpointsAsync(ct);

		Session initial = LoadOrCreateSession();
		RegisterSession(initial);

		// Nothing configured at all: proactively open the client's /config picker — a fresh
		// install can do nothing until at least one model is enabled, so don't make the user
		// discover the command on their own.
		if (!_registry.HasModels)
			_transport.Config(initial.Id, "{\"kind\":\"no-models\"}");

		// Restore all saved sessions to the client before the first handler starts so the F10 session
		// tree shows the complete worktree history. The resumed root is replayed last inside
		// SessionHandler.RunAsync so it becomes the active view.
		if (!_ephemeral)
			RestoreAllSessions(initial);

		Task rootHandlerTask = RunRootHandlerAsync(initial, ct);
		await ReadInputAsync(ct);
		await rootHandlerTask;
	}

	// ---- ISessionOrchestrator ----

	public void RegisterSession(Session session)
	{
		// Both refusal checks live UNDER the lock so they cannot race the quiesce snapshot or a
		// subtree deletion sweep (which mark and remove their sets under this same lock): a
		// /finish teardown must not gain new sessions after its snapshot, and a child must not
		// slip in after its parent's tree was swept — its handler would resurrect deleted files.
		bool refused = false;
		lock (_sessionLock)
		{
			string? parentId = GetParentId(session.Id);
			bool parentGone = parentId != null && (!_allSessions.TryGetValue(parentId, out Session? parent) || parent.Deleted);
			if (_quiescing || parentGone)
				refused = true;
			else
				_allSessions[session.Id] = session;
		}
		if (refused)
			session.MarkDeleted();
	}

	// Registration for the restore pass only: bypasses the parent-alive rule so an orphaned child
	// file (its parent deleted out-of-band) still restores as a standalone session.
	private void RegisterRestoredSession(Session session)
	{
		lock (_sessionLock)
			_allSessions[session.Id] = session;
	}

	public void UnregisterSession(string sessionId)
	{
		lock (_sessionLock)
			_allSessions.Remove(sessionId);
	}

	// Spawns a child session under parent for the named role, runs it via a SessionHandler until
	// it answers, and returns the result through the OnComplete callback. One entry point for
	// every subagent — Developer, Reviewer, and the helper roles (Explorer, WebFetch) alike.
	public async Task<(bool ok, string text, int responseTokens)> SpawnChildAsync(
		BeastSettings settings,
		Session parent,
		string roleName,
		string? displayName,
		string prompt,
		int maxWorkTurns,
		int outputBudgetTokens,
		CancellationToken ct)
	{
		if (outputBudgetTokens <= 0)
			return (false, "No context space remains for a subagent's reply — the caller's context window is effectively full. Compact (/compact) or switch to a larger model, then retry.", 0);

		// A deleted/quiesced parent must not spawn: /finish is tearing the worktree down, or the
		// user deleted this tree — either way new work would race the teardown.
		if (parent.Deleted || _quiescing)
			return (false, "The calling session is shutting down; no new subagents may start.", 0);

		Role? role = _roleService.GetRole(roleName);
		if (role == null)
			return (false, $"Unknown role '{roleName}'.", 0);

		if (role.Kind != RoleKind.Subagent)
			return (false, $"Role '{roleName}' is not a subagent role.", 0);

		LlmService? service = _registry.CreateService(role, string.Empty, 0);
		if (service == null)
			return (false, $"No model available for role '{roleName}'.", 0);

		// No caller-supplied name: build one from the first line of the prompt.
		if (displayName == null)
		{
			int nl = prompt.IndexOf('\n');
			string head = (nl >= 0 ? prompt.Substring(0, nl) : prompt).Trim();
			if (head.Length > 60)
				head = head.Substring(0, 60);
			displayName = head.Length > 0 ? $"{role.Name} {head}" : role.Name;
		}

		// Allocate child ID and persist the parent counter so it survives a reload.
		string childId = parent.AllocateChildId();
		if (!parent.Ephemeral)
			SessionService.Save(parent.Data);

		// Inject worktree context so the subagent knows which branch and path it operates on.
		string banner = await WorktreeBannerAsync(ct);
		string seededPrompt = string.IsNullOrEmpty(banner) ? prompt : $"{banner}\n\n{prompt}";

		// The reply obligation is written onto the session itself so it survives save/load and can
		// travel to a compaction successor: the child knows which tool answers the caller.
		BeastSession subData = new BeastSession(childId, displayName, service.Model.ConfigId, role.Name,
			role.TerminatorName, outputBudgetTokens, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral);
		Session subSession = new Session(subData, role.SystemPrompt, _transport, true);
		// Claim the session before registering it so no Deliver can slip in and start a second,
		// generic handler ahead of the configured one below.
		subSession.TryAttachHandler();
		subSession.UpdateModel(service.Model);
		subSession.AnnounceToClient();
		parent.AddChild(subSession);
		RegisterSession(subSession);
		subSession.AddUserMessage(seededPrompt);

		TaskCompletionSource<(bool ok, string text, int responseTokens)> tcs =
			new TaskCompletionSource<(bool, string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

		// The work-turn budget rides on the session with the rest of the reply obligation; the live
		// callback that resolves this caller is registered against the session id (and follows the
		// obligation to a compaction successor via TransferCompletion).
		subSession.SetMaxWorkTurns(maxWorkTurns);
		lock (_sessionLock)
			_completions[subSession.Id] = (ok, text, tokens) => tcs.TrySetResult((ok, text, tokens));

		SessionHandler handler = new SessionHandler(subSession);

		// The handler runs on the orchestrator's lifetime token, not the caller's turn scope: the
		// session must outlive the parent's turn so the user can keep talking to it after it answers.
		// The caller's ct instead governs the wait below — a parent /cancel unblocks the caller
		// immediately and interrupts the child's current work, but the child session itself survives
		// (dormant if its handler winds down) and is revived by EnsureHandler on the next message.
		using CancellationTokenRegistration cancelReg = ct.Register(() =>
		{
			subSession.Interrupt();
			tcs.TrySetCanceled(ct);
		});

		// Fire-and-forget: the handler keeps servicing the session after the terminator fires so the
		// user can continue interacting with it. The TCS resolves at the first OnComplete callback;
		// the fallback result is a no-op if OnComplete already resolved it.
		_ = RunChildAndCleanUpAsync();
		return await tcs.Task;

		async Task RunChildAndCleanUpAsync()
		{
			try
			{
				await handler.RunAsync(_registry, _roleService, _settings, _transport, this, settings.WebSearch, replayOnStart: false, _cancellationTokenSource.Token);
			}
			catch (OperationCanceledException)
			{
				tcs.TrySetCanceled(_cancellationTokenSource.Token);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[Orchestrator] Child session {subSession.Id} handler failed: {ex}");
				tcs.TrySetResult((false, ex.Message, 0));
			}
			finally
			{
				tcs.TrySetResult((false, "The subagent finished without returning a result.", 0));
			}
		}
	}

	// Routes a message to the session identified by sessionId. Every registered session normally
	// has a servicing handler already; EnsureHandler here is a self-healing backstop for the one
	// way a handler can die early (an unhandled failure) — the next message restarts it instead of
	// queuing forever.
	public void Deliver(string sessionId, string content)
	{
		Session? session;
		lock (_sessionLock)
			_allSessions.TryGetValue(sessionId, out session);
		if (session != null)
		{
			session.Deliver(sessionId, content);
			EnsureHandler(session);
		}
	}

	// Starts the servicing handler for a session when none is attached; a no-op otherwise. Called
	// eagerly wherever a session comes into existence without its own configured handler (restored
	// from disk, compaction predecessor) and as a backstop from Deliver. Runs on the orchestrator's
	// lifetime token. Everything the handler needs is reconstituted from the session itself: the
	// budgets ride on BeastSession, the parent resolves from the session id, and a completion
	// callback exists only if a live caller registered one.
	public void EnsureHandler(Session session)
	{
		if (!session.TryAttachHandler())
			return;

		SessionHandler handler = new SessionHandler(session);
		_ = RunRevivedAsync();

		async Task RunRevivedAsync()
		{
			try
			{
				await handler.RunAsync(_registry, _roleService, _settings, _transport, this, _settings.Settings.WebSearch, replayOnStart: false, _cancellationTokenSource.Token);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[Orchestrator] Revived session {session.Id} handler failed: {ex}");
			}
		}
	}

	// Set while /finish is quiescing so no new session can register underneath the teardown; a
	// registration attempted during the window is refused and marked deleted immediately.
	private volatile bool _quiescing;

	// Marks every registered session deleted (stopping its handler and blocking further saves),
	// unregisters it, fails any caller still waiting on one, and then WAITS for the handlers to
	// actually unwind — signalling alone is not quiescence: a handler mid-tool could still write
	// after a premature cleanliness check. Returns false when a handler failed to stop in time.
	private async Task<bool> QuiesceAllSessionsAsync(string reason, CancellationToken ct)
	{
		// Flag and snapshot under one lock hold: a concurrent registration either lands before
		// the snapshot (and is quiesced with the rest) or sees the flag and is refused.
		List<Session> live;
		lock (_sessionLock)
		{
			_quiescing = true;
			live = new List<Session>(_allSessions.Values);
		}
		foreach (Session s in live)
		{
			s.MarkDeleted();
			UnregisterSession(s.Id);
			CompleteSession(s.Id, false, reason, 0);
		}

		// Handlers detach in their finally; tool kills are prompt, so a healthy handler unwinds in
		// well under this bound. A stuck one fails the quiesce rather than being written off.
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		for (; ; )
		{
			bool anyAttached = false;
			foreach (Session s in live)
			{
				if (s.HasAttachedHandler)
				{
					anyAttached = true;
					break;
				}
			}
			if (!anyAttached)
				return true;
			if (DateTimeOffset.UtcNow >= deadline)
				return false;
			await Task.Delay(100, ct);
		}
	}

	// Starts a fresh root session and announces it with SessionReset — the client's contract when
	// its root was deleted is that the agent supplies the replacement and names it in this frame.
	private void StartReplacementRoot()
	{
		Session replacement = CreateFreshRootSession(string.Empty, _ephemeral);
		RegisterSession(replacement);
		_transport.SessionReset(replacement.Id);
		EnsureHandler(replacement);
	}

	// True when any registered session is a root (no parent suffix in its id).
	private bool AnyRootRegistered()
	{
		lock (_sessionLock)
		{
			foreach (string id in _allSessions.Keys)
			{
				if (!id.Contains('_'))
					return true;
			}
		}
		return false;
	}

	// Resolves the registered parent Session for a child id ("parentId_N"), or null for roots and
	// parents that are no longer registered.
	public Session? FindParent(Session session)
	{
		string? parentId = GetParentId(session.Id);
		Session? parent = null;
		if (parentId != null)
		{
			lock (_sessionLock)
				_allSessions.TryGetValue(parentId, out parent);
		}
		return parent;
	}

	// Moves a waiting caller's callback to the compaction successor that inherited the obligation.
	public void TransferCompletion(string fromSessionId, string toSessionId)
	{
		lock (_sessionLock)
		{
			if (_completions.TryGetValue(fromSessionId, out Action<bool, string, int>? callback))
			{
				_completions.Remove(fromSessionId);
				_completions[toSessionId] = callback;
			}
		}
	}

	// Fires and clears the callback waiting on this session, if any. A no-op for sessions with no
	// live caller (roots, restored sessions, sessions that already answered).
	public void CompleteSession(string sessionId, bool ok, string text, int responseTokens)
	{
		Action<bool, string, int>? callback;
		lock (_sessionLock)
		{
			if (_completions.TryGetValue(sessionId, out callback))
				_completions.Remove(sessionId);
		}
		callback?.Invoke(ok, text, responseTokens);
	}

	// ---- Root session loop ----

	// Drives one root session chain until cancelled. Compaction successors are created and
	// registered by the SessionHandler itself; the handler simply advances along the chain.
	private async Task RunRootHandlerAsync(Session initial, CancellationToken ct)
	{
		SessionHandler handler = new SessionHandler(initial);
		initial.TryAttachHandler();
		await handler.RunAsync(_registry, _roleService, _settings, _transport, this, _settings.Settings.WebSearch, replayOnStart: true, ct);
	}

	// ---- Session initialization ----

	private Session LoadOrCreateSession()
	{
		if (!_ephemeral)
		{
			BeastSession? last = FindLastRootSession();
			if (last != null)
			{
				_transport.Status(last.Id, "Resumed session: " + last.DisplayName);
				return new Session(last, string.Empty, _transport, false);
			}
		}

		return CreateFreshRootSession(string.Empty, _ephemeral);
	}

	private Session CreateFreshRootSession(string roleName, bool ephemeral)
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
		BeastSession fresh = new BeastSession(Guid.NewGuid().ToString(), string.Empty, modelId, roleName,
			string.Empty, 0, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, ephemeral);
		return new Session(fresh, systemPrompt, _transport, false);
	}

	// Returns the most recently created root session from disk, or null if none exists.
	private static BeastSession? FindLastRootSession()
	{
		List<SessionService.SessionFileInfo> all = SessionService.LoadAll();
		BeastSession? best = null;
		long bestOrder = -1;

		foreach (SessionService.SessionFileInfo info in all)
		{
			if (info.Session.Id.Contains('_'))
				continue;
			if (info.CreationOrder > bestOrder)
			{
				bestOrder = info.CreationOrder;
				best = info.Session;
			}
		}

		return best;
	}

	// Restores all saved sessions in the worktree to the client. Called once at startup, before
	// the first handler starts. The resumed root is announced last (DFS, root replayed last) so
	// SessionHandler.RunAsync's own replay makes it the client's active view.
	private void RestoreAllSessions(Session root)
	{
		List<SessionService.SessionFileInfo> allSessions = SessionService.LoadAll();
		if (allSessions.Count == 0)
			return;

		Dictionary<string, List<SessionService.SessionFileInfo>> childrenMap =
			new Dictionary<string, List<SessionService.SessionFileInfo>>(StringComparer.Ordinal);
		HashSet<string> hasParent = new HashSet<string>(StringComparer.Ordinal);

		foreach (SessionService.SessionFileInfo info in allSessions)
		{
			string? parentId = GetParentId(info.Session.Id);
			if (parentId != null)
			{
				bool parentExists = false;
				foreach (SessionService.SessionFileInfo s in allSessions)
				{
					if (string.Equals(s.Session.Id, parentId, StringComparison.Ordinal))
					{
						parentExists = true;
						break;
					}
				}
				if (parentExists)
				{
					if (!childrenMap.TryGetValue(parentId, out List<SessionService.SessionFileInfo>? kids))
					{
						kids = new List<SessionService.SessionFileInfo>();
						childrenMap[parentId] = kids;
					}
					kids.Add(info);
					hasParent.Add(info.Session.Id);
				}
			}
		}

		foreach (KeyValuePair<string, List<SessionService.SessionFileInfo>> kvp in childrenMap)
			kvp.Value.Sort((a, b) => b.CreationOrder.CompareTo(a.CreationOrder));

		List<SessionService.SessionFileInfo> roots = new List<SessionService.SessionFileInfo>();
		foreach (SessionService.SessionFileInfo s in allSessions)
		{
			if (!hasParent.Contains(s.Session.Id))
				roots.Add(s);
		}
		roots.Sort((a, b) => b.CreationOrder.CompareTo(a.CreationOrder));

		SessionService.SessionFileInfo? resumedRoot = null;
		foreach (SessionService.SessionFileInfo r in roots)
		{
			if (string.Equals(r.Session.Id, root.Id, StringComparison.Ordinal))
			{
				resumedRoot = r;
				break;
			}
		}
		if (resumedRoot != null)
			roots.Remove(resumedRoot);

		List<SessionService.SessionFileInfo> ordered = new List<SessionService.SessionFileInfo>();
		foreach (SessionService.SessionFileInfo rootInfo in roots)
			DfsAddToOrdered(rootInfo, childrenMap, ordered);
		if (resumedRoot != null)
			DfsAddToOrdered(resumedRoot, childrenMap, ordered);

		foreach (SessionService.SessionFileInfo info in ordered)
		{
			// The resumed root already has a live, registered Session; its handler replays it.
			// Building a second wrapper for it here would shadow the live one in the registry.
			if (string.Equals(info.Session.Id, root.Id, StringComparison.Ordinal))
				continue;

			Session session = new Session(info.Session, string.Empty, _transport, info.Session.Id.Contains('_'));

			// Whoever spawned this session cannot be waiting across a process restart; without a
			// caller the reply obligation is meaningless and would only steer a revived session
			// toward answering into the void. A session unloaded before it delivered its reply is
			// marked Incomplete first, so the tree shows its work was cut short rather than
			// blending it in with ordinary conversations.
			if (session.OwesReply && session.Status == SessionStatus.Ongoing)
				session.SetTerminationStatus(SessionStatus.Incomplete);
			session.ClearReplyObligation();

			session.AnnounceToClient();
			session.SendStats();
			session.ReplayToTransport();

			// Park the session until real user input arrives: an interrupted save can end with
			// dangling tool calls, and a fleet of restored sessions must not start running turns
			// (and spending tokens) on their own at startup.
			session.MarkInterrupted();

			// Every session that exists is serviced: register it and start its handler immediately.
			// The handler sits in the idle wait until the user talks to it. The restore-only
			// registration skips the parent-alive rule so orphaned child files still come back.
			RegisterRestoredSession(session);
			EnsureHandler(session);
		}
	}

	private static void DfsAddToOrdered(
		SessionService.SessionFileInfo node,
		Dictionary<string, List<SessionService.SessionFileInfo>> childrenMap,
		List<SessionService.SessionFileInfo> ordered)
	{
		ordered.Add(node);
		if (childrenMap.TryGetValue(node.Session.Id, out List<SessionService.SessionFileInfo>? children))
		{
			foreach (SessionService.SessionFileInfo child in children)
				DfsAddToOrdered(child, childrenMap, ordered);
		}
	}

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

	// ---- Input routing ----

	private static readonly HashSet<string> GlobalCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"quit", "finish", "reload", "help", "delete-session", "test",
		"config-endpoints", "config-catalog", "config-apply"
	};

	private static bool IsGlobalCommand(string text)
	{
		// Caller guarantees text starts with '/'.
		string trimmed = text.Substring(1).Trim();
		int spaceIdx = trimmed.IndexOf(' ');
		string verb = spaceIdx >= 0 ? trimmed.Substring(0, spaceIdx) : trimmed;
		return GlobalCommands.Contains(verb);
	}

	private async Task ReadInputAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			// Inbound wire format: sessionId|content
			// Falls back to the active session if no pipe is present (e.g. debug transport).
			string? line = await _transport.TryReadAsync(100, ct);
			if (line == null)
			{
				_cancellationTokenSource.Cancel();
				break;
			}
			if (line.Length == 0)
				continue;

			int pipe = line.IndexOf('|');
			string content = pipe >= 0 ? line.Substring(pipe + 1) : line;
			if (content.Length == 0)
				continue;

			if (content.StartsWith("/", StringComparison.Ordinal) && IsGlobalCommand(content))
			{
				// Global commands run exactly once; the session id only routes status output.
				// Dispatching per registered session ran /test N times, reloaded N times, and made
				// /finish delete every session tree instead of the one it was invoked from.
				string targetId = pipe >= 0 ? line.Substring(0, pipe) : string.Empty;
				if (targetId.Length == 0)
				{
					lock (_sessionLock)
					{
						foreach (string sid in _allSessions.Keys)
						{
							targetId = sid;
							break;
						}
					}
				}
				// A faulting command handler must NEVER kill this loop: it is the agent's only
				// ear, and an unhandled throw here leaves the whole process deaf — every later
				// command silently ignored, which presents as a total hang.
				try
				{
					await HandleGlobalCommandAsync(targetId, content, ct);
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"[AgentOrchestrator] Global command '{content}' threw: {ex}");
					_transport.Error(targetId, $"Command failed: {ex.Message}");
				}
				continue;
			}

			if (pipe >= 0)
			{
				Deliver(line.Substring(0, pipe), content);
			}
			else
			{
				// No session prefix — broadcast to all registered sessions.
				List<Session> all;
				lock (_sessionLock)
					all = new List<Session>(_allSessions.Values);
				foreach (Session s in all)
				{
					s.Deliver(s.Id, content);
					EnsureHandler(s);
				}
			}
		}
	}

	// Reloads every configuration component atomically. Snapshot every published reference first:
	// each component swaps atomically, but a failure partway (bad roles.json after settings
	// already loaded, probe error) would otherwise leave a MIX of old and new config. Restoring
	// the snapshots makes "keeping the previous config" true across the whole reload. Returns
	// true when the new configuration is live.
	private async Task<bool> ReloadConfigurationAsync(string sessionId, CancellationToken ct)
	{
		bool reloaded = false;
		Dictionary<string, Role> priorRoles = _roleService.SnapshotRoles();
		BeastSettings priorSettings = _settings.SnapshotSettings();
		Dictionary<string, LlmModel> priorModels = _registry.SnapshotModels();
		try
		{
			_roleService.Reload();
			_settings.LoadSettings();
			_registry.LoadFromConfigs(_settings, _roleService);
			await _registry.LoadAutoProvidersAsync(_settings, ct);
			_registry.ExpandRoleWildcards(_roleService);
			await _registry.ProbeEndpointsAsync(ct);
			_registry.ResetAllAvailability();
			_transport.Status(sessionId, "Config files reloaded.");

			// Signal every active session to refresh its role and LlmService so
			// changes to roles.json (roles added/removed/modified) and settings.json
			// (models/endpoints) propagate immediately to running sessions.
			List<Session> allSessions;
			lock (_sessionLock)
				allSessions = new List<Session>(_allSessions.Values);
			foreach (Session s in allSessions)
				s.RequestRefresh();
			reloaded = true;
		}
		catch (Exception ex)
		{
			_roleService.RestoreRoles(priorRoles);
			_settings.RestoreSettings(priorSettings);
			_registry.RestoreModels(priorModels);
			_transport.Error(sessionId, $"Reload failed: {ex}. Keeping the previous config.");
		}
		return reloaded;
	}

	// ---- /config flow ----

	// Sends the known endpoints (auto first, then the manual providers for reference) so the
	// picker's first screen can list them without Beast ever holding API keys.
	private void HandleConfigEndpoints(string sessionId)
	{
		ConfigEndpointsPayload payload = new ConfigEndpointsPayload();
		foreach (AutoProviderConfig auto in _settings.Settings.Auto)
		{
			payload.Endpoints.Add(new ConfigEndpointInfo { BaseUrl = auto.BaseUrl, Source = "auto", EnabledCount = auto.Models.Count });
		}
		foreach (ProviderConfig manual in _settings.Settings.Providers)
		{
			int enabled = 0;
			foreach (ModelConfig m in manual.Models)
			{
				if (m.Enabled)
					enabled++;
			}
			payload.Endpoints.Add(new ConfigEndpointInfo { BaseUrl = manual.BaseUrl, Source = "manual", EnabledCount = enabled });
		}
		_transport.Config(sessionId, JsonSerializer.Serialize(payload, BeastJson.Compact.ConfigEndpointsPayload));
	}

	// Fetches an endpoint's catalog and sends it merged with the current enabled state and any
	// persisted overrides. args is JSON: {"baseUrl": "...", "apiKey": "..."} — the key may be
	// empty for an already-configured endpoint, whose stored key is used.
	private async Task HandleConfigCatalogAsync(string sessionId, string args, CancellationToken ct)
	{
		string baseUrl = string.Empty;
		string apiKey = string.Empty;
		try
		{
			System.Text.Json.Nodes.JsonNode? node = System.Text.Json.Nodes.JsonNode.Parse(args);
			baseUrl = node?["baseUrl"]?.GetValue<string>() ?? string.Empty;
			apiKey = node?["apiKey"]?.GetValue<string>() ?? string.Empty;
		}
		catch (Exception)
		{
		}

		ConfigCatalogPayload payload = new ConfigCatalogPayload { BaseUrl = baseUrl };
		if (string.IsNullOrWhiteSpace(baseUrl))
		{
			payload.Error = "No baseUrl supplied.";
		}
		else
		{
			AutoProviderConfig? existing = FindAutoEndpoint(baseUrl);
			if (string.IsNullOrEmpty(apiKey) && existing != null)
				apiKey = existing.ApiKey;

			// A manually-configured provider at the same URL can lend its key too, so browsing a
			// manual endpoint's catalog in the picker just works.
			if (string.IsNullOrEmpty(apiKey))
			{
				foreach (ProviderConfig manual in _settings.Settings.Providers)
				{
					if (string.Equals(manual.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
					{
						apiKey = manual.ApiKey;
						break;
					}
				}
			}

			// Progress lands in the status bar, which stays visible under the picker modal — the
			// user can see exactly which step is in flight instead of a silent wait.
			_transport.Status(sessionId, $"[config] Fetching catalog from {baseUrl} ...");
			(List<DiscoveredModel> discovered, string error) = await ModelCatalog.FetchAsync(baseUrl, apiKey, ct);

			// Docker/native host mismatch: the picker runs on the host where "localhost" is the
			// natural spelling, but this fetch runs wherever the Agent runs — inside Docker that
			// host is host.docker.internal (and the reverse on a native run). Retry with the
			// swapped host and, when it works, adopt the swapped URL as the endpoint so the
			// WORKING form is what the picker carries forward and persists.
			if (error.Length > 0)
			{
				string swapped = SwapDockerHost(baseUrl);
				if (swapped.Length > 0)
				{
					_transport.Status(sessionId, $"[config] {baseUrl} unreachable; trying {swapped} ...");
					(List<DiscoveredModel> retried, string retryError) = await ModelCatalog.FetchAsync(swapped, apiKey, ct);
					if (retryError.Length == 0)
					{
						baseUrl = swapped;
						payload.BaseUrl = swapped;
						discovered = retried;
						error = string.Empty;
						AutoProviderConfig? swappedExisting = FindAutoEndpoint(baseUrl);
						if (swappedExisting != null)
							existing = swappedExisting;
					}
				}
			}

			if (error.Length > 0)
			{
				List<DiscoveredModel>? cached = _registry.CachedCatalog(baseUrl);
				if (cached != null)
				{
					discovered = cached;
					error = string.Empty;
				}
			}
			payload.Error = error;

			foreach (DiscoveredModel d in discovered)
			{
				AutoModelConfig? entry = FindAutoModel(existing, d.Id);
				payload.Models.Add(new ConfigModelInfo
				{
					Id = d.Id,
					Name = d.Name,
					ContextWindow = d.ContextWindow,
					MaxOutputTokens = d.MaxOutputTokens,
					CostInput = d.CostInput,
					CostOutput = d.CostOutput,
					Modalities = d.Modalities,
					Configured = entry != null,
					Enabled = entry != null && entry.Enabled,
					Created = d.Created,
					Override = entry
				});
			}

			// Configured models the catalog no longer lists still appear (temporarily disabled at
			// runtime), so the user can see them and deliberately drop them if the removal is real.
			if (existing != null)
			{
				foreach (AutoModelConfig entry in existing.Models)
				{
					bool inCatalog = false;
					foreach (DiscoveredModel d in discovered)
					{
						if (string.Equals(d.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
						{
							inCatalog = true;
							break;
						}
					}
					if (!inCatalog)
					{
						payload.Models.Add(new ConfigModelInfo
						{
							Id = entry.Id,
							Name = $"{entry.Id} (not in catalog)",
							Configured = true,
							Enabled = entry.Enabled,
							Override = entry
						});
					}
				}
			}
		}

		_transport.Status(sessionId, payload.Error.Length > 0
			? $"[config] Catalog fetch failed: {payload.Error}"
			: $"[config] {payload.Models.Count} models from {payload.BaseUrl}");
		_transport.Config(sessionId, JsonSerializer.Serialize(payload, BeastJson.Compact.ConfigCatalogPayload));
	}

	// Applies one endpoint's desired state from the picker: persists it to the project
	// settings.json, then runs the full reload so the new models are live immediately.
	private async Task HandleConfigApplyAsync(string sessionId, string args, CancellationToken ct)
	{
		ConfigApplyPayload? payload = null;
		try
		{
			payload = JsonSerializer.Deserialize(args, BeastJson.Compact.ConfigApplyPayload);
		}
		catch (Exception ex)
		{
			_transport.Error(sessionId, $"config-apply: unusable payload: {ex.Message}");
		}

		if (payload != null && !string.IsNullOrWhiteSpace(payload.BaseUrl))
		{
			_settings.SaveAutoEndpoint(payload.BaseUrl, payload.ApiKey, payload.Models);
			bool reloaded = await ReloadConfigurationAsync(sessionId, ct);
			_transport.Config(sessionId, reloaded ? "{\"kind\":\"applied\"}" : "{\"kind\":\"apply-failed\"}");
			if (reloaded)
				_transport.Status(sessionId, $"Saved {payload.Models.Count} enabled model(s) for {payload.BaseUrl}.");
		}
		else if (payload != null)
		{
			_transport.Error(sessionId, "config-apply: no baseUrl supplied.");
		}
	}

	// Swaps localhost ↔ host.docker.internal in a URL; empty when the host is neither (no swap
	// to try). Mirrors ProtocolProxy's probe fallback, in the opposite direction.
	private static string SwapDockerHost(string url)
	{
		if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out Uri? uri))
			return string.Empty;

		string replacement;
		if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1")
			replacement = "host.docker.internal";
		else if (string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
			replacement = "localhost";
		else
			return string.Empty;

		UriBuilder builder = new UriBuilder(uri) { Host = replacement };
		return builder.Uri.ToString().TrimEnd('/');
	}

	private AutoProviderConfig? FindAutoEndpoint(string baseUrl)
	{
		foreach (AutoProviderConfig auto in _settings.Settings.Auto)
		{
			if (string.Equals(auto.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
				return auto;
		}
		return null;
	}

	private static AutoModelConfig? FindAutoModel(AutoProviderConfig? endpoint, string id)
	{
		if (endpoint == null)
			return null;
		foreach (AutoModelConfig model in endpoint.Models)
		{
			if (string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase))
				return model;
		}
		return null;
	}

	private async Task HandleGlobalCommandAsync(string sessionId, string text, CancellationToken ct)
	{
		string trimmed = text.Substring(1).Trim();
		string verb;
		int spaceIdx = trimmed.IndexOf(' ');
		if (spaceIdx >= 0)
			verb = trimmed.Substring(0, spaceIdx).ToLowerInvariant();
		else
			verb = trimmed.ToLowerInvariant();

		switch (verb)
		{
			case "quit":
				_cancellationTokenSource.Cancel();
				break;
			case "finish":
				await FinishAsync(sessionId, ct);
				break;
			case "reload":
				await ReloadConfigurationAsync(sessionId, ct);
				break;
			case "config-endpoints":
				HandleConfigEndpoints(sessionId);
				break;
			case "config-catalog":
			{
				// Runs in the background: an unreachable endpoint can eat the full HTTP timeout
				// twice (host-swap retry), and the input loop must keep servicing commands —
				// including the /quit or Esc-driven traffic of a user giving up on the fetch.
				string catalogArgs = spaceIdx >= 0 ? trimmed.Substring(spaceIdx + 1).Trim() : string.Empty;
				_ = Task.Run(async () =>
				{
					try
					{
						await HandleConfigCatalogAsync(sessionId, catalogArgs, ct);
					}
					catch (Exception ex)
					{
						Console.Error.WriteLine($"[AgentOrchestrator] config-catalog threw: {ex}");
						_transport.Error(sessionId, $"Catalog fetch failed: {ex.Message}");
					}
				}, ct);
				break;
			}
			case "config-apply":
				await HandleConfigApplyAsync(sessionId, spaceIdx >= 0 ? trimmed.Substring(spaceIdx + 1).Trim() : string.Empty, ct);
				break;
			case "help":
				_transport.Output(sessionId, "Commands: /compact, /reload, /model <id>, /finish, /test, /quit");
				break;
			case "delete-session":
			{
				string? target = spaceIdx >= 0 ? trimmed.Substring(spaceIdx + 1).Trim() : null;
				if (string.IsNullOrEmpty(target))
				{
					_transport.Error(sessionId, "No session specified to delete.");
				}
				else
				{
					// Stop and unregister the target AND every descendant before touching the files:
					// a live handler anywhere in the subtree would otherwise save its session again
					// and resurrect the files DeleteTree just removed. Collect, mark, and remove
					// under ONE lock hold so a concurrently spawning child either lands in the sweep
					// or is refused by RegisterSession's parent-alive rule — never orphaned between.
					string childPrefix = target + "_";
					List<Session> doomed = new List<Session>();
					lock (_sessionLock)
					{
						foreach ((string id, Session s) in _allSessions)
						{
							if (string.Equals(id, target, StringComparison.Ordinal) || id.StartsWith(childPrefix, StringComparison.Ordinal))
								doomed.Add(s);
						}
						foreach (Session s in doomed)
						{
							s.MarkDeleted();
							_allSessions.Remove(s.Id);
						}
					}
					foreach (Session s in doomed)
					{
						// A caller still waiting on a deleted child gets a definitive failure
						// instead of hanging on a session that will never answer.
						CompleteSession(s.Id, false, "The session was deleted.", 0);
					}
					SessionService.DeleteTree(target);
					_transport.Status(sessionId, $"Deleted session: {target}");

					// If that removed the last root, the client is now showing nothing and waiting
					// for the replacement the agent is contracted to provide: start a fresh root
					// and announce it via SessionReset so the user can keep working.
					if (!AnyRootRegistered())
						StartReplacementRoot();
				}
				break;
			}
			case "test":
			{
				if (_testTask != null && !_testTask.IsCompleted)
				{
					_transport.Status(sessionId, "A /test run is already in progress.");
					break;
				}

				string? filter = spaceIdx >= 0 ? trimmed.Substring(spaceIdx + 1).Trim() : null;
				// Run in the background so the input loop keeps servicing commands (including
				// /quit) while the suite executes. Tracked so a second /test cannot overlap it.
				_testTask = Task.Run(async () =>
				{
					try
					{
						await RunTestsAsync(sessionId, filter, ct);
					}
					catch (OperationCanceledException)
					{
					}
					catch (Exception ex)
					{
						_transport.Error(sessionId, $"/test failed: {ex}");
					}
				}, ct);
				break;
			}
		}
	}

	// ---- /finish (worktree integration) ----

	// Bash script to check whether the worktree's feature branch is fully integrated into the base
	// branch and has no uncommitted changes. Reports PENDING with details or OK with both branch names.
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

	// Bash script to detach the worktree, prune its registration, and delete its merged branch.
	private const string FinishRemoveScript =
		"cd /git\n" +
		"feat=$(git -C /workspace rev-parse --abbrev-ref HEAD)\n" +
		"git worktree remove --force /workspace >/dev/null 2>&1 || true\n" +
		"git worktree prune >/dev/null 2>&1 || true\n" +
		"if git worktree list --porcelain | grep -qx 'worktree /workspace'; then echo 'ERROR: worktree still registered at /workspace.'; exit 1; fi\n" +
		"git branch -d \"$feat\" >/dev/null 2>&1 || true\n" +
		"echo REMOVED\n";

	private async Task FinishAsync(string sessionId, CancellationToken ct)
	{
		if (!Directory.Exists("/git"))
		{
			_transport.Error(sessionId, "/finish is only available inside a Beast worktree container.");
			return;
		}

		if (_testTask != null && !_testTask.IsCompleted)
		{
			_transport.Error(sessionId, "/finish refused: a /test run is still in progress. Wait for it to complete.");
			return;
		}

		ToolResult check = await ShellTools.BashAsync("finish_check", FinishCheckScript, null, ct);
		string[] lines = check.StdOut.Trim().Length == 0
			? Array.Empty<string>()
			: check.StdOut.Trim().Split('\n');
		string verdict = lines.Length > 0 ? lines[0].Trim() : string.Empty;

		if (verdict != "OK")
		{
			string detail = lines.Length > 1
				? string.Join("\n", lines, 1, lines.Length - 1)
				: "Could not determine worktree status.";
			_transport.Output(sessionId,
				"Cannot finish yet — the worktree is not fully integrated:\n" + detail +
				"\n\nTell the agent that its task is to finish the work (review and approve the changes) or to reset the branch, then run /finish again.");
			return;
		}

		string baseBranch = lines.Length > 2 ? lines[2].Trim() : string.Empty;

		// The check said clean — but a still-running handler could land new commits or edits
		// between that check and the forced worktree removal below. Quiesce every session (and
		// WAIT for the handlers to unwind), then re-check: anything that slipped in while they
		// wound down keeps the worktree alive instead of being destroyed with it.
		if (!await QuiesceAllSessionsAsync("The worktree is being finished.", ct))
		{
			_quiescing = false;
			_transport.Error(sessionId,
				"Aborting /finish: a session did not stop in time. The worktree was left in place; a fresh session has been started.");
			StartReplacementRoot();
			return;
		}

		ToolResult recheck = await ShellTools.BashAsync("finish_check", FinishCheckScript, null, ct);
		string[] recheckLines = recheck.StdOut.Trim().Length == 0
			? Array.Empty<string>()
			: recheck.StdOut.Trim().Split('\n');
		if (recheckLines.Length == 0 || recheckLines[0].Trim() != "OK")
		{
			string changed = recheckLines.Length > 1
				? string.Join("\n", recheckLines, 1, recheckLines.Length - 1)
				: "Could not determine worktree status.";
			_quiescing = false;
			_transport.Error(sessionId,
				"Aborting /finish: the worktree changed while sessions were being stopped:\n" + changed +
				"\nThe worktree was left in place. All sessions were stopped; a fresh session has been started.");
			StartReplacementRoot();
			return;
		}

		// No session-file deletion here: the worktree removal below deletes /workspace (sessions
		// included) wholesale, and deleting them early meant a FAILED removal had already lost
		// every conversation.
		ToolResult remove = await ShellTools.BashAsync("finish_remove", FinishRemoveScript, null, ct);
		if (!remove.StdOut.Contains("REMOVED"))
		{
			string detail = remove.StdErr;
			if (!string.IsNullOrEmpty(remove.StdOut))
				detail = string.IsNullOrEmpty(detail) ? remove.StdOut : detail + "\n" + remove.StdOut;
			_quiescing = false;
			_transport.Error(sessionId, "Failed to remove the worktree:\n" +
				(string.IsNullOrWhiteSpace(detail) ? "(no output)" : detail) +
				"\nAll sessions were stopped; a fresh session has been started.");
			StartReplacementRoot();
			return;
		}

		// The CWD (/workspace) is gone now; move to /git so the process keeps a valid working directory.
		try { Directory.SetCurrentDirectory("/git"); } catch { }

		_transport.Output(sessionId, $"Worktree finished and integrated into '{baseBranch}'. Removing it and shutting down.");
		_transport.Status(sessionId, "worktree-finished");
	}

	// ---- Helpers ----

	private async Task RunTestsAsync(string sessionId, string? filter, CancellationToken ct)
	{
		_transport.Status(sessionId, "Running tests...");
		TestContext ctx = new TestContext(_transport);
		LlmServiceTests.Test(ctx);
		ContextBudgetTests.Test(ctx);
		FixJsonTests.Test(ctx);
		await FileToolsTests.TestAsync(ctx);
		ShellToolsTests.Test(ctx);

		Session? any;
		lock (_sessionLock)
		{
			any = null;
			foreach (Session s in _allSessions.Values)
			{
				any = s;
				break;
			}
		}
		if (any != null)
			await WebToolsTests.TestAsync(ctx, _settings.Settings.WebSearch, _roleService, _transport, any, ct);

		await PerModelLlmTests.TestAsync(ctx, _registry, _roleService, _settings, ct);
		ProtocolSwitchTests.Test(ctx);
		_transport.Output(sessionId, $"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
	}

	// Builds the worktree context
	// Returns empty when the CWD is not a git checkout.
	private static async Task<string> WorktreeBannerAsync(CancellationToken ct)
	{
		string script =
			"branch=$(git rev-parse --abbrev-ref HEAD 2>/dev/null)\n" +
			"[ -z \"$branch\" ] && exit 0\n" +
			"echo \"$branch\"\n" +
			"pwd\n";

		ToolResult result = await ShellTools.BashAsync("subagent_worktree", script, null, ct);
		if (result.ExitCode != 0)
			return string.Empty;

		string[] lines = result.StdOut.Trim().Split('\n');
		if (lines.Length < 2)
			return string.Empty;

		string branch = lines[0].Trim();
		string path = lines[1].Trim();
		if (string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(path))
			return string.Empty;

		return $"[Worktree] Your working directory is the git worktree '{path}', on branch '{branch}'. All file reads, edits, and commands operate here.";
	}
}