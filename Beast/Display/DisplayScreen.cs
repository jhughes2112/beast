using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TextCopy;


// IDisplay implementation that builds a frame by compositing independent layer Screens:
//   BlockRenderer   → per-message BlockLayers stacked via StackLayout / ScreenView
//   SeparatorLayer  → the horizontal rule row with busy-animation overlay
//   InputLayer      → multi-row input text area + slash-command completion popup
//   StatusBarLayer  → left/center/right status bar
//   SessionTreeLayer → F10 session tree overlay (optional, on top of everything else)
// Each Blit call in Redraw is the "enable/disable" switch for that layer.
public class DisplayScreen : IDisplay
{
    private const string HelpText     = "Commands: /compact, /clear, /reload, /role <id>, /model <id>, /session <id>, /verbose, /test, /quit";
    private const int    MaxInputRows = 10;

    private static readonly HashSet<string> AgentVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "compact", "clear", "reload", "role", "model", "session", "test", "quit", "cancel"
    };

    internal static class Palette
    {
        // RGB equivalents of the indexed colors used by DisplayAnsi.
        public static readonly Rgb InputFg       = new Rgb(238, 238, 238);  // 255
        public static readonly Rgb InputBg       = new Rgb(38, 38, 38);     // 235
        public static readonly Rgb Silver        = new Rgb(188, 188, 188);  // 250
        public static readonly Rgb BrightUser    = new Rgb(255, 255, 255);  // 231
        public static readonly Rgb UserBg        = new Rgb(80, 80, 80);     // light gray for user message background
        public static readonly Rgb MedGrey       = new Rgb(138, 138, 138);  // 245
        public static readonly Rgb ThinkingFg    = new Rgb(112, 112, 112);  // slightly dimmer than MedGrey
        public static readonly Rgb GhostFg       = new Rgb(110, 110, 110);  // ghost-text dim version of InputFg
        public static readonly Rgb PopupSelBg    = new Rgb(70, 70, 70);     // selected row in completion popup
        public static readonly Rgb Red           = new Rgb(255, 0, 0);      // 196
        public static readonly Rgb Blue          = new Rgb(0, 135, 215);    // 33
        public static readonly Rgb Orange        = new Rgb(215, 95, 0);     // 166
        public static readonly Rgb Yellow        = new Rgb(215, 175, 0);    // 178
        public static readonly Rgb BrightWhite   = new Rgb(238, 238, 238);  // 255
        public static readonly Rgb ToolCallFg    = new Rgb(95, 255, 255);   // 51
        public static readonly Rgb ToolCallBg    = new Rgb(0, 0, 95);       // 17
        public static readonly Rgb ToolCallErrFg = new Rgb(255, 230, 230);  // pale red text on the first line
        public static readonly Rgb ToolCallErrBg = new Rgb(120, 30, 30);    // muted red background for the error first line
        public static readonly Rgb ToolRespFg    = new Rgb(95, 175, 175);   // 73
        public static readonly Rgb ToolRespBg    = new Rgb(0, 25, 60);      // same dark blue as FileBodyBg
        public static readonly Rgb FileBodyBg    = new Rgb(0, 25, 60);      // dark/dim blue used as the body background for read/write file content
        public static readonly Rgb FileBodyFg    = new Rgb(210, 215, 225);  // soft off-white text on the dark-blue file body
        public static readonly Rgb FileErrBodyBg = new Rgb(70, 10, 10);     // duller red used for the body of an errored tool call
        public static readonly Rgb FileErrBodyFg = new Rgb(255, 200, 200);
        public static readonly Rgb ScrollThumb   = new Rgb(88, 88, 88);     // 240
        public static readonly Rgb ScrollTrack   = new Rgb(18, 18, 18);     // 233
        public static readonly Rgb HoverBar      = new Rgb(128, 128, 128);  // 244
        public static readonly Rgb Background    = new Rgb(0, 0, 0);

        // Pre-built SGR strings derived from the palette above.
        public static readonly string BodyAnsi      = $"\x1b[38;2;{FileBodyFg.R};{FileBodyFg.G};{FileBodyFg.B}m\x1b[48;2;{FileBodyBg.R};{FileBodyBg.G};{FileBodyBg.B}m";
        public static readonly string ErrBodyAnsi   = $"\x1b[38;2;{FileErrBodyFg.R};{FileErrBodyFg.G};{FileErrBodyFg.B}m\x1b[48;2;{FileErrBodyBg.R};{FileErrBodyBg.G};{FileErrBodyBg.B}m";
        public static readonly string BodyBgAnsi    = $"\x1b[48;2;{FileBodyBg.R};{FileBodyBg.G};{FileBodyBg.B}m";
        public static readonly string ErrBodyBgAnsi = $"\x1b[48;2;{FileErrBodyBg.R};{FileErrBodyBg.G};{FileErrBodyBg.B}m";
        public static readonly string FileNameAnsi  = "\x1b[38;5;226m";  // yellow filename highlight
        public static readonly string ResetAnsi     = "\x1b[39m";         // reset foreground only
    }

    // Collapse mode applied to each attached model; starts at the launch mode and follows Ctrl+O
    // cycling so session switches don't reset the user's chosen verbosity.
    private CollapseMode               _currentMode;
    private ConversationModel?         _model;
    private Func<string, Task>?        _sendAsync;
    private Action?                    _requestExit;
    private CancellationTokenSource?   _runCts;
    private Action?                    _frameDrain;
    private Action<string>?            _sessionSwitchCallback;

    // Session tree overlay state.
    private bool _sessionTreeOpen = false;
    private int  _sessionTreeSelected = 0;
    private int  _sessionTreeScroll = 0;
    private int  _sessionActive = 0;
    private int  _sessionTotal = 0;
    private readonly List<SessionDisplayInfo> _sessionList = new List<SessionDisplayInfo>();
    private string _sessionActiveId = "";
    // Session that was active when the overlay opened; restored if the user cancels with Escape.
    private string _sessionTreeRestoreId = "";

    private readonly List<string> _completions = new List<string> { "/verbose" };
    private readonly object       _consoleLock = new object();

    private string _currentInputText   = "";
    private int    _currentInputCursor = 0;
    private string _statusText         = "";
    // Status bar is laid out as three segments: left (path + mode), center (token/cost metrics),
    // right (model name). They're stored separately so each can be positioned independently.
    private string _statsMetrics       = "";
    private string _statsModelName     = "";
    // Role of the most recent stats frame; shown in yellow at the right end of the separator line.
    private string _currentRole        = "";

    // Bottom-left of the status bar is normally the rooted client path Beast was launched from.
    // SetStatus messages are transient: they replace the path for TransientStatusMs then revert.
    private string _baseStatusText        = "";
    private long   _transientStatusUntil  = 0;
    private const int TransientStatusMs   = 4000;

    // Optimistic override for the model name shown on the right side of the status bar. When the user
    // submits /model <id>, we set this immediately so the change is visible before the agent's next Stats
    // frame arrives. Cleared whenever a real Stats frame is received.
    private string _modelOverride         = "";

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

    // Last known mouse position in terminal cell coordinates. -1 means "no mouse event seen yet" and
    // suppresses the cursor glow until the user actually moves the mouse over the window.
    private int _mouseRow = -1;
    private int _mouseCol = -1;

    // Cursor glow: a small radial brightening centered on the mouse position, applied as a final
    // overlay on the composited frame. Purely cosmetic; never alters cell characters or style.
    private const float CursorGlowRadius   = 6f;
    private const float CursorGlowStrength = 0.45f;

    // Completion popup state. Active whenever the input begins with '/' and at least one completion matches.
    private readonly List<string> _completionMatches = new List<string>();
    private int  _completionIndex   = 0;
    private bool _completionActive  = false;
    private const int CompletionPopupMaxRows = 5;
    private const int ScrollbarShowMs = 1000;
    private const int ScrollbarFadeMs = 350;

    private readonly StringBuilder _drawBuf = new StringBuilder(65536);
    private StreamWriter? _bufferedOut;
    private bool _needsErase = true;

    // Agent busy animation: driven by SeparatorLayer. State fields wired up here; arrays live in SeparatorLayer.
    private bool _agentBusy = false;
    private long _busyStartTick = 0;
    // Incremented each time a new block type arrives (StreamStart or ToolCall) so the word changes per activity, not per clock tick.
    private int _busyWordIndex = 0;

    private int _currentAnimationIndex = 0;

    public DisplayScreen(CollapseMode initialMode)
    {
        _currentMode = initialMode;
    }

    public void Attach(ConversationModel model)
    {
        if (_model != null)
            _model.MessageUpdated -= OnMessageUpdated;
        _model = model;
        _model.Mode = _currentMode;
        _model.MessageUpdated += OnMessageUpdated;
        lock (_consoleLock)
        {
            _blockCache.Clear();
            _historyScrollOffset = 0f;
            _scrollTarget = 0f;
            _needsErase = true;
            // New conversation model — the previous layout is not a valid anchoring basis.
            _lastStack = null;
            _lastView  = null;
            if (_runCts != null) Redraw();
        }
    }

    public void SetStatus(string text)
    {
        lock (_consoleLock)
        {
            _statusText = text;
            _transientStatusUntil = Environment.TickCount64 + TransientStatusMs;
            Redraw();
        }
    }

    public void SetStatsInfo(string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens)
    {
        string contextInfo = maxContext > 0 && contextTokens > 0
            ? $"  {(int)((double)contextTokens / maxContext * 100)}%/{maxContext}"
            : "";
        string metrics = promptTokens > 0 || completionTokens > 0
            ? $"in:{promptTokens} out:{completionTokens} ${totalCost:F4}{contextInfo}"
            : "";
        lock (_consoleLock)
        {
            _statsMetrics   = metrics;
            _statsModelName = model;
            _currentRole    = role;
            // A real stats frame supersedes any optimistic /model override.
            _modelOverride = "";
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

    public void OnStreamStart(int streamIndex, FrameType type) { lock (_consoleLock) { _streamingSlot = streamIndex; _busyWordIndex++; } }
    public void OnStreamChunk(string chunk) { }
    public void OnStreamEnd() { lock (_consoleLock) _streamingSlot = -1; }

    public void SetAgentBusy(bool busy)
    {
        lock (_consoleLock)
        {
            if (busy && !_agentBusy)
            {
                _busyStartTick = Environment.TickCount64;
                _agentBusy = true;
                _currentAnimationIndex = Random.Shared.Next(SeparatorLayer.AnimationCount);
            }
            else if (busy && _agentBusy)
            {
                // New activity while already busy — advance the word.
                _busyWordIndex++;
            }
            else if (!busy)
            {
                _agentBusy = false;
                _streamingSlot = -1;
                Redraw();
            }
        }
    }

    public void SetSendAsync(Func<string, Task> sendAsync) { _sendAsync = sendAsync; }
    public void SetRequestExit(Action requestExit) { _requestExit = requestExit; }
    public void SetFrameDrain(Action drain) { _frameDrain = drain; }
    public void SetSessionSwitchCallback(Action<string> switchTo) { _sessionSwitchCallback = switchTo; }

    public bool IsAutoTrackSuppressed()
    {
        lock (_consoleLock)
            // Same pinned-to-bottom threshold as ApplyLayoutAnchoring.
            return _sessionTreeOpen || _scrollTarget >= 0.5f;
    }

    public void SetSessionCounts(int active, int total)
    {
        lock (_consoleLock)
        {
            _sessionActive = active;
            _sessionTotal = total;
            Redraw();
        }
    }

    public void SetSessionList(IReadOnlyList<SessionDisplayInfo> sessions, string activeId)
    {
        lock (_consoleLock)
        {
            _sessionList.Clear();
            foreach (SessionDisplayInfo s in sessions)
                _sessionList.Add(s);
            _sessionActiveId = activeId;
            // Clamp selection to valid range.
            if (_sessionTreeSelected >= _sessionList.Count)
                _sessionTreeSelected = Math.Max(0, _sessionList.Count - 1);
            Redraw();
        }
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _baseStatusText = Path.GetFullPath(Directory.GetCurrentDirectory());
        _statusText     = _baseStatusText;

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
                _scrollTarget = 0f;
                _blockCache.Clear();
                _needsErase = true;
                _lastStack = null;
                _lastView  = null;
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
            // Reflow changes every block height; the previous layout is not a valid anchoring basis.
            _lastStack = null;
            _lastView  = null;
        }

        if (w != _lastWidth || h != _lastHeight)
        {
            _lastWidth  = w;
            _lastHeight = h;
            _needsErase = true;
        }

        int rawInputRows = InputLayer.ComputeInputRows(_currentInputText, w);
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
        // SpacerRows=1 gives every block exactly one row of breathing room beneath it without making
        // the spacer itself part of the block (so toggle/hover math stays clean).
        StackLayout stack = new StackLayout(w, spacerRows: 1);
        BuildBlockLayers(stack, w);

        Cell bgCell = new Cell(' ', null, Palette.Background, CellStyle.None);
        (Screen historyComposite, ScreenCompositor historyCompositor) = stack.Compose(bgCell);

        int totalRows = historyComposite.H;
        int maxOffset = Math.Max(0, totalRows - historyH);

        // 2. Re-anchor against the previous frame's layout so block height changes (collapse,
        // expand, streaming growth, blocks appearing or hiding) keep the on-screen content stable,
        // then clamp the animation values into the new valid range.
        ApplyLayoutAnchoring(stack);

        if (_scrollTarget        > maxOffset) _scrollTarget        = maxOffset;
        if (_scrollTarget        < 0f)        _scrollTarget        = 0f;
        if (_historyScrollOffset > maxOffset) _historyScrollOffset = maxOffset;
        if (_historyScrollOffset < 0f)        _historyScrollOffset = 0f;

        _scrollbarTopRow    = 0;
        _scrollbarHeight    = historyH;
        _scrollbarMaxOffset = maxOffset;

        // Scroll offset is "rows from the bottom"; convert to "rows from the top" for ScreenView.
        int viewOffsetFromTop = totalRows - historyH - (int)Math.Round(_historyScrollOffset);
        if (viewOffsetFromTop < 0) viewOffsetFromTop = 0;

        ScreenView view = new ScreenView(historyComposite, historyH, viewOffsetFromTop);
        Screen historyView = view.Render(bgCell);

        _lastStack         = stack;
        _lastView          = view;
        _lastHistoryHeight = historyH;

        // 3. Hover effect: brightens the whole hovered-block rectangle.
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

        // 4. Scrollbar overlay (last two columns). Time-based opacity fades in/out.
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

        // 5. Composite all layers onto the full-frame Screen.
        Screen frame = new Screen(w, h, bgCell);
        frame.Blit(historyView, 0, 0, BlendMode.Normal, null);

        // Separator layer. Carries the "{Role} F10(N/T)" status at its right end.
        Screen sep = SeparatorLayer.Build(w, _agentBusy, _busyStartTick, _busyWordIndex, _currentAnimationIndex, _currentRole, _sessionActive, _sessionTotal);
        frame.Blit(sep, 0, separatorRow, BlendMode.Normal, null);

        // Input layer.
        string ghostSuffix = ComputeGhostSuffix();
        Screen inputScreen = InputLayer.Build(_currentInputText, w, inputRows, skip, ghostSuffix);
        frame.Blit(inputScreen, 0, inputStart, BlendMode.Normal, null);

        // Completion popup layer.
        if (popupRows > 0)
        {
            Screen popupScreen = InputLayer.BuildCompletionPopup(w, popupRows, _completionMatches, _completionIndex);
            frame.Blit(popupScreen, 0, popupTop, BlendMode.Normal, null);
        }

        // Status bar layer.
        if (_transientStatusUntil > 0 && Environment.TickCount64 >= _transientStatusUntil)
        {
            _transientStatusUntil = 0;
            _statusText = _baseStatusText;
        }
        string modeName = _model != null ? _model.Mode.ToString() : "";
        string left = string.IsNullOrEmpty(modeName) ? _statusText : $"{_statusText}  {modeName}";
        string right = !string.IsNullOrEmpty(_modelOverride) ? _modelOverride : _statsModelName;
        string center = _statsMetrics;
        Screen statusScreen = StatusBarLayer.Build(left, center, right, w);
        frame.Blit(statusScreen, 0, statusRow, BlendMode.Normal, null);

        // Session tree overlay layer (optional): narrow right-side panel so agent output stays visible.
        if (_sessionTreeOpen)
        {
            int panelW = Math.Min(52, Math.Max(36, w / 3));
            Screen treeOverlay = SessionTreeLayer.Build(_sessionList, _sessionTreeSelected, _sessionTreeScroll, panelW, historyH, _sessionActiveId);
            frame.Blit(treeOverlay, w - panelW, 0, BlendMode.Normal, null);
        }

        // Cursor glow layer (applied last so it lifts all underlying layers).
        if (_mouseRow >= 0 && _mouseCol >= 0)
        {
            int rad = (int)Math.Ceiling(CursorGlowRadius);
            Rect glowRect = new Rect(_mouseCol - rad, _mouseRow - rad, rad * 2 + 1, rad * 2 + 1);
            new CursorGlowEffect(_mouseCol, _mouseRow, CursorGlowRadius, CursorGlowStrength).Apply(frame, glowRect);
        }

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
                layer = BlockRenderer.Build(msg, w, isStreaming);
                // Force streaming slots fully expanded so the user always sees content as it arrives.
                // Once streaming ends the normal collapsed state takes over.
                if (isStreaming)
                    layer = new BlockLayer(layer.SlotIndex, layer.Collapsed, layer.Expanded, isExpanded: true);
                else
                    _blockCache[msg.Index] = layer;
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

    // Re-anchors the scroll offset so on-screen content stays put when the block layout changes
    // (collapse/expand toggles, streaming growth, blocks appearing or hiding). The offset is measured
    // in rows from the bottom, which already follows content through changes entirely above the view
    // top, so only blocks at or below the view top need an explicit correction. Walks the new layout
    // against the previous frame's layout and sums the required correction per changed block.
    private void ApplyLayoutAnchoring(StackLayout stack)
    {
        if (_lastStack == null || _lastView == null) return;
        // Pinned to the bottom: follow new content instead of anchoring (the normal streaming case).
        if (_scrollTarget < 0.5f) return;

        int oldViewTop = _lastView.ScrollOffset;
        IReadOnlyList<BlockPlacement> oldP = _lastStack.Placements;
        IReadOnlyList<BlockPlacement> newP = stack.Placements;
        int spacer = stack.SpacerRows;

        int shift = 0;     // correction to apply to the from-bottom offsets
        int cumDelta = 0;  // total height delta walked so far, for mapping new coordinates to old
        int iOld = 0;
        int iNew = 0;
        while (iOld < oldP.Count || iNew < newP.Count)
        {
            bool haveOld = iOld < oldP.Count;
            bool haveNew = iNew < newP.Count;
            bool takeOld = haveOld && (!haveNew || oldP[iOld].SlotIndex <= newP[iNew].SlotIndex);
            bool takeNew = haveNew && (!haveOld || newP[iNew].SlotIndex <= oldP[iOld].SlotIndex);

            if (takeOld && takeNew)
            {
                // Slot present in both frames: a plain height change.
                BlockPlacement op = oldP[iOld++];
                BlockPlacement np = newP[iNew++];
                int d = np.Height - op.Height;
                if (d != 0)
                {
                    shift += ClassifyShift(op.Top, op.Bottom, oldViewTop, d);
                    cumDelta += d;
                }
            }
            else if (takeOld)
            {
                // Block disappeared (hidden or cleared); its spacer row goes with it.
                BlockPlacement op = oldP[iOld++];
                int d = -(op.Height + spacer);
                shift += ClassifyShift(op.Top, op.Bottom, oldViewTop, d);
                cumDelta += d;
            }
            else
            {
                // Block appeared. Map its new top back into the previous frame's coordinates so it
                // can be classified against the old view top like everything else.
                BlockPlacement np = newP[iNew++];
                int d = np.Height + spacer;
                int effTop = np.Top - cumDelta;
                shift += ClassifyShift(effTop, effTop + d, oldViewTop, d);
                cumDelta += d;
            }
        }

        if (shift != 0)
        {
            _historyScrollOffset += shift;
            _scrollTarget        += shift;
        }
    }

    // How much of a block's height delta must be applied to the from-bottom scroll offset:
    //   entirely above the view top → 0 (from-bottom offsets follow content above automatically)
    //   at or below the view top    → d (keeps the view's top row on the same content)
    //   straddling the view top     → growth keeps the visible rows fixed (d); shrink leaves the
    //                                 bottom of the screen anchored (0)
    private static int ClassifyShift(int top, int bottom, int viewTop, int d)
    {
        int result;
        if (bottom <= viewTop)
            result = 0;
        else if (top >= viewTop)
            result = d;
        else
            result = d > 0 ? d : 0;
        return result;
    }

    // ------------------------------------------------------------------------------------------------------
    // Input utilities (instance wrappers over InputLayer static methods)
    // ------------------------------------------------------------------------------------------------------

    private (int Row, int Col) GetCursorScreenPos(int inputStartRow, int skip, int w)
    {
        (int lineIdx, int col) = InputLayer.CursorInInputLines(_currentInputText, _currentInputCursor, w);
        int visibleLine = Math.Max(0, lineIdx - skip);
        return (inputStartRow + visibleLine, col);
    }

    private float ComputeScrollbarOpacity()
    {
        long now = Environment.TickCount64;
        if (_scrollbarShowUntil == 0 || now >= _scrollbarShowUntil) return 0f;
        long remaining = _scrollbarShowUntil - now;
        if (remaining >= ScrollbarFadeMs) return 1f;
        return (float)remaining / ScrollbarFadeMs;
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

    private void SetInput(string text, int cursor)
    {
        lock (_consoleLock)
        {
            _currentInputText = text;
            _currentInputCursor = cursor;
            Redraw();
        }
    }

    // Switches the display to the overlay's highlighted session so its contents are visible behind
    // the panel while navigating. Called with _consoleLock held; the switch callback re-enters
    // Attach on this thread, which is safe because the lock is reentrant.
    private void PreviewSelectedSession()
    {
        if (_sessionTreeSelected >= 0 && _sessionTreeSelected < _sessionList.Count)
            _sessionSwitchCallback?.Invoke(_sessionList[_sessionTreeSelected].Id);
    }

    private int? SlotAtTerminalRow(int row)
    {
        if (_lastStack == null || _lastView == null) return null;
        if (row < 0 || row >= _lastHistoryHeight) return null;
        int sourceRow = _lastView.MapViewRowToSourceRow(row);
        return _lastStack.SlotAtRow(sourceRow);
    }

    // ------------------------------------------------------------------------------------------------------
    // Input loop
    // ------------------------------------------------------------------------------------------------------

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

        // Large clipboard pastes are replaced in the input line with a short placeholder key;
        // the full content is held here and substituted back in when the line is committed.
        Dictionary<string, string> pasteBuffers = new Dictionary<string, string>();
        int pasteSeq = 0;

        SetInput("", 0);

        while (!token.IsCancellationRequested)
        {
            {
                bool needRedraw = false;
                lock (_consoleLock)
                {
                    _frameDrain?.Invoke();
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
                        _lastStack = null;
                        _lastView  = null;
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
                    if (_transientStatusUntil > 0 && Environment.TickCount64 >= _transientStatusUntil)
                    {
                        // Transient status expired — Redraw will revert to the base (rooted path).
                        needRedraw = true;
                    }
                    if (_agentBusy)
                    {
                        // Keep animating the separator worm while the agent is busy.
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
                    _mouseRow = inputEv.Row;
                    _mouseCol = inputEv.Col;
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
                    _mouseRow = inputEv.Row;
                    _mouseCol = inputEv.Col;
                    _scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
                    _scrollTarget = Math.Max(0, _scrollTarget + (inputEv.WheelDelta > 0 ? 3 : -3));
                }
                continue;
            }

            if (inputEv.Type == InputEventType.MouseClick)
            {
                lock (_consoleLock)
                {
                    _mouseRow = inputEv.Row;
                    _mouseCol = inputEv.Col;
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
                            // Redraw's layout anchoring keeps the view stable through the height change.
                            _model?.ToggleCollapsed(slot.Value);
                            // Keep hover indicator after the click — mouse is still over the block.
                            _hoverSlot = slot.Value;
                        }
                    }
                }
                continue;
            }

            if (inputEv.Type == InputEventType.Paste)
            {
                lock (_consoleLock)
                {
                    inCompletion = false;
                    string insert = InputLayer.BuildPasteInsert(inputEv.Text, pasteBuffers, ref pasteSeq);
                    inputBuffer.Insert(cursorPos, insert);
                    cursorPos += insert.Length;
                    SetInput(inputBuffer.ToString(), cursorPos);
                }
                continue;
            }

            ConsoleKeyInfo key = inputEv.Key;
            bool ctrl  = key.Modifiers.HasFlag(ConsoleModifiers.Control);
            bool alt   = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
            bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);

            // When the session tree overlay is open, intercept all keystrokes for tree navigation.
            // No input goes to the agent while the overlay is active.
            if (_sessionTreeOpen)
            {
                lock (_consoleLock)
                {
                    if (key.Key == ConsoleKey.UpArrow)
                    {
                        if (_sessionTreeSelected > 0)
                        {
                            _sessionTreeSelected--;
                            if (_sessionTreeSelected < _sessionTreeScroll)
                                _sessionTreeScroll = _sessionTreeSelected;
                            // Live preview: show the highlighted session behind the panel immediately.
                            PreviewSelectedSession();
                        }
                        Redraw();
                    }
                    else if (key.Key == ConsoleKey.DownArrow)
                    {
                        if (_sessionTreeSelected < _sessionList.Count - 1)
                        {
                            _sessionTreeSelected++;
                            int visRows = Math.Max(1, _lastHeight - 5);
                            if (_sessionTreeSelected >= _sessionTreeScroll + visRows)
                                _sessionTreeScroll = _sessionTreeSelected - visRows + 1;
                            PreviewSelectedSession();
                        }
                        Redraw();
                    }
                    else if (key.Key == ConsoleKey.F10 || key.Key == ConsoleKey.Enter)
                    {
                        // Commit the highlighted session (already previewed) and close the overlay.
                        _sessionTreeOpen = false;
                        PreviewSelectedSession();
                        Redraw();
                    }
                    else if (key.Key == ConsoleKey.Escape)
                    {
                        _sessionTreeOpen = false;
                        // Cancel: restore the session that was active when the overlay opened.
                        if (!string.IsNullOrEmpty(_sessionTreeRestoreId))
                            _sessionSwitchCallback?.Invoke(_sessionTreeRestoreId);
                        Redraw();
                    }
                }
                continue;
            }

            if (key.Key == ConsoleKey.Enter && !shift && !alt)
            {
                // If a completion popup is active, accept the highlighted entry first.
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
                }

                string text = inputBuffer.ToString().TrimEnd('\n');
                foreach ((string placeholder, string content) in pasteBuffers)
                {
                    text = text.Replace(placeholder, content);
                }
                pasteBuffers.Clear();
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

                    InputLayer.UpdateMatches(inputBuffer.ToString(), matches, completionsCopy);
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
                int newPos = InputLayer.WordStartBefore(inputBuffer.ToString(), cursorPos);
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
                int newPos = InputLayer.WordEndAfter(inputBuffer.ToString(), cursorPos);
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
                cursorPos = InputLayer.WordStartBefore(inputBuffer.ToString(), cursorPos);
                SetInput(inputBuffer.ToString(), cursorPos);
            }
            else if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursorPos > 0) { cursorPos--; SetInput(inputBuffer.ToString(), cursorPos); }
            }
            else if (key.Key == ConsoleKey.RightArrow && ctrl)
            {
                cursorPos = InputLayer.WordEndAfter(inputBuffer.ToString(), cursorPos);
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
                (int upLine, int upCol) = InputLayer.CursorInInputLines(inputBuffer.ToString(), cursorPos, upW);

                if (upLine > 0)
                {
                    cursorPos = InputLayer.CharFromInputLines(inputBuffer.ToString(), upLine - 1, upCol, upW);
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
                (int downLine, int downCol) = InputLayer.CursorInInputLines(inputBuffer.ToString(), cursorPos, downW);
                int totalLines = InputLayer.WrapInput(inputBuffer.ToString(), downW).Count;

                if (downLine < totalLines - 1)
                {
                    cursorPos = InputLayer.CharFromInputLines(inputBuffer.ToString(), downLine + 1, downCol, downW);
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
                    int pageH = Math.Max(1, Console.WindowHeight - 3 - InputLayer.ComputeInputRows(_currentInputText, Console.WindowWidth));
                    _scrollTarget += Math.Max(1, pageH - 1);
                }
                continue;
            }
            else if (key.Key == ConsoleKey.PageDown)
            {
                lock (_consoleLock)
                {
                    _scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
                    int pageH = Math.Max(1, Console.WindowHeight - 3 - InputLayer.ComputeInputRows(_currentInputText, Console.WindowWidth));
                    _scrollTarget = Math.Max(0f, _scrollTarget - Math.Max(1, pageH - 1));
                }
                continue;
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                if (inputBuffer.Length > 0)
                {
                    inputBuffer.Clear();
                    cursorPos = 0;
                    inCompletion = false;
                    matches.Clear();
                    historyIndex = -1;
                    historySavedDraft = "";
                    SetInput("", 0);
                }
                else
                {
                    // Empty input — interrupt any in-progress agent turn.
                    _ = SendAsync("/cancel");
                }
            }
            else if (key.Key == ConsoleKey.F10)
            {
                lock (_consoleLock)
                {
                    if (_sessionList.Count > 0)
                    {
                        _sessionTreeOpen = !_sessionTreeOpen;
                        if (_sessionTreeOpen)
                        {
                            _sessionTreeRestoreId = _sessionActiveId;
                            // Pre-select the currently active session.
                            _sessionTreeSelected = 0;
                            for (int i = 0; i < _sessionList.Count; i++)
                            {
                                if (string.Equals(_sessionList[i].Id, _sessionActiveId, StringComparison.Ordinal))
                                {
                                    _sessionTreeSelected = i;
                                    break;
                                }
                            }
                            // Scroll the pre-selected row into view.
                            _sessionTreeScroll = 0;
                            int visRows = Math.Max(1, _lastHeight - 5);
                            if (_sessionTreeSelected >= visRows)
                                _sessionTreeScroll = _sessionTreeSelected - visRows + 1;
                        }
                        Redraw();
                    }
                }
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
                    string insert = InputLayer.BuildPasteInsert(clip, pasteBuffers, ref pasteSeq);
                    inputBuffer.Insert(cursorPos, insert);
                    cursorPos += insert.Length;
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

            // Optimistically reflect a /model <id> change in the status bar immediately rather than
            // waiting for the agent to round-trip a Stats frame back to us.
            if (verb.Equals("model", StringComparison.OrdinalIgnoreCase) && spaceIdx >= 0)
            {
                string newModel = trimmed.Substring(spaceIdx + 1).Trim();
                if (newModel.Length > 0)
                {
                    lock (_consoleLock)
                    {
                        _modelOverride = newModel;
                        Redraw();
                    }
                }
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
        _currentMode = next;
        // Redraw's layout anchoring keeps the view stable through the per-block height changes.
        _model.Mode = next;
    }
}
