using System;
using System.Collections.Generic;
using System.Text;


// Utilities for working with strings that contain ANSI escape codes.
// "Visible length" means the count of printable characters, ignoring escape sequences.
public static class AnsiString
{
	// Returns the number of visible (printable) characters, ignoring ANSI escape sequences.
	public static int VisibleLength(string s)
	{
		int len = 0;
		bool inEsc = false;
		foreach (char c in s)
		{
			if (c == '\x1b')
			{ inEsc = true; continue; }
			if (inEsc)
			{ if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) inEsc = false; continue; }
			len += CharWidth(c);
		}
		return len;
	}

	// Parses a string and returns the ANSI SGR reset sequence needed to close all open
	// formatting codes (bold, italic, colors, etc.) at the end of the string.
	// Returns "\x1b[0m" if any SGR codes were found that aren't explicitly reset,
	// otherwise returns empty string.
	public static string GetCloseSequences(string s)
	{
		bool inEsc = false;
		StringBuilder escBuf = new StringBuilder();
		bool foundNonResetSgr = false;

		foreach (char c in s)
		{
			if (c == '\x1b')
			{
				inEsc = true;
				escBuf.Clear();
				escBuf.Append(c);
				continue;
			}

			if (inEsc)
			{
				escBuf.Append(c);
				if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
				{
					inEsc = false;
					string esc = escBuf.ToString();
					// Check if this is an SGR sequence (ends with 'm')
					if (esc.Length >= 2 && esc[esc.Length - 1] == 'm' && esc[1] == '[')
					{
						// Check if it's a reset sequence (0m or just m)
						string paramsPart = esc.Substring(2, esc.Length - 3); // skip \x1b[ and m
						if (paramsPart == "0" || paramsPart == "")
						{
							// Reset clears all prior state
							foundNonResetSgr = false;
						}
						else
						{
							// Non-reset SGR - we have open formatting
							foundNonResetSgr = true;
						}
					}
				}
				continue;
			}
		}

		return foundNonResetSgr ? "\x1b[0m" : string.Empty;
	}

	// Truncates a string to maxVisible visible characters and appends ANSI reset
	// if any formatting codes were left open at the truncation point.
	public static string TruncateVisible(string s, int maxVisible)
	{
		StringBuilder sb = new StringBuilder();
		int count = 0;
		bool inEsc = false;
		foreach (char c in s)
		{
			if (c == '\x1b')
			{ inEsc = true; sb.Append(c); continue; }
			if (inEsc)
			{ sb.Append(c); if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) inEsc = false; continue; }
			int cw = CharWidth(c);
			if (count + cw > maxVisible)
				break;
			sb.Append(c);
			count += cw;
		}
		// Append close sequences for any open ANSI codes
		sb.Append(GetCloseSequences(sb.ToString()));
		return sb.ToString();
	}

	// Returns the terminal column width of a single character: 2 for wide (CJK, emoji, etc.), 1 for everything else.
	public static int CharWidth(char c)
	{
		int cp = c;
		// CJK unified ideographs and common extensions.
		if (cp >= 0x4E00 && cp <= 0x9FFF)
			return 2;
		if (cp >= 0x3400 && cp <= 0x4DBF)
			return 2;
		if (cp >= 0xF900 && cp <= 0xFAFF)
			return 2;
		// Hangul syllables.
		if (cp >= 0xAC00 && cp <= 0xD7AF)
			return 2;
		// Fullwidth and halfwidth forms (fullwidth half = 2, but halfwidth katakana = 1; we cover the fullwidth block).
		if (cp >= 0xFF01 && cp <= 0xFF60)
			return 2;
		if (cp >= 0xFFE0 && cp <= 0xFFE6)
			return 2;
		// Enclosed alphanumerics, box drawing, block elements — these are 1-wide.
		// Misc symbols and dingbats (includes checkmarks ✅ U+2705, stars, arrows, etc.).
		if (cp >= 0x2600 && cp <= 0x27BF)
			return 2;
		// Supplemental symbols and emoji: U+1F000 and up. Since char is UTF-16, these are
		// surrogate pairs and won't appear as a single char — handled as 1 each (surrogate half).
		// The high surrogate range D800-DBFF contributes 2 for the pair when the low follows,
		// but since we iterate char-by-char we count the high surrogate as 2 and low as 0.
		if (cp >= 0xD800 && cp <= 0xDBFF)
			return 2;  // high surrogate: start of wide emoji pair
		if (cp >= 0xDC00 && cp <= 0xDFFF)
			return 0;  // low surrogate: already counted by high
		return 1;
	}

	// Wraps a string with ANSI codes into multiple strings each with at most maxWidth visible chars.
	// ANSI state is re-applied at the start of each continuation line.
	public static List<string> Wrap(string s, int maxWidth)
	{
		List<string> result = new List<string>();
		if (maxWidth < 1)
			maxWidth = 1;

		StringBuilder currentLine = new StringBuilder();
		StringBuilder currentEscState = new StringBuilder();
		int visLen = 0;
		bool inEsc = false;
		StringBuilder escBuf = new StringBuilder();

		foreach (char c in s)
		{
			if (c == '\x1b')
			{
				inEsc = true;
				escBuf.Clear();
				escBuf.Append(c);
				continue;
			}

			if (inEsc)
			{
				escBuf.Append(c);
				if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
				{
					inEsc = false;
					string esc = escBuf.ToString();
					currentLine.Append(esc);
					// Track state: a reset clears accumulated state.
					if (esc == "\x1b[0m" || esc == "\x1b[m")
						currentEscState.Clear();
					else
						currentEscState.Append(esc);
				}
				continue;
			}

			if (visLen + CharWidth(c) > maxWidth)
			{
				result.Add(currentLine.ToString());
				currentLine.Clear();
				currentLine.Append(currentEscState.ToString());
				visLen = 0;
			}

			currentLine.Append(c);
			visLen += CharWidth(c);
		}

		result.Add(currentLine.ToString());
		return result;
	}

	// Word-aware wrap. Breaks at spaces when possible, falling back to hard char-break for runs that
	// exceed maxWidth (long URLs, code-like tokens). ANSI state is re-applied at the start of each
	// continuation line so colors/styles carry across the wrap.
	public static List<string> WordWrap(string s, int maxWidth)
	{
		List<string> result = new List<string>();
		if (maxWidth < 1)
			maxWidth = 1;

		StringBuilder currentLine    = new StringBuilder();
		StringBuilder currentEscState = new StringBuilder();
		StringBuilder wordBuf        = new StringBuilder();
		StringBuilder wordEscBuf     = new StringBuilder(); // ANSI codes embedded inside the current word
		int visLen        = 0;  // visible chars on current line
		int wordVisLen    = 0;  // visible chars in pending word
		bool inEsc        = false;
		StringBuilder escBuf = new StringBuilder();

		void FlushWord(bool soft)
		{
			if (wordVisLen == 0 && wordBuf.Length == 0)
				return;

			// If the pending word fits on the current line, append it.
			if (visLen + wordVisLen <= maxWidth)
			{
				currentLine.Append(wordBuf);
				currentEscState.Append(wordEscBuf);
				visLen += wordVisLen;
			}
			else if (wordVisLen <= maxWidth)
			{
				// Word fits on a fresh line — wrap before it.
				result.Add(currentLine.ToString());
				currentLine.Clear();
				currentLine.Append(currentEscState.ToString());
				currentLine.Append(wordBuf);
				currentEscState.Append(wordEscBuf);
				visLen = wordVisLen;
			}
			else
			{
				// Word is longer than the line width: hard-break inside it.
				// Re-walk wordBuf and emit chunks character by character.
				StringBuilder esc = new StringBuilder();
				StringBuilder chunkEscState = new StringBuilder(currentEscState.ToString());
				bool wInEsc = false;
				StringBuilder wEscBuf = new StringBuilder();
				foreach (char wc in wordBuf.ToString())
				{
					if (wc == '\x1b')
					{ wInEsc = true; wEscBuf.Clear(); wEscBuf.Append(wc); currentLine.Append(wc); continue; }
					if (wInEsc)
					{
						wEscBuf.Append(wc);
						currentLine.Append(wc);
						if ((wc >= 'A' && wc <= 'Z') || (wc >= 'a' && wc <= 'z'))
						{
							wInEsc = false;
							string e = wEscBuf.ToString();
							if (e == "\x1b[0m" || e == "\x1b[m")
								chunkEscState.Clear();
							else
								chunkEscState.Append(e);
						}
						continue;
					}
					if (visLen + CharWidth(wc) > maxWidth)
					{
						result.Add(currentLine.ToString());
						currentLine.Clear();
						currentLine.Append(chunkEscState.ToString());
						visLen = 0;
					}
					currentLine.Append(wc);
					visLen += CharWidth(wc);
				}
				currentEscState = chunkEscState;
			}

			wordBuf.Clear();
			wordEscBuf.Clear();
			wordVisLen = 0;
		}

		foreach (char c in s)
		{
			if (c == '\x1b')
			{
				inEsc = true;
				escBuf.Clear();
				escBuf.Append(c);
				continue;
			}

			if (inEsc)
			{
				escBuf.Append(c);
				if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
				{
					inEsc = false;
					string esc = escBuf.ToString();
					// ANSI codes belong to whatever they were inside — pending word or current line.
					if (wordBuf.Length > 0)
					{
						wordBuf.Append(esc);
						if (esc == "\x1b[0m" || esc == "\x1b[m")
							wordEscBuf.Clear();
						else
							wordEscBuf.Append(esc);
					}
					else
					{
						currentLine.Append(esc);
						if (esc == "\x1b[0m" || esc == "\x1b[m")
							currentEscState.Clear();
						else
							currentEscState.Append(esc);
					}
				}
				continue;
			}

			if (c == ' ')
			{
				FlushWord(soft: true);
				// Spaces at end-of-line are dropped to avoid trailing whitespace producing an extra wrap.
				if (visLen + CharWidth(c) > maxWidth)
				{
					result.Add(currentLine.ToString());
					currentLine.Clear();
					currentLine.Append(currentEscState.ToString());
					visLen = 0;
				}
				else if (visLen > 0)
				{
					currentLine.Append(' ');
					visLen += CharWidth(c);
				}
				// else: leading space on a wrapped line — drop it.
			}
			else
			{
				wordBuf.Append(c);
				wordVisLen += CharWidth(c);
			}
		}

		FlushWord(soft: false);
		result.Add(currentLine.ToString());
		return result;
	}

	// Word-wraps while preserving leading spaces as a hanging indent. Plain WordWrap drops leading
	// spaces, which erases markdown section indentation and list nesting; this keeps the first line's
	// indent and aligns every wrapped continuation line beneath it.
	public static List<string> WordWrapIndented(string s, int maxWidth)
	{
		int indent = 0;
		while (indent < s.Length && s[indent] == ' ')
			indent++;
		if (indent == 0)
			return WordWrap(s, maxWidth);

		string prefix = new string(' ', indent);
		string body   = s.Substring(indent);
		int bodyWidth = Math.Max(1, maxWidth - indent);

		List<string> wrapped = WordWrap(body, bodyWidth);
		List<string> result  = new List<string>();
		foreach (string line in wrapped)
			result.Add(prefix + line);
		return result;
	}

	// Pads or truncates to exactly visibleWidth visible characters.
	// Trailing reset is appended when the string contained codes.
	public static string TruncatePad(string s, int visibleWidth)
	{
		int vl = VisibleLength(s);
		if (vl == visibleWidth)
			return s;
		if (vl > visibleWidth)
			return TruncateVisible(s, visibleWidth);
		return s + new string(' ', visibleWidth - vl);
	}
}