using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TextCopy;


// IDisplay implementation that builds a frame by compositing independent layer Screens:
//   BlockRenderer   → per-message BlockLayers; StackLayout lays them out and renders the visible window
//   SeparatorLayer  → the horizontal rule row with busy-animation overlay
//   InputLayer      → multi-row input text area + slash-command completion popup
//   StatusBarLayer  → left/center/right status bar
//   SessionTreeLayer → F10 session tree overlay (optional, on top of everything else)
// Each Blit call in Redraw is the "enable/disable" switch for that layer.
public class DisplayScreen : IDisplay
{
	private const string HelpText     = "Commands: /compact, /clear, /reload, /model <id>, /finish, /verbose, /test, /quit";
	private const int    MaxInputRows = 10;

	private static readonly HashSet<string> AgentVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"compact", "clear", "reload", "model", "finish", "test", "quit", "cancel"
	};

	internal static class Palette
	{
		// A restrained dark theme: accents are desaturated rather than primary, and the "colored"
		// backgrounds are dark neutral slates with only a hint of hue so text stays legible on them.
		public static readonly Rgb InputFg       = new Rgb(232, 232, 234);  // crisp text on the input line
		public static readonly Rgb InputBg       = new Rgb(34, 34, 38);     // near-neutral dark input strip
		public static readonly Rgb Silver        = new Rgb(206, 206, 210);  // primary body text (lifted for legibility)
		public static readonly Rgb BrightUser    = new Rgb(244, 244, 246);  // user message text — bright but not pure white
		public static readonly Rgb UserBg        = new Rgb(48, 50, 56);     // dark slate behind user messages (was a too-bright grey)
		public static readonly Rgb MedGrey       = new Rgb(146, 146, 150);  // secondary/muted text
		public static readonly Rgb ThinkingFg    = new Rgb(128, 128, 132);  // dim, but a touch brighter than before
		public static readonly Rgb GhostFg       = new Rgb(116, 116, 120);  // ghost-text dim version of InputFg
		public static readonly Rgb CopyIconFg    = new Rgb(130, 184, 222);  // floating "copy block" affordance glyph
		public static readonly Rgb CopyIconBg    = new Rgb(40, 40, 44);     // small contrasting chip behind the copy glyph
		public static readonly Rgb PopupSelBg    = new Rgb(66, 68, 74);     // selected row in completion popup
		public static readonly Rgb Red           = new Rgb(214, 102, 102);  // softened error red (was pure 255,0,0)
		public static readonly Rgb Blue          = new Rgb(98, 158, 204);   // softened accent blue
		public static readonly Rgb Orange        = new Rgb(204, 140, 82);   // softened amber (system)
		public static readonly Rgb Yellow        = new Rgb(206, 182, 112);  // softened gold
		public static readonly Rgb BrightWhite   = new Rgb(232, 232, 234);
		public static readonly Rgb ToolCallFg    = new Rgb(126, 192, 196);  // calm teal header (was harsh cyan)
		public static readonly Rgb ToolCallBg    = new Rgb(40, 48, 72);     // dark blue-slate tool block; kept brighter than the response body so the call header reads as distinct
		public static readonly Rgb ToolCallErrFg = new Rgb(240, 208, 208);  // pale red text on the error first line
		public static readonly Rgb ToolCallErrBg = new Rgb(94, 50, 50);     // muted red background for the error first line
		public static readonly Rgb ToolRespFg    = new Rgb(128, 174, 176);  // muted teal
		public static readonly Rgb ToolRespBg    = new Rgb(22, 30, 44);     // same dark slate as FileBodyBg
		public static readonly Rgb FileBodyBg    = new Rgb(22, 30, 44);     // dark neutral slate (faint blue) for read/write file content
		public static readonly Rgb FileBodyFg    = new Rgb(206, 212, 222);  // soft off-white text on the file body
		public static readonly Rgb FileErrBodyBg = new Rgb(52, 32, 32);     // muted dark red for the body of an errored tool call
		public static readonly Rgb FileErrBodyFg = new Rgb(238, 198, 198);
		public static readonly Rgb ScrollThumb   = new Rgb(84, 84, 88);     // 240
		public static readonly Rgb ScrollTrack   = new Rgb(20, 20, 22);     // 233
		public static readonly Rgb HoverBar      = new Rgb(124, 124, 128);  // 244
		public static readonly Rgb Background    = new Rgb(0, 0, 0);

		// Pre-built SGR strings derived from the palette above.
		public static readonly string BodyAnsi      = $"\x1b[38;2;{FileBodyFg.R};{FileBodyFg.G};{FileBodyFg.B}m\x1b[48;2;{FileBodyBg.R};{FileBodyBg.G};{FileBodyBg.B}m";
		public static readonly string ErrBodyAnsi   = $"\x1b[38;2;{FileErrBodyFg.R};{FileErrBodyFg.G};{FileErrBodyFg.B}m\x1b[48;2;{FileErrBodyBg.R};{FileErrBodyBg.G};{FileErrBodyBg.B}m";
		public static readonly string BodyBgAnsi    = $"\x1b[48;2;{FileBodyBg.R};{FileBodyBg.G};{FileBodyBg.B}m";
		public static readonly string ErrBodyBgAnsi = $"\x1b[48;2;{FileErrBodyBg.R};{FileErrBodyBg.G};{FileErrBodyBg.B}m";
		public static readonly string FileNameAnsi  = "\x1b[38;2;212;182;120m";  // soft gold filename highlight
		public static readonly string ResetAnsi     = "\x1b[39m";         // reset foreground only

		// Diff rows for the edit_file echo. Added lines use a soft dark blue (no green); removed lines a
		// muted dark red. The *HiBg variants are the slightly brighter background marking the intra-line
		// changed span. Each *Ansi sets both fg and the tinted row bg.
		public static readonly Rgb DiffAddFg   = new Rgb(198, 212, 232);  // soft light text on the blue add row
		public static readonly Rgb DiffAddBg   = new Rgb(30, 46, 76);     // soft dark blue add row (a step up from the slate body)
		public static readonly Rgb DiffAddHiBg = new Rgb(46, 74, 116);    // brighter blue for the changed span
		public static readonly Rgb DiffDelFg   = new Rgb(216, 188, 188);  // muted text on the red remove row
		public static readonly Rgb DiffDelBg   = new Rgb(54, 32, 32);     // muted dark red remove row background
		public static readonly Rgb DiffDelHiBg = new Rgb(94, 54, 54);     // brighter red for the changed span

		public static readonly string DiffAddAnsi   = $"\x1b[38;2;{DiffAddFg.R};{DiffAddFg.G};{DiffAddFg.B}m\x1b[48;2;{DiffAddBg.R};{DiffAddBg.G};{DiffAddBg.B}m";
		public static readonly string DiffAddHiAnsi = $"\x1b[48;2;{DiffAddHiBg.R};{DiffAddHiBg.G};{DiffAddHiBg.B}m";
		public static readonly string DiffDelAnsi   = $"\x1b[38;2;{DiffDelFg.R};{DiffDelFg.G};{DiffDelFg.B}m\x1b[48;2;{DiffDelBg.R};{DiffDelBg.G};{DiffDelBg.B}m";
		public static readonly string DiffDelHiAnsi = $"\x1b[48;2;{DiffDelHiBg.R};{DiffDelHiBg.G};{DiffDelHiBg.B}m";
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
	private Action<string>?            _sessionDeleteCallback;

	// Session tree overlay state.
	private bool _sessionTreeOpen = false;
	private int  _sessionTreeSelected = 0;
	private int  _sessionTreeScroll = 0;
	private int  _sessionActive = 0;
	private int  _sessionTotal = 0;
	private readonly List<SessionDisplayInfo> _sessionList = new List<SessionDisplayInfo>();
	private string _sessionActiveId = "";

	private readonly List<string> _completions = new List<string> { "/verbose" };
	private readonly object       _consoleLock = new object();

	private string _currentInputText   = "";
	private int    _currentInputCursor = 0;
	// Plain-text submissions that have been sent to the agent but not yet echoed back as a user
	// message — the agent decides when to consume them mid-turn. Shown as a dim ghost overlay just
	// above the input separator. Keyed by session id so each session keeps its own pending text: a
	// steer can sit queued a long time, and switching sessions must swap which pending text is shown.
	// Submissions accumulate per session and clear together when that session echoes a User frame
	// (ClearPendingGhost). They are unmodifiable and uncancelable once sent (only /cancel, which tears
	// down the whole turn, removes them). Guarded by _consoleLock.
	private readonly Dictionary<string, List<string>> _pendingGhost = new Dictionary<string, List<string>>();
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

	// Auto-follow gating: following the active session is re-enabled only after the view has stayed
	// pinned to the bottom continuously for FollowDelayMs with no new output in the viewed session.
	// _followReadyTick holds the tick when that stable state began (0 = not stable). Output in the
	// viewed session, leaving the bottom, or any scroll/click gesture resets it. The F10 overlay
	// being open does not block it.
	private long _followReadyTick = 0;
	private const int FollowDelayMs = 5000;

	private int _streamingSlot = -1;

	// Per-slot cached BlockLayer (collapsed + expanded Screens). Invalidated on width change or message update.
	private readonly Dictionary<int, BlockLayer> _blockCache = new Dictionary<int, BlockLayer>();
	private int _renderedWidth = 0;

	// Effective width of the history area (full terminal width, minus the session-tree panel when open).
	// Set every Redraw; used by mouse hit-testing so column math matches the reflowed layout.
	private int _historyWidth = 0;

	// Last frame's StackLayout — captured during Redraw so mouse handlers can map row→slot without recomputing.
	// _lastViewTop is that frame's window offset (rows from the top of the stack); valid only when _lastStack
	// is non-null, which is how a "no prior frame" state is signaled.
	private StackLayout? _lastStack;
	private int          _lastViewTop = 0;
	private int          _lastHistoryHeight = 0;

	private int  _scrollbarTopRow    = 0;
	private int  _scrollbarHeight    = 0;
	private int  _scrollbarMaxOffset = 0;

	private int  _hoverSlot         = -1;
	private long _scrollbarShowUntil = 0;

	// A click-toggle (collapse/expand) requests that the toggled block stay pinned under the cursor through
	// the height change rather than letting the view jump. -1 means no pending request; _pendingToggleRow is
	// the terminal row the block's top should land on once the new layout is built.
	private int _pendingToggleSlot = -1;
	private int _pendingToggleRow  = 0;

	// Per-slot horizontal scroll offset (columns), for blocks whose wide code/tables extend past the viewport.
	// Absent / zero means the block sits at its left edge. Driven by horizontal wheel, Shift+wheel and
	// Alt/Shift+arrows over the hovered block.
	private readonly Dictionary<int, int> _blockHScroll = new Dictionary<int, int>();
	private const int HScrollWheelStep = 8;
	private const int HScrollKeyStep   = 6;

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
			// Viewing a new session restarts the follow timer from scratch.
			_followReadyTick = 0;
			_needsErase = true;
			// New conversation model — the previous layout is not a valid anchoring basis.
			_lastStack = null;
			if (_runCts != null)
				Redraw();
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

	public void SetStatsInfo(string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens, int cachedTokens)
	{
		string contextInfo = maxContext > 0 && contextTokens > 0
			? $"  {(int)((double)contextTokens / maxContext * 100)}%/{maxContext}"
			: "";
		string metrics = promptTokens > 0 || completionTokens > 0
			? $"c:{cachedTokens} i:{promptTokens} o:{completionTokens} ${totalCost:F4}{contextInfo}"
			: "";
		lock (_consoleLock)
		{
			_statsMetrics = metrics;
			_statsModelName = model;
			_currentRole = role;
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

	public void OnStreamStart(int streamIndex, FrameType type) { lock (_consoleLock) { _streamingSlot = streamIndex; _busyWordIndex++; _followReadyTick = 0; } }
	public void OnStreamChunk(string chunk) { lock (_consoleLock) _followReadyTick = 0; }
	public void OnStreamEnd() { lock (_consoleLock) _streamingSlot = -1; }

	public void SetAgentBusy(bool busy, long startTick)
	{
		lock (_consoleLock)
		{
			if (busy)
			{
				if (!_agentBusy)
				{
					_agentBusy = true;
					_currentAnimationIndex = Random.Shared.Next(SeparatorLayer.AnimationCount);
				}
				else
				{
					// New activity while already busy — advance the word.
					_busyWordIndex++;
				}
				// Always adopt the viewed session's turn-start tick so the duration tracks that
				// session, even when switching directly between two busy sessions.
				_busyStartTick = startTick;
			}
			else
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
	public void SetSessionDeleteCallback(Action<string> deleteSession) { _sessionDeleteCallback = deleteSession; }

	public void ClearPendingGhost(string sessionId)
	{
		lock (_consoleLock)
		{
			if (!_pendingGhost.TryGetValue(sessionId, out List<string>? queued) || queued.Count == 0)
				return;
			_pendingGhost.Remove(sessionId);
			// Only the viewed session's ghost is on screen, so a redraw is needed only for it.
			if (string.Equals(sessionId, _sessionActiveId, StringComparison.Ordinal))
				Redraw();
		}
	}

	public void SetPendingGhost(string sessionId, string[] lines)
	{
		lock (_consoleLock)
		{
			if (lines.Length == 0)
				_pendingGhost.Remove(sessionId);
			else
				_pendingGhost[sessionId] = new List<string>(lines);
			if (string.Equals(sessionId, _sessionActiveId, StringComparison.Ordinal))
				Redraw();
		}
	}

	public bool IsAutoTrackSuppressed()
	{
		lock (_consoleLock)
		{
			// Following is allowed once the view has sat pinned to the bottom (same threshold as
			// ApplyLayoutAnchoring) with no new output in the viewed session and no user input for the
			// full delay. The viewed session being busy does NOT block following — a session that is
			// working but producing nothing is exactly when the user wants to drift to live output.
			// Output in the viewed session, scrolling, clicking, or leaving the bottom resets the timer.
			// The F10 overlay being open does not suppress following.
			if (_scrollTarget >= 0.5f)
				return true;
			if (_followReadyTick == 0)
				return true;
			return Environment.TickCount64 - _followReadyTick < FollowDelayMs;
		}
	}

	// Maintains the auto-follow timer: starts it when the view is pinned to the bottom, clears it the
	// instant that breaks. Viewed-session output resets it separately (see OnMessageUpdated). Called
	// every input tick under the lock.
	private void UpdateFollowTimer()
	{
		bool stable = _scrollTarget < 0.5f;
		if (stable)
		{
			if (_followReadyTick == 0)
				_followReadyTick = Environment.TickCount64;
		}
		else
		{
			_followReadyTick = 0;
		}
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

			// The F10 selection drives the view, so it must always point at the session currently
			// being viewed. When the view changes on its own (auto-track follows a subagent) or the
			// tree is reordered, snap the cursor to the active session so the highlight and the view
			// stay in sync rather than pointing at a stale row.
			for (int i = 0; i < _sessionList.Count; i++)
			{
				if (string.Equals(_sessionList[i].Id, activeId, StringComparison.Ordinal))
				{
					_sessionTreeSelected = i;
					break;
				}
			}

			// Clamp selection to valid range.
			if (_sessionTreeSelected >= _sessionList.Count)
				_sessionTreeSelected = Math.Max(0, _sessionList.Count - 1);

			// Keep the selected row scrolled into view after a reorder.
			int visRows = Math.Max(1, _lastHeight - 5);
			if (_sessionTreeSelected < _sessionTreeScroll)
				_sessionTreeScroll = _sessionTreeSelected;
			else if (_sessionTreeSelected >= _sessionTreeScroll + visRows)
				_sessionTreeScroll = _sessionTreeSelected - visRows + 1;

			Redraw();
		}
	}

	public Task RunAsync(CancellationToken cancellationToken)
	{
		_runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_baseStatusText = Path.GetFullPath(Directory.GetCurrentDirectory());
		_statusText = _baseStatusText;

		Console.OutputEncoding = new UTF8Encoding(false);
		Console.InputEncoding = new UTF8Encoding(false);
		WindowsConsole.EnableVirtualTerminal();
		WindowsConsole.ReapplyModes();

		_bufferedOut = new StreamWriter(
		Console.OpenStandardOutput(131072),
		new UTF8Encoding(false),
		131072,
		false);
		_bufferedOut.AutoFlush = false;
		Console.SetOut(_bufferedOut);

		_lastWidth = Console.WindowWidth;
		_lastHeight = Console.WindowHeight;

		Console.Write("\x1b[?1049h");                           // EnterAltScreen
		Console.Write("\x1b[?7l");                              // DisableWrap
		Console.Write("\x1b[?1000h\x1b[?1003h\x1b[?1006h");     // EnableMouse
		Console.CursorVisible = false;

		lock (_consoleLock)
			Redraw();

		InputLoop(_runCts.Token);

		RestoreTerminal();
		WindowsConsole.Restore();

		if (_bufferedOut != null)
		{
			StreamWriter restore = new StreamWriter(
				Console.OpenStandardOutput(),
				new UTF8Encoding(false));
			restore.AutoFlush = true;
			Console.SetOut(restore);
			_bufferedOut.Dispose();
			_bufferedOut = null;
		}
		return Task.CompletedTask;
	}

	// Emits the escape sequences that return the terminal to its normal state: disable mouse reporting,
	// re-enable line wrap, leave the alt screen, show the cursor. Idempotent — safe to call after RunAsync
	// already restored, or on a failure path where RunAsync never ran (the worktree chooser left the alt
	// screen up to show "Launching Sandbox…" and the sandbox then failed to start).
	public void RestoreTerminal()
	{
		Console.Write("\x1b[?1006l\x1b[?1003l\x1b[?1000l");     // DisableMouse
		Console.Write("\x1b[?7h");                              // EnableWrap
		Console.Write("\x1b[?1049l");                           // ExitAltScreen
		Console.Out.Flush();
		Console.CursorVisible = true;
	}

	private void OnMessageUpdated(DisplayMessage msg)
	{
		lock (_consoleLock)
		{
			// New output in the viewed session — the user is watching something happen here, so
			// restart the follow delay rather than drifting away to another active session.
			_followReadyTick = 0;
			_blockCache.Remove(msg.Index);
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
		if (w < 10 || h < 5)
			return;

		// When the session tree panel is open it claims the right edge, so the history reflows into the
		// remaining width rather than being painted over. Every width-dependent step below (block layout,
		// the rendered window, the scrollbar, hover/copy hit-testing) works in historyW, not the full w.
		int panelW   = _sessionTreeOpen ? Math.Min(52, Math.Max(36, w / 3)) : 0;
		int historyW = w - panelW;
		_historyWidth = historyW;

		if (historyW != _renderedWidth)
		{
			_blockCache.Clear();
			// Block widths change with the viewport, so horizontal offsets no longer mean the same thing.
			_blockHScroll.Clear();
			_renderedWidth = historyW;
			// Reflow changes every block height; the previous layout is not a valid anchoring basis.
			_lastStack = null;
		}

		if (w != _lastWidth || h != _lastHeight)
		{
			_lastWidth = w;
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

		// Calculate how many rows the pending ghost needs, so we can reserve dedicated layout space.
		int ghostRows = 0;
		lock (_consoleLock)
		{
			if (_pendingGhost.TryGetValue(_sessionActiveId, out List<string>? queued) && queued.Count > 0)
			{
				const string marker = "⧗ ";
				int textWidth = Math.Max(1, historyW - marker.Length);
				List<string> wrapped = new List<string>();
				bool first = true;
				foreach (string entry in queued)
				{
					foreach (string raw in entry.Split('\n'))
					{
						string remaining = raw;
						do
						{
							string slice = remaining.Length > textWidth ? remaining.Substring(0, textWidth) : remaining;
							wrapped.Add((first ? marker : "  ") + slice);
							first = false;
							remaining = remaining.Length > textWidth ? remaining.Substring(textWidth) : string.Empty;
						}
						while (remaining.Length > 0);
					}
				}
				ghostRows = Math.Min(MaxGhostRows, wrapped.Count);
			}
		}

		int historyH = Math.Max(0, popupTop - ghostRows);

		// 1. Lay out one BlockLayer per visible message. Only placements (top/height) are computed here;
		// no full-history Screen is built. SpacerRows=1 gives every block one row of breathing room beneath
		// it without making the spacer part of the block (so toggle/hover math stays clean).
		StackLayout stack = new StackLayout(historyW, spacerRows: 1);
		BuildBlockLayers(stack, historyW);

		Cell bgCell = new Cell(' ', null, Palette.Background, CellStyle.None);

		int totalRows = stack.TotalRows;
		int maxOffset = Math.Max(0, totalRows - historyH);

		// 2. Re-anchor against the previous frame's layout so block height changes (collapse,
		// expand, streaming growth, blocks appearing or hiding) keep the on-screen content stable,
		// then clamp the animation values into the new valid range. A click-toggle pins its own block
		// under the cursor and takes precedence over the generic frame-to-frame anchoring — except when
		// pinned to the bottom, where following new content is the right behavior.
		if (_pendingToggleSlot >= 0 && _scrollTarget >= 0.5f)
			ResolvePendingToggleAnchor(stack, totalRows, historyH, maxOffset);
		else
			ApplyLayoutAnchoring(stack);
		_pendingToggleSlot = -1;

		if (_scrollTarget > maxOffset)
			_scrollTarget = maxOffset;
		if (_scrollTarget < 0f)
			_scrollTarget = 0f;
		if (_historyScrollOffset > maxOffset)
			_historyScrollOffset = maxOffset;
		if (_historyScrollOffset < 0f)
			_historyScrollOffset = 0f;

		_scrollbarTopRow = 0;
		_scrollbarHeight = historyH;
		_scrollbarMaxOffset = maxOffset;

		// Scroll offset is "rows from the bottom"; convert to "rows from the top" of the stack.
		int viewOffsetFromTop = totalRows - historyH - (int)Math.Round(_historyScrollOffset);
		if (viewOffsetFromTop < 0)
			viewOffsetFromTop = 0;

		// Render only the visible window directly — cost scales with what is on screen, not history size.
		Screen historyView = stack.RenderWindow(historyH, viewOffsetFromTop, bgCell, _blockHScroll);

		_lastStack = stack;
		_lastViewTop = viewOffsetFromTop;
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
					Rect hoverRect = new Rect(0, clipTop, historyW, clipBottom - clipTop);
					new ChannelBrightnessEffect(fgFactor: 1.3f, bgFactor: 1.075f).Apply(historyView, hoverRect);
					// Small lerp-to-white on the background so the highlight is still visible when
					// the underlying cell bg is true black (where multiplicative scaling does nothing).
					new BackgroundTintEffect(Rgb.White, 0.03f).Apply(historyView, hoverRect);
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

			Rect trackRect = new Rect(historyW - 2, 0, 2, historyH);
			new TintEffect(Palette.ScrollTrack, scrollbarOpacity).Apply(historyView, trackRect);
			if (thumbH > 0)
			{
				Rect thumbRect = new Rect(historyW - 2, thumbTop, 2, thumbH);
				new TintEffect(Palette.ScrollThumb, scrollbarOpacity).Apply(historyView, thumbRect);
			}
		}

		// 5. Composite all layers onto the full-frame Screen.
		Screen frame = new Screen(w, h, bgCell);
		frame.Blit(historyView, 0, 0, BlendMode.Normal, null);

		// Separator layer. Carries the "{Role} F10(N/T)" status at its right end.
		Screen sep = SeparatorLayer.Build(w, _agentBusy, _busyStartTick, _busyWordIndex, _currentAnimationIndex, _currentRole, _sessionActive, _sessionTotal);
		frame.Blit(sep, 0, separatorRow, BlendMode.Normal, null);

		// Animate the console tab title/icon off the same busy state and tick as the separator.
		ConsoleChrome.Update(_agentBusy, _busyStartTick);

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

		// Pending-input ghost: a dim strip sitting at the bottom of the history area (between
		// history and the popup/separator), showing steering text already sent but not yet
		// consumed by the agent. Renders in the reserved ghostRows space.
		if (ghostRows > 0)
		{
			RenderPendingGhost(frame, w, historyW, historyH, ghostRows);
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
		// The history was already laid out in historyW (= w - panelW), so the panel sits flush beside it
		// rather than on top of it.
		if (_sessionTreeOpen)
		{
			Screen treeOverlay = SessionTreeLayer.Build(_sessionList, _sessionTreeSelected, _sessionTreeScroll, panelW, historyH, _sessionActiveId);
			// Replace (opaque) so underline/italic in the content behind the panel does not bleed through.
			frame.Blit(treeOverlay, historyW, 0, BlendMode.Replace, null);
		}

		// Cursor glow layer (applied last so it lifts all underlying layers).
		if (_mouseRow >= 0 && _mouseCol >= 0)
		{
			int rad = (int)Math.Ceiling(CursorGlowRadius);
			Rect glowRect = new Rect(_mouseCol - rad, _mouseRow - rad, rad * 2 + 1, rad * 2 + 1);
			new CursorGlowEffect(_mouseCol, _mouseRow, CursorGlowRadius, CursorGlowStrength).Apply(frame, glowRect);
		}

		// Copy affordance: a two-cell "copy block" glyph on the mouse's row, six columns left of the
		// scrollbar (so clicking it never lands on the scrollbar and scrolls). ⧉ is narrow in terminals
		// (cursor advance of 1), so a trailing space is appended to make both cells share CopyIconBg. Shown whenever the mouse is
		// over a copyable block in the history area — including while the F10 panel is open, where it tracks
		// the narrowed history width and sits just left of the moved scrollbar rather than hiding under the
		// panel. Drawn after the glow (and after the panel) so it stays crisp. Clicking it copies that block
		// (Shift+click appends) — handled in the MouseClick branch.
		if (_mouseRow >= 0 && _mouseRow < historyH && _mouseCol >= 0 && _mouseCol < historyW && SlotAtTerminalRow(_mouseRow).HasValue)
			frame.WriteText(historyW - 6, _mouseRow, "⧉ ", Palette.CopyIconFg, Palette.CopyIconBg, CellStyle.Bold);

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
		if (_model == null)
			return;

		foreach (DisplayMessage msg in _model.Messages)
		{
			if (ConversationModel.ShouldHide(msg.Type, _model.Mode))
				continue;
			if (string.IsNullOrEmpty(msg.Content))
				continue;

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
	// Pins the just-toggled block so its top lands on the requested terminal row in the newly built layout.
	// Sets the scroll offset directly (rows-from-bottom), clamped into the valid range, so collapsing a block
	// that filled the viewport leaves it visible under the cursor instead of snapping the view to the top.
	private void ResolvePendingToggleAnchor(StackLayout stack, int totalRows, int historyH, int maxOffset)
	{
		BlockPlacement? tp = stack.PlacementOfSlot(_pendingToggleSlot);
		if (!tp.HasValue)
			return;

		int desiredViewTop = tp.Value.Top - _pendingToggleRow;
		float offset = totalRows - historyH - desiredViewTop;
		if (offset < 0f)
			offset = 0f;
		if (offset > maxOffset)
			offset = maxOffset;

		_historyScrollOffset = offset;
		_scrollTarget = offset;
	}

	// Pans the block currently under the cursor by delta columns (positive = right). Clamped so it never
	// scrolls past the block's content; a block no wider than the viewport ignores the gesture. Called under
	// _consoleLock. Returns true if a scrollable block absorbed the gesture.
	private bool ScrollHoveredBlockHorizontally(int delta)
	{
		if (_hoverSlot < 0 || _lastStack == null)
			return false;

		int blockW = _lastStack.BlockWidthOfSlot(_hoverSlot);
		int maxOff = blockW - _historyWidth;
		if (maxOff <= 0)
			return false;   // nothing hidden to the side

		int cur  = _blockHScroll.TryGetValue(_hoverSlot, out int v) ? v : 0;
		int next = cur + delta;
		if (next < 0)
			next = 0;
		if (next > maxOff)
			next = maxOff;

		if (next == 0)
			_blockHScroll.Remove(_hoverSlot);
		else
			_blockHScroll[_hoverSlot] = next;

		Redraw();
		return true;
	}

	private void ApplyLayoutAnchoring(StackLayout stack)
	{
		if (_lastStack == null)
			return;
		// Pinned to the bottom: follow new content instead of anchoring (the normal streaming case).
		if (_scrollTarget < 0.5f)
			return;

		int oldViewTop = _lastViewTop;
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
			_scrollTarget += shift;
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
		if (_scrollbarShowUntil == 0 || now >= _scrollbarShowUntil)
			return 0f;
		long remaining = _scrollbarShowUntil - now;
		if (remaining >= ScrollbarFadeMs)
			return 1f;
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
			_completionIndex = 0;
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
			_completionIndex = 0;
			return;
		}
		_completionActive = true;
		if (_completionIndex < 0 || _completionIndex >= _completionMatches.Count)
			_completionIndex = 0;
	}

	// Returns the substring that would be appended if Tab were pressed right now, or empty if no popup.
	private string ComputeGhostSuffix()
	{
		if (!_completionActive || _completionMatches.Count == 0)
			return string.Empty;
		string pick = _completionMatches[_completionIndex];
		if (pick.Length <= _currentInputText.Length)
			return string.Empty;
		return pick.Substring(_currentInputText.Length);
	}

	private const int MaxGhostRows = 5;

	// Renders the pending-input ghost as a dim, full-width strip in the reserved ghostRows space.
	// The ghost occupies rows [ghostTop, ghostTop + ghostRows - 1], where ghostTop is the first row
	// below the history view. Submissions are flattened and wrapped to the history width; when they
	// exceed MaxGhostRows only the most recent rows are shown.
	private void RenderPendingGhost(Screen frame, int w, int historyW, int ghostTop, int ghostRows)
	{
		if (ghostTop < 0 || w <= 0 || historyW <= 0 || ghostRows <= 0)
			return;

		List<string> entries;
		lock (_consoleLock)
		{
			if (!_pendingGhost.TryGetValue(_sessionActiveId, out List<string>? queued) || queued.Count == 0)
				return;
			entries = new List<string>(queued);
		}

		const string marker = "⧗ ";
		const string indent = "  ";
		// Use historyW (the history area width) for wrapping so the reserved rows match.
		int textWidth = Math.Max(1, historyW - marker.Length);

		// Flatten every submission to raw lines, then hard-wrap each to the available width. The very
		// first row carries the queued marker; all others are indented to align beneath it.
		List<string> wrapped = new List<string>();
		bool first = true;
		foreach (string entry in entries)
		{
			foreach (string raw in entry.Split('\n'))
			{
				string remaining = raw;
				do
				{
					string slice = remaining.Length > textWidth ? remaining.Substring(0, textWidth) : remaining;
					wrapped.Add((first ? marker : indent) + slice);
					first = false;
					remaining = remaining.Length > textWidth ? remaining.Substring(textWidth) : string.Empty;
				}
				while (remaining.Length > 0);
			}
		}

		if (wrapped.Count == 0)
			return;

		// Keep the most recent rows when the queue is taller than the cap.
		int show = Math.Min(ghostRows, wrapped.Count);
		int firstIndex = wrapped.Count - show;

		for (int i = 0; i < show; i++)
		{
			int row = ghostTop + i;
			string line = wrapped[firstIndex + i];
			// Full-width strip so it reads as a floating object over the history beneath it.
			frame.WriteText(0, row, line.PadRight(w), Palette.GhostFg, Palette.InputBg, CellStyle.None);
		}
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
		if (_lastStack == null)
			return null;
		if (row < 0 || row >= _lastHistoryHeight)
			return null;
		int sourceRow = row + _lastViewTop;
		return _lastStack.SlotAtRow(sourceRow);
	}

	// Copies the block under the given terminal row to the clipboard, replacing its contents — or appending
	// when add is true (Shift+click). Returns true when a block was found and copied. Called under _consoleLock.
	private bool CopyBlockAtRow(int row, bool add)
	{
		int? slot = SlotAtTerminalRow(row);
		if (!slot.HasValue || _model == null || slot.Value < 0 || slot.Value >= _model.Messages.Count)
			return false;

		string text = BlockCopyText(_model.Messages[slot.Value]);
		if (string.IsNullOrEmpty(text))
			return false;

		if (add)
		{
			string existing = ClipboardService.GetText() ?? string.Empty;
			ClipboardService.SetText(existing.Length == 0 ? text : existing + "\n\n" + text);
			SetStatus("Added block to clipboard");
		}
		else
		{
			ClipboardService.SetText(text);
			SetStatus("Copied block to clipboard");
		}
		return true;
	}

	// Returns the plain text to copy for a block. Tool-call blocks include the paired response body so the
	// copy matches what is shown on screen; every other block copies its content verbatim.
	private static string BlockCopyText(DisplayMessage msg)
	{
		if (msg.Type == FrameType.ToolCall)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append(msg.Content);
			if (!string.IsNullOrEmpty(msg.PairedResponseContent))
				sb.Append('\n').Append(msg.PairedResponseContent);
			if (!string.IsNullOrEmpty(msg.PairedResponseError))
				sb.Append('\n').Append(msg.PairedResponseError);
			return sb.ToString();
		}
		return msg.Content;
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
					// Update the follow timer before draining frames so the suppression check the
					// drained frames trigger sees the current idle/at-bottom state.
					UpdateFollowTimer();
					_frameDrain?.Invoke();
					int curW = Console.WindowWidth;
					int curH = Console.WindowHeight;
					if (curW != _lastWidth || curH != _lastHeight)
					{
						// Reflow immediately on resize instead of waiting for a mouse/key event.
						_lastWidth = curW;
						_lastHeight = curH;
						_needsErase = true;
						_blockCache.Clear();
						_blockHScroll.Clear();
						_renderedWidth = curW;
						_lastStack = null;
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
					if (needRedraw)
						Redraw();
				}
			}

			ConsoleInputEvent? evOpt = WindowsConsole.ReadInputWithTimeout(16);
			if (evOpt == null)
				continue;
			ConsoleInputEvent inputEv = evOpt.Value;

			if (inputEv.Type == InputEventType.MouseMove)
			{
				lock (_consoleLock)
				{
					_followReadyTick = 0;
					int oldHoverSlot = _hoverSlot;
					_mouseRow = inputEv.Row;
					_mouseCol = inputEv.Col;
					// Over the session-tree panel there is no history block to highlight.
					bool overPanel = _sessionTreeOpen && inputEv.Col >= _historyWidth;
					int? slot = overPanel ? null : SlotAtTerminalRow(inputEv.Row);
					_hoverSlot = slot ?? -1;
					if (oldHoverSlot != _hoverSlot)
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
					// Re-resolve the block under the cursor: it's the wheel target, and on a vertical scroll the
					// content moves under a stationary mouse so the highlight should track it.
					bool wheelOverPanel = _sessionTreeOpen && inputEv.Col >= _historyWidth;
					_hoverSlot = wheelOverPanel ? -1 : (SlotAtTerminalRow(inputEv.Row) ?? -1);

					// A horizontal wheel (tilt / trackpad) or Shift+wheel pans the hovered block sideways;
					// positive delta scrolls right. If the block has nothing hidden to the side, fall through
					// to vertical scrolling so the gesture is never swallowed.
					if (inputEv.Horizontal || inputEv.Shift)
					{
						int hDelta = inputEv.WheelDelta > 0 ? HScrollWheelStep : -HScrollWheelStep;
						if (ScrollHoveredBlockHorizontally(hDelta))
							continue;
					}

					_scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
					// Any scroll gesture resets the follow timer, even one that lands back at the bottom.
					_followReadyTick = 0;
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
					int scrollCol = _historyWidth - 2;

					// A click inside the session-tree panel is the panel's, not the history's — never toggle a
					// block underneath it.
					if (_sessionTreeOpen && inputEv.Col >= _historyWidth)
						continue;

					// Copy-block affordance sits three columns left of the scrollbar on the mouse's row. A click
					// there copies that block to the clipboard; Shift+click appends instead of replacing. The
					// panel-area guard above already excluded clicks over the F10 panel, so this works whether
					// the panel is open or closed.
					if ((inputEv.Col == _historyWidth - 6 || inputEv.Col == _historyWidth - 5) && CopyBlockAtRow(inputEv.Row, inputEv.Shift))
						continue;

					if (inputEv.Col >= scrollCol && _scrollbarMaxOffset > 0
						&& inputEv.Row >= _scrollbarTopRow
						&& inputEv.Row < _scrollbarTopRow + _scrollbarHeight)
					{
						_scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
						_followReadyTick = 0;
						float fraction = (float)(inputEv.Row - _scrollbarTopRow) / Math.Max(1, _scrollbarHeight - 1);
						_scrollTarget = (1f - fraction) * _scrollbarMaxOffset;
						_scrollTarget = Math.Max(0f, Math.Min(_scrollbarMaxOffset, _scrollTarget));
					}
					else
					{
						int? slot = SlotAtTerminalRow(inputEv.Row);
						if (slot.HasValue)
						{
							// Decide where the toggled block's top should land after the height change so the
							// view stays put under the cursor. If the block's header is currently on-screen,
							// keep it on the same row; otherwise (a block taller than the viewport, whose top
							// is scrolled off above) drop its top to the clicked row so the collapsed block
							// sits directly under the mouse and can be toggled back open.
							int desiredRow = inputEv.Row;
							BlockPlacement? op = _lastStack?.PlacementOfSlot(slot.Value);
							if (op.HasValue)
							{
								int topRow = op.Value.Top - _lastViewTop;
								if (topRow >= 0 && topRow < _lastHistoryHeight)
									desiredRow = topRow;
							}
							_pendingToggleSlot = slot.Value;
							_pendingToggleRow = desiredRow;

							// Redraw's pending-toggle anchoring keeps the block stable through the height change.
							_model?.ToggleCollapsed(slot.Value);
							// Keep hover indicator after the click — mouse is still over the block.
							_hoverSlot = slot.Value;
							_followReadyTick = 0;
						}
					}
				}
				continue;
			}

			if (inputEv.Type == InputEventType.Paste)
			{
				lock (_consoleLock)
				{
					_followReadyTick = 0;
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

			// The session-tree panel is non-modal: it stays open beside the input line without blocking
			// typing. It borrows only the bare Up/Down arrows for tree navigation when the input line is
			// empty — when the buffer is non-empty, Up/Down fall through to autocomplete/history so the
			// user can keep typing. Delete drops a session only when the input line is empty. Every other
			// key falls through to normal handling below. Navigation previews live.
			if (_sessionTreeOpen)
			{
				bool treeHandled = false;
				lock (_consoleLock)
				{
					_followReadyTick = 0;
					// Up/Down only navigate the tree when the input line is empty.
					if (inputBuffer.Length == 0 && key.Key == ConsoleKey.UpArrow && !ctrl && !alt && !shift)
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
						treeHandled = true;
					}
					else if (inputBuffer.Length == 0 && key.Key == ConsoleKey.DownArrow && !ctrl && !alt && !shift)
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
						treeHandled = true;
					}
					else if (key.Key == ConsoleKey.Delete && inputBuffer.Length == 0)
					{
						// Delete removes a session and its descendants from memory and disk — but only when
						// the input line is empty, so it never steals the forward-delete from text editing. A
						// session still running is left alone. Deleting the root tears the whole tree down and
						// the agent starts a fresh session in its place.
						if (_sessionTreeSelected >= 0 && _sessionTreeSelected < _sessionList.Count)
						{
							SessionDisplayInfo target = _sessionList[_sessionTreeSelected];
							if (!target.IsBusy)
							{
								// The callback rebuilds the list via SetSessionList; clamp afterward,
								// then preview whatever now sits at the selection so the view follows.
								_sessionDeleteCallback?.Invoke(target.Id);
								if (_sessionTreeSelected >= _sessionList.Count)
									_sessionTreeSelected = Math.Max(0, _sessionList.Count - 1);
								PreviewSelectedSession();
								Redraw();
							}
						}
						treeHandled = true;
					}
				}
				if (treeHandled)
					continue;
			}

			if (key.Key == ConsoleKey.Enter && !shift && !alt)
			{
				// If a completion popup is active, accept the highlighted entry first.
				bool popupActive;
				string? accept = null;
				lock (_consoleLock)
				{
					_followReadyTick = 0;
					popupActive = _completionActive && _completionMatches.Count > 0;
					if (popupActive)
						accept = _completionMatches[_completionIndex];
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
					// Ghost all submitted text (including slash commands) so the user sees what was sent.
					// Keyed by the session being viewed — which is exactly the session the text is sent to.
					lock (_consoleLock)
					{
						if (!_pendingGhost.TryGetValue(_sessionActiveId, out List<string>? queued))
						{
							queued = new List<string>();
							_pendingGhost[_sessionActiveId] = queued;
						}
						queued.Add(text);
					}
					_ = SendAsync(text);
				}

				lock (_consoleLock)
					_scrollTarget = 0f;

				SetInput("", 0);
			}
			else if ((key.Key == ConsoleKey.Enter && (shift || alt)) || (key.Key == ConsoleKey.J && ctrl))
			{
				lock (_consoleLock)
					_followReadyTick = 0;
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
					_followReadyTick = 0;
					popupActive = _completionActive && _completionMatches.Count > 0;
					if (popupActive)
						accept = _completionMatches[_completionIndex];
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
				lock (_consoleLock)
					_followReadyTick = 0;
				inCompletion = false;
				int newPos = InputLayer.WordStartBefore(inputBuffer.ToString(), cursorPos);
				inputBuffer.Remove(newPos, cursorPos - newPos);
				cursorPos = newPos;
				SetInput(inputBuffer.ToString(), cursorPos);
			}
			else if (key.Key == ConsoleKey.Backspace)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
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
				lock (_consoleLock)
					_followReadyTick = 0;
				inCompletion = false;
				int newPos = InputLayer.WordEndAfter(inputBuffer.ToString(), cursorPos);
				inputBuffer.Remove(cursorPos, newPos - cursorPos);
				SetInput(inputBuffer.ToString(), cursorPos);
			}
			else if (key.Key == ConsoleKey.Delete)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				inCompletion = false;
				if (cursorPos < inputBuffer.Length)
				{
					inputBuffer.Remove(cursorPos, 1);
					SetInput(inputBuffer.ToString(), cursorPos);
				}
			}
			else if ((key.Key == ConsoleKey.LeftArrow || key.Key == ConsoleKey.RightArrow) && (alt || shift))
			{
				// Alt/Shift + Left/Right pans the block under the cursor sideways instead of moving the text
				// caret. It is dedicated to block scrolling — with no hovered scrollable block it is a no-op
				// rather than caret motion, so it never disturbs the input line.
				int dir = key.Key == ConsoleKey.RightArrow ? HScrollKeyStep : -HScrollKeyStep;
				lock (_consoleLock)
				{
					_followReadyTick = 0;
					ScrollHoveredBlockHorizontally(dir);
				}
			}
			else if ((key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.DownArrow) && alt)
			{
				// Alt + Up/Down nudges the history scroll one row, the vertical counterpart to Alt + Left/Right.
				// Kept distinct from the bare arrows so it never disturbs the input caret or history recall.
				lock (_consoleLock)
				{
					_scrollbarShowUntil = Environment.TickCount64 + ScrollbarShowMs;
					_followReadyTick = 0;
					if (key.Key == ConsoleKey.UpArrow)
						_scrollTarget += 1;
					else
						_scrollTarget = Math.Max(0f, _scrollTarget - 1);
				}
			}
			else if (key.Key == ConsoleKey.LeftArrow && ctrl)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				cursorPos = InputLayer.WordStartBefore(inputBuffer.ToString(), cursorPos);
				SetInput(inputBuffer.ToString(), cursorPos);
			}
			else if (key.Key == ConsoleKey.LeftArrow)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				if (cursorPos > 0)
				{ cursorPos--; SetInput(inputBuffer.ToString(), cursorPos); }
			}
			else if (key.Key == ConsoleKey.RightArrow && ctrl)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				cursorPos = InputLayer.WordEndAfter(inputBuffer.ToString(), cursorPos);
				SetInput(inputBuffer.ToString(), cursorPos);
			}
			else if (key.Key == ConsoleKey.RightArrow)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				if (cursorPos < inputBuffer.Length)
				{ cursorPos++; SetInput(inputBuffer.ToString(), cursorPos); }
			}
			else if (key.Key == ConsoleKey.Home)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				cursorPos = 0;
				SetInput(inputBuffer.ToString(), cursorPos);
			}
			else if (key.Key == ConsoleKey.End)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				cursorPos = inputBuffer.Length;
				SetInput(inputBuffer.ToString(), cursorPos);
			}
			else if (key.Key == ConsoleKey.UpArrow)
			{
				bool popupActive;
				lock (_consoleLock)
				{
					_followReadyTick = 0;
					popupActive = _completionActive && _completionMatches.Count > 0;
				}
				if (popupActive)
				{
					lock (_consoleLock)
					{
						_completionIndex--;
						if (_completionIndex < 0)
							_completionIndex = _completionMatches.Count - 1;
						Redraw();
					}
					continue;
				}
				if (inputBuffer.Length > 0)
				{
					int upW = Console.WindowWidth;
					if (upW < 1)
						upW = 80;
					(int upLine, int upCol) = InputLayer.CursorInInputLines(inputBuffer.ToString(), cursorPos, upW);
					cursorPos = InputLayer.CharFromInputLines(inputBuffer.ToString(), upLine - 1, upCol, upW);
					SetInput(inputBuffer.ToString(), cursorPos);
				}
				else if (history.Count > 0)
				{
					if (historyIndex == -1)
					{ historySavedDraft = inputBuffer.ToString(); historyIndex = history.Count - 1; }
					else if (historyIndex > 0)
						historyIndex--;
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
				{
					_followReadyTick = 0;
					popupActive = _completionActive && _completionMatches.Count > 0;
				}
				if (popupActive)
				{
					lock (_consoleLock)
					{
						_completionIndex++;
						if (_completionIndex >= _completionMatches.Count)
							_completionIndex = 0;
						Redraw();
					}
					continue;
				}
				if (inputBuffer.Length > 0)
				{
					int downW = Console.WindowWidth;
					if (downW < 1)
						downW = 80;
					(int downLine, int downCol) = InputLayer.CursorInInputLines(inputBuffer.ToString(), cursorPos, downW);
					int totalLines = InputLayer.WrapInput(inputBuffer.ToString(), downW).Count;
					if (downLine < totalLines - 1)
					{
						cursorPos = InputLayer.CharFromInputLines(inputBuffer.ToString(), downLine + 1, downCol, downW);
						SetInput(inputBuffer.ToString(), cursorPos);
					}
				}
				else if (historyIndex >= 0)
				{
					historyIndex++;
					string entry;
					if (historyIndex >= history.Count)
					{ historyIndex = -1; entry = historySavedDraft; }
					else
						entry = history[historyIndex];
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
					_followReadyTick = 0;
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
					_followReadyTick = 0;
					int pageH = Math.Max(1, Console.WindowHeight - 3 - InputLayer.ComputeInputRows(_currentInputText, Console.WindowWidth));
					_scrollTarget = Math.Max(0f, _scrollTarget - Math.Max(1, pageH - 1));
				}
				continue;
			}
			else if (key.Key == ConsoleKey.Escape)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
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
					_followReadyTick = 0;
					if (_sessionList.Count > 0)
					{
						_sessionTreeOpen = !_sessionTreeOpen;
						if (_sessionTreeOpen)
						{
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
				lock (_consoleLock)
					_followReadyTick = 0;
				cursorPos = 0;
				SetInput(inputBuffer.ToString(), cursorPos);
			}
			else if (key.Key == ConsoleKey.E && ctrl)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				cursorPos = inputBuffer.Length;
				SetInput(inputBuffer.ToString(), cursorPos);
			}
			else if (key.Key == ConsoleKey.V && ctrl)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
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
				lock (_consoleLock)
					_followReadyTick = 0;
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
				lock (_consoleLock)
					_followReadyTick = 0;
				_requestExit?.Invoke();
			}
			else if (key.Key == ConsoleKey.D && ctrl)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				_requestExit?.Invoke();
			}
			else if (key.Key == ConsoleKey.O && ctrl)
			{
				lock (_consoleLock)
					_followReadyTick = 0;
				CycleCollapseMode();
			}
			else if (!char.IsControl(key.KeyChar))
			{
				lock (_consoleLock)
					_followReadyTick = 0;
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
			SetStatus($"[send error] {ex}");
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