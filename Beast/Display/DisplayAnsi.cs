using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TextCopy;


// Full-screen TUI: every cell is explicitly positioned via direct ANSI codes; the terminal never scrolls.
// Uses the alternate screen buffer so no scrollback is visible.
// Layout (bottom-up):
//   H-1              : status bar
//   H-2..(H-2-R+1)   : input area (R rows, grows upward, capped at MaxInputRows)
//   H-2-R            : separator
//   0..(H-3-R)       : history (scrollable with PageUp/PageDown)
public class DisplayAnsi : IDisplay
{
    private const string HelpText = "Commands: /compact, /clear, /reload, /role <id>, /model <id>, /session <id>, /verbose, /test, /quit";
    private const string PromptPrefix = "» ";
    private const int MaxInputRows = 10;

    private static readonly HashSet<string> AgentVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "compact", "clear", "reload", "role", "model", "session", "test", "quit", "ping", "history"
    };

    private readonly CollapseMode _initialMode;
    private ConversationModel? _model;
    private Func<string, Task>? _sendAsync;
    private Action? _requestExit;
    private CancellationTokenSource? _runCts;

    private readonly List<string> _completions = new List<string> { "/verbose" };
    private readonly object _consoleLock = new object();

    private string _currentInputText = "";
    private int _currentInputCursor = 0;
    private string _statusText = "";
    private string _statsText = "";

    // _historyScrollOffset > 0 means scrolled back (pinned); 0 means follow-mode (auto-scroll to bottom).
    private float _historyScrollOffset = 0f;
    private float _scrollTarget = 0f;
    private int _lastWidth = 0;
    private int _lastHeight = 0;

    // Scroll animation: exponential decay toward _scrollTarget each tick (~16ms).
    // Alpha tuned so ~95% of the distance is covered in ~0.2s (~13 ticks).
    private const float ScrollAlpha = 0.22f;

    // Slot index of the currently active stream, or -1 if none.
    private int _streamingSlot = -1;

    // Per-slot rendered markdown row cache. Invalidated when width changes or message updates.
    private readonly Dictionary<int, List<string>> _renderedRows = new Dictionary<int, List<string>>();
    private int _renderedWidth = 0;

    // Maps screen row → message slot index (rebuilt each Redraw for click-to-toggle).
    private readonly Dictionary<int, int> _rowToSlot = new Dictionary<int, int>();

    // Scrollbar geometry from the last Redraw, used to map scrollbar clicks to scroll position.
    private int _scrollbarTopRow = 0;
    private int _scrollbarHeight = 0;
    private int _scrollbarMaxOffset = 0;

    // Slot index the mouse is currently hovering over, or -1 if none.
    private int _hoverSlot = -1;

    // TickCount64 deadline until which the scrollbar overlay is visible; 0 = hidden.
    private long _scrollbarShowUntil = 0;
    private const int ScrollbarShowMs = 500;

    // Single reusable output buffer: all Redraw writes accumulate here and are flushed once.
    private readonly StringBuilder _drawBuf = new StringBuilder(65536);

    // Large-buffered stdout writer: batches an entire frame into one WriteFile call to eliminate flicker.
    private StreamWriter? _bufferedOut;

    // True when the last frame was a different terminal size (triggers EraseScreen before next blit).
    private bool _needsErase = true;

    public bool RequestHistory => true;

    private static class Ansi
    {
        public const string Reset             = "\x1b[0m";
        public const string EraseScreen       = "\x1b[2J";
        public const string HideCursor        = "\x1b[?25l";
        public const string ShowCursor        = "\x1b[?25h";
        public const string EnterAltScreen    = "\x1b[?1049h";
        public const string ExitAltScreen     = "\x1b[?1049l";
        public const string EnableMouse   = "\x1b[?1000h\x1b[?1003h\x1b[?1006h";
        public const string DisableMouse  = "\x1b[?1006l\x1b[?1003l\x1b[?1000l";
        public const string DisableWrap   = "\x1b[?7l";
        public const string EnableWrap    = "\x1b[?7h";

        public const string InputText      = "\x1b[38;5;255m\x1b[48;5;235m";   // bright white on very dark bg
        public const string Silver         = "\x1b[38;5;250m";
        public const string Grey           = "\x1b[38;5;248m";                   // dim labels
        public const string BrightUser     = "\x1b[38;5;231m";                   // user messages — pure white
        public const string MedGrey        = "\x1b[38;5;245m";                   // status bar, dim labels
        public const string ItalicGrey     = "\x1b[3;38;5;245m";                 // thinking — italic, not dim
        public const string Red            = "\x1b[38;5;196m";
        public const string Blue           = "\x1b[38;5;33m";
        public const string Orange         = "\x1b[38;5;166m";
        public const string BrightWhite    = "\x1b[38;5;255m";
        // Tool call: bright cyan text on dark blue-tinted background.
        public const string ToolCallFg     = "\x1b[38;5;51m\x1b[48;5;17m";
        // Tool call error: yellow-orange on dark red background.
        public const string ToolCallErrFg  = "\x1b[38;5;214m\x1b[48;5;52m";
        // Tool response: subdued teal on near-black background (box-styled by renderer).
        public const string ToolResponseFg = "\x1b[38;5;73m\x1b[48;5;233m";
        // Scrollbar thumb and track: shown temporarily during scroll/mouse activity.
        public const string ScrollThumbBg  = "\x1b[48;5;240m";
        public const string ScrollTrackBg  = "\x1b[48;5;233m";   // barely non-black track
        // Hover: single-column left-edge accent on the hovered block.
        public const string HoverBar        = "\x1b[48;5;244m";   // medium-grey left sliver
    }

    public DisplayAnsi(CollapseMode initialMode)
    {
        _initialMode = initialMode;
    }

    public void Attach(ConversationModel model)
    {
        _model = model;
        _model.Mode = _initialMode;
        _model.MessageUpdated += OnMessageUpdated;
    }

    public void SetStatus(string text)
    {
        lock (_consoleLock)
        {
            _statusText = text;
            Redraw();
        }
    }

    public void SetStatsInfo(string model, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens)
    {
        string contextInfo = maxContext > 0 && contextTokens > 0
            ? $"{(int)((double)contextTokens / maxContext * 100)}%/{maxContext} "
            : "";
        string stats = promptTokens > 0 || completionTokens > 0
            ? $"in:{promptTokens} out:{completionTokens} ${totalCost:F4}  {contextInfo}{model}"
            : model;
        lock (_consoleLock)
        {
            _statsText = stats;
            Redraw();
        }
    }

    public void SetCompletions(IReadOnlyList<string> completions)
    {
        lock (_consoleLock)
        {
            _completions.Clear();
            _completions.Add("/verbose");
            foreach (string c in completions)
                _completions.Add(c);
        }
    }

    // Track active stream slot so the render cache skips markdown during streaming.
    public void OnStreamStart(int streamIndex, FrameType type) { lock (_consoleLock) _streamingSlot = streamIndex; }
    public void OnStreamChunk(string chunk) { }
    public void OnStreamEnd() { lock (_consoleLock) _streamingSlot = -1; }

    public void SetSendAsync(Func<string, Task> sendAsync) { _sendAsync = sendAsync; }
    public void SetRequestExit(Action requestExit) { _requestExit = requestExit; }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _statusText = Directory.GetCurrentDirectory();

        // Enable VT processing and disable Quick Edit before any VT sequences are written.
        // Encoding must be set first because .NET may reset console mode when swapping the writer.
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding  = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        // Apply twice: SetConsoleOutputCP (triggered by encoding change) can clear VT processing mode.
        WindowsConsole.EnableVirtualTerminal();
        WindowsConsole.ReapplyModes();

        // Replace Console.Out with a large-buffered writer so each full frame is one WriteFile call.
        _bufferedOut = new StreamWriter(
            Console.OpenStandardOutput(bufferSize: 131072),
            encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 131072,
            leaveOpen: false);
        _bufferedOut.AutoFlush = false;
        Console.SetOut(_bufferedOut);

        _lastWidth = Console.WindowWidth;
        _lastHeight = Console.WindowHeight;

        Console.Write(Ansi.EnterAltScreen);
        Console.Write(Ansi.DisableWrap);
        Console.Write(Ansi.EnableMouse);
        Console.CursorVisible = false;

        lock (_consoleLock)
            Redraw();

        InputLoop(_runCts.Token);

        Console.Write(Ansi.DisableMouse);
        Console.Write(Ansi.EnableWrap);
        Console.Write(Ansi.ExitAltScreen);
        Console.Out.Flush();
        Console.CursorVisible = true;
        WindowsConsole.Restore();
        // Restore original stdout so the process exits cleanly.
        if (_bufferedOut != null)
        {
            StreamWriter restore = new StreamWriter(
                Console.OpenStandardOutput(),
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            restore.AutoFlush = true;
            Console.SetOut(restore);
            _bufferedOut.Dispose();
            _bufferedOut = null;
        }
        return Task.CompletedTask;
    }

    private void OnMessageUpdated(DisplayMessage msg)
    {
        lock (_consoleLock)
        {
            if (msg.Type == FrameType.Clear)
            {
                _historyScrollOffset = 0f;
                _renderedRows.Clear();
                _needsErase = true;
            }
            else
            {
                _renderedRows.Remove(msg.Index);
            }
            Redraw();
        }
    }

    // Redraws the entire screen. Must be called under _consoleLock.
    private void Redraw()
    {
        int w = Console.WindowWidth;
        int h = Console.WindowHeight;
        if (w < 10 || h < 5) return;

        // Invalidate render cache on width change.
        if (w != _renderedWidth)
        {
            _renderedRows.Clear();
            _renderedWidth = w;
        }

        // Trigger a full erase-before-blit when terminal size changes.
        if (w != _lastWidth || h != _lastHeight)
        {
            _lastWidth = w;
            _lastHeight = h;
            _needsErase = true;
        }

        int rawInputRows = ComputeInputRows(_currentInputText, w);
        int inputRows = Math.Min(rawInputRows, Math.Min(MaxInputRows, Math.Max(1, h / 3)));
        int skip = rawInputRows - inputRows;

        int statusRow    = h - 1;
        int inputEndRow  = h - 2;
        int inputStart   = inputEndRow - inputRows + 1;
        int separatorRow = inputStart - 1;
        int historyH     = separatorRow;

        _drawBuf.Clear();
        _drawBuf.Append(Ansi.HideCursor);

        // On resize erase once so cells outside the new area are cleared; then home and blit.
        if (_needsErase)
        {
            _drawBuf.Append(Ansi.EraseScreen);
            _needsErase = false;
        }
        // Home cursor — every row is overwritten in order so no erase is needed between frames.
        _drawBuf.Append("\x1b[H");

        List<(string AnsiCode, string Text, int SlotIndex)> histRows = BuildHistoryRows(w);
        int totalRows = histRows.Count;
        int maxOffset = Math.Max(0, totalRows - historyH);
        if (_scrollTarget > maxOffset) _scrollTarget = maxOffset;
        if (_scrollTarget < 0f) _scrollTarget = 0f;
        if (_historyScrollOffset > maxOffset) _historyScrollOffset = maxOffset;
        if (_historyScrollOffset < 0f) _historyScrollOffset = 0f;

        // Scrollbar geometry (stored for click mapping).
        _scrollbarTopRow    = 0;
        _scrollbarHeight    = historyH;
        _scrollbarMaxOffset = maxOffset;

        int thumbH   = maxOffset > 0 ? Math.Max(1, (int)Math.Round((double)historyH * historyH / totalRows)) : historyH;
        int thumbTop = maxOffset > 0
            ? (int)Math.Round((double)(maxOffset - _historyScrollOffset) / maxOffset * (historyH - thumbH))
            : 0;
        thumbTop = Math.Max(0, Math.Min(historyH - thumbH, thumbTop));

        int startIdx = totalRows - historyH - (int)Math.Round(_historyScrollOffset);

        _rowToSlot.Clear();
        for (int r = 0; r < historyH; r++)
        {
            int idx = startIdx + r;
            if (idx >= 0 && idx < histRows.Count)
            {
                AppendRow(_drawBuf, topRow: r, w: w, ansiCode: histRows[idx].AnsiCode, text: histRows[idx].Text);
                if (histRows[idx].SlotIndex >= 0)
                    _rowToSlot[r] = histRows[idx].SlotIndex;
            }
            else
            {
                AppendRow(_drawBuf, topRow: r, w: w, ansiCode: "", text: "");
            }
        }

        // Overlay a single-column left-edge accent on every row of the hovered block.
        if (_hoverSlot >= 0)
        {
            for (int r = 0; r < historyH; r++)
            {
                if (_rowToSlot.TryGetValue(r, out int rowSlot) && rowSlot == _hoverSlot)
                {
                    _drawBuf.Append($"\x1b[{r + 1};1H");
                    _drawBuf.Append(Ansi.HoverBar);
                    _drawBuf.Append(' ');
                    _drawBuf.Append(Ansi.Reset);
                }
            }
        }

        // Overlay scrollbar on the last two columns only while scroll/mouse activity is recent.
        if (maxOffset > 0 && Environment.TickCount64 < _scrollbarShowUntil)
        {
            for (int r = 0; r < historyH; r++)
            {
                bool inThumb = r >= thumbTop && r < thumbTop + thumbH;
                _drawBuf.Append($"\x1b[{r + 1};{w - 1}H");
                _drawBuf.Append(inThumb ? Ansi.ScrollThumbBg : Ansi.ScrollTrackBg);
                _drawBuf.Append("  ");
                _drawBuf.Append(Ansi.Reset);
            }
        }

        // Separator row.
        AppendRow(_drawBuf, separatorRow, w, Ansi.BrightWhite, new string('─', w));

        // Input rows (use full width).
        List<string> inputLines = WrapInput(_currentInputText, w);
        for (int r = 0; r < inputRows; r++)
        {
            int lineIdx = skip + r;
            string line = lineIdx < inputLines.Count ? inputLines[lineIdx] : PromptPrefix;
            AppendRow(_drawBuf, inputStart + r, w, Ansi.InputText, line);
        }

        // Status bar (full width).
        string statusLine = BuildStatusLine(_statusText, _statsText, w);
        AppendRow(_drawBuf, statusRow, w, Ansi.MedGrey, statusLine);

        // Reposition cursor and show it.
        (int curRow, int curCol) = GetCursorScreenPos(inputStart, skip, w);
        _drawBuf.Append($"\x1b[{Math.Max(1, Math.Min(h, curRow + 1))};{Math.Max(1, curCol + 1)}H");
        _drawBuf.Append(Ansi.ShowCursor);

        Console.Write(_drawBuf);
        Console.Out.Flush();
    }

    private (int Row, int Col) GetCursorScreenPos(int inputStartRow, int skip, int w)
    {
        (int lineIdx, int col) = CursorInInputLines(_currentInputText, _currentInputCursor, w);
        int visibleLine = Math.Max(0, lineIdx - skip);
        return (inputStartRow + visibleLine, col);
    }

    private List<(string AnsiCode, string Text, int SlotIndex)> BuildHistoryRows(int w)
    {
        List<(string, string, int)> rows = new List<(string, string, int)>();
        if (_model == null) return rows;

        foreach (DisplayMessage msg in _model.Messages)
        {
            if (ConversationModel.ShouldHide(msg.Type, _model.Mode)) continue;
            if (string.IsNullOrEmpty(msg.Content)) continue;

            bool isError = msg.Type == FrameType.ToolCall && msg.PairedResponseIsError;
            string displayAnsi = isError ? Ansi.ToolCallErrFg : AnsiForType(msg.Type);
            string prefix = PrefixTextForType(msg.Type, msg.Collapsed);
            // ToolCall and Thinking blocks do not get a trailing blank spacer row (they are compact).
            bool addSpacer = msg.Type != FrameType.ToolCall && msg.Type != FrameType.Thinking;

            if (msg.Collapsed)
            {
                string summary = msg.Type == FrameType.ToolCall
                    ? FormatToolCallSummary(msg.Content)
                    : msg.Content.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

                // Ellipsis only when content was visually cut or the expanded form has more rows.
                bool hasMoreContent = msg.Type == FrameType.ToolCall
                    ? msg.Content.Contains('(') || !string.IsNullOrEmpty(msg.PairedResponseContent)
                    : msg.Content.Contains('\n') || msg.Content.Contains('\r');

                int availW = w - prefix.Length;
                bool summaryTruncated = AnsiString.VisibleLength(summary) > availW - 1;
                bool needsEllipsis = summaryTruncated || hasMoreContent;

                string line = needsEllipsis
                    ? prefix + AnsiString.TruncateVisible(summary, availW - 1) + "\u2026"
                    : prefix + AnsiString.TruncateVisible(summary, availW);
                rows.Add((displayAnsi, line, msg.Index));
                if (addSpacer) rows.Add(("", "", msg.Index));
                continue;
            }

            bool isStreaming = msg.Index == _streamingSlot;

            if (!isStreaming && !_renderedRows.TryGetValue(msg.Index, out List<string>? cached))
            {
                cached = msg.Type == FrameType.ToolCall
                    ? RenderToolCallRows(msg, w)
                    : RenderMessageRows(msg, w);
                _renderedRows[msg.Index] = cached;
            }

            List<string>? rendered = isStreaming
                ? RenderMessageRowsRaw(msg, w)
                : _renderedRows[msg.Index];

            foreach (string line in rendered)
            {
                rows.Add((displayAnsi, line, msg.Index));
            }

            if (addSpacer) rows.Add(("", "", msg.Index));
        }

        return rows;
    }

    // Renders a message to a list of screen-width strings (may contain ANSI codes).
    private static List<string> RenderMessageRows(DisplayMessage msg, int w)
    {
        List<string> result = new List<string>();
        string prefix = PrefixTextForType(msg.Type, collapsed: false);
        bool useMarkdown = msg.Type == FrameType.Output || msg.Type == FrameType.System || msg.Type == FrameType.User || msg.Type == FrameType.ToolResponse;

        if (useMarkdown)
        {
            List<string> mdLines = MarkdownAnsi.Render(ExpandTabs(msg.Content), msg.Type, w);
            bool firstLine = true;
            foreach (string mdLine in mdLines)
            {
                string full = firstLine ? prefix + mdLine : mdLine;
                firstLine = false;
                foreach (string wrapped in AnsiString.Wrap(full, w))
                    result.Add(wrapped);
            }

            if (result.Count == 0)
                result.Add(prefix);
        }
        else
        {
            string[] logicalLines = msg.Content.Split('\n');
            bool first = true;
            foreach (string line in logicalLines)
            {
                string full = first ? prefix + ExpandTabs(line) : ExpandTabs(line);
                first = false;
                List<string> wrapped = AnsiString.Wrap(full, w);
                foreach (string wl in wrapped)
                    result.Add(wl);
            }
        }

        return result;
    }

    // Plain-text render for streaming slots: no markdown parsing, just wrap raw content.
    private static List<string> RenderMessageRowsRaw(DisplayMessage msg, int w)
    {
        List<string> result = new List<string>();
        string prefix = PrefixTextForType(msg.Type, collapsed: false);
        string[] logicalLines = msg.Content.Split('\n');
        bool first = true;
        foreach (string line in logicalLines)
        {
            string full = first ? prefix + ExpandTabs(line) : ExpandTabs(line);
            first = false;
            foreach (string wl in AnsiString.Wrap(full, w))
                result.Add(wl);
        }
        if (result.Count == 0)
            result.Add(prefix);
        return result;
    }

    private static (int LineIdx, int Col) CursorInInputLines(string text, int cursor, int w)
    {
        string[] logicalLines = text.Split('\n');
        int remaining = cursor;
        int screenLine = 0;

        for (int li = 0; li < logicalLines.Length; li++)
        {
            string prefix = li == 0 ? PromptPrefix : "  ";
            int logLen = logicalLines[li].Length;

            if (remaining <= logLen || li == logicalLines.Length - 1)
            {
                int colInFull = prefix.Length + remaining;
                return (screenLine + colInFull / w, colInFull % w);
            }

            remaining -= logLen + 1;
            int fullLen = prefix.Length + logicalLines[li].Length;
            screenLine += Math.Max(1, (int)Math.Ceiling((double)fullLen / w));
        }

        return (screenLine, 0);
    }

    private static int CharFromInputLines(string text, int targetLine, int targetCol, int w)
    {
        string[] logicalLines = text.Split('\n');
        int screenLine = 0;
        int charPos = 0;

        for (int li = 0; li < logicalLines.Length; li++)
        {
            string prefix = li == 0 ? PromptPrefix : "  ";
            int fullLen = prefix.Length + logicalLines[li].Length;
            int wrappedRows = Math.Max(1, (int)Math.Ceiling((double)fullLen / w));

            if (screenLine + wrappedRows > targetLine)
            {
                int rowWithin = targetLine - screenLine;
                int colInFull = Math.Min(rowWithin * w + targetCol, fullLen);
                int chars = Math.Max(0, colInFull - prefix.Length);
                return charPos + chars;
            }

            screenLine += wrappedRows;
            charPos += logicalLines[li].Length + (li < logicalLines.Length - 1 ? 1 : 0);
        }

        return text.Length;
    }

    private static List<string> WrapInput(string text, int w)
    {
        List<string> result = new List<string>();
        string[] logicalLines = text.Split('\n');
        for (int li = 0; li < logicalLines.Length; li++)
        {
            string prefix = li == 0 ? PromptPrefix : "  ";
            string full = prefix + ExpandTabs(logicalLines[li]);
            if (full.Length == 0) { result.Add(prefix); continue; }
            foreach (string wl in AnsiString.Wrap(full, w))
                result.Add(wl);
        }

        if (result.Count == 0)
            result.Add(PromptPrefix);
        return result;
    }

    private static int ComputeInputRows(string text, int w)
    {
        return WrapInput(text, w).Count;
    }

    private static string ExpandTabs(string text) => text.Replace("\t", "    ");

    // Writes one full row: positions cursor, emits color-coded content padded to exactly w visible chars, resets.
    private static void AppendRow(StringBuilder buf, int topRow, int w, string ansiCode, string text)
    {
        buf.Append($"\x1b[{topRow + 1};1H");
        if (!string.IsNullOrEmpty(ansiCode))
            buf.Append(ansiCode);
        string truncated = AnsiString.TruncateVisible(text, w);
        buf.Append(truncated);
        int visible = AnsiString.VisibleLength(truncated);
        if (visible < w)
        {
            // Re-apply the row code so padding inherits the row background even after embedded resets.
            if (!string.IsNullOrEmpty(ansiCode))
                buf.Append(ansiCode);
            buf.Append(new string(' ', w - visible));
        }
        buf.Append(Ansi.Reset);
    }

    private static string BuildStatusLine(string left, string right, int w)
    {
        if (string.IsNullOrEmpty(right))
            return AnsiString.TruncatePad(left, w);
        int padding = w - AnsiString.VisibleLength(left) - AnsiString.VisibleLength(right);
        if (padding < 1) padding = 1;
        return AnsiString.TruncatePad(left + new string(' ', padding) + right, w);
    }

    private static string AnsiForType(FrameType type)
    {
        return type switch
        {
            FrameType.Output       => Ansi.Silver,
            FrameType.User         => Ansi.BrightUser,
            FrameType.Error        => Ansi.Red,
            FrameType.Thinking     => Ansi.ItalicGrey,
            FrameType.Tool         => Ansi.Blue,
            FrameType.ToolCall     => Ansi.ToolCallFg,
            FrameType.ToolResponse => Ansi.ToolResponseFg,
            FrameType.System       => Ansi.Orange,
            FrameType.Debug        => Ansi.MedGrey,
            _                      => Ansi.Silver
        };
    }

    private static string PrefixTextForType(FrameType type, bool collapsed)
    {
        return type switch
        {
            FrameType.Thinking     => "[thinking] ",
            FrameType.Tool         => "[tool] ",
            FrameType.ToolCall     => "",           // formatted by FormatToolCallSummary / RenderToolCallRows
            FrameType.ToolResponse => "",
            FrameType.Debug        => "[debug] ",
            FrameType.System       => "# ",
            FrameType.Error        => "! ",
            FrameType.User         => "» ",
            _                      => ""
        };
    }

    // Extracts a friendly one-line summary from a tool-call string "name({...})"
    private static string FormatToolCallSummary(string content)
    {
        int paren = content.IndexOf('(');
        if (paren < 0) return content.Replace('\n', ' ');

        string name = content.Substring(0, paren).Trim();
        string argsJson = content.Substring(paren + 1);
        if (argsJson.Length > 0 && argsJson[argsJson.Length - 1] == ')')
            argsJson = argsJson.Substring(0, argsJson.Length - 1);

        JsonElement root = default;
        bool parsed = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            root = doc.RootElement.Clone();
            parsed = true;
        }
        catch { }

        string Get(string key)
        {
            if (!parsed) return string.Empty;
            return root.TryGetProperty(key, out JsonElement el) ? el.GetString() ?? string.Empty : string.Empty;
        }

        string label = name.Replace('_', ' ');

        string summary = name switch
        {
            "read_file"                              => BuildReadFileSummary(label, Get("file_path"), Get("offset"), Get("lines")),
            "write_file" or "edit_file"
                or "edit_file_replace" or "edit_file_insert" => BuildPathSummary(label, Get("file_path")),
            "list_directory" or "glob" or "grep"    => BuildPathSummary(label, Get("path")),
            "bash"                                   => BuildRunCommandSummary(label, Get("command")),
            "search_web"                             => BuildPathSummary(label, Get("query")),
            "fetch_page"                             => BuildPathSummary(label, Get("url")),
            _                                        => BuildGenericSummary(label, parsed ? root : default, parsed)
        };

        return summary;
    }

    private static string BuildPathSummary(string label, string path)
        => string.IsNullOrEmpty(path) ? label : $"{label} {path}";

    private static string BuildReadFileSummary(string label, string path, string offset, string lines)
    {
        if (string.IsNullOrEmpty(path)) return label;
        if (!string.IsNullOrEmpty(offset) && !string.IsNullOrEmpty(lines))
        {
            int end = 0;
            int.TryParse(offset, out int start);
            int.TryParse(lines, out int count);
            end = start + count - 1;
            return $"{label} {path}  [{offset}-{end}]";
        }
        if (!string.IsNullOrEmpty(offset))
            return $"{label} {path}  [from {offset}]";
        return $"{label} {path}";
    }

    private static string BuildRunCommandSummary(string label, string command)
    {
        if (string.IsNullOrEmpty(command)) return label;
        // Trim to the first line so long pipelines don't dominate the summary.
        int nl = command.IndexOf('\n');
        string first = nl >= 0 ? command.Substring(0, nl).TrimEnd() : command;
        return $"{label} {first}";
    }

    private static string BuildGenericSummary(string label, JsonElement root, bool parsed)
    {
        if (!parsed) return label;
        // Use the first string property value as the identifying token.
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                string val = prop.Value.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(val))
                    return $"{label} {val}";
            }
        }
        return label;
    }

    // Renders an expanded tool-call block: summary line + expanded args + paired response (if any).
    private static List<string> RenderToolCallRows(DisplayMessage msg, int w)
    {
        List<string> result = new List<string>();
        string content = msg.Content;

        int paren = content.IndexOf('(');
        string name = paren >= 0 ? content.Substring(0, paren).Trim() : content;
        string argsJson = paren >= 0 ? content.Substring(paren + 1) : string.Empty;
        if (argsJson.Length > 0 && argsJson[argsJson.Length - 1] == ')')
            argsJson = argsJson.Substring(0, argsJson.Length - 1);

        // Summary line: friendly name + primary key value.
        string summary = FormatToolCallSummary(content);
        foreach (string wl in AnsiString.Wrap(summary, w))
            result.Add(wl);

        // Detail lines: pretty-print the JSON args.
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(argsJson);
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    string val = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.ToString();

                    val = val.Replace("\\n", "\n").Replace("\\t", "    ").Replace("\t", "    ");

                    bool firstPropLine = true;
                    foreach (string valLine in val.Split('\n'))
                    {
                        string propLine = firstPropLine ? $"  {prop.Name}: {valLine}" : $"    {valLine}";
                        firstPropLine = false;
                        foreach (string wl in AnsiString.Wrap(propLine, w))
                            result.Add(wl);
                    }
                }
            }
            catch
            {
                foreach (string rawLine in argsJson.Split('\n'))
                {
                    foreach (string wl in AnsiString.Wrap(rawLine, w))
                        result.Add(wl);
                }
            }
        }

        // Paired response: show the tool result under a divider with dimmer styling.
        if (!string.IsNullOrEmpty(msg.PairedResponseContent))
        {
            // Divider line inherits the call color (no prefix needed).
            result.Add(new string('·', Math.Min(w, 40)));
            // Response lines use a dimmer teal-on-darker-bg to distinguish from the call header.
            string respAnsi = msg.PairedResponseIsError
                ? "\x1b[38;5;172m\x1b[48;5;52m"   // dim orange-red on dark red
                : "\x1b[38;5;66m\x1b[48;5;234m";   // dim teal on darker near-black
            foreach (string respLine in msg.PairedResponseContent.Split('\n'))
            {
                foreach (string wl in AnsiString.Wrap(ExpandTabs(respLine), w))
                    result.Add(respAnsi + wl);
            }
        }

        if (result.Count == 0)
            result.Add(name);
        return result;
    }

    private void SetInput(string text, int cursor)
    {
        lock (_consoleLock)
        {
            _currentInputText = text;
            _currentInputCursor = cursor;
            Redraw();
        }
    }

    private void InputLoop(CancellationToken token)
    {
        StringBuilder inputBuffer = new StringBuilder();
        int cursorPos = 0;
        int matchIndex = 0;
        List<string> matches = new List<string>();
        bool inCompletion = false;
        List<string> history = new List<string>();
        int historyIndex = -1;
        string historySavedDraft = "";

        SetInput("", 0);

        while (!token.IsCancellationRequested)
        {
            // Animation and resize tick runs before every blocking wait.
            {
                bool needRedraw = false;

                // Animate scroll offset toward target each tick.
                lock (_consoleLock)
                {
                    float remaining = _scrollTarget - _historyScrollOffset;
                    if (Math.Abs(remaining) >= 0.5f)
                    {
                        _historyScrollOffset += remaining * ScrollAlpha;
                        needRedraw = true;
                    }
                    else if (_historyScrollOffset != _scrollTarget)
                    {
                        _historyScrollOffset = Math.Max(0f, _scrollTarget);
                        needRedraw = true;
                    }
                    // Clear scrollbar overlay once its show timer expires.
                    if (_scrollbarShowUntil > 0 && Environment.TickCount64 >= _scrollbarShowUntil)
                    {
                        _scrollbarShowUntil = 0;
                        needRedraw = true;
                    }
                    if (needRedraw) Redraw();
                }
            }

            ConsoleInputEvent? evOpt = WindowsConsole.ReadInputWithTimeout(16);
            if (evOpt == null) continue;
            ConsoleInputEvent inputEv = evOpt.Value;

            if (inputEv.Type == InputEventType.MouseMove)
            {
                lock (_consoleLock)
                {
                    _scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
                    int newSlot = _rowToSlot.TryGetValue(inputEv.Row, out int s) ? s : -1;
                    _hoverSlot = newSlot;
                    Redraw();
                }
                continue;
            }

            if (inputEv.Type == InputEventType.MouseWheel)
            {
                lock (_consoleLock)
                {
                    _scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
                    _scrollTarget = Math.Max(0, _scrollTarget + (inputEv.WheelDelta > 0 ? 3 : -3));
                }
                continue;
            }

            if (inputEv.Type == InputEventType.MouseClick)
            {
                lock (_consoleLock)
                {
                    _scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
                    _hoverSlot = -1;
                    // Click on the last two columns (scrollbar width) within the history area when scrollable → seek.
                    int scrollCol = Console.WindowWidth - 2;
                    if (inputEv.Col >= scrollCol && _scrollbarMaxOffset > 0
                        && inputEv.Row >= _scrollbarTopRow
                        && inputEv.Row < _scrollbarTopRow + _scrollbarHeight)
                    {
                        float fraction = (float)(inputEv.Row - _scrollbarTopRow) / Math.Max(1, _scrollbarHeight - 1);
                        _scrollTarget = (1f - fraction) * _scrollbarMaxOffset;
                        _scrollTarget = Math.Max(0f, Math.Min(_scrollbarMaxOffset, _scrollTarget));
                    }
                    else if (_rowToSlot.TryGetValue(inputEv.Row, out int slotIdx))
                    {
                        _model?.ToggleCollapsed(slotIdx);
                    }
                }
                continue;
            }

            ConsoleKeyInfo key = inputEv.Key;
            bool ctrl  = key.Modifiers.HasFlag(ConsoleModifiers.Control);
            bool alt   = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
            bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);

            if (key.Key == ConsoleKey.Enter && !shift && !alt)
            {
                string text = inputBuffer.ToString().TrimEnd('\n');
                inputBuffer.Clear();
                cursorPos = 0;
                matchIndex = 0;
                matches.Clear();
                inCompletion = false;
                historyIndex = -1;
                historySavedDraft = "";

                if (text.Length > 0)
                {
                    if (history.Count == 0 || history[history.Count - 1] != text)
                        history.Add(text);
                    _ = SendAsync(text);
                }

                // Animate back to follow-mode when a message is submitted.
                lock (_consoleLock)
                    _scrollTarget = 0f;

                SetInput("", 0);
            }
            else if ((key.Key == ConsoleKey.Enter && (shift || alt)) || (key.Key == ConsoleKey.J && ctrl))
            {
                inCompletion = false;
                inputBuffer.Insert(cursorPos, '\n');
                cursorPos++;
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.Tab)
            {
                List<string> completionsCopy;
                lock (_consoleLock)
                    completionsCopy = new List<string>(_completions);

                UpdateMatches(inputBuffer.ToString(), matches, completionsCopy);
                if (matches.Count > 0)
                {
                    matchIndex = inCompletion ? (matchIndex + 1) % matches.Count : 0;
                    inCompletion = true;
                    string completion = matches[matchIndex];
                    inputBuffer.Clear();
                    inputBuffer.Append(completion);
                    cursorPos = completion.Length;
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
            }
            else if (key.Key == ConsoleKey.Backspace && ctrl)
            {
                inCompletion = false;
                int newPos = WordStartBefore(inputBuffer.ToString(), cursorPos);
                inputBuffer.Remove(newPos, cursorPos - newPos);
                cursorPos = newPos;
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                inCompletion = false;
                if (cursorPos > 0)
                {
                    inputBuffer.Remove(cursorPos - 1, 1);
                    cursorPos--;
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
            }
            else if (key.Key == ConsoleKey.Delete && ctrl)
            {
                inCompletion = false;
                int newPos = WordEndAfter(inputBuffer.ToString(), cursorPos);
                inputBuffer.Remove(cursorPos, newPos - cursorPos);
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.Delete)
            {
                inCompletion = false;
                if (cursorPos < inputBuffer.Length)
                {
                    inputBuffer.Remove(cursorPos, 1);
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
            }
            else if (key.Key == ConsoleKey.LeftArrow && ctrl)
            {
                cursorPos = WordStartBefore(inputBuffer.ToString(), cursorPos);
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursorPos > 0) { cursorPos--; SetInput(inputBuffer.ToString(), cursorPos); }
            }
            else if (key.Key == ConsoleKey.RightArrow && ctrl)
            {
                cursorPos = WordEndAfter(inputBuffer.ToString(), cursorPos);
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.RightArrow)
            {
                if (cursorPos < inputBuffer.Length) { cursorPos++; SetInput(inputBuffer.ToString(), cursorPos); }
            }
            else if (key.Key == ConsoleKey.Home)
            {
                cursorPos = 0;
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.End)
            {
                cursorPos = inputBuffer.Length;
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.UpArrow)
            {
                int upW = Console.WindowWidth;
                if (upW < 1) upW = 80;
                (int upLine, int upCol) = CursorInInputLines(inputBuffer.ToString(), cursorPos, upW);

                if (upLine > 0)
                {
                    cursorPos = CharFromInputLines(inputBuffer.ToString(), upLine - 1, upCol, upW);
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
                else if (history.Count > 0)
                {
                    if (historyIndex == -1) { historySavedDraft = inputBuffer.ToString(); historyIndex = history.Count - 1; }
                    else if (historyIndex > 0) historyIndex--;
                    string entry = history[historyIndex];
                    inputBuffer.Clear();
                    inputBuffer.Append(entry);
                    cursorPos = inputBuffer.Length;
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
            }
            else if (key.Key == ConsoleKey.DownArrow)
            {
                int downW = Console.WindowWidth;
                if (downW < 1) downW = 80;
                (int downLine, int downCol) = CursorInInputLines(inputBuffer.ToString(), cursorPos, downW);
                int totalLines = WrapInput(inputBuffer.ToString(), downW).Count;

                if (downLine < totalLines - 1)
                {
                    cursorPos = CharFromInputLines(inputBuffer.ToString(), downLine + 1, downCol, downW);
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
                else if (historyIndex >= 0)
                {
                    historyIndex++;
                    string entry;
                    if (historyIndex >= history.Count) { historyIndex = -1; entry = historySavedDraft; }
                    else entry = history[historyIndex];
                    inputBuffer.Clear();
                    inputBuffer.Append(entry);
                    cursorPos = inputBuffer.Length;
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
            }
            else if (key.Key == ConsoleKey.PageUp)
            {
                lock (_consoleLock)
                {
                    _scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
                    int pageH = Math.Max(1, Console.WindowHeight - 3 - ComputeInputRows(_currentInputText, Console.WindowWidth));
                    _scrollTarget += Math.Max(1, pageH - 1);
                }
                continue;
            }
            else if (key.Key == ConsoleKey.PageDown)
            {
                lock (_consoleLock)
                {
                    _scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
                    int pageH = Math.Max(1, Console.WindowHeight - 3 - ComputeInputRows(_currentInputText, Console.WindowWidth));
                    _scrollTarget = Math.Max(0f, _scrollTarget - Math.Max(1, pageH - 1));
                }
                continue;
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                inputBuffer.Clear();
                cursorPos = 0;
                inCompletion = false;
                matches.Clear();
                historyIndex = -1;
                historySavedDraft = "";
                SetInput("", 0);
            }
            else if (key.Key == ConsoleKey.A && ctrl)
            {
                cursorPos = 0;
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.E && ctrl)
            {
                cursorPos = inputBuffer.Length;
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.V && ctrl)
            {
                string? clip = ClipboardService.GetText();
                if (!string.IsNullOrEmpty(clip))
                {
                    inCompletion = false;
                    inputBuffer.Insert(cursorPos, clip);
                    cursorPos += clip.Length;
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
            }
            else if (key.Key == ConsoleKey.X && ctrl)
            {
                if (inputBuffer.Length > 0)
                {
                    ClipboardService.SetText(inputBuffer.ToString());
                    inputBuffer.Clear();
                    cursorPos = 0;
                    inCompletion = false;
                    SetInput("", 0);
                }
            }
            else if (key.Key == ConsoleKey.C && ctrl)
            {
                _requestExit?.Invoke();
            }
            else if (key.Key == ConsoleKey.D && ctrl)
            {
                _requestExit?.Invoke();
            }
            else if (key.Key == ConsoleKey.O && ctrl)
            {
                CycleCollapseMode();
            }
            else if (!char.IsControl(key.KeyChar))
            {
                inCompletion = false;
                inputBuffer.Insert(cursorPos, key.KeyChar);
                cursorPos++;
                SetInput(inputBuffer.ToString(), cursorPos);
            }
        }
    }

    private static int WordStartBefore(string text, int pos)
    {
        int i = pos - 1;
        while (i > 0 && text[i - 1] != ' ' && text[i - 1] != '\n') i--;
        return i < 0 ? 0 : i;
    }

    private static int WordEndAfter(string text, int pos)
    {
        int i = pos;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\n')) i++;
        while (i < text.Length && text[i] != ' ' && text[i] != '\n') i++;
        return i;
    }

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
            return;
        }

        try
        {
            await _sendAsync(text);
        }
        catch (Exception ex)
        {
            SetStatus($"[send error] {ex.Message}");
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

    private static void UpdateMatches(string input, List<string> matches, List<string> completions)
    {
        matches.Clear();
        if (!input.StartsWith("/", StringComparison.Ordinal)) return;

        foreach (string completion in completions)
        {
            if (completion.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                matches.Add(completion);
        }
    }
}

internal enum InputEventType { Key, MouseClick, MouseWheel, MouseMove }

internal struct ConsoleInputEvent
{
    internal InputEventType Type;
    internal ConsoleKeyInfo Key;
    internal int   Col;
    internal int   Row;
    internal short WheelDelta;  // positive = wheel up, negative = wheel down
}

// Manages Windows console mode and provides a unified ReadInputWithTimeout that surfaces
// both KEY_EVENT and MOUSE_EVENT records via ReadConsoleInput. Console.ReadKey discards
// mouse events, so we call ReadConsoleInput directly.
internal static class WindowsConsole
{
    private const int StdInputHandle  = -10;
    private const int StdOutputHandle = -11;

    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint DisableQuickEditMode            = 0x0040;
    private const uint EnableExtendedFlags             = 0x0080;

    private const uint   WAIT_OBJECT_0  = 0x00000000;
    private const ushort KEY_EVENT_TYPE   = 0x0001;
    private const ushort MOUSE_EVENT_TYPE = 0x0002;
    private const uint   MOUSE_WHEELED  = 0x0004;
    private const uint   LEFT_BUTTON    = 0x0001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleInputW(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort           EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD  KeyEvent;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        public int    bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char   UnicodeChar;
        public uint   dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public short MouseX;
        public short MouseY;
        public uint  dwButtonState;
        public uint  dwControlKeyState;
        public uint  dwEventFlags;
    }

    private static uint _originalInputMode;
    private static uint _originalOutputMode;
    private static bool _saved;

    internal static void EnableVirtualTerminal()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        IntPtr hOut = GetStdHandle(StdOutputHandle);
        IntPtr hIn  = GetStdHandle(StdInputHandle);

        if (!GetConsoleMode(hOut, out uint outMode)) return;
        if (!GetConsoleMode(hIn,  out uint inMode))  return;

        if (!_saved)
        {
            _originalOutputMode = outMode;
            _originalInputMode  = inMode;
            _saved = true;
        }

        SetConsoleMode(hOut, outMode | EnableVirtualTerminalProcessing);
        // Disable Quick Edit so mouse clicks are not swallowed for text selection.
        SetConsoleMode(hIn, (inMode | EnableExtendedFlags) & ~DisableQuickEditMode);
    }

    internal static void ReapplyModes()
    {
        if (!_saved) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        IntPtr hOut = GetStdHandle(StdOutputHandle);
        IntPtr hIn  = GetStdHandle(StdInputHandle);

        if (!GetConsoleMode(hOut, out uint outMode)) return;
        if (!GetConsoleMode(hIn,  out uint inMode))  return;

        SetConsoleMode(hOut, outMode | EnableVirtualTerminalProcessing);
        SetConsoleMode(hIn, (inMode | EnableExtendedFlags) & ~DisableQuickEditMode);
    }

    // Blocks up to timeoutMs for the next actionable input event.
    // On Windows uses ReadConsoleInput directly so MOUSE_EVENT records are not discarded.
    // Returns null on timeout, key-up, mouse-move, or other ignored events.
    internal static ConsoleInputEvent? ReadInputWithTimeout(int timeoutMs)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            for (int i = 0; i < timeoutMs && !Console.KeyAvailable; i++)
                Thread.Sleep(1);
            if (!Console.KeyAvailable) return null;
            ConsoleKeyInfo k = Console.ReadKey(true);
            return new ConsoleInputEvent { Type = InputEventType.Key, Key = k };
        }

        IntPtr hIn = GetStdHandle(StdInputHandle);
        if (WaitForSingleObject(hIn, (uint)timeoutMs) != WAIT_OBJECT_0) return null;

        INPUT_RECORD[] buf = new INPUT_RECORD[1];
        if (!ReadConsoleInputW(hIn, buf, 1, out uint read) || read == 0) return null;

        INPUT_RECORD rec = buf[0];

        if (rec.EventType == KEY_EVENT_TYPE)
        {
            KEY_EVENT_RECORD k = rec.KeyEvent;
            if (k.bKeyDown == 0) return null;

            bool shift = (k.dwControlKeyState & 0x0010u) != 0;
            bool ctrl  = (k.dwControlKeyState & 0x000Cu) != 0;
            bool alt   = (k.dwControlKeyState & 0x0003u) != 0;
            ConsoleKey ck = (ConsoleKey)k.wVirtualKeyCode;
            ConsoleKeyInfo ki = new ConsoleKeyInfo(k.UnicodeChar, ck, shift, alt, ctrl);
            return new ConsoleInputEvent { Type = InputEventType.Key, Key = ki };
        }

        if (rec.EventType == MOUSE_EVENT_TYPE)
        {
            MOUSE_EVENT_RECORD m = rec.MouseEvent;

            if ((m.dwEventFlags & MOUSE_WHEELED) != 0)
            {
                short delta = (short)(m.dwButtonState >> 16);
                return new ConsoleInputEvent { Type = InputEventType.MouseWheel, Col = m.MouseX, Row = m.MouseY, WheelDelta = delta };
            }

            if (m.dwEventFlags == 0 && (m.dwButtonState & LEFT_BUTTON) != 0)
            {
                return new ConsoleInputEvent { Type = InputEventType.MouseClick, Col = m.MouseX, Row = m.MouseY };
            }

            if ((m.dwEventFlags & 0x0001u) != 0)  // MOUSE_MOVED
            {
                return new ConsoleInputEvent { Type = InputEventType.MouseMove, Col = m.MouseX, Row = m.MouseY };
            }
        }

        return null;
    }

    internal static void Restore()
    {
        if (!_saved) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        IntPtr hOut = GetStdHandle(StdOutputHandle);
        IntPtr hIn  = GetStdHandle(StdInputHandle);
        SetConsoleMode(hOut, _originalOutputMode);
        SetConsoleMode(hIn,  _originalInputMode);
    }
}
