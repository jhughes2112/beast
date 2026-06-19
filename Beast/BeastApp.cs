using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;


// Beast app: starts the agent backend via IAgentContext and drives a session via the provided display.
public class BeastApp : IDisposable, IAsyncDisposable
{
    // Per-session conversation model and streaming state, keyed by session ID.
    private class SessionState
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
    }

    private readonly ILauncher _agentContext;
    private readonly List<string> _messages;
    private readonly IDisplay _display;
    private readonly Log _log;
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

    public BeastApp(ILauncher agentContext, List<string> messages, IDisplay display, Log log, string agentName, Worktrees.Selection? worktree)
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
        async Task GracefulExitAsync()
        {
            try
            {
                if (_wsClient != null)
                    await _wsClient.SendAsync(_activeSessionId + "|/quit");
            }
            catch { }

            if (_readTask != null)
                await Task.WhenAny(_readTask, Task.Delay(3000));

            _cts?.Cancel();
        }
        _ = GracefulExitAsync();
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
        _display.SetSendAsync(text => _wsClient!.SendAsync(_activeSessionId + "|" + text));
        _display.SetRequestExit(RequestGracefulExit);
        _display.SetFrameDrain(DrainFrameQueue);
        _display.SetSessionSwitchCallback(SwitchActiveSession);
        _display.SetSessionDeleteCallback(DeleteSession);

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
            _log.Error($"[beast] Error: {ex.Message}");
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
                if (message == null) break;

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
        int pipe1 = message.IndexOf('|');
        if (pipe1 < 0 || !byte.TryParse(message.Substring(0, pipe1), out byte typeByte))
            return (FrameType.Output, string.Empty, message);

        int pipe2 = message.IndexOf('|', pipe1 + 1);
        if (pipe2 < 0)
        {
            // Old single-pipe format (no session ID) — backward compatibility.
            return ((FrameType)typeByte, string.Empty, message.Substring(pipe1 + 1));
        }

        string sessionId = message.Substring(pipe1 + 1, pipe2 - pipe1 - 1);
        string content = message.Substring(pipe2 + 1);
        return ((FrameType)typeByte, sessionId, content);
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
        if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)) return;
        if (!_sessions.TryGetValue(sessionId, out SessionState? state)) return;

        _activeSessionId = sessionId;
        _display.OnStreamEnd();
        _display.SetAgentBusy(_busySessions.Contains(sessionId), state.BusyStartTick);
        _display.SetStatsInfo(state.StatsModel, state.StatsRole, state.StatsPromptTokens, state.StatsCompletionTokens, state.StatsTotalCost, state.StatsMaxContext, state.StatsContextTokens);
        _display.Attach(state.Model);
        NotifySessionList();
    }

    // Deletes a subagent session from the client's memory and tells the agent to drop its file from
    // the session folder. Invoked from the F10 overlay (under the display lock, like ProcessFrame).
    // The root session is never deletable, and a still-running session is left alone.
    private void DeleteSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (GetParentId(sessionId) == null) return;
        if (!_sessions.ContainsKey(sessionId)) return;
        if (_busySessions.Contains(sessionId)) return;

        // The session files live in the agent's folder, so the agent performs the disk delete. The
        // command is routed through the root session — only its command queue is drained externally.
        string rootId = GetRootSessionId();
        if (!string.IsNullOrEmpty(rootId) && _wsClient != null)
            _ = _wsClient.SendAsync(rootId + "|/session delete " + sessionId);

        bool wasActive = string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal);
        _sessions.Remove(sessionId);
        _busySessions.Remove(sessionId);
        _sessionDisplayNames.Remove(sessionId);

        if (wasActive && !string.IsNullOrEmpty(rootId))
            SwitchActiveSession(rootId);
        else
            NotifySessionList();
    }

    // Returns the ID of the root session: the one whose parent is not itself a known session.
    private string GetRootSessionId()
    {
        foreach (string id in _sessions.Keys)
        {
            if (string.IsNullOrEmpty(id)) continue;
            string? parentId = GetParentId(id);
            if (parentId == null || !_sessions.ContainsKey(parentId))
                return id;
        }
        return "";
    }

    // Returns the parent session ID for a given session ID, or null if it is a root.
    // Parent-child relationship is encoded as "parentId_N" where N is a positive integer.
    private static string? GetParentId(string id)
    {
        int last = id.LastIndexOf('_');
        if (last < 0) return null;
        string suffix = id.Substring(last + 1);
        if (!int.TryParse(suffix, out _)) return null;
        return id.Substring(0, last);
    }

    // Appends this node and its children to result in DFS pre-order. Children are sorted by numeric
    // suffix descending so the most recently spawned agent lands directly under its parent — newest
    // activity stays at the top of the list instead of scrolling off the bottom.
    private static void DfsAdd(
        string id,
        int depth,
        Dictionary<string, List<string>> childrenMap,
        List<(string Id, int Depth)> result)
    {
        result.Add((id, depth));
        if (!childrenMap.TryGetValue(id, out List<string>? children)) return;
        children.Sort((a, b) =>
        {
            int lastA = a.LastIndexOf('_');
            int lastB = b.LastIndexOf('_');
            int numA = lastA >= 0 && int.TryParse(a.Substring(lastA + 1), out int nA) ? nA : 0;
            int numB = lastB >= 0 && int.TryParse(b.Substring(lastB + 1), out int nB) ? nB : 0;
            return numB.CompareTo(numA);
        });
        foreach (string child in children)
            DfsAdd(child, depth + 1, childrenMap, result);
    }

    // Pushes the current session list and counts to the display.
    private void NotifySessionList()
    {
        // Build parent→children map from IDs.
        Dictionary<string, List<string>> childrenMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HashSet<string> hasParent = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in _sessions.Keys)
        {
            if (string.IsNullOrEmpty(id)) continue;
            string? parentId = GetParentId(id);
            if (parentId != null && _sessions.ContainsKey(parentId))
            {
                if (!childrenMap.TryGetValue(parentId, out List<string>? kids))
                {
                    kids = new List<string>();
                    childrenMap[parentId] = kids;
                }
                kids.Add(id);
                hasParent.Add(id);
            }
        }

        // DFS from roots (sorted by key for deterministic order).
        List<string> roots = new List<string>();
        foreach (string id in _sessions.Keys)
        {
            if (!string.IsNullOrEmpty(id) && !hasParent.Contains(id))
                roots.Add(id);
        }
        roots.Sort(StringComparer.Ordinal);

        List<(string Id, int Depth)> ordered = new List<(string, int)>();
        foreach (string root in roots)
            DfsAdd(root, 0, childrenMap, ordered);

        List<SessionDisplayInfo> list = new List<SessionDisplayInfo>();
        foreach ((string id, int depth) in ordered)
        {
            string name = _sessionDisplayNames.TryGetValue(id, out string? announced) ? announced : id;
            list.Add(new SessionDisplayInfo(id, name, _busySessions.Contains(id), depth));
        }

        _display.SetSessionList(list, _activeSessionId);
        _display.SetSessionCounts(_busySessions.Count, _sessions.Count);
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
                    async Task SendMessagesAsync()
                    {
                        foreach (string msg in _messages)
                            await _wsClient!.SendAsync(readySessionId + "|" + msg);
                    }
                    _ = SendMessagesAsync();
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

                    if (string.Equals(statsId, _activeSessionId, StringComparison.Ordinal))
                        _display.SetStatsInfo(model, role, prompt, completion, cost, maxContext, contextTokens);
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
                // The separator busy animation reflects the viewed session only; the F10 overlay
                // dots show every session's busy state independently via NotifySessionList.
                if (isActive) _display.SetAgentBusy(true, session.BusyStartTick);
                NotifySessionList();
                break;

            case FrameType.Idle:
                _busySessions.Remove(effectiveId);
                session.BusyStartTick = 0;
                if (isActive) _display.SetAgentBusy(false, 0);
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
                if (session.StreamIndex < 0) break;
                session.StreamContent += content;
                session.SlotTypes.TryGetValue(session.StreamIndex, out FrameType chunkType);
                session.Model.Update(session.StreamIndex, chunkType, session.StreamContent);
                if (isActive) _display.OnStreamChunk(content);
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
                if (isActive) _display.OnStreamEnd();
                break;

            case FrameType.User:
                session.Model.Update(session.NextIndex++, FrameType.User, content);
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
                    bool isDirectChild = string.Equals(GetParentId(effectiveId), _activeSessionId, StringComparison.Ordinal);
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
        if (string.IsNullOrEmpty(soundFile)) return;
        if (!File.Exists(soundFile)) return;
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
    private static async Task<ITransportClient> RetryConnectAsync(string url, ILauncher launcher, Log log, CancellationToken cancellationToken)
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
                // exited (e.g. crashed on bad roles.json/settings.json), retrying is pointless — show
                // its log so the failure is visible, and bail.
                if (!await launcher.IsAliveAsync())
                {
                    string containerLog = await launcher.GetLogsAsync();
                    log.Error("[beast] Agent backend exited before its server started. Log follows:");
                    log.Error(string.IsNullOrWhiteSpace(containerLog) ? "(no output captured)" : containerLog.TrimEnd());
                    throw new InvalidOperationException("Agent backend failed to start.");
                }

                await Task.Delay(200, cancellationToken);
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }

    public void Dispose()
    {
        _readCts?.Cancel();
        _readTask?.Wait(2000);
        _readCts?.Dispose();
        _wsClient?.Dispose();
        _agentContext.Dispose();
        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        _readTask?.Wait(2000);
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
        // next launch's menu no longer lists it. Only on an explicit finish — ordinary exits keep it.
        if (_worktreeFinished && _worktree != null)
            Worktrees.RemoveFolder(_worktree.Value.RepoCwd, _worktree.Value.Name);

        _cts?.Dispose();
    }

    // Stops and removes the container, then disposes the launcher. Run detached from the exit path so a slow
    // or stuck container cannot hold up closing Beast; errors are swallowed since we are tearing down anyway.
    private async Task StopSandboxAsync()
    {
        try { await _agentContext.StopAsync(); } catch { }
        _agentContext.Dispose();
    }
}
