// Terminal column width of a character for the cell grid. The renderer is a fixed grid of one cell per
// column, so a glyph the terminal draws two columns wide — CJK, Hangul, and specific wide symbols — must
// reserve two cells, or everything after it slides one column to the right (the "F10 panel pushed over by
// one" symptom). Zero-width combining marks reserve no column.
// Surrogate code units report width 1; callers replace them with a placeholder, since one Cell holds a
// single UTF-16 char and cannot carry an astral-plane glyph.
//
// The symbol blocks are the perennial problem: they mix narrow and wide glyphs (✓ advances one column,
// ✅ two; ⧉ draws wide in some fonts but advances one) and terminals disagree about them, so no static
// table is ever right everywhere. InitialProbe() therefore measures the real cursor advance of every
// codepoint in those blocks on the live terminal at startup, using the Cursor Position Report (write the
// glyph at column 1, ask \x1b[6n where the cursor landed). Measured widths override the static table
// below, which remains the fallback for unprobed codepoints and for terminals that do not answer CPR.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;


public static class CharWidth
{
	// Terminal-measured widths: codepoint -> measured advance (0, 1, or 2). Populated once by
	// InitialProbe() before the input loop starts, read-only afterwards. Checked before the static
	// table so the live terminal always wins.
	private static readonly Dictionary<int, int> _overrides = new Dictionary<int, int>();

	// Windows P/Invoke for reading CPR responses from the console input buffer.
	private const int StdInputHandle = -10;
	private const uint WAIT_OBJECT_0 = 0x00000000;
	private const ushort KEY_EVENT_TYPE = 0x0001;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetStdHandle(int nStdHandle);

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
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct KEY_EVENT_RECORD
	{
		public int bKeyDown;
		public ushort wRepeatCount;
		public ushort wVirtualKeyCode;
		public ushort wVirtualScanCode;
		public char UnicodeChar;
		public uint dwControlKeyState;
	}

	// Per-response CPR timeout — locally attached terminals answer within a few ms.
	private const int CprTimeoutMs = 100;

	// Probes are pipelined in small batches: all queries of a batch are written in one flush, then the
	// responses are read back in order. Small batches keep the console input buffer (which holds each
	// response character as a separate event record) from overflowing before we drain it.
	private const int ProbeBatchSize = 16;

	// Hard ceiling on total probing time so a sluggish terminal can never stall startup noticeably;
	// whatever was measured before the budget ran out is kept.
	private const int ProbeBudgetMs = 1000;

	// 0 = zero-width (combining mark / zero-width space), 2 = wide, 1 = everything else.
	public static int Of(char c)
	{
		if (c < 0x0080)
			return 1;          // ASCII fast path: always single-width, never probed
		if (_overrides.TryGetValue(c, out int width))
			return width;
		if (IsZeroWidth(c))
			return 0;
		if (IsWide(c))
			return 2;
		return 1;
	}

	// Measures the ambiguous symbol blocks on the live terminal and records the results as overrides.
	// Every codepoint is probed individually — the blocks mix narrow and wide glyphs, so a per-range
	// representative would get many of them wrong. Must run on the alt screen while the probe owns the
	// input stream: before mouse reporting is enabled and before anything else reads console input.
	// The scratch row it draws on is erased by the first full redraw. A terminal that fails the canary
	// (an ASCII 'x' must measure width 1) leaves the override set empty and the static table in charge.
	public static void InitialProbe()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return;

		IntPtr hIn = GetStdHandle(StdInputHandle);
		if (hIn == IntPtr.Zero || hIn == (IntPtr)(-1))
			return;

		// Drop any queued input so stale keystrokes are not mistaken for CPR responses.
		FlushInput(hIn);

		long deadline = Environment.TickCount64 + ProbeBudgetMs;

		int[] canary = ProbeBatch(hIn, new int[] { 'x' }, deadline);
		if (canary.Length != 1 || canary[0] != 1)
			return;

		// The trouble blocks: miscellaneous technical (⌚ ⏰ …), geometric shapes (◽ ▶ …), miscellaneous
		// symbols and dingbats (☑ ⚠ ✓ ✅ …), supplemental arrows/symbols (⬆ ⭐ ⭕ …), plus ⧉ (the copy
		// glyph, wide-looking but single-advance in some terminals).
		List<int> codepoints = new List<int>();
		AddRange(codepoints, 0x2300, 0x23FF);
		AddRange(codepoints, 0x25A0, 0x25FF);
		AddRange(codepoints, 0x2600, 0x27BF);
		AddRange(codepoints, 0x2B00, 0x2BFF);
		codepoints.Add(0x29C9);

		int done = 0;
		while (done < codepoints.Count && Environment.TickCount64 < deadline)
		{
			int count = Math.Min(ProbeBatchSize, codepoints.Count - done);
			int[] batch = new int[count];
			for (int i = 0; i < count; i++)
				batch[i] = codepoints[done + i];

			int[] widths = ProbeBatch(hIn, batch, deadline);
			for (int i = 0; i < widths.Length; i++)
			{
				if (widths[i] >= 0 && widths[i] <= 2)
					_overrides[batch[i]] = widths[i];
			}
			if (widths.Length < count)
				break;  // a response timed out — the stream is unreliable, keep what we have

			done += count;
		}

