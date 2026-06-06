using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Manages the agent loop: read input, run LLM turns, compact, save sessions.
//
// Architecture:
//   - Session is a LOCAL VARIABLE in RunAsync. No field holds it (except _activeSession for Interrupt).
//   - Startup is silent: no upfront validation. Errors fire at interaction time.
//   - A cooperative async reader task feeds a ConcurrentQueue; the main loop drains it each iteration.
//   - The LLM runs whenever the session has pending work; it is not gated on input arrival.
//   - /compact sets a flag; compaction runs at the top of the next iteration.
//   - Sessions are loaded from disk on start (last session resumed by default)
//     and saved after compaction, session switch, and on exit.
//   - The active role is resolved through the workflow state machine via ResolveRole; model selection
//     follows from the role's allowed list, preferring the session's current model when compatible.
public class AgentOrchestrator
{
    private readonly LlmRegistry _registry;
    private readonly RoleService _roleService;
    private readonly SettingsService _settings;
    private readonly ITransportServer _transport;
    private readonly WorkflowService _workflowService;
    private readonly CancellationTokenSource _cancellationTokenSource;

    // Reference to the currently running session, so the reader task can call Interrupt().
    // Assignment is atomic on 64-bit; worst case the reader cancels an already-finished turn, which is a no-op.
    private Session? _activeSession;

    private bool _wantsCompact = false;
    private string _clientSessionId = string.Empty;
    private List<(string id, string displayName, int messageCount)> _cachedSessions = new List<(string, string, int)>();
    private int _busyDepth = 0;

