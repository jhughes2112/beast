using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TextCopy;


// IDisplay implementation that mirrors DisplayAnsi's behavior but composes its frame using the Screen system:
//   - Each conversation message becomes a BlockLayer with a 1-row collapsed Screen and an N-row expanded Screen.
//   - StackLayout stacks blocks (plus trailing spacer rows) into one tall composite Screen.
//   - ScreenView slices the visible window based on scroll offset.
//   - Overlays (hover accent, scrollbar) and effects (BrightnessEffect on the hovered block) are composited
//     on top non-destructively each frame.
//   - Status bar, separator, and input area are separate Screens stamped onto a frame-sized target Screen.
//   - ScreenAnsiWriter emits the final frame as 24-bit truecolor ANSI.
public class DisplayScreen : IDisplay
{
    private const string HelpText     = "Commands: /compact, /clear, /reload, /role <id>, /model <id>, /session <id>, /verbose, /test, /quit";
    private const string PromptPrefix = "» ";
    private const int    MaxInputRows = 10;

    private static readonly HashSet<string> AgentVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "compact", "clear", "reload", "role", "model", "session", "test", "quit", "ping", "history"
    };

    private static class Palette
    {
        // RGB equivalents of the indexed colors used by DisplayAnsi.
        public static readonly Rgb InputFg       = new Rgb(238, 238, 238);  // 255
        public static readonly Rgb InputBg       = new Rgb(38, 38, 38);     // 235
        public static readonly Rgb Silver        = new Rgb(188, 188, 188);  // 250
        public static readonly Rgb BrightUser    = new Rgb(255, 255, 255);  // 231
        public static readonly Rgb MedGrey       = new Rgb(138, 138, 138);  // 245
        public static readonly Rgb ThinkingFg    = new Rgb(112, 112, 112);  // slightly dimmer than MedGrey
        public static readonly Rgb GhostFg       = new Rgb(110, 110, 110);  // ghost-text dim version of InputFg
        public static readonly Rgb PopupSelBg    = new Rgb(70, 70, 70);     // selected row in completion popup
        public static readonly Rgb Red           = new Rgb(255, 0, 0);      // 196
        public static readonly Rgb Blue          = new Rgb(0, 135, 215);    // 33
        public static readonly Rgb Orange        = new Rgb(215, 95, 0);     // 166
        public static readonly Rgb BrightWhite   = new Rgb(238, 238, 238);  // 255
        public static readonly Rgb ToolCallFg    = new Rgb(95, 255, 255);   // 51
        public static readonly Rgb ToolCallBg    = new Rgb(0, 0, 95);       // 17
        public static readonly Rgb ToolCallErrFg = new Rgb(255, 175, 0);    // 214
        public static readonly Rgb ToolCallErrBg = new Rgb(95, 0, 0);       // 52
        public static readonly Rgb ToolRespFg    = new Rgb(95, 175, 175);   // 73
        public static readonly Rgb ToolRespBg    = new Rgb(18, 18, 18);     // 233
        public static readonly Rgb ScrollThumb   = new Rgb(88, 88, 88);     // 240
        public static readonly Rgb ScrollTrack   = new Rgb(18, 18, 18);     // 233
        public static readonly Rgb HoverBar      = new Rgb(128, 128, 128);  // 244
        public static readonly Rgb Background    = new Rgb(0, 0, 0);
    }

    private readonly CollapseMode      _initialMode;
    private ConversationModel?         _model;
    private Func<string, Task>?        _sendAsync;
    private Action?                    _requestExit;
    private CancellationTokenSource?   _runCts;

    private readonly List<string> _completions = new List<string> { "/verbose" };
    private readonly object       _consoleLock = new object();

    private string _currentInputText   = "";
    private int    _currentInputCursor = 0;
    private string _statusText         = "";
    private string _statsText          = "";

    private float _historyScrollOffset = 0f;
    private float _scrollTarget        = 0f;
    private int   _lastWidth           = 0;
    private int   _lastHeight          = 0;

    private const float ScrollAlpha = 0.22f;

    private int _streamingSlot = -1;

    // Per-slot cached BlockLayer (collapsed + expanded Screens). Invalidated on width change or message update.
    private readonly Dictionary<int, BlockLayer> _blockCache = new Dictionary<int, BlockLayer>();
    private int _renderedWidth = 0;

    // Last frame's StackLayout — captured during Redraw so mouse handlers can map row→slot without recomputing.
    private StackLayout? _lastStack;
    private ScreenView?  _lastView;
    private int          _lastHistoryHeight = 0;

    private int  _scrollbarTopRow    = 0;
    private int  _scrollbarHeight    = 0;
    private int  _scrollbarMaxOffset = 0;

    private int  _hoverSlot         = -1;
    private long _scrollbarShowUntil = 0;

    // Completion popup state. Active whenever the input begins with '/' and at least one completion matches.
    private readonly List<string> _completionMatches = new List<string>();
    private int  _completionIndex   = 0;
    private bool _completionActive  = false;
    private const int CompletionPopupMaxRows = 5;
    private const int ScrollbarShowMs = 1000;
    private const int ScrollbarFadeMs = 350;

    // When set, the next Redraw will adjust scroll so this source-row stays at the top of the viewport.
    // Used to keep the top line stable when a block expands/collapses or the view mode changes.
    private int? _preserveTopSourceRow = null;

    private readonly StringBuilder _drawBuf = new StringBuilder(65536);
    private StreamWriter? _bufferedOut;
    private bool _needsErase = true;

    public bool RequestHistory => true;

    public DisplayScreen(CollapseMode initialMode)
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
            Redraw();
        }
    }

    public void OnStreamStart(int streamIndex, FrameType type) { lock (_consoleLock) _streamingSlot = streamIndex; }
    public void OnStreamChunk(string chunk) { }
    public void OnStreamEnd() { lock (_consoleLock) _streamingSlot = -1; }

    public void SetSendAsync(Func<string, Task> sendAsync) { _sendAsync = sendAsync; }
    public void SetRequestExit(Action requestExit) { _requestExit = requestExit; }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _statusText = Directory.GetCurrentDirectory();

        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        WindowsConsole.EnableVirtualTerminal();
        WindowsConsole.ReapplyModes();

        _bufferedOut = new StreamWriter(
            Console.OpenStandardOutput(bufferSize: 131072),
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 131072,
            leaveOpen: false);
        _bufferedOut.AutoFlush = false;
        Console.SetOut(_bufferedOut);

        _lastWidth  = Console.WindowWidth;
        _lastHeight = Console.WindowHeight;

        Console.Write("\x1b[?1049h");                           // EnterAltScreen
        Console.Write("\x1b[?7l");                              // DisableWrap
        Console.Write("\x1b[?1000h\x1b[?1003h\x1b[?1006h");     // EnableMouse
        Console.CursorVisible = false;

        lock (_consoleLock)
            Redraw();

        InputLoop(_runCts.Token);

        Console.Write("\x1b[?1006l\x1b[?1003l\x1b[?1000l");     // DisableMouse
        Console.Write("\x1b[?7h");                              // EnableWrap
        Console.Write("\x1b[?1049l");                           // ExitAltScreen
        Console.Out.Flush();
        Console.CursorVisible = true;
        WindowsConsole.Restore();

        if (_bufferedOut != null)
        {
            StreamWriter restore = new StreamWriter(
                Console.OpenStandardOutput(),
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
                _blockCache.Clear();
                _needsErase = true;
            }
            else
            {
                _blockCache.Remove(msg.Index);
            }
            Redraw();
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Frame composition
    // ------------------------------------------------------------------------------------------------------

    private void Redraw()
    {
        int w = Console.WindowWidth;
        int h = Console.WindowHeight;
        if (w < 10 || h < 5) return;

        if (w != _renderedWidth)
        {
            _blockCache.Clear();
            _renderedWidth = w;
        }

        if (w != _lastWidth || h != _lastHeight)
        {
            _lastWidth  = w;
            _lastHeight = h;
            _needsErase = true;
        }

        int rawInputRows = ComputeInputRows(_currentInputText, w);
        int inputRows    = Math.Min(rawInputRows, Math.Min(MaxInputRows, Math.Max(1, h / 3)));
        int skip         = rawInputRows - inputRows;

        int statusRow    = h - 1;
        int inputEndRow  = h - 2;
        int inputStart   = inputEndRow - inputRows + 1;
        int separatorRow = inputStart - 1;

        // Recompute completion matches each frame so the popup stays in sync with the buffer.
        RecomputeCompletions();
        int popupRows = _completionActive ? Math.Min(CompletionPopupMaxRows, _completionMatches.Count) : 0;
        int popupTop  = separatorRow - popupRows;
        int historyH  = popupTop;

        // 1. Build the tall history composite via StackLayout of BlockLayers.
        StackLayout stack = new StackLayout(w, spacerRows: 0);
        BuildBlockLayers(stack, w);

        Cell bgCell = new Cell(' ', null, Palette.Background, CellStyle.None);
        (Screen historyComposite, ScreenCompositor historyCompositor) = stack.Compose(bgCell);

        int totalRows = historyComposite.H;
        int maxOffset = Math.Max(0, totalRows - historyH);

        // 2. Clamp animation values.
        if (_scrollTarget        > maxOffset) _scrollTarget        = maxOffset;
        if (_scrollTarget        < 0f)        _scrollTarget        = 0f;
        if (_historyScrollOffset > maxOffset) _historyScrollOffset = maxOffset;
        if (_historyScrollOffset < 0f)        _historyScrollOffset = 0f;

        _scrollbarTopRow    = 0;
        _scrollbarHeight    = historyH;
        _scrollbarMaxOffset = maxOffset;

        // If a preservation request is pending (toggle/mode change), reproject the scroll offset so the
        // captured source-row stays at the top of the view. totalRows changed since the request was filed.
        if (_preserveTopSourceRow.HasValue)
        {
            int targetTop = _preserveTopSourceRow.Value;
            if (targetTop < 0) targetTop = 0;
            if (targetTop > maxOffset) targetTop = maxOffset;
            float newOffsetFromBottom = totalRows - historyH - targetTop;
            if (newOffsetFromBottom < 0f) newOffsetFromBottom = 0f;
            if (newOffsetFromBottom > maxOffset) newOffsetFromBottom = maxOffset;
            _historyScrollOffset = newOffsetFromBottom;
            _scrollTarget        = newOffsetFromBottom;
            _preserveTopSourceRow = null;
        }

        // Scroll offset is "rows from the bottom"; convert to "rows from the top" for ScreenView.
        int viewOffsetFromTop = totalRows - historyH - (int)Math.Round(_historyScrollOffset);
        if (viewOffsetFromTop < 0) viewOffsetFromTop = 0;

        ScreenView view = new ScreenView(historyComposite, historyH, viewOffsetFromTop);
        Screen historyView = view.Render(bgCell);

        _lastStack         = stack;
        _lastView          = view;
        _lastHistoryHeight = historyH;

        // 3. Hover effect: brightens the whole hovered-block rectangle. The foreground (text) gets a strong
        //    boost so it visibly pops; the background gets a small nudge so the block reads as highlighted
        //    without washing out the underlying color. Non-destructive: characters and style untouched.
        if (_hoverSlot >= 0)
        {
            BlockPlacement? p = stack.PlacementOfSlot(_hoverSlot);
            if (p.HasValue)
            {
                BlockPlacement bp = p.Value;
                int topInView    = bp.Top    - viewOffsetFromTop;
                int bottomInView = bp.Bottom - viewOffsetFromTop;
                int clipTop      = Math.Max(0, topInView);
                int clipBottom   = Math.Min(historyH, bottomInView);
                if (clipBottom > clipTop)
                {
                    Rect hoverRect = new Rect(0, clipTop, w, clipBottom - clipTop);
                    new ChannelBrightnessEffect(fgFactor: 1.6f, bgFactor: 1.15f).Apply(historyView, hoverRect);
                    // Small lerp-to-white on the background so the highlight is still visible when
                    // the underlying cell bg is true black (where multiplicative scaling does nothing).
                    new BackgroundTintEffect(Rgb.White, 0.06f).Apply(historyView, hoverRect);
                }
            }
        }

        // 4. Scrollbar overlay (last two columns). Uses time-based opacity to fade in/out smoothly.
        //    Tints the existing background toward thumb/track colors — never replaces cell characters.
        //    Max strength is intentionally well below 1.0 so underlying text stays legible through the bar.
        const float ScrollbarMaxOpacity = 0.5f;
        float scrollbarOpacity = ComputeScrollbarOpacity() * ScrollbarMaxOpacity;
        int thumbH = 0;
        int thumbTop = 0;
        if (maxOffset > 0 && scrollbarOpacity > 0.01f)
        {
            thumbH = Math.Max(1, (int)Math.Round((double)historyH * historyH / Math.Max(1, totalRows)));
            thumbTop = (int)Math.Round((double)(maxOffset - _historyScrollOffset) / maxOffset * (historyH - thumbH));
            thumbTop = Math.Max(0, Math.Min(historyH - thumbH, thumbTop));

            Rect trackRect = new Rect(w - 2, 0, 2, historyH);
            new TintEffect(Palette.ScrollTrack, scrollbarOpacity).Apply(historyView, trackRect);
            if (thumbH > 0)
            {
                Rect thumbRect = new Rect(w - 2, thumbTop, 2, thumbH);
                new TintEffect(Palette.ScrollThumb, scrollbarOpacity).Apply(historyView, thumbRect);
            }
        }

        // 5. Build the full-frame target Screen and stamp history, separator, input, status onto it.
        Screen frame = new Screen(w, h, bgCell);
        frame.Blit(historyView, 0, 0, BlendMode.Normal, null);

        // Separator row.
        Screen sep = new Screen(w, 1, new Cell('─', Palette.BrightWhite, Palette.Background, CellStyle.None));
        frame.Blit(sep, 0, separatorRow, BlendMode.Normal, null);

        // Input area (with optional ghost-text completion preview).
        string ghostSuffix = ComputeGhostSuffix();
        Screen inputScreen = BuildInputScreen(_currentInputText, w, inputRows, skip, ghostSuffix);
        frame.Blit(inputScreen, 0, inputStart, BlendMode.Normal, null);

        // Completion popup, if active.
        if (popupRows > 0)
        {
            Screen popupScreen = BuildCompletionPopup(w, popupRows, _completionMatches, _completionIndex);
            frame.Blit(popupScreen, 0, popupTop, BlendMode.Normal, null);
        }

        // Status bar — current mode is always shown on the right alongside any stats text.
        string modeName = _model != null ? _model.Mode.ToString() : "";
        string right;
        if (string.IsNullOrEmpty(_statsText))
            right = modeName;
        else if (string.IsNullOrEmpty(modeName))
            right = _statsText;
        else
            right = $"{_statsText}  {modeName}";
        Screen statusScreen = BuildStatusScreen(_statusText, right, w);
        frame.Blit(statusScreen, 0, statusRow, BlendMode.Normal, null);

        // 6. Emit.
        _drawBuf.Clear();
        _drawBuf.Append("\x1b[?25l"); // HideCursor
        if (_needsErase)
        {
            _drawBuf.Append("\x1b[2J");
            _needsErase = false;
        }
        ScreenAnsiWriter.Write(_drawBuf, frame, startRow: 1, startCol: 1);

        (int curRow, int curCol) = GetCursorScreenPos(inputStart, skip, w);
        _drawBuf.Append("\x1b[").Append(Math.Max(1, Math.Min(h, curRow + 1))).Append(';').Append(Math.Max(1, curCol + 1)).Append('H');
        _drawBuf.Append("\x1b[?25h");

        Console.Write(_drawBuf);
        Console.Out.Flush();
    }

    // Builds (or pulls from cache) one BlockLayer per visible message and adds them to the stack in order.
    private void BuildBlockLayers(StackLayout stack, int w)
    {
        if (_model == null) return;

        foreach (DisplayMessage msg in _model.Messages)
        {
            if (ConversationModel.ShouldHide(msg.Type, _model.Mode)) continue;
            if (string.IsNullOrEmpty(msg.Content)) continue;

            bool isStreaming = msg.Index == _streamingSlot;

            // Streaming slots bypass cache (content changes constantly).
            BlockLayer layer;
            if (isStreaming || !_blockCache.TryGetValue(msg.Index, out BlockLayer? cached))
            {
                layer = BuildBlockLayer(msg, w, plainText: isStreaming);
                if (!isStreaming) _blockCache[msg.Index] = layer;
            }
            else
            {
                // Cache stores a layer with whichever IsExpanded was current when built; rebuild if it has changed.
                if (cached.IsExpanded == !msg.Collapsed)
                {
                    layer = cached;
                }
                else
                {
                    layer = new BlockLayer(msg.Index, cached.Collapsed, cached.Expanded, !msg.Collapsed);
                    _blockCache[msg.Index] = layer;
                }
            }

            stack.Add(layer);
        }
    }

    private static BlockLayer BuildBlockLayer(DisplayMessage msg, int w, bool plainText)
    {
        bool isError = msg.Type == FrameType.ToolCall && msg.PairedResponseIsError;
        (Rgb fg, Rgb? bg) = ColorsForType(msg.Type, isError);
        // Trailing spacer rows are folded into the expanded Screen so StackLayout stays simple.
        bool addSpacer = msg.Type != FrameType.ToolCall && msg.Type != FrameType.Thinking;
        int spacer = addSpacer ? 1 : 0;

        // Collapsed view: one row containing the summary.
        string prefix = PrefixTextForType(msg.Type);
        string summary = msg.Type == FrameType.ToolCall
            ? FormatToolCallSummary(msg.Content)
            : msg.Content.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

        bool hasMoreContent = msg.Type == FrameType.ToolCall
            ? msg.Content.Contains('(') || !string.IsNullOrEmpty(msg.PairedResponseContent)
            : msg.Content.Contains('\n') || msg.Content.Contains('\r');

        int availW = Math.Max(1, w - prefix.Length);
        bool truncated = AnsiString.VisibleLength(summary) > availW - 1;
        bool needsEllipsis = truncated || hasMoreContent;

        string collapsedLine = needsEllipsis
            ? prefix + AnsiString.TruncateVisible(summary, availW - 1) + "\u2026"
            : prefix + AnsiString.TruncateVisible(summary, availW);

        Cell rowBg = new Cell(' ', fg, bg, CellStyle.None);
        Screen collapsed = new Screen(w, 1 + spacer, rowBg);
        int endX = AnsiToScreen.WriteLine(collapsed, 0, 0, collapsedLine, fg, bg);
        AnsiToScreen.PadRowBackground(collapsed, endX, 0, fg, bg);
        if (spacer > 0)
            collapsed.Fill(new Rect(0, 1, w, spacer), new Cell(' ', null, Palette.Background, CellStyle.None));

        // Expanded view: render to ANSI lines then convert to Screen cells row-by-row.
        List<string> ansiLines = plainText
            ? RenderMessageRowsRaw(msg, w)
            : (msg.Type == FrameType.ToolCall ? RenderToolCallRows(msg, w) : RenderMessageRows(msg, w));

        int expandedRows = Math.Max(1, ansiLines.Count);
        Screen expanded = new Screen(w, expandedRows + spacer, rowBg);
        for (int r = 0; r < ansiLines.Count; r++)
        {
            int endCx = AnsiToScreen.WriteLine(expanded, 0, r, ansiLines[r], fg, bg);
            AnsiToScreen.PadRowBackground(expanded, endCx, r, fg, bg);
        }
        if (spacer > 0)
            expanded.Fill(new Rect(0, expandedRows, w, spacer), new Cell(' ', null, Palette.Background, CellStyle.None));

        // Thinking blocks render italic — bake the style bit into every cell of both screens.
        if (msg.Type == FrameType.Thinking)
        {
            ApplyStyle(collapsed, CellStyle.Italic);
            ApplyStyle(expanded,  CellStyle.Italic);
        }

        return new BlockLayer(msg.Index, collapsed, expanded, isExpanded: !msg.Collapsed);
    }

    private static void ApplyStyle(Screen s, CellStyle add)
    {
        for (int y = 0; y < s.H; y++)
        {
            for (int x = 0; x < s.W; x++)
            {
                Cell c = s.Get(x, y);
                s.Set(x, y, new Cell(c.Ch, c.Fg, c.Bg, c.Style | add));
            }
        }
    }

    private static (Rgb Fg, Rgb? Bg) ColorsForType(FrameType type, bool isError)
    {
        if (isError) return (Palette.ToolCallErrFg, Palette.ToolCallErrBg);
        switch (type)
        {
            case FrameType.Output:       return (Palette.Silver,      null);
            case FrameType.User:         return (Palette.BrightUser,  null);
            case FrameType.Error:        return (Palette.Red,         null);
            case FrameType.Thinking:     return (Palette.ThinkingFg,  null);
            case FrameType.Tool:         return (Palette.Blue,        null);
            case FrameType.ToolCall:     return (Palette.ToolCallFg,  Palette.ToolCallBg);
            case FrameType.ToolResponse: return (Palette.ToolRespFg,  Palette.ToolRespBg);
            case FrameType.System:       return (Palette.Orange,      null);
            case FrameType.Debug:        return (Palette.MedGrey,     null);
            default:                     return (Palette.Silver,      null);
        }
    }

    private static string PrefixTextForType(FrameType type)
    {
        switch (type)
        {
            case FrameType.Thinking:     return "[thinking] ";
            case FrameType.Tool:         return "[tool] ";
            case FrameType.ToolCall:     return "";
            case FrameType.ToolResponse: return "";
            case FrameType.Debug:        return "[debug] ";
            case FrameType.System:       return "# ";
            case FrameType.Error:        return "! ";
            case FrameType.User:         return "» ";
            default:                     return "";
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Status / input Screens
    // ------------------------------------------------------------------------------------------------------

    private static Screen BuildStatusScreen(string left, string right, int w)
    {
        Screen s = new Screen(w, 1, new Cell(' ', Palette.MedGrey, null, CellStyle.None));
        string line;
        if (string.IsNullOrEmpty(right))
        {
            line = left;
        }
        else
        {
            int padding = w - AnsiString.VisibleLength(left) - AnsiString.VisibleLength(right);
            if (padding < 1) padding = 1;
            line = left + new string(' ', padding) + right;
        }
        AnsiToScreen.WriteLine(s, 0, 0, line, Palette.MedGrey, null);
        return s;
    }

    private static Screen BuildInputScreen(string text, int w, int inputRows, int skip, string ghostSuffix)
    {
        Screen s = new Screen(w, inputRows, new Cell(' ', Palette.InputFg, Palette.InputBg, CellStyle.None));
        List<string> inputLines = WrapInput(text, w);
        for (int r = 0; r < inputRows; r++)
        {
            int lineIdx = skip + r;
            string line = lineIdx < inputLines.Count ? inputLines[lineIdx] : PromptPrefix;
            int endX = AnsiToScreen.WriteLine(s, 0, r, line, Palette.InputFg, Palette.InputBg);
            AnsiToScreen.PadRowBackground(s, endX, r, Palette.InputFg, Palette.InputBg);

            // Ghost text is appended to whatever the last visible input row is, just after the current text.
            if (!string.IsNullOrEmpty(ghostSuffix) && lineIdx == inputLines.Count - 1)
            {
                int gx = endX;
                for (int i = 0; i < ghostSuffix.Length && gx < w; i++, gx++)
                    s.Set(gx, r, new Cell(ghostSuffix[i], Palette.GhostFg, Palette.InputBg, CellStyle.None));
            }
        }
        return s;
    }

    private static Screen BuildCompletionPopup(int w, int rows, List<string> matches, int selected)
    {
        // Same colors as the input area; selected row gets a brighter background so it reads as the active pick.
        Screen s = new Screen(w, rows, new Cell(' ', Palette.InputFg, Palette.InputBg, CellStyle.None));

        int total = matches.Count;
        int first = 0;
        if (total > rows)
        {
            first = selected - rows / 2;
            if (first < 0) first = 0;
            if (first > total - rows) first = total - rows;
        }

        for (int r = 0; r < rows; r++)
        {
            int idx = first + r;
            if (idx >= total) break;
            bool isSel = idx == selected;
            Rgb bg = isSel ? Palette.PopupSelBg : Palette.InputBg;
            string line = "  " + matches[idx];
            // Pre-fill row background so selection highlight extends past the text to the right edge.
            s.Fill(new Rect(0, r, w, 1), new Cell(' ', Palette.InputFg, bg, CellStyle.None));
            int endX = AnsiToScreen.WriteLine(s, 0, r, line, Palette.InputFg, bg);
            AnsiToScreen.PadRowBackground(s, endX, r, Palette.InputFg, bg);
        }
        return s;
    }

    // Refresh _completionMatches/_completionIndex/_completionActive based on the current input buffer.
    // Active only when the buffer starts with '/' and at least one completion is a prefix match.
    private void RecomputeCompletions()
    {
        _completionMatches.Clear();
        if (string.IsNullOrEmpty(_currentInputText) || _currentInputText[0] != '/')
        {
            _completionActive = false;
            _completionIndex  = 0;
            return;
        }
        foreach (string c in _completions)
        {
            if (c.StartsWith(_currentInputText, StringComparison.OrdinalIgnoreCase) && c.Length > _currentInputText.Length)
                _completionMatches.Add(c);
        }
        if (_completionMatches.Count == 0)
        {
            _completionActive = false;
            _completionIndex  = 0;
            return;
        }
        _completionActive = true;
        if (_completionIndex < 0 || _completionIndex >= _completionMatches.Count) _completionIndex = 0;
    }

    // Returns the substring that would be appended if Tab were pressed right now, or empty if no popup.
    private string ComputeGhostSuffix()
    {
        if (!_completionActive || _completionMatches.Count == 0) return string.Empty;
        string pick = _completionMatches[_completionIndex];
        if (pick.Length <= _currentInputText.Length) return string.Empty;
        return pick.Substring(_currentInputText.Length);
    }

    // ------------------------------------------------------------------------------------------------------
    // ANSI rendering of messages (reused logic from DisplayAnsi, fed back through AnsiToScreen)
    // ------------------------------------------------------------------------------------------------------

    private static List<string> RenderMessageRows(DisplayMessage msg, int w)
    {
        List<string> result = new List<string>();
        string prefix = PrefixTextForType(msg.Type);
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
                foreach (string wl in AnsiString.Wrap(full, w))
                    result.Add(wl);
            }
        }

        return result;
    }

    private static List<string> RenderMessageRowsRaw(DisplayMessage msg, int w)
    {
        List<string> result = new List<string>();
        string prefix = PrefixTextForType(msg.Type);
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

    private static List<string> RenderToolCallRows(DisplayMessage msg, int w)
    {
        List<string> result = new List<string>();
        string content = msg.Content;

        int paren = content.IndexOf('(');
        string name = paren >= 0 ? content.Substring(0, paren).Trim() : content;
        string argsJson = paren >= 0 ? content.Substring(paren + 1) : string.Empty;
        if (argsJson.Length > 0 && argsJson[argsJson.Length - 1] == ')')
            argsJson = argsJson.Substring(0, argsJson.Length - 1);

        string summary = FormatToolCallSummary(content);
        foreach (string wl in AnsiString.Wrap(summary, w))
            result.Add(wl);

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

        if (!string.IsNullOrEmpty(msg.PairedResponseContent))
        {
            result.Add(new string('·', Math.Min(w, 40)));
            // Use SGR codes that AnsiToScreen understands; downstream renderer picks them up.
            string respAnsi = msg.PairedResponseIsError
                ? "\x1b[38;5;172m\x1b[48;5;52m"
                : "\x1b[38;5;66m\x1b[48;5;234m";
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
            "read_file"                                                => BuildReadFileSummary(label, Get("file_path"), Get("offset"), Get("lines")),
            "write_file" or "edit_file"
                or "edit_file_replace" or "edit_file_insert"           => BuildPathSummary(label, Get("file_path")),
            "list_directory" or "glob" or "grep"                       => BuildPathSummary(label, Get("path")),
            "bash"                                                     => BuildRunCommandSummary(label, Get("command")),
            "search_web"                                               => BuildPathSummary(label, Get("query")),
            "fetch_page"                                               => BuildPathSummary(label, Get("url")),
            _                                                          => BuildGenericSummary(label, parsed ? root : default, parsed)
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
            int.TryParse(offset, out int start);
            int.TryParse(lines, out int count);
            int end = start + count - 1;
            return $"{label} {path}  [{offset}-{end}]";
        }
        if (!string.IsNullOrEmpty(offset))
            return $"{label} {path}  [from {offset}]";
        return $"{label} {path}";
    }

    private static string BuildRunCommandSummary(string label, string command)
    {
        if (string.IsNullOrEmpty(command)) return label;
        int nl = command.IndexOf('\n');
        string first = nl >= 0 ? command.Substring(0, nl).TrimEnd() : command;
        return $"{label} {first}";
    }

    private static string BuildGenericSummary(string label, JsonElement root, bool parsed)
    {
        if (!parsed) return label;
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

    // ------------------------------------------------------------------------------------------------------
    // Input wrapping (unchanged from DisplayAnsi)
    // ------------------------------------------------------------------------------------------------------

    private (int Row, int Col) GetCursorScreenPos(int inputStartRow, int skip, int w)
    {
        (int lineIdx, int col) = CursorInInputLines(_currentInputText, _currentInputCursor, w);
        int visibleLine = Math.Max(0, lineIdx - skip);
        return (inputStartRow + visibleLine, col);
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

    // ------------------------------------------------------------------------------------------------------
    // Input loop (mirrors DisplayAnsi, with mouse mapping going through the captured StackLayout/ScreenView)
    // ------------------------------------------------------------------------------------------------------

    private void SetInput(string text, int cursor)
    {
        lock (_consoleLock)
        {
            _currentInputText = text;
            _currentInputCursor = cursor;
            Redraw();
        }
    }

    private int? SlotAtTerminalRow(int row)
    {
        if (_lastStack == null || _lastView == null) return null;
        if (row < 0 || row >= _lastHistoryHeight) return null;
        int sourceRow = _lastView.MapViewRowToSourceRow(row);
        return _lastStack.SlotAtRow(sourceRow);
    }

    // Source-row currently at the top of the visible viewport, or 0 if no frame has been drawn yet.
    // Used to capture a stable anchor before toggling a block so expansion does not shift the top line.
    private int CurrentTopSourceRow()
    {
        if (_lastView == null) return 0;
        return _lastView.ScrollOffset;
    }

    private float ComputeScrollbarOpacity()
    {
        long now = Environment.TickCount64;
        if (_scrollbarShowUntil == 0 || now >= _scrollbarShowUntil) return 0f;
        long remaining = _scrollbarShowUntil - now;
        if (remaining >= ScrollbarFadeMs) return 1f;
        return (float)remaining / ScrollbarFadeMs;
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
            {
                bool needRedraw = false;
                lock (_consoleLock)
                {
                    int curW = Console.WindowWidth;
                    int curH = Console.WindowHeight;
                    if (curW != _lastWidth || curH != _lastHeight)
                    {
                        // Reflow immediately on resize instead of waiting for a mouse/key event.
                        _lastWidth  = curW;
                        _lastHeight = curH;
                        _needsErase = true;
                        _blockCache.Clear();
                        _renderedWidth = curW;
                        needRedraw = true;
                    }
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
                    if (_scrollbarShowUntil > 0)
                    {
                        if (Environment.TickCount64 >= _scrollbarShowUntil)
                        {
                            _scrollbarShowUntil = 0;
                            needRedraw = true;
                        }
                        else
                        {
                            // Keep redrawing while the scrollbar is visible so the fade animates smoothly.
                            needRedraw = true;
                        }
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
                    int? slot = SlotAtTerminalRow(inputEv.Row);
                    _hoverSlot = slot ?? -1;
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
                    int scrollCol = Console.WindowWidth - 2;
                    if (inputEv.Col >= scrollCol && _scrollbarMaxOffset > 0
                        && inputEv.Row >= _scrollbarTopRow
                        && inputEv.Row < _scrollbarTopRow + _scrollbarHeight)
                    {
                        _scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
                        float fraction = (float)(inputEv.Row - _scrollbarTopRow) / Math.Max(1, _scrollbarHeight - 1);
                        _scrollTarget = (1f - fraction) * _scrollbarMaxOffset;
                        _scrollTarget = Math.Max(0f, Math.Min(_scrollbarMaxOffset, _scrollTarget));
                    }
                    else
                    {
                        int? slot = SlotAtTerminalRow(inputEv.Row);
                        if (slot.HasValue)
                        {
                            // Anchor the top of the view to its current source row so the expansion grows
                            // downward rather than shifting earlier content out of view at the top.
                            _preserveTopSourceRow = CurrentTopSourceRow();
                            _model?.ToggleCollapsed(slot.Value);
                            // Keep hover indicator after the click — mouse is still over the block.
                            _hoverSlot = slot.Value;
                        }
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
                // If the completion popup is up, Tab accepts the highlighted entry; otherwise cycle inline.
                bool popupActive;
                string? accept = null;
                lock (_consoleLock)
                {
                    popupActive = _completionActive && _completionMatches.Count > 0;
                    if (popupActive) accept = _completionMatches[_completionIndex];
                }

                if (popupActive && accept != null)
                {
                    inputBuffer.Clear();
                    inputBuffer.Append(accept);
                    cursorPos = inputBuffer.Length;
                    inCompletion = false;
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
                else
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
                bool popupActive;
                lock (_consoleLock)
                    popupActive = _completionActive && _completionMatches.Count > 0;
                if (popupActive)
                {
                    lock (_consoleLock)
                    {
                        _completionIndex--;
                        if (_completionIndex < 0) _completionIndex = _completionMatches.Count - 1;
                        Redraw();
                    }
                    continue;
                }
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
                bool popupActive;
                lock (_consoleLock)
                    popupActive = _completionActive && _completionMatches.Count > 0;
                if (popupActive)
                {
                    lock (_consoleLock)
                    {
                        _completionIndex++;
                        if (_completionIndex >= _completionMatches.Count) _completionIndex = 0;
                        Redraw();
                    }
                    continue;
                }
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
        // Preserve the top visible source row through the mode switch — same rationale as block toggle.
        lock (_consoleLock)
            _preserveTopSourceRow = CurrentTopSourceRow();
        _model.Mode = next;
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
