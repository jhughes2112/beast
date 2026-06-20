using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;


internal enum InputEventType { Key, MouseClick, MouseWheel, MouseMove, Paste }

internal struct ConsoleInputEvent
{
    internal InputEventType Type;
    internal ConsoleKeyInfo Key;
    internal int    Col;
    internal int    Row;
    internal short  WheelDelta;  // positive = wheel up, negative = wheel down
    internal bool   Shift;       // Shift held during a MouseClick (used for shift-click = add-to-clipboard)
    internal string Text;        // full text for a coalesced paste burst (newlines preserved)
}

// Manages Windows console mode and provides a unified ReadInputWithTimeout that surfaces
// both KEY_EVENT and MOUSE_EVENT records via ReadConsoleInput. Console.ReadKey discards
// mouse events, so we call ReadConsoleInput directly.
internal static class WindowsConsole
{
    private const int StdInputHandle  = -10;
    private const int StdOutputHandle = -11;

    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint EnableVirtualTerminalInput      = 0x0200;
    private const uint DisableQuickEditMode            = 0x0040;
    private const uint EnableExtendedFlags             = 0x0080;

    private const uint   WAIT_OBJECT_0  = 0x00000000;
    private const ushort KEY_EVENT_TYPE   = 0x0001;
    private const ushort MOUSE_EVENT_TYPE = 0x0002;
    private const uint   MOUSE_WHEELED  = 0x0004;
    private const uint   LEFT_BUTTON    = 0x0001;

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
    private const string DisableVtMouseModes = "\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l";
    private static readonly char[] PasteStartMarker = new char[] { '[', '2', '0', '0', '~' };
    private static readonly char[] PasteEndMarker   = new char[] { '\x1b', '[', '2', '0', '1', '~' };

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
        // Enable VT input so bracketed-paste markers are delivered to us, and disable Quick Edit so
        // mouse clicks are not swallowed for text selection.
        SetConsoleMode(hIn, (inMode | EnableExtendedFlags | EnableVirtualTerminalInput) & ~DisableQuickEditMode);

