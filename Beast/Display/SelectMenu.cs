using System;
using System.Collections.Generic;
using System.Text;


// A reusable full-screen arrow-key picker, drawn with the same Screen/ScreenAnsiWriter primitives as the
// main display. Runs standalone (its own alt-screen + input loop), so it can be used before the live
// DisplayScreen exists — e.g. the pre-launch session chooser. Navigation: ↑/↓ move over the rows (headers
// are skipped), Enter picks one (disabled rows cannot be picked), Esc cancels. Any other key, and Enter
// first, goes to the caller's key handler, which may act on the row (asking with Ask if it needs to) and
// report back on the status line. A row marked IsCreate drops into an inline text field pre-filled with a
// suggestion; Enter there returns the typed value as a new entry.
public static class SelectMenu
{
	// One row. Value is what Choose returns; Label is shown; Note is a dim right-side tag (e.g. "stopped").
	// Disabled rows render dim and cannot be picked, but the cursor still reaches them so the key handler
	// can act on them. A header is a dim section title the cursor passes over. IsCreate makes the row the
	// "create new" text field. Tag is the caller's own.
	public sealed class Item
	{
		public string  Label;
		public string  Value;
		public string  Note;
		public bool    Disabled;
		public bool    IsCreate;
		public bool    IsHeader;
		public object? Tag;

		public Item(string label, string value, string note, bool disabled)
		{
			Label    = label;
			Value    = value;
			Note     = note;
			Disabled = disabled;
		}
	}

	public readonly struct Result
	{
		public readonly bool   Cancelled;
		public readonly bool   IsNew;
		public readonly string Value;

		public Result(bool cancelled, bool isNew, string value)
		{
			Cancelled = cancelled;
			IsNew     = isNew;
			Value     = value;
		}
	}

	// What the key handler did with a key: Status is shown on the status line (red when IsError), Remove
	// takes the row out of the menu; otherwise the handler has updated the row in place.
	public readonly struct KeyResult
	{
		public readonly string Status;
		public readonly bool   IsError;
		public readonly bool   Remove;

		public KeyResult(string status, bool isError, bool remove)
		{
			Status  = status;
			IsError = isError;
			Remove  = remove;
		}
	}

	private static readonly Rgb Bg       = new Rgb( 20,  20,  24);
	private static readonly Rgb TitleFg  = new Rgb(220, 220, 220);
	private static readonly Rgb HintFg   = new Rgb(120, 120, 120);
	private static readonly Rgb ItemFg   = new Rgb(180, 180, 180);
	private static readonly Rgb SelBg    = new Rgb( 50,  80, 120);
	private static readonly Rgb SelFg    = new Rgb(240, 240, 240);
	private static readonly Rgb DisFg    = new Rgb( 90,  90,  90);
	private static readonly Rgb DisSelFg = new Rgb(150, 150, 150);
	private static readonly Rgb HeaderFg = new Rgb(140, 150, 170);
	private static readonly Rgb NoteFg   = new Rgb(120, 110,  90);
	private static readonly Rgb NewFg    = new Rgb(150, 200, 150);
	private static readonly Rgb FieldBg  = new Rgb( 35,  40,  48);
	private static readonly Rgb LaunchFg = new Rgb(240, 220, 140);
	private static readonly Rgb ErrorFg  = new Rgb(214, 102, 102);
	private static readonly Rgb InfoFg   = new Rgb(150, 190, 150);

	// The menu as last drawn, so Ask and ShowStatus can draw over it from inside the key handler.
	private static Screen? _drawn;

