using System;
using System.Collections.Generic;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;


// Converts markdown text to plain-text lines with embedded ANSI escape codes.
// Returned strings contain ANSI codes; use AnsiString helpers to compute visible lengths.
public static class MarkdownAnsi
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    // Returns rendered lines for display in the history panel.
    // Each string may contain ANSI codes but represents one logical display line.
    // w is the terminal width used to bound code block rectangles.
    public static List<string> Render(string markdown, FrameType type, int w)
    {
        List<string> lines = new List<string>();
        if (string.IsNullOrEmpty(markdown)) return lines;

        MarkdownDocument doc = Markdown.Parse(markdown, Pipeline);
        RenderTopLevel(doc, lines, type, w);
        // Trim trailing blank lines.
        while (lines.Count > 0 && string.IsNullOrEmpty(lines[lines.Count - 1]))
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // Top-level pass: tracks the current heading section so blocks beneath a heading are indented
    // two spaces per heading level. A heading itself renders at its parent level's indent; the body
    // that follows it (until the next heading of equal-or-shallower level) is pushed in one level.
    private static void RenderTopLevel(MarkdownDocument doc, List<string> lines, FrameType type, int w)
    {
        string sectionIndent = "";
        bool first = true;
        foreach (Block block in doc)
        {
            if (!first) lines.Add("");
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

    private static void RenderBlocks(IEnumerable<Block> blocks, List<string> lines, string indent, FrameType type, int w)
    {
        bool first = true;
        foreach (Block block in blocks)
        {
            if (!first) lines.Add("");
            first = false;
            RenderBlock(block, lines, indent, type, w);
        }
    }

    private static void RenderBlock(Block block, List<string> lines, string indent, FrameType type, int w)
    {
        if (block is HeadingBlock heading)
        {
            string text = InlinesToString(heading.Inline);
            int level = heading.Level;
            // No literal '#' markers — hierarchy is conveyed by the section indent and a muted color
            // per level (kept calm for a medium-dark background). H1 also carries an underline rule.
            string code = level == 1 ? Codes.H1 : level == 2 ? Codes.H2 : Codes.H3;
            lines.Add(indent + code + text + Codes.Reset);
        }
        else if (block is ParagraphBlock para)
        {
            string text = InlinesToAnsi(para.Inline, type);
            foreach (string segment in text.Split('\n'))
                lines.Add(indent + segment + Codes.Reset);
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
                                lines.Add((firstSegment ? childIndent : hangingIndent) + segment + Codes.Reset);
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
            lines.Add(indent + Codes.DimGrey + new string('─', 40) + Codes.Reset);
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

    // Renders a markdown table with pipe-delimited columns and a header separator row.
    private static void RenderTable(Table table, List<string> lines, string indent, FrameType type, int w)
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

        // Second pass: emit rows.
        for (int r = 0; r < rows.Count; r++)
        {
            List<string> cells = rows[r];
            StringBuilder sb = new StringBuilder();
            sb.Append(indent);
            sb.Append(Codes.Border);
            sb.Append('│');
            sb.Append(Codes.Reset);
            for (int ci = 0; ci < colWidths.Count; ci++)
            {
                string cell = ci < cells.Count ? cells[ci] : string.Empty;
                int vis = AnsiString.VisibleLength(cell);
                int pad = colWidths[ci] - vis;
                sb.Append(' ');
                if (isHeader[r])
                {
                    sb.Append(Codes.Bold);
                    sb.Append(cell);
                    sb.Append(Codes.Reset);
                    // Re-apply the border color after reset so padding spaces render with the block's
                    // background rather than the terminal default, keeping the row visually the right width.
                    sb.Append(Codes.Border);
                }
                else
                {
                    sb.Append(cell);
                }
                sb.Append(new string(' ', pad + 1));
                sb.Append(Codes.Border);
                sb.Append('│');
                sb.Append(Codes.Reset);
            }
            lines.Add(sb.ToString());

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
                lines.Add(sep.ToString());
            }
        }
    }

    // Renders a code block as a bordered rectangle.
    // Lines are NOT word-wrapped; each is truncated to fit the terminal.
    // All lines are padded to the same width so the background forms a solid rectangle.
    private static void RenderCodeBlock(string raw, string lang, List<string> lines, string indent, int w)
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

        // Box width = content width + 2 padding chars, bounded by terminal width minus indent and borders.
        int indentLen = indent.Length;
        int maxBox = Math.Max(1, w - indentLen - 2);
        int boxContent = Math.Min(maxContent, maxBox);
        if (boxContent < 1) boxContent = 1;

        string labelSuffix = !string.IsNullOrEmpty(lang) ? $" {lang} " : "";
        int topFillLen = Math.Max(0, boxContent - labelSuffix.Length);
        string topBorder    = Codes.Border + "╭" + labelSuffix + new string('─', topFillLen) + "╮" + Codes.Reset;
        string bottomBorder = Codes.Border + "╰" + new string('─', boxContent) + "╯" + Codes.Reset;

        lines.Add(indent + topBorder);

        // Second pass: emit each line truncated and padded to boxContent.
        for (int i = 0; i < codeLines.Length; i++)
        {
            // Truncate visible content to boxContent chars.
            string hl = AnsiString.TruncateVisible(highlighted[i], boxContent);
            int pad = Math.Max(0, boxContent - Math.Min(visLens[i], boxContent));
            lines.Add(indent + Codes.Border + "│" + Codes.Reset + Codes.CodeBg + hl + Codes.CodeBg + new string(' ', pad) + Codes.Reset + Codes.Border + "│" + Codes.Reset);
        }

        lines.Add(indent + bottomBorder);
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
            sb.Append(Codes.LinkText);
            AppendInlines(link, sb, type);
            if (!string.IsNullOrEmpty(link.Url))
            {
                sb.Append(Codes.DimGrey);
                sb.Append(" (");
                sb.Append(link.Url);
                sb.Append(")");
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

        // Heading colors — desaturated blue/teal tones chosen to read well on a medium-dark background.
        public const string H1         = "\x1b[1m\x1b[4m\x1b[38;5;75m";
        public const string H2         = "\x1b[1m\x1b[38;5;73m";
        public const string H3         = "\x1b[1m\x1b[38;5;110m";

        public const string Output     = "\x1b[38;5;250m";
        public const string Thinking   = "\x1b[2;3;38;5;59m";
        public const string Tool       = "\x1b[38;5;33m";
        public const string System     = "\x1b[38;5;166m";
        public const string User       = "\x1b[38;5;244m";
        public const string Error      = "\x1b[38;5;196m";

        public const string InlineCode = "\x1b[38;5;221m";
        public const string DarkBg     = "\x1b[48;5;236m";            // background for markdown code blocks
        public const string CodeBg     = "\x1b[38;5;250m\x1b[48;5;236m";
        public const string CodeText   = "\x1b[38;5;252m\x1b[48;5;236m";
        public const string Keyword    = "\x1b[38;5;75m\x1b[48;5;236m";
        public const string StringLit  = "\x1b[38;5;150m\x1b[48;5;236m";
        public const string Number     = "\x1b[38;5;215m\x1b[48;5;236m";
        public const string Comment    = "\x1b[38;5;244m\x1b[48;5;236m\x1b[3m";

        public const string LinkText   = "\x1b[38;5;75m\x1b[4m";
        public const string DimGrey    = "\x1b[2;38;5;244m";
        public const string Border     = "\x1b[38;5;240m";
    }
}
