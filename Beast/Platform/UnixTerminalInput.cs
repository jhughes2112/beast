using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;


// Raw terminal input for macOS and Linux, read straight from stdin so exactly the same escape sequences
// arrive whether the terminal is local or on the far side of SSH: keys (CSI/SS3 navigation, Alt prefixes),
// SGR mouse reports, and bracketed paste, all decoded by VtKeys like the Windows reader.
//
// Raw mode comes from the C library's termios calls. The termios struct differs between Linux and macOS, so
// it is handled as an opaque buffer that cfmakeraw fills in; the one field touched by hand is OPOST in
// c_oflag, put back so '\n' in output still returns the carriage. Console.ReadKey is not used: .NET's own
// key parsing drops the mouse and paste sequences, and its terminal handling would fight this one.
internal sealed class UnixTerminalInput
{
	private const int   StdinFd           = 0;
	private const int   TcsaNow           = 0;
	private const short PollIn            = 0x0001;
	private const short PollHup           = 0x0010;
	private const byte  OutputPostProcess = 0x01; // OPOST: the same bit on Linux and macOS

	// Larger than any platform's struct termios (Linux 60 bytes, macOS 72), so tcgetattr never overruns it.
	private const int TermiosBufferSize = 256;

	// Escape sequences normally arrive in one read, but over SSH a sequence can be split across packets;
	// this is how long a lone ESC waits for the rest before it counts as the Escape key.
	private const int EscapeWaitMs = 50;

	// A paste body with no data for this long is treated as complete even without its end marker.
	private const int PasteIdleMs = 500;

	private const string PasteStart = "\x1b[200~";
	private const string PasteEnd   = "\x1b[201~";

	[StructLayout(LayoutKind.Sequential)]
	private struct PollFd
	{
		public int   Fd;
		public short Events;
		public short Revents;
	}

	private delegate int TcGetAttrFn(int fd, [In, Out] byte[] termios);
	private delegate int TcSetAttrFn(int fd, int optionalActions, [In] byte[] termios);
	private delegate void CfMakeRawFn([In, Out] byte[] termios);
	private delegate int PollFn([In, Out] PollFd[] fds, nuint count, int timeoutMs);
	private delegate nint ReadFn(int fd, [Out] byte[] buffer, nint count);

	private readonly TcGetAttrFn? _tcgetattr;
	private readonly TcSetAttrFn? _tcsetattr;
	private readonly CfMakeRawFn? _cfmakeraw;
	private readonly PollFn?      _poll;
	private readonly ReadFn?      _read;

	private readonly byte[] _original = new byte[TermiosBufferSize];
	private readonly byte[] _raw      = new byte[TermiosBufferSize];
	private readonly byte[] _check    = new byte[TermiosBufferSize];
	private          bool   _saved;
	private          bool   _rawActive;
	private          bool   _endOfInput;

	// Decoded characters not yet turned into events, read from _head onward.
	private readonly List<char> _pending = new List<char>();
	private          int        _head;
	private readonly Decoder    _utf8       = new UTF8Encoding(false).GetDecoder();
	private readonly byte[]     _readBuffer = new byte[4096];
	private readonly char[]     _charBuffer = new char[4097];
	private readonly PollFd[]   _pollFds    = new PollFd[1];

	internal UnixTerminalInput()
	{
		IntPtr libc = LoadLibc();
		if (libc != IntPtr.Zero)
		{
			_tcgetattr = Function<TcGetAttrFn>(libc, "tcgetattr");
			_tcsetattr = Function<TcSetAttrFn>(libc, "tcsetattr");
			_cfmakeraw = Function<CfMakeRawFn>(libc, "cfmakeraw");
			_poll      = Function<PollFn>(libc, "poll");
			_read      = Function<ReadFn>(libc, "read");
		}
	}

	private bool Available => _tcgetattr != null && _tcsetattr != null && _cfmakeraw != null && _poll != null && _read != null;

