using System;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;


// TUI display: full terminal UI with message history, status bar, and input field.
public class DisplayTui : IDisplay
{
    private bool _verbose;
    private ConversationModel? _model;
    private Func<string, Task>? _sendAsync;

    private Label? _statusLabel;
    private TextField? _inputField;

    public DisplayTui(bool verbose)
    {
        _verbose = verbose;
    }

    public void Attach(ConversationModel model)
    {
        _model = model;
    }

    public void SetStatus(string text)
    {
        Application.MainLoop?.Invoke(() =>
        {
            if (_statusLabel != null)
                _statusLabel.Text = text;
        });
    }

    public void OnStreamStart(int streamIndex, FrameType type) { }
    public void OnStreamChunk(string chunk) { }
    public void OnStreamEnd() { }

    public void SetSendAsync(Func<string, Task> sendAsync)
    {
        _sendAsync = sendAsync;
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        Application.Init();

        cancellationToken.Register(() => Application.RequestStop());

        Toplevel top = Application.Top;
        top.ColorScheme = Colors.Base;

        MessageHistoryView historyView = new MessageHistoryView(_model!)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2)
        };

        _statusLabel = new Label("")
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = Colors.Menu
        };

        _inputField = new TextField("")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
        };

        _inputField.KeyDown += OnInputKeyDown;

        top.Add(historyView, _statusLabel, _inputField);
        _inputField.SetFocus();

        Application.Run();
        Application.Shutdown();

        return Task.CompletedTask;
    }

    private async void OnInputKeyDown(View.KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key == Key.Enter)
        {
            string text = _inputField!.Text?.ToString() ?? "";
            _inputField.Text = "";
            if (text.Length > 0)
                await SendAsync(text);
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

    private async Task SendAsync(string text)
    {
        if (text.Equals("/verbose", StringComparison.OrdinalIgnoreCase))
        {
            _verbose = !_verbose;
            SetStatus(_verbose ? "Verbose: on" : "Verbose: off");
            return;
        }

        if (_sendAsync == null)
        {
            SetStatus("[not connected]");
        }
        else
        {
            try
            {
                await _sendAsync(text);
            }
            catch (Exception ex)
            {
                SetStatus($"[send error] {ex.Message}");
            }
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
}
