using System;
using System.Text;


// Converts a Screen into ANSI VT escape sequences using 24-bit truecolor (\x1b[38;2;R;G;Bm / [48;2;R;G;Bm).
// Tracks the previous cell's Fg/Bg/Style so identical state between adjacent cells emits no extra escapes.
// Null Fg/Bg are emitted as the terminal default (\x1b[39m for fg, \x1b[49m for bg).
public static class ScreenAnsiWriter
{
	// Writes the full screen into buf, positioning the cursor at (startRow, startCol) (1-based) at the start
	// of each row. Caller is responsible for hiding/showing the cursor and final reset.
	public static void Write(StringBuilder buf, Screen screen, int startRow, int startCol)
	{
		if (screen.W == 0 || screen.H == 0)
			return;

		Rgb?      lastFg    = default;
		Rgb?      lastBg    = default;
		CellStyle lastStyle = CellStyle.None;
		bool      haveLast  = false;

		for (int y = 0; y < screen.H; y++)
		{
			buf.Append('\x1b').Append('[').Append(startRow + y).Append(';').Append(startCol).Append('H');

			for (int x = 0; x < screen.W; x++)
			{
				Cell c = screen.Get(x, y);
				bool changed = !haveLast
					|| !ColorEquals(c.Fg, lastFg)
					|| !ColorEquals(c.Bg, lastBg)
					|| c.Style != lastStyle;

				if (changed)
				{
					EmitState(buf, c.Fg, c.Bg, c.Style, lastFg, lastBg, lastStyle, haveLast);
					lastFg = c.Fg;
					lastBg = c.Bg;
					lastStyle = c.Style;
					haveLast = true;
				}

				buf.Append(c.Ch == '\0' ? ' ' : c.Ch);

				// A wide glyph (CJK, emoji, checkbox mark) advances the terminal cursor two columns on its
				// own, so skip the reserved trailing cell the layout left blank — keeping the grid and the
				// terminal column-aligned. Driving the skip off the glyph's width (not a marker cell) also
				// keeps a wide glyph two columns wide even if an overlay overwrote the cell behind it.
				if (CharWidth.Of(c.Ch) == 2)
					x++;
			}
		}

		buf.Append("\x1b[0m");
	}

	private static bool ColorEquals(Rgb? a, Rgb? b)
	{
		if (a.HasValue != b.HasValue)
			return false;
		if (!a.HasValue)
			return true;
		Rgb av = a!.Value;
		Rgb bv = b!.Value;
		return av.R == bv.R && av.G == bv.G && av.B == bv.B;
	}

	private static void EmitState(
		StringBuilder buf,
		Rgb? fg, Rgb? bg, CellStyle style,
		Rgb? lastFg, Rgb? lastBg, CellStyle lastStyle,
		bool haveLast)
	{
		// Style changes other than additive bits require a reset, since SGR has no "turn off italic only" in
		// many terminals. Reset and re-emit colors when removing any style bit.
		bool styleNeedsReset = haveLast && (lastStyle & ~style) != CellStyle.None;
		if (styleNeedsReset)
		{
			buf.Append("\x1b[0m");
			haveLast = false;
			lastFg = null;
			lastBg = null;
		}

		CellStyle baseline = haveLast ? lastStyle : CellStyle.None;
		CellStyle added    = style & ~baseline;
		if ((added & CellStyle.Bold) != 0)
			buf.Append("\x1b[1m");
		if ((added & CellStyle.Italic) != 0)
			buf.Append("\x1b[3m");
		if ((added & CellStyle.Underline) != 0)
			buf.Append("\x1b[4m");

		if (!haveLast || !ColorEquals(fg, lastFg))
		{
			if (fg.HasValue)
			{
				Rgb v = fg.Value;
				buf.Append("\x1b[38;2;").Append(v.R).Append(';').Append(v.G).Append(';').Append(v.B).Append('m');
			}
			else
			{
				buf.Append("\x1b[39m");
			}
		}

		if (!haveLast || !ColorEquals(bg, lastBg))
		{
			if (bg.HasValue)
			{
				Rgb v = bg.Value;
				buf.Append("\x1b[48;2;").Append(v.R).Append(';').Append(v.G).Append(';').Append(v.B).Append('m');
			}
			else
			{
				buf.Append("\x1b[49m");
			}
		}
	}
}