using System;
using System.Text;


// Decoding shared by every platform's input reader: once the characters of a VT sequence (a control char,
// a CSI/SS3 key, an Alt prefix, an SGR or X10 mouse report, a paste body) have been read, turning them into
// the ConsoleInputEvent shape DisplayScreen expects is the same everywhere. Only how the characters are read
// differs (Win32 console records on Windows, raw stdin bytes on macOS/Linux).
internal static class VtKeys
{
	internal static ConsoleInputEvent KeyEvent(char keyChar, ConsoleKey key, bool shift, bool alt, bool ctrl)
	{
		return new ConsoleInputEvent { Type = InputEventType.Key, Key = new ConsoleKeyInfo(keyChar, key, shift, alt, ctrl) };
	}

	// Translates a control character delivered under VT input into the ConsoleKey/modifier shape the
	// consumer expects. Returns null for non-control characters so printable input falls through.
	internal static ConsoleInputEvent? TranslateControlChar(char c, bool shift, bool alt, bool ctrl)
	{
		// Named editing keys. These overlap with Ctrl-I/M/H but the terminal meaning is the named key.
		if (c == '\t')
			return KeyEvent('\t', ConsoleKey.Tab, shift, alt, ctrl);
		if (c == '\r' || c == '\n')
			return KeyEvent('\r', ConsoleKey.Enter, shift, alt, ctrl);
		if (c == '\b' || c == '\x7f')
			return KeyEvent('\b', ConsoleKey.Backspace, shift, alt, ctrl);

		// Remaining control chars 0x01-0x1A are Ctrl + a letter (A=0x01 .. Z=0x1A). Surface them as the
		// letter key with the Control modifier so DisplayScreen's Ctrl-A/E/V/X/C/D/O/J handlers fire.
		// The KeyChar stays the control char so any consumer that inspects it sees the real value.
		if (c >= '\x01' && c <= '\x1a')
		{
			ConsoleKey letter = (ConsoleKey)('A' + (c - 1));
			return KeyEvent(c, letter, shift, alt, true);
		}

		return null;
	}

	// A printable character typed on its own. Windows supplies the virtual key alongside the char; a raw
	// byte stream does not, so the ConsoleKey the handlers match on is derived from the char itself.
	internal static ConsoleInputEvent PrintableKey(char c)
	{
		ConsoleKey key   = (ConsoleKey)0;
		bool       shift = false;
		if (c >= 'a' && c <= 'z')
		{
			key = (ConsoleKey)('A' + (c - 'a'));
		}
		else if (c >= 'A' && c <= 'Z')
		{
			key   = (ConsoleKey)c;
			shift = true;
		}
		else if (c >= '0' && c <= '9')
		{
			key = (ConsoleKey)c;
		}
		else if (c == ' ')
		{
			key = ConsoleKey.Spacebar;
		}
		else if (c == '-' || c == '_')
		{
			key   = ConsoleKey.OemMinus;
			shift = c == '_';
		}
		else if (c == '=' || c == '+')
		{
			key   = ConsoleKey.OemPlus;
			shift = c == '+';
		}
		return KeyEvent(c, key, shift, false, false);
	}

	// ESC + <char>: the char translated as if it stood alone, with Alt added. Letters map to their
	// ConsoleKey; other printable chars keep only the char.
	internal static ConsoleInputEvent AltKey(char c)
	{
		ConsoleInputEvent? ctl = TranslateControlChar(c, false, true, false);
		if (ctl != null)
			return ctl.Value;

		ConsoleKey key = (ConsoleKey)0;
		if (c >= 'a' && c <= 'z')
			key = (ConsoleKey)('A' + (c - 'a'));
		else if (c >= 'A' && c <= 'Z')
			key = (ConsoleKey)c;
		else if (c >= '0' && c <= '9')
			key = (ConsoleKey)c;

		return KeyEvent(c, key, false, true, false);
	}

	// A complete CSI key sequence (ESC [ <params> <final>, the ESC and '[' already consumed) to its key
	// event, or null when the final byte names no key. A ';mod' parameter carries Shift/Alt/Ctrl.
	internal static ConsoleInputEvent? CsiToEvent(string paramStr, char final)
	{
		bool shift = false, alt = false, ctrl = false;
		int  semi  = paramStr.IndexOf(';');
		if (semi >= 0 && int.TryParse(paramStr.Substring(semi + 1), out int mod))
		{
			int m = mod - 1;
			shift = (m & 1) != 0;
			alt   = (m & 2) != 0;
			ctrl  = (m & 4) != 0;
		}

		// ESC [ Z is Shift+Tab (back-tab).
		if (final == 'Z')
			return KeyEvent('\t', ConsoleKey.Tab, true, alt, ctrl);

		ConsoleKey key;
		if (final == '~')
			key = TildeCodeToKey(ParseLeadingInt(paramStr));
		else
			key = FinalToKey(final);

		if (key == (ConsoleKey)0)
			return null;
		return KeyEvent('\0', key, shift, alt, ctrl);
	}

