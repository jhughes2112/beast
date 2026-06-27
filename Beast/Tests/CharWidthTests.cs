// Unit tests for CharWidth — terminal column width calculations.
// Validates that each codepoint returns the expected width (0 = zero-width, 1 = single-width, 2 = wide).
public static class CharWidthTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  CharWidthTests");

		TestAscii(ctx);
		TestHangulJamow(ctx);
		TestHangulSyllables(ctx);
		TestCJK(ctx);
		TestYi(ctx);
		TestCJKRadicals(ctx);
		TestHiragana(ctx);
		TestCJKCompatibility(ctx);
		TestCJKCompatibilityIdeographs(ctx);
		TestVerticalForms(ctx);
		TestCJKCompatibilityForms(ctx);
		TestFullwidthForms(ctx);
		TestFullwidthSigns(ctx);
		TestSpecificWideSymbols(ctx);
		TestMiscSymbols(ctx);
		TestDingbats(ctx);
		TestStar(ctx);
		TestCopyGlyph(ctx);
		TestZeroWidth(ctx);
		TestSurrogates(ctx);
		TestWideEmoji(ctx);
		TestAnsiStringCharWidthConsistency(ctx);
	}

	private static void AssertWidth(TestContext ctx, char c, int expected, string testName)
	{
		int actual = CharWidth.Of(c);
		ctx.AssertEqual(expected, actual, testName);
	}

	private static void TestAscii(TestContext ctx)
	{
		AssertWidth(ctx, 'A', 1, "ASCII: 'A' is single-width");
		AssertWidth(ctx, 'z', 1, "ASCII: 'z' is single-width");
		AssertWidth(ctx, '0', 1, "ASCII: '0' is single-width");
		AssertWidth(ctx, ' ', 1, "ASCII: space is single-width");
		AssertWidth(ctx, '\n', 1, "ASCII: newline is single-width");
		AssertWidth(ctx, '\t', 1, "ASCII: tab is single-width");
	}

	private static void TestHangulJamow(TestContext ctx)
	{
		// Hangul Jamo (U+1100–U+115F)
		AssertWidth(ctx, (char)0x1100, 2, "Hangul Jamo: U+1100 is wide");
		AssertWidth(ctx, (char)0x115F, 2, "Hangul Jamo: U+115F is wide");
	}

	private static void TestHangulSyllables(TestContext ctx)
	{
		// Hangul syllables (U+AC00–U+D7A3)
		AssertWidth(ctx, (char)0xAC00, 2, "Hangul syllables: U+AC00 (가) is wide");
		AssertWidth(ctx, (char)0xD7A3, 2, "Hangul syllables: U+D7A3 (힣, last) is wide");
		// Just beyond the range — should be single-width.
		AssertWidth(ctx, (char)0xD7A4, 1, "Hangul syllables: U+D7A4 (past end) is single-width");
	}

	private static void TestCJK(TestContext ctx)
	{
		// CJK unified ideographs (U+4E00–U+9FFF)
		AssertWidth(ctx, (char)0x4E00, 2, "CJK: U+4E00 (一) is wide");
		AssertWidth(ctx, (char)0x9FFF, 2, "CJK: U+9FFF is wide");
	}

	private static void TestYi(TestContext ctx)
	{
		// Yi (U+A000–U+A4CF)
		AssertWidth(ctx, (char)0xA000, 2, "Yi: U+A000 is wide");
		AssertWidth(ctx, (char)0xA4CF, 2, "Yi: U+A4CF is wide");
	}

	private static void TestCJKRadicals(TestContext ctx)
	{
		// CJK radicals (U+2E80–U+303E)
		AssertWidth(ctx, (char)0x2E80, 2, "CJK radicals: U+2E80 is wide");
		AssertWidth(ctx, (char)0x303E, 2, "CJK radicals: U+303E is wide");
	}

	private static void TestHiragana(TestContext ctx)
	{
		// Hiragana (U+3041–U+33FF)
		AssertWidth(ctx, (char)0x3041, 2, "Hiragana: U+3041 is wide");
		AssertWidth(ctx, (char)0x33FF, 2, "Hiragana: U+33FF is wide");
	}

	private static void TestCJKCompatibility(TestContext ctx)
	{
		// CJK extension A (U+3400–U+4DBF)
		AssertWidth(ctx, (char)0x3400, 2, "CJK ext A: U+3400 is wide");
		AssertWidth(ctx, (char)0x4DBF, 2, "CJK ext A: U+4DBF is wide");
	}

	private static void TestCJKCompatibilityIdeographs(TestContext ctx)
	{
		// CJK compatibility ideographs (U+F900–U+FAFF)
		AssertWidth(ctx, (char)0xF900, 2, "CJK compat: U+F900 is wide");
		AssertWidth(ctx, (char)0xFAFF, 2, "CJK compat: U+FAFF is wide");
	}

	private static void TestVerticalForms(TestContext ctx)
	{
		// Vertical forms (U+FE10–U+FE19)
		AssertWidth(ctx, (char)0xFE10, 2, "Vertical forms: U+FE10 is wide");
		AssertWidth(ctx, (char)0xFE19, 2, "Vertical forms: U+FE19 is wide");
	}

	private static void TestCJKCompatibilityForms(TestContext ctx)
	{
		// CJK compatibility forms (U+FE30–U+FE6B)
		AssertWidth(ctx, (char)0xFE30, 2, "CJK compat forms: U+FE30 is wide");
		AssertWidth(ctx, (char)0xFE6B, 2, "CJK compat forms: U+FE6B is wide");
	}

	private static void TestFullwidthForms(TestContext ctx)
	{
		// Fullwidth forms (U+FF00–U+FF60)
		AssertWidth(ctx, (char)0xFF00, 2, "Fullwidth: U+FF00 (fullwidth space) is wide");
		AssertWidth(ctx, (char)0xFF01, 2, "Fullwidth: U+FF01 (fullwidth !) is wide");
		AssertWidth(ctx, (char)0xFF60, 2, "Fullwidth: U+FF60 is wide");
	}

	private static void TestFullwidthSigns(TestContext ctx)
	{
		// Fullwidth signs (U+FFE0–U+FFE6)
		AssertWidth(ctx, (char)0xFFE0, 2, "Fullwidth signs: U+FFE0 is wide");
		AssertWidth(ctx, (char)0xFFE6, 2, "Fullwidth signs: U+FFE6 is wide");
	}

	private static void TestSpecificWideSymbols(TestContext ctx)
	{
		// Angle brackets
		AssertWidth(ctx, (char)0x2329, 2, "Angle bracket left 〈 is wide");
		AssertWidth(ctx, (char)0x232A, 2, "Angle bracket right 〉 is wide");
		// Clocks
		AssertWidth(ctx, (char)0x231A, 2, "Clock: U+231A is wide");
		AssertWidth(ctx, (char)0x231B, 2, "Clock: U+231B is wide");
		// Media controls
		AssertWidth(ctx, (char)0x23E9, 2, "Media: U+23E9 is wide");
		AssertWidth(ctx, (char)0x23FA, 2, "Media: U+23FA is wide");
		// Geometric shapes
		AssertWidth(ctx, (char)0x25FD, 2, "Geometric: U+25FD is wide");
		AssertWidth(ctx, (char)0x25FE, 2, "Geometric: U+25FE is wide");
	}

	private static void TestMiscSymbols(TestContext ctx)
	{
		// U+2600–U+26FF misc symbols should be wide
		AssertWidth(ctx, (char)0x2600, 2, "Misc symbols: U+2600 (☃ snowman) is wide");
		AssertWidth(ctx, (char)0x2611, 2, "Misc symbols: U+2611 (☑ ballot box) is wide");
		AssertWidth(ctx, (char)0x2603, 2, "Misc symbols: U+2603 (snowman) is wide");
		AssertWidth(ctx, (char)0x261E, 2, "Misc symbols: U+261E (white pointing hand) is wide");
		AssertWidth(ctx, (char)0x2660, 2, "Misc symbols: U+2660 (spade) is wide");
		AssertWidth(ctx, (char)0x2665, 2, "Misc symbols: U+2665 (heart) is wide");
		AssertWidth(ctx, (char)0x267F, 2, "Misc symbols: U+267F (wheelchair) is wide");
	}

	private static void TestDingbats(TestContext ctx)
	{
		// U+2700–U+27BF dingbats should be wide
		AssertWidth(ctx, (char)0x2700, 2, "Dingbats: U+2700 is wide");
		AssertWidth(ctx, (char)0x2713, 2, "Dingbats: U+2713 (✓ checkmark) is wide");
		AssertWidth(ctx, (char)0x2714, 2, "Dingbats: U+2714 (✔ heavy check) is wide");
		AssertWidth(ctx, (char)0x2716, 2, "Dingbats: U+2716 (✖ multiply) is wide");
		AssertWidth(ctx, (char)0x27BF, 2, "Dingbats: U+27BF (end) is wide");
	}

	private static void TestStar(TestContext ctx)
	{
		// U+2B50 star ⭐
		AssertWidth(ctx, (char)0x2B50, 2, "Star ⭐ U+2B50 is wide");
	}

	private static void TestCopyGlyph(TestContext ctx)
	{
		// U+29C9 copy/clipboard glyph ⧉
		AssertWidth(ctx, (char)0x29C9, 2, "Copy glyph ⧉ U+29C9 is wide");
	}

	private static void TestZeroWidth(TestContext ctx)
	{
		// Combining diacritical marks
		AssertWidth(ctx, (char)0x0300, 0, "Zero-width: U+0300 combining grave is zero-width");
		AssertWidth(ctx, (char)0x036F, 0, "Zero-width: U+036F is zero-width");
		// Zero-width spaces / joiners
		AssertWidth(ctx, '\u200B', 0, "Zero-width: U+200B (zero-width space) is zero-width");
		AssertWidth(ctx, '\u200C', 0, "Zero-width: U+200C (zero-width non-joiner) is zero-width");
		AssertWidth(ctx, '\u200D', 0, "Zero-width: U+200D (zero-width joiner) is zero-width");
		AssertWidth(ctx, '\u2060', 0, "Zero-width: U+2060 (word joiner) is zero-width");
		AssertWidth(ctx, '\uFEFF', 0, "Zero-width: U+FEFF (BOM) is zero-width");
		// Combining diacritical marks extended
		AssertWidth(ctx, (char)0x1AB0, 0, "Zero-width: U+1AB0 is zero-width");
		// Combining diacritical marks supplement
		AssertWidth(ctx, (char)0x1DC0, 0, "Zero-width: U+1DC0 is zero-width");
		// Combining diacritical marks for symbols
		AssertWidth(ctx, (char)0x20D0, 0, "Zero-width: U+20D0 is zero-width");
		// Combining half marks
		AssertWidth(ctx, (char)0xFE20, 0, "Zero-width: U+FE20 is zero-width");
	}

	private static void TestSurrogates(TestContext ctx)
	{
		// CharWidth.Of does not include surrogate ranges in IsWide or IsZeroWidth.
		// Surrogates are handled by AnsiString.CharWidth (standalone method), not by CharWidth.Of.
		// Here we verify that CharWidth.Of returns 1 for surrogates (the "default" path).
		// This is correct: callers replace surrogates with a placeholder before passing to the renderer.
		AssertWidth(ctx, (char)0xD800, 1, "CharWidth.Of: high surrogate U+D800 is 1 (default path — handled by AnsiString)");
		AssertWidth(ctx, (char)0xDBFF, 1, "CharWidth.Of: high surrogate U+DBFF is 1 (default path — handled by AnsiString)");
		AssertWidth(ctx, (char)0xDC00, 1, "CharWidth.Of: low surrogate U+DC00 is 1 (default path — handled by AnsiString)");
		AssertWidth(ctx, (char)0xDFFF, 1, "CharWidth.Of: low surrogate U+DFFF is 1 (default path — handled by AnsiString)");
	}

	private static void TestWideEmoji(TestContext ctx)
	{
		// CharWidth.Of does not include surrogate ranges in IsWide or IsZeroWidth.
		// AnsiString.CharWidth handles surrogates specially (high=2, low=0).
		// Verify CharWidth.Of returns 1 for emoji surrogates (default path).
		// U+1F600 (😀) → high surrogate U+D83D, low surrogate U+DE00
		AssertWidth(ctx, (char)0xD83D, 1, "CharWidth.Of: emoji high surrogate U+D83D is 1 (default path — handled by AnsiString)");
		AssertWidth(ctx, (char)0xDE00, 1, "CharWidth.Of: emoji low surrogate U+DE00 is 1 (default path — handled by AnsiString)");
	}

	private static void TestAnsiStringCharWidthConsistency(TestContext ctx)
	{
		// AnsiString.CharWidth must agree with CharWidth.Of for all non-surrogate codepoints.
		// This guards against drift between the two implementations.
		TestAgreement(ctx, (char)0x0041, "ASCII A");
		TestAgreement(ctx, (char)0x00E9, "Latin small e-acute");
		TestAgreement(ctx, (char)0x1100, "Hangul Jamo U+1100");
		TestAgreement(ctx, (char)0x115F, "Hangul Jamo U+115F");
		TestAgreement(ctx, (char)0x2329, "Angle bracket left");
		TestAgreement(ctx, (char)0x232A, "Angle bracket right");
		TestAgreement(ctx, (char)0x231A, "Clock U+231A");
		TestAgreement(ctx, (char)0x23E9, "Media fast-forward");
		TestAgreement(ctx, (char)0x25FD, "Geometric U+25FD");
		TestAgreement(ctx, (char)0x2600, "Misc symbol U+2600");
		TestAgreement(ctx, (char)0x2611, "Ballot box U+2611");
		TestAgreement(ctx, (char)0x2660, "Spade U+2660");
		TestAgreement(ctx, (char)0x2700, "Dingbat U+2700");
		TestAgreement(ctx, (char)0x2713, "Checkmark U+2713");
		TestAgreement(ctx, (char)0x27BF, "Dingbat end U+27BF");
		TestAgreement(ctx, (char)0x2B50, "Star U+2B50");
		TestAgreement(ctx, (char)0x29C9, "Copy glyph U+29C9");
		TestAgreement(ctx, (char)0x2E80, "CJK radical U+2E80");
		TestAgreement(ctx, (char)0x3041, "Hiragana U+3041");
		TestAgreement(ctx, (char)0x3400, "CJK ext A U+3400");
		TestAgreement(ctx, (char)0x4E00, "CJK U+4E00");
		TestAgreement(ctx, (char)0x9FFF, "CJK U+9FFF");
		TestAgreement(ctx, (char)0xA000, "Yi U+A000");
		TestAgreement(ctx, (char)0xAC00, "Hangul U+AC00");
		TestAgreement(ctx, (char)0xD7A3, "Hangul last U+D7A3");
		TestAgreement(ctx, (char)0xF900, "CJK compat U+F900");
		TestAgreement(ctx, (char)0xFE10, "Vertical form U+FE10");
		TestAgreement(ctx, (char)0xFE30, "CJK compat form U+FE30");
		TestAgreement(ctx, (char)0xFF00, "Fullwidth space U+FF00");
		TestAgreement(ctx, (char)0xFF60, "Fullwidth end U+FF60");
		TestAgreement(ctx, (char)0xFFE0, "Fullwidth sign U+FFE0");
		TestAgreement(ctx, (char)0xFFE6, "Fullwidth sign end U+FFE6");

		// Surrogate handling: AnsiString.CharWidth adds surrogate support on top of CharWidth.Of.
		ctx.AssertEqual(2, AnsiString.CharWidth((char)0xD800), "AnsiString: high surrogate U+D800 is 2");
		ctx.AssertEqual(2, AnsiString.CharWidth((char)0xDBFF), "AnsiString: high surrogate U+DBFF is 2");
		ctx.AssertEqual(0, AnsiString.CharWidth((char)0xDC00), "AnsiString: low surrogate U+DC00 is 0");
		ctx.AssertEqual(0, AnsiString.CharWidth((char)0xDFFF), "AnsiString: low surrogate U+DFFF is 0");

		// Emoji surrogate pair: U+1F600 (😀) = D83D DE00
		ctx.AssertEqual(2, AnsiString.CharWidth((char)0xD83D), "AnsiString: emoji high surrogate U+D83D is 2");
		ctx.AssertEqual(0, AnsiString.CharWidth((char)0xDE00), "AnsiString: emoji low surrogate U+DE00 is 0");
	}

	private static void TestAgreement(TestContext ctx, char c, string name)
	{
		int ofWidth = CharWidth.Of(c);
		int ansiWidth = AnsiString.CharWidth(c);
		ctx.AssertEqual(ofWidth, ansiWidth, $"AnsiString.CharWidth agrees with CharWidth.Of for {name} (U+{c:X4}): expected {ofWidth}");
	}
}
