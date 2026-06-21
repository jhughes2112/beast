using System;
using System.Collections.Generic;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;


// One rendered display line plus whether it may be word-wrapped. Prose (paragraphs, headings, lists, quotes)
// wraps to the viewport; code blocks and tables are NoWrap — they render at their natural width and the
// caller leaves them long so the block can be scrolled horizontally instead of reflowed.
public readonly struct RenderLine
{
    public readonly string Text;
    public readonly bool   NoWrap;

    public RenderLine(string text, bool noWrap)
    {
        Text   = text;
        NoWrap = noWrap;
    }
}

// Converts markdown text to plain-text lines with embedded ANSI escape codes.
// Returned strings contain ANSI codes; use AnsiString helpers to compute visible lengths.
public static class MarkdownAnsi
{
    // How far past the viewport width code blocks and tables are allowed to render before being clipped.
    // Bounds the natural width so a pathological minified line can't allocate an enormous block Screen.
    private const int MaxOverscan = 1000;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    // Returns rendered lines for display in the history panel. Each RenderLine is one logical display line
    // (may contain ANSI codes) tagged with whether the caller may word-wrap it. w is the viewport width: it
    // sets the natural-width clamp for code blocks and tables (w + MaxOverscan); prose is not wrapped here —
    // the caller wraps the wrappable lines to its own width.
    public static List<RenderLine> Render(string markdown, FrameType type, int w)
    {
        List<RenderLine> lines = new List<RenderLine>();
        if (string.IsNullOrEmpty(markdown)) return lines;

        MarkdownDocument doc = Markdown.Parse(markdown, Pipeline);
        RenderTopLevel(doc, lines, type, w);
        // Trim trailing blank lines.
        while (lines.Count > 0 && string.IsNullOrEmpty(lines[lines.Count - 1].Text))
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // Top-level pass: tracks the current heading section so blocks beneath a heading are indented
    // two spaces per heading level. A heading itself renders at its parent level's indent; the body
    // that follows it (until the next heading of equal-or-shallower level) is pushed in one level.
    private static void RenderTopLevel(MarkdownDocument doc, List<RenderLine> lines, FrameType type, int w)
    {
        string sectionIndent = "";
        bool first = true;
        foreach (Block block in doc)
        {
            if (!first) lines.Add(new RenderLine("", false));
            first = false;

            if (block is HeadingBlock heading)
            {
                string headIndent = new string(' ', Math.Max(0, heading.Level - 1) * 2);
                RenderBlock(block, lines, headIndent, type, w);
                sectionIndent = new string(' ', heading.Level * 2);
            }
            else
            {
                RenderBlock(block, lines, sectionIndent, type, w);
            }
        }
    }

    private static void RenderBlocks(IEnumerable<Block> blocks, List<RenderLine> lines, string indent, FrameType type, int w)
    {
        bool first = true;
        foreach (Block block in blocks)
        {
            if (!first) lines.Add(new RenderLine("", false));
            first = false;
            RenderBlock(block, lines, indent, type, w);
        }
    }

    private static void RenderBlock(Block block, List<RenderLine> lines, string indent, FrameType type, int w)
    {
        if (block is HeadingBlock heading)
        {
            string text = InlinesToString(heading.Inline);
            int level = heading.Level;
            // No literal '#' markers — hierarchy is conveyed by the section indent and a muted color
            // per level (kept calm for a medium-dark background). H1 also carries an underline rule.
            string code = level == 1 ? Codes.H1 : level == 2 ? Codes.H2 : Codes.H3;
            lines.Add(new RenderLine(indent + code + text + Codes.Reset, false));
        }
        else if (block is ParagraphBlock para)
        {
            string text = InlinesToAnsi(para.Inline, type);
            foreach (string segment in text.Split('\n'))
                lines.Add(new RenderLine(indent + segment + Codes.Reset, false));
        }
        else if (block is FencedCodeBlock fenced)
        {
            string lang = fenced.Info ?? "";
            string raw = fenced.Lines.ToString();
            RenderCodeBlock(raw, lang, lines, indent, w);
        }
        else if (block is CodeBlock code)
        {
            string raw = code.Lines.ToString();
            RenderCodeBlock(raw, "", lines, indent, w);
        }
        else if (block is ListBlock list)
        {
            int ordinal = 1;
            foreach (Block item in list)
            {
                if (item is ListItemBlock listItem)
                {
                    string bullet = list.IsOrdered ? $"{ordinal++}. " : "• ";
                    bool firstChild = true;
                    foreach (Block child in listItem)
                    {
                        string childIndent = firstChild ? indent + bullet : indent + new string(' ', bullet.Length);
                        firstChild = false;
                        if (child is ParagraphBlock cp)
                        {
                            // First wrapped line carries the bullet (in childIndent); any line breaks
                            // inside the item continue under the bullet's hanging indent.
                            string hangingIndent = indent + new string(' ', bullet.Length);
                            bool firstSegment = true;
                            foreach (string segment in InlinesToAnsi(cp.Inline, type).Split('\n'))
                            {
                                lines.Add(new RenderLine((firstSegment ? childIndent : hangingIndent) + segment + Codes.Reset, false));
                                firstSegment = false;
                            }
                        }
                        else
                            RenderBlock(child, lines, childIndent, type, w);
                    }
                }
            }
        }
        else if (block is QuoteBlock quote)
        {
            string quoteIndent = indent + Codes.DimGrey + "│ " + Codes.Reset;
            foreach (Block child in quote)
                RenderBlock(child, lines, quoteIndent, type, w);
        }
        else if (block is ThematicBreakBlock)
        {
            // Start the rule flush with the block's own indent (not pushed in further), and pull the right end
            // in by the same indent width so it sits symmetrically inside the block rather than running the full
            // width of the screen. Plain (non-dim) grey so the rule sits on the block's own background.
            int ruleLen = Math.Max(4, w - indent.Length * 2);
            string rule = new string('─', ruleLen);
            lines.Add(new RenderLine(indent + Codes.Border + rule + Codes.Reset, true));
        }
        else if (block is Table table)
        {
            RenderTable(table, lines, indent, type, w);
        }
        else if (block is ContainerBlock container)
        {
            RenderBlocks(container, lines, indent, type, w);
        }
    }

    // Renders a markdown table with pipe-delimited columns and a header separator row. The table renders at
    // its natural width (rows tagged NoWrap) so wide tables scroll horizontally rather than reflow; only a
    // pathological width past the overscan clamp gets its columns shaved to bound memory.
    private static void RenderTable(Table table, List<RenderLine> lines, string indent, FrameType type, int w)
    {
        // First pass: extract cell text and measure column widths.
        List<List<string>> rows = new List<List<string>>();
        List<bool> isHeader = new List<bool>();
        List<int> colWidths = new List<int>();

        foreach (Block rowBlock in table)
        {
            if (rowBlock is not TableRow row) continue;
            List<string> cells = new List<string>();
            int ci = 0;
            foreach (Block cellBlock in row)
            {
                if (cellBlock is not TableCell cell) continue;
                StringBuilder sb = new StringBuilder();
                foreach (Block child in cell)
                {
                    if (child is ParagraphBlock pp)
                        sb.Append(InlinesToAnsi(pp.Inline, type));
                }
                // A table cell is always one row; flatten any in-cell line break back to a space so it
                // can't blow out the column layout.
                string text = sb.ToString().Replace('\n', ' ');
                cells.Add(text);
                int vis = AnsiString.VisibleLength(text);
                if (ci >= colWidths.Count) colWidths.Add(vis);
                else if (vis > colWidths[ci]) colWidths[ci] = vis;
                ci++;
            }
            rows.Add(cells);
            isHeader.Add(row.IsHeader);
        }

        if (rows.Count == 0) return;

        // Cap each column so no single column is wider than about a third of the viewport. A wide free-text
        // column otherwise spreads the whole table out uselessly; capped cells word-wrap within their column
        // (below) instead of forcing the table wide.
        int maxColWidth = Math.Max(8, (w - indent.Length) / 3);
        for (int ci = 0; ci < colWidths.Count; ci++)
            if (colWidths[ci] > maxColWidth) colWidths[ci] = maxColWidth;

        // Clamp only against the natural-width overscan (not the viewport) so the table keeps its column
        // widths and scrolls; the layout is the leading border plus, per column, a leading space, the content,
        // a trailing space and a closing border — 1 + Σ(width + 3). Past the clamp (only reachable with many
        // columns now that each is capped), shave the widest columns first so narrow data columns keep width.
        int clamp = Math.Max(8, w + MaxOverscan - indent.Length);
        FitColumns(colWidths, clamp);

        // Second pass: emit rows. Each cell is word-wrapped to its column width, so a tall cell spans several
        // display lines with the other columns left blank on the continuation lines.
        for (int r = 0; r < rows.Count; r++)
        {
            List<string> cells = rows[r];

            // Wrap every cell to its column width up front and find the tallest cell — that is the row height.
            List<List<string>> wrapped = new List<List<string>>();
            int rowHeight = 1;
            for (int ci = 0; ci < colWidths.Count; ci++)
            {
                string cell = ci < cells.Count ? cells[ci] : string.Empty;
                List<string> cellLines = AnsiString.WordWrap(cell, colWidths[ci]);
                if (cellLines.Count == 0) cellLines.Add(string.Empty);
                wrapped.Add(cellLines);
                if (cellLines.Count > rowHeight) rowHeight = cellLines.Count;
            }

            for (int sub = 0; sub < rowHeight; sub++)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(indent);
                sb.Append(Codes.Border);
                sb.Append('│');
                sb.Append(Codes.Reset);
                for (int ci = 0; ci < colWidths.Count; ci++)
                {
                    List<string> cellLines = wrapped[ci];
                    string frag = sub < cellLines.Count ? cellLines[sub] : string.Empty;
                    int vis = AnsiString.VisibleLength(frag);
                    int pad = colWidths[ci] - vis;
                    if (pad < 0) pad = 0;
                    sb.Append(' ');
                    if (isHeader[r])
                        sb.Append(Codes.Bold);
                    sb.Append(frag);
                    sb.Append(Codes.Reset);
                    // Re-apply the border color after reset so padding spaces render with the block's
                    // background rather than the terminal default, keeping the row visually the right width.
                    sb.Append(Codes.Border);
                    sb.Append(new string(' ', pad + 1));
                    sb.Append('│');
                    sb.Append(Codes.Reset);
                }
                lines.Add(new RenderLine(sb.ToString(), true));
            }

            // Separator after header row.
            if (isHeader[r])
            {
                StringBuilder sep = new StringBuilder();
                sep.Append(indent);
                sep.Append(Codes.Border);
                sep.Append('├');
                for (int ci = 0; ci < colWidths.Count; ci++)
                {
                    sep.Append(new string('─', colWidths[ci] + 2));
                    sep.Append(ci < colWidths.Count - 1 ? '┼' : '┤');
                }
                sep.Append(Codes.Reset);
                lines.Add(new RenderLine(sep.ToString(), true));
            }
        }
    }

