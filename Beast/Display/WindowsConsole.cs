using System;
using System.Runtime.InteropServices;
using System.Threading;


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
