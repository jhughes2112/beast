using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


// Beast app: starts the agent backend via IAgentContext and drives a session via the provided display.
public class BeastApp : IAsyncDisposable
{
	// Per-session conversation model and streaming state, keyed by session ID.
	internal class SessionState
	{
		public ConversationModel Model { get; } = new ConversationModel();
		public int NextIndex;
		public int StreamIndex = -1;
		public string StreamContent = "";
		public Dictionary<string, int> StreamTagToSlot = new Dictionary<string, int>();
		public Dictionary<int, FrameType> SlotTypes = new Dictionary<int, FrameType>();
		public Dictionary<FrameType, int> PendingCommit = new Dictionary<FrameType, int>();

		// Tick when this session's current turn began (its Busy frame arrived). Drives the duration
		// shown in the separator so it reflects how long this session has been working, independent
		// of when the user started viewing it. 0 when the session is idle.
		public long BusyStartTick;

		// Last stats reported by the agent for this session. Pushed to the display whenever
		// this session becomes the actively viewed one.
		public string StatsModel = "";
		public string StatsRole = "";
		public int StatsPromptTokens;
		public int StatsCompletionTokens;
		public decimal StatsTotalCost;
		public int StatsMaxContext;
		public int StatsContextTokens;
		public int StatsCachedTokens;

		// Termination status reported by the agent via SessionStatus frames. Defaults to Ongoing.
		public SessionStatus Status = SessionStatus.Ongoing;
	}

	private readonly ILauncher _agentContext;
	private readonly List<string> _messages;
	private readonly IDisplay _display;
	private readonly ClientLog _log;
	private readonly string _agentName;
	private readonly Worktrees.Selection? _worktree;
	// Set when the agent reports a successful /finish; on shutdown the worktree's host folder is removed.
	private bool _worktreeFinished;
	private string? _idleSoundFile;
	private string? _subagentSoundFile;

	private CancellationTokenSource? _cts;
	private ITransportClient? _wsClient;

