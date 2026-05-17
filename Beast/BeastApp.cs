using Terminal.Gui;


// Owns the full-screen terminal layout and connects DockerContext, AgentTransport, and the UI.
public class BeastApp : IDisposable, IAsyncDisposable
{
    private readonly string _image;
    private readonly List<string> _agentSwitches;
    private readonly string? _initialPrompt;

    private CancellationTokenSource? _cts;
    private ConversationModel? _model;
    private AgentTransport? _transport;
    private DockerContext? _docker;
    private string? _containerId;

    private Label? _statusLabel;
    private TextField? _inputField;

    public BeastApp(string image, List<string> agentSwitches, string? initialPrompt)
    {
        _image = image;
        _agentSwitches = agentSwitches;
        _initialPrompt = initialPrompt;
    }

    public async Task<int> Run()
    {
        _cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        if (_initialPrompt != null || _agentSwitches.Count > 0)
            return await RunConsole(_cts.Token);

        RunTui(_cts.Token);
        return 0;
    }

    private async Task<int> RunConsole(CancellationToken cancellationToken)
    {
        ConversationModel model = new ConversationModel();
        int exitCode = 0;

        DockerContext? docker = null;
        AgentTransport? transport = null;
        string? containerId = null;

        try
        {
            Console.Error.WriteLine("[beast] Connecting to agent...");
            docker = new DockerContext();
            string containerName = $"beastagent_{Guid.NewGuid():N}";

            await docker.RemoveContainerByNameAsync(containerName);
            containerId = await docker.LaunchContainerAsync("beastagent", containerName, new List<string>());

            transport = new AgentTransport(model, status => Console.Error.WriteLine($"[agent] {status}"), () =>
            {
                Console.Error.WriteLine("[beast] Agent disconnected.");
                _cts?.Cancel();
            });
            transport.Start(docker);

            // Wire up model updates to print to stdout.
            model.MessageUpdated += msg =>
            {
                if (msg.Type == FrameType.Output && !string.IsNullOrEmpty(msg.Content))
                {
                    Console.WriteLine(msg.Content);
                }
                else if (msg.Type == FrameType.Error)
                {
                    Console.Error.WriteLine($"[error] {msg.Content}");
                }
                else if (msg.Type == FrameType.Status)
                {
                    Console.Error.WriteLine($"[status] {msg.Content}");
                }
            };

            Console.Error.WriteLine("[beast] Connected. Sending inputs...");
            foreach (string sw in _agentSwitches)
                await docker.SendAsync(sw);

            if (_initialPrompt != null)
                await docker.SendAsync(_initialPrompt);

            // Wait for the conversation to finish (no more new messages for a while)
            // Simple approach: wait for the model to have at least one assistant response,
            // then wait a bit more for any trailing output.
            int lastCount = 0;
            int idleTicks = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500);
                int currentCount = model.Messages.Count;
                if (currentCount > lastCount)
                {
                    lastCount = currentCount;
                    idleTicks = 0;
                }
                else
                {
                    idleTicks++;
                    // After 10 idle ticks (5 seconds) with no new messages, check if
                    // the last message is from the assistant (meaning it's done)
                    if (idleTicks >= 10 && lastCount > 0)
                    {
                        DisplayMessage lastMsg = model.Messages[model.Messages.Count - 1];
                        if (lastMsg.Type == FrameType.StreamEnd || lastMsg.Type == FrameType.Output)
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[beast] Error: {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            transport?.Dispose();
            if (containerId != null && docker != null)
            {
                try { await docker.StopAndRemoveContainerAsync(containerId); } catch { }
            }
            docker?.Dispose();
        }

        return exitCode;
    }

    private void RunTui(CancellationToken cancellationToken)
    {
        Application.Init();

        _model = new ConversationModel();

        // When Ctrl+C fires, stop the TUI event loop cleanly.
        cancellationToken.Register(() => Application.RequestStop());

        Toplevel top = Application.Top;
        top.ColorScheme = Colors.Base;

        MessageHistoryView historyView = new MessageHistoryView(_model)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2)
        };

        // Status bar: one line above the input.
        _statusLabel = new Label("")
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = Colors.Menu
        };

        // Input line pinned at the very bottom.
        _inputField = new TextField("")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
        };

        _inputField.KeyDown += OnInputKeyDown;

        top.Add(historyView, _statusLabel, _inputField);

        // Launch the Agent container asynchronously so the UI is already running when it connects.
        Task.Run(LaunchAgentAsync);

        // Focus the input immediately.
        _inputField.SetFocus();

        Application.Run();
        Application.Shutdown();
    }

    private async void OnInputKeyDown(View.KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key == Key.Enter)
        {
            string text = _inputField!.Text?.ToString() ?? "";
            _inputField.Text = "";
            if (text.Length > 0)
            {
                await SendPromptAsync(text);
            }
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == (Key.CtrlMask | Key.O))
        {
            CycleCollapseMode();
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == Key.Esc)
        {
            _inputField!.Text = "";
            args.Handled = true;
        }
    }

    private async Task SendPromptAsync(string text)
    {
        // _transport is assigned last in LaunchAgentAsync, after _docker and _stdio are both ready.
        // Checking _transport ensures we don't send before the container is fully initialized.
        if (_transport == null)
        {
            SetStatus("[not connected]");
        }
        else
        {
            try
            {
                await _docker!.SendAsync(text);
            }
            catch (Exception ex)
            {
                SetStatus($"[send error] {ex.Message}");
            }
        }
    }

    private async Task LaunchAgentAsync()
    {
        SetStatus("Connecting to agent...");
        try
        {
            _docker = new DockerContext();
            string containerName = $"beastagent_{Guid.NewGuid():N}";

            await _docker.RemoveContainerByNameAsync(containerName);
            _containerId = await _docker.LaunchContainerAsync(
                _image,
                containerName,
                new List<string>());

            _transport = new AgentTransport(_model!, SetStatus, () =>
            {
                SetStatus("Agent disconnected.");
                _cts?.Cancel();
            });
            _transport.Start(_docker);

            SetStatus("Connected.");

            foreach (string sw in _agentSwitches)
                await SendPromptAsync(sw);

            if (_initialPrompt != null)
                await SendPromptAsync(_initialPrompt);
        }
        catch (Exception ex)
        {
            SetStatus($"[launch error] {ex.Message}");
        }
    }

    private void CycleCollapseMode()
    {
        CollapseMode next = _model!.Mode switch
        {
            CollapseMode.Verbose   => CollapseMode.Minimized,
            CollapseMode.Minimized => CollapseMode.Quiet,
            _                      => CollapseMode.Verbose
        };
        _model.Mode = next;
        SetStatus($"View mode: {next}");
    }

    private void SetStatus(string text)
    {
        Application.MainLoop.Invoke(() =>
        {
            if (_statusLabel != null)
                _statusLabel.Text = text;
        });
    }

    public void Dispose()
    {
        _transport?.Dispose();
        _docker?.Dispose();
        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _transport?.Dispose();
        if (_containerId != null && _docker != null)
        {
            try { await _docker.StopAndRemoveContainerAsync(_containerId); } catch { }
        }
        _docker?.Dispose();
        _cts?.Dispose();
    }
}