    public AgentOrchestrator(
        LlmRegistry registry,
        RoleService roleService,
        SettingsService settings,
        ITransportServer transport,
        WorkflowService workflowService,
        CancellationTokenSource cancellationTokenSource)
    {
        _registry = registry;
        _roleService = roleService;
        _settings = settings;
        _transport = transport;
        _workflowService = workflowService;
        _cancellationTokenSource = cancellationTokenSource;
        ReloadRegistry();
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

    public async Task RunAsync()
    {
        Session session = LoadOrCreateSession();
        _activeSession = session;
        ConcurrentQueue<string> inputQueue = new ConcurrentQueue<string>();

        // Send initial stats so the client sees token counts and cost immediately.
        LLMRole? initialRole = ResolveRole(session.Data);
        LlmService? initialService = _registry.GetServiceForRole(initialRole, session.Data.Model, session.Data.GetContextLength() + _settings.Settings.CompactionReserveTokens);
        SendStats(session.Data, initialService?.Model.Config.ContextWindow ?? 0);

        // Cooperative async reader: awaits input and feeds the queue.
        async Task ReadInputAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                string? line = await _transport.TryReadAsync(100, token);
                if (line == null)
                    break;
                if (line.Length > 0)
                {
                    _transport.Debug($"[orchestrator] Received: '{line}'");
                    if (line.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                        _activeSession?.Interrupt();
                    else
                        inputQueue.Enqueue(line);
                }
            }
        }

        Task readerTask = ReadInputAsync(_cancellationTokenSource.Token);

        _cachedSessions = SessionService.List();
        string lastCompletionCandidates = JsonSerializer.Serialize(BuildCompletionCandidates(session.Data));
        _transport.Completions(lastCompletionCandidates);

        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                // 1. Resolve role and service through the active workflow state.
                LLMRole? role = ResolveRole(session.Data);
                LlmService? service = _registry.GetServiceForRole(role, session.Data.Model, session.Data.GetContextLength() + _settings.Settings.CompactionReserveTokens);

                // 2. Drain all pending input: run commands in order, accumulate text.
                // Returns null accumulatedText if nothing was dequeued; empty string if only commands ran.
                string? accumulatedText = null;
                if (!inputQueue.IsEmpty)
                    (accumulatedText, session) = await DrainInputAsync(inputQueue, session, service, role);

                // Re-resolve after commands so any role/model/workflow changes take effect immediately.
                role = ResolveRole(session.Data);
                service = _registry.GetServiceForRole(role, session.Data.Model, session.Data.GetContextLength() + _settings.Settings.CompactionReserveTokens);

                if (service != null)
                {
                    // Send history to the client whenever the active session changes.
                    if (session.Data.Id != _clientSessionId)
                    {
                        _clientSessionId = session.Data.Id;
                        _transport.Clear();
                        session.ReplayToTransport();
                        SendStats(session.Data, service.Model.Config.ContextWindow);
                        _transport.Status("ready");
                    }

                    string currentCandidates = JsonSerializer.Serialize(BuildCompletionCandidates(session.Data));
                    if (currentCandidates != lastCompletionCandidates)
                    {
                        lastCompletionCandidates = currentCandidates;
                        _transport.Completions(currentCandidates);
                    }

                    if (service.Model.ConfigId != session.Data.Model)
                    {
                        session.Data.Model = service.Model.ConfigId;
                        SendStats(session.Data, service.Model.Config.ContextWindow);
                    }

                    if (!string.IsNullOrEmpty(role!.SystemPrompt) && role.SystemPrompt != session.Data.GetSystemPrompt())
                        session.SetSystemPrompt(role.SystemPrompt);

                    if (_wantsCompact)
                    {
                        _wantsCompact = false;
                        IncrementBusy();
                        try
                        {
                            _transport.Status("Running compaction...");
                            Session? compacted = await RunCompactionAsync(session, service!, role!);
                            if (compacted != null)
                            {
                                session = compacted;
                                _activeSession = session;
                            }
                        }
                        finally
                        {
                            DecrementBusy();
                            // Re-resolve after compaction because the model may differ in the new session.
                            service = _registry.GetServiceForRole(role, session.Data.Model, session.Data.GetContextLength() + _settings.Settings.CompactionReserveTokens);
                        }
                    }
                }

                // Queue the accumulated user text; RunTurnAsync will flush it before calling the LLM.
                if (!string.IsNullOrEmpty(accumulatedText))
                    session.AddUserMessage(accumulatedText);

                // 3. Run the LLM whenever the session has work; yield briefly if there is nothing to do.
                if (session.NeedsAttention() && service != null && role != null)
                {
                    IncrementBusy();
                    try
                    {
                        Tool[] tools = _registry.GetToolsForRole(role);
                        LlmResult result = await session.RunTurnAsync(service, tools, _settings.Settings.CompactionReserveTokens, _cancellationTokenSource.Token);
                        SendStats(session.Data, service.Model.Config.ContextWindow);

                        if (result.ExitReason == LlmExitReason.ContextFull)
                        {
                            _wantsCompact = true;
                        }
                        else if (result.ExitReason == LlmExitReason.Failed)
                        {
                            _transport.Error(result.ErrorMessage);
                        }
                        else if (result.ExitReason == LlmExitReason.Completed)
                        {
                            Session? advanced = await AdvanceWorkflowAsync(session, service, role, _cancellationTokenSource.Token);
                            if (advanced != null)
                            {
                                session = advanced;
                                _activeSession = session;
                            }
                        }
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
            SessionService.Save(session.Data);
        }
    }

    // ---- Session management ----

    private Session LoadOrCreateSession()
    {
        string? lastSessionId = SessionService.LoadLastSession();
        BeastSession? lastData = SessionService.LoadBySessionId(lastSessionId);
        if (lastData != null)
        {
            _transport.Status("Resumed session: " + lastData.DisplayName);
            return new Session(lastData, _transport);
        }
        return CreateFreshSession(string.Empty);
    }

    private Session CreateFreshSession(string roleName)
    {
        if (string.IsNullOrEmpty(roleName))
        {
            string? wfRole = _workflowService.GetRoleForState("default", "default");
            if (!string.IsNullOrEmpty(wfRole))
            {
                roleName = wfRole;
            }
            else
            {
                foreach (LLMRole r in _roleService.Roles.Values)
                {
                    roleName = r.Name;
                    break;
                }
            }
        }
        BeastSession fresh = BeastSession.CreateNew(Guid.NewGuid().ToString(), roleName, string.Empty);
        return new Session(fresh, _transport);
    }

    // ---- Compaction ----

    // Summarizes the current session and returns a new compacted Session, or null on failure.
    private async Task<Session?> RunCompactionAsync(Session session, LlmService service, LLMRole role)
    {
        List<JsonNode> tailExchanges = ExtractTailExchanges(session.Data.ChatCompletionsState, 2);

        _transport.Status("[Compaction] Started.");
        string? summary = await session.SummarizeAsync(service, _settings.Settings.CompactionPrompt, _cancellationTokenSource.Token);

        if (string.IsNullOrWhiteSpace(summary))
        {
            _transport.Status("[Compaction] Failed.");
            return null;
        }

        SessionService.Save(session.Data);

        string newDisplayName = BeastSession.IncrementDisplayName(session.Data.DisplayName);
        string newSessionId = IncrementSessionId(session.Data.Id);

        // Compaction creates a fresh session (no history), not a Fork.
        // Fork = deep copy from a branch point; compaction = clean slate seeded with the summary.
        BeastSession freshData = BeastSession.CreateNew(newSessionId, session.Data.Role, newDisplayName);
        freshData.Model = session.Data.Model;
        freshData.Ephemeral = session.Data.Ephemeral;
        freshData.Workflow = session.Data.Workflow;
        freshData.WorkflowState = session.Data.WorkflowState;
        Session fresh = new Session(freshData, _transport);

        if (!string.IsNullOrEmpty(role.SystemPrompt))
            fresh.SetSystemPrompt(role.SystemPrompt);

        fresh.AddUserMessage(summary);
        fresh.FlushPendingMessages();
        fresh.ReplayExchanges(tailExchanges);

        if (!fresh.Data.Ephemeral)
            SessionService.Save(fresh.Data);

        _cachedSessions = SessionService.List();
        _transport.Status("[Compaction] Complete.");
        return fresh;
    }

    // ---- Input processing ----

    // Drains all queued input lines. Commands are dispatched immediately; plain text is accumulated.
    // Returns null for accumulatedText if the queue was empty; empty string if only commands ran.
    private async Task<(string? accumulatedText, Session session)> DrainInputAsync(
        ConcurrentQueue<string> inputQueue,
        Session session,
        LlmService? service,
        LLMRole? role)
    {
        if (!inputQueue.TryPeek(out _))
            return (null, session);

        string accumulatedText = string.Empty;

        while (inputQueue.TryDequeue(out string? line))
        {
            string? text;
            (text, session) = await HandleInputLineAsync(line, session, service, role);
            if (text != null)
                accumulatedText = string.IsNullOrEmpty(accumulatedText) ? text : accumulatedText + "\n" + text;
        }

        return (accumulatedText, session);
    }

    // Dispatches a single input line. Returns the line as plain text if it is not a command, null otherwise.
    private async Task<(string? text, Session session)> HandleInputLineAsync(
        string line,
        Session session,
        LlmService? service,
        LLMRole? role)
    {
        if (!line.StartsWith("/"))
            return (line, session);

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
                    SessionService.Save(session.Data);
                    session = CreateFreshSession(session.Data.Role);
                    _activeSession = session;
                    _cachedSessions = SessionService.List();
                    _transport.Status("New session started.");
                }
                else if (args == "none")
                {
                    SessionService.Save(session.Data);
                    session = CreateFreshSession(session.Data.Role);
                    session.Data.Ephemeral = true;
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
                        SessionService.Save(session.Data);
                        session = new Session(loaded, _transport);
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
                _transport.Status("Config files reloaded. Workflows reloaded.");
                break;
            case "workflow":
                if (args != null)
                {
                    Workflow? targetWorkflow = _workflowService.GetWorkflow(args);
                    if (targetWorkflow == null)
                        _transport.Error($"Unknown workflow: {args}");
                    else
                    {
                        session.Data.Workflow = args;
                        session.Data.WorkflowState = targetWorkflow.GetFirstState()?.Name ?? string.Empty;
                        _transport.Status($"Workflow set to {args}, state: {session.Data.WorkflowState}");
                    }
                }
                break;
            case "state":
                if (args != null)
                {
                    string wfName = string.IsNullOrEmpty(session.Data.Workflow) ? "default" : session.Data.Workflow;
                    Workflow? stateWorkflow = _workflowService.GetWorkflow(wfName);
                    if (stateWorkflow == null)
                        _transport.Error($"Current workflow '{wfName}' not found");
                    else if (stateWorkflow.GetState(args) == null)
                        _transport.Error($"State '{args}' not found in workflow '{wfName}'");
                    else
                    {
                        session.Data.WorkflowState = args;
                        _transport.Status($"Workflow state set to {args}");
                    }
                }
                break;
            case "role":
                if (args != null)
                {
                    session.Data.Role = args;
                    _transport.Status($"Role set to {args}");
                }
                break;
            case "model":
                if (args != null)
                {
                    LLMRole? modelRole = ResolveRole(session.Data);
                    LlmService? modelService = modelRole != null ? _registry.GetServiceForRole(modelRole, args, session.Data.GetContextLength() + _settings.Settings.CompactionReserveTokens) : null;
                    if (modelService == null)
                        _transport.Error($"Unknown model: {args}");
                    else
                    {
                        session.Data.Model = args;
                        _registry.ResetAvailability(args);
                        session.InvalidateProtocol();
                        _transport.Status($"Model set to {args}");
                    }
                }
                break;
            case "help":
                _transport.Output("Commands: /compact, /clear, /reload, /workflow <name>, /state <name>, /role <id>, /model <id>, /session new, /session none, /session <id>, /test, /quit");
                break;
            case "test":
                await RunTestsAsync(args);
                break;
            default:
                _transport.Error($"Unknown command reached agent: /{verb}");
                break;
        }

        return (null, session);
    }

