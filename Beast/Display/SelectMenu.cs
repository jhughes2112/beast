using System;
using System.Collections.Generic;
using System.Text;


// A reusable full-screen arrow-key picker, drawn with the same Screen/ScreenAnsiWriter primitives as the
// main display. Runs standalone (its own alt-screen + input loop), so it can be used before the live
// DisplayScreen exists — e.g. the pre-launch worktree chooser. Navigation: ↑/↓ move (skipping disabled
// rows), Enter selects, Esc cancels. The last row is always "create new", which drops into an inline text
// field pre-filled with a suggestion. Enter there returns the typed value as a new entry.
public static class SelectMenu
{
    // One selectable row. Value is what Choose returns; Label is shown; Note is a dim right-side tag
    // (e.g. "in use"); Disabled rows render dim and cannot be selected.
    public sealed class Item
    {
        public string Label;
        public string Value;
        public string Note;
        public bool Disabled;

        public Item(string label, string value, string note, bool disabled)
        {
            Label = label;
            Value = value;
            Note = note;
            Disabled = disabled;
        }
    }

    public readonly struct Result
    {
        public readonly bool Cancelled;
        public readonly bool IsNew;
        public readonly string Value;

        public Result(bool cancelled, bool isNew, string value)
        {
            Cancelled = cancelled;
            IsNew = isNew;
            Value = value;
        }
    }

    private static readonly Rgb Bg       = new Rgb(20, 20, 24);
    private static readonly Rgb TitleFg  = new Rgb(220, 220, 220);
    private static readonly Rgb HintFg   = new Rgb(120, 120, 120);
    private static readonly Rgb ItemFg   = new Rgb(180, 180, 180);
    private static readonly Rgb SelBg    = new Rgb(50, 80, 120);
    private static readonly Rgb SelFg    = new Rgb(240, 240, 240);
    private static readonly Rgb DisFg    = new Rgb(90, 90, 90);
    private static readonly Rgb NoteFg   = new Rgb(120, 110, 90);
    private static readonly Rgb NewFg    = new Rgb(150, 200, 150);
    private static readonly Rgb FieldBg  = new Rgb(35, 40, 48);
    private static readonly Rgb LaunchFg = new Rgb(240, 220, 140);

