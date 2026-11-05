using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;


// Owns the OS console "chrome": the window/tab title and, on the legacy console host, the title-bar icon.
// While a session is working the title's separator animates — the same trick Claude Code uses, since no
// terminal exposes a runtime escape to set a tab-icon image — and it reads "!!" when nothing is running and
// a host command waits on the user's approval. The icon animation reaches only the classic conhost window (Windows Terminal owns
// its tab icons), so it is best-effort and silently no-ops elsewhere.
internal static class ConsoleChrome
{
	// The idle separator and the busy rotation. Every frame is a single cell wide so the title's length
	// never changes while it animates — that avoids the tab resize/flicker a variable-width spinner causes.
	private const           string   Paw          = "🐾";
	private const           string   IdleSep      = "─";
	private const           string   WaitingSep   = "!!";
	private static readonly string[] BusyFrames   = new string[] { "─", "╲", "│", "╱" };
	private const           long     TitleFrameMs = 120;
	private const           long     IconFrameMs  = 450;

	private static readonly object Gate           = new object();
	private static          string _name          = "";
	private static          string _lastTitle     = "";
	private static          int    _lastIconFrame = -1;

	// Two copies of beast.ico (upright, horizontally mirrored) that the busy icon animation alternates
	// between for a gentle "look left / look right" wiggle. Null when unavailable — the title animation
	// works regardless.
	private static IntPtr[]? _iconFrames;

	private const           uint   WM_SETICON = 0x0080;
	private static readonly IntPtr IconSmall  = IntPtr.Zero;
	private static readonly IntPtr IconBig    = new IntPtr(1);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetConsoleWindow();

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	// Records the label (the worktree/agent name) and primes the icon frames. Call once at startup.
	internal static void Configure(string name)
	{
		lock (Gate)
		{
			_name = name ?? "";
			TryInitIcon();
		}
		Update(false, false);
	}

	// The title for a state at a moment in time. "!!" means nothing at all is happening until the user
	// answers, so any work still going on outranks it: the animation is the true report while it lasts.
	internal static string BuildTitle(string name, bool busy, bool waiting, long tick)
	{
		string sep = busy ? BusyFrames[(int)((tick / TitleFrameMs) % BusyFrames.Length)] : waiting ? WaitingSep : IdleSep;

		string title;
		if (name.Length > 0)
			title = $"{Paw} Beast {sep} {name}";
		else
			title = busy || waiting ? $"{Paw} Beast {sep}" : $"{Paw} Beast";
		return title;
	}

	// Refreshes the title (and conhost icon) for the current state. Cheap to call every tick: it recomputes
	// the frame and only touches the OS when the rendered title or icon frame actually changes.
	internal static void Update(bool busy, bool waiting)
	{
		long   tick      = Environment.TickCount64;
		int    iconFrame = busy ? (int)((tick / IconFrameMs) % 2) : 0;
		string title     = BuildTitle(_name, busy, waiting, tick);

		lock (Gate)
		{
			if (title != _lastTitle)
			{
				Console.Title = title;
				_lastTitle    = title;
			}

			if (_iconFrames != null && iconFrame != _lastIconFrame)
			{
				SetConsoleIcon(_iconFrames[iconFrame]);
				_lastIconFrame = iconFrame;
			}
		}
	}

	private static void SetConsoleIcon(IntPtr hIcon)
	{
		IntPtr hwnd = GetConsoleWindow();
		if (hwnd == IntPtr.Zero)
			return;
		SendMessage(hwnd, WM_SETICON, IconSmall, hIcon);
		SendMessage(hwnd, WM_SETICON,   IconBig, hIcon);
	}

	// Builds the upright + mirrored HICON frames from the embedded beast.ico. No-ops off Windows or when
	// the resource/GDI is unavailable. Caller already holds Gate.
	private static void TryInitIcon()
	{
		if (_iconFrames != null)
			return;
		// IsWindowsVersionAtLeast (not just IsWindows) is what satisfies the platform analyzer for the
		// System.Drawing calls below, which are annotated as requiring Windows 6.1+.
		if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
			return;

		try
		{
			Assembly asm     = typeof(ConsoleChrome).Assembly;
			string?  resName = null;
			foreach (string candidate in asm.GetManifestResourceNames())
			{
				if (candidate.EndsWith("beast.ico", StringComparison.OrdinalIgnoreCase))
				{
					resName = candidate;
					break;
				}
			}
			if (resName == null)
				return;

			using Stream? stream = asm.GetManifestResourceStream(resName);
			if (stream == null)
				return;

			using Icon icon       = new Icon(stream);
			using Bitmap upright  = icon.ToBitmap();
			using Bitmap mirrored = (Bitmap)upright.Clone();
			mirrored.RotateFlip(RotateFlipType.RotateNoneFlipX);

			_iconFrames = new IntPtr[] { upright.GetHicon(), mirrored.GetHicon() };
		}
		catch
		{
			_iconFrames = null;
		}
	}
}