	// Runs the menu. initialIndex pre-selects a row (moved off a header). createSuggestion pre-fills the
	// create row's text field. onKey, when given, sees Enter and every key the menu does not use itself, on
	// the selected row; it returns null for a key it leaves alone (Enter then picks the row as usual). Returns
	// a Cancelled result on Esc. When launchingNote is non-empty, a confirmed selection draws that note bold at
	// the bottom row and leaves the alt screen up (rather than tearing it down) so the caller can launch
	// behind it and hand off to the live display without the screen flashing back to the normal terminal.
	public static Result Choose(string title, string hint, List<Item> items, string createSuggestion, int initialIndex, string launchingNote, Func<Item, ConsoleKeyInfo, KeyResult?>? onKey)
	{
		Platform.Current.EnterTerminalModes();
		Console.Write("\x1b[?1049h"); // alt screen
		Console.Write("\x1b[?7l");    // disable wrap
		Console.CursorVisible = false;

		bool   keepAlive = false;
		Result result    = new Result(true, false, string.Empty);
		try
		{
			int           selected = Step(items, Math.Clamp(initialIndex, 0, Math.Max(0, items.Count - 1)) - 1, 1);
			bool          editing  = false;
			bool          done     = false;
			string        status   = string.Empty;
			bool          isError  = false;
			StringBuilder text     = new StringBuilder(createSuggestion ?? string.Empty);
			int           lastW    = -1;
			int           lastH    = -1;

			while (!done)
			{
				int w = Console.WindowWidth;
				int h = Console.WindowHeight;
				if (w != lastW || h != lastH)
				{
					Console.Write("\x1b[2J");
					lastW = w;
					lastH = h;
				}

				_drawn = Render(title, hint, items, selected, editing, text.ToString(), status, isError, w, h);
				Write(_drawn);

				ConsoleInputEvent? evOpt = Platform.Current.ReadInput(50);
				if (evOpt == null || evOpt.Value.Type != InputEventType.Key)
					continue;
				ConsoleKeyInfo key = evOpt.Value.Key;

				// Ctrl+C leaves the menu, even mid-edit. While beast reads the keyboard it arrives as a key on
				// every OS (raw mode on macOS/Linux, processed input off on Windows), not as a signal.
				if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
				{
					done = true;
					continue;
				}

				if (editing)
				{
					if (key.Key == ConsoleKey.Escape)
					{
						editing = false;
					}
					else if (key.Key == ConsoleKey.Enter)
					{
						string typed = text.ToString().Trim();
						if (typed.Length > 0)
						{
							keepAlive = EnterLaunching(launchingNote, w, h);
							result    = new Result(false, true, typed);
							done      = true;
						}
					}
					else if (key.Key == ConsoleKey.Backspace)
					{
						if (text.Length > 0)
							text.Remove(text.Length - 1, 1);
					}
					else if (!char.IsControl(key.KeyChar))
					{
						text.Append(key.KeyChar);
					}
					continue;
				}

				status      = string.Empty;
				isError     = false;
				bool hasRow = selected >= 0 && selected < items.Count && !items[selected].IsHeader;
				if (key.Key == ConsoleKey.Escape)
				{
					done = true;
				}
				else if (key.Key == ConsoleKey.UpArrow)
				{
					selected = Step(items, selected, -1);
				}
				else if (key.Key == ConsoleKey.DownArrow)
				{
					selected = Step(items, selected, 1);
				}
				else if (hasRow && key.Key == ConsoleKey.Enter && items[selected].IsCreate)
				{
					editing = true;
				}
				else if (hasRow)
				{
					KeyResult? handled = onKey?.Invoke(items[selected], key);
					if (handled.HasValue)
					{
						status  = handled.Value.Status;
						isError = handled.Value.IsError;
						if (handled.Value.Remove)
						{
							items.RemoveAt(selected);
							selected = Step(items, Math.Min(selected, items.Count) - 1, 1);
						}
					}
					else if (key.Key == ConsoleKey.Enter && !items[selected].Disabled)
					{
						keepAlive = EnterLaunching(launchingNote, w, h);
						result    = new Result(false, false, items[selected].Value);
						done      = true;
					}
				}
			}
		}
		finally
		{
			_drawn = null;
			// On a confirmed selection with a launching note, leave the alt screen up so the caller can
			// launch behind the "Launching Sandbox…" footer and hand off to the live display. Otherwise
			// (cancel, or no note) restore the terminal as normal.
			if (!keepAlive)
			{
				Console.CursorVisible = true;
				Console.Write("\x1b[?7h");    // re-enable wrap
				Console.Write("\x1b[?1049l"); // leave alt screen
				Console.Out.Flush();
			}

			// Nothing reads the keyboard while the agent launches, so hand Ctrl+C back to the terminal: it
			// becomes a signal again, which BeastApp turns into abandoning the launch. The live display takes
			// the keyboard back when it starts.
			Platform.Current.RestoreTerminalModes();
		}
		return result;
	}

	// Asks a question over the menu, from inside the key handler: a centered popup with a row of answers
	// (the same ChoicePopup the live display uses). Returns the chosen answer's index; Esc gives escapeIndex.
	public static int Ask(string title, List<string> lines, List<string> labels, int defaultIndex, int escapeIndex)
	{
		List<ChoiceLine> text = new List<ChoiceLine>();
		foreach (string line in lines)
			text.Add(new ChoiceLine(line, null, false));
		ChoicePopup popup  = new ChoicePopup(string.Empty, title, new Rgb(130, 150, 190), text, labels, defaultIndex, escapeIndex, 0, _ => { });
		int         chosen = -1;
		while (chosen < 0)
		{
			int    w     = Console.WindowWidth;
			int    h     = Console.WindowHeight;
			Screen frame = new Screen(w, h, new Cell(' ', ItemFg, Bg, CellStyle.None));
			if (_drawn != null)
				frame.Blit(_drawn, 0, 0, BlendMode.Replace, null);
			Screen box = popup.Build(w, h, 0);
			frame.Blit(box, Math.Max(0, (w - box.W) / 2), Math.Max(0, (h - box.H) / 2), BlendMode.Replace, null);
			Write(frame);

			ConsoleInputEvent? evOpt = Platform.Current.ReadInput(50);
			if (evOpt != null && evOpt.Value.Type == InputEventType.Key)
			{
				ConsoleKeyInfo key = evOpt.Value.Key;
				// Ctrl+C backs out of the question like Esc; another one leaves the menu.
				if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
					chosen = escapeIndex;
				else
					chosen = popup.HandleKey(key);
			}
		}
		if (_drawn != null)
			Write(_drawn);
		return chosen;
	}