    // Runs the menu. initialIndex pre-selects a row (clamped). createLabel is the always-present final row;
    // createSuggestion pre-fills its text field. Returns a Cancelled result on Esc. When launchingNote is
    // non-empty, a confirmed selection draws that note bold at the bottom row and leaves the alt screen up
    // (rather than tearing it down) so the caller can launch behind it and hand off to the live display
    // without the screen flashing back to the normal terminal in between.
    public static Result Choose(string title, IReadOnlyList<Item> items, string createLabel, string createSuggestion, int initialIndex, string launchingNote)
    {
        WindowsConsole.EnableVirtualTerminal();
        Console.Write("\x1b[?1049h");   // alt screen
        Console.Write("\x1b[?7l");      // disable wrap
        Console.CursorVisible = false;

        bool keepAlive = false;
        try
        {
            int createRow = items.Count;          // the synthetic "create new" row index
            int selected = initialIndex;
            if (selected < 0 || selected > createRow) selected = createRow;
            selected = SkipDisabled(items, createRow, selected, 1);

            bool editing = false;
            StringBuilder text = new StringBuilder(createSuggestion ?? string.Empty);
            int lastW = -1, lastH = -1;

            while (true)
            {
                int w = Console.WindowWidth;
                int h = Console.WindowHeight;
                if (w != lastW || h != lastH)
                {
                    Console.Write("\x1b[2J");
                    lastW = w;
                    lastH = h;
                }

                Render(title, items, createLabel, createRow, selected, editing, text.ToString(), w, h);

                ConsoleInputEvent? evOpt = WindowsConsole.ReadInputWithTimeout(50);
                if (evOpt == null) continue;
                ConsoleInputEvent ev = evOpt.Value;
                if (ev.Type != InputEventType.Key) continue;
                ConsoleKeyInfo key = ev.Key;

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
                            return new Result(false, true, typed);
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

                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        return new Result(true, false, string.Empty);

                    case ConsoleKey.UpArrow:
                        selected = SkipDisabled(items, createRow, Wrap(selected - 1, createRow + 1), -1);
                        break;

                    case ConsoleKey.DownArrow:
                        selected = SkipDisabled(items, createRow, Wrap(selected + 1, createRow + 1), 1);
                        break;

                    case ConsoleKey.Enter:
                        if (selected == createRow)
                            editing = true;
                        else if (!items[selected].Disabled)
                        {
                            keepAlive = EnterLaunching(launchingNote, w, h);
                            return new Result(false, false, items[selected].Value);
                        }
                        break;
                }
            }
        }
        finally
        {
            // On a confirmed selection with a launching note, leave the alt screen up so the caller can
            // launch behind the "Launching Sandbox…" footer and hand off to the live display. Otherwise
            // (cancel, or no note) restore the terminal as normal.
            if (!keepAlive)
            {
                Console.CursorVisible = true;
                Console.Write("\x1b[?7h");      // re-enable wrap
                Console.Write("\x1b[?1049l");   // leave alt screen
                Console.Out.Flush();
            }
        }
    }

    // Draws the launching note bold on the bottom row over the current menu, leaving the alt screen up.
    // Returns true (keep the alt screen alive) when a note was drawn, false when there is nothing to show.
    private static bool EnterLaunching(string launchingNote, int w, int h)
    {
        if (string.IsNullOrEmpty(launchingNote))
            return false;

        Screen s = new Screen(w, 1, new Cell(' ', LaunchFg, Bg, CellStyle.None));
        s.WriteText(2, 0, launchingNote, LaunchFg, Bg, CellStyle.Bold);

        StringBuilder buf = new StringBuilder();
        ScreenAnsiWriter.Write(buf, s, h, 1);   // bottom row (1-based terminal row h)
        Console.Write(buf);
        Console.Out.Flush();
        return true;
    }

    // Moves off a disabled row in the given direction (+1/-1), wrapping; the create row is never disabled.
    private static int SkipDisabled(IReadOnlyList<Item> items, int createRow, int start, int dir)
    {
        int idx = start;
        for (int guard = 0; guard <= createRow; guard++)
        {
            if (idx == createRow || !items[idx].Disabled)
                return idx;
            idx = Wrap(idx + dir, createRow + 1);
        }
        return createRow;
    }

    private static int Wrap(int v, int count)
    {
        if (count <= 0) return 0;
        v %= count;
        if (v < 0) v += count;
        return v;
    }

    private static void Render(string title, IReadOnlyList<Item> items, string createLabel, int createRow, int selected, bool editing, string text, int w, int h)
    {
        Screen s = new Screen(w, h, new Cell(' ', ItemFg, Bg, CellStyle.None));

        s.WriteText(2, 1, title, TitleFg, Bg, CellStyle.Bold);
        s.WriteText(2, 2, "↑/↓ select · Enter confirm · Esc cancel", HintFg, Bg, CellStyle.None);

        int row = 4;
        for (int i = 0; i < items.Count && row < h - 1; i++, row++)
        {
            DrawRow(s, row, w, items[i].Label, items[i].Note, items[i].Disabled, i == selected && !editing);
        }

        // The create-new row, and its inline text field when editing.
        if (row < h - 1)
        {
            bool selCreate = selected == createRow && !editing;
            if (editing)
            {
                s.Fill(new Rect(0, row, w, 1), new Cell(' ', NewFg, FieldBg, CellStyle.None));
                int x = s.WriteText(2, row, createLabel + ": ", NewFg, FieldBg, CellStyle.None);
                x = s.WriteText(x, row, text, SelFg, FieldBg, CellStyle.None);
                s.WriteText(x, row, "_", SelFg, FieldBg, CellStyle.Bold);
            }
            else
            {
                Rgb bg = selCreate ? SelBg : Bg;
                Rgb fg = selCreate ? SelFg : NewFg;
                s.Fill(new Rect(0, row, w, 1), new Cell(' ', fg, bg, CellStyle.None));
                s.WriteText(2, row, (selCreate ? "▶ " : "  ") + "＋ " + createLabel, fg, bg, CellStyle.None);
            }
        }

        StringBuilder buf = new StringBuilder();
        ScreenAnsiWriter.Write(buf, s, 1, 1);
        Console.Write(buf);
    }

    private static void DrawRow(Screen s, int row, int w, string label, string note, bool disabled, bool selected)
    {
        Rgb bg = selected ? SelBg : Bg;
        Rgb fg = disabled ? DisFg : (selected ? SelFg : ItemFg);
        s.Fill(new Rect(0, row, w, 1), new Cell(' ', fg, bg, CellStyle.None));

        string marker = selected ? "▶ " : "  ";
        s.WriteText(2, row, marker + label, fg, bg, CellStyle.None);

        if (!string.IsNullOrEmpty(note))
        {
            string tag = "(" + note + ")";
            int x = w - tag.Length - 2;
            if (x > 2)
                s.WriteText(x, row, tag, NoteFg, bg, CellStyle.None);
        }
    }
}