	// Final byte of a CSI/SS3 sequence (non-tilde) to ConsoleKey.
	internal static ConsoleKey FinalToKey(char final)
	{
		return final switch
		{
			'A' => ConsoleKey.UpArrow,
			'B' => ConsoleKey.DownArrow,
			'C' => ConsoleKey.RightArrow,
			'D' => ConsoleKey.LeftArrow,
			'H' => ConsoleKey.Home,
			'F' => ConsoleKey.End,
			'P' => ConsoleKey.F1,
			'Q' => ConsoleKey.F2,
			'R' => ConsoleKey.F3,
			'S' => ConsoleKey.F4,
			_   => (ConsoleKey)0,
		};
	}

	// Numeric parameter of a 'ESC [ <n> ~' sequence to ConsoleKey.
	private static ConsoleKey TildeCodeToKey(int code)
	{
		return code switch
		{
			1 or 7 => ConsoleKey.Home,
			2      => ConsoleKey.Insert,
			3      => ConsoleKey.Delete,
			4 or 8 => ConsoleKey.End,
			5      => ConsoleKey.PageUp,
			6      => ConsoleKey.PageDown,
			11     => ConsoleKey.F1,
			12     => ConsoleKey.F2,
			13     => ConsoleKey.F3,
			14     => ConsoleKey.F4,
			15     => ConsoleKey.F5,
			17     => ConsoleKey.F6,
			18     => ConsoleKey.F7,
			19     => ConsoleKey.F8,
			20     => ConsoleKey.F9,
			21     => ConsoleKey.F10,
			23     => ConsoleKey.F11,
			24     => ConsoleKey.F12,
			_      => (ConsoleKey)0,
		};
	}

	// SGR mouse report body (the "Cb;Cx;Cy" between ESC [ < and the M/m terminator; M=press, m=release).
	// Cx/Cy are 1-based columns/rows. Returns null when malformed (the caller has already consumed it).
	internal static ConsoleInputEvent? DecodeSgrMouse(string body, char terminator)
	{
		string[] parts = body.Split(';');
		if (parts.Length != 3
			|| !int.TryParse(parts[0], out int cb)
			|| !int.TryParse(parts[1], out int cx)
			|| !int.TryParse(parts[2], out int cy))
			return null;

		return DecodeMouse(cb, cx - 1, cy - 1, terminator == 'M');
	}

	// Normal/X10 mouse report: ESC [ M Cb Cx Cy, where Cb/Cx/Cy are single bytes biased by 32. Press-only
	// protocol: a release arrives as button 3.
	internal static ConsoleInputEvent DecodeX10Mouse(char b1, char b2, char b3)
	{
		return DecodeMouse(b1 - 32, b2 - 32 - 1, b3 - 32 - 1, true);
	}

	// Decodes a mouse button byte into a wheel, click, or move event. Wheel and motion flags come first;
	// a press of the left or right button is a click; everything else (releases, middle button) is a move.
	private static ConsoleInputEvent DecodeMouse(int cb, int col, int row, bool press)
	{
		bool shift = (cb & 0x04) != 0;
		int  btn   = cb & 0x03;

		if ((cb & 0x40) != 0) // wheel
		{
			// Low two bits select the wheel axis/direction: 0=up, 1=down, 2=left, 3=right. Without this the
			// horizontal (trackpad/tilt) deltas fold onto the vertical axis and scroll the view up/down.
			bool  horizontal = btn == 2 || btn == 3;
			short delta      = horizontal
				? (short)(btn == 3 ? 120 : -120)  // right vs left
				: (short)(btn == 0 ? 120 : -120); // up vs down
			return new ConsoleInputEvent { Type = InputEventType.MouseWheel, Col = col, Row = row, WheelDelta = delta, Horizontal = horizontal, Shift = shift };
		}

		if ((cb & 0x20) != 0) // motion
			return new ConsoleInputEvent { Type = InputEventType.MouseMove, Col = col, Row = row };

		if (press && (btn == 0 || btn == 2)) // left or right button press
			return new ConsoleInputEvent { Type = InputEventType.MouseClick, Col = col, Row = row, Shift = shift, Right = btn == 2 };

		return new ConsoleInputEvent { Type = InputEventType.MouseMove, Col = col, Row = row };
	}

	internal static int ParseLeadingInt(string s)
	{
		int i = 0;
		while (i < s.Length && char.IsDigit(s[i]))
			i++;
		return i > 0 && int.TryParse(s.Substring(0, i), out int v) ? v : -1;
	}

	// Appends one pasted character, normalizing '\r' and '\r\n' line endings to '\n'.
	internal static void AppendPasteChar(StringBuilder sb, char c)
	{
		if (c == '\r')
		{
			sb.Append('\n');
		}
		else if (c == '\n')
		{
			// Collapse '\r\n' into the single '\n' already appended.
			if (sb.Length == 0 || sb[sb.Length - 1] != '\n')
				sb.Append('\n');
		}
		else
		{
			sb.Append(c);
		}
	}

	// Parses the column from an accumulated cursor position report ("\x1b[<row>;<col>", terminator consumed):
	// the number after the last ';', or -1 when there is none.
	internal static int ParseCursorColumn(string resp)
	{
		int col  = -1;
		int semi = resp.LastIndexOf(';');
		if (semi >= 0 && !int.TryParse(resp.Substring(semi + 1), out col))
			col = -1;
		return col;
	}
}