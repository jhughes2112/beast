using System;
using System.Collections.Generic;
using System.Text;
using Markdig;
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
        RenderBlocks(doc, lines, "", type, w);
        // Trim trailing blank lines.
        while (lines.Count > 0 && string.IsNullOrEmpty(lines[lines.Count - 1]))
            lines.RemoveAt(lines.Count - 1);
        return lines;
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
            string prefix = level == 1 ? Codes.Bold + "# " : level == 2 ? Codes.Bold + "## " : Codes.Bold;
            lines.Add(indent + prefix + text + Codes.Reset);
        }
        else if (block is ParagraphBlock para)
        {
            string text = InlinesToAnsi(para.Inline, type);
            lines.Add(indent + text + Codes.Reset);
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
                            lines.Add(childIndent + InlinesToAnsi(cp.Inline, type) + Codes.Reset);
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
        else if (block is ContainerBlock container)
        {
            RenderBlocks(container, lines, indent, type, w);
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
            sb.Append(' ');
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

    // Very lightweight token-based syntax colouring for common languages.
    // Colours keywords, strings, comments, and numbers; everything else stays default.
    private static string SyntaxHighlight(string line, string lang)
    {
        bool isClike = lang is "c" or "c#" or "csharp" or "cs" or "cpp" or "java" or "javascript" or "js" or "ts" or "typescript" or "go" or "rust" or "swift" or "kotlin";
        bool isPython = lang is "python" or "py";
        bool isBash = lang is "bash" or "sh" or "shell" or "powershell" or "ps1";

        if (!isClike && !isPython && !isBash)
            return Codes.CodeText + line;

        HashSet<string> keywords = GetKeywords(lang);
        return TokenizeLine(line, keywords, lang);
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
        {
            return Codes.Comment + line + Codes.Reset;
        }

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

        public const string Output     = "\x1b[38;5;250m";
        public const string Thinking   = "\x1b[2;3;38;5;59m";
        public const string Tool       = "\x1b[38;5;33m";
        public const string System     = "\x1b[38;5;166m";
        public const string User       = "\x1b[38;5;244m";
        public const string Error      = "\x1b[38;5;196m";

        public const string InlineCode = "\x1b[38;5;221m";
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