	// The C library under a name this OS resolves: glibc and musl differ on Linux, libSystem on macOS.
	private static IntPtr LoadLibc()
	{
		string[] candidates = OperatingSystem.IsMacOS()
			? new string[] { "/usr/lib/libSystem.B.dylib", "libc" }
			: new string[] { "libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1", "libc" };

		IntPtr handle = IntPtr.Zero;
		foreach (string name in candidates)
		{
			if (NativeLibrary.TryLoad(name, out handle))
				break;
		}
		return handle;
	}

	private static T? Function<T>(IntPtr library, string name) where T : Delegate
	{
		T? fn = null;
		if (NativeLibrary.TryGetExport(library, name, out IntPtr address))
			fn = Marshal.GetDelegateForFunctionPointer<T>(address);
		return fn;
	}

	// Switches stdin to raw mode, saving the original settings on first use. A stdin that is not a terminal
	// (a pipe) is left alone and simply read as bytes.
	internal void EnterRawMode()
	{
		if (!Available)
			return;

		if (!_saved)
		{
			if (_tcgetattr!(StdinFd, _original) != 0)
				return;

			Array.Copy(_original, _raw, TermiosBufferSize);
			_cfmakeraw!(_raw);
			_raw[OutputFlagsOffset()] |= OutputPostProcess;
			_saved                     = true;

			// Whatever ends the process, the user's shell gets its terminal back.
			AppDomain.CurrentDomain.ProcessExit += (sender, e) => Restore();
		}

		_tcsetattr!(StdinFd, TcsaNow, _raw);

		// Keep the settings as the driver reports them back, so later comparisons are like for like.
		Array.Clear(_raw);
		_tcgetattr!(StdinFd, _raw);
		_rawActive = true;
	}

	// c_oflag follows c_iflag; tcflag_t is 4 bytes on Linux and 8 on macOS. Both targets are little-endian,
	// so the low byte (where OPOST lives) is at the field's offset either way.
	private static int OutputFlagsOffset()
	{
		return OperatingSystem.IsMacOS() ? 8 : 4;
	}

	// Puts back the settings saved by EnterRawMode.
	internal void Restore()
	{
		if (_saved && _rawActive)
		{
			_tcsetattr!(StdinFd, TcsaNow, _original);
			_rawActive = false;
		}
	}

	// Raw mode can be undone behind our back: .NET hands the terminal's original settings to a child process
	// that shares it, and a suspended/resumed job comes back cooked. Cheap enough to check before every read.
	private void ReassertRawMode()
	{
		if (!_rawActive)
			return;

		Array.Clear(_check);
		if (_tcgetattr!(StdinFd, _check) != 0)
			return;

		bool same = true;
		for (int i = 0; i < TermiosBufferSize && same; i++)
			same = _check[i] == _raw[i];
		if (!same)
			_tcsetattr!(StdinFd, TcsaNow, _raw);
	}

	// Blocks up to timeoutMs for the next input event. Returns null on timeout, and for a recognized
	// sequence that carries nothing to act on (it is consumed so it never leaks as typed text).
	internal ConsoleInputEvent? Read(int timeoutMs)
	{
		if (!Available)
			return ReadKeyFallback(timeoutMs);

		ReassertRawMode();
		if (Count == 0 && !Fill(timeoutMs))
			return null;
		if (Count == 0)
			return null;

		char c = _pending[_head];
		if (c == '\x1b')
			return ReadEscape();

		Drain(1);
		ConsoleInputEvent? ctl = VtKeys.TranslateControlChar(c, false, false, false);
		if (ctl != null)
			return ctl;
		return VtKeys.PrintableKey(c);
	}

	// Without the C library (an unusual libc), keys still work through Console.ReadKey; mouse and paste do not.
	private static ConsoleInputEvent? ReadKeyFallback(int timeoutMs)
	{
		for (int i = 0; i < timeoutMs && !Console.KeyAvailable; i++)
			Thread.Sleep(1);
		if (!Console.KeyAvailable)
			return null;
		return new ConsoleInputEvent { Type = InputEventType.Key, Key = Console.ReadKey(true) };
	}

	private int Count => _pending.Count - _head;

