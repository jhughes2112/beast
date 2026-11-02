using System;
using System.Collections.Generic;


// What every boxed popup and overlay shares: the bordered frame with its title in the top edge, and the
// plain-text fitting used inside it.
internal static class PopupFrame
{
	// Draws a one-cell border round the whole screen with the title set into the top edge.
	public static void Draw(Screen s, string title, Rgb borderFg, Rgb titleFg, Rgb bg)
	{
		int w = s.W;
		int h = s.H;
		for (int x = 0; x < w; x++)
		{
			s.Set(x,     0, new Cell('─', borderFg, bg, CellStyle.None));
			s.Set(x, h - 1, new Cell('─', borderFg, bg, CellStyle.None));
		}
		for (int y = 0; y < h; y++)
		{
			s.Set(    0, y, new Cell('│', borderFg, bg, CellStyle.None));
			s.Set(w - 1, y, new Cell('│', borderFg, bg, CellStyle.None));
		}
		s.Set(    0,     0, new Cell('┌', borderFg, bg, CellStyle.None));
		s.Set(w - 1,     0, new Cell('┐', borderFg, bg, CellStyle.None));
		s.Set(    0, h - 1, new Cell('└', borderFg, bg, CellStyle.None));
		s.Set(w - 1, h - 1, new Cell('┘', borderFg, bg, CellStyle.None));
		AnsiToScreen.WriteLine(s, 2, 0, $" {title} ", titleFg, bg);
	}

	// Cuts text to max characters, ending in an ellipsis when anything was cut.
	public static string Fit(string text, int max)
	{
		string fitted = text;
		if (max <= 0)
			fitted = string.Empty;
		else if (text.Length > max)
			fitted = max > 1 ? text.Substring(0, max - 1) + "…" : text.Substring(0, max);
		return fitted;
	}

	// Wraps plain text at word boundaries into lines of at most width characters; a word longer than a
	// whole line is cut to fit.
	public static List<string> WrapWords(string text, int width)
	{
		List<string> lines   = new List<string>();
		string       current = string.Empty;
		foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (current.Length > 0 && current.Length + 1 + word.Length > width)
			{
				lines.Add(current);
				current = string.Empty;
			}
			current = current.Length == 0 ? Fit(word, width) : $"{current} {word}";
		}
		if (current.Length > 0)
			lines.Add(current);
		return lines;
	}
}