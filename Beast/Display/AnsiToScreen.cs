using System;
using System.Collections.Generic;
using System.Text;


// Parses strings containing ANSI SGR escape sequences and writes them as Cells onto a Screen row.
// Supported SGR params: 0 reset, 1 bold, 2 dim (folded by darkening the active fg/bg), 3 italic, 4 underline,
// 22/23/24 disable bold-or-dim/italic/underline, 30-37/40-47 standard colors, 38;5;N / 48;5;N indexed,
// 38;2;R;G;B / 48;2;R;G;B truecolor, 39 / 49 default fg/bg, 90-97/100-107 bright variants.
// Unknown params are ignored. Embedded newlines/tabs are not handled — caller pre-processes.
public static class AnsiToScreen
{
    // Writes a single logical line of ANSI text into target at row y, starting at column x0.
    // baseFg/baseBg are the row's default colors used whenever a reset or 39/49 is encountered.
    // Returns the next column past the last written cell plus the trailing fg/bg state so callers can
    // pad the remainder of the row with whatever colors the text actually ended on (important when the
    // line switches to a different bg mid-row — e.g. a tool response inside a tool-call block).
    public static (int EndX, Rgb? FinalFg, Rgb? FinalBg) WriteLine(Screen target, int x0, int y, string ansiText, Rgb? baseFg, Rgb? baseBg)
    {
        if (y < 0 || y >= target.H) return (x0, baseFg, baseBg);

        Rgb?      fg    = baseFg;
        Rgb?      bg    = baseBg;
        CellStyle style = CellStyle.None;
        bool      dim   = false;

        int cx = x0;
        int i  = 0;
        while (i < ansiText.Length)
        {
            char c = ansiText[i];
            if (c == '\x1b' && i + 1 < ansiText.Length && ansiText[i + 1] == '[')
            {
                int j = i + 2;
                while (j < ansiText.Length && !(ansiText[j] >= '@' && ansiText[j] <= '~')) j++;
                if (j >= ansiText.Length) break;
                char final = ansiText[j];
                if (final == 'm')
                {
                    ApplySgr(ansiText, i + 2, j, ref fg, ref bg, ref style, ref dim, baseFg, baseBg);
                }
                i = j + 1;
                continue;
            }

            if (cx < target.W && cx >= 0)
            {
                Rgb? renderFg = dim && fg.HasValue ? fg.Value.Scale(0.5f) : fg;
                Rgb? renderBg = dim && bg.HasValue ? bg.Value.Scale(0.5f) : bg;
                target.Set(cx, y, new Cell(c, renderFg, renderBg, style));
            }
            cx++;
            i++;
        }

        Rgb? finalFg = dim && fg.HasValue ? fg.Value.Scale(0.5f) : fg;
        Rgb? finalBg = dim && bg.HasValue ? bg.Value.Scale(0.5f) : bg;
        return (cx, finalFg, finalBg);
    }

    // Fills the remainder of the row from (cx..target.W) with blank cells using the trailing fg/bg state.
    // Useful so a colored background extends to the right edge of the row.
    public static void PadRowBackground(Screen target, int cx, int y, Rgb? fg, Rgb? bg)
    {
        if (y < 0 || y >= target.H) return;
        Cell pad = new Cell(' ', fg, bg, CellStyle.None);
        for (int x = cx; x < target.W; x++)
            target.Set(x, y, pad);
    }

