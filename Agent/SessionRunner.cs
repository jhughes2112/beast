using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


public enum SessionRunnerExit { Cancelled, ContextFull }

// Executes a session: LLM turns, tool dispatch (including sub-session spawning), and role transitions.
// Ownership rules:
//   - Session system prompt is set once at construction; never mutated during a run.
//   - RunAsync exits with ContextFull when the context limit is hit or /compact is issued.
//     The caller is responsible for calling CompactAsync and swapping to the successor runner.
//   - CompactAsync runs the summarization and returns a pre-loaded successor runner sharing
//     the same input queue, so no input is lost across the swap.
//   - Session is saved in the RunAsync finally block on every exit.
public class SessionRunner
{
    private readonly LlmRegistry _registry;
    private readonly RoleService _roleService;
    private readonly SettingsService _settings;
    private readonly ITransportServer _transport;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ConcurrentQueue<string> _inputQueue;

    // Tracks the session currently being executed; updated whenever the session changes
    // (session switch, role transition). Accessed by CompactAsync after RunAsync returns.
    private Session _currentSession = null!;

    // Held only so Interrupt() can reach the active turn from another thread.
    // Assignment is atomic on 64-bit; worst case it cancels an already-finished turn (no-op).
    private Session? _activeSession;

    private bool _wantsCompact = false;
    private string _clientSessionId = string.Empty;
    private List<(string id, string displayName, int messageCount)> _cachedSessions = new List<(string, string, int)>();
    private int _busyDepth = 0;

    // Initial runner: reloads config and loads (or creates) the last session.
    public SessionRunner(
        ConcurrentQueue<string> inputQueue,
        LlmRegistry registry,
        RoleService roleService,
        SettingsService settings,
        ITransportServer transport,
        CancellationTokenSource cancellationTokenSource)
    {
        _inputQueue = inputQueue;
        _registry = registry;
        _roleService = roleService;
        _settings = settings;
        _transport = transport;
        _cancellationTokenSource = cancellationTokenSource;
        ReloadRegistry();
        _currentSession = LoadOrCreateSession();
    }

    // Compaction successor: receives a pre-built session; skips config reload and session load.
    private SessionRunner(
        Session session,
        ConcurrentQueue<string> inputQueue,
        LlmRegistry registry,
        RoleService roleService,
        SettingsService settings,
        ITransportServer transport,
        CancellationTokenSource cancellationTokenSource)
    {
        _inputQueue = inputQueue;
        _registry = registry;
        _roleService = roleService;
        _settings = settings;
        _transport = transport;
        _cancellationTokenSource = cancellationTokenSource;
        _currentSession = session;
    }

    public void Interrupt() => _activeSession?.Interrupt();

    public async Task<SessionRunnerExit> RunAsync(CancellationToken ct)
    {
        Session session = _currentSession;
        _activeSession = session;

        Role? initialRole = _roleService.GetRole(session.Role);
        LlmService? initialService = _registry.GetServiceForRole(initialRole, session.Model, session.ContextLength + _settings.Settings.CompactionReserveTokens);
        SendStats(session, initialService?.Model.Config.ContextWindow ?? 0);

        _cachedSessions = SessionService.List();
        string lastCompletionCandidates = JsonSerializer.Serialize(BuildCompletionCandidates(session));
        _transport.Completions(lastCompletionCandidates);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1. Drain all pending input: text → AddUserMessage, commands → dispatch.
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
                        _transport.Clear();
                        session.ReplayToTransport();
                        SendStats(session, service.Model.Config.ContextWindow);
                        _transport.Status("ready");
                    }

                    string currentCandidates = JsonSerializer.Serialize(BuildCompletionCandidates(session));
                    if (currentCandidates != lastCompletionCandidates)
                    {
                        lastCompletionCandidates = currentCandidates;
                        _transport.Completions(currentCandidates);
                    }

                    if (service.Model.ConfigId != session.Model)
                    {
                        session.UpdateModel(service.Model.ConfigId);
                        SendStats(session, service.Model.Config.ContextWindow);
                    }

