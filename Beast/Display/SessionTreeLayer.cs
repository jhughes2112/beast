using System;
using System.Collections.Generic;


// Builds the F10 session tree overlay Screen as a narrow right-side panel.
// panelW is the panel width; callers blit it at (totalWidth - panelW, 0) so the main
// content remains visible to the left.
internal static class SessionTreeLayer
{
	internal static Screen Build(
		List<SessionDisplayInfo> sessions,
		int                      selected,
		int                      scroll,
		int                      panelW,
		int                      h,
		string                   activeId,
		HashSet<string>          marked,
		HashSet<string>          waiting)
	{
		Rgb panelBg   = new Rgb( 28,  28,  28);
		Rgb borderFg  = new Rgb( 65,  65,  65);
		Rgb headerFg  = new Rgb(138, 138, 138);
		Rgb busyFg    = new Rgb(110, 168, 210);
		Rgb waitingFg = new Rgb(255, 225,  60); // bright yellow — stuck until the user answers
		Rgb selBg     = new Rgb( 55,  55,  55);
		Rgb successFg = new Rgb(130, 170, 210); // soft blue
		Rgb failureFg = new Rgb(210, 140, 140); // muted red
		Rgb openFg    = new Rgb(215, 215, 215); // near white — running, or Play would continue it
		Rgb doneFg    = new Rgb(115, 115, 115); // gray — nothing more happens unless the user writes
		Rgb workingFg = new Rgb(206, 178, 108); // amber — still owes its caller a reply

		Screen s = new Screen(panelW, h, new Cell(' ', headerFg, panelBg, CellStyle.None));

		// Left border column.
		for (int r = 0; r < h; r++)
			AnsiToScreen.WriteLine(s, 0, r, "│", borderFg, panelBg);

		Rgb markFg = new Rgb(230,  90,  90); // marked for deletion

		string header = marked.Count > 0 ? $" Sessions  ({marked.Count} marked · Enter deletes)" : " Sessions  (↑↓ · Enter · Del marks · Esc)";
		AnsiToScreen.WriteLine(s, 1, 0, header, headerFg, panelBg);

		// The header above and the Pause/Play button below take a row each.
		int visibleRows = h - 2;
		for (int r = 0; r < visibleRows; r++)
		{
			int idx = scroll + r;
			if (idx >= sessions.Count)
				break;

			SessionDisplayInfo info     = sessions[idx];
			bool               isSel    = idx == selected;
			bool               isActive = string.Equals(info.Id, activeId, StringComparison.Ordinal);
			Rgb                bg       = isSel ? selBg : panelBg;

			// Name color says whether the session can still go anywhere: a delivered result keeps its
			// termination hue, and anything else reads open or done by whether F9 has something to do.
			// The active row brightens its own color, and selection shows only through the row
			// background — either way the hue stays readable.
			bool done   = ActionFor(info) == PlayAction.None;
			Rgb  nameFg = info.Status switch
			{
				SessionStatus.Success => successFg,
				SessionStatus.Failure => failureFg,
				SessionStatus.Working => done ? doneFg : workingFg,
				_                     => done ? doneFg : openFg
			};
			if (isActive)
				nameFg = nameFg.Scale(1.35f);

			// Fill row background starting after the border column.
			s.Fill(new Rect(1, r + 1, panelW - 1, 1), new Cell(' ', nameFg, bg, CellStyle.None));

			string indent = new string(' ', info.Depth * 2);

			// The first character: ! when it needs an answer from the user, a red X when marked for deletion,
			// the play arrow when running. A stopped session, paused or not, shows nothing.
			bool   isWaiting = waiting.Contains(info.Id);
			bool   isMarked  = marked.Contains(info.Id);
			string marker    = isWaiting ? "! " : isMarked ? "✗ " : SessionTree.IsRunning(info, sessions, waiting) ? "▶ " : "  ";
			Rgb    markerFg  = isWaiting ? waitingFg : isMarked ? markFg : busyFg;

			int col = 1; // start after border
			AnsiToScreen.WriteLine(s, col, r + 1, indent, nameFg, bg);
			col += indent.Length;
			AnsiToScreen.WriteLine(s, col, r + 1, marker, markerFg, bg);
			col += marker.Length;
			AnsiToScreen.WriteLine(s, col, r + 1, info.Name, nameFg, bg);
		}

		// The button acts on the selected session; clicking it is the same as pressing F9. A session F9 could
		// not move on offers nothing.
		if (selected >= 0 && selected < sessions.Count && h > 1)
		{
			PlayAction action = ActionFor(sessions[selected]);
			if (action != PlayAction.None)
				AnsiToScreen.WriteLine(s, 1, h - 1, $" {(action == PlayAction.Pause ? "‖ Pause" : "▶ Play")}  (F9)", busyFg, selBg);
		}

		return s;
	}

	// What F9 does to a session. One at work can be paused, and one finishing its turn under a pause can be
	// let carry on. Once stopped, Play is only offered if the agent says there is a turn left to send.
	internal static PlayAction ActionFor(SessionDisplayInfo session)
	{
		PlayAction action;
		if (session.IsBusy)
			action = session.Paused ? PlayAction.Play : PlayAction.Pause;
		else
			action = session.CanContinue ? PlayAction.Play : PlayAction.None;
		return action;
	}
}

internal enum PlayAction
{
	None,
	Pause,
	Play
}