    // Shrinks column widths in place until the table fits within available columns. Each column costs its
    // width plus three (a leading space, a trailing space and a border); the leading border adds one more.
    // The widest column above a small floor is shaved one cell at a time so narrow data columns are left at
    // full width and only the wide free-text columns give ground.
    private static void FitColumns(List<int> colWidths, int available)
    {
        const int MinCol = 3;
        int total = 1;
        foreach (int cw in colWidths)
            total += cw + 3;

        int overflow = total - available;
        while (overflow > 0)
        {
            int widest = -1;
            for (int i = 0; i < colWidths.Count; i++)
            {
                if (colWidths[i] <= MinCol) continue;
                if (widest < 0 || colWidths[i] > colWidths[widest]) widest = i;
            }
            if (widest < 0) break;

            colWidths[widest]--;
            overflow--;
        }
    }

    // Renders a code block as a bordered rectangle. Lines are NOT word-wrapped and the box is sized to the
    // widest line (clamped to the overscan) so wide code scrolls horizontally; every line is padded to the
    // box width so the background forms a solid rectangle. Rows are tagged NoWrap so the caller leaves them
    // at full width.
    private static void RenderCodeBlock(string raw, string lang, List<RenderLine> lines, string indent, int w)
    {
        string[] codeLines = raw.TrimEnd('\n', '\r').Split('\n');
        string[] highlighted = new string[codeLines.Length];
        int[] visLens = new int[codeLines.Length];

        // First pass: highlight and measure.
        int maxContent = 0;
        for (int i = 0; i < codeLines.Length; i++)
        {
            highlighted[i] = SyntaxHighlight(codeLines[i].TrimEnd('\r'), lang);
            visLens[i] = codeLines[i].TrimEnd('\r').Length;
            if (visLens[i] > maxContent) maxContent = visLens[i];
        }

        // Box width = the natural content width, clamped only by the overscan (the viewport plus headroom)
        // so wide code keeps its true width and is reachable by horizontal scroll instead of being truncated.
        int maxBox = Math.Max(1, w + MaxOverscan - indent.Length - 2);
        int boxContent = Math.Min(maxContent, maxBox);
        if (boxContent < 1) boxContent = 1;

        string labelSuffix = !string.IsNullOrEmpty(lang) ? $" {lang} " : "";
        int topFillLen = Math.Max(0, boxContent - labelSuffix.Length);
        string topBorder    = Codes.Border + "╭" + labelSuffix + new string('─', topFillLen) + "╮" + Codes.Reset;
        string bottomBorder = Codes.Border + "╰" + new string('─', boxContent) + "╯" + Codes.Reset;

        lines.Add(new RenderLine(indent + topBorder, true));

        // Second pass: emit each line truncated and padded to boxContent.
        for (int i = 0; i < codeLines.Length; i++)
        {
            // Truncate visible content to boxContent chars.
            string hl = AnsiString.TruncateVisible(highlighted[i], boxContent);
            int pad = Math.Max(0, boxContent - Math.Min(visLens[i], boxContent));
            lines.Add(new RenderLine(indent + Codes.Border + "│" + Codes.Reset + Codes.CodeBg + hl + Codes.CodeBg + new string(' ', pad) + Codes.Reset + Codes.Border + "│" + Codes.Reset, true));
        }

        lines.Add(new RenderLine(indent + bottomBorder, true));
    }

