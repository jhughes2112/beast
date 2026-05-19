using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;


// TUI display: full terminal UI with message history, status bar, and input field.
// Layout (bottom-up):
//   Row -1  persistent statusbar: folder | tokens/cost ... context/model
//   Row -2  flash status label (reverts to empty after a few seconds)
//   Row -3  input field (white text)
//   above   autocomplete popup (up to 5 rows, gray)
//   rest    message history
public class DisplayTui : IDisplay
{
    private const int PopupRows = 5;
    private const int FlashMs = 4000;

    private CollapseMode _initialMode;
    private ConversationModel? _model;
    private Func<string, Task>? _sendAsync;

    // _completions comes from the agent (via SetCompletions); /verbose is beast-local.
    private readonly List<string> _completions = new List<string> { "/verbose" };
    private readonly List<string> _matches = new List<string>();

    private Label? _flashLabel;
    private Label? _persistentBar;
    private TextField? _inputField;
    private ListView? _popupList;
    private Toplevel? _top;
    private int _matchIndex;
    private Action? _requestExit;

    // Persistent bar content.
    private string _statsModel = "";
    private int _statsPrompt;
    private int _statsCompletion;
    private decimal _statsCost;
    private int _statsMaxContext;
    private int _statsContextTokens;

    // Flash timer state.
    private System.Threading.Timer? _flashTimer;

    public DisplayTui(CollapseMode initialMode)
    {
        _initialMode = initialMode;
    }

    public void Attach(ConversationModel model)
    {
        _model = model;
        _model.Mode = _initialMode;
    }

    public void SetStatus(string text)
    {
        Application.MainLoop?.Invoke(() =>
        {
            if (_flashLabel == null) return;
            _flashLabel.Text = text;
            _flashTimer?.Dispose();
            _flashTimer = new System.Threading.Timer(_ =>
            {
                Application.MainLoop?.Invoke(() =>
                {
                    if (_flashLabel != null)
                        _flashLabel.Text = "";
                });
            }, null, FlashMs, Timeout.Infinite);
        });
    }

    public void SetStatsInfo(string model, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens)
    {
        _statsModel = model;
        _statsPrompt = promptTokens;
        _statsCompletion = completionTokens;
        _statsCost = totalCost;
        _statsMaxContext = maxContext;
        _statsContextTokens = contextTokens;
        Application.MainLoop?.Invoke(UpdatePersistentBar);
    }

    public void SetCompletions(IReadOnlyList<string> completions)
    {
        Application.MainLoop?.Invoke(() =>
        {
            // Keep the beast-local /verbose entry; replace the rest.
            _completions.Clear();
            _completions.Add("/verbose");
            foreach (string completion in completions)
                _completions.Add(completion);

            _matchIndex = 0;
            RefreshPopup();
        });
    }

    public void OnStreamStart(int streamIndex, FrameType type) { }
    public void OnStreamChunk(string chunk) { }
    public void OnStreamEnd() { }

    public void SetSendAsync(Func<string, Task> sendAsync)
    {
        _sendAsync = sendAsync;
    }

