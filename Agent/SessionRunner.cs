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
    private Session _currentSession = null!;

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

    // Set by the always-available Answer tool's handler when the model opts to transition. Read and
    // cleared by RunAsync after each turn; non-null _pendingTruth means a transition is pending.
    private string? _pendingTruth;
    private string? _pendingAnswer;

    private string _clientSessionId = string.Empty;
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
        _subagent = new SubagentRunner(registry, roleService, transport, () => _currentSession);
    }

    // Routes inbound input to the session tree by ID.
    public void Deliver(string targetId, string text) => _currentSession.Deliver(targetId, text);

    public string ActiveSessionId => _currentSession.Id;
    public Session CurrentSession => _currentSession;

    // Runs until cancelled. Compaction is handled inline: when context fills the current session
    // is summarized, a child session is created and announced, and the loop continues on the new session.
    public async Task RunAsync(CancellationToken ct)
    {
        Session session = _currentSession;

        SendStats(session, _service?.Model.Config.ContextWindow ?? 0);

        _cachedSessions = SessionService.List();
        string lastCompletionCandidates = JsonSerializer.Serialize(BuildCompletionCandidates(session));
        _transport.Completions(session.Id, lastCompletionCandidates);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1. Drain all pending commands from the session queue.
                session = await DrainInputAsync(session);
                _currentSession = session;

                // 2. Resolve role and refresh service after drain (commands may have changed role or model).
                Role? role = _roleService.GetRole(session.Role);
                RefreshService(role, session);
                LlmService? service = _service;

                if (service != null)
                {
                    // Send history to the client whenever the active session changes.
                    if (session.Id != _clientSessionId)
                    {
                        _clientSessionId = session.Id;
                        _transport.Clear(session.Id);
                        session.ReplayToTransport();
                        SendStats(session, service.Model.Config.ContextWindow);
                        _transport.Status(session.Id, "ready");
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
                        _currentSession = compacted;
                        _service = null;  // compacted session starts fresh
                        // Drop the local too: its protocol still holds the old session's conversation.
                        // The next loop iteration rebuilds a fresh service for the compacted session.
                        service = null;
                    }
                }

                // 3. Run the LLM whenever the session has work.
                // NeedsAttention() returns false after an interrupt until AddUserMessage() is called.
                bool contextFull = false;
                if (session.NeedsAttention() && service != null && role != null)
                {
                    session.SendBusy();
                    try
                    {
                        Tool[] tools = GetOrBuildTools(role, session.IsSubagent);
                        LlmResult result = await TurnRunner.RunTurnAsync(session, service, tools, null, _settings.Settings.CompactionReserveTokens, 0, _transport, _cancellationTokenSource.Token);
                        SendStats(session, service.Model.Config.ContextWindow);

                        // A model-initiated Answer call records a pending transition. Apply it
                        // regardless of how the turn ended — the model opted to transition, so it
                        // takes precedence over context-full/failed handling for this turn.
                        if (_pendingTruth != null)
                        {
                            string truth = _pendingTruth;
                            string answer = _pendingAnswer ?? string.Empty;
                            _pendingTruth = null;
                            _pendingAnswer = null;

                            Session? advanced = await ApplyTransitionAsync(session, role, truth, answer, _cancellationTokenSource.Token);
                            if (advanced != null)
                            {
                                session = advanced;
                                _currentSession = session;
                                _service = null;  // new role may use a different model
                            }
                        }
                        else if (result.ExitReason == LlmExitReason.ContextFull)
                        {
                            contextFull = true;
                        }
                        else if (result.ExitReason == LlmExitReason.Failed)
                        {
                            _transport.Error(session.Id, result.ErrorMessage);
                        }
                        else if (result.ExitReason == LlmExitReason.Completed)
                        {
                            // The model finished without opting to transition. Confront it with the
                            // role's end-of-turn question and require an explicit Answer-tool call.
                            Session? advanced = await AdvanceRoleAsync(session, role, service, _cancellationTokenSource.Token);
                            if (advanced != null)
                            {
                                session = advanced;
                                _currentSession = session;
                                _service = null;  // new role may use a different model
                            }
                        }
                        // Interrupted: _interruptedAndWaiting is set in EndTurn;
                        // NeedsAttention() returns false until AddUserMessage() is called with new input.
                    }
                    finally
                    {
                        session.SendIdle();
                    }
                }

                if (contextFull)
                {
                    Session? compacted = await CompactAsync(ct);
                    if (compacted == null)
                        break;
                    session = compacted;
                    _currentSession = compacted;
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
                    try { await waitTask; } catch (OperationCanceledException) { }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!_currentSession.Ephemeral)
                SessionService.Save(_currentSession.Data);
        }
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
        string? summary = await TurnRunner.SummarizeAsync(_currentSession, role.SummaryPrompt, Array.Empty<Tool>(), _registry, _roleService, _transport, ct);

        if (string.IsNullOrWhiteSpace(summary))
        {
            _transport.Status(_currentSession.Id, "[Compaction] Failed.");
            return null;
        }

        string newDisplayName = Session.IncrementDisplayName(_currentSession.DisplayName);
        // Allocate the child ID before saving so the updated ChildCounter is persisted.
        string newSessionId = _currentSession.AllocateChildId();

        SessionService.Save(_currentSession.Data);

        // Compaction creates a fresh session (no history), not a Fork.
        // Fork = deep copy from a branch point; compaction = clean slate seeded with the summary.
        BeastSession freshData = new BeastSession(newSessionId, newDisplayName, _currentSession.Model, _currentSession.Role, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, _currentSession.Ephemeral, 0);

        Session newSession = new Session(freshData, role.SystemPrompt, _transport, false);
        newSession.AddUserMessage(summary);
        newSession.FlushPendingMessages();
        newSession.ReplayExchanges(tailExchanges);

        _currentSession.AddChild(newSession);
        newSession.AnnounceToClient();

        if (!newSession.Ephemeral)
            SessionService.Save(newSession.Data);

        _cachedSessions = SessionService.List();
        _transport.Status(_currentSession.Id, "[Compaction] Complete.");
        return newSession;
    }

    // ---- Session management ----

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
                            SessionService.Save(session.Data);
                        session = CreateFreshSession(session.Role, false);
                        _service = null;
                        _cachedSessions = SessionService.List();
                        _transport.Status(session.Id, "New session started.");
                    }
                    else if (args == "none")
                    {
                        if (!session.Ephemeral)
                            SessionService.Save(session.Data);
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
                                SessionService.Save(session.Data);
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
                case "clear":
                    session.Clear();
                    _registry.ResetAllAvailability();
                    _transport.Status(session.Id, "Session cleared.");
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
                            session.UpdateModel(args);
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
            "/compact", "/clear", "/reload", "/role", "/model",
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
            if (value == currentValue) continue;
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
    // (different role). If the role defines no EndOfTurnPrompt + Truths, there is nothing to confront.
    private async Task<Session?> AdvanceRoleAsync(Session session, Role currentRole, LlmService service, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(currentRole.EndOfTurnPrompt) || currentRole.Truths.Count == 0)
            return null;

        string? selectedTruth = null;
        string? selectedAnswer = null;
        Tool answerTool = BuildAnswerTool(currentRole.EndOfTurnPrompt, currentRole.Truths.Keys, (truth, answer) => { selectedTruth = truth; selectedAnswer = answer; });

        // Run the confrontation in the current session with only the Answer tool, forcing the model to
        // call it, and reusing the session's service so the conversation continues uninterrupted. Retry
        // on a bad answer (the tool rejects an answer that does not begin with a truth statement).
        session.AddUserMessage(currentRole.EndOfTurnPrompt);

        // Cap the answer to whatever space remains in the window so the briefing always fits.
        int answerCap = session.Budget.MaxCompletionTokens() ?? 0;
        LlmResult evalResult = await TurnRunner.RunTurnAsync(session, service, new Tool[] { answerTool }, "Answer", 0, answerCap, _transport, ct);
        if (selectedTruth == null)
        {
            _transport.Error(session.Id, "[Role] Completed session failed to call Answer tool; staying in current role.");
            return null;
        }

        return CreateNextRoleSession(session, currentRole, selectedTruth, selectedAnswer!);
    }

    // Maps the chosen truth label to the next role and returns the successor session. The Answer tool
    // handler only accepts answers beginning with one of the Truths keys, so the lookup always hits.
    // Empty next-role = task finished; returns null and control goes back to the user. Same role =
    // continue in place: the answer becomes the next user prompt and null is returned so the caller
    // keeps the current session. Different role = save the current session and start a fresh one whose
    // first user prompt is the model's full answer.
    private Session? CreateNextRoleSession(Session session, Role currentRole, string selectedTruth, string selectedAnswer)
    {
        currentRole.Truths.TryGetValue(selectedTruth, out string? nextRoleName);

        if (string.IsNullOrEmpty(nextRoleName))
        {
            _transport.Status(session.Id, $"[Role] {selectedTruth} → done.");
            return null;
        }

        Role nextRole = _roleService.GetRole(nextRoleName) ?? currentRole;

        // Same role: stay in the current conversation. The briefing becomes the next user prompt so the
        // model keeps working in place; no new session is created and the caller keeps this session.
        if (string.Equals(nextRole.Name, currentRole.Name, StringComparison.OrdinalIgnoreCase))
        {
            session.AddUserMessage(selectedAnswer);
            _transport.Status(session.Id, $"[Role] {selectedTruth} → continue ({currentRole.Name}).");
            return null;
        }

        if (!session.Ephemeral)
            SessionService.Save(session.Data);

        BeastSession freshData = new BeastSession(session.AllocateChildId(), session.DisplayName, session.Model, nextRole.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, session.Ephemeral, 0);
        Session next = new Session(freshData, nextRole.SystemPrompt, _transport, false);

        next.AddUserMessage(selectedAnswer);

        _transport.Status(session.Id, $"[Role] {selectedTruth} → {nextRole.Name}");
        return next;
    }

    // Builds the always-available Answer tool. Its description is the role's EndOfTurnPrompt, so the
    // model knows the question that, once answerable, lets it transition at any point in a turn.
    // The answer must begin with one of the truth statements verbatim and continues with a briefing
    // for the next phase; onAnswer receives (matched statement, full answer). A non-matching answer
    // returns an error result so the model can correct itself within the same turn.
    private static Tool BuildAnswerTool(string endOfTurnPrompt, IEnumerable<string> truthLabels, Action<string, string> onAnswer)
    {
        List<string> labels = new List<string>(truthLabels);
        string optionList = string.Join("\n", labels);

        JsonObject answerProp = new JsonObject();
        answerProp["type"] = "string";
        answerProp["description"] = $"Your full answer. It must begin with exactly one of these statements, verbatim: {optionList}\n\nAfter the statement, continue with the context the next phase needs to start working: what has been accomplished, what remains, and any constraints or decisions it must know about. The entire answer becomes the next phase's first prompt.";

        JsonObject properties = new JsonObject();
        properties["answer"] = answerProp;

        JsonArray required = new JsonArray();
        required.Add(JsonValue.Create("answer"));

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
                    Name = "Answer",
                    Description = $"{endOfTurnPrompt} Call this tool the moment that can be answered to record your determination and move to the next phase — you need not finish anything else first. Begin your answer with exactly one of the provided statements verbatim, then continue with a briefing that gives the next phase its goal and context.",
                    Parameters = parameters
                }
            },
            Handler = (JsonObject args, CancellationToken ct, ITransportServer transport, string sessionId, int maxOutputTokens) =>
            {
                string answer = (args["answer"]?.GetValue<string>() ?? string.Empty).TrimStart();

                // Longest prefix match so one statement being a prefix of another resolves correctly.
                string? matched = null;
                foreach (string label in labels)
                {
                    if (answer.StartsWith(label, StringComparison.OrdinalIgnoreCase) && (matched == null || label.Length > matched.Length))
                        matched = label;
                }

                ToolResult result;
                if (matched != null)
                {
                    onAnswer(matched, answer);
                    result = new ToolResult("Answer recorded.", string.Empty, 0);
                }
                else
                {
                    result = new ToolResult(string.Empty, $"Your answer must begin with exactly one of these statements, verbatim: {optionList}", 1);
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
        if (!isSubagent && !string.IsNullOrEmpty(role.EndOfTurnPrompt) && role.Truths.Count > 0)
        {
            Tool answerTool = BuildAnswerTool(role.EndOfTurnPrompt, role.Truths.Keys, (truth, answer) =>
            {
                _pendingTruth = truth;
                _pendingAnswer = answer;
            });
            toolList.Add(answerTool);
        }

        Tool[] tools = toolList.ToArray();
        _toolsByRole[key] = tools;
        return tools;
    }

    // Replaces _service when the model or role has changed, or when the service has permanently
    // failed. Also updates session.Model if the registry selected a different model as fallback.
    private void RefreshService(Role? role, Session session)
    {
        if (_service != null && !_service.IsDown && _service.Model.ConfigId == session.Model)
            return;

        int minCtx = session.ContextLength + _settings.Settings.CompactionReserveTokens;
        _service = _registry.CreateService(role, session.Model, minCtx);

        if (_service != null && _service.Model.ConfigId != session.Model)
        {
            session.UpdateModel(_service.Model.ConfigId);
            SendStats(session, _service.Model.Config.ContextWindow);
        }
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

    private void SendStats(Session session, int maxContext)
    {
        string json = JsonSerializer.Serialize(new
        {
            model = session.Model,
            role = session.Role,
            promptTokens = session.CumulativeInputTokens,
            completionTokens = session.CumulativeOutputTokens,
            totalCost = session.TotalCost,
            maxContext,
            contextTokens = session.ContextLength
        });
        _transport.Stats(session.Id, json);
    }
}
