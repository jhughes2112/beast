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
        "compact", "clear", "reload", "role", "model", "session", "test", "quit", "cancel"
    };

    private static class Palette
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
        public static readonly string BodyBgAnsi    = $"\x1b[48;2;{FileBodyBg.R};{FileBodyBg.G};{FileBodyBg.B}m";    // bg-only: used as syntax-highlight base
        public static readonly string ErrBodyBgAnsi = $"\x1b[48;2;{FileErrBodyBg.R};{FileErrBodyBg.G};{FileErrBodyBg.B}m";  // bg-only: error body highlight base
        public static readonly string FileNameAnsi  = "\x1b[38;5;226m";  // yellow filename highlight
        public static readonly string ResetAnsi     = "\x1b[39m";         // reset foreground only
    }

    private readonly CollapseMode      _initialMode;
    private ConversationModel?         _model;
    private Func<string, Task>?        _sendAsync;
    private Action?                    _requestExit;
    private CancellationTokenSource?   _runCts;
    private Action?                    _frameDrain;

    private readonly List<string> _completions = new List<string> { "/verbose" };
    private readonly object       _consoleLock = new object();

    private string _currentInputText   = "";
    private int    _currentInputCursor = 0;
    private string _statusText         = "";
    // Status bar is laid out as three segments: left (path + mode), center (token/cost metrics),
    // right (model name). They're stored separately so each can be positioned independently.
    private string _statsMetrics       = "";
    private string _statsModelName     = "";

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

    // When set, the next Redraw will adjust scroll so this source-row stays at the top of the viewport.
    // Used to keep the top line stable when a block expands/collapses or the view mode changes.
    private int? _preserveTopSourceRow = null;

    // When set, the next Redraw will re-anchor around a block that just toggled collapse state. We capture
    // the slot index plus the old (slotTop, slotBottom, topViewRow) so we can pick the correct strategy:
    //   - view top was above the block: keep view top unchanged
    //   - view top was inside the block: snap view top to the block's new top (avoids "popping" into deleted rows)
    //   - view top was below the block: shift view top by the height delta so what was visible stays put
    private int? _pendingToggleSlot   = null;
    private int  _pendingToggleSlotTop;
    private int  _pendingToggleSlotBottom;
    private int  _pendingToggleViewTop;

    private readonly StringBuilder _drawBuf = new StringBuilder(65536);
    private StreamWriter? _bufferedOut;
    private bool _needsErase = true;

    // Agent busy animation: worm + rotating word on the left side of the separator.
    private bool _agentBusy = false;
    private long _busyStartTick = 0;
    // Incremented each time a new block type arrives (StreamStart or ToolCall) so the word changes per activity, not per clock tick.
    private int _busyWordIndex = 0;

    private static readonly string[] BusyWords = new string[]
    {
        "Rampaging", "Burninating", "Mauling", "Howling", "Stampeding", "Pouncing",
        "Ripping", "Devouring", "Chomping", "Gnashing", "Roaring", "Thundering",
        "Smashing", "Wrecking", "Ravaging", "Preying", "Stalking", "Charging",
        "Attacking", "Clawing", "Biting", "Tearing", "Feasting", "Unleashing",
        "Slashing", "Goring", "Gnawing", "Lunging", "Trampling", "Swooping",
        "Burrowing", "Rending", "Pulverizing", "Sprinting", "Prowling", "Hunting",
        "Snarling", "Hissing", "Snapping", "Striking", "Swiping", "Thrashing",
        "Galloping", "Bolting", "Skulking", "Slithering", "Lurking", "Scuttling",
        "Grappling", "Pinning", "Tossing", "Hurling", "Screeching", "Shrieking",
        "Crunching", "Grinding", "Butting", "Ramming", "Pecking", "Tracking",
        "Scouring", "Foraging", "Scavenging", "Obliterating", "Annihilating",
        "Flattening", "Demolishing", "Rupturing", "Piercing", "Impaling",
        "Skewering", "Slicing", "Cleaving", "Hacking", "Hewing", "Bashing",
        "Pummeling", "Flailing", "Surging", "Seething", "Churning", "Whirling",
        "Splintering", "Shattering", "Bursting", "Exploding", "Blasting", "Torching",
        "Toppling", "Crushing", "Crumbling", "Leveling", "Uprooting", "Devastating",
        "Submerging", "Melting", "Vaporizing", "Disintegrating", "Decimating", "Quaking",
        "Trembling", "Splitting", "Catapulting", "Launching", "Tumbling", "Crashing",
        "Bombarding", "Engulfing", "Swallowing", "Drowning", "Smothering", "Singeing",
        "Searing", "Scorching", "Incinerating", "Moltening", "Corking", "Plunging",
        "Diving", "Scaling", "Ascending", "Descending", "Encroaching", "Invading"
    };

    // Busy animation frames.
    private static readonly string[][] BusyAnimations = new string[][]
    {
        new[] { "●∙∙∙", "∙●∙∙", "∙∙●∙", "∙∙∙●", "∙∙●∙", "∙●∙∙" }, // Worm
        new[] { "∙∙∙∙", "●∙∙∙", "●●∙∙", "●●●∙", "●●●●", "∙●●●", "∙∙●●", "∙∙∙●" }, // Growth
        new[] { "⠋   ", " ⠙  ", "  ⠹ ", "   ⠸", "   ⠼", "  ⠴ ", " ⠦  ", "⠧   " }, // Braille chase
        new[] { "←↖↑↗", "↖↑↗→", "↑↗→↘", "↗→↘↓", "→↘↓↙", "↘↓↙←", "↓↙←↖", "↙←↖↑" }, // Arrow wave
        new[] { "    ", "▃   ", "▆▃  ", "█▆▃ ", "▇█▆▃", " ▇█▆", "  ▇█", "   ▇" }, // Pulse bar
        new[] { "▖   ", " ▘  ", "  ▝ ", "   ▗", "  ▝ ", " ▘  " },             // Quadrant scan
        new[] { "◢◣◤◥", "◣◤◥◢", "◤◥◢◣", "◥◢◣◤" },                         // Triangles
        new[] { "||||", "////", "----", "\\\\\\\\" },                        // Rotating pipes (escaped)
        new[] { "◇◇◇◇", "◈◇◇◇", "◆◈◇◇", "◈◆◈◇", "◇◈◆◈", "◇◇◈◆", "◇◇◇◈" },    // Diamond pulse
        new[] { "○◔◑◕", "◔◑◕●", "◑◕●◕", "◕●◕◑", "●◕◑◔", "◕◑◔○" },           // Moon cycle
        new[] { "▐░▒▓", "░▒▓█", "▒▓█▓", "▓█▓▒", "█▓▒░", "▓▒░▐" },           // Density wave
        new[] { "⊶⊷⊶⊷", "⊷⊶⊷⊶" },                                         // Oscillation
        new[] { "◜◠◝◞", "◠◝◞◡", "◝◞◡◟", "◞◡◟◜", "◡◟◜◠", "◟◜◠◝" },           // Arc flow
        new[] { "⌞⌜⌝⌟", "⌜⌝⌟⌞", "⌝⌟⌞⌜", "⌟⌞⌜⌝" },                         // Corner spin
        new[] { "[●  ]", "[ ● ]", "[  ●]", "[ ● ]" },                     // Scanner
        new[] { "{  }", " { }", "{  }", " { }" },                         // Pulse brackets
        new[] { "<  >", "<==>", " <  >", "  <  >" },                      // Jaws
        new[] { "v   ", " v  ", "  v ", "   v", "  ^ ", " ^  " },          // Gravity bounce
        new[] { "◰◱◲◳", "◱◲◳◰", "◲◳◰◱", "◳◰◱◲" },                         // Box corners
        new[] { "◴◵◶◷", "◵◶◷◴", "◶◷◴◵", "◷◴◵◶" },                         // Clock rotate
        new[] { "⠐⠠⢀⡀", "⠠⢀⡀⠐", "⢀⡀⠐⠠", "⡀⠐⠠⢀" },                   // Marquee
        new[] { "⠁⠂⠄⡀", "⠂⠄⡀⠠", "⠄⡀⠠⠐", "⡀⠠⠐⠈" }                    // Staircase
    };

    private int _currentAnimationIndex = 0;

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
            _transientStatusUntil = Environment.TickCount64 + TransientStatusMs;
            Redraw();
        }
    }

    public void SetStatsInfo(string model, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens)
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
                _currentAnimationIndex = Random.Shared.Next(BusyAnimations.Length);
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
        // SpacerRows=1 gives every block exactly one row of breathing room beneath it without making
        // the spacer itself part of the block (so toggle/hover math stays clean).
        StackLayout stack = new StackLayout(w, spacerRows: 1);
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

        // If a block-toggle anchor is pending, pick the best top-of-view based on where the toggled block
        // used to be relative to the viewport. This produces stable behavior even when the block is taller
        // than the screen, where a naive "preserve top source row" can pop into now-deleted rows.
        if (_pendingToggleSlot.HasValue)
        {
            BlockPlacement? newPlace = stack.PlacementOfSlot(_pendingToggleSlot.Value);
            int viewTop    = _pendingToggleViewTop;
            int oldTop     = _pendingToggleSlotTop;
            int oldBottom  = _pendingToggleSlotBottom;
            // Default: keep the view's top source-row exactly where it was. Toggling a block should not
            // scroll the rest of the content, even when the block changes height.
            int desiredTop = viewTop;
            // Exception: if the toggled block straddled (or started at) the top of the view, anchor the
            // view to that block's new top so the collapsed/expanded block is what sits at the top.
            if (newPlace.HasValue && oldTop <= viewTop && oldBottom > viewTop)
                desiredTop = newPlace.Value.Top;

            if (desiredTop < 0) desiredTop = 0;
            if (desiredTop > maxOffset) desiredTop = maxOffset;
            float newOffsetFromBottom = totalRows - historyH - desiredTop;
            if (newOffsetFromBottom < 0f) newOffsetFromBottom = 0f;
            if (newOffsetFromBottom > maxOffset) newOffsetFromBottom = maxOffset;
            _historyScrollOffset = newOffsetFromBottom;
            _scrollTarget        = newOffsetFromBottom;
            _pendingToggleSlot   = null;
        }
        // If a preservation request is pending (toggle/mode change), reproject the scroll offset so the
        // captured source-row stays at the top of the view. totalRows changed since the request was filed.
        else if (_preserveTopSourceRow.HasValue)
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
        if (_agentBusy)
        {
            long elapsed = Environment.TickCount64 - _busyStartTick;
            // Animations crawl at or around ~8 fps (125ms per frame).
            string[] anim = BusyAnimations[_currentAnimationIndex % BusyAnimations.Length];
            int frameIdx = (int)(elapsed / 125) % anim.Length;
            string frames = anim[frameIdx];

            // Word changes each time a new activity block arrives, not on a timer.
            string word   = BusyWords[_busyWordIndex % BusyWords.Length];

            TimeSpan ts   = TimeSpan.FromMilliseconds(elapsed);
            string timeLabel = ts.TotalHours >= 1 
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" 
                : ts.TotalMinutes >= 1 
                    ? $"{ts.Minutes}:{ts.Seconds:D2}" 
                    : $"{ts.TotalSeconds:F1}s";

            string label  = $" {frames} {word} {timeLabel} ";
            // Write the label in a calm cyan over the background so it reads as "active but not alarming".
            Rgb busyFg = new Rgb(80, 200, 200);
            AnsiToScreen.WriteLine(sep, 0, 0, label, busyFg, Palette.Background);
        }
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
        // If a transient status message has expired, fall back to the base (rooted client path).
        if (_transientStatusUntil > 0 && Environment.TickCount64 >= _transientStatusUntil)
        {
            _transientStatusUntil = 0;
            _statusText = _baseStatusText;
        }

        string modeName = _model != null ? _model.Mode.ToString() : "";
        // Left segment: path followed by the current view mode.
        string left = string.IsNullOrEmpty(modeName) ? _statusText : $"{_statusText}  {modeName}";
        // Right segment: model name. The optimistic override (filled by /model <id>) wins until the agent
        // sends a real stats frame back.
        string rightModel = !string.IsNullOrEmpty(_modelOverride) ? _modelOverride : _statsModelName;
        // Center segment: token / cost metrics. Empty unless a stats frame has populated them.
        string center = _statsMetrics;
        Screen statusScreen = BuildStatusScreen(left, center, rightModel, w);
        frame.Blit(statusScreen, 0, statusRow, BlendMode.Normal, null);

        // Cursor glow: small radial brightening over the final frame at the last known mouse position.
        // Applied last so it lifts every layer (history, separator, input, status) within a few cells of
        // the cursor. Suppressed until the mouse has actually moved over the window.
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
                layer = BuildBlockLayer(msg, w, plainText: isStreaming);
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

    private static BlockLayer BuildBlockLayer(DisplayMessage msg, int w, bool plainText)
    {
        bool isToolCall = msg.Type == FrameType.ToolCall;
        // Only flag as error when the paired response is explicitly an error. A tool that finished
        // successfully with no output (e.g. a quiet bash command) stays in the normal tool color.
        bool isError   = isToolCall && msg.PairedResponseIsError;
        (Rgb fg, Rgb? bg) = ColorsForType(msg.Type, isError);
        // Trailing spacer is supplied by StackLayout's SpacerRows; blocks themselves contain no padding.
        int spacer = 0;

        // Collapsed view: summary line plus, for select tools, a short preview of the response body.
        string prefix = PrefixTextForType(msg.Type);
        string summary = isToolCall
            ? FormatToolCallSummary(msg.Content, msg.PairedResponseContent)
            : msg.Content.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

        string toolName = string.Empty;
        if (isToolCall)
        {
            int paren = msg.Content.IndexOf('(');
            toolName = paren >= 0 ? msg.Content.Substring(0, paren).Trim() : msg.Content;
        }

        int availW = Math.Max(1, w - prefix.Length);
        bool truncated = AnsiString.VisibleLength(summary) > availW - 1;

        // Pick the body SGR for compact previews. read_file/write_file use a dark/dim blue body so file
        // content reads as "this is the file"; errors use the duller red body underneath the bright-red
        // first line; everything else uses the neutral gray body.
        // Bg-only variants are passed to the syntax highlighter to preserve token foreground colors.
        string respAnsi   = isError ? Palette.ErrBodyAnsi   : Palette.BodyAnsi;
        string respBgAnsi = isError ? Palette.ErrBodyBgAnsi : Palette.BodyBgAnsi;
        string fileLang = string.Empty;
        if (!isError && (toolName == "read_file" || toolName == "write_file" || toolName == "edit_file"
                      || toolName == "edit_file_replace" || toolName == "edit_file_insert"))
            fileLang = MarkdownAnsi.GuessLang(ExtractStringArg(msg.Content, "file_path"));

        // Build collapsed preview rows (summary + a small body excerpt for select tools).
        List<string> collapsedLines = new List<string>();
        collapsedLines.Add(prefix + AnsiString.TruncateVisible(summary, availW));

        // Multi-line collapsed previews for tools where a tiny excerpt is far more useful than the
        // bare summary line alone. Bash shows the tail; read_file shows the head;
        // write_file pulls from the call's own content argument (its response is just a status line).
        if (isToolCall && !string.IsNullOrEmpty(msg.PairedResponseContent))
        {
            string[] respLines = msg.PairedResponseContent!.Replace("\r\n", "\n").Split('\n');
            if (toolName == "bash")
            {
                int start = Math.Max(0, respLines.Length - 5);
                for (int i = start; i < respLines.Length; i++)
                    collapsedLines.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(respLines[i]), "bash", respBgAnsi));
            }
            else if (toolName == "read_file")
            {
                int end = Math.Min(respLines.Length, 5);
                for (int i = 0; i < end; i++)
                    collapsedLines.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(respLines[i]), fileLang, respBgAnsi));
            }
        }
        if (isToolCall && toolName == "write_file")
        {
            string writeContent = ExtractStringArg(msg.Content, "content");
            if (!string.IsNullOrEmpty(writeContent))
            {
                string[] wlines = writeContent.Replace("\r\n", "\n").Split('\n');
                int end = Math.Min(wlines.Length, 5);
                for (int i = 0; i < end; i++)
                    collapsedLines.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(wlines[i]), fileLang, respBgAnsi));
            }
        }

        // Expanded view: render to ANSI lines then convert to Screen cells row-by-row.
        List<string> ansiLines = plainText
            ? RenderMessageRowsRaw(msg, w)
            : (isToolCall ? RenderToolCallRows(msg, w) : RenderMessageRows(msg, w));

        // Show the ellipsis only when expanding actually reveals more — either because the summary line
        // was truncated, or because the expanded view has strictly more rows than the collapsed preview.
        bool needsEllipsis = truncated || ansiLines.Count > collapsedLines.Count;
        if (needsEllipsis)
            collapsedLines[0] = prefix + AnsiString.TruncateVisible(summary, availW - 1) + "\u2026";

        Cell rowBg = new Cell(' ', fg, bg, CellStyle.None);
        Screen collapsed = new Screen(w, collapsedLines.Count + spacer, rowBg);
        for (int r = 0; r < collapsedLines.Count; r++)
        {
            (int endX, Rgb? cFg, Rgb? cBg) = AnsiToScreen.WriteLine(collapsed, 0, r, collapsedLines[r], fg, bg);
            AnsiToScreen.PadRowBackground(collapsed, endX, r, cFg, cBg);
        }

        int expandedRows = Math.Max(1, ansiLines.Count);
        Screen expanded = new Screen(w, expandedRows + spacer, rowBg);
        for (int r = 0; r < ansiLines.Count; r++)
        {
            (int endCx, Rgb? eFg, Rgb? eBg) = AnsiToScreen.WriteLine(expanded, 0, r, ansiLines[r], fg, bg);
            AnsiToScreen.PadRowBackground(expanded, endCx, r, eFg, eBg);
        }

        // Right-justified duration tag on the first row of tool blocks. Only shown for tools that
        // ran successfully (have a non-error, non-empty response) and that took long enough to matter.
        if (isToolCall && !isError && msg.ToolDuration.HasValue && msg.ToolDuration.Value.TotalSeconds >= 0.1)
        {
            string tag = $"Took {msg.ToolDuration.Value.TotalSeconds:F1}s";
            StampRightOnRow(collapsed, 0, tag, fg, bg);
            StampRightOnRow(expanded,  0, tag, fg, bg);
        }

        // Thinking blocks render italic — bake the style bit into every cell of both screens.
        if (msg.Type == FrameType.Thinking)
        {
            ApplyStyle(collapsed, CellStyle.Italic);
            ApplyStyle(expanded,  CellStyle.Italic);
        }

        return new BlockLayer(msg.Index, collapsed, expanded, isExpanded: !msg.Collapsed);
    }

    // Writes `text` flush against the right edge of `row` on `s`, leaving at least one blank cell of
    // separation from any existing content. No-op if `row` is out of range.
    private static void StampRightOnRow(Screen s, int row, string text, Rgb fg, Rgb? bg)
    {
        if (row < 0 || row >= s.H) return;
        int len = text.Length;
        if (len + 1 > s.W) return;
        int startCol = s.W - len;
        for (int i = 0; i < len; i++)
            s.Set(startCol + i, row, new Cell(text[i], fg, bg, CellStyle.None));
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
            case FrameType.User:         return (Palette.BrightUser,  Palette.UserBg);
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
            case FrameType.Thinking:     return "";
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

    // Status bar layout: left segment flush-left, right segment flush-right, center segment centered
    // in the full bar width and then clipped/shifted if it would collide with the side segments.
    // All three segments share the same MedGrey foreground.
    private static Screen BuildStatusScreen(string left, string center, string right, int w)
    {
        Screen s = new Screen(w, 1, new Cell(' ', Palette.MedGrey, null, CellStyle.None));

        int leftLen   = AnsiString.VisibleLength(left);
        int centerLen = AnsiString.VisibleLength(center);
        int rightLen  = AnsiString.VisibleLength(right);

        // Place left at column 0.
        if (leftLen > 0)
            AnsiToScreen.WriteLine(s, 0, 0, left, Palette.MedGrey, null);

        // Place right flush against the right edge.
        int rightCol = w - rightLen;
        if (rightLen > 0 && rightCol >= 0)
            AnsiToScreen.WriteLine(s, rightCol, 0, right, Palette.MedGrey, null);

        // Place center centered within the full bar, but nudge it so it doesn't overlap left/right.
        if (centerLen > 0)
        {
            int centerCol = (w - centerLen) / 2;
            int minCol    = leftLen > 0 ? leftLen + 1 : 0;
            int maxCol    = rightLen > 0 ? rightCol - 1 - centerLen : w - centerLen;
            if (centerCol < minCol) centerCol = minCol;
            if (centerCol > maxCol) centerCol = maxCol;
            if (centerCol >= 0 && centerCol + centerLen <= w)
                AnsiToScreen.WriteLine(s, centerCol, 0, center, Palette.MedGrey, null);
        }

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
            (int endX, Rgb? _, Rgb? _) = AnsiToScreen.WriteLine(s, 0, r, line, Palette.InputFg, Palette.InputBg);
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
            (int endX, Rgb? _, Rgb? _) = AnsiToScreen.WriteLine(s, 0, r, line, Palette.InputFg, bg);
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
        bool useMarkdown = msg.Type == FrameType.Output || msg.Type == FrameType.System || msg.Type == FrameType.User;
        // Prose-style messages get true word-boundary wrapping; everything else falls back to hard
        // character wrap so code-like content isn't broken at arbitrary spaces inside tokens.
        bool wordWrap = msg.Type == FrameType.Output || msg.Type == FrameType.User
                     || msg.Type == FrameType.System || msg.Type == FrameType.Thinking;

        if (useMarkdown)
        {
            List<string> mdLines = MarkdownAnsi.Render(ExpandTabs(msg.Content), msg.Type, w);
            bool firstLine = true;
            foreach (string mdLine in mdLines)
            {
                string full = firstLine ? prefix + mdLine : mdLine;
                firstLine = false;
                List<string> wrappedLines = wordWrap ? AnsiString.WordWrap(full, w) : AnsiString.Wrap(full, w);
                foreach (string wrapped in wrappedLines)
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
                List<string> wrappedLines = wordWrap ? AnsiString.WordWrap(full, w) : AnsiString.Wrap(full, w);
                foreach (string wl in wrappedLines)
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
        // Tool call arguments and paired responses do NOT word-wrap; long lines are truncated at the
        // screen edge by the Screen blitter. This keeps things like code/diffs/log output readable
        // rather than breaking mid-token. However, the summary line wraps so long commands are visible.
        List<string> result = new List<string>();
        string content = msg.Content;

        int paren = content.IndexOf('(');
        string name = paren >= 0 ? content.Substring(0, paren).Trim() : content;
        string argsJson = paren >= 0 ? content.Substring(paren + 1) : string.Empty;
        if (argsJson.Length > 0 && argsJson[argsJson.Length - 1] == ')')
            argsJson = argsJson.Substring(0, argsJson.Length - 1);

        // Show wrapped summary as first line(s) so long commands are fully visible when expanded
        string summary = FormatToolCallSummary(content, msg.PairedResponseContent);
        foreach (string wrappedLine in AnsiString.WordWrap(summary, w))
            result.Add(wrappedLine);

        // Properties whose values are already shown in the summary line — don't repeat them in the body.
        // "content" is shown as a free-floating block with the response background instead of a labeled value.
        HashSet<string> summaryProps = SummaryPropertiesFor(name);

        // SGR sequences for body rows. read_file / write_file get the dim/dark blue file background;
        // everything else uses the neutral gray response background.
        // Bg-only variants are passed to the syntax highlighter so token foreground colors are preserved.
        string respBodyAnsi   = msg.PairedResponseIsError ? Palette.ErrBodyAnsi   : Palette.BodyAnsi;
        string respBodyBgAnsi = msg.PairedResponseIsError ? Palette.ErrBodyBgAnsi : Palette.BodyBgAnsi;
        string fileLang = MarkdownAnsi.GuessLang(ExtractStringArg(msg.Content, "file_path"));

        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(argsJson);
                List<string> inlineProps = new List<string>();

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (summaryProps.Contains(prop.Name)) continue;

                    string val = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.ToString();

                    val = val.Replace("\\n", "\n").Replace("\\t", "    ").Replace("\t", "    ");

                    // "content" (write_file etc.) is rendered as a body block with no label, using the same
                    // background as a tool response so it doesn't sit on the bright tool-call blue.
                    if (prop.Name.Equals("content", StringComparison.OrdinalIgnoreCase))
                    {
                        if (inlineProps.Count > 0) { result.Add("  " + string.Join("  ", inlineProps)); inlineProps.Clear(); }
                        foreach (string valLine in val.Split('\n'))
                            result.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(valLine), fileLang, respBodyBgAnsi));
                        continue;
                    }

                    string[] valLines = val.Split('\n');
                    if (valLines.Length == 1)
                    {
                        // Collect short single-line props and join them onto one compact line.
                        inlineProps.Add($"{prop.Name} {val}");
                    }
                    else
                    {
                        // Multi-line value: flush pending inline props first, then render as indented block.
                        if (inlineProps.Count > 0) { result.Add("  " + string.Join("  ", inlineProps)); inlineProps.Clear(); }
                        bool firstPropLine = true;
                        foreach (string valLine in valLines)
                        {
                            result.Add(firstPropLine ? $"  {prop.Name}  {valLine}" : $"    {valLine}");
                            firstPropLine = false;
                        }
                    }
                }

                if (inlineProps.Count > 0)
                    result.Add("  " + string.Join("  ", inlineProps));
            }
            catch
            {
                foreach (string rawLine in argsJson.Split('\n'))
                    result.Add(rawLine);
            }
        }

        // The write_file / edit_file* paired responses are just status lines ("File written: ...",
        // "File edited: ... (N operation(s) applied)") — the filename and line count are already part
        // of the summary, so suppressing them removes redundant noise. Errors are never suppressed.
        bool suppressPairedResponse = !msg.PairedResponseIsError
            && (name == "write_file" || name == "edit_file" || name == "edit_file_replace" || name == "edit_file_insert");

        // Render stdout (PairedResponseContent) with normal response background (blue/gray).
        if (!suppressPairedResponse && !string.IsNullOrEmpty(msg.PairedResponseContent))
        {
            // Pass bg-only SGR so token foreground colors are preserved in syntax-highlighted response lines.
            string respBgAnsi = msg.PairedResponseIsError ? Palette.ErrBodyBgAnsi : respBodyBgAnsi;
            foreach (string respLine in msg.PairedResponseContent.Split('\n'))
                result.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(respLine), fileLang, respBgAnsi));
        }

        // Render stderr (PairedResponseError) with error background (red).
        if (!string.IsNullOrEmpty(msg.PairedResponseError))
        {
            foreach (string errLine in msg.PairedResponseError.Split('\n'))
                result.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(errLine), fileLang, Palette.ErrBodyBgAnsi));
        }

        if (result.Count == 0)
            result.Add(name);
        return result;
    }

    // Property names that are already represented in the one-line summary for a given tool, so the
    // expanded body shouldn't repeat them.
    private static HashSet<string> SummaryPropertiesFor(string toolName)
    {
        HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (toolName)
        {
            case "read_file":
                set.Add("file_path"); set.Add("offset"); set.Add("lines"); break;
            case "write_file":
            case "edit_file":
            case "edit_file_replace":
            case "edit_file_insert":
                set.Add("file_path"); break;
            case "bash":
                set.Add("command"); break;
            case "search_web":
                set.Add("query"); break;
            case "fetch_page":
                set.Add("url"); break;
        }
        return set;
    }

    private static string FormatToolCallSummary(string content, string? pairedResponse)
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
        // Response line count appended as "(N lines)" for tools where it's meaningful: read returns the
        // file slice, write/edit don't return content but we know how many lines were written from the
        // call's own "content" arg. Computed once and threaded through the specific summary builders.
        int respLineCount = CountLines(pairedResponse);
        int writeLineCount = (name == "write_file" || name == "edit_file" || name == "edit_file_replace" || name == "edit_file_insert")
            ? CountLines(Get("content") + Get("new_text"))
            : 0;
        string summary = name switch
        {
            "read_file"                                                => BuildReadFileSummary(label, Get("file_path"), Get("offset"), Get("lines"), respLineCount),
            "write_file" or "edit_file"
                or "edit_file_replace" or "edit_file_insert"           => BuildWriteFileSummary(label, Get("file_path"), writeLineCount),
            "bash"                                                     => BuildRunCommandSummary(label, Get("command")),
            "search_web"                                               => BuildPathSummary(label, Get("query"), respLineCount),
            "fetch_page"                                               => BuildPathSummary(label, Get("url"), respLineCount),
            _                                                          => BuildGenericSummary(label, parsed ? root : default, parsed)
        };
        return summary;
    }

    // Returns the number of newline-delimited lines in text, treating empty as 0.
    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') count++;
        // A trailing newline shouldn't inflate the line count.
        if (text[text.Length - 1] == '\n') count--;
        return Math.Max(1, count);
    }

    // Extracts a string-valued argument from a ToolCall content string of the form "name({...json...})".
    // Returns empty when the JSON cannot be parsed or the property is missing/non-string.
    private static string ExtractStringArg(string content, string argName)
    {
        int paren = content.IndexOf('(');
        if (paren < 0) return string.Empty;
        string argsJson = content.Substring(paren + 1);
        if (argsJson.Length > 0 && argsJson[argsJson.Length - 1] == ')')
            argsJson = argsJson.Substring(0, argsJson.Length - 1);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty(argName, out JsonElement el) && el.ValueKind == JsonValueKind.String)
                return el.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    // SGR codes used to colorize filenames / paths / commands inside tool summaries.

    private static string BuildPathSummary(string label, string path, int respLineCount)
    {
        if (string.IsNullOrEmpty(path)) return label;
        string tail = respLineCount > 0 ? $" ({respLineCount} lines)" : string.Empty;
        return $"{label} {Palette.FileNameAnsi}{path}{Palette.ResetAnsi}{tail}";
    }

    private static string BuildWriteFileSummary(string label, string path, int writeLineCount)
    {
        if (string.IsNullOrEmpty(path)) return label;
        string tail = writeLineCount > 0 ? $" ({writeLineCount} lines)" : string.Empty;
        return $"{label} {Palette.FileNameAnsi}{path}{Palette.ResetAnsi}{tail}";
    }

    private static string BuildReadFileSummary(string label, string path, string offset, string lines, int respLineCount)
    {
        if (string.IsNullOrEmpty(path)) return label;
        string tail = respLineCount > 0 ? $" ({respLineCount} lines)" : string.Empty;
        if (!string.IsNullOrEmpty(offset) && !string.IsNullOrEmpty(lines))
        {
            int.TryParse(offset, out int start);
            int.TryParse(lines, out int count);
            int end = start + count - 1;
            return $"{label} {Palette.FileNameAnsi}{path}{Palette.ResetAnsi}  [{offset}-{end}]{tail}";
        }
        if (!string.IsNullOrEmpty(offset))
            return $"{label} {Palette.FileNameAnsi}{path}{Palette.ResetAnsi}  [from {offset}]{tail}";
        return $"{label} {Palette.FileNameAnsi}{path}{Palette.ResetAnsi}{tail}";
    }

    private static string BuildRunCommandSummary(string label, string command)
    {
        if (string.IsNullOrEmpty(command)) return "$";
        int nl = command.IndexOf('\n');
        string first = nl >= 0 ? command.Substring(0, nl).TrimEnd() : command;
        // Bash command text is left in the row's normal color — no yellow highlight.
        return $"$ {first}";
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
                    return $"{label} {Palette.FileNameAnsi}{val}{Palette.ResetAnsi}";
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
                            // Compute the toggle anchor only when we have everything we need. All state
                            // mutations happen together at the bottom — no early-outs, no partial writes.
                            bool hasAnchor = false;
                            int anchorSlotTop = 0;
                            int anchorSlotBottom = 0;
                            int anchorViewTop = 0;

                            if (_lastStack != null && _lastView != null)
                            {
                                BlockPlacement? p = _lastStack.PlacementOfSlot(slot.Value);
                                if (p.HasValue)
                                {
                                    hasAnchor        = true;
                                    anchorSlotTop    = p.Value.Top;
                                    anchorSlotBottom = p.Value.Bottom;
                                    anchorViewTop    = _lastView.ScrollOffset;
                                }
                            }

                            // Apply all mutations at once.
                            if (hasAnchor)
                            {
                                _pendingToggleSlot       = slot.Value;
                                _pendingToggleSlotTop    = anchorSlotTop;
                                _pendingToggleSlotBottom = anchorSlotBottom;
                                _pendingToggleViewTop    = anchorViewTop;
                            }
                            else
                            {
                                _pendingToggleSlot = null;
                            }
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
                    string insert = BuildPasteInsert(inputEv.Text, pasteBuffers, ref pasteSeq);
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

            if (key.Key == ConsoleKey.Enter && !shift && !alt)
            {
                string text = inputBuffer.ToString().TrimEnd('\n');
                foreach ((string placeholder, string content) in pasteBuffers)
                    text = text.Replace(placeholder, content);
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
                    string insert = BuildPasteInsert(clip, pasteBuffers, ref pasteSeq);
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

    // Builds the text to insert at the cursor for a paste. Content under 256 characters is inserted
    // inline (embedded newlines preserved as literal text); larger content is stored under a short
    // placeholder key that is expanded back to the full content when the line is committed.
    private static string BuildPasteInsert(string content, Dictionary<string, string> pasteBuffers, ref int pasteSeq)
    {
        if (content.Length < 256)
            return content;

        int byteCount = Encoding.UTF8.GetByteCount(content);
        string placeholder = $"[Pasted {byteCount} bytes from clipboard #{++pasteSeq}]";
        pasteBuffers[placeholder] = content;
        return placeholder;
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