	// Drops everything already read or waiting in the terminal, without blocking.
	internal void DiscardPending()
	{
		_pending.Clear();
		_head = 0;
		while (Available && Fill(0))
		{
			_pending.Clear();
			_head = 0;
		}
	}

	// Reads one cursor position report (ESC [ <row> ; <col> R) and returns its column, or -1 when it has not
	// arrived by deadline. Over SSH the reply makes a round trip to the user's terminal, so it can take a while.
	internal int ReadCursorColumn(long deadline)
	{
		StringBuilder sb   = new StringBuilder(16);
		int           col  = -1;
		bool          done = false;
		while (!done)
		{
			long remaining = deadline - Environment.TickCount64;
			if (Count == 0 && (!Available || remaining <= 0 || !Fill((int)remaining)))
			{
				done = true;
			}
			else if (Count > 0)
			{
				char c = _pending[_head];
				Drain(1);
				if (c == 'R')
				{
					col  = VtKeys.ParseCursorColumn(sb.ToString());
					done = true;
				}
				else
				{
					sb.Append(c);
				}
			}
		}
		return col;
	}

	// Decodes the ESC at the head: a bracketed paste, a CSI or SS3 sequence, Alt + a key, or Escape itself.
	private ConsoleInputEvent? ReadEscape()
	{
		if (MatchesAt(0, PasteStart))
		{
			Drain(PasteStart.Length);
			return new ConsoleInputEvent { Type = InputEventType.Paste, Text = ReadPasteBody() };
		}

		char intro = PeekAt(1, EscapeWaitMs);
		if (intro == '\0')
		{
			Drain(1);
			return VtKeys.KeyEvent('\x1b', ConsoleKey.Escape, false, false, false);
		}
		if (intro == '[')
			return ReadCsi();
		if (intro == 'O')
			return ReadSs3();

		Drain(2);
		return VtKeys.AltKey(intro);
	}

	// CSI: ESC [ ... — an SGR or X10 mouse report, or a key with optional ';mod' parameter. An incomplete
	// sequence leaves its ESC to stand alone as the Escape key, as on Windows.
	private ConsoleInputEvent? ReadCsi()
	{
		char first = PeekAt(2, EscapeWaitMs);
		if (first == '<')
			return ReadSgrMouse();
		if (first == 'M')
			return ReadX10Mouse();

		StringBuilder param = new StringBuilder();
		int           idx   = 2;
		char          final = '\0';
		const int maxLen = 18;
		while (idx < maxLen && final == '\0')
		{
			char c = PeekAt(idx, EscapeWaitMs);
			if (c == '\0')
				break;
			if (c >= '@' && c <= '~' && !(c >= '0' && c <= '9') && c != ';')
				final = c;
			else
			{
				param.Append(c);
				idx++;
			}
		}

		if (final == '\0')
		{
			Drain(1);
			return VtKeys.KeyEvent('\x1b', ConsoleKey.Escape, false, false, false);
		}

		// Unknown finals (focus reports and the like) are swallowed rather than typed into the input.
		Drain(idx + 1);
		return VtKeys.CsiToEvent(param.ToString(), final);
	}

	// SGR mouse: ESC [ < Cb ; Cx ; Cy M|m.
	private ConsoleInputEvent? ReadSgrMouse()
	{
		StringBuilder body       = new StringBuilder();
		int           idx        = 3;
		char          terminator = '\0';
		const int maxLen = 32;
		while (idx < maxLen && terminator == '\0')
		{
			char c = PeekAt(idx, EscapeWaitMs);
			if (c == '\0')
				break;
			if (c == 'M' || c == 'm')
				terminator = c;
			else
			{
				body.Append(c);
				idx++;
			}
		}

		if (terminator == '\0')
		{
			Drain(1);
			return VtKeys.KeyEvent('\x1b', ConsoleKey.Escape, false, false, false);
		}

		Drain(idx + 1);
		return VtKeys.DecodeSgrMouse(body.ToString(), terminator);
	}

