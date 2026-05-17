using Terminal.Gui;


// Owns the full-screen terminal layout and connects DockerContext, AgentTransport, and the UI.
public class BeastApp : IDisposable
{
    private readonly string _image;
    private readonly string? _initialPrompt;

    private ConversationModel? _model;
    private AgentTransport? _transport;
    private DockerContext? _docker;
    private string? _containerId;

    private Label? _statusLabel;
    private TextField? _inputField;

    public BeastApp(string image, string? initialPrompt)
    {
        _image = image;
        _initialPrompt = initialPrompt;
    }

    public int Run()
    {
        if (_initialPrompt != null)
            return RunConsole();

        RunTui();
        return 0;
    }

    private int RunConsole()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
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

            docker.RemoveContainerByNameAsync(containerName).GetAwaiter().GetResult();
            containerId = docker.LaunchContainerAsync("beastagent", containerName, null).GetAwaiter().GetResult();

            transport = new AgentTransport(model, status => Console.Error.WriteLine($"[agent] {status}"));
            transport.Start(docker);

            // Wire up model updates to print to stdout
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

            Console.Error.WriteLine("[beast] Connected. Sending prompt...");
            docker.SendAsync(_initialPrompt!).GetAwaiter().GetResult();

            // Wait for the conversation to finish (no more new messages for a while)
            // Simple approach: wait for the model to have at least one assistant response,
            // then wait a bit more for any trailing output.
            int lastCount = 0;
            int idleTicks = 0;
            while (!cts.IsCancellationRequested)
            {
                Thread.Sleep(500);
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
                try { docker.StopAndRemoveContainerAsync(containerId).GetAwaiter().GetResult(); } catch { }
            }
            docker?.Dispose();
        }

        return exitCode;
    }

    private void RunTui()
    {
        Application.Init();

        _model = new ConversationModel();

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

    private void OnInputKeyDown(View.KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key == Key.Enter)
        {
            string text = _inputField!.Text?.ToString() ?? "";
            _inputField.Text = "";
            if (text.Length > 0)
            {
                SendPrompt(text);
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

    private void SendPrompt(string text)
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
                _docker!.SendAsync(text).GetAwaiter().GetResult();
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
                null);

            _transport = new AgentTransport(_model!, SetStatus);
            _transport.Start(_docker);

            SetStatus("Connected.");

            if (_initialPrompt != null)
            {
                SendPrompt(_initialPrompt);
            }
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
        if (_containerId != null && _docker != null)
        {
            _docker.StopAndRemoveContainerAsync(_containerId).GetAwaiter().GetResult();
        }
        _docker?.Dispose();
    }
}
