using System;
using System.Collections.Generic;


internal enum InputEventType { Key, MouseClick, MouseWheel, MouseMove, Paste }

internal struct ConsoleInputEvent
{
	internal InputEventType Type;
	internal ConsoleKeyInfo Key;
	internal int            Col;
	internal int            Row;
	internal short          WheelDelta; // wheel: positive = up / right, negative = down / left
	internal bool           Shift;      // Shift held during a MouseClick or wheel (shift-click = add-to-clipboard; shift+wheel = pan)
	internal bool           Horizontal; // a horizontal wheel (tilt / trackpad) rather than the vertical wheel
	internal bool           Right;      // MouseClick: the right button rather than the left (right-click = paste)
	internal string         Text;       // full text for a coalesced paste burst (newlines preserved)
}

// The operations that differ between Windows and macOS/Linux: terminal input modes and reading, the
// clipboard, and notification sounds. Rendering is plain VT output and needs none of this, so it works the
// same everywhere, including over SSH.
internal interface IPlatform
{
	// Puts the terminal into the input modes Beast needs (raw keys, bracketed paste). Safe to call repeatedly.
	void EnterTerminalModes();

	// Re-asserts those modes after something else (a child process, a resumed job) may have changed them.
	void ReapplyTerminalModes();

	// Returns the terminal to the modes it had before EnterTerminalModes.
	void RestoreTerminalModes();

	// Blocks up to timeoutMs for the next input event; null on timeout or an ignored record.
	ConsoleInputEvent? ReadInput(int timeoutMs);

	// Drops input already queued, so a stale keystroke is not mistaken for the terminal's reply to a query.
	void DiscardPendingInput();

	// Reads the terminal's reply to a cursor position query (ESC [ 6 n) and returns its column, or -1 when
	// none arrives before deadline (an Environment.TickCount64 value).
	int ReadCursorColumn(long deadline);

	// Clipboard text, empty when there is none or it cannot be read.
	string GetClipboardText();

	void SetClipboardText(string text);

	// Files copied in the OS file manager, empty when there are none.
	List<string> GetClipboardFiles();

	// Saves a clipboard image into folder as a PNG and returns its path, or empty when there is none.
	string SaveClipboardImage(string folder);

	// Plays the first of candidates that exists on this machine; no-op when none does.
	void PlaySound(IReadOnlyList<string> candidates);
}

// The implementation for the OS beast is running on, chosen once at startup.
internal static class Platform
{
	internal static readonly IPlatform Current = OperatingSystem.IsWindows() ? new PlatformWindows() : new PlatformUnix();
}