		// Leave the scratch row clean.
		Console.Out.Write("\x1b[9999;1H\x1b[K");
		Console.Out.Flush();
	}

	private static void AddRange(List<int> codepoints, int lo, int hi)
	{
		for (int cp = lo; cp <= hi; cp++)
			codepoints.Add(cp);
	}

	// Probes one batch: for each codepoint, park at column 1 of the scratch row, erase it, print the
	// glyph, and ask the terminal where the cursor landed (column = width + 1). All queries go out in a
	// single flush; the terminal answers them in order, so the responses are then read back one by one.
	// Returns one width per answered query — shorter than the batch when a response timed out.
	private static int[] ProbeBatch(IntPtr hIn, int[] codepoints, long deadline)
	{
		List<int> widths = new List<int>(codepoints.Length);
		try
		{
			StringBuilder query = new StringBuilder(codepoints.Length * 16);
			foreach (int cp in codepoints)
				query.Append("\x1b[9999;1H\x1b[K").Append((char)cp).Append("\x1b[6n");
			Console.Out.Write(query);
			Console.Out.Flush();

			for (int i = 0; i < codepoints.Length; i++)
			{
				int col = ReadCprColumn(hIn, deadline);
				if (col < 1)
					break;
				widths.Add(col - 1);
			}
		}
		catch
		{
			// Console gone or handle invalid — abandon probing, static table remains authoritative.
		}
		return widths.ToArray();
	}

	// Reads one CPR response (ESC [ <row> ; <col> R) from the console input buffer and returns its
	// column, or -1 on timeout. Requires ENABLE_VIRTUAL_TERMINAL_INPUT so the response arrives as
	// key-event records (WindowsConsole.EnableVirtualTerminal sets it before probing runs).
	private static int ReadCprColumn(IntPtr hIn, long deadline)
	{
		StringBuilder sb = new StringBuilder(16);
		while (Environment.TickCount64 < deadline)
		{
			uint result = WaitForSingleObject(hIn, (uint)Math.Min(CprTimeoutMs, Math.Max(1, deadline - Environment.TickCount64)));
			if (result != WAIT_OBJECT_0)
				break;  // timeout

			INPUT_RECORD[] buf = new INPUT_RECORD[1];
			if (!ReadConsoleInputW(hIn, buf, 1, out uint read) || read == 0)
				continue;
			if (buf[0].EventType != KEY_EVENT_TYPE || buf[0].KeyEvent.bKeyDown == 0)
				continue;

			char ch = buf[0].KeyEvent.UnicodeChar;
			if (ch == 'R')
				return ParseCprColumn(sb.ToString());
			sb.Append(ch);
		}
		return -1;
	}

	// Parses the column from an accumulated CPR response ("\x1b[<row>;<col>", terminator consumed):
	// the number after the last ';'.
	private static int ParseCprColumn(string resp)
	{
		int col = -1;
		int semi = resp.LastIndexOf(';');
		if (semi >= 0 && !int.TryParse(resp.Substring(semi + 1), out col))
			col = -1;
		return col;
	}

	// Drains any pending records from the console input buffer.
	private static void FlushInput(IntPtr hIn)
	{
		const int maxFlush = 256;
		INPUT_RECORD[] buf = new INPUT_RECORD[maxFlush];
		while (PeekConsoleInputW(hIn, buf, maxFlush, out uint peeked) && peeked > 0)
			ReadConsoleInputW(hIn, buf, peeked, out _);
	}

	private static bool IsZeroWidth(char c)
	{
		return (c >= 0x0300 && c <= 0x036F)   // combining diacritical marks
			|| (c >= 0x1AB0 && c <= 0x1AFF)   // combining diacritical marks extended
			|| (c >= 0x1DC0 && c <= 0x1DFF)   // combining diacritical marks supplement
			|| (c >= 0x20D0 && c <= 0x20FF)   // combining diacritical marks for symbols
			|| (c >= 0xFE20 && c <= 0xFE2F)   // combining half marks
			|| c == 0x200B || c == 0x200C || c == 0x200D || c == 0x2060 || c == 0xFEFF; // zero-width spaces / joiners / BOM
	}

	// Static fallback, used when a codepoint was not probed (or probing was unavailable). The symbol
	// entries reflect Windows Terminal's behaviour: U+2600–U+26FF (misc symbols: ☑ ☀ ⚠ …) advance two
	// columns, U+2700–U+27BF (dingbats: ✓ ✗ ✖ …) advance one; ⧉ (U+29C9) advances one despite drawing
	// wide in some fonts.
	private static bool IsWide(char c)
	{
		return (c >= 0x1100 && c <= 0x115F)   // Hangul Jamo
			|| c == 0x2329 || c == 0x232A     // angle brackets 〈 〉
			|| (c >= 0x231A && c <= 0x231B)   // ⌚ ⌛
			|| (c >= 0x23E9 && c <= 0x23FA)   // media control symbols (⏩ ⏪ ⏫ ⏬ ⏰ ⏳ …)
			|| (c >= 0x25FD && c <= 0x25FE)   // ◽ ◾
			|| (c >= 0x2600 && c <= 0x26FF)   // misc symbols (☑ ☀ ⚠ …)
			|| c == 0x2B50                    // star ⭐
			|| (c >= 0x2E80 && c <= 0x303E)   // CJK radicals … Kangxi
			|| (c >= 0x3041 && c <= 0x33FF)   // Hiragana … CJK compatibility
			|| (c >= 0x3400 && c <= 0x4DBF)   // CJK extension A
			|| (c >= 0x4E00 && c <= 0x9FFF)   // CJK unified ideographs
			|| (c >= 0xA000 && c <= 0xA4CF)   // Yi
			|| (c >= 0xAC00 && c <= 0xD7A3)   // Hangul syllables
			|| (c >= 0xF900 && c <= 0xFAFF)   // CJK compatibility ideographs
			|| (c >= 0xFE10 && c <= 0xFE19)   // vertical forms
			|| (c >= 0xFE30 && c <= 0xFE6B)   // CJK compatibility forms / small form variants
			|| (c >= 0xFF00 && c <= 0xFF60)   // fullwidth forms
			|| (c >= 0xFFE0 && c <= 0xFFE6);  // fullwidth signs
	}
}