    private static void ApplySgr(string s, int start, int end,
        ref Rgb? fg, ref Rgb? bg, ref CellStyle style, ref bool dim,
        Rgb? baseFg, Rgb? baseBg)
    {
        List<int> p = ParseParams(s, start, end);
        if (p.Count == 0)
        {
            fg = baseFg;
            bg = baseBg;
            style = CellStyle.None;
            dim = false;
            return;
        }

        int k = 0;
        while (k < p.Count)
        {
            int code = p[k];
            switch (code)
            {
                case 0:
                    fg = baseFg;
                    bg = baseBg;
                    style = CellStyle.None;
                    dim = false;
                    k++;
                    break;
                case 1:  style |= CellStyle.Bold;      k++; break;
                case 2:  dim = true;                   k++; break;
                case 3:  style |= CellStyle.Italic;    k++; break;
                case 4:  style |= CellStyle.Underline; k++; break;
                case 22: style &= ~CellStyle.Bold; dim = false; k++; break;
                case 23: style &= ~CellStyle.Italic;    k++; break;
                case 24: style &= ~CellStyle.Underline; k++; break;
                case 38:
                    if (k + 4 < p.Count && p[k + 1] == 2)
                    {
                        fg = new Rgb((byte)p[k + 2], (byte)p[k + 3], (byte)p[k + 4]);
                        k += 5;
                    }
                    else if (k + 2 < p.Count && p[k + 1] == 5)
                    {
                        fg = Palette256(p[k + 2]);
                        k += 3;
                    }
                    else
                    {
                        k++;
                    }
                    break;
                case 48:
                    if (k + 4 < p.Count && p[k + 1] == 2)
                    {
                        bg = new Rgb((byte)p[k + 2], (byte)p[k + 3], (byte)p[k + 4]);
                        k += 5;
                    }
                    else if (k + 2 < p.Count && p[k + 1] == 5)
                    {
                        bg = Palette256(p[k + 2]);
                        k += 3;
                    }
                    else
                    {
                        k++;
                    }
                    break;
                case 39: fg = baseFg; k++; break;
                case 49: bg = baseBg; k++; break;
                default:
                    if (code >= 30 && code <= 37)        { fg = StandardAnsi(code - 30, bright: false); k++; }
                    else if (code >= 40 && code <= 47)   { bg = StandardAnsi(code - 40, bright: false); k++; }
                    else if (code >= 90 && code <= 97)   { fg = StandardAnsi(code - 90, bright: true);  k++; }
                    else if (code >= 100 && code <= 107) { bg = StandardAnsi(code - 100, bright: true); k++; }
                    else                                  { k++; }
                    break;
            }
        }
    }

    private static List<int> ParseParams(string s, int start, int end)
    {
        List<int> result = new List<int>();
        int value = 0;
        bool any = false;
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9')
            {
                value = value * 10 + (c - '0');
                any = true;
            }
            else if (c == ';' || c == ':')
            {
                result.Add(any ? value : 0);
                value = 0;
                any = false;
            }
        }
        if (any || (start < end && s[end - 1] != ';' && s[end - 1] != ':'))
            result.Add(value);
        return result;
    }

    private static Rgb StandardAnsi(int idx, bool bright)
    {
        // Classic VGA-ish palette used by most terminals for indices 0..15.
        switch (idx + (bright ? 8 : 0))
        {
            case 0:  return new Rgb(0, 0, 0);
            case 1:  return new Rgb(170, 0, 0);
            case 2:  return new Rgb(0, 170, 0);
            case 3:  return new Rgb(170, 85, 0);
            case 4:  return new Rgb(0, 0, 170);
            case 5:  return new Rgb(170, 0, 170);
            case 6:  return new Rgb(0, 170, 170);
            case 7:  return new Rgb(170, 170, 170);
            case 8:  return new Rgb(85, 85, 85);
            case 9:  return new Rgb(255, 85, 85);
            case 10: return new Rgb(85, 255, 85);
            case 11: return new Rgb(255, 255, 85);
            case 12: return new Rgb(85, 85, 255);
            case 13: return new Rgb(255, 85, 255);
            case 14: return new Rgb(85, 255, 255);
            default: return new Rgb(255, 255, 255);
        }
    }

    // xterm 256-color palette: 0-15 standard, 16-231 6×6×6 RGB cube, 232-255 grayscale.
    public static Rgb Palette256(int n)
    {
        if (n < 16) return StandardAnsi(n & 7, n >= 8);
        if (n >= 232)
        {
            int v = 8 + (n - 232) * 10;
            if (v > 255) v = 255;
            return new Rgb((byte)v, (byte)v, (byte)v);
        }
        int c = n - 16;
        int r = c / 36;
        int g = (c / 6) % 6;
        int b = c % 6;
        return new Rgb(Cube(r), Cube(g), Cube(b));
    }

    private static byte Cube(int n)
    {
        // Standard xterm step values for the 6-level cube.
        switch (n)
        {
            case 0: return 0;
            case 1: return 95;
            case 2: return 135;
            case 3: return 175;
            case 4: return 215;
            default: return 255;
        }
    }
}
