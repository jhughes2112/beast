using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


// Beast app: starts the agent backend via IAgentContext and drives a session via the provided display.
public class BeastApp : IAsyncDisposable
{
	// Per-session conversation model and streaming state, keyed by session ID.
	internal class SessionState
	{
		public ConversationModel          Model { get; } = new ConversationModel();
		public int                        NextIndex;
		public int                        StreamIndex     = -1;
		public Dictionary<string, int>    StreamTagToSlot = new Dictionary<string, int>();
		public Dictionary<int, FrameType> SlotTypes       = new Dictionary<int, FrameType>();
		public Dictionary<FrameType, int> PendingCommit   = new Dictionary<FrameType, int>();

		// Tick when this session's current turn began (its Busy frame arrived). Drives the duration
		// shown in the separator so it reflects how long this session has been working, independent
		// of when the user started viewing it. 0 when the session is idle.
		public long BusyStartTick;

		// Last stats reported by the agent for this session. Pushed to the display whenever
		// this session becomes the actively viewed one.
		public string  StatsModel = "";
		public string  StatsRole  = "";
		public int     StatsPromptTokens;
		public int     StatsCompletionTokens;
		public decimal StatsTotalCost;
		public long    StatsWorkedMs;
		public int     StatsMaxContext;
		public int     StatsContextTokens;
		public int     StatsCachedTokens;

		// Termination status reported by the agent via SessionStatus frames. Defaults to Ongoing.
		public SessionStatus Status = SessionStatus.Ongoing;

		// Paused by the user, as reported by the agent. Drives the F10 row and its Play/Pause button.
		public bool Paused;

		// Stopped with a turn still to send, as reported by the agent. Whether Play is offered once idle.
		public bool CanContinue;
	}

	private readonly ILauncher      _agentContext;
	private readonly List<string>   _messages;
	private readonly IDisplay       _display;
	private readonly ClientLog      _log;
	private readonly string         _agentName;
	private readonly SessionRecord? _session;
	// beast -p: nobody is at the keyboard, and Beast quits once the prompt's work is done.
	private readonly bool _unattended;
	// Set when the session is finished (the agent's /finish, or Finish after the connection was lost); on
	// shutdown its container and its folder under the sessions folder are removed.
	private bool _sessionFinished;
	// Candidate sound paths; the first one that exists on this machine is played.
	private List<string> _idleSounds     = new List<string>();
	private List<string> _subagentSounds = new List<string>();

	private CancellationTokenSource? _cts;
	private ITransportClient?        _wsClient;

	// Runs the agent's host_command on this machine once HallMonitor has let it through.
	private HostCommandRunner? _hostRunner;

	// The exit question is on screen, so another Ctrl+C does not stack a second one.
	private volatile bool _exitAsked;

	// Set once Beast has asked the agent to quit, so its disconnect is expected.
	private volatile bool _quitting;

	// The agent disconnected without being asked to; see OnConnectionLost.
	private volatile bool _connectionLost;

