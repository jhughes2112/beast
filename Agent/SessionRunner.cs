using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Executes a session: LLM turns, tool dispatch (including sub-session spawning), and role transitions.
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

    private bool _wantsCompact = false;
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

        Role? initialRole = _roleService.GetRole(session.Role);
        LlmService? initialService = _registry.GetServiceForRole(initialRole, session.Model, session.ContextLength + _settings.Settings.CompactionReserveTokens);
        SendStats(session, initialService?.Model.Config.ContextWindow ?? 0);

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

                // 2. Resolve role and service after drain (commands may have changed role or model).
                Role? role = _roleService.GetRole(session.Role);
                LlmService? service = _registry.GetServiceForRole(role, session.Model, session.ContextLength + _settings.Settings.CompactionReserveTokens);

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

                    if (service.Model.ConfigId != session.Model)
                    {
                        session.UpdateModel(service.Model.ConfigId);
                        SendStats(session, service.Model.Config.ContextWindow);
                    }

                    if (_wantsCompact)
                    {
                        _wantsCompact = false;
                        Session? compacted = await CompactAsync(ct);
                        if (compacted == null)
                            break;
                        session = compacted;
                        _currentSession = compacted;
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
                        bool hasToolsRole = _roleService.GetRole("Tools") != null;
                        Dictionary<string, Tool> allTools = (!session.IsSubagent && hasToolsRole)
                            ? ToolFactory.BuildSubagent(_settings.Settings.WebSearch, RunSubSessionAsync)
                            : ToolFactory.Build(_settings.Settings.WebSearch);
                        List<Tool> toolList = new List<Tool>();
                        foreach (string toolName in role.Tools)
                        {
                            if (allTools.TryGetValue(toolName, out Tool? tool))
                                toolList.Add(tool);
                        }
                        Tool[] tools = toolList.ToArray();
                        LlmResult result = await RunTurnAsync(session, service, tools, _settings.Settings.CompactionReserveTokens, _cancellationTokenSource.Token);
                        SendStats(session, service.Model.Config.ContextWindow);

                        if (result.ExitReason == LlmExitReason.ContextFull)
                        {
                            contextFull = true;
                        }
                        else if (result.ExitReason == LlmExitReason.Failed)
                        {
                            _transport.Error(session.Id, result.ErrorMessage);
                        }
                        else if (result.ExitReason == LlmExitReason.Completed)
                        {
                            Session? advanced = await AdvanceRoleAsync(session, service, role, _cancellationTokenSource.Token);
                            if (advanced != null)
                            {
                                session = advanced;
                                _currentSession = session;
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
                }

                if (role != null)
                {
                    long waitMs = _registry.GetMillisecondsUntilAvailable(role);
                    if (waitMs > 0)
                        _transport.Status(session.Id, $"No Models Available, waiting {(int)Math.Ceiling(waitMs / 1000.0)}s");
                    int delayMs = Math.Clamp((int)waitMs, 10, 30000);
                    await Task.Delay(delayMs, _cancellationTokenSource.Token);
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
        LlmService? service = role != null ? _registry.GetServiceForRole(role, _currentSession.Model, 0) : null;

        if (role == null || service == null || string.IsNullOrEmpty(role.SummaryPrompt))
        {
            _transport.Status(_currentSession.Id, "[Compaction] No service or summary prompt available.");
            return null;
        }

        IReadOnlyList<CanonicalMessage> tailExchanges = ExtractTailExchanges(_currentSession.Data.Messages, 2);

        _transport.Status(_currentSession.Id, "[Compaction] Started.");
        string? summary = await SummarizeAsync(_currentSession, service, role.SummaryPrompt, Array.Empty<Tool>(), ct);

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
        LlmService? service = role != null ? _registry.GetServiceForRole(role, string.Empty, 0) : null;
        string model = service?.Model.ConfigId ?? string.Empty;
        BeastSession fresh = new BeastSession(Guid.NewGuid().ToString(), string.Empty, model, roleName, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, ephemeral, 0);
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
                        _cachedSessions = SessionService.List();
                        _transport.Status(session.Id, "New session started.");
                    }
                    else if (args == "none")
                    {
                        if (!session.Ephemeral)
                            SessionService.Save(session.Data);
                        session = CreateFreshSession(session.Role, true);
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
                    ReloadRegistry();
                    _registry.ResetAllAvailability();
                    _transport.Status(session.Id, "Config files reloaded.");
                    break;
                case "role":
                    if (args != null)
                    {
                        session.UpdateRole(args);
                        _transport.Status(session.Id, $"Role set to {args}");
                    }
                    break;
                case "model":
                    if (args != null)
                    {
                        Role? modelRole = _roleService.GetRole(session.Role);
                        LlmService? modelService = modelRole != null ? _registry.GetServiceForRole(modelRole, args, session.ContextLength + _settings.Settings.CompactionReserveTokens) : null;
                        if (modelService == null)
                            _transport.Error(session.Id, $"Unknown model: {args}");
                        else
                        {
                            session.UpdateModel(args);
                            _registry.ResetAvailability(args);
                            session.InvalidateProtocol();
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
        LlmService? activeService = activeRole != null
            ? _registry.GetServiceForRole(activeRole, session.Model, session.ContextLength + _settings.Settings.CompactionReserveTokens)
            : null;

        string currentRoleName = activeRole != null ? activeRole.Name : session.Role;
        AddCurrentFirst(candidates, "/role ", currentRoleName, _roleService.Roles.Keys);

        if (activeRole != null)
        {
            string currentModelId = activeService != null ? activeService.Model.ConfigId : session.Model + " (not available)";
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
        FixJsonTests.Test(ctx);
        await FileToolsTests.TestAsync(ctx);
        ShellToolsTests.Test(ctx);
        await WebToolsTests.TestAsync(ctx, _settings.Settings.WebSearch);
        await PerModelLlmTests.TestAsync(ctx, _registry, _roleService, _settings, _cancellationTokenSource.Token);
        ProtocolSwitchTests.Test(ctx);

        _transport.Output(sessionId, $"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
    }

    // ---- Role transition ----

    // Called after a completed worker turn. If the role defines EndOfTurnPrompt + Truths:
    //   1. Runs SummaryPrompt in a fork (with full tools) to let the model do bookkeeping.
    //   2. Runs EndOfTurnPrompt in a fresh ephemeral session with only the Answer tool.
    //      Retries up to MaxEvalAttempts times if the model fails to call the tool.
    //   3. Maps the selected truth label to the next role name.
    //      Empty next-role = stop and return to user; non-empty = start fresh session.
    private async Task<Session?> AdvanceRoleAsync(Session session, LlmService service, Role currentRole, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(currentRole.EndOfTurnPrompt) || currentRole.Truths.Count == 0)
            return null;

        // 1. Run the role's summary prompt in a fork so the model can update MEMORY.md, PLAN.md, etc.
        Tool[] roleTools = _registry.GetToolsForRole(currentRole);
        string summaryPrompt = currentRole.SummaryPrompt;

        _transport.Status(session.Id, "[Role] Running end-of-turn summary...");
        string? summary = await SummarizeAsync(session, service, summaryPrompt, roleTools, ct);
        if (string.IsNullOrEmpty(summary))
        {
            _transport.Error(session.Id, "[Role] Summary failed; staying in current role.");
            return null;
        }

        // 2. Build the evaluation message: summary context + end-of-turn question + truth options.
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(summary);
        sb.AppendLine();
        sb.AppendLine(currentRole.EndOfTurnPrompt);
        sb.AppendLine();
        sb.AppendLine("You must call the Answer tool with exactly one of these statements verbatim:");
        foreach (string truth in currentRole.Truths.Keys)
            sb.AppendLine($"- {truth}");

        // 3. Run the evaluation on the current model with only the Answer tool.
        const int MaxEvalAttempts = 3;
        string? selectedTruth = null;
        Tool answerTool = BuildAnswerTool(currentRole.Truths.Keys, t => selectedTruth = t);

        BeastSession evalData = new BeastSession(session.AllocateChildId(), string.Empty, service.Model.ConfigId, currentRole.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, session.Ephemeral, 0);
        Session evalSession = new Session(evalData, currentRole.SystemPrompt, _transport, true);
        evalSession.AddUserMessage(sb.ToString());

        _transport.Status(session.Id, $"[Role] Evaluating: {currentRole.EndOfTurnPrompt}");
        for (int attempt = 0; attempt < MaxEvalAttempts; attempt++)
        {
            LlmResult evalResult = await RunTurnAsync(evalSession, service, new Tool[] { answerTool }, 0, ct);

            if (evalResult.ExitReason != LlmExitReason.Completed)
                break;

            if (selectedTruth != null)
                break;

            if (attempt < MaxEvalAttempts - 1)
                evalSession.AddUserMessage("You must call the Answer tool. Please call it now with one of the listed truth statements.");
        }

        if (selectedTruth == null)
        {
            _transport.Error(session.Id, "[Role] Evaluation failed; staying in current role.");
            return null;
        }

        // 4. Map the selected truth label to the next role name (case-insensitive).
        string? nextRoleName = null;
        bool foundTruth = false;
        foreach (KeyValuePair<string, string> kvp in currentRole.Truths)
        {
            if (string.Equals(kvp.Key, selectedTruth, StringComparison.OrdinalIgnoreCase))
            {
                nextRoleName = kvp.Value;
                foundTruth = true;
                break;
            }
        }
        if (!foundTruth)
        {
            _transport.Error(session.Id, $"[Role] Answer '{selectedTruth}' not in truths; staying in current role.");
            return null;
        }

        // 5. Empty next role = task finished; return control to the user.
        if (string.IsNullOrEmpty(nextRoleName))
        {
            _transport.Status(session.Id, $"[Role] {selectedTruth} → done.");
            return null;
        }

        // 6. Transition: save current session and start a fresh one seeded with the summary.
        Role nextRole = _roleService.GetRole(nextRoleName) ?? currentRole;

        if (!session.Ephemeral)
            SessionService.Save(session.Data);

        BeastSession freshData = new BeastSession(session.AllocateChildId(), session.DisplayName, session.Model, nextRole.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, session.Ephemeral, 0);
        Session next = new Session(freshData, nextRole.SystemPrompt, _transport, false);

        next.AddUserMessage(summary);

        _transport.Status(session.Id, $"[Role] {selectedTruth} → {nextRole.Name}");
        return next;
    }

    // Builds a one-shot Answer tool that captures the evaluator's truth selection via a closure.
    // The evaluator is given only this tool, forcing a structured decision.
    private static Tool BuildAnswerTool(IEnumerable<string> truthLabels, Action<string> onAnswer)
    {
        string optionList = string.Join("; ", truthLabels);

        JsonObject truthProp = new JsonObject();
        truthProp["type"] = "string";
        truthProp["description"] = $"One of the provided truth statements verbatim. Options: {optionList}";

        JsonObject properties = new JsonObject();
        properties["truth"] = truthProp;

        JsonArray required = new JsonArray();
        required.Add(JsonValue.Create("truth"));

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
                    Description = "Submit your determination. Call once with exactly one of the provided statements verbatim.",
                    Parameters = parameters
                }
            },
            Handler = (JsonObject args, CancellationToken ct, ITransportServer transport, string sessionId) =>
            {
                string? truth = args["truth"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(truth))
                    onAnswer(truth);
                return Task.FromResult(new ToolResult("Answer recorded.", string.Empty, 0));
            }
        };
    }

    // ---- Sub-session dispatch ----

    private static string BuildSubSessionMessage(string toolName, JsonObject args, string goal)
    {
        JsonObject displayArgs = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> kv in args)
        {
            if (kv.Key != "goal")
                displayArgs[kv.Key] = kv.Value?.DeepClone();
        }

        return $"Use tools such as {toolName} with parameters {displayArgs.ToJsonString()} to return: {goal}\nYour final message will be the response that most accurately satisfies the request.";
    }

    // Runs an ephemeral sub-session using the "Tools" role to fulfill a single tool call.
    // Returns the last assistant text, or null if the Tools role is unavailable (falls back to raw handler).
    private async Task<string?> RunSubSessionAsync(string toolName, JsonObject args, string goal, CancellationToken ct)
    {
        Role? toolsRole = _roleService.GetRole("Tools");
        if (toolsRole == null)
            return null;

        LlmService? service = _registry.GetServiceForRole(toolsRole, string.Empty, 0);
        if (service == null)
            return null;

        Tool[] innerTools = _registry.GetToolsForRole(toolsRole);

        // Truncate goal to a display-friendly length for use as the session name.
        string displayName = goal.Length > 80 ? goal.Substring(0, 80) : goal;

        BeastSession subData = new BeastSession(_currentSession.AllocateChildId(), displayName, service.Model.ConfigId, "Tools", new List<CanonicalMessage>(), null, 0m, 0, 0, 0, _currentSession.Ephemeral, 0);
        Session subSession = new Session(subData, toolsRole.SystemPrompt, _transport, true);
        subSession.AnnounceToClient();

        subSession.AddUserMessage(BuildSubSessionMessage(toolName, args, goal));

        subSession.SendBusy();
        _currentSession.AddChild(subSession);
        LlmResult result;
        try
        {
            result = await RunTurnAsync(subSession, service, innerTools, 0, ct);
        }
        finally
        {
            if (!subSession.Ephemeral)
                SessionService.Save(subSession.Data);
            subSession.SendIdle();
        }

        if (result.ExitReason == LlmExitReason.Completed)
            return subSession.GetLastAssistantText();

        return null;
    }

    // ---- LLM turn execution ----

    // Runs one LLM turn on the given session. Delegates bundle management and turn-CTS lifecycle
    // to the session; this method owns the LlmService call and interrupt handling.
    private async Task<LlmResult> RunTurnAsync(Session session, LlmService service, Tool[] tools, int reserveTokens, CancellationToken appToken)
    {
        session.UpdateModel(service.Model.ConfigId);
        session.InferDisplayName();

        CancellationToken turnToken = session.BeginTurn();
        bool interrupted = false;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(turnToken, appToken);
            try
            {
                return await service.RunToCompletionAsync(session, session.Bundle, tools, reserveTokens, _transport, linked.Token);
            }
            catch (OperationCanceledException) when (turnToken.IsCancellationRequested && !appToken.IsCancellationRequested)
            {
                interrupted = true;
                return new LlmResult(LlmExitReason.Interrupted, "Interrupted by user");
            }
        }
        finally
        {
            session.EndTurn(interrupted);
        }
    }

    // Runs a summarization prompt in a temporary fork of the session and returns the assistant text.
    // The fork is discarded after the call; the original session is never modified.
    private async Task<string?> SummarizeAsync(Session session, LlmService service, string prompt, Tool[] tools, CancellationToken appToken)
    {
        Session temp = session.Fork($"{session.Id}_sum", string.Empty, true);
        temp.AddUserMessage(prompt);
        LlmResult result = await RunTurnAsync(temp, service, tools, 0, appToken);
        if (result.ExitReason == LlmExitReason.Completed)
            return temp.GetLastAssistantText();
        return null;
    }

    // ---- Helpers ----

    private void ReloadRegistry()
    {
        _roleService.Reload();
        _settings.LoadSettings();
        _registry.LoadFromConfigs(_settings, _roleService);
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
            promptTokens = session.CumulativeInputTokens,
            completionTokens = session.CumulativeOutputTokens,
            totalCost = session.TotalCost,
            maxContext,
            contextTokens = session.ContextLength
        });
        _transport.Stats(session.Id, json);
    }
}
