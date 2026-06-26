using System;
using System.Collections.Generic;
using System.Text;


// Builds input area and completion popup Screens; hosts all pure input utilities (cursor math, word
// navigation, paste handling, completions matching). Disable input/popup in Redraw via the Blit calls.
internal static class InputLayer
{
	internal const string PromptPrefix = "» ";

	// Builds the input text area with ghost-text completion preview.
	internal static Screen Build(string text, int w, int inputRows, int skip, string ghostSuffix)
	{
		Screen s = new Screen(w, inputRows, new Cell(' ', DisplayScreen.Palette.InputFg, DisplayScreen.Palette.InputBg, CellStyle.None));
		List<string> inputLines = WrapInput(text, w);
		for (int r = 0; r < inputRows; r++)
		{
			int lineIdx = skip + r;
			string line = lineIdx < inputLines.Count ? inputLines[lineIdx] : PromptPrefix;
			(int endX, Rgb? _, Rgb? _) = AnsiToScreen.WriteLine(s, 0, r, line, DisplayScreen.Palette.InputFg, DisplayScreen.Palette.InputBg);
			AnsiToScreen.PadRowBackground(s, endX, r, DisplayScreen.Palette.InputFg, DisplayScreen.Palette.InputBg);

			if (!string.IsNullOrEmpty(ghostSuffix) && lineIdx == inputLines.Count - 1)
			{
				int gx = endX;
				for (int i = 0; i < ghostSuffix.Length && gx < w; i++, gx++)
					s.Set(gx, r, new Cell(ghostSuffix[i], DisplayScreen.Palette.GhostFg, DisplayScreen.Palette.InputBg, CellStyle.None));
			}
		}
		return s;
	}

	// Builds the slash-command completion popup above the input area.
	internal static Screen BuildCompletionPopup(int w, int rows, List<string> matches, int selected)
	{
		Screen s = new Screen(w, rows, new Cell(' ', DisplayScreen.Palette.InputFg, DisplayScreen.Palette.InputBg, CellStyle.None));

		int total = matches.Count;
		int first = 0;
		if (total > rows)
		{
			first = selected - rows / 2;
			if (first < 0)
				first = 0;
			if (first > total - rows)
				first = total - rows;
		}

		for (int r = 0; r < rows; r++)
		{
			int idx = first + r;
			if (idx >= total)
				break;
			bool isSel = idx == selected;
			Rgb bg = isSel ? DisplayScreen.Palette.PopupSelBg : DisplayScreen.Palette.InputBg;
			string line = "  " + matches[idx];
			s.Fill(new Rect(0, r, w, 1), new Cell(' ', DisplayScreen.Palette.InputFg, bg, CellStyle.None));
			(int endX, Rgb? _, Rgb? _) = AnsiToScreen.WriteLine(s, 0, r, line, DisplayScreen.Palette.InputFg, bg);
			AnsiToScreen.PadRowBackground(s, endX, r, DisplayScreen.Palette.InputFg, bg);
		}
		return s;
	}

	// Wraps input text into screen lines, applying PromptPrefix on the first logical line.
	internal static List<string> WrapInput(string text, int w)
	{
		List<string> result = new List<string>();
		string[] logicalLines = text.Split('\n');
		for (int li = 0; li < logicalLines.Length; li++)
		{
			string prefix = li == 0 ? PromptPrefix : "  ";
			string full = prefix + ExpandTabs(logicalLines[li]);
			if (full.Length == 0)
			{ result.Add(prefix); continue; }
			foreach (string wl in AnsiString.Wrap(full, w))
				result.Add(wl);
		}
		if (result.Count == 0)
			result.Add(PromptPrefix);
		return result;
	}

	internal static int ComputeInputRows(string text, int w) => WrapInput(text, w).Count;

	// Maps a character offset into the input string to (screen line index, column).
	internal static (int LineIdx, int Col) CursorInInputLines(string text, int cursor, int w)
	{
		string[] logicalLines = text.Split('\n');
		int remaining = cursor;
		int screenLine = 0;

		for (int li = 0; li < logicalLines.Length; li++)
		{
			string prefix = li == 0 ? PromptPrefix : "  ";
			int logLen = logicalLines[li].Length;

			if (remaining <= logLen || li == logicalLines.Length - 1)
			{
				int colInFull = prefix.Length + remaining;
				return (screenLine + colInFull / w, colInFull % w);
			}

			remaining -= logLen + 1;
			int fullLen = prefix.Length + logicalLines[li].Length;
			screenLine += Math.Max(1, (int)Math.Ceiling((double)fullLen / w));
		}

		return (screenLine, 0);
	}

	// Maps a (screen line, column) position back to a character offset in the input string.
	internal static int CharFromInputLines(string text, int targetLine, int targetCol, int w)
	{
		string[] logicalLines = text.Split('\n');
		int screenLine = 0;
		int charPos = 0;

		for (int li = 0; li < logicalLines.Length; li++)
		{
			string prefix = li == 0 ? PromptPrefix : "  ";
			int fullLen = prefix.Length + logicalLines[li].Length;
			int wrappedRows = Math.Max(1, (int)Math.Ceiling((double)fullLen / w));

			if (screenLine + wrappedRows > targetLine)
			{
				int rowWithin = targetLine - screenLine;
				int colInFull = Math.Min(rowWithin * w + targetCol, fullLen);
				int chars = Math.Max(0, colInFull - prefix.Length);
				return charPos + chars;
			}

			screenLine += wrappedRows;
			charPos += logicalLines[li].Length + (li < logicalLines.Length - 1 ? 1 : 0);
		}

		return text.Length;
	}

	// Moves the cursor to the start of the previous word.
	internal static int WordStartBefore(string text, int pos)
	{
		int i = pos - 1;
		while (i > 0 && text[i - 1] != ' ' && text[i - 1] != '\n')
			i--;
		return i < 0 ? 0 : i;
	}

	// Moves the cursor past the next word.
	internal static int WordEndAfter(string text, int pos)
	{
		int i = pos;
		while (i < text.Length && (text[i] == ' ' || text[i] == '\n'))
			i++;
		while (i < text.Length && text[i] != ' ' && text[i] != '\n')
			i++;
		return i;
	}

	// Builds the text to insert at the cursor for a paste event. Large pastes become a placeholder
	// that is expanded back to full content when the line is committed.
	internal static string BuildPasteInsert(string content, Dictionary<string, string> pasteBuffers, ref int pasteSeq)
	{
		if (content.Length < 256)
			return content;

		int byteCount = Encoding.UTF8.GetByteCount(content);
		string placeholder = $"[Pasted {byteCount} bytes from clipboard #{++pasteSeq}]";
		pasteBuffers[placeholder] = content;
		return placeholder;
	}

	// Rebuilds the completion match list for the current input prefix.
	internal static void UpdateMatches(string input, List<string> matches, List<string> completions)
	{
		matches.Clear();
		if (!input.StartsWith("/", StringComparison.Ordinal))
			return;

		foreach (string completion in completions)
		{
			if (completion.StartsWith(input, StringComparison.OrdinalIgnoreCase))
				matches.Add(completion);
		}
	}

	private static string ExpandTabs(string text) => text.Replace("\t", "    ");
}