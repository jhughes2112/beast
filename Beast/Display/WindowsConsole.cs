using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;


// Manages Windows console mode and provides a unified ReadInputWithTimeout that surfaces
// both KEY_EVENT and MOUSE_EVENT records via ReadConsoleInput. Console.ReadKey discards
// mouse events, so we call ReadConsoleInput directly. Used through PlatformWindows; the sequence
// decoding it shares with macOS/Linux lives in VtKeys.
internal static class WindowsConsole
{
	private const int StdInputHandle  = -10;
	private const int StdOutputHandle = -11;

	private const uint EnableProcessedInput            = 0x0001;
	private const uint EnableVirtualTerminalProcessing = 0x0004;
	private const uint EnableVirtualTerminalInput      = 0x0200;
	private const uint DisableQuickEditMode            = 0x0040;
	private const uint EnableExtendedFlags             = 0x0080;

	private const uint   WAIT_OBJECT_0    = 0x00000000;
	private const ushort KEY_EVENT_TYPE   = 0x0001;
	private const ushort MOUSE_EVENT_TYPE = 0x0002;
	private const uint   MOUSE_WHEELED    = 0x0004;
	private const uint   MOUSE_HWHEELED   = 0x0008;
	private const uint   LEFT_BUTTON      = 0x0001;
	private const uint   RIGHT_BUTTON     = 0x0002;

	// Bracketed paste: when enabled, the terminal wraps any paste in ESC[200~ ... ESC[201~ and hands
	// the content to us as data instead of cooking it. This is what suppresses the console host's
	// "paste many lines?" confirmation popup. The markers arrive through ReadConsoleInput as ordinary
	// key char records once ENABLE_VIRTUAL_TERMINAL_INPUT is set.
	private const string EnableBracketedPaste  = "\x1b[?2004h";
	private const string DisableBracketedPaste = "\x1b[?2004l";

