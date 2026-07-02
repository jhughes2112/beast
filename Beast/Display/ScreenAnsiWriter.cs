using System;
using System.Text;


// Converts a Screen into ANSI VT escape sequences using 24-bit truecolor (\x1b[38;2;R;G;Bm / [48;2;R;G;Bm).
// Tracks the previous cell's Fg/Bg/Style so identical state between adjacent cells emits no extra escapes.
// Null Fg/Bg are emitted as the terminal default (\x1b[39m for fg, \x1b[49m for bg).
public static class ScreenAnsiWriter
{
	// Running SGR state threaded through span emission so identical state between adjacent cells (and
	// across spans within one write) emits no extra escapes. Valid=false means the terminal state is unknown.
	private struct SgrTracker
	{
		public Rgb?      Fg;
		public Rgb?      Bg;
		public CellStyle Style;
		public bool      Valid;
	}

	// Writes the full screen into buf, positioning the cursor at (startRow, startCol) (1-based) at the start
	// of each row. Caller is responsible for hiding/showing the cursor and final reset.
	public static void Write(StringBuilder buf, Screen screen, int startRow, int startCol)
	{
		if (screen.W == 0 || screen.H == 0)
			return;

		SgrTracker tracker = new SgrTracker();

		for (int y = 0; y < screen.H; y++)
			EmitSpan(buf, screen, y, 0, screen.W - 1, startRow, startCol, ref tracker);

		buf.Append("\x1b[0m");
	}

	// Writes only the cells that differ from prev (a same-sized Screen holding what the terminal currently
	// shows) — one contiguous span per row, from the first changed cell to the last. This is what makes
	// per-tick redraws (busy spinner, clock, cursor glow) cheap: an unchanged frame emits nothing, and a
	// small change emits a few dozen bytes instead of the whole screen.
	public static void WriteDiff(StringBuilder buf, Screen screen, Screen prev, int startRow, int startCol)
	{
		if (screen.W == 0 || screen.H == 0)
			return;

		SgrTracker tracker = new SgrTracker();
		bool wrote = false;

		for (int y = 0; y < screen.H; y++)
		{
			int first = -1;
			int last  = -1;
			for (int x = 0; x < screen.W; x++)
			{
				Cell c = screen.Get(x, y);
				Cell p = prev.Get(x, y);
				if (!RenderEquals(c, p))
				{
					if (first < 0)
						first = x;
					last = x;
					// A wide glyph being replaced leaves its right half on the terminal; the span must
					// rewrite that column too even if the new cell there matches the stored prev cell.
					if (CharWidth.Of(p.Ch) == 2 && x + 1 < screen.W)
						last = x + 1;
				}
			}

			if (first < 0)
				continue;

			// Never start a span on a wide glyph's reserved trailing cell — back up onto the glyph so it
			// is re-emitted whole and the terminal's two-column advance stays aligned with the grid.
			if (first > 0 && CharWidth.Of(screen.Get(first - 1, y).Ch) == 2)
				first--;

			EmitSpan(buf, screen, y, first, last, startRow, startCol, ref tracker);
			wrote = true;
		}

		if (wrote)
			buf.Append("\x1b[0m");
	}

	// Emits cells [x0, x1] of row y, positioning the cursor first. Threads the SGR tracker so state carries
	// across spans within one write.
	private static void EmitSpan(StringBuilder buf, Screen screen, int y, int x0, int x1, int startRow, int startCol, ref SgrTracker tracker)
	{
		buf.Append('\x1b').Append('[').Append(startRow + y).Append(';').Append(startCol + x0).Append('H');

		for (int x = x0; x <= x1; x++)
		{
			Cell c = screen.Get(x, y);
			bool changed = !tracker.Valid
				|| !ColorEquals(c.Fg, tracker.Fg)
				|| !ColorEquals(c.Bg, tracker.Bg)
				|| c.Style != tracker.Style;

			if (changed)
			{
				EmitState(buf, c.Fg, c.Bg, c.Style, tracker.Fg, tracker.Bg, tracker.Style, tracker.Valid);
				tracker.Fg = c.Fg;
				tracker.Bg = c.Bg;
				tracker.Style = c.Style;
				tracker.Valid = true;
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

	// Whether two cells render identically on the terminal. '\0' and ' ' are the same visible glyph.
	private static bool RenderEquals(Cell a, Cell b)
	{
		char ca = a.Ch == '\0' ? ' ' : a.Ch;
		char cb = b.Ch == '\0' ? ' ' : b.Ch;
		return ca == cb && a.Style == b.Style && ColorEquals(a.Fg, b.Fg) && ColorEquals(a.Bg, b.Bg);
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