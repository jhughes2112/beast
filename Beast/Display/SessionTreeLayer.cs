using System;
using System.Collections.Generic;


// Builds the F10 session tree overlay Screen as a narrow right-side panel.
// panelW is the panel width; callers blit it at (totalWidth - panelW, 0) so the main
// content remains visible to the left.
internal static class SessionTreeLayer
{
    internal static Screen Build(
        List<SessionDisplayInfo> sessions,
        int selected,
        int scroll,
        int panelW,
        int h,
        string activeId)
    {
        Rgb panelBg  = new Rgb(28, 28, 28);
        Rgb borderFg = new Rgb(65, 65, 65);
        Rgb headerFg = new Rgb(138, 138, 138);
        Rgb busyFg   = new Rgb(110, 168, 210);
        Rgb idleFg   = new Rgb(100, 100, 100);
        Rgb selBg    = new Rgb(55, 55, 55);
        Rgb activeFg = new Rgb(220, 220, 220);

        Screen s = new Screen(panelW, h, new Cell(' ', headerFg, panelBg, CellStyle.None));

        // Left border column.
        for (int r = 0; r < h; r++)
            AnsiToScreen.WriteLine(s, 0, r, "│", borderFg, panelBg);

        string header = " Sessions  (↑↓ · Enter · Del · Esc)";
        AnsiToScreen.WriteLine(s, 1, 0, header, headerFg, panelBg);

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

            // Fill row background starting after the border column.
            s.Fill(new Rect(1, r + 1, panelW - 1, 1), new Cell(' ', nameFg, bg, CellStyle.None));

            string indent = new string(' ', info.Depth * 2);
            string marker = isActive ? "▶ " : "  ";
            string dot    = info.IsBusy ? "●" : "○";

            int col = 1; // start after border
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
