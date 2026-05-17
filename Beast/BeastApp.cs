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

    public void Run()
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
            return;
        }

        try
        {
            _docker!.SendAsync(text).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            SetStatus($"[send error] {ex.Message}");
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