	// Normal/X10 mouse: ESC [ M Cb Cx Cy, three raw bytes biased by 32.
	private ConsoleInputEvent? ReadX10Mouse()
	{
		char b1 = PeekAt(3, EscapeWaitMs);
		char b2 = PeekAt(4, EscapeWaitMs);
		char b3 = PeekAt(5, EscapeWaitMs);
		if (b1 == '\0' || b2 == '\0' || b3 == '\0')
		{
			Drain(1);
			return VtKeys.KeyEvent('\x1b', ConsoleKey.Escape, false, false, false);
		}

		Drain(6);
		return VtKeys.DecodeX10Mouse(b1, b2, b3);
	}

	// SS3: ESC O <final> — arrows/Home/End in application cursor mode and F1-F4.
	private ConsoleInputEvent? ReadSs3()
	{
		char final = PeekAt(2, EscapeWaitMs);
		if (final == '\0')
		{
			Drain(1);
			return VtKeys.KeyEvent('\x1b', ConsoleKey.Escape, false, false, false);
		}

		Drain(3);
		ConsoleKey key = VtKeys.FinalToKey(final);
		if (key == (ConsoleKey)0)
			return null;
		return VtKeys.KeyEvent('\0', key, false, false, false);
	}

	// Reads a paste body up to and including ESC [ 201 ~, normalizing line endings.
	private string ReadPasteBody()
	{
		StringBuilder sb   = new StringBuilder();
		bool          done = false;
		while (!done)
		{
			if (Count == 0 && !Fill(PasteIdleMs))
			{
				// No more data arrived; treat the paste as complete rather than hang on a lost terminator.
				done = true;
			}
			else if (Count > 0)
			{
				char c = _pending[_head];
				if (c == '\x1b' && MatchesAt(0, PasteEnd))
				{
					Drain(PasteEnd.Length);
					done = true;
				}
				else
				{
					Drain(1);
					VtKeys.AppendPasteChar(sb, c);
				}
			}
		}
		return sb.ToString();
	}

	// True when text follows at offset, waiting briefly for characters still in flight.
	private bool MatchesAt(int offset, string text)
	{
		bool match = true;
		for (int i = 0; i < text.Length && match; i++)
			match = PeekAt(offset + i, EscapeWaitMs) == text[i];
		return match;
	}

	// The pending char at offset, reading more input for up to waitMs if it has not arrived; '\0' if it never does.
	private char PeekAt(int offset, int waitMs)
	{
		long deadline = Environment.TickCount64 + waitMs;
		while (Count <= offset)
		{
			long remaining = deadline - Environment.TickCount64;
			if (remaining <= 0 || !Fill((int)remaining))
				break;
		}
		return Count > offset ? _pending[_head + offset] : '\0';
	}

	private void Drain(int count)
	{
		_head = Math.Min(_pending.Count, _head + count);
	}

	// Waits up to timeoutMs for stdin to become readable and decodes whatever arrived. False on timeout or
	// end of input (stdin closed: the wait is still honored so callers do not spin).
	private bool Fill(int timeoutMs)
	{
		if (_endOfInput)
		{
			Thread.Sleep(Math.Max(0, timeoutMs));
			return false;
		}

		_pollFds[0] = new PollFd { Fd = StdinFd, Events = PollIn, Revents = 0 };
		int ready   = _poll!(_pollFds, 1, timeoutMs);
		if (ready <= 0 || (_pollFds[0].Revents & (PollIn | PollHup)) == 0)
			return false;

		nint read = _read!(StdinFd, _readBuffer, _readBuffer.Length);
		if (read <= 0)
		{
			_endOfInput = true;
			return false;
		}

		// Consumed chars are dropped before appending, so the buffer only ever holds what is unread.
		if (_head > 0)
		{
			_pending.RemoveRange(0, _head);
			_head = 0;
		}

		int chars = _utf8.GetChars(_readBuffer, 0, (int)read, _charBuffer, 0, false);
		for (int i = 0; i < chars; i++)
			_pending.Add(_charBuffer[i]);
		return true;
	}
}