                    // Signal the owner to compact; do not run inline.
                    if (_wantsCompact)
                    {
                        _wantsCompact = false;
                        return SessionRunnerExit.ContextFull;
                    }
                }

                // 3. Run the LLM whenever the session has work.
                // NeedsAttention() returns false after an interrupt until AddUserMessage() is called.
                if (session.NeedsAttention() && service != null && role != null)
                {
                    IncrementBusy();
                    try
                    {
                        Tool[] tools = BuildOuterTools(_registry.GetToolsForRole(role));
                        // Mid-turn: move text from the input queue into the session so the LLM can see
                        // it between tool calls. Commands stay queued and are processed next iteration.
                        Action midTurnDrain = () =>
                        {
                            List<string> deferred = new List<string>();
                            while (_inputQueue.TryDequeue(out string? item))
                            {
                                if (item.StartsWith("/"))
                                    deferred.Add(item);
                                else
                                    session.AddUserMessage(item);
                            }
                            foreach (string cmd in deferred)
                                _inputQueue.Enqueue(cmd);
                        };
                        LlmResult result = await session.RunTurnAsync(service, tools, _settings.Settings.CompactionReserveTokens, _cancellationTokenSource.Token, midTurnDrain);
                        SendStats(session, service.Model.Config.ContextWindow);

                        if (result.ExitReason == LlmExitReason.ContextFull)
                        {
                            return SessionRunnerExit.ContextFull;
                        }
                        else if (result.ExitReason == LlmExitReason.Failed)
                        {
                            _transport.Error(result.ErrorMessage);
                        }
                        else if (result.ExitReason == LlmExitReason.Completed)
                        {
                            Session? advanced = await AdvanceRoleAsync(session, service, role, _cancellationTokenSource.Token);
                            if (advanced != null)
                            {
                                session = advanced;
                                _currentSession = session;
                                _activeSession = session;
                            }
                        }
                        // Interrupted: session._interruptedAndWaiting is set in RunTurnAsync;
                        // NeedsAttention() returns false until AddUserMessage() is called with new input.
                    }
                    finally
                    {
                        DecrementBusy();
                    }
                }

                if (role != null)
                {
                    long waitMs = _registry.GetMillisecondsUntilAvailable(role);
                    if (waitMs > 0)
                        _transport.Status($"No Models Available, waiting {(int)Math.Ceiling(waitMs / 1000.0)}s");
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

        return SessionRunnerExit.Cancelled;
    }

    // Summarizes the current session and returns a new runner seeded with the compacted content.
    // The successor shares the same input queue so no input is lost across the swap.
    // Returns null if the service, role, or summary prompt is unavailable.
    public async Task<SessionRunner?> CompactAsync(CancellationToken ct)
    {
        Role? role = _roleService.GetRole(_currentSession.Role);
        LlmService? service = role != null ? _registry.GetServiceForRole(role, _currentSession.Model, 0) : null;

        if (role == null || service == null || string.IsNullOrEmpty(role.SummaryPrompt))
        {
            _transport.Status("[Compaction] No service or summary prompt available.");
            return null;
        }

        IReadOnlyList<CanonicalMessage> tailExchanges = ExtractTailExchanges(_currentSession.Data.Messages, 2);

        _transport.Status("[Compaction] Started.");
        string? summary = await _currentSession.SummarizeAsync(service, role.SummaryPrompt, Array.Empty<Tool>(), ct);

        if (string.IsNullOrWhiteSpace(summary))
        {
            _transport.Status("[Compaction] Failed.");
            return null;
        }

        SessionService.Save(_currentSession.Data);

        string newDisplayName = Session.IncrementDisplayName(_currentSession.DisplayName);
        string newSessionId = IncrementSessionId(_currentSession.Id);

        // Compaction creates a fresh session (no history), not a Fork.
        // Fork = deep copy from a branch point; compaction = clean slate seeded with the summary.
        BeastSession freshData = new BeastSession(newSessionId, newDisplayName, _currentSession.Model, _currentSession.Role, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, _currentSession.Ephemeral);

        Session newSession = new Session(freshData, role.SystemPrompt, _transport);
        newSession.AddUserMessage(summary);
        newSession.FlushPendingMessages();
        newSession.ReplayExchanges(tailExchanges);

        if (!newSession.Ephemeral)
            SessionService.Save(newSession.Data);

        _cachedSessions = SessionService.List();
        _transport.Status("[Compaction] Complete.");
        return new SessionRunner(newSession, _inputQueue, _registry, _roleService, _settings, _transport, _cancellationTokenSource);
    }

    // ---- Session management ----

    private Session LoadOrCreateSession()
    {
        string? lastSessionId = SessionService.LoadLastSession();
        BeastSession? lastData = SessionService.LoadBySessionId(lastSessionId);
        if (lastData != null)
        {
            _transport.Status("Resumed session: " + lastData.DisplayName);
            return new Session(lastData, string.Empty, _transport);
        }
        return CreateFreshSession(string.Empty, false);
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
        LlmService? service = role != null ? _registry.GetServiceForRole(role, string.Empty, 0) : null;
        string model = service?.Model.ConfigId ?? string.Empty;
        BeastSession fresh = new BeastSession(Guid.NewGuid().ToString(), string.Empty, model, roleName, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, ephemeral);
        return new Session(fresh, systemPrompt, _transport);
    }

    // ---- Input processing ----

    // Drains all queued input in arrival order: plain text becomes user messages; slash commands
    // are dispatched. This preserves ordering (text, /cmd, text stays text → action → text).
    private async Task<Session> DrainInputAsync(Session session)
    {
        while (_inputQueue.TryDequeue(out string? line))
        {
            if (!line.StartsWith("/"))
            {
                session.AddUserMessage(line);
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
                case "compact":
                    _wantsCompact = true;
                    break;
                case "session":
                    if (args == "new")
                    {
                        if (!session.Ephemeral)
                            SessionService.Save(session.Data);
                        session = CreateFreshSession(session.Role, false);
                        _activeSession = session;
                        _cachedSessions = SessionService.List();
                        _transport.Status("New session started.");
                    }
                    else if (args == "none")
                    {
                        if (!session.Ephemeral)
                            SessionService.Save(session.Data);
                        session = CreateFreshSession(session.Role, true);
                        _activeSession = session;
                        _cachedSessions = SessionService.List();
                        _transport.Status("Ephemeral session started (not saved).");
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
                            session = new Session(loaded, string.Empty, _transport);
                            _activeSession = session;
                            _cachedSessions = SessionService.List();
                            _transport.Status("Switched to session: " + loaded.DisplayName);
                        }
                        else
                        {
                            _transport.Error("Session not found: " + args);
                        }
                    }
                    break;
                case "clear":
                    session.Clear();
                    _registry.ResetAllAvailability();
                    _transport.Status("Session cleared.");
                    break;
                case "reload":
                    ReloadRegistry();
                    _registry.ResetAllAvailability();
                    _transport.Status("Config files reloaded.");
                    break;
                case "role":
                    if (args != null)
                    {
                        session.UpdateRole(args);
                        _transport.Status($"Role set to {args}");
                    }
                    break;
                case "model":
                    if (args != null)
                    {
                        Role? modelRole = _roleService.GetRole(session.Role);
                        LlmService? modelService = modelRole != null ? _registry.GetServiceForRole(modelRole, args, session.ContextLength + _settings.Settings.CompactionReserveTokens) : null;
                        if (modelService == null)
                            _transport.Error($"Unknown model: {args}");
                        else
                        {
                            session.UpdateModel(args);
                            _registry.ResetAvailability(args);
                            session.InvalidateProtocol();
                            _transport.Status($"Model set to {args}");
                        }
                    }
                    break;
                case "help":
                    _transport.Output("Commands: /compact, /clear, /reload, /role <id>, /model <id>, /session new, /session none, /session <id>, /test, /quit");
                    break;
                case "test":
                    await RunTestsAsync(args);
                    break;
                default:
                    _transport.Error($"Unknown command reached agent: /{verb}");
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

    private async Task RunTestsAsync(string? filter)
    {
        _transport.Status("Running tests...");
        TestContext ctx = new TestContext(_transport);

        LlmServiceTests.Test(ctx);
        FixJsonTests.Test(ctx);
        await FileToolsTests.TestAsync(ctx);
        ShellToolsTests.Test(ctx);
        await WebToolsTests.TestAsync(ctx, _settings.Settings.WebSearch);
        await PerModelLlmTests.TestAsync(ctx, _registry, _roleService, _settings, _cancellationTokenSource.Token);
        ProtocolSwitchTests.Test(ctx);

        _transport.Output($"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
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

        _transport.Status("[Role] Running end-of-turn summary...");
        string? summary = await session.SummarizeAsync(service, summaryPrompt, roleTools, ct);
        if (string.IsNullOrEmpty(summary))
        {
            _transport.Error("[Role] Summary failed; staying in current role.");
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

        Session evalSession = CreateFreshSession(currentRole.Name, true);
        evalSession.AddUserMessage(sb.ToString());

        _transport.Status($"[Role] Evaluating: {currentRole.EndOfTurnPrompt}");
        for (int attempt = 0; attempt < MaxEvalAttempts; attempt++)
        {
            LlmResult evalResult = await evalSession.RunTurnAsync(service, new Tool[] { answerTool }, 0, ct, null);

            if (evalResult.ExitReason != LlmExitReason.Completed)
                break;

            if (selectedTruth != null)
                break;

            if (attempt < MaxEvalAttempts - 1)
                evalSession.AddUserMessage("You must call the Answer tool. Please call it now with one of the listed truth statements.");
        }

        if (selectedTruth == null)
        {
            _transport.Error("[Role] Evaluation failed; staying in current role.");
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
            _transport.Error($"[Role] Answer '{selectedTruth}' not in truths; staying in current role.");
            return null;
        }

        // 5. Empty next role = task finished; return control to the user.
        if (string.IsNullOrEmpty(nextRoleName))
        {
            _transport.Status($"[Role] {selectedTruth} → done.");
            return null;
        }

        // 6. Transition: save current session and start a fresh one seeded with the summary.
        Role nextRole = _roleService.GetRole(nextRoleName) ?? currentRole;

        if (!session.Ephemeral)
            SessionService.Save(session.Data);

        BeastSession freshData = new BeastSession(Guid.NewGuid().ToString(), session.DisplayName, session.Model, nextRole.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, session.Ephemeral);
        Session next = new Session(freshData, nextRole.SystemPrompt, _transport);

        next.AddUserMessage(summary);

        _transport.Status($"[Role] {selectedTruth} → {nextRole.Name}");
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
            Handler = (JsonObject args, CancellationToken ct, ITransportServer transport) =>
            {
                string? truth = args["truth"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(truth))
                    onAnswer(truth);
                return Task.FromResult(new ToolResult("Answer recorded.", string.Empty, 0));
            }
        };
    }

    // ---- Outer tool wrapping (sub-session dispatch) ----

    // Adds "goal" to a tool's parameter schema so the main LLM must describe the desired result.
    private static JsonObject CloneParamsWithGoal(JsonObject original)
    {
        JsonObject cloned = (JsonObject)original.DeepClone();

        if (cloned["properties"] is not JsonObject props)
        {
            props = new JsonObject();
            cloned["properties"] = props;
        }

        props["goal"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Describe the desired result — what you want this tool call to return or accomplish."
        };

        if (cloned["required"] is not JsonArray req)
        {
            req = new JsonArray();
            cloned["required"] = req;
        }

        req.Add(JsonValue.Create("goal"));
        return cloned;
    }

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

        BeastSession subData = new BeastSession(Guid.NewGuid().ToString(), string.Empty, service.Model.ConfigId, "Tools", new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
        Session subSession = new Session(subData, toolsRole.SystemPrompt, _transport);

        subSession.AddUserMessage(BuildSubSessionMessage(toolName, args, goal));

        LlmResult result = await subSession.RunTurnAsync(service, innerTools, 0, ct, null);

        if (result.ExitReason == LlmExitReason.Completed)
            return subSession.GetLastAssistantText();

        return null;
    }

    // Wraps each raw tool with a "goal" parameter. The handler spawns a sub-session to fulfill the
    // request; if the Tools role is unavailable, it falls back to calling the raw handler directly.
    private Tool[] BuildOuterTools(Tool[] rawTools)
    {
        Tool[] outer = new Tool[rawTools.Length];
        for (int i = 0; i < rawTools.Length; i++)
        {
            Tool raw = rawTools[i];
            string toolName = raw.Definition.Function.Name;
            Func<JsonObject, CancellationToken, ITransportServer, Task<ToolResult>> rawHandler = raw.Handler;
            JsonObject outerParams = CloneParamsWithGoal(raw.Definition.Function.Parameters);

            outer[i] = new Tool
            {
                Definition = new ToolDefinition
                {
                    Function = new FunctionDefinition
                    {
                        Name = toolName,
                        Description = raw.Definition.Function.Description,
                        Parameters = outerParams
                    }
                },
                Handler = async (args, ct, transport) =>
                {
                    if (args.TryGetPropertyValue("goal", out JsonNode? goalNode) && goalNode != null)
                    {
                        string goal = goalNode.ToString();
                        string? subResult = await RunSubSessionAsync(toolName, args, goal, ct);
                        if (subResult != null)
                            return new ToolResult(subResult, string.Empty, 0);
                    }

                    return await rawHandler(args, ct, transport);
                }
            };
        }

        return outer;
    }

    // ---- Helpers ----

    private void ReloadRegistry()
    {
        _roleService.Reload();
        _settings.LoadSettings();
        _registry.LoadFromConfigs(_settings, _roleService);
    }

    private void IncrementBusy()
    {
        _busyDepth++;
        if (_busyDepth == 1)
            _transport.Busy();
    }

    private void DecrementBusy()
    {
        _busyDepth--;
        if (_busyDepth == 0)
            _transport.Idle();
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

    // Generates a new session ID from an existing one by incrementing the numeric suffix.
    private static string IncrementSessionId(string sessionId)
    {
        int lastUnderscore = sessionId.LastIndexOf('_');
        if (lastUnderscore >= 0 && lastUnderscore < sessionId.Length - 1)
        {
            string suffix = sessionId.Substring(lastUnderscore + 1);
            if (int.TryParse(suffix, out int number))
                return $"{sessionId.Substring(0, lastUnderscore)}_{number + 1}";
        }
        return $"{sessionId}_2";
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
        _transport.Stats(json);
    }
}
