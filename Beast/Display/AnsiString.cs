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
            if (c == '\x1b') { inEsc = true; continue; }
            if (inEsc) { if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) inEsc = false; continue; }
            len++;
        }
        return len;
    }

    // Wraps a string with ANSI codes into multiple strings each with at most maxWidth visible chars.
    // ANSI state is re-applied at the start of each continuation line.
    public static List<string> Wrap(string s, int maxWidth)
    {
        List<string> result = new List<string>();
        if (maxWidth < 1) maxWidth = 1;

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

            if (visLen == maxWidth)
            {
                result.Add(currentLine.ToString());
                currentLine.Clear();
                currentLine.Append(currentEscState.ToString());
                visLen = 0;
            }

            currentLine.Append(c);
            visLen++;
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
        if (maxWidth < 1) maxWidth = 1;

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
            if (wordVisLen == 0 && wordBuf.Length == 0) return;

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
                int wVis = 0;
                StringBuilder esc = new StringBuilder();
                StringBuilder chunkEscState = new StringBuilder(currentEscState.ToString());
                bool wInEsc = false;
                StringBuilder wEscBuf = new StringBuilder();
                foreach (char wc in wordBuf.ToString())
                {
                    if (wc == '\x1b') { wInEsc = true; wEscBuf.Clear(); wEscBuf.Append(wc); currentLine.Append(wc); continue; }
                    if (wInEsc)
                    {
                        wEscBuf.Append(wc);
                        currentLine.Append(wc);
                        if ((wc >= 'A' && wc <= 'Z') || (wc >= 'a' && wc <= 'z'))
                        {
                            wInEsc = false;
                            string e = wEscBuf.ToString();
                            if (e == "\x1b[0m" || e == "\x1b[m") chunkEscState.Clear();
                            else chunkEscState.Append(e);
                        }
                        continue;
                    }
                    if (visLen == maxWidth)
                    {
                        result.Add(currentLine.ToString());
                        currentLine.Clear();
                        currentLine.Append(chunkEscState.ToString());
                        visLen = 0;
                    }
                    currentLine.Append(wc);
                    visLen++;
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
                        if (esc == "\x1b[0m" || esc == "\x1b[m") wordEscBuf.Clear();
                        else wordEscBuf.Append(esc);
                    }
                    else
                    {
                        currentLine.Append(esc);
                        if (esc == "\x1b[0m" || esc == "\x1b[m") currentEscState.Clear();
                        else currentEscState.Append(esc);
                    }
                }
                continue;
            }

            if (c == ' ')
            {
                FlushWord(soft: true);
                // Spaces at end-of-line are dropped to avoid trailing whitespace producing an extra wrap.
                if (visLen == maxWidth)
                {
                    result.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentLine.Append(currentEscState.ToString());
                    visLen = 0;
                }
                else if (visLen > 0)
                {
                    currentLine.Append(' ');
                    visLen++;
                }
                // else: leading space on a wrapped line — drop it.
            }
            else
            {
                wordBuf.Append(c);
                wordVisLen++;
            }
        }

        FlushWord(soft: false);
        result.Add(currentLine.ToString());
        return result;
    }

    // Pads or truncates to exactly visibleWidth visible characters.
    // Trailing reset is appended when the string contained codes.
    public static string TruncatePad(string s, int visibleWidth)
    {
        int vl = VisibleLength(s);
        if (vl == visibleWidth) return s;
        if (vl > visibleWidth) return TruncateVisible(s, visibleWidth);
        return s + new string(' ', visibleWidth - vl);
    }

    public static string TruncateVisible(string s, int maxVisible)
    {
        StringBuilder sb = new StringBuilder();
        int count = 0;
        bool inEsc = false;
        foreach (char c in s)
        {
            if (c == '\x1b') { inEsc = true; sb.Append(c); continue; }
            if (inEsc) { sb.Append(c); if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) inEsc = false; continue; }
            if (count >= maxVisible) break;
            sb.Append(c);
            count++;
        }
        return sb.ToString();
    }
}
