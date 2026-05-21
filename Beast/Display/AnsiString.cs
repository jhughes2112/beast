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
