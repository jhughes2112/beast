using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;


// Thin host: owns the input queue and one active SessionRunner.
// Routes transport input to the runner; swaps in a successor runner on compaction.
// All session execution logic lives in SessionRunner.
public class AgentOrchestrator
{
    private readonly ITransportServer _transport;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ConcurrentQueue<string> _inputQueue;
    private readonly LlmRegistry _registry;
    private readonly RoleService _roleService;
    private readonly SettingsService _settings;

    // Volatile so ReadInputAsync (background task) can call Interrupt() on the current runner
    // without a data race. Assignment is pointer-sized and atomic on all .NET platforms.
    private volatile SessionRunner? _activeRunner;

    public AgentOrchestrator(
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
        _inputQueue = new ConcurrentQueue<string>();
    }

    public async Task RunAsync()
    {
        CancellationToken ct = _cancellationTokenSource.Token;
        _ = ReadInputAsync(ct);

        SessionRunner runner = new SessionRunner(
            _inputQueue, _registry, _roleService, _settings, _transport, _cancellationTokenSource);

        while (!ct.IsCancellationRequested)
        {
            _activeRunner = runner;
            SessionRunnerExit exit = await runner.RunAsync(ct);

            if (exit == SessionRunnerExit.Cancelled)
                break;

            // ContextFull: compact and swap to the successor runner.
            SessionRunner? next = await runner.CompactAsync(ct);
            if (next == null)
            {
                _transport.Error("[orchestrator] Compaction failed; runner cannot continue.");
                break;
            }

            runner = next;
        }
    }

    private async Task ReadInputAsync(CancellationToken token)
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
                {
                    _transport.Debug("[orchestrator] /cancel received — interrupting active session");
                    _activeRunner?.Interrupt();
                }
                else
                {
                    _inputQueue.Enqueue(line);
                }
            }
        }
    }
}
