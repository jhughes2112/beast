using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Thin host: drives one SessionRunner at a time.
// Starts a ReadInputAsync loop paired to each runner; cancels and awaits it before swapping.
// All session execution logic lives in SessionRunner.
public class AgentOrchestrator
{
    private readonly ITransportServer _transport;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly LlmRegistry _registry;
    private readonly RoleService _roleService;
    private readonly SettingsService _settings;
    // True for a current-folder launch with no worktree: the root session is ephemeral and nothing is resumed.
    private readonly bool _ephemeral;

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

        // The first runner resumes the saved root, so it restores that root's child sessions into the
        // client's list; later runners (compaction-failure restarts) reuse the live session and must not.
        SessionRunner runner = new SessionRunner(LoadOrCreateSession(), _registry, _roleService, _settings, _transport, _cancellationTokenSource, true);

        while (!ct.IsCancellationRequested)
        {
            // A fresh linked CTS per iteration: cancelling it stops only this runner's input loop. Reusing
            // one across iterations would leave it cancelled, so a compaction-failure restart's input loop
            // would exit immediately and the user could never issue another command.
            using CancellationTokenSource inputCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task inputTask = ReadInputAsync(runner, inputCts.Token);
            Session currentSession = await runner.RunAsync(ct);
            inputCts.Cancel();
            await inputTask;

            if (ct.IsCancellationRequested)
                break;

            // RunAsync exited without cancellation (compaction failure) — keep the server alive
            // and restart on the same session so the user can issue commands and continue.
            runner = new SessionRunner(currentSession, _registry, _roleService, _settings, _transport, _cancellationTokenSource, false);
        }
    }

    private Session LoadOrCreateSession()
    {
        // A worktree launch resumes the session saved in that worktree; an ephemeral launch always starts fresh
        // and never persists, so it skips the resume entirely.
        if (!_ephemeral)
        {
            string? lastSessionId = SessionService.LoadLastSession();
            BeastSession? lastData = SessionService.LoadBySessionId(lastSessionId);
            if (lastData != null)
            {
                _transport.Status(lastData.Id, "Resumed session: " + lastData.DisplayName);
                return new Session(lastData, string.Empty, _transport, false);
            }
        }

        string roleName = string.Empty;
        foreach (Role r in _roleService.Roles.Values)
        {
            roleName = r.Name;
            break;
        }
        Role? role = _roleService.GetRole(roleName);
        string systemPrompt = role?.SystemPrompt ?? string.Empty;
        LlmModel? model = role != null ? _registry.GetModelForRole(role, string.Empty, 0) : null;
        string modelId = model?.ConfigId ?? string.Empty;
        BeastSession fresh = new BeastSession(Guid.NewGuid().ToString(), string.Empty, modelId, roleName, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, _ephemeral, 0);
        return new Session(fresh, systemPrompt, _transport, false);
    }

    private async Task ReadInputAsync(SessionRunner runner, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string? line = await _transport.TryReadAsync(100, token);
            if (line == null)
                break;
            if (line.Length == 0)
                continue;

            // Inbound wire format: sessionId|content
            // Falls back to the active session if no pipe is present (e.g. debug transport).
            int pipe = line.IndexOf('|');
            string sessionId = pipe >= 0 ? line.Substring(0, pipe) : runner.ActiveSessionId;
            string content = pipe >= 0 ? line.Substring(pipe + 1) : line;
            if (content.Length == 0)
                continue;

            runner.Deliver(sessionId, content);
        }
    }
}