	// Per-session models and streaming state.
	private readonly Dictionary<string, SessionState> _sessions = new Dictionary<string, SessionState>(StringComparer.Ordinal);
	private          string _activeSessionId = "";
	// The queued steering messages have gone to the first session the agent announced.
	private          bool            _queuedSent;
	private readonly HashSet<string> _busySessions = new HashSet<string>(StringComparer.Ordinal);
	// Display names announced by the agent via SessionAnnounce frames, keyed by session ID.
	private readonly Dictionary<string, string> _sessionDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal);

	// Read loop state
	private Task?                    _readTask;
	private CancellationTokenSource? _readCts;
	// Inbound frames queued by ReadLoop and drained under _consoleLock by DrainFrameQueue().
	private readonly System.Collections.Concurrent.ConcurrentQueue<(FrameType Type, string SessionId, string Content)> _frameQueue
		= new System.Collections.Concurrent.ConcurrentQueue<(FrameType, string, string)>();

	public BeastApp(ILauncher agentContext, List<string> messages, IDisplay display, ClientLog log, string agentName, SessionRecord? session, bool unattended = false)
	{
		_unattended   = unattended;
		_agentContext = agentContext;
		_messages     = messages;
		_display      = display;
		_log          = log;
		_agentName    = agentName;
		_session      = session;
	}

	// Leaving a worktree asks first: Finish runs /finish (which removes the worktree, its branch and the
	// session once the work is integrated, and otherwise says what is left and stays open); Stop quits and
	// keeps the session, stopped, with its worktree, conversations and agent container, to start again from
	// the session menu. Anywhere else, and once /finish has gone through, it just quits. With the agent gone
	// (the connection was lost) there is no one to tell, so it just closes.
	private void RequestGracefulExit()
	{
		bool inWorktree = _session != null && !_session.IsBare && !_sessionFinished;
		if (_connectionLost)
		{
			if (!_exitAsked)
				_cts?.Cancel();
		}
		else if (_wsClient == null)
		{
			// Still launching: there is no agent to ask yet, so abandon the launch.
			_cts?.Cancel();
		}
		else if (!inWorktree || _unattended)
		{
			_ = SendQuitAsync();
		}
		else if (!_exitAsked)
		{
			_exitAsked         = true;
			List<string> lines = new List<string>
			{
				$"You are leaving worktree '{_session!.Worktree}'.",
				string.Empty,
				"Finish: remove the worktree, its branch and this session, once its work has been merged. If it has not, nothing is removed and Beast says what is left.",
				"Stop: keep the session, stopped, with its worktree and conversations, to start again from the session menu."
			};
			List<string> labels = new List<string> { "&Finish", "&Stop", "&Cancel" };
			_display.AskChoice("Exit", lines, labels, 1, 2, choice =>
			{
				_exitAsked = false;
				if (choice == 0)
					_ = _wsClient?.SendAsync(_activeSessionId + "|/finish", _cts!.Token);
				else if (choice == 1)
					_ = SendQuitAsync();
			});
		}
	}

	// The agent went away without being asked to (its container was stopped, from another Beast window's
	// session menu or by hand, or it crashed). Says so, and for a worktree session says what it holds and
	// offers to finish it here on the host, as /finish would, when its work is all committed and merged.
	private void OnConnectionLost()
	{
		_connectionLost         = true;
		_exitAsked              = true;
		bool         finishable = false;
		List<string> lines      = new List<string>
		{
			"The connection to the agent was lost: its container stopped, or the agent crashed.",
			string.Empty
		};
		if (_session != null && !_session.IsBare)
		{
			HostGit.WorktreeStatus status = HostGit.Status(_session);
			lines.AddRange(status.Describe(_session.Worktree));
			lines.Add(string.Empty);
			finishable = status.Finishable;
			lines.Add(finishable
				? "Finish: remove the worktree, its branch and this session, as /finish would. Exit: keep the session, stopped, to start again from the session menu."
				: "It cannot be finished yet: that needs every change committed and merged. The session is kept, stopped, to start again from the session menu.");
		}
		else
		{
			lines.Add("The session is kept, stopped, to start again from the session menu.");
		}

		List<string> labels = finishable ? new List<string> { "&Finish", "&Exit" } : new List<string> { "&Exit" };
		_display.AskChoice("Connection lost", lines, labels, labels.Count - 1, labels.Count - 1, choice =>
		{
			_exitAsked = false;
			if (finishable && choice == 0)
				FinishOnHost();
			_cts?.Cancel();
		});
	}

	// Finishes the worktree session without its agent: removes the checkout, its registration and its
	// merged branch from the repository; shutdown then removes the container and the session's folder.
	private void FinishOnHost()
	{
		(_, string error) = HostGit.RemoveWorktree(_session!);
		if (error.Length > 0)
			_log.Error($"Could not finish worktree '{_session!.Worktree}': {error}. The session is kept.");
		else
			_sessionFinished = true;
	}

	// Sends /quit to the agent so it saves the session, then waits for it to disconnect.
	// Falls back to hard cancel after a timeout.
	private async Task SendQuitAsync()
	{
		_quitting = true;
		try
		{
			if (_wsClient != null)
				await _wsClient.SendAsync(_activeSessionId + "|/quit", _cts!.Token);
		}
		catch { }

		if (_readTask != null)
			await Task.WhenAny(_readTask, Task.Delay(3000));

		_cts?.Cancel();
	}

	// Tells the agent which shells host_command may use on this machine. Sent as soon as the connection is up,
	// before any queued message can start a turn, so the first toolset already has them; the agent takes
	// /host- lines whatever session they name.
	private async Task SendHostShellsAsync()
	{
		if (_hostRunner != null)
			await _wsClient!.SendAsync("|/host-shells " + _hostRunner.ShellNames, _cts!.Token);
	}

	// Sends the command-line switches and prompt to the conversation the agent shows at startup, as if typed there.
	private async Task SendQueuedMessagesAsync(string sessionId)
	{
		foreach (string msg in _messages)
			await _wsClient!.SendAsync(sessionId + "|" + msg, _cts!.Token);
	}

	public async Task<int> Run()
	{
		_cts                    = new CancellationTokenSource();
		Console.CancelKeyPress += (sender, e) =>
		{
			e.Cancel = true;
			RequestGracefulExit();
		};

		// Create the root session placeholder and attach it.
		SessionState rootState = new SessionState();
		_display.Attach(rootState.Model);
		_display.SetSendAsync(text => _wsClient!.SendAsync(_activeSessionId + "|" + text, _cts!.Token));
		_display.SetSendToAsync((sessionId, text) => _wsClient!.SendAsync(sessionId + "|" + text, _cts!.Token));
		_display.SetRequestExit(RequestGracefulExit);
		_display.SetFrameDrain(DrainFrameQueue);
		_display.SetSessionSwitchCallback(SwitchActiveSession);
		_display.SetSessionDeleteCallback(DeleteSession);

		// Dropped files stage into the session's agent state folder (bound at /root/.beast-state), so
		// the agent can read them, the project stays clean, and they go when the session is deleted.
		// Without a session (a debug run against a native agent) that is the working folder's .beast,
		// which is where a native agent keeps its state.
		string stateDir = _session != null ? _session.StateDir : Path.Combine(Environment.CurrentDirectory, ".beast");
		_display.SetAgentStateDir(stateDir);

		// host_command runs in the folder bound to /workspace, so the agent's relative paths mean the
		// same on both sides.
		string workspaceRoot = _session != null ? _session.WorkspacePath : Environment.CurrentDirectory;
		_hostRunner          = new HostCommandRunner((sessionId, text) => _wsClient!.SendAsync(sessionId + "|" + text, _cts!.Token), workspaceRoot, _cts.Token);

		// Put the worktree (or agent) name in the console tab title so multiple Beast tabs are
		// distinguishable; the separator animates while the agent is busy.
		ConsoleChrome.Configure(_session == null ? _agentName : !_session.IsBare ? _session.Worktree : _session.Name.Length > 0 ? $"{Path.GetFileName(_session.Folder)} {_session.Name}" : Path.GetFileName(_session.Folder));

		// Load settings to pick up the optional notification sound paths.
		try
		{
			SettingsService settings = new SettingsService();
			_idleSounds              = settings.Settings.IdleSounds;
			_subagentSounds          = settings.Settings.SubagentSounds;
		}
		catch (ConfigException)
		{
			// SettingsService already printed a friendly parse error; bail before launching the agent.
			return 1;
		}

		int exitCode = 0;
		try
		{
			int hostPort = await _agentContext.StartAsync(_agentName, _cts.Token);

			_wsClient = await RetryConnectAsync($"ws://localhost:{hostPort}/", _agentContext, _log, _cts.Token);

			await SendHostShellsAsync();

			_readCts  = new CancellationTokenSource();
			_readTask = ReadLoop(_wsClient, _readCts.Token);

			await _display.RunAsync(_cts.Token);
		}
		catch (OperationCanceledException) when (_wsClient == null)
		{
			// Ctrl+C during the launch: leave quietly, on the normal screen.
			_display.RestoreTerminal();
			exitCode = 1;
		}
		catch (Exception ex)
		{
			// The worktree chooser may have left its alt screen up (showing "Launching Sandbox…") for the
			// live display to take over. If we failed before that happened, the display never restored the
			// terminal — do it here so the error prints on the normal screen instead of a stranded alt buffer.
			_display.RestoreTerminal();
			_log.Error($"[beast] Error: {ex}");
			exitCode = 1;
		}

		return exitCode;
	}

	private async Task ReadLoop(ITransportClient wsClient, CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				string? message = await wsClient.ReceiveAsync(token);
				if (message == null)
					break;

				(FrameType type, string sessionId, string content) = ParseFrame(message);
				_frameQueue.Enqueue((type, sessionId, content));
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			_display.SetStatus($"[read error] {ex.Message}");
		}

		// Agent connection is gone — stop the busy animation and any open stream. Expected after /quit; otherwise
		// the user is told and asked what to do before Beast closes.
		_display.OnStreamEnd();
		_display.SetAgentBusy(false, 0);
		if (_quitting || _sessionFinished || token.IsCancellationRequested || _cts == null || _cts.IsCancellationRequested)
		{
			_display.SetStatus("Agent disconnected.");
			_cts?.Cancel();
		}
		else
		{
			_display.SetStatus("Connection to the agent lost.");
			OnConnectionLost();
		}
	}

	// Drains all queued inbound frames. Must be called under the display's lock.
	public void DrainFrameQueue()
	{
		while (_frameQueue.TryDequeue(out (FrameType Type, string SessionId, string Content) frame))
			ProcessFrame(frame.Type, frame.SessionId, frame.Content);
	}

	// The kind of block a stream with this tag fills.
	private static FrameType StreamFrameType(string tag)
	{
		FrameType type = FrameType.Output;
		if (tag == StreamTag.Thinking)
			type = FrameType.Thinking;
		else if (tag == StreamTag.Tool)
			type = FrameType.Tool;
		else if (tag == StreamTag.Host)
			type = FrameType.Host;
		return type;
	}

	// Wire format: N|sessionId|content (sessionId may be empty for orchestrator-level frames).
	private static (FrameType Type, string SessionId, string Content) ParseFrame(string message)
	{
		string[] parts = message.Split('|', 3);
		if (parts.Length == 1 || !byte.TryParse(parts[0], out byte typeByte))
			return (FrameType.Output, string.Empty, message);

		// Old single-pipe format (no session ID) — backward compatibility.
		if (parts.Length == 2)
			return ((FrameType)typeByte, string.Empty, parts[1]);

		return ((FrameType)typeByte, parts[1], parts[2]);
	}

	// Returns the SessionState for the given ID, creating it if new.
	// Announces new sessions to the display.
	private SessionState EnsureSession(string sessionId)
	{
		if (_sessions.TryGetValue(sessionId, out SessionState? existing))
			return existing;

		SessionState state   = new SessionState();
		_sessions[sessionId] = state;

		if (string.IsNullOrEmpty(_activeSessionId))
		{
			_activeSessionId = sessionId;
			_display.Attach(state.Model);
		}

		NotifySessionList();
		return state;
	}

	// Switches the actively displayed session. Called from the F10 overlay.
	private void SwitchActiveSession(string sessionId)
	{
		if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
			return;
		if (!_sessions.TryGetValue(sessionId, out SessionState? state))
			return;

		_activeSessionId = sessionId;
		// Once the restore is over, the conversation on screen is the one the next start shows.
		if (_queuedSent && _session != null)
		{
			_session.Focus = sessionId;
			try
			{ SessionStore.Save(_session); }
			catch (Exception)
			{
			}
		}
		_display.OnStreamEnd();
		_display.SetAgentBusy(_busySessions.Contains(sessionId), state.BusyStartTick);
		_display.SetActiveTime(state.StatsWorkedMs);
		_display.SetStatsInfo(state.StatsModel, state.StatsRole, state.StatsPromptTokens, state.StatsCompletionTokens, state.StatsTotalCost, state.StatsMaxContext,
state.StatsContextTokens, state.StatsCachedTokens);
		_display.Attach(state.Model);

		// A stream still running in the session now on screen must be shown as streaming again: without
		// this its block renders through the full per-frame rebuild, and can be collapsed, until it ends.
		if (state.StreamIndex >= 0 && state.SlotTypes.TryGetValue(state.StreamIndex, out FrameType streamType))
			_display.OnStreamStart(state.StreamIndex, streamType);

		NotifySessionList();
	}

	// Deletes a session and its whole descendant subtree from the client's memory and tells the agent to
	// drop the matching .json files from the session folder. Invoked from the F10 overlay (under the
	// display lock, like ProcessFrame). A still-running session (or one with a running descendant) is left
	// alone. Removal is the CLIENT'S job — it owns the session list and prunes it here; the agent is only
	// told to drop the files. Deleting the last root leaves nothing to show, and the agent answers that by
	// standing up a fresh session and asking for it with SessionActivate.
	private void DeleteSession(string sessionId)
	{
		if (string.IsNullOrEmpty(sessionId))
			return;
		if (!_sessions.ContainsKey(sessionId))
			return;

		if (!SessionTree.IsSubtreeIdle(sessionId, _busySessions))
			return;

		string rootId = SessionTree.GetRootSessionId(_sessions);
		if (string.IsNullOrEmpty(rootId) || _wsClient == null)
			return;
		SendDeleteCommandAsync(rootId, sessionId);

		if (string.Equals(sessionId, rootId, StringComparison.Ordinal))
		{
			// Root: remove its subtree locally and adopt another root when one remains. Only when
			// nothing remains does the agent start a fresh root and ask for it with SessionActivate;
			// the pruning has already happened here, which is why the agent never needs to order a wipe.
			(bool rootWasActive, string rootPrefix) = SessionTree.CollectSubtreeIds(sessionId, _sessions.Keys, _activeSessionId);
			SessionTree.RemoveSessionFromLists(rootPrefix, sessionId, _sessions, _busySessions, _sessionDisplayNames);
			string remaining = SessionTree.GetRootSessionId(_sessions);
			if (!string.IsNullOrEmpty(remaining))
				UpdateDisplayAfterDelete(rootWasActive, remaining);
			return;
		}

		(bool wasActiveInSubtree, string subtreePrefix) = SessionTree.CollectSubtreeIds(sessionId, _sessions.Keys, _activeSessionId);
		SessionTree.RemoveSessionFromLists(subtreePrefix, sessionId, _sessions, _busySessions, _sessionDisplayNames);
		UpdateDisplayAfterDelete(wasActiveInSubtree, rootId);
	}

	// The session files live in the agent's folder, so the agent performs the disk delete recursively.
	// The command is routed through the root session — only its command queue is drained externally.
	private void SendDeleteCommandAsync(string rootId, string sessionId)
	{
		_ = _wsClient!.SendAsync(rootId + "|/delete-session " + sessionId, _cts!.Token);
	}

	// After removing a non-root session subtree, switch the active view if needed and refresh the list.
	private void UpdateDisplayAfterDelete(bool wasActiveInSubtree, string rootId)
	{
		if (wasActiveInSubtree)
			SwitchActiveSession(rootId);
		else
			NotifySessionList();
	}

	// Pushes the current session list and counts to the display.
	private void NotifySessionList()
	{
		SessionTree.NotifySessionList(_sessions, _busySessions, _sessionDisplayNames, _activeSessionId, _display);
	}

	// Frames that represent live activity worth following to. Busy/Idle are plumbing, and StreamEnd
	// and SessionAnnounce carry no new viewable content, so none of them pull focus. StreamChunk
	// DOES: a session that began streaming before the user switched away emits only chunks, so
	// excluding them would leave focus unable to ever drift back to it. The FollowDelayMs gate in
	// IsAutoTrackSuppressed prevents per-chunk thrash between concurrent streams.
	// Conversation content, the only kind of frame that may pull the view to its session. Bookkeeping the agent
	// sends to every session at once (stats after a /config model change, status lines, queue snapshots, busy
	// and idle) must not, or the view and the F10 selection jump to whichever session heard first.
	private static bool IsMessageFrame(FrameType type)
	{
		bool content = type == FrameType.Output || type == FrameType.Error || type == FrameType.Tool || type == FrameType.ToolCall
			|| type == FrameType.ToolResponse || type == FrameType.Thinking || type == FrameType.System || type == FrameType.StreamStart
			|| type == FrameType.StreamChunk || type == FrameType.User || type == FrameType.Alert;
		return content;
	}

	private void ProcessFrame(FrameType type, string sessionId, string content)
	{
		bool isActive = string.IsNullOrEmpty(sessionId) || string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal);

		// Errors also surface inside the /config overlay while it is open (no-op otherwise):
		// a rejected /config command would otherwise render into the transcript hidden behind
		// the modal, leaving the picker waiting forever with no visible reason.
		if (type == FrameType.Error)
			_display.OnConfigError(content);

		// Global frames that don't route to a specific session model.
		switch (type)
		{
			case FrameType.Status:
				if (content == "worktree-finished")
				{
					// The agent detached and deleted its worktree/branch on /finish. Flag it so shutdown
					// removes the container and the session's folder, then drive the graceful exit (sends /quit, waits for
					// the agent to disconnect, tears the container down) — the agent does not exit itself.
					_sessionFinished = true;
					_display.SetStatus("Worktree finished — cleaning up.");
					RequestGracefulExit();
				}
				else
				{
					_display.SetStatus(content);
				}
				return;

			case FrameType.Debug:
				// Debug frames suppressed on the Beast side.
				return;

			case FrameType.Stats:
				try
				{
					using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(content);
					System.Text.Json.JsonElement root       = doc.RootElement;
					string                       model      = root.TryGetProperty("model",  out System.Text.Json.JsonElement m) ? m.GetString() ?? "" : "";
					string                       role       = root.TryGetProperty("role",  out System.Text.Json.JsonElement rl) ? rl.GetString() ?? "" : "";
					int     prompt        = root.TryGetProperty("promptTokens",       out System.Text.Json.JsonElement p) ? p.GetInt32() : 0;
					int     completion    = root.TryGetProperty("completionTokens",   out System.Text.Json.JsonElement c) ? c.GetInt32() : 0;
					decimal cost          = root.TryGetProperty("totalCost",         out System.Text.Json.JsonElement tc) ? tc.GetDecimal() : 0m;
					int     maxContext    = root.TryGetProperty("maxContext",        out System.Text.Json.JsonElement mc) ? mc.GetInt32() : 0;
					int     contextTokens = root.TryGetProperty("contextTokens",     out System.Text.Json.JsonElement ct) ? ct.GetInt32() : 0;
					int     cachedTokens  = root.TryGetProperty("cachedTokens",     out System.Text.Json.JsonElement ct2) ? ct2.GetInt32() : 0;
					long    workedMs      = root.TryGetProperty("workedMs",          out System.Text.Json.JsonElement wm) ? wm.GetInt64() : 0;

					// Stats are session-scoped: store them on the session they belong to so switching
					// sessions in the F10 overlay shows that session's own model/role/token counts.
					string       statsId               = string.IsNullOrEmpty(sessionId) ? _activeSessionId : sessionId;
					SessionState statsSession          = EnsureSession(statsId);
					statsSession.StatsModel            = model;
					statsSession.StatsRole             = role;
					statsSession.StatsPromptTokens     = prompt;
					statsSession.StatsCompletionTokens = completion;
					statsSession.StatsTotalCost        = cost;
					statsSession.StatsMaxContext       = maxContext;
					statsSession.StatsContextTokens    = contextTokens;
					statsSession.StatsCachedTokens     = cachedTokens;
					statsSession.StatsWorkedMs         = workedMs;

					if (string.Equals(statsId, _activeSessionId, StringComparison.Ordinal))
					{
						_display.SetStatsInfo(model, role, prompt, completion, cost, maxContext, contextTokens, cachedTokens);
						_display.SetActiveTime(workedMs);
					}
				}
				catch { }
				return;

			case FrameType.Completions:
				try
				{
					string[]? completions = JsonSerializer.Deserialize<string[]>(content);
					_display.SetCompletions(completions ?? Array.Empty<string>());
				}
				catch
				{
					_display.SetCompletions(Array.Empty<string>());
				}
				return;

			case FrameType.SessionActivate:
				// The agent stood up a session it wants on screen — a compaction successor, or a fresh
				// root after the active one was deleted. Everything else the client knows is KEPT: the
				// client owns its own session list, and it removes sessions when the user deletes them,
				// which it already does locally before the agent ever hears about it. This frame used
				// to clear that list, which quietly erased every other session — sibling subagents,
				// earlier compaction predecessors — that were still alive in the agent.
				// The first one ends the restore: every conversation is known now, so the one last on screen is shown
				// when it is still here, else this one, and the command line goes to it.
				SessionState activated = EnsureSession(sessionId);
				activated.Status       = SessionStatus.Ongoing;
				if (!_queuedSent)
				{
					string focus = _session != null && _sessions.ContainsKey(_session.Focus) ? _session.Focus : sessionId;
					SwitchActiveSession(focus);
					_queuedSent = true;
					_           = SendQueuedMessagesAsync(focus);
				}
				else
				{
					SwitchActiveSession(sessionId);
				}
				return;

			case FrameType.SessionStatus:
				// Update the session's termination status and rebuild the F10 list so the color takes effect.
				if (!string.IsNullOrEmpty(sessionId))
				{
					SessionState st = EnsureSession(sessionId);
					if (Enum.TryParse<SessionStatus>(content, out SessionStatus parsed))
						st.Status = parsed;
				}
				NotifySessionList();
				return;

			case FrameType.SessionAnnounce:
				// Handled below to ensure session is created.
				break;
		}

		// Session-scoped frames: route to the appropriate SessionState.
		// Empty session ID means the frame came from the orchestrator (no specific session).
		// For those, use or create the active session.
		string       effectiveId = string.IsNullOrEmpty(sessionId) ? _activeSessionId : sessionId;
		SessionState session;

		if (string.IsNullOrEmpty(effectiveId))
		{
			// First frame before any session is known — create a placeholder with no ID.
			session          = new SessionState();
			_sessions[""]    = session;
			_activeSessionId = "";
			_display.Attach(session.Model);
		}
		else
		{
			session = EnsureSession(effectiveId);
		}

		isActive = string.Equals(effectiveId, _activeSessionId, StringComparison.Ordinal);

		// Auto-track: switch the display to whichever session most recently received message
		// content, unless the user is navigating the session overlay or reading scrolled-up history.
		if (!isActive && IsMessageFrame(type) && !_display.IsAutoTrackSuppressed())
		{
			SwitchActiveSession(effectiveId);
			isActive = true;
		}

		switch (type)
		{
			case FrameType.Busy:
				// Record the turn start on the first Busy of a turn so the duration reflects the
				// session's own working time, not when the user switched to view it.
				if (!_busySessions.Contains(effectiveId))
					session.BusyStartTick = Environment.TickCount64;
				_busySessions.Add(effectiveId);
				// Status is the agent's to report (SessionStatus frames), not inferred from busyness:
				// a busy session may be Working (owes its caller a reply) or plain Ongoing chat.
				// The separator busy animation reflects the viewed session only; the F10 overlay
				// indicators show every session's state independently via NotifySessionList.
				if (isActive)
					_display.SetAgentBusy(true, session.BusyStartTick);
				NotifySessionList();
				break;

			case FrameType.Idle:
				_busySessions.Remove(effectiveId);
				session.BusyStartTick = 0;
				if (isActive)
					_display.SetAgentBusy(false, 0);
				// Content "subagent" marks a sub-session completion; it gets its own sound.
				Platform.Current.PlaySound(content == "subagent" ? _subagentSounds : _idleSounds);
				NotifySessionList();
				// Unattended, the run is over when nothing is working any more.
				if (_unattended && _busySessions.Count == 0)
					RequestGracefulExit();
				break;

			case FrameType.StreamStart:
				FrameType startType                    = StreamFrameType(content);
				session.StreamIndex                    = session.NextIndex++;
				session.StreamTagToSlot[content]       = session.StreamIndex;
				session.SlotTypes[session.StreamIndex] = startType;
				session.Model.Update(session.StreamIndex, startType, "");
				if (isActive)
					_display.OnStreamStart(session.StreamIndex, startType);
				break;

			case FrameType.StreamChunk:
				if (session.StreamIndex < 0)
					break;
				// Appended, never re-sent whole: rebuilding the full text per token copied the message on every
				// chunk, and past ~85 KB each copy landed on the large-object heap.
				session.Model.Append(session.StreamIndex, content);
				if (isActive)
					_display.OnStreamChunk(content);
				break;

			case FrameType.StreamEnd:
				FrameType endType = StreamFrameType(content);
				if (session.StreamTagToSlot.TryGetValue(content, out int endSlot))
				{
					// A finished host command's live block goes away: its tool call above now holds the
					// output, under the same HOST: header. Hidden the way a paired ToolResponse slot is.
					if (endType == FrameType.Host)
						session.Model.Update(endSlot, FrameType.Debug, string.Empty);
					else
						session.PendingCommit[endType] = endSlot;
					session.StreamTagToSlot.Remove(content);
				}
				session.StreamIndex = -1;
				if (isActive)
					_display.OnStreamEnd();
				break;

			case FrameType.User:
				session.Model.Update(session.NextIndex++, FrameType.User, content);
				break;

			case FrameType.PendingQueue:
				// Agent sent the current queue snapshot — rebuild the ghost to match exactly.
				string[] pendingLines = string.IsNullOrEmpty(content)
					? Array.Empty<string>()
					: content.Split('\n');
				_display.SetPendingGhost(effectiveId, pendingLines);
				break;

			case FrameType.ToolCall:
				session.Model.Update(session.NextIndex++, FrameType.ToolCall, content);
				break;

			case FrameType.Config:
				// The agent's report of whether a session is paused and whether Play could continue it; otherwise a
				// host_command to run or stop here, or a /config flow payload for the picker overlay or a popup.
				// Never rendered in the transcript.
				if (content.Contains("\"kind\":\"session-state\"", StringComparison.Ordinal))
				{
					session.Paused      = content.Contains("\"paused\":true",      StringComparison.Ordinal);
					session.CanContinue = content.Contains("\"canContinue\":true", StringComparison.Ordinal);
					NotifySessionList();
				}
				else if (_hostRunner == null || !_hostRunner.OnConfigFrame(effectiveId, content))
				{
					_display.OnConfigFrame(content);
				}
				break;

			case FrameType.SessionAnnounce:
				try
				{
					using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(content);
					if (doc.RootElement.TryGetProperty("name", out System.Text.Json.JsonElement nameEl))
					{
						string announcedName = nameEl.GetString() ?? "";
						if (!string.IsNullOrEmpty(announcedName) && !string.IsNullOrEmpty(effectiveId))
							_sessionDisplayNames[effectiveId] = announcedName;
					}
				}
				catch { }
				NotifySessionList();
				// Auto-switch when a compacted successor is announced. Sub-sessions are announced
				// while the parent is still busy, so the busy check skips them correctly.
				if (!string.IsNullOrEmpty(effectiveId) && !string.IsNullOrEmpty(_activeSessionId))
				{
					bool isDirectChild = string.Equals(SessionTree.GetParentId(effectiveId), _activeSessionId, StringComparison.Ordinal);
					if (isDirectChild && !_busySessions.Contains(_activeSessionId))
						SwitchActiveSession(effectiveId);
				}
				break;

			default:
				// Reuse the stream slot for the committed frame that immediately follows StreamEnd.
				int slotIndex;
				if (session.PendingCommit.TryGetValue(type, out int pendingSlot))
				{
					slotIndex = pendingSlot;
					session.PendingCommit.Remove(type);
				}
				else if (type == FrameType.ToolCall && session.PendingCommit.TryGetValue(FrameType.Output, out int orphanedSlot))
				{
					// A turn whose whole output was a literal <tool_call> block streamed that raw text
					// into an Output slot, then committed with the text stripped and the call salvaged —
					// so no Output frame ever came to overwrite it and the raw XML sat there looking
					// unprocessed. The call it became claims the slot instead.
					slotIndex = orphanedSlot;
					session.PendingCommit.Remove(FrameType.Output);
				}
				else
				{
					slotIndex = session.NextIndex++;
				}
				session.Model.Update(slotIndex, type, content);
				break;
		}
	}

	// Retries WebSocket connection until success or cancellation, with 200ms delays.
	private static async Task<ITransportClient> RetryConnectAsync(string url, ILauncher launcher, ClientLog log, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TransportClientWebsocket wsClient = new TransportClientWebsocket(url, log);
			try
			{
				await wsClient.ConnectAsync(cancellationToken);
				return wsClient;
			}
			catch (OperationCanceledException)
			{
				wsClient.Dispose();
				throw;
			}
			catch (Exception)
			{
				wsClient.Dispose();

				// The WS server only comes up once the agent is running. If the backend has already
				// exited (e.g. crashed on bad roles.json/settings.json), retrying is pointless. Carry the
				// container log in the thrown exception so Run() can print it after restoring the terminal,
				// instead of painting it onto the worktree menu's alt screen where it would be lost.
				if (!await launcher.IsAliveAsync())
				{
					string containerLog = await launcher.GetLogsAsync(cancellationToken);
					string body         = string.IsNullOrWhiteSpace(containerLog) ? "(no output captured)" : containerLog.TrimEnd();
					throw new InvalidOperationException(
						"Agent backend exited before its server started. Container log follows:\n" + body);
				}

				await Task.Delay(200, cancellationToken);
			}
		}
		throw new OperationCanceledException(cancellationToken);
	}

	public async ValueTask DisposeAsync()
	{
		_readCts?.Cancel();

		if (_readTask != null)
		{
			try
			{
				await _readTask.WaitAsync(TimeSpan.FromSeconds(2));
			}
			catch { }
		}

		_readCts?.Dispose();
		_wsClient?.Dispose();

		// Tell the sandbox to shut down, but don't block the user's exit on the container actually stopping.
		// The /quit sent during graceful exit already makes the agent exit (so the container is usually
		// stopping on its own). A worktree session's container is kept, stopped, for the session to start again;
		// a finished session's and a bare session's are removed (see RemoveContainer). Cap the wait so closing
		// stays snappy — the stop request reaches the Docker daemon well within the cap, and it finishes the job
		// independently of this process. Removing a container gets longer, since the remove request only goes
		// out once the stop has finished, so it is not left behind.
		Task stop = StopSandboxAsync();
		await Task.WhenAny(stop, Task.Delay(RemoveContainer ? 20000 : 2000));

		// A finished session is gone: its folder under the sessions folder (record, emptied worktree folder,
		// conversations and logs) goes too, so the session menu no longer lists it.
		if (_sessionFinished && _session != null)
		{
			string error = SessionStore.DeleteDir(_session);
			if (error.Length > 0)
				_log.Error($"Could not remove the finished session's folder {_session.Dir}: {error}");
		}

		_cts?.Dispose();
	}

	// Whether the container goes on exit: after /finish, and always for a bare session. A bare session works on
	// the folder in place, so its container holds nothing of value (installed packages, /tmp); its conversations
	// and logs live in the session's own folder on the host and stay, and the next launch makes a fresh container.
	private bool RemoveContainer => _sessionFinished || (_session != null && _session.IsBare);

	// Stops the container (removing it after /finish or for a bare session), then disposes the launcher. Run
	// detached from the exit path so a slow or stuck container cannot hold up closing Beast; errors are
	// swallowed since we are tearing down anyway.
	private async Task StopSandboxAsync()
	{
		try
		{ await _agentContext.StopAsync(RemoveContainer); }
		catch { }
		_agentContext.Dispose();
	}
}