    // Returns all completable tokens: slash commands, workflow/state names, and session ids.
    private List<string> BuildCompletionCandidates(BeastSession data)
    {
        List<string> candidates = new List<string>
        {
            "/compact", "/clear", "/reload", "/role", "/model",
            "/workflow", "/state", "/session", "/help", "/test"
        };

        LLMRole? activeRole = ResolveRole(data);
        LlmService? activeService = activeRole != null
            ? _registry.GetServiceForRole(activeRole, data.Model, data.GetContextLength() + _settings.Settings.CompactionReserveTokens)
            : null;

        string currentRoleName = activeRole != null ? activeRole.Name : data.Role;
        AddCurrentFirst(candidates, "/role ", currentRoleName, _roleService.Roles.Keys);

        if (activeRole != null)
        {
            string currentModelId = activeService != null ? activeService.Model.ConfigId : data.Model + " (not available)";
            List<string> enabledModels = _registry.GetEnabledModelsForRole(activeRole);
            AddCurrentFirst(candidates, "/model ", currentModelId, enabledModels);
        }

        string currentWorkflow = string.IsNullOrEmpty(data.Workflow) ? "default" : data.Workflow;
        List<string> workflowNames = new List<string>(_workflowService.Workflows.Keys);
        AddCurrentFirst(candidates, "/workflow ", currentWorkflow, workflowNames);

        Workflow? activeWorkflow = _workflowService.GetWorkflow(currentWorkflow);
        if (activeWorkflow != null)
        {
            string currentState = string.IsNullOrEmpty(data.WorkflowState) ? "default" : data.WorkflowState;
            List<string> stateNames = new List<string>();
            foreach (WorkflowState ws in activeWorkflow.States)
                stateNames.Add(ws.Name);
            AddCurrentFirst(candidates, "/state ", currentState, stateNames);
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
        WorkflowTests.Test(ctx);
        await FileToolsTests.TestAsync(ctx);
        ShellToolsTests.Test(ctx);
        await WebToolsTests.TestAsync(ctx, _settings.Settings.WebSearch);
        await PerModelLlmTests.TestAsync(ctx, _registry, _roleService, _settings, _cancellationTokenSource.Token);
        await ProtocolSwitchTests.TestAsync(ctx, _registry, _roleService, _cancellationTokenSource.Token);

        _transport.Output($"=== Tests complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
    }

    // ---- Helpers ----

    // Called after a completed worker turn. Summarizes the session, presents the state's Query
    // to the StateEvaluator with only the Answer tool, then creates a fresh session for the next
    // state seeded with the summary. Returns null to stay in the current session unchanged.
    //
    // Null/empty transitions wait for user input (summary not injected — session is idle).
    // Non-null transitions inject the summary as the first user message so the agent auto-continues.
    private async Task<Session?> AdvanceWorkflowAsync(Session session, LlmService service, LLMRole currentRole, CancellationToken ct)
    {
        string workflowName = string.IsNullOrEmpty(session.Data.Workflow) ? "default" : session.Data.Workflow;
        string stateName = string.IsNullOrEmpty(session.Data.WorkflowState) ? "default" : session.Data.WorkflowState;
        Workflow? workflow = _workflowService.GetWorkflow(workflowName);
        WorkflowState? state = workflow?.GetState(stateName);

        if (state == null || string.IsNullOrEmpty(state.Query) || state.Truths.Count == 0)
            return null;

        string? evaluatorRoleName = workflow!.StateEvaluatorRole;
        if (string.IsNullOrEmpty(evaluatorRoleName))
            return null;

        LLMRole? evaluatorRole = _roleService.GetRole(evaluatorRoleName);
        if (evaluatorRole == null)
        {
            _transport.Error($"StateEvaluator role '{evaluatorRoleName}' not found");
            return null;
        }

        LlmService? evaluatorService = _registry.GetServiceForRole(evaluatorRole, string.Empty, 0);
        if (evaluatorService == null)
        {
            _transport.Status("StateEvaluator: no available model");
            return null;
        }

        // 1. Summarize the completed worker turn (non-mutating — runs in a temp Fork).
        _transport.Status("[Workflow] Summarizing turn...");
        const string summaryPrompt = "Provide a concise summary of everything accomplished this session: decisions made, actions taken, results, and any context needed to continue the work.";
        string? summary = await session.SummarizeAsync(service, summaryPrompt, ct);
        if (string.IsNullOrEmpty(summary))
        {
            _transport.Error("[Workflow] Summary failed; staying in current state.");
            return null;
        }

        // 2. Build the evaluator prompt: summary + query + truth options.
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(summary);
        sb.AppendLine();
        sb.AppendLine(state.Query);
        sb.AppendLine();
        sb.AppendLine("Call the Answer tool with one of these statements verbatim:");
        foreach (string truth in state.Truths.Keys)
            sb.AppendLine($"- {truth}");

        // 3. Run the evaluator with only the Answer tool (no other tools).
        string? selectedTruth = null;
        Tool answerTool = BuildAnswerTool(state.Truths.Keys, t => selectedTruth = t);

        Session evalSession = CreateFreshSession(evaluatorRole.Name);
        evalSession.Data.Ephemeral = true;
        if (!string.IsNullOrEmpty(evaluatorRole.SystemPrompt))
            evalSession.SetSystemPrompt(evaluatorRole.SystemPrompt);
        evalSession.AddUserMessage(sb.ToString());

        _transport.Status($"[Workflow] Evaluating: {state.Query}");
        LlmResult evalResult = await evalSession.RunTurnAsync(evaluatorService, new Tool[] { answerTool }, 0, ct);

        if (evalResult.ExitReason != LlmExitReason.Completed || string.IsNullOrEmpty(selectedTruth))
        {
            _transport.Error("[Workflow] Evaluator did not call Answer; staying in current state.");
            return null;
        }

        // 4. Map the selected truth to the next state name (case-insensitive).
        string? nextStateName = null;
        bool foundTruth = false;
        foreach (KeyValuePair<string, string> kvp in state.Truths)
        {
            if (string.Equals(kvp.Key, selectedTruth, StringComparison.OrdinalIgnoreCase))
            {
                nextStateName = kvp.Value;
                foundTruth = true;
                break;
            }
        }
        if (!foundTruth)
        {
            _transport.Error($"[Workflow] Answer '{selectedTruth}' not in truths; staying in current state.");
            return null;
        }

        // 5. Resolve the role for the new session.
        bool isNullTransition = string.IsNullOrEmpty(nextStateName);
        string resolvedStateName = isNullTransition ? stateName : nextStateName!;
        WorkflowState? nextState = workflow.GetState(resolvedStateName);
        string nextRoleName = nextState?.Role ?? currentRole.Name;
        LLMRole nextRole = _roleService.GetRole(nextRoleName) ?? currentRole;

        // 6. Save the old session and build the fresh one.
        if (!session.Data.Ephemeral)
            SessionService.Save(session.Data);

        BeastSession freshData = BeastSession.CreateNew(Guid.NewGuid().ToString(), nextRole.Name, session.Data.DisplayName);
        freshData.Model = session.Data.Model;
        freshData.Ephemeral = session.Data.Ephemeral;
        freshData.Workflow = session.Data.Workflow;
        freshData.WorkflowState = resolvedStateName;
        Session next = new Session(freshData, _transport);

        if (!string.IsNullOrEmpty(nextRole.SystemPrompt))
            next.SetSystemPrompt(nextRole.SystemPrompt);

        // Non-null transitions inject the summary so the new session auto-continues.
        // Null transitions leave the session idle, waiting for the user's next message.
        if (!isNullTransition)
            next.AddUserMessage(summary);

        _transport.Status($"[Workflow] {selectedTruth} → {resolvedStateName}");
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

    // Resolves the active LLMRole from the session's workflow state.
    // Falls back to data.Role when the workflow or state is not found.
    private LLMRole? ResolveRole(BeastSession data)
    {
        string workflowName = string.IsNullOrEmpty(data.Workflow) ? "default" : data.Workflow;
        string stateName = string.IsNullOrEmpty(data.WorkflowState) ? "default" : data.WorkflowState;
        string? roleName = _workflowService.GetRoleForState(workflowName, stateName);
        if (string.IsNullOrEmpty(roleName))
            roleName = data.Role;
        return _roleService.GetRole(roleName);
    }

    private void ReloadRegistry()
    {
        _roleService.Reload();
        _settings.LoadSettings();
        _registry.LoadFromConfigs(_settings, _roleService);
        _workflowService.Reload();
    }

    // Returns the last `count` user-exchange groups from the ChatCompletions state, oldest-first.
    private static List<JsonNode> ExtractTailExchanges(JsonArray state, int count)
    {
        List<int> userStarts = new List<int>();
        for (int i = 0; i < state.Count; i++)
        {
            JsonNode? n = state[i];
            if (n != null && n["role"]?.GetValue<string>() == "user")
                userStarts.Add(i);
        }

        if (userStarts.Count == 0)
            return new List<JsonNode>();

        int startGroup = userStarts.Count > count ? userStarts.Count - count : 0;
        int startIndex = userStarts[startGroup];

        List<JsonNode> result = new List<JsonNode>();
        for (int i = startIndex; i < state.Count; i++)
        {
            JsonNode? n = state[i];
            if (n != null)
                result.Add((JsonNode)n.DeepClone());
        }
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

    private void SendStats(BeastSession data, int maxContext)
    {
        string json = JsonSerializer.Serialize(new
        {
            model = data.Model,
            promptTokens = data.CumulativeInputTokens,
            completionTokens = data.CumulativeOutputTokens,
            totalCost = data.TotalCost,
            maxContext,
            contextTokens = data.GetContextLength()
        });
        _transport.Stats(json);
    }
}