	// Per-session models and streaming state.
	private readonly Dictionary<string, SessionState> _sessions = new Dictionary<string, SessionState>(StringComparer.Ordinal);
	private string _activeSessionId = "";
	private readonly HashSet<string> _busySessions = new HashSet<string>(StringComparer.Ordinal);
	// Display names announced by the agent via SessionAnnounce frames, keyed by session ID.
	private readonly Dictionary<string, string> _sessionDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal);

	// Read loop state
	private Task? _readTask;
	private CancellationTokenSource? _readCts;
	// Inbound frames queued by ReadLoop and drained under _consoleLock by DrainFrameQueue().
	private readonly System.Collections.Concurrent.ConcurrentQueue<(FrameType Type, string SessionId, string Content)> _frameQueue
		= new System.Collections.Concurrent.ConcurrentQueue<(FrameType, string, string)>();

	public BeastApp(ILauncher agentContext, List<string> messages, IDisplay display, ClientLog log, string agentName, Worktrees.Selection? worktree)
	{
		_agentContext = agentContext;
		_messages = messages;
		_display = display;
		_log = log;
		_agentName = agentName;
		_worktree = worktree;
	}

	// Sends /quit to the agent so it saves the session, then waits for it to disconnect.
	// Falls back to hard cancel after a timeout.
	private void RequestGracefulExit()
	{
		_ = SendQuitAsync();
	}

	// Sends /quit to the agent so it saves the session, then waits for it to disconnect.
	// Falls back to hard cancel after a timeout.
	private async Task SendQuitAsync()
	{
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

	// Sends the queued steering messages to the agent's ready session.
	private async Task SendInitialMessagesAsync(string sessionId)
	{
		foreach (string msg in _messages)
			await _wsClient!.SendAsync(sessionId + "|" + msg, _cts!.Token);
	}

	public async Task<int> Run()
	{
		_cts = new CancellationTokenSource();
		Console.CancelKeyPress += (sender, e) =>
		{
			e.Cancel = true;
			RequestGracefulExit();
		};

		// Create the root session placeholder and attach it.
		SessionState rootState = new SessionState();
		_display.Attach(rootState.Model);
		_display.SetSendAsync(text => _wsClient!.SendAsync(_activeSessionId + "|" + text, _cts!.Token));
		_display.SetRequestExit(RequestGracefulExit);
		_display.SetFrameDrain(DrainFrameQueue);
		_display.SetSessionSwitchCallback(SwitchActiveSession);
		_display.SetSessionDeleteCallback(DeleteSession);

		// Put the worktree (or agent) name in the console tab title so multiple Beast tabs are
		// distinguishable; the separator animates while the agent is busy.
		ConsoleChrome.Configure(_worktree.HasValue ? _worktree.Value.Name : _agentName);

		// Load settings to pick up the optional notification sound paths.
		try
		{
			SettingsService settings = new SettingsService(Directory.GetCurrentDirectory());
			_idleSoundFile = settings.Settings.IdleSoundFile;
			_subagentSoundFile = settings.Settings.SubagentSoundFile;
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

			_readCts = new CancellationTokenSource();
			_readTask = ReadLoop(_wsClient, _readCts.Token);

			await _display.RunAsync(_cts.Token);
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

		// Agent connection is gone — stop the busy animation and any open stream.
		_display.OnStreamEnd();
		_display.SetAgentBusy(false, 0);
		_display.SetStatus("Agent disconnected.");
		_cts?.Cancel();
	}

	// Drains all queued inbound frames. Must be called under the display's lock.
	public void DrainFrameQueue()
	{
		while (_frameQueue.TryDequeue(out (FrameType Type, string SessionId, string Content) frame))
			ProcessFrame(frame.Type, frame.SessionId, frame.Content);
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

		SessionState state = new SessionState();
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
		_display.OnStreamEnd();
		_display.SetAgentBusy(_busySessions.Contains(sessionId), state.BusyStartTick);
		_display.SetStatsInfo(state.StatsModel, state.StatsRole, state.StatsPromptTokens, state.StatsCompletionTokens, state.StatsTotalCost, state.StatsMaxContext,
state.StatsContextTokens, state.StatsCachedTokens);
		_display.Attach(state.Model);
		NotifySessionList();
	}

	// Deletes a session and its whole descendant subtree from the client's memory and tells the agent to
	// drop the matching .json files from the session folder. Invoked from the F10 overlay (under the
	// display lock, like ProcessFrame). A still-running session (or one with a running descendant) is left
	// alone. Deleting the active root tears the tree down and the agent starts a fresh session in its
	// place, announced back via SessionReset — so for the root we just send the command and let that frame
	// rebuild the client state.
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
			// Root: the agent replies with SessionReset for the new root, which clears and rebuilds the
			// client's session map and active view. Leave that to the frame so the two never disagree.
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
	private static bool IsMessageFrame(FrameType type)
	{
		bool plumbing = type == FrameType.Busy || type == FrameType.Idle || type == FrameType.StreamEnd || type == FrameType.SessionAnnounce;
		return !plumbing;
	}

	private void ProcessFrame(FrameType type, string sessionId, string content)
	{
		bool isActive = string.IsNullOrEmpty(sessionId) || string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal);

		// Global frames that don't route to a specific session model.
		switch (type)
		{
			case FrameType.Status:
				if (content == "ready")
				{
					string readySessionId = sessionId;
					_ = SendInitialMessagesAsync(readySessionId);
				}
				else if (content == "worktree-finished")
				{
					// The agent detached and deleted its worktree/branch on /finish. Flag it so shutdown
					// removes the now-empty host folder, then drive the graceful exit (sends /quit, waits for
					// the agent to disconnect, tears the container down) — the agent does not exit itself.
					_worktreeFinished = true;
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
					System.Text.Json.JsonElement root = doc.RootElement;
					string model = root.TryGetProperty("model", out System.Text.Json.JsonElement m) ? m.GetString() ?? "" : "";
					string role = root.TryGetProperty("role", out System.Text.Json.JsonElement rl) ? rl.GetString() ?? "" : "";
					int prompt = root.TryGetProperty("promptTokens", out System.Text.Json.JsonElement p) ? p.GetInt32() : 0;
					int completion = root.TryGetProperty("completionTokens", out System.Text.Json.JsonElement c) ? c.GetInt32() : 0;
					decimal cost = root.TryGetProperty("totalCost", out System.Text.Json.JsonElement tc) ? tc.GetDecimal() : 0m;
					int maxContext = root.TryGetProperty("maxContext", out System.Text.Json.JsonElement mc) ? mc.GetInt32() : 0;
					int contextTokens = root.TryGetProperty("contextTokens", out System.Text.Json.JsonElement ct) ? ct.GetInt32() : 0;
					int cachedTokens = root.TryGetProperty("cachedTokens", out System.Text.Json.JsonElement ct2) ? ct2.GetInt32() : 0;

					// Stats are session-scoped: store them on the session they belong to so switching
					// sessions in the F10 overlay shows that session's own model/role/token counts.
					string statsId = string.IsNullOrEmpty(sessionId) ? _activeSessionId : sessionId;
					SessionState statsSession = EnsureSession(statsId);
					statsSession.StatsModel = model;
					statsSession.StatsRole = role;
					statsSession.StatsPromptTokens = prompt;
					statsSession.StatsCompletionTokens = completion;
					statsSession.StatsTotalCost = cost;
					statsSession.StatsMaxContext = maxContext;
					statsSession.StatsContextTokens = contextTokens;
					statsSession.StatsCachedTokens = cachedTokens;

					if (string.Equals(statsId, _activeSessionId, StringComparison.Ordinal))
						_display.SetStatsInfo(model, role, prompt, completion, cost, maxContext, contextTokens, cachedTokens);
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

			case FrameType.SessionReset:
				// The agent started a fresh root (e.g. after the active root was deleted from the F10 tree).
				// Forget every session we knew so the F10 list and the active view collapse to just the new one.
				_sessions.Clear();
				_busySessions.Clear();
				_sessionDisplayNames.Clear();
				_activeSessionId = "";
				_display.OnStreamEnd();
				_display.SetAgentBusy(false, 0);
				SessionState reset = EnsureSession(sessionId);
				reset.Status = SessionStatus.Ongoing;
				_display.SetStatsInfo(reset.StatsModel, reset.StatsRole, 0, 0, 0m, 0, 0, 0);
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
		string effectiveId = string.IsNullOrEmpty(sessionId) ? _activeSessionId : sessionId;
		SessionState session;

		if (string.IsNullOrEmpty(effectiveId))
		{
			// First frame before any session is known — create a placeholder with no ID.
			session = new SessionState();
			_sessions[""] = session;
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
				session.Status = SessionStatus.Ongoing;
				// The separator busy animation reflects the viewed session only; the F10 overlay
				// dots show every session's busy state independently via NotifySessionList.
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
				PlaySound(content == "subagent" ? _subagentSoundFile : _idleSoundFile);
				NotifySessionList();
				break;

			case FrameType.StreamStart:
				FrameType startType = content == StreamTag.Thinking ? FrameType.Thinking : content == StreamTag.Tool ? FrameType.Tool : FrameType.Output;
				session.StreamContent = "";
				session.StreamIndex = session.NextIndex++;
				session.StreamTagToSlot[content] = session.StreamIndex;
				session.SlotTypes[session.StreamIndex] = startType;
				session.Model.Update(session.StreamIndex, startType, session.StreamContent);
				if (isActive)
					_display.OnStreamStart(session.StreamIndex, startType);
				break;

			case FrameType.StreamChunk:
				if (session.StreamIndex < 0)
					break;
				session.StreamContent += content;
				session.SlotTypes.TryGetValue(session.StreamIndex, out FrameType chunkType);
				session.Model.Update(session.StreamIndex, chunkType, session.StreamContent);
				if (isActive)
					_display.OnStreamChunk(content);
				break;

			case FrameType.StreamEnd:
				FrameType endType = content == StreamTag.Thinking ? FrameType.Thinking : content == StreamTag.Tool ? FrameType.Tool : FrameType.Output;
				if (session.StreamTagToSlot.TryGetValue(content, out int endSlot))
				{
					session.PendingCommit[endType] = endSlot;
					session.StreamTagToSlot.Remove(content);
				}
				session.StreamIndex = -1;
				session.StreamContent = "";
				if (isActive)
					_display.OnStreamEnd();
				break;

			case FrameType.User:
				session.Model.Update(session.NextIndex++, FrameType.User, content);
				// The agent echoing a user message back means this session's queued steering text was
				// consumed — clear its pending ghost (even if a different session is currently viewed).
				_display.ClearPendingGhost(effectiveId);
				break;

			case FrameType.ToolCall:
				session.Model.Update(session.NextIndex++, FrameType.ToolCall, content);
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
				else
				{
					slotIndex = session.NextIndex++;
				}
				session.Model.Update(slotIndex, type, content);
				break;
		}
	}

	// Plays the given sound file via NAudio, which supports WAV, MP3, and most other formats.
	// Playback is fire-and-forget on a threadpool thread; the player is disposed when playback stops.
	// Errors are silently swallowed — a missing or unplayable file is not fatal.
	private static void PlaySound(string? soundFile)
	{
		if (string.IsNullOrEmpty(soundFile))
			return;
		if (!File.Exists(soundFile))
			return;
		try
		{
			AudioFileReader reader = new AudioFileReader(soundFile);
			WaveOutEvent output = new WaveOutEvent();
			output.Init(reader);
			output.PlaybackStopped += (s, e) =>
			{
				output.Dispose();
				reader.Dispose();
			};
			output.Play();
		}
		catch { }
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
					string body = string.IsNullOrWhiteSpace(containerLog) ? "(no output captured)" : containerLog.TrimEnd();
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

		// Tell the sandbox to shut down, but don't block the user's exit on the container actually stopping
		// and being removed. The /quit sent during graceful exit already makes the agent exit (so the
		// container is usually stopping on its own); this stop+remove is best-effort cleanup, and anything
		// left behind is reaped on the next launch into the same worktree. Cap the wait so closing stays
		// snappy — the stop request reaches the Docker daemon well within the cap, and it finishes the job
		// independently of this process.
		Task stop = StopSandboxAsync();
		await Task.WhenAny(stop, Task.Delay(2000));

		// After /finish the container is gone and the worktree detached; remove its host folder so the
		// next launch's menu no longer lists it. Only on an explicit finish — ordinary exits keep it. Never
		// for an ephemeral run: there is no worktree folder, and Name there is the current folder's leaf.
		if (_worktreeFinished && _worktree != null && !_worktree.Value.Ephemeral)
			Worktrees.RemoveFolder(_worktree.Value.RepoCwd, _worktree.Value.Name);

		_cts?.Dispose();
	}

	// Stops and removes the container, then disposes the launcher. Run detached from the exit path so a slow
	// or stuck container cannot hold up closing Beast; errors are swallowed since we are tearing down anyway.
	private async Task StopSandboxAsync()
	{
		try
		{ await _agentContext.StopAsync(); }
		catch { }
		_agentContext.Dispose();
	}
}