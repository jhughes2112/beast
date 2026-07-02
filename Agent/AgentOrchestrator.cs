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
		await _registry.ProbeEndpointsAsync(ct);

		Session initial = LoadOrCreateSession();
		RegisterSession(initial);

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
			return (false, "No output budget remaining for a subagent.", 0);

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
		subSession.UpdateModel(service.Model);
		subSession.AnnounceToClient();
		parent.AddChild(subSession);
		RegisterSession(subSession);
		subSession.AddUserMessage(seededPrompt);

		TaskCompletionSource<(bool ok, string text, int responseTokens)> tcs =
			new TaskCompletionSource<(bool, string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

		SessionRunConfig config = new SessionRunConfig(
			parent, maxWorkTurns, (ok, text, tokens) => tcs.TrySetResult((ok, text, tokens)));

		SessionHandler handler = new SessionHandler(subSession);

		// Fire-and-forget: the handler keeps the session alive after the terminator fires so the
		// user can continue interacting with it. The TCS resolves at the first OnComplete callback.
		// The local function owns the lifetime: unregisters the session and sets a fallback result
		// (no-op if OnComplete already resolved the TCS) once the handler truly exits.
		_ = RunChildAndCleanUpAsync();
		return await tcs.Task;

		async Task RunChildAndCleanUpAsync()
		{
			try
			{
				await handler.RunAsync(config, _registry, _roleService, _settings, _transport, this, settings.WebSearch, ct);
			}
			catch (OperationCanceledException)
			{
				tcs.TrySetCanceled(ct);
			}
			catch (Exception ex)
			{
				tcs.TrySetResult((false, ex.Message, 0));
			}
			finally
			{
				UnregisterSession(subSession.Id);
				tcs.TrySetResult((false, "The subagent finished without returning a result.", 0));
			}
		}
	}

	// Routes a message to the session identified by sessionId.
	public void Deliver(string sessionId, string content)
	{
		Session? session;
		lock (_sessionLock)
			_allSessions.TryGetValue(sessionId, out session);
		session?.Deliver(sessionId, content);
	}

	// ---- Root session loop ----

	// Drives one root session chain until cancelled. Compaction successors are created and
	// registered by the SessionHandler itself; the handler simply advances along the chain.
	private async Task RunRootHandlerAsync(Session initial, CancellationToken ct)
	{
		SessionRunConfig config = new SessionRunConfig(null, 0, null);
		SessionHandler handler = new SessionHandler(initial);
		await handler.RunAsync(config, _registry, _roleService, _settings, _transport, this, _settings.Settings.WebSearch, ct);
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
			Session session = new Session(info.Session, string.Empty, _transport, true);
			session.AnnounceToClient();
			session.SendStats();
			session.ReplayToTransport();
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
		"quit", "finish", "reload", "help", "delete-session", "test"
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
				// Global commands are dispatched to every registered session.
				List<string> sessionIds;
				lock (_sessionLock)
					sessionIds = new List<string>(_allSessions.Keys);
				foreach (string sid in sessionIds)
					await HandleGlobalCommandAsync(sid, content, ct);
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
					s.Deliver(s.Id, content);
			}
		}
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
				try
				{
					_roleService.Reload();
					_settings.LoadSettings();
					_registry.LoadFromConfigs(_settings, _roleService);
					await _registry.ProbeEndpointsAsync(ct);
					_registry.ResetAllAvailability();
					_transport.Status(sessionId, "Config files reloaded.");
				}
				catch (Exception ex)
				{
					_transport.Error(sessionId, $"Reload failed: {ex}. Keeping the previous config.");
				}
				break;
			case "help":
				_transport.Output(sessionId, "Commands: /compact, /clear, /reload, /model <id>, /finish, /test, /quit");
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
					SessionService.DeleteTree(target);
					Session? targetSession;
					lock (_sessionLock)
						_allSessions.TryGetValue(target, out targetSession);
					UnregisterSession(target);
					if (targetSession != null)
						targetSession.Interrupt();
					_transport.Status(sessionId, $"Deleted session: {target}");
				}
				break;
			}
			case "test":
			{
				string? filter = spaceIdx >= 0 ? trimmed.Substring(spaceIdx + 1).Trim() : null;
				await RunTestsAsync(sessionId, filter, ct);
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

		SessionService.DeleteTree(sessionId);

		ToolResult remove = await ShellTools.BashAsync("finish_remove", FinishRemoveScript, null, ct);
		if (!remove.StdOut.Contains("REMOVED"))
		{
			string detail = remove.StdErr;
			if (!string.IsNullOrEmpty(remove.StdOut))
				detail = string.IsNullOrEmpty(detail) ? remove.StdOut : detail + "\n" + remove.StdOut;
			_transport.Error(sessionId, "Failed to remove the worktree:\n" +
				(string.IsNullOrWhiteSpace(detail) ? "(no output)" : detail));
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