	// Enabling ENABLE_VIRTUAL_TERMINAL_INPUT can put the terminal into VT mouse-reporting modes, in
	// which case mouse movement/clicks arrive as CSI mouse sequences (ESC[<...M/m) in the char stream
	// instead of as native MOUSE_EVENT records. We handle mouse through MOUSE_EVENT records, so turn
	// every VT mouse-reporting mode OFF: X10/normal (1000), button-event (1002), any-event (1003),
	// and SGR extended (1006). With these off the terminal stops emitting mouse VT sequences and
	// Windows keeps delivering MOUSE_EVENT records.
	private const           string DisableVtMouseModes = "\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l";
	private static readonly char[] PasteStartMarker    = new char[] { '[', '2', '0', '0', '~' };
	private static readonly char[] PasteEndMarker      = new char[] { '\x1b', '[', '2', '0', '1', '~' };

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

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool PeekConsoleInputW(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

	[StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
	private struct INPUT_RECORD
	{
		[FieldOffset(0)] public ushort EventType;
		[FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
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
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return;

		IntPtr hOut = GetStdHandle(StdOutputHandle);
		IntPtr hIn  = GetStdHandle(StdInputHandle);

		if (!GetConsoleMode(hOut, out uint outMode))
			return;
		if (!GetConsoleMode(hIn, out uint inMode))
			return;

		if (!_saved)
		{
			_originalOutputMode = outMode;
			_originalInputMode  = inMode;
			_saved              = true;
		}

		SetConsoleMode(hOut, outMode | EnableVirtualTerminalProcessing);
		// Enable VT input so bracketed-paste markers are delivered to us, and disable Quick Edit so
		// mouse clicks are not swallowed for text selection. Processed input off makes Ctrl+C a key
		// rather than a signal, as it is in raw mode on macOS/Linux.
		SetConsoleMode(hIn, (inMode | EnableExtendedFlags | EnableVirtualTerminalInput) & ~(DisableQuickEditMode | EnableProcessedInput));

		Console.Out.Write(DisableVtMouseModes);
		Console.Out.Write(EnableBracketedPaste);
		Console.Out.Flush();
	}

	internal static void ReapplyModes()
	{
		if (!_saved)
			return;
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return;

		IntPtr hOut = GetStdHandle(StdOutputHandle);
		IntPtr hIn  = GetStdHandle(StdInputHandle);

		if (!GetConsoleMode(hOut, out uint outMode))
			return;
		if (!GetConsoleMode(hIn, out uint inMode))
			return;

		SetConsoleMode(hOut, outMode | EnableVirtualTerminalProcessing);
		SetConsoleMode(hIn, (inMode | EnableExtendedFlags | EnableVirtualTerminalInput) & ~(DisableQuickEditMode | EnableProcessedInput));

		Console.Out.Write(DisableVtMouseModes);
		Console.Out.Write(EnableBracketedPaste);
		Console.Out.Flush();
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
			if (!Console.KeyAvailable)
				return null;
			ConsoleKeyInfo k = Console.ReadKey(true);
			return new ConsoleInputEvent { Type = InputEventType.Key, Key = k };
		}

		IntPtr hIn = GetStdHandle(StdInputHandle);
		if (WaitForSingleObject(hIn, (uint)timeoutMs) != WAIT_OBJECT_0)
			return null;

		INPUT_RECORD[] buf = new INPUT_RECORD[1];
		if (!ReadConsoleInputW(hIn, buf, 1, out uint read) || read == 0)
			return null;

		INPUT_RECORD rec = buf[0];

		if (rec.EventType == KEY_EVENT_TYPE)
		{
			KEY_EVENT_RECORD k = rec.KeyEvent;
			if (k.bKeyDown == 0)
				return null;

			// With VT input enabled the terminal no longer hands us cooked virtual-key records for
			// navigation, function, or Alt-modified keys. Everything beginning with ESC is a VT sequence:
			// bracketed paste (ESC[200~...), CSI/SS3 keys (ESC[ / ESCO), or an Alt-prefixed key (ESC + char).
			// Decode the whole thing in one place so no fragment ever leaks to the consumer.
			if (k.UnicodeChar == '\x1b')
			{
				if (PeekMatches(hIn, PasteStartMarker))
				{
					ConsumeMarker(hIn, PasteStartMarker.Length);
					string pasted = ReadBracketedPasteBody(hIn);
					return new ConsoleInputEvent { Type = InputEventType.Paste, Text = pasted };
				}

				ConsoleInputEvent? vt = TryReadEscapeSequence(hIn);
				if (vt != null)
					return vt;

				// Nothing followed the ESC: it is a real Escape press.
				return new ConsoleInputEvent { Type = InputEventType.Key, Key = new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false) };
			}

			bool shift = (k.dwControlKeyState & 0x0010u) != 0;
			bool ctrl  = (k.dwControlKeyState & 0x000Cu) != 0;
			bool alt   = (k.dwControlKeyState & 0x0003u) != 0;

			// With VT input enabled, editing/control keys (Tab, Backspace, Enter, Ctrl-letter, etc.)
			// arrive as char records carrying a control character in UnicodeChar with wVirtualKeyCode == 0.
			// Translate the control char back into the ConsoleKey/modifier shape DisplayScreen expects.
			// Printable characters and keys that already carry a virtual key code fall through unchanged.
			ConsoleInputEvent? ctl = VtKeys.TranslateControlChar(k.UnicodeChar, shift, alt, ctrl);
			if (ctl != null)
				return ctl;

			ConsoleKey     ck = (ConsoleKey)k.wVirtualKeyCode;
			ConsoleKeyInfo ki = new ConsoleKeyInfo(k.UnicodeChar, ck, shift, alt, ctrl);

			return new ConsoleInputEvent { Type = InputEventType.Key, Key = ki };
		}

		if (rec.EventType == MOUSE_EVENT_TYPE)
		{
			MOUSE_EVENT_RECORD m = rec.MouseEvent;

			if ((m.dwEventFlags & MOUSE_WHEELED) != 0 || (m.dwEventFlags & MOUSE_HWHEELED) != 0)
			{
				bool  horizontal = (m.dwEventFlags & MOUSE_HWHEELED) != 0;
				short delta      = (short)(m.dwButtonState >> 16);
				bool  wheelShift = (m.dwControlKeyState & 0x0010u) != 0;
				return new ConsoleInputEvent { Type = InputEventType.MouseWheel, Col = m.MouseX, Row = m.MouseY, WheelDelta = delta, Shift = wheelShift, Horizontal = horizontal };
			}

			// Button presses only: the matching release arrives with no button bits set and is
			// ignored, so one physical click produces one event. Quick Edit is off, so the right
			// button is ours rather than the console host's own paste.
			if (m.dwEventFlags == 0 && (m.dwButtonState & (LEFT_BUTTON | RIGHT_BUTTON)) != 0)
			{
				bool clickShift = (m.dwControlKeyState & 0x0010u) != 0;
				bool right      = (m.dwButtonState & RIGHT_BUTTON) != 0;
				return new ConsoleInputEvent { Type = InputEventType.MouseClick, Col = m.MouseX, Row = m.MouseY, Shift = clickShift, Right = right };
			}

			if ((m.dwEventFlags & 0x0001u) != 0) // MOUSE_MOVED
			{
				return new ConsoleInputEvent { Type = InputEventType.MouseMove, Col = m.MouseX, Row = m.MouseY };
			}
		}

		return null;
	}

	// Peeks the next records (without consuming) to see whether they are key char records spelling out
	// the given marker characters in order. Used to confirm an ESC begins a bracketed-paste sequence.
	private static bool PeekMatches(IntPtr hIn, char[] marker)
	{
		INPUT_RECORD[] peek = new INPUT_RECORD[marker.Length];
		if (!PeekConsoleInputW(hIn, peek, (uint)marker.Length, out uint peeked) || peeked < marker.Length)
			return false;

		for (int i = 0; i < marker.Length; i++)
		{
			if (peek[i].EventType != KEY_EVENT_TYPE)
				return false;
			KEY_EVENT_RECORD k = peek[i].KeyEvent;
			if (k.bKeyDown == 0)
				return false;
			if (k.UnicodeChar != marker[i])
				return false;
		}
		return true;
	}

	// Consumes exactly count records from the input queue (the marker we already matched via PeekMatches).
	private static void ConsumeMarker(IntPtr hIn, int count)
	{
		INPUT_RECORD[] buf = new INPUT_RECORD[count];
		ReadConsoleInputW(hIn, buf, (uint)count, out _);
	}

	// Reads the body of a bracketed paste up to and including the ESC[201~ terminator. Key-up records
	// and non-key events inside the block are ignored; carriage returns are normalized to '\n'.
	private static string ReadBracketedPasteBody(IntPtr hIn)
	{
		StringBuilder  sb  = new StringBuilder();
		INPUT_RECORD[] one = new INPUT_RECORD[1];

		while (true)
		{
			// Stop when the end marker is next in the queue, then consume it.
			if (PeekMatches(hIn, PasteEndMarker))
			{
				ConsumeMarker(hIn, PasteEndMarker.Length);
				break;
			}

			if (WaitForSingleObject(hIn, 50) != WAIT_OBJECT_0)
			{
				// No more data arrived; treat the paste as complete to avoid hanging on a missing
				// terminator (e.g. a host that dropped the closing marker).
				if (!PeekHasInput(hIn))
					break;
			}

			if (!ReadConsoleInputW(hIn, one, 1, out uint read) || read == 0)
				break;
			if (one[0].EventType != KEY_EVENT_TYPE)
				continue;

			KEY_EVENT_RECORD k = one[0].KeyEvent;
			if (k.bKeyDown == 0)
				continue;
			if (k.UnicodeChar == '\0')
				continue;

			VtKeys.AppendPasteChar(sb, k.UnicodeChar);
		}

		return sb.ToString();
	}

	private static bool PeekHasInput(IntPtr hIn)
	{
		INPUT_RECORD[] one = new INPUT_RECORD[1];
		return PeekConsoleInputW(hIn, one, 1, out uint peeked) && peeked > 0;
	}

	// Decodes a VT sequence that follows an already-consumed ESC. Returns null (consuming nothing) when
	// nothing recognizable follows, so the caller can treat the ESC as a real Escape press. Handles:
	//   ESC [ ...        CSI: arrows, Home/End, Insert/Delete, PageUp/Down, F-keys, with modifiers
	//   ESC O <final>    SS3: arrows, Home/End, F1-F4
	//   ESC <char>       Alt-modified key (Alt+letter, Alt+digit, Alt+Enter, Alt+Backspace, ...)
	private static ConsoleInputEvent? TryReadEscapeSequence(IntPtr hIn)
	{
		char intro = PeekCharAt(hIn, 0);
		if (intro == '\0')
			return null;

		if (intro == '[')
			return TryReadCsi(hIn);
		if (intro == 'O')
			return TryReadSs3(hIn);

		// ESC followed by any other char is Alt + that key. Decode the char the same way a standalone
		// key record would be, then force the Alt modifier on.
		return ReadAltPrefixedKey(hIn, intro);
	}

	// SS3: ESC O <final>. Used for arrows/Home/End in application cursor mode and for F1-F4.
	private static ConsoleInputEvent? TryReadSs3(IntPtr hIn)
	{
		char       final = PeekCharAt(hIn, 1);
		ConsoleKey key   = VtKeys.FinalToKey(final);
		if (key == (ConsoleKey)0)
			return null;

		DrainChars(hIn, 2);
		return VtKeys.KeyEvent('\0', key, false, false, false);
	}

	// CSI: ESC [ <params> <final>. Collects numeric/';' params up to a small bound, then maps the final
	// byte (and any ';mod' modifier) to a ConsoleKey.
	private static ConsoleInputEvent? TryReadCsi(IntPtr hIn)
	{
		char first = PeekCharAt(hIn, 1);

		// Mouse reports can still arrive as CSI sequences in some hosts even after we disable VT mouse
		// modes (e.g. events already queued). Recognize and consume them so they never leak as input.
		//   SGR (1006):  ESC [ < Cb ; Cx ; Cy M|m
		//   Normal/X10:  ESC [ M Cb Cx Cy   (three raw bytes after M)
		if (first == '<')
			return ConsumeSgrMouse(hIn);
		if (first == 'M')
			return ConsumeNormalMouse(hIn);

		StringBuilder param = new StringBuilder();
		int           idx   = 1;
		char          final = '\0';
		const int maxLen = 16;

		while (idx < maxLen)
		{
			char c = PeekCharAt(hIn, idx);
			if (c == '\0')
				return null; // sequence not fully present yet — leave it, treat ESC as Escape
			if (c >= '@' && c <= '~' && !(c >= '0' && c <= '9') && c != ';')
			{
				final = c;
				break;
			}
			param.Append(c);
			idx++;
		}

		if (final == '\0')
			return null;

		ConsoleInputEvent? ev = VtKeys.CsiToEvent(param.ToString(), final);
		if (ev == null)
			return null;

		DrainChars(hIn, idx + 1); // params plus the final byte (idx is the final's offset)
		return ev;
	}

	// SGR mouse report: ESC [ < Cb ; Cx ; Cy (M=press, m=release). Cx/Cy are 1-based columns/rows.
	// Decodes wheel/click/move into the corresponding ConsoleInputEvent; motion-only reports return
	// MouseMove. Returns null (consuming nothing) if the sequence is not yet fully present.
	private static ConsoleInputEvent? ConsumeSgrMouse(IntPtr hIn)
	{
		StringBuilder body       = new StringBuilder();
		int           idx        = 2; // skip '[' and '<'
		char          terminator = '\0';
		const int maxLen = 24;

		while (idx < maxLen)
		{
			char c = PeekCharAt(hIn, idx);
			if (c == '\0')
				return null; // not fully present yet
			if (c == 'M' || c == 'm')
			{
				terminator = c;
				break;
			}
			body.Append(c);
			idx++;
		}

		if (terminator == '\0')
			return null;

		DrainChars(hIn, idx + 1); // '[' '<' params and the M/m terminator

		// Null when malformed: consumed either way, so a bad report is swallowed rather than leaked.
		return VtKeys.DecodeSgrMouse(body.ToString(), terminator);
	}

	// Normal/X10 mouse report: ESC [ M Cb Cx Cy, where Cb/Cx/Cy are single bytes biased by 32.
	// Decoded the same way as the SGR form (press-only protocol).
	private static ConsoleInputEvent? ConsumeNormalMouse(IntPtr hIn)
	{
		char b1 = PeekCharAt(hIn, 2);
		char b2 = PeekCharAt(hIn, 3);
		char b3 = PeekCharAt(hIn, 4);
		if (b1 == '\0' || b2 == '\0' || b3 == '\0')
			return null; // not fully present yet

		DrainChars(hIn, 5); // '[' 'M' and the three encoded bytes
		return VtKeys.DecodeX10Mouse(b1, b2, b3);
	}

	// ESC + <char>: the char is the next record. Translate it as if it stood alone, then add Alt.
	private static ConsoleInputEvent? ReadAltPrefixedKey(IntPtr hIn, char c)
	{
		DrainChars(hIn, 1);
		return VtKeys.AltKey(c);
	}

	// Returns the UnicodeChar of the key-down record at the given offset in the peek queue, or '\0' if
	// there is no such record (or it is not a key-down char record).
	private static char PeekCharAt(IntPtr hIn, int offset)
	{
		INPUT_RECORD[] peek = new INPUT_RECORD[offset + 1];
		if (!PeekConsoleInputW(hIn, peek, (uint)(offset + 1), out uint peeked) || peeked <= offset)
			return '\0';
		if (peek[offset].EventType != KEY_EVENT_TYPE)
			return '\0';
		KEY_EVENT_RECORD k = peek[offset].KeyEvent;
		if (k.bKeyDown == 0)
			return '\0';
		return k.UnicodeChar;
	}

	private static void DrainChars(IntPtr hIn, int count)
	{
		INPUT_RECORD[] buf = new INPUT_RECORD[count];
		ReadConsoleInputW(hIn, buf, (uint)count, out _);
	}

	// Drains any pending records from the console input buffer.
	internal static void DiscardPendingInput()
	{
		const int maxFlush = 256;
		IntPtr         hIn = GetStdHandle(StdInputHandle);
		INPUT_RECORD[] buf = new INPUT_RECORD[maxFlush];
		while (PeekConsoleInputW(hIn, buf, maxFlush, out uint peeked) && peeked > 0)
			ReadConsoleInputW(hIn, buf, peeked, out _);
	}

	// Reads one CPR response (ESC [ <row> ; <col> R) from the console input buffer and returns its
	// column, or -1 on timeout. Requires ENABLE_VIRTUAL_TERMINAL_INPUT so the response arrives as
	// key-event records (EnableVirtualTerminal sets it before probing runs).
	internal static int ReadCursorColumn(long deadline)
	{
		IntPtr        hIn  = GetStdHandle(StdInputHandle);
		StringBuilder sb   = new StringBuilder(16);
		int           col  = -1;
		bool          done = false;
		while (!done && Environment.TickCount64 < deadline)
		{
			uint result = WaitForSingleObject(hIn, (uint)Math.Max(1, deadline - Environment.TickCount64));
			if (result != WAIT_OBJECT_0)
				break; // timeout

			INPUT_RECORD[] buf = new INPUT_RECORD[1];
			if (!ReadConsoleInputW(hIn, buf, 1, out uint read) || read == 0)
				continue;
			if (buf[0].EventType != KEY_EVENT_TYPE || buf[0].KeyEvent.bKeyDown == 0)
				continue;

			char ch = buf[0].KeyEvent.UnicodeChar;
			if (ch == 'R')
			{
				col  = VtKeys.ParseCursorColumn(sb.ToString());
				done = true;
			}
			else
			{
				sb.Append(ch);
			}
		}
		return col;
	}

	internal static void Restore()
	{
		if (!_saved)
			return;
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return;

		Console.Out.Write(DisableBracketedPaste);
		Console.Out.Flush();

		IntPtr hOut = GetStdHandle(StdOutputHandle);
		IntPtr hIn  = GetStdHandle(StdInputHandle);
		SetConsoleMode(hOut, _originalOutputMode);
		SetConsoleMode( hIn,  _originalInputMode);
	}
}