	// Shows a note on the status line straight away, for a slow action the key handler is about to take.
	public static void ShowStatus(string text)
	{
		if (_drawn != null)
		{
			int row = _drawn.H - 2;
			_drawn.Fill(new Rect(0, row, _drawn.W, 1), new Cell(' ', InfoFg, Bg, CellStyle.None));
			_drawn.WriteText(2, row, text, InfoFg, Bg, CellStyle.None);
			Write(_drawn);
		}
	}

	// Draws the launching note bold on the bottom row over the current menu, leaving the alt screen up.
	// Returns true (keep the alt screen alive) when a note was drawn, false when there is nothing to show.
	private static bool EnterLaunching(string launchingNote, int w, int h)
	{
		bool shown = false;
		if (!string.IsNullOrEmpty(launchingNote))
		{
			Screen s = new Screen(w, 1, new Cell(' ', LaunchFg, Bg, CellStyle.None));
			s.WriteText(2, 0, launchingNote, LaunchFg, Bg, CellStyle.Bold);

			StringBuilder buf = new StringBuilder();
			ScreenAnsiWriter.Write(buf, s, h, 1); // bottom row (1-based terminal row h)
			Console.Write(buf);
			Console.Out.Flush();
			shown = true;
		}
		return shown;
	}

	// The next row from `from` in direction dir that is not a header, wrapping around; from itself when
	// every row is a header (or there are none).
	private static int Step(List<Item> items, int from, int dir)
	{
		int  next  = from;
		int  count = items.Count;
		bool found = false;
		for (int i = 1; i <= count && !found; i++)
		{
			int candidate = ((from + dir * i) % count + count) % count;
			if (!items[candidate].IsHeader)
			{
				next  = candidate;
				found = true;
			}
		}
		return Math.Clamp(next, 0, Math.Max(0, count - 1));
	}

	private static void Write(Screen s)
	{
		StringBuilder buf = new StringBuilder();
		ScreenAnsiWriter.Write(buf, s, 1, 1);
		Console.Write(buf);
	}

	private static Screen Render(string title, string hint, List<Item> items, int selected, bool editing, string text, string status, bool isError, int w, int h)
	{
		Screen s = new Screen(w, h, new Cell(' ', ItemFg, Bg, CellStyle.None));

		s.WriteText(2, 1, title, TitleFg, Bg, CellStyle.Bold);
		s.WriteText(2, 2,  hint,  HintFg, Bg, CellStyle.None);

		// Scrolls so the selected row stays on screen.
		int rows  = Math.Max(1,               h - 6);
		int first = Math.Max(0, selected - rows + 1);
		int row   = 4;
		for (int i = first; i < items.Count && row < h - 2; i++, row++)
		{
			Item item = items[i];
			if (item.IsHeader)
			{
				s.WriteText(2, row, item.Label, HeaderFg, Bg, CellStyle.Bold);
			}
			else if (item.IsCreate && editing)
			{
				s.Fill(new Rect(0, row, w, 1), new Cell(' ', NewFg, FieldBg, CellStyle.None));
				int x = s.WriteText(2, row, item.Label + ": ", NewFg, FieldBg, CellStyle.None);
				x     = s.WriteText(x, row, text, SelFg, FieldBg, CellStyle.None);
				s.WriteText(x, row, "_", SelFg, FieldBg, CellStyle.Bold);
			}
			else if (item.IsCreate)
			{
				bool sel = i == selected;
				Rgb  bg  = sel ? SelBg : Bg;
				Rgb  fg  = sel ? SelFg : NewFg;
				s.Fill(new Rect(0, row, w, 1), new Cell(' ', fg, bg, CellStyle.None));
				s.WriteText(2, row, (sel ? "▶ " : "  ") + "＋ " + item.Label, fg, bg, CellStyle.None);
			}
			else
			{
				DrawRow(s, row, w, item.Label, item.Note, item.Disabled, i == selected && !editing);
			}
		}

		if (status.Length > 0)
			s.WriteText(2, h - 2, status, isError ? ErrorFg : InfoFg, Bg, CellStyle.None);
		return s;
	}

	private static void DrawRow(Screen s, int row, int w, string label, string note, bool disabled, bool selected)
	{
		Rgb bg = selected ? SelBg : Bg;
		Rgb fg = disabled ? (selected ? DisSelFg : DisFg) : (selected ? SelFg : ItemFg);
		s.Fill(new Rect(0, row, w, 1), new Cell(' ', fg, bg, CellStyle.None));

		string marker = selected ? "▶ " : "  ";
		s.WriteText(2, row, marker + label, fg, bg, CellStyle.None);

		if (!string.IsNullOrEmpty(note))
		{
			string tag = "(" + note + ")";
			int    x   = w - tag.Length - 2;
			if (x > 2)
				s.WriteText(x, row, tag, NoteFg, bg, CellStyle.None);
		}
	}
}