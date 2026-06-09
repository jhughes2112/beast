using System;
using System.Collections.Generic;


// Builds the F10 session tree overlay Screen. Disable in Redraw by commenting out the Blit.
internal static class SessionTreeLayer
{
    internal static Screen Build(
        List<SessionDisplayInfo> sessions,
        int selected,
        int scroll,
        int w,
        int h,
        string activeId)
    {
        Rgb panelBg  = new Rgb(28, 28, 28);
        Rgb headerFg = new Rgb(138, 138, 138);
        Rgb busyFg   = new Rgb(80, 180, 255);
        Rgb idleFg   = new Rgb(100, 100, 100);
        Rgb selBg    = new Rgb(55, 55, 55);
        Rgb activeFg = new Rgb(220, 220, 220);

        Screen s = new Screen(w, h, new Cell(' ', headerFg, panelBg, CellStyle.None));

        string header = " Sessions  (↑↓ navigate · Enter/F10 select · Esc cancel)";
        AnsiToScreen.WriteLine(s, 0, 0, header, headerFg, panelBg);

        int visibleRows = h - 1;
        for (int r = 0; r < visibleRows; r++)
        {
            int idx = scroll + r;
            if (idx >= sessions.Count) break;

            SessionDisplayInfo info = sessions[idx];
            bool isSel    = idx == selected;
            bool isActive = string.Equals(info.Id, activeId, StringComparison.Ordinal);
            Rgb bg        = isSel ? selBg : panelBg;
            Rgb dotFg     = info.IsBusy ? busyFg : idleFg;
            Rgb nameFg    = isActive ? activeFg : (isSel ? new Rgb(200, 200, 200) : new Rgb(160, 160, 160));

            s.Fill(new Rect(0, r + 1, w, 1), new Cell(' ', nameFg, bg, CellStyle.None));

            string indent = new string(' ', info.Depth * 2);
            string marker = isActive ? "▶ " : "  ";
            string dot    = info.IsBusy ? "●" : "○";

            int col = 0;
            AnsiToScreen.WriteLine(s, col, r + 1, indent, nameFg, bg);
            col += indent.Length;
            AnsiToScreen.WriteLine(s, col, r + 1, marker, nameFg, bg);
            col += marker.Length;
            AnsiToScreen.WriteLine(s, col, r + 1, dot, dotFg, bg);
            col += 1;
            AnsiToScreen.WriteLine(s, col, r + 1, " " + info.Name, nameFg, bg);
        }

        return s;
    }
}
