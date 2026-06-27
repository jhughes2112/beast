// Terminal column width of a character for the cell grid. The renderer is a fixed grid of one cell per
// column, so a glyph the terminal draws two columns wide — CJK, Hangul, and specific wide symbols — must
// reserve two cells, or everything after it slides one column to the right (the "F10 panel pushed over by
// one" symptom). Zero-width combining marks reserve no column.
// Surrogate code units report width 1; callers replace them with a placeholder, since one Cell holds a
// single UTF-16 char and cannot carry an astral-plane glyph.
//
// Note: the U+2600–U+26FF (misc symbols: ☑ ☀ ⚠ …) and U+2700–U+27BF (dingbats: ✓ ✔ ✖ …) ranges are
// intentionally NOT marked wide. Although Unicode lists some of these as "Wide" (EA width W), that
// classification is for CJK compatibility; in Western terminals they render one column wide. Marking them
// as 2 would inflate row widths, cause premature wrapping, and shift the layout (the bullet/checkmark
// counting bug this module was fixed for).
public static class CharWidth
{
	// 0 = zero-width (combining mark / zero-width space), 2 = wide, 1 = everything else.
	public static int Of(char c)
	{
		if (c < 0x0080)
			return 1;          // ASCII fast path: always single-width
		if (IsZeroWidth(c))
			return 0;
		if (IsWide(c))
			return 2;
		return 1;
	}

	private static bool IsZeroWidth(char c)
	{
		return (c >= 0x0300 && c <= 0x036F)   // combining diacritical marks
			|| (c >= 0x1AB0 && c <= 0x1AFF)   // combining diacritical marks extended
			|| (c >= 0x1DC0 && c <= 0x1DFF)   // combining diacritical marks supplement
			|| (c >= 0x20D0 && c <= 0x20FF)   // combining diacritical marks for symbols
			|| (c >= 0xFE20 && c <= 0xFE2F)   // combining half marks
			|| c == 0x200B || c == 0x200C || c == 0x200D || c == 0x2060 || c == 0xFEFF; // zero-width spaces / joiners / BOM
	}

	private static bool IsWide(char c)
	{
		return (c >= 0x1100 && c <= 0x115F)   // Hangul Jamo
			|| c == 0x2329 || c == 0x232A     // angle brackets 〈 〉
			|| (c >= 0x231A && c <= 0x231B)   // ⌚ ⌛
			|| (c >= 0x23E9 && c <= 0x23FA)   // media control symbols (⏩ ⏪ ⏫ ⏬ ⏰ ⏳ …)
			|| (c >= 0x25FD && c <= 0x25FE)   // ◽ ◾
			// NOTE: U+2600–U+26FF (misc symbols) and U+2700–U+27BF (dingbats) are intentionally excluded —
			// they render single-width in Western terminals despite some being classified "Wide" by Unicode
			// for CJK compatibility. Including them here inflates row widths and breaks wrapping.
			|| (c >= 0x2E80 && c <= 0x303E)   // CJK radicals … Kangxi
			|| (c >= 0x3041 && c <= 0x33FF)   // Hiragana … CJK compatibility
			|| (c >= 0x3400 && c <= 0x4DBF)   // CJK extension A
			|| (c >= 0x4E00 && c <= 0x9FFF)   // CJK unified ideographs
			|| (c >= 0xA000 && c <= 0xA4CF)   // Yi
			|| (c >= 0xAC00 && c <= 0xD7A3)   // Hangul syllables
			|| (c >= 0xF900 && c <= 0xFAFF)   // CJK compatibility ideographs
			|| (c >= 0xFE10 && c <= 0xFE19)   // vertical forms
			|| (c >= 0xFE30 && c <= 0xFE6B)   // CJK compatibility forms / small form variants
			|| (c >= 0xFF00 && c <= 0xFF60)   // fullwidth forms
			|| (c >= 0xFFE0 && c <= 0xFFE6);  // fullwidth signs
	}
}