        Console.Out.Write(DisableVtMouseModes);
        Console.Out.Write(EnableBracketedPaste);
        Console.Out.Flush();
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
        SetConsoleMode(hIn, (inMode | EnableExtendedFlags | EnableVirtualTerminalInput) & ~DisableQuickEditMode);

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
                if (vt != null) return vt;

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
            ConsoleInputEvent? ctl = TryTranslateControlChar(k.UnicodeChar, shift, alt, ctrl);
            if (ctl != null) return ctl;

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
                bool clickShift = (m.dwControlKeyState & 0x0010u) != 0;
                return new ConsoleInputEvent { Type = InputEventType.MouseClick, Col = m.MouseX, Row = m.MouseY, Shift = clickShift };
            }

            if ((m.dwEventFlags & 0x0001u) != 0)  // MOUSE_MOVED
            {
                return new ConsoleInputEvent { Type = InputEventType.MouseMove, Col = m.MouseX, Row = m.MouseY };
            }
        }

        return null;
    }

    // Translates a control character delivered under VT input into the ConsoleKey/modifier shape the
    // consumer expects. Returns null for non-control characters so printable input falls through.
    private static ConsoleInputEvent? TryTranslateControlChar(char c, bool shift, bool alt, bool ctrl)
    {
        // Named editing keys. These overlap with Ctrl-I/M/H but the terminal meaning is the named key.
        if (c == '\t') return KeyEvent('\t', ConsoleKey.Tab, shift, alt, ctrl);
        if (c == '\r' || c == '\n') return KeyEvent('\r', ConsoleKey.Enter, shift, alt, ctrl);
        if (c == '\b' || c == '\x7f') return KeyEvent('\b', ConsoleKey.Backspace, shift, alt, ctrl);

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

    // Peeks the next records (without consuming) to see whether they are key char records spelling out
    // the given marker characters in order. Used to confirm an ESC begins a bracketed-paste sequence.
    private static bool PeekMatches(IntPtr hIn, char[] marker)
    {
        INPUT_RECORD[] peek = new INPUT_RECORD[marker.Length];
        if (!PeekConsoleInputW(hIn, peek, (uint)marker.Length, out uint peeked) || peeked < marker.Length)
            return false;

        for (int i = 0; i < marker.Length; i++)
        {
            if (peek[i].EventType != KEY_EVENT_TYPE) return false;
            KEY_EVENT_RECORD k = peek[i].KeyEvent;
            if (k.bKeyDown == 0) return false;
            if (k.UnicodeChar != marker[i]) return false;
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
        StringBuilder sb = new StringBuilder();
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
                if (!PeekHasInput(hIn)) break;
            }

            if (!ReadConsoleInputW(hIn, one, 1, out uint read) || read == 0) break;
            if (one[0].EventType != KEY_EVENT_TYPE) continue;

            KEY_EVENT_RECORD k = one[0].KeyEvent;
            if (k.bKeyDown == 0) continue;
            if (k.UnicodeChar == '\0') continue;

            AppendPasteChar(sb, k.UnicodeChar);
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
        if (intro == '\0') return null;

        if (intro == '[') return TryReadCsi(hIn);
        if (intro == 'O') return TryReadSs3(hIn);

        // ESC followed by any other char is Alt + that key. Decode the char the same way a standalone
        // key record would be, then force the Alt modifier on.
        return ReadAltPrefixedKey(hIn, intro);
    }

    // SS3: ESC O <final>. Used for arrows/Home/End in application cursor mode and for F1-F4.
    private static ConsoleInputEvent? TryReadSs3(IntPtr hIn)
    {
        char final = PeekCharAt(hIn, 1);
        ConsoleKey key = FinalToKey(final);
        if (key == (ConsoleKey)0) return null;

        DrainChars(hIn, 2);
        return KeyEvent('\0', key, false, false, false);
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
        if (first == '<') return ConsumeSgrMouse(hIn);
        if (first == 'M') return ConsumeNormalMouse(hIn);

        StringBuilder param = new StringBuilder();
        int idx = 1;
        char final = '\0';
        const int maxLen = 16;

        while (idx < maxLen)
        {
            char c = PeekCharAt(hIn, idx);
            if (c == '\0') return null;  // sequence not fully present yet — leave it, treat ESC as Escape
            if (c >= '@' && c <= '~' && !(c >= '0' && c <= '9') && c != ';')
            {
                final = c;
                break;
            }
            param.Append(c);
            idx++;
        }

        if (final == '\0') return null;

        string paramStr = param.ToString();

        bool shift = false, alt = false, ctrl = false;
        int semi = paramStr.IndexOf(';');
        if (semi >= 0 && int.TryParse(paramStr.Substring(semi + 1), out int mod))
        {
            int m = mod - 1;
            shift = (m & 1) != 0;
            alt   = (m & 2) != 0;
            ctrl  = (m & 4) != 0;
        }

        ConsoleKey key;
        if (final == '~')
            key = TildeCodeToKey(ParseLeadingInt(paramStr));
        else
            key = FinalToKey(final);

        if (key == (ConsoleKey)0) return null;

        DrainChars(hIn, idx + 1);  // params plus the final byte (idx is the final's offset)
        return KeyEvent('\0', key, shift, alt, ctrl);
    }

    // SGR mouse report: ESC [ < Cb ; Cx ; Cy (M=press, m=release). Cx/Cy are 1-based columns/rows.
    // Decodes wheel/click/move into the corresponding ConsoleInputEvent; motion-only reports return
    // MouseMove. Returns null (consuming nothing) if the sequence is not yet fully present.
    private static ConsoleInputEvent? ConsumeSgrMouse(IntPtr hIn)
    {
        StringBuilder body = new StringBuilder();
        int idx = 2;  // skip '[' and '<'
        char terminator = '\0';
        const int maxLen = 24;

        while (idx < maxLen)
        {
            char c = PeekCharAt(hIn, idx);
            if (c == '\0') return null;  // not fully present yet
            if (c == 'M' || c == 'm')
            {
                terminator = c;
                break;
            }
            body.Append(c);
            idx++;
        }

        if (terminator == '\0') return null;

        DrainChars(hIn, idx + 1);  // '[' '<' params and the M/m terminator

        string[] parts = body.ToString().Split(';');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out int cb)
            || !int.TryParse(parts[1], out int cx)
            || !int.TryParse(parts[2], out int cy))
            return null;  // malformed but consumed — swallow it

        int col = cx - 1;
        int row = cy - 1;
        bool press = terminator == 'M';

        if ((cb & 0x40) != 0)  // wheel
        {
            short delta = (short)((cb & 0x01) == 0 ? 120 : -120);  // bit0: 0=up, 1=down
            return new ConsoleInputEvent { Type = InputEventType.MouseWheel, Col = col, Row = row, WheelDelta = delta };
        }

        if ((cb & 0x20) != 0)  // motion
            return new ConsoleInputEvent { Type = InputEventType.MouseMove, Col = col, Row = row };

        if (press && (cb & 0x03) == 0)  // left button press
            return new ConsoleInputEvent { Type = InputEventType.MouseClick, Col = col, Row = row, Shift = (cb & 0x04) != 0 };

        return new ConsoleInputEvent { Type = InputEventType.MouseMove, Col = col, Row = row };
    }

    // Normal/X10 mouse report: ESC [ M Cb Cx Cy, where Cb/Cx/Cy are single bytes biased by 32.
    // Decoded the same way as the SGR form (press-only protocol).
    private static ConsoleInputEvent? ConsumeNormalMouse(IntPtr hIn)
    {
        char b1 = PeekCharAt(hIn, 2);
        char b2 = PeekCharAt(hIn, 3);
        char b3 = PeekCharAt(hIn, 4);
        if (b1 == '\0' || b2 == '\0' || b3 == '\0') return null;  // not fully present yet

        DrainChars(hIn, 5);  // '[' 'M' and the three encoded bytes

        int cb  = b1 - 32;
        int col = b2 - 32 - 1;
        int row = b3 - 32 - 1;

        if ((cb & 0x40) != 0)
        {
            short delta = (short)((cb & 0x01) == 0 ? 120 : -120);
            return new ConsoleInputEvent { Type = InputEventType.MouseWheel, Col = col, Row = row, WheelDelta = delta };
        }

        if ((cb & 0x20) != 0)
            return new ConsoleInputEvent { Type = InputEventType.MouseMove, Col = col, Row = row };

        if ((cb & 0x03) == 0)
            return new ConsoleInputEvent { Type = InputEventType.MouseClick, Col = col, Row = row, Shift = (cb & 0x04) != 0 };

        return new ConsoleInputEvent { Type = InputEventType.MouseMove, Col = col, Row = row };
    }

    // ESC + <char>: the char is the next record. Translate it as if it stood alone, then add Alt.
    private static ConsoleInputEvent? ReadAltPrefixedKey(IntPtr hIn, char c)
    {
        ConsoleInputEvent? ctl = TryTranslateControlChar(c, false, true, false);
        if (ctl != null)
        {
            DrainChars(hIn, 1);
            return ctl;
        }

        // Printable char: Alt + that character. Map letters to their ConsoleKey, leave others as the char.
        ConsoleKey key = (ConsoleKey)0;
        if (c >= 'a' && c <= 'z') key = (ConsoleKey)('A' + (c - 'a'));
        else if (c >= 'A' && c <= 'Z') key = (ConsoleKey)c;
        else if (c >= '0' && c <= '9') key = (ConsoleKey)c;

        DrainChars(hIn, 1);
        return KeyEvent(c, key, false, true, false);
    }

    // Final byte of a CSI/SS3 sequence (non-tilde) to ConsoleKey.
    private static ConsoleKey FinalToKey(char final)
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

    private static ConsoleInputEvent KeyEvent(char keyChar, ConsoleKey key, bool shift, bool alt, bool ctrl)
    {
        return new ConsoleInputEvent { Type = InputEventType.Key, Key = new ConsoleKeyInfo(keyChar, key, shift, alt, ctrl) };
    }

    // Returns the UnicodeChar of the key-down record at the given offset in the peek queue, or '\0' if
    // there is no such record (or it is not a key-down char record).
    private static char PeekCharAt(IntPtr hIn, int offset)
    {
        INPUT_RECORD[] peek = new INPUT_RECORD[offset + 1];
        if (!PeekConsoleInputW(hIn, peek, (uint)(offset + 1), out uint peeked) || peeked <= offset)
            return '\0';
        if (peek[offset].EventType != KEY_EVENT_TYPE) return '\0';
        KEY_EVENT_RECORD k = peek[offset].KeyEvent;
        if (k.bKeyDown == 0) return '\0';
        return k.UnicodeChar;
    }

    private static void DrainChars(IntPtr hIn, int count)
    {
        INPUT_RECORD[] buf = new INPUT_RECORD[count];
        ReadConsoleInputW(hIn, buf, (uint)count, out _);
    }

    private static int ParseLeadingInt(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        return i > 0 && int.TryParse(s.Substring(0, i), out int v) ? v : -1;
    }

    private static void AppendPasteChar(StringBuilder sb, char c)
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

    internal static void Restore()
    {
        if (!_saved) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        Console.Out.Write(DisableBracketedPaste);
        Console.Out.Flush();

        IntPtr hOut = GetStdHandle(StdOutputHandle);
        IntPtr hIn  = GetStdHandle(StdInputHandle);
        SetConsoleMode(hOut, _originalOutputMode);
        SetConsoleMode(hIn,  _originalInputMode);
    }
}
