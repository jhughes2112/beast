using System;
using System.Collections.Generic;


// One line of a ChoicePopup's text. Wrapped lines flow at word boundaries; verbatim lines (a command the
// user is approving) are cut at the box edge instead, with control characters shown rather than obeyed.
internal readonly struct ChoiceLine
{
	public readonly string Text;
	public readonly Rgb?   Fg;
	public readonly bool   Verbatim;

	public ChoiceLine(string text, Rgb? fg, bool verbatim)
	{
		Text     = text;
		Fg       = fg;
		Verbatim = verbatim;
	}
}

// A centered modal question with a row of answers: what to do with the worktree on exit, whether a host
// command may run. Keys: ←/→ (or Tab) and Enter, an answer's hotkey (the letter after '&' in its label,
// else its first letter), or Esc for the escape answer. With an arming delay, keys are ignored for a moment
// after it appears, so a keystroke meant for the input line cannot answer it by accident. The callback runs
// once, after the popup has been taken down.
internal class ChoicePopup
{
	private readonly string           _title;
	private readonly Rgb              _accent;
	private readonly List<ChoiceLine> _lines;
	private readonly List<string>     _labels;
	private readonly List<char>       _hotkeys  = new List<char>();
	private readonly List<int>        _hotkeyAt = new List<int>();
	private readonly int              _escapeIndex;
	private readonly Action<int>      _onChoice;
	private readonly long             _armedAt;
	private          int              _selected;

	// id names the question so its asker can withdraw it (empty when it never will).
	public ChoicePopup(string id, string title, Rgb accent, List<ChoiceLine> lines, List<string> labels, int defaultIndex, int escapeIndex, long armMs, Action<int> onChoice)
	{
		Id           = id;
		_title       = title;
		_accent      = accent;
		_lines       = lines;
		_labels      = new List<string>();
		_selected    = defaultIndex;
		_escapeIndex = escapeIndex;
		_onChoice    = onChoice;
		_armedAt     = Environment.TickCount64 + armMs;
		foreach (string label in labels)
		{
			int    amp   = label.IndexOf('&');
			string plain = amp >= 0 && amp < label.Length - 1 ? label.Remove(amp, 1) : label;
			int    at    = amp >= 0 && amp < label.Length - 1 ? amp : 0;
			_labels.Add(plain);
			_hotkeyAt.Add(at);
			_hotkeys.Add(char.ToLowerInvariant(plain[at]));
		}
	}

	public string Id { get; }

	// Whether keys are still being ignored, so the display knows to redraw once they are not.
	public bool Arming => Environment.TickCount64 < _armedAt;

	// Handles one key. Returns the chosen answer's index, or -1 while the question is still open.
	public int HandleKey(ConsoleKeyInfo key)
	{
		int chosen = -1;
		if (!Arming)
		{
			int hotkey = _hotkeys.IndexOf(char.ToLowerInvariant(key.KeyChar));
			if (key.Key == ConsoleKey.LeftArrow)
				_selected = Math.Max(0, _selected - 1);
			else if (key.Key == ConsoleKey.RightArrow || key.Key == ConsoleKey.Tab)
				_selected = Math.Min(_labels.Count - 1, _selected + 1);
			else if (key.Key == ConsoleKey.Enter)
				chosen = _selected;
			else if (key.Key == ConsoleKey.Escape)
				chosen = _escapeIndex;
			else if (hotkey >= 0 && char.IsLetterOrDigit(key.KeyChar))
				chosen = hotkey;
		}
		return chosen;
	}

	public void Choose(int index)
	{
		_onChoice(index);
	}

	// waiting: how many more questions are queued behind this one.
	public Screen Build(int screenW, int screenH, int waiting)
	{
		int bw     = Math.Min(90, Math.Max(40, screenW - 8));
		int innerW = bw - 4;

		Rgb bg     = new Rgb( 24,  26,  32);
		Rgb textFg = new Rgb(210, 210, 214);
		Rgb dimFg  = new Rgb(140, 140, 140);
		Rgb selBg  = new Rgb( 66,  70,  84);
		Rgb selFg  = new Rgb(250, 250, 250);

		// A verbatim block may take up to a third of the screen; past that it ends in an ellipsis.
		int                         maxVerbatim  = Math.Max(3, screenH / 3);
		List<(string Text, Rgb Fg)> rows         = new List<(string Text, Rgb Fg)>();
		int                         verbatimRows = 0;
		foreach (ChoiceLine line in _lines)
		{
			Rgb          fg     = line.Fg ?? textFg;
			List<string> pieces = line.Text.Length == 0 ? new List<string> { string.Empty } : line.Verbatim ? Chop(line.Text, innerW) : PopupFrame.WrapWords(line.Text, innerW);
			foreach (string piece in pieces)
			{
				if (!line.Verbatim)
					rows.Add((piece, fg));
				else if (verbatimRows < maxVerbatim - 1)
					rows.Add((piece, fg));
				else if (verbatimRows == maxVerbatim - 1)
					rows.Add(("…", fg));
				if (line.Verbatim)
					verbatimRows++;
			}
		}
		rows.Add((string.Empty, textFg));
		int choiceRow = rows.Count;
		rows.Add((string.Empty, textFg));
		if (waiting > 0)
			rows.Add(($"{waiting} more waiting", dimFg));

		int    bh = rows.Count + 2;
		Screen s  = new Screen(bw, bh, new Cell(' ', textFg, bg, CellStyle.None));
		PopupFrame.Draw(s, _title, _accent, _accent, bg);

		// Written cell by cell so nothing in the text (a command's escape sequences) is interpreted.
		for (int i = 0; i < rows.Count; i++)
		{
			for (int k = 0; k < rows[i].Text.Length && 2 + k < bw - 1; k++)
				s.Set(2 + k, i + 1, new Cell(rows[i].Text[k], rows[i].Fg, bg, CellStyle.None));
		}

		// The answers on one row, the selected one highlighted, its hotkey underlined; dim while arming.
		bool armed = !Arming;
		int  x0    = 2;
		for (int c = 0; c < _labels.Count; c++)
		{
			string label    = $" {_labels[c]} ";
			bool   selected = c == _selected;
			Rgb    fg       = !armed ? dimFg : selected ? selFg : textFg;
			for (int k = 0; k < label.Length && x0 + k < bw - 1; k++)
				s.Set(x0 + k, choiceRow + 1, new Cell(label[k], fg, selected ? selBg : bg, k == _hotkeyAt[c] + 1 ? CellStyle.Underline : CellStyle.None));
			x0 += label.Length + 2;
		}
		return s;
	}

	// Cuts a verbatim line at the box edge, every line of it, showing control characters as ¿.
	private static List<string> Chop(string text, int width)
	{
		List<string> rows = new List<string>();
		foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
		{
			char[] visible = line.Replace("\t", "    ").ToCharArray();
			for (int i = 0; i < visible.Length; i++)
			{
				if (char.IsControl(visible[i]))
					visible[i] = '¿';
			}
			string rest = new string(visible);
			while (rest.Length > width)
			{
				rows.Add(rest.Substring(0, width));
				rest = rest.Substring(width);
			}
			rows.Add(rest);
		}
		return rows;
	}
}