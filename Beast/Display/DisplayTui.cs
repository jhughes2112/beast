using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;


// TUI display: full terminal UI with message history, status bar, and input field.
public class DisplayTui : IDisplay
{
    private bool _verbose;
    private ConversationModel? _model;
    private Func<string, Task>? _sendAsync;
    private readonly List<string> _completions = new List<string>();
    private readonly List<string> _matches = new List<string>();

    private Label? _statusLabel;
    private TextField? _inputField;
    private Label? _completionLabel;
    private int _matchIndex;

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

    public void SetCompletions(IReadOnlyList<string> completions)
    {
        Application.MainLoop?.Invoke(() =>
        {
            _completions.Clear();
            foreach (string completion in completions)
            {
                _completions.Add(completion);
            }
            _matchIndex = 0;
            UpdateCompletionPreview();
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

        _completionLabel = new Label("")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Enabled = false
        };

        _inputField.KeyDown += OnInputKeyDown;

        top.Add(historyView, _statusLabel, _completionLabel, _inputField);
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
            _matchIndex = 0;
            UpdateCompletionPreview();
            if (text.Length > 0)
                await SendAsync(text);
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == Key.Tab)
        {
            AcceptCompletion();
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == Key.CursorUp)
        {
            CycleCompletion(-1);
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == Key.CursorDown)
        {
            CycleCompletion(1);
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
            _matchIndex = 0;
            UpdateCompletionPreview();
            args.Handled = true;
        }
        else
        {
            Application.MainLoop?.Invoke(UpdateCompletionPreview);
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

    private void CycleCompletion(int delta)
    {
        UpdateMatches();
        if (_matches.Count == 0)
        {
            UpdateCompletionPreview();
            return;
        }

        _matchIndex += delta;
        if (_matchIndex < 0)
        {
            _matchIndex = _matches.Count - 1;
        }
        else if (_matchIndex >= _matches.Count)
        {
            _matchIndex = 0;
        }

        UpdateCompletionPreview();
    }

    private void AcceptCompletion()
    {
        UpdateMatches();
        if (_matches.Count == 0)
        {
            return;
        }

        string completion = _matches[_matchIndex];
        _inputField!.Text = completion;
        UpdateCompletionPreview();
    }

    private void UpdateCompletionPreview()
    {
        if (_inputField == null || _completionLabel == null)
        {
            return;
        }

        UpdateMatches();

        string input = _inputField.Text?.ToString() ?? string.Empty;
        if (!input.StartsWith("/", StringComparison.Ordinal) || _matches.Count == 0)
        {
            _completionLabel.Text = string.Empty;
            return;
        }

        if (_matchIndex < 0 || _matchIndex >= _matches.Count)
        {
            _matchIndex = 0;
        }

        string match = _matches[_matchIndex];
        string remainder = match.StartsWith(input, StringComparison.OrdinalIgnoreCase)
            ? match.Substring(input.Length)
            : string.Empty;

        if (string.IsNullOrEmpty(remainder))
        {
            _completionLabel.Text = string.Empty;
            return;
        }

        int typedLength = input.Length;
        _completionLabel.X = typedLength;
        _completionLabel.Text = remainder;
    }

    private void UpdateMatches()
    {
        string input = _inputField?.Text?.ToString() ?? string.Empty;

        _matches.Clear();
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            _matchIndex = 0;
            return;
        }

        foreach (string completion in _completions)
        {
            if (completion.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            {
                _matches.Add(completion);
            }
        }

        if (_matchIndex >= _matches.Count)
        {
            _matchIndex = 0;
        }
    }
}
