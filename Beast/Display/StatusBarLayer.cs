

// Builds the 1-row status bar Screen. Disable in Redraw by commenting out the Blit.
internal static class StatusBarLayer
{
	// Three-segment layout: left flush-left, right flush-right, center centered and nudged to avoid overlap.
	internal static Screen Build(string left, string center, string right, int w)
	{
		Screen s = new Screen(w, 1, new Cell(' ', DisplayScreen.Palette.MedGrey, null, CellStyle.None));

		int leftLen   = AnsiString.VisibleLength(left);
		int centerLen = AnsiString.VisibleLength(center);
		int rightLen  = AnsiString.VisibleLength(right);

		if (leftLen > 0)
			AnsiToScreen.WriteLine(s, 0, 0, left, DisplayScreen.Palette.MedGrey, null);

		int rightCol = w - rightLen;
		if (rightLen > 0 && rightCol >= 0)
			AnsiToScreen.WriteLine(s, rightCol, 0, right, DisplayScreen.Palette.MedGrey, null);

		if (centerLen > 0)
		{
			int centerCol = (w - centerLen) / 2;
			int minCol    = leftLen > 0 ? leftLen + 1 : 0;
			int maxCol    = rightLen > 0 ? rightCol - 1 - centerLen : w - centerLen;
			if (centerCol < minCol)
				centerCol = minCol;
			if (centerCol > maxCol)
				centerCol = maxCol;
			if (centerCol >= 0 && centerCol + centerLen <= w)
				AnsiToScreen.WriteLine(s, centerCol, 0, center, DisplayScreen.Palette.MedGrey, null);
		}

		return s;
	}
}