    public void SetRequestExit(Action requestExit)
    {
        _requestExit = requestExit;
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        Application.Init();

        cancellationToken.Register(() => Application.RequestStop());

        _top = Application.Top;
        _top.ColorScheme = BuildScheme(Color.White, Color.Black);

        MessageHistoryView historyView = new MessageHistoryView(_model!)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3)
        };

        // Popup sits just above the input line; hidden by default.
        _popupList = new ListView(new List<string>())
        {
            X = 0,
            Y = Pos.AnchorEnd(3 + PopupRows),
            Width = Dim.Fill(),
            Height = PopupRows,
            Visible = false,
            ColorScheme = BuildScheme(Color.Gray, Color.Black)
        };

        _inputField = new TextField("")
        {
            X = 0,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = BuildScheme(Color.White, Color.Black)
        };

        // Flash label: shows transient status messages, clears after FlashMs.
        _flashLabel = new Label("")
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = BuildScheme(Color.Gray, Color.Black)
        };

        // Persistent bar: folder on left, tokens/cost in middle-left, context+model on right.
        _persistentBar = new Label("")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = BuildScheme(Color.Gray, Color.Black)
        };

        _inputField.KeyDown += OnInputKeyDown;

        _top.Add(historyView, _popupList, _inputField, _flashLabel, _persistentBar);
        _inputField.SetFocus();

        UpdatePersistentBar();

        Application.Run();
        Application.Shutdown();

        _flashTimer?.Dispose();

        return Task.CompletedTask;
    }

    private void UpdatePersistentBar()
    {
        if (_persistentBar == null) return;

        string left = Directory.GetCurrentDirectory();

        string mid = _statsPrompt > 0 || _statsCompletion > 0
            ? $"  in:{_statsPrompt} out:{_statsCompletion} ${_statsCost:F4}"
            : "";

        string contextInfo = _statsMaxContext > 0 && _statsContextTokens > 0
            ? $"{(int)((double)_statsContextTokens / _statsMaxContext * 100)}%/{_statsMaxContext} "
            : "";
        string right = string.IsNullOrEmpty(_statsModel) ? "" : contextInfo + _statsModel;

        int width = _persistentBar.Frame.Width;
        if (width <= 0) width = 80;

        string combined = left + mid;
        string bar;
        if (string.IsNullOrEmpty(right))
        {
            bar = combined;
        }
        else
        {
            int padding = width - combined.Length - right.Length;
            if (padding < 1) padding = 1;
            bar = combined + new string(' ', padding) + right;
        }

        _persistentBar.Text = bar;
    }

    private static ColorScheme BuildScheme(Color fg, Color bg)
    {
        Terminal.Gui.Attribute normal = Terminal.Gui.Attribute.Make(fg, bg);
        return new ColorScheme
        {
            Normal = normal,
            Focus = normal,
            HotNormal = normal,
            HotFocus = normal,
            Disabled = Terminal.Gui.Attribute.Make(Color.Gray, bg)
        };
    }

    private async void OnInputKeyDown(View.KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key == Key.Enter)
        {
            string text = _inputField!.Text?.ToString() ?? "";
            _inputField.Text = "";
            _matchIndex = 0;
            HidePopup();
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
        else if (args.KeyEvent.Key == (Key.CtrlMask | Key.C) || args.KeyEvent.Key == (Key.CtrlMask | Key.D))
        {
            _requestExit?.Invoke();
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == (Key.CtrlMask | Key.O))
        {
            CycleCollapseMode();
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == Key.Esc)
        {
            if (_popupList != null && _popupList.Visible)
            {
                HidePopup();
            }
            else
            {
                _inputField!.Text = "";
            }
            args.Handled = true;
        }
        else
        {
            Application.MainLoop?.Invoke(RefreshPopup);
        }
    }

    private const string HelpText = "Commands: /compact, /clear, /reload, /role <id>, /model <id>, /session <id>, /verbose, /test, /quit";

    // Agent command verbs that are valid to forward.
    private static readonly HashSet<string> AgentVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "compact", "clear", "reload", "role", "model", "session", "test", "quit", "ping", "history"
    };

    private async Task SendAsync(string text)
    {
        if (text.StartsWith("/", StringComparison.Ordinal))
        {
            string trimmed = text.Substring(1).Trim();
            int spaceIdx = trimmed.IndexOf(' ');
            string verb = spaceIdx >= 0 ? trimmed.Substring(0, spaceIdx) : trimmed;

            if (verb.Equals("verbose", StringComparison.OrdinalIgnoreCase))
            {
                CycleCollapseMode();
                return;
            }

            if (verb.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(HelpText);
                return;
            }

            if (!AgentVerbs.Contains(verb))
            {
                SetStatus($"Unknown command: /{verb}  —  {HelpText}");
                return;
            }
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
            HidePopup();
            return;
        }

        _matchIndex += delta;
        if (_matchIndex < 0)
            _matchIndex = _matches.Count - 1;
        else if (_matchIndex >= _matches.Count)
            _matchIndex = 0;

        RefreshPopup();
    }

    private void AcceptCompletion()
    {
        UpdateMatches();
        if (_matches.Count == 0)
            return;

        string completion = _matches[_matchIndex];
        _inputField!.Text = completion;
        _inputField.CursorPosition = completion.Length;

        HidePopup();
    }

    private void RefreshPopup()
    {
        if (_inputField == null || _popupList == null)
            return;

        UpdateMatches();

        if (_matches.Count == 0)
        {
            HidePopup();
            return;
        }

        if (_matchIndex < 0 || _matchIndex >= _matches.Count)
            _matchIndex = 0;

        _popupList.SetSource(_matches);
        _popupList.SelectedItem = _matchIndex;
        _popupList.Visible = true;

        int rows = Math.Min(PopupRows, _matches.Count);
        _popupList.Height = rows;
        _popupList.Y = Pos.AnchorEnd(3 + rows);

        _top?.SetNeedsDisplay();
    }

    private void HidePopup()
    {
        if (_popupList == null)
            return;

        _popupList.Visible = false;
        _top?.SetNeedsDisplay();
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
                _matches.Add(completion);
        }

        if (_matchIndex >= _matches.Count)
            _matchIndex = 0;
    }
}