    private static string InlinesToAnsi(ContainerInline? container, FrameType type)
    {
        if (container == null) return "";
        StringBuilder sb = new StringBuilder();
        AppendInlines(container, sb, type);
        return sb.ToString();
    }

    private static string InlinesToString(ContainerInline? container)
    {
        if (container == null) return "";
        StringBuilder sb = new StringBuilder();
        foreach (Inline inline in container)
            sb.Append(InlineToPlain(inline));
        return sb.ToString();
    }

    private static string InlineToPlain(Inline inline)
    {
        if (inline is LiteralInline lit) return lit.Content.ToString();
        if (inline is LineBreakInline) return " ";
        if (inline is ContainerInline ci)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Inline child in ci)
                sb.Append(InlineToPlain(child));
            return sb.ToString();
        }
        return "";
    }

    private static void AppendInlines(IEnumerable<Inline> inlines, StringBuilder sb, FrameType type)
    {
        foreach (Inline inline in inlines)
            AppendInline(inline, sb, type);
    }

    private static void AppendInline(Inline inline, StringBuilder sb, FrameType type)
    {
        if (inline is LiteralInline lit)
        {
            sb.Append(lit.Content.ToString());
        }
        else if (inline is EmphasisInline em)
        {
            string open = em.DelimiterCount == 2 ? Codes.Bold : Codes.Italic;
            sb.Append(open);
            AppendInlines(em, sb, type);
            sb.Append(Codes.Reset);
            sb.Append(BaseCodeForType(type));
        }
        else if (inline is CodeInline code)
        {
            sb.Append(Codes.InlineCode);
            sb.Append(code.Content);
            sb.Append(Codes.Reset);
            sb.Append(BaseCodeForType(type));
        }
        else if (inline is LinkInline link)
        {
            // When the display text is identical to the URL, drop the redundant "text (url)" form and
            // show the bare URL — underlined so it still reads as a link, and saving horizontal space.
            string linkText = InlinesToString(link);
            if (!string.IsNullOrEmpty(link.Url) && linkText == link.Url)
            {
                sb.Append(Codes.LinkUrl);
                sb.Append("\x1b[4m");
                sb.Append(link.Url);
                sb.Append("\x1b[24m");
            }
            else
            {
                sb.Append(Codes.LinkText);
                AppendInlines(link, sb, type);
                if (!string.IsNullOrEmpty(link.Url))
                {
                    // Underline only the URL itself — the parens stay plain so the line reads as less noisy.
                    sb.Append(Codes.Reset);
                    sb.Append(Codes.LinkUrl);
                    sb.Append(" (");
                    sb.Append("\x1b[4m");
                    sb.Append(link.Url);
                    sb.Append("\x1b[24m");
                    sb.Append(")");
                }
            }
            sb.Append(Codes.Reset);
            sb.Append(BaseCodeForType(type));
        }
        else if (inline is LineBreakInline)
        {
            // Preserve the author's newline as a real line break. The paragraph/list emitters split
            // on it into separate display lines; only table cells flatten it back to a space.
            sb.Append('\n');
        }
        else if (inline is HtmlEntityInline entity)
        {
            sb.Append(entity.Transcoded.ToString());
        }
        else if (inline is ContainerInline ci)
        {
            AppendInlines(ci, sb, type);
        }
    }

    private static string BaseCodeForType(FrameType type)
    {
        return type switch
        {
            FrameType.Thinking => Codes.Thinking,
            FrameType.Tool     => Codes.Tool,
            FrameType.System   => Codes.System,
            FrameType.User     => Codes.User,
            FrameType.Error    => Codes.Error,
            _                  => Codes.Output
        };
    }

    // True when a file path is a markdown document by extension. Such files render through the markdown
    // pipeline rather than the code path, regardless of content.
    internal static bool IsMarkdownFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        int dot = filePath.LastIndexOf('.');
        if (dot < 0) return false;
        string ext = filePath.Substring(dot + 1).ToLowerInvariant();
        return ext == "md" || ext == "markdown" || ext == "mdown" || ext == "mkd";
    }

    // Heuristic: does this text read as markdown rather than plain prose / code / tool output? Scans for
    // structural markers (headings, fences, lists, quotes, tables, rules) plus a couple of inline markers,
    // and only commits when at least two signals are present so a stray bullet in grep output or a lone
    // "- " in prose does not trip it. Used to decide whether a tool response should be rendered as markdown
    // or preserved verbatim line-for-line.
    internal static bool LooksLikeMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int signals = 0;
        bool fence = false;

        foreach (string raw in lines)
        {
            string line = raw.TrimStart();

            if (line.StartsWith("```"))
            {
                signals += 2;
                fence = !fence;
                continue;
            }
            if (fence) continue;

            // ATX heading: one to six '#' followed by a space.
            if (line.Length >= 2 && line[0] == '#')
            {
                int h = 0;
                while (h < line.Length && line[h] == '#') h++;
                if (h <= 6 && h < line.Length && line[h] == ' ')
                {
                    signals += 2;
                    continue;
                }
            }

            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
            {
                signals += 1;
                continue;
            }

            // Ordered list: digits then ". ".
            int d = 0;
            while (d < line.Length && char.IsDigit(line[d])) d++;
            if (d > 0 && d + 1 < line.Length && line[d] == '.' && line[d + 1] == ' ')
            {
                signals += 1;
                continue;
            }

            if (line.StartsWith("> "))
            {
                signals += 1;
                continue;
            }

            if (line.StartsWith("|") && line.IndexOf('|', 1) > 0)
            {
                signals += 1;
                continue;
            }

            if (line == "---" || line == "***" || line == "___")
            {
                signals += 1;
                continue;
            }
        }

        // Inline markers are weaker on their own, so they only nudge an already-suspicious body over the line.
        if (text.Contains("](")) signals += 1;
        if (text.Contains("**")) signals += 1;

        // A genuine inline code span (`like this`) is on its own a strong signal — often a backtick-wrapped
        // word or two is the only markdown a short message carries — so it alone clears the threshold.
        if (HasInlineCodeSpan(text)) signals += 2;

        return signals >= 2;
    }

    // True when some line contains a `…` inline code span: a backtick, one or more non-backtick chars, then a
    // closing backtick on the same line. Fence lines (``` …) do not match because their run of backticks has
    // no non-backtick content before a closing backtick on the line.
    private static bool HasInlineCodeSpan(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (string line in lines)
        {
            int tick = line.IndexOf('`');
            while (tick >= 0)
            {
                int p = tick + 1;
                bool sawContent = false;
                while (p < line.Length && line[p] != '`')
                {
                    sawContent = true;
                    p++;
                }
                if (p < line.Length && sawContent) return true;
                tick = line.IndexOf('`', tick + 1);
            }
        }
        return false;
    }

    // Guesses a syntax-highlight language tag from a file path extension.
    internal static string GuessLang(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return string.Empty;
        int dot = filePath.LastIndexOf('.');
        if (dot < 0) return string.Empty;
        string ext = filePath.Substring(dot + 1).ToLowerInvariant();
        switch (ext)
        {
            case "cs":                          return "csharp";
            case "js":                          return "javascript";
            case "ts":                          return "typescript";
            case "py":                          return "python";
            case "sh": case "bash":             return "bash";
            case "ps1":                         return "powershell";
            case "go":                          return "go";
            case "rs":                          return "rust";
            case "java":                        return "java";
            case "cpp": case "cc": case "cxx":  return "cpp";
            case "c": case "h":                return "c";
            case "kt":                          return "kotlin";
            case "swift":                       return "swift";
            default:                            return string.Empty;
        }
    }

    // Highlights a single line using the dark-gray code-block background (for markdown fenced blocks).
    internal static string SyntaxHighlight(string line, string lang)
    {
        bool isClike = lang is "c" or "c#" or "csharp" or "cs" or "cpp" or "java" or "javascript" or "js" or "ts" or "typescript" or "go" or "rust" or "swift" or "kotlin";
        bool isPython = lang is "python" or "py";
        bool isBash = lang is "bash" or "sh" or "shell" or "powershell" or "ps1";

        if (!isClike && !isPython && !isBash)
            return Codes.CodeText + line;

        HashSet<string> keywords = GetKeywords(lang);
        return TokenizeLine(line, keywords, lang);
    }

    // Highlights a single line, replacing the code-block dark background with bgOnlyAnsi (background-only SGR).
    // Token foreground colors (keyword, string, etc.) are preserved; only the background is substituted.
    internal static string SyntaxHighlight(string line, string lang, string bgOnlyAnsi)
    {
        string result = SyntaxHighlight(line, lang);
        return bgOnlyAnsi + result.Replace(Codes.DarkBg, bgOnlyAnsi) + bgOnlyAnsi;
    }

    private static string TokenizeLine(string line, HashSet<string> keywords, string lang)
    {
        StringBuilder sb = new StringBuilder();
        int i = 0;

        // Full-line comment detection.
        string trimmed = line.TrimStart();
        bool isBash = lang is "bash" or "sh" or "shell" or "powershell" or "ps1";
        bool isClike = !isBash && lang != "python" && lang != "py";
        bool isPython = lang is "python" or "py";

        if ((isClike && trimmed.StartsWith("//")) || (isBash && trimmed.StartsWith("#")) || (isPython && trimmed.StartsWith("#")))
            return Codes.Comment + line + Codes.Reset;

        sb.Append(Codes.CodeText);

        while (i < line.Length)
        {
            char c = line[i];

            // String literal.
            if (c == '"' || c == '\'' || c == '`')
            {
                char quote = c;
                int start = i;
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '\\') { i += 2; continue; }
                    if (line[i] == quote) { i++; break; }
                    i++;
                }
                sb.Append(Codes.StringLit);
                sb.Append(line.Substring(start, i - start));
                sb.Append(Codes.CodeText);
                continue;
            }

            // Number literal.
            if (char.IsDigit(c) && (i == 0 || !char.IsLetterOrDigit(line[i - 1])))
            {
                int start = i;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == 'x' || line[i] == '_' || (i > start && char.IsLetter(line[i]))))
                    i++;
                sb.Append(Codes.Number);
                sb.Append(line.Substring(start, i - start));
                sb.Append(Codes.CodeText);
                continue;
            }

            // Identifier or keyword.
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                string word = line.Substring(start, i - start);
                if (keywords.Contains(word))
                {
                    sb.Append(Codes.Keyword);
                    sb.Append(word);
                    sb.Append(Codes.CodeText);
                }
                else
                {
                    sb.Append(word);
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        sb.Append(Codes.Reset);
        return sb.ToString();
    }

    private static HashSet<string> GetKeywords(string lang)
    {
        if (lang is "python" or "py")
            return new HashSet<string>(StringComparer.Ordinal) { "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "False", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not", "or", "pass", "raise", "return", "True", "try", "while", "with", "yield" };

        if (lang is "bash" or "sh" or "shell" or "powershell" or "ps1")
            return new HashSet<string>(StringComparer.Ordinal) { "if", "then", "else", "elif", "fi", "for", "in", "do", "done", "while", "until", "case", "esac", "function", "return", "local", "export", "echo", "exit" };

        // C-like family.
        return new HashSet<string>(StringComparer.Ordinal) { "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "record", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while", "var", "let", "const", "function", "import", "export", "from", "extends", "implements", "type", "interface", "package", "fn", "mut", "pub", "use", "mod", "impl", "trait", "struct", "enum", "match", "Some", "None", "Ok", "Err" };
    }

    private static class Codes
    {
        public const string Reset      = "\x1b[0m";
        public const string Bold       = "\x1b[1m";
        public const string Italic     = "\x1b[3m";

        // Heading colors — desaturated blue tones that read well on the dark slate background without
        // shouting. H1 also carries an underline rule.
        public const string H1         = "\x1b[1m\x1b[4m\x1b[38;2;124;170;214m";
        public const string H2         = "\x1b[1m\x1b[38;2;120;166;190m";
        public const string H3         = "\x1b[1m\x1b[38;2;138;160;196m";

        // These "base" colors are re-applied after every inline span (bold, code, link) and at the start of
        // each word-wrapped continuation line, so each must equal the block's own base foreground — otherwise
        // a paragraph visibly shifts color the moment it hits its first inline element or wraps to a new line.
        public const string Output     = "\x1b[38;2;206;206;210m";    // matches Palette.Silver (Output base fg)
        public const string Thinking   = "\x1b[3m\x1b[38;2;128;128;132m"; // matches Palette.ThinkingFg (no dim — block is already italic)
        public const string Tool       = "\x1b[38;2;126;192;196m";    // calm teal
        public const string System     = "\x1b[38;2;204;140;82m";     // soft amber
        public const string User       = "\x1b[38;2;244;244;246m";    // matches Palette.BrightUser (User base fg)
        public const string Error      = "\x1b[38;2;214;102;102m";    // softened red

        public const string InlineCode = "\x1b[38;2;212;182;120m";    // soft gold
        public const string DarkBg     = "\x1b[48;5;236m";            // background for markdown code blocks
        public const string CodeBg     = "\x1b[38;2;206;206;210m\x1b[48;5;236m";
        public const string CodeText   = "\x1b[38;2;210;212;216m\x1b[48;5;236m";
        public const string Keyword    = "\x1b[38;2;124;170;214m\x1b[48;5;236m";   // soft blue
        public const string StringLit  = "\x1b[38;2;152;186;130m\x1b[48;5;236m";   // soft green
        public const string Number     = "\x1b[38;2;206;160;110m\x1b[48;5;236m";   // soft amber
        public const string Comment    = "\x1b[38;2;142;142;146m\x1b[48;5;236m\x1b[3m";

        public const string LinkText   = "\x1b[38;2;124;170;214m";                 // visible link text — blue, not underlined (only the URL is)
        public const string LinkUrl    = "\x1b[38;2;158;160;166m";                 // the parenthesized URL — medium grey, no dim so its background matches the block
        public const string DimGrey    = "\x1b[2m\x1b[38;2;142;142;146m";
        public const string Border     = "\x1b[38;2;108;108;114m";
    }
}
