using System;
using System.Collections.Generic;
using System.Text.Json;


// Renders individual conversation message blocks into BlockLayer (collapsed + expanded Screen pair).
// All methods are stateless and take only what they need — easy to test or disable in isolation.
internal static class BlockRenderer
{
    // Upper bound on an expanded block's pixel width. Wide code/tables render at natural width for horizontal
    // scrolling, but this caps the backing Screen so a pathological minified line can't allocate unbounded.
    private const int MaxBlockWidth = 2000;

    // Builds (or rebuilds) one BlockLayer for a single DisplayMessage.
    // plainText skips markdown rendering (used for streaming slots where content changes every frame).
    internal static BlockLayer Build(DisplayMessage msg, int w, bool plainText)
    {
        bool isToolCall = msg.Type == FrameType.ToolCall;
        bool isError   = isToolCall && msg.PairedResponseIsError;
        // The header line and the stderr block go red whenever the response carries any error content —
        // a non-zero exit, or any stderr text even on a clean exit. The block's base color stays normal,
        // though: only the header row and the stderr region are painted red, so stdout keeps its normal
        // (blue) background and reads as distinct from stderr below it.
        bool headerError = isError || (isToolCall && !string.IsNullOrEmpty(msg.PairedResponseError));
        (Rgb fg, Rgb? bg) = ColorsForType(msg.Type, false);
        (Rgb headerFg, Rgb? headerBg) = ColorsForType(msg.Type, headerError);
        int spacer = 0;

        string prefix = PrefixTextForType(msg.Type);
        string summary = isToolCall
            ? FormatToolCallSummary(msg.Content, msg.PairedResponseContent)
            : msg.Content.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

        string toolName = string.Empty;
        if (isToolCall)
        {
            int paren = msg.Content.IndexOf('(');
            toolName = paren >= 0 ? msg.Content.Substring(0, paren).Trim() : msg.Content;
        }

        // Thinking/tool/tool-response blocks indent their whole expanded body (header included) two spaces
        // so it nests under the message. The collapsed header must carry the same indent or the text shifts
        // right when the block is expanded.
        string headerIndent = (msg.Type == FrameType.Thinking || msg.Type == FrameType.Tool
                            || msg.Type == FrameType.ToolResponse) ? "  " : string.Empty;

        int availW = Math.Max(1, w - prefix.Length - headerIndent.Length);
        // The collapsed header shows up to availW visible chars, so it is only actually truncated past availW.
        // (Using availW - 1 here flagged a summary that exactly fills the line, producing an ellipsis with
        // nothing extra to reveal when expanded.)
        bool truncated = AnsiString.VisibleLength(summary) > availW;

        // The collapsed preview is always stdout (bash output, a file's contents) — never stderr — so it
        // keeps the normal body background regardless of exit code.
        string respBgAnsi = DisplayScreen.Palette.BodyBgAnsi;
        string fileLang = string.Empty;
        if (!isError && (toolName == "read_file" || toolName == "write_file"
                      || toolName == "edit_file_replace" || toolName == "edit_file_insert"))
            fileLang = MarkdownAnsi.GuessLang(ExtractStringArg(msg.Content, "file_path"));

        List<string> collapsedLines = new List<string>();
        collapsedLines.Add(headerIndent + prefix + AnsiString.TruncateVisible(summary, availW));

        if (isToolCall && !string.IsNullOrEmpty(msg.PairedResponseContent))
        {
            string[] respLines = msg.PairedResponseContent!.Replace("\r\n", "\n").Split('\n');
            if (toolName == "bash")
            {
                int start = Math.Max(0, respLines.Length - 5);
                for (int i = start; i < respLines.Length; i++)
                    collapsedLines.Add("  " + MarkdownAnsi.SyntaxHighlight(ExpandTabs(respLines[i]), "bash", respBgAnsi));
            }
            else if (toolName == "read_file")
            {
                int end = Math.Min(respLines.Length, 5);
                for (int i = 0; i < end; i++)
                    collapsedLines.Add("  " + MarkdownAnsi.SyntaxHighlight(ExpandTabs(respLines[i]), fileLang, respBgAnsi));
            }
        }
        if (isToolCall && toolName == "write_file")
        {
            string writeContent = ExtractStringArg(msg.Content, "content");
            if (!string.IsNullOrEmpty(writeContent))
            {
                string[] wlines = writeContent.Replace("\r\n", "\n").Split('\n');
                int end = Math.Min(wlines.Length, 5);
                for (int i = 0; i < end; i++)
                    collapsedLines.Add("  " + MarkdownAnsi.SyntaxHighlight(ExpandTabs(wlines[i]), fileLang, respBgAnsi));
            }
        }

        // The header already carries the filename, so the condensed preview is just the goal — one line per
        // source line so a multi-line goal keeps its newlines rather than being collapsed onto a single row.
        if (isToolCall && toolName == "find_relevant_file_sections")
        {
            string goal = ExtractStringArg(msg.Content, "goal");
            if (!string.IsNullOrEmpty(goal))
            {
                foreach (string gline in goal.Replace("\r\n", "\n").Split('\n'))
                    collapsedLines.Add("  " + ExpandTabs(gline));
            }
        }

        // While streaming (plainText), tool calls and assistant output stay on the raw path — partial markdown
        // (half-open fences, dangling tables) renders badly mid-stream. Thinking is the exception: it reads
        // well as markdown as it arrives, and RenderMessageRows only treats it as markdown once it actually
        // looks like markdown, so plain reasoning prose still streams verbatim. The committed block is rebuilt
        // through the full markdown path once streaming ends.
        List<string> ansiLines;
        // Row index where the tool's returned output begins (-1 when there is none, or on the streaming raw
        // path). Output rows render on a slightly darker body background so the response reads as distinct
        // from the call header and its arguments above it.
        int responseStart = -1;
        // Row index where the stderr block begins (-1 when there is none). Those rows are painted red.
        int errorStart = -1;
        if (isToolCall)
            ansiLines = plainText ? RenderMessageRowsRaw(msg, w) : RenderToolCallRows(msg, w, out responseStart, out errorStart);
        else if (plainText && msg.Type != FrameType.Thinking)
            ansiLines = RenderMessageRowsRaw(msg, w);
        else
            ansiLines = RenderMessageRows(msg, w);

        bool needsEllipsis = truncated || ansiLines.Count > collapsedLines.Count;
        if (needsEllipsis)
            collapsedLines[0] = headerIndent + prefix + AnsiString.TruncateVisible(summary, availW - 1) + "…";

        Cell rowBg = new Cell(' ', fg, bg, CellStyle.None);
        Screen collapsed = new Screen(w, collapsedLines.Count + spacer, rowBg);
        for (int r = 0; r < collapsedLines.Count; r++)
        {
            Rgb lineFg = r == 0 ? headerFg : fg;
            Rgb? lineBg = r == 0 ? headerBg : bg;
            (int endX, Rgb? cFg, Rgb? cBg) = AnsiToScreen.WriteLine(collapsed, 0, r, collapsedLines[r], lineFg, lineBg);
            AnsiToScreen.PadRowBackground(collapsed, endX, r, cFg, cBg);
        }

        // The expanded block is as wide as its widest line (never narrower than the viewport) so wide code
        // and tables keep their full width; the history viewport shows a horizontal window into it. Prose was
        // already wrapped to the viewport, so this only grows for NoWrap content. Capped via MaxBlockWidth so a
        // pathological line can't allocate a giant Screen.
        int contentW = w;
        for (int r = 0; r < ansiLines.Count; r++)
        {
            int vl = AnsiString.VisibleLength(ansiLines[r]);
            if (vl > contentW) contentW = vl;
        }
        if (contentW > MaxBlockWidth) contentW = MaxBlockWidth;

        int expandedRows = Math.Max(1, ansiLines.Count);
        Screen expanded = new Screen(contentW, expandedRows + spacer, rowBg);
        // Output rows fall back to the body background (slightly darker than the block) on any reset, so prose
        // and markdown responses — which carry no explicit background of their own — still read as distinct
        // output rather than blending into the call block. stdout always uses the normal body background;
        // only the header (row 0) and the stderr block fall back to the red error background.
        Rgb? responseBaseBg = DisplayScreen.Palette.FileBodyBg;
        Rgb? errorBaseBg = DisplayScreen.Palette.FileErrBodyBg;
        for (int r = 0; r < ansiLines.Count; r++)
        {
            Rgb rowFg = r == 0 ? headerFg : fg;
            Rgb? rowBaseBg;
            if (r == 0)
                rowBaseBg = headerBg;
            else if (errorStart >= 0 && r >= errorStart)
                rowBaseBg = errorBaseBg;
            else if (responseStart >= 0 && r >= responseStart)
                rowBaseBg = responseBaseBg;
            else
                rowBaseBg = bg;
            (int endCx, Rgb? eFg, Rgb? eBg) = AnsiToScreen.WriteLine(expanded, 0, r, ansiLines[r], rowFg, rowBaseBg);
            AnsiToScreen.PadRowBackground(expanded, endCx, r, eFg, eBg);
        }

        if (isToolCall && !isError && msg.ToolDuration.HasValue && msg.ToolDuration.Value.TotalSeconds >= 0.1)
        {
            string tag = $"Took {msg.ToolDuration.Value.TotalSeconds:F1}s";
            StampRightOnRow(collapsed, 0, tag, headerFg, headerBg);
            StampRightOnRow(expanded,  0, tag, headerFg, headerBg);
        }

        if (msg.Type == FrameType.Thinking)
        {
            ApplyStyle(collapsed, CellStyle.Italic);
            ApplyStyle(expanded,  CellStyle.Italic);
        }

        return new BlockLayer(msg.Index, collapsed, expanded, isExpanded: !msg.Collapsed);
    }

    internal static string ExpandTabs(string text) => text.Replace("\t", "    ");

    private static void StampRightOnRow(Screen s, int row, string text, Rgb fg, Rgb? bg)
    {
        if (row < 0 || row >= s.H) return;
        int len = text.Length;
        if (len + 1 > s.W) return;
        int startCol = s.W - len;
        for (int i = 0; i < len; i++)
            s.Set(startCol + i, row, new Cell(text[i], fg, bg, CellStyle.None));
    }

    private static void ApplyStyle(Screen s, CellStyle add)
    {
        for (int y = 0; y < s.H; y++)
        {
            for (int x = 0; x < s.W; x++)
            {
                Cell c = s.Get(x, y);
                s.Set(x, y, new Cell(c.Ch, c.Fg, c.Bg, c.Style | add));
            }
        }
    }

    private static (Rgb Fg, Rgb? Bg) ColorsForType(FrameType type, bool isError)
    {
        if (isError) return (DisplayScreen.Palette.ToolCallErrFg, DisplayScreen.Palette.ToolCallErrBg);
        switch (type)
        {
            case FrameType.Output:       return (DisplayScreen.Palette.Silver,      null);
            case FrameType.User:         return (DisplayScreen.Palette.BrightUser,  DisplayScreen.Palette.UserBg);
            case FrameType.Error:        return (DisplayScreen.Palette.Red,         null);
            case FrameType.Thinking:     return (DisplayScreen.Palette.ThinkingFg,  null);
            case FrameType.Tool:         return (DisplayScreen.Palette.Blue,        null);
            case FrameType.ToolCall:     return (DisplayScreen.Palette.ToolCallFg,  DisplayScreen.Palette.ToolCallBg);
            case FrameType.ToolResponse: return (DisplayScreen.Palette.ToolRespFg,  DisplayScreen.Palette.ToolRespBg);
            case FrameType.System:       return (DisplayScreen.Palette.Orange,      null);
            case FrameType.Debug:        return (DisplayScreen.Palette.MedGrey,     null);
            default:                     return (DisplayScreen.Palette.Silver,      null);
        }
    }

    private static string PrefixTextForType(FrameType type)
    {
        switch (type)
        {
            case FrameType.Thinking:     return "";
            case FrameType.Tool:         return "[tool] ";
            case FrameType.ToolCall:     return "";
            case FrameType.ToolResponse: return "";
            case FrameType.Debug:        return "[debug] ";
            case FrameType.System:       return "# ";
            case FrameType.Error:        return "! ";
            case FrameType.User:         return "» ";
            default:                     return "";
        }
    }

    private static List<string> RenderMessageRows(DisplayMessage msg, int w)
    {
        List<string> result = new List<string>();
        string prefix = PrefixTextForType(msg.Type);
        // The system prompt is plain instructional text, not markdown: rendering it through Markdig drops
        // angle-bracket tokens (parsed as HTML) and collapses single newlines, so it is rendered verbatim.
        // Thinking is markdown only when it actually reads as markdown; plain reasoning prose stays on the
        // verbatim path so its newlines and indentation survive (the markdown parser would collapse them).
        bool useMarkdown = msg.Type == FrameType.Output || msg.Type == FrameType.User
                        || (msg.Type == FrameType.Thinking && MarkdownAnsi.LooksLikeMarkdown(msg.Content));
        bool wordWrap = msg.Type == FrameType.Output || msg.Type == FrameType.User
                     || msg.Type == FrameType.System || msg.Type == FrameType.Thinking;

        // Thinking and tool output nest under their headers: indent the whole block two spaces (wrapping
        // to the narrower width first so the indent doesn't clip the trailing characters).
        bool indentBlock = msg.Type == FrameType.Thinking || msg.Type == FrameType.Tool
                        || msg.Type == FrameType.ToolResponse;
        int wrapW = indentBlock ? Math.Max(1, w - 2) : w;

        if (useMarkdown)
        {
            List<RenderLine> mdLines = MarkdownAnsi.Render(ExpandTabs(msg.Content), msg.Type, wrapW);
            bool firstLine = true;
            foreach (RenderLine mdLine in mdLines)
            {
                string full = firstLine ? prefix + mdLine.Text : mdLine.Text;
                firstLine = false;
                // Code blocks and tables are NoWrap — leave them at natural width so the block scrolls
                // horizontally; only prose reflows to the viewport.
                if (mdLine.NoWrap)
                {
                    result.Add(full);
                    continue;
                }
                List<string> wrappedLines = wordWrap ? AnsiString.WordWrapIndented(full, wrapW) : AnsiString.Wrap(full, wrapW);
                foreach (string wrapped in wrappedLines)
                    result.Add(wrapped);
            }
            if (result.Count == 0)
                result.Add(prefix);
        }
        else
        {
            string[] logicalLines = msg.Content.Split('\n');
            bool first = true;
            foreach (string line in logicalLines)
            {
                string full = first ? prefix + ExpandTabs(line) : ExpandTabs(line);
                first = false;
                List<string> wrappedLines = wordWrap ? AnsiString.WordWrapIndented(full, wrapW) : AnsiString.Wrap(full, wrapW);
                foreach (string wl in wrappedLines)
                    result.Add(wl);
            }
        }

        if (indentBlock)
        {
            for (int i = 0; i < result.Count; i++)
                result[i] = "  " + result[i];
        }

        return result;
    }

    private static List<string> RenderMessageRowsRaw(DisplayMessage msg, int w)
    {
        List<string> result = new List<string>();
        string prefix = PrefixTextForType(msg.Type);
        string[] logicalLines = msg.Content.Split('\n');
        bool first = true;
        foreach (string line in logicalLines)
        {
            string full = first ? prefix + ExpandTabs(line) : ExpandTabs(line);
            first = false;
            foreach (string wl in AnsiString.Wrap(full, w))
                result.Add(wl);
        }
        if (result.Count == 0)
            result.Add(prefix);
        return result;
    }

    private static List<string> RenderToolCallRows(DisplayMessage msg, int w, out int responseStart, out int errorStart)
    {
        responseStart = -1;
        errorStart = -1;
        List<string> result = new List<string>();
        string content = msg.Content;

        int paren = content.IndexOf('(');
        string name = paren >= 0 ? content.Substring(0, paren).Trim() : content;
        string argsJson = paren >= 0 ? content.Substring(paren + 1) : string.Empty;
        if (argsJson.Length > 0 && argsJson[argsJson.Length - 1] == ')')
            argsJson = argsJson.Substring(0, argsJson.Length - 1);

        // The header stays a single truncated line even when expanded — wrapping the summary across several
        // rows reads worse than a clean one-line header with the body opened beneath it.
        string summary = FormatToolCallSummary(content, msg.PairedResponseContent);
        result.Add(AnsiString.VisibleLength(summary) > w
            ? AnsiString.TruncateVisible(summary, w - 1) + "…"
            : summary);

        // Everything emitted past here is the body that nests under the summary header; it gets indented
        // two spaces at the end so it reads as part of the block.
        int bodyStart = result.Count;

        HashSet<string> summaryProps = SummaryPropertiesFor(name);

        // Arguments and stdout always render on the normal body background — only the stderr block below
        // carries the red error background.
        string respBodyBgAnsi = DisplayScreen.Palette.BodyBgAnsi;
        string fileLang = MarkdownAnsi.GuessLang(ExtractStringArg(msg.Content, "file_path"));

        // edit_file's parameters (the old/new text) are redundant with the diff shown below, so its
        // arguments are not emitted at all — only the diffed response.
        if (name != "edit_file" && !string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(argsJson);
                List<string> inlineProps = new List<string>();
                // Block-valued arguments deferred until after the short scalars. Kind: 0 = prose markdown,
                // 1 = code content, 2 = plain multi-line. Sorted shortest-first before emission.
                List<(string Name, string Val, int Kind)> blockProps = new List<(string, string, int)>();

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (summaryProps.Contains(prop.Name)) continue;

                    string val = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.ToString();

                    val = val.Replace("\\n", "\n").Replace("\\t", "    ").Replace("\t", "    ");

                    // Free-form prose arguments (a subagent's return value, a phase transition briefing)
                    // are markdown. Render them the same way assistant output is rendered so fenced code
                    // blocks and long paragraphs are interpreted and width-wrapped, rather than dumped raw
                    // (raw over-width lines desync the terminal columns around the rendered content).
                    if (IsProseArg(name, prop.Name))
                        blockProps.Add((prop.Name, val, 0));
                    else if (prop.Name.Equals("content", StringComparison.OrdinalIgnoreCase))
                        blockProps.Add((prop.Name, val, 1));
                    else if (val.IndexOf('\n') >= 0)
                        blockProps.Add((prop.Name, val, 2));
                    else
                        inlineProps.Add($"{prop.Name} {val}");
                }

                // Short scalar arguments first, word-wrapped, so the most scannable parameters lead.
                if (inlineProps.Count > 0)
                {
                    foreach (string wrapped in AnsiString.WordWrap("  " + string.Join("  ", inlineProps), w))
                        result.Add(wrapped);
                }

                // Then the heavier block arguments, shortest first so the bulkiest content sinks last.
                blockProps.Sort((a, b) => a.Val.Length.CompareTo(b.Val.Length));

                foreach ((string Name, string Val, int Kind) bp in blockProps)
                {
                    if (bp.Kind == 0)
                    {
                        // Prose args are markdown only when they read as markdown; otherwise emit them
                        // verbatim so plain output (a subagent's citation list) keeps its newlines and
                        // indentation rather than being collapsed by the markdown parser.
                        if (MarkdownAnsi.LooksLikeMarkdown(bp.Val))
                        {
                            foreach (RenderLine mdLine in MarkdownAnsi.Render(ExpandTabs(bp.Val), FrameType.Output, w))
                            {
                                if (mdLine.NoWrap)
                                {
                                    result.Add(mdLine.Text);
                                    continue;
                                }
                                foreach (string wrapped in AnsiString.WordWrapIndented(mdLine.Text, w))
                                    result.Add(wrapped);
                            }
                        }
                        else
                        {
                            foreach (string valLine in bp.Val.Split('\n'))
                                result.Add(ExpandTabs(valLine));
                        }
                    }
                    else if (bp.Kind == 1)
                    {
                        foreach (string valLine in bp.Val.Split('\n'))
                            result.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(valLine), fileLang, respBodyBgAnsi));
                    }
                    else
                    {
                        bool firstPropLine = true;
                        foreach (string valLine in bp.Val.Split('\n'))
                        {
                            result.Add(firstPropLine ? $"  {bp.Name}  {valLine}" : $"    {valLine}");
                            firstPropLine = false;
                        }
                    }
                }
            }
            catch
            {
                foreach (string rawLine in argsJson.Split('\n'))
                    result.Add(rawLine);
            }
        }

        bool suppressPairedResponse = !msg.PairedResponseIsError
            && (name == "write_file" || name == "edit_file_replace" || name == "edit_file_insert");

        if (!suppressPairedResponse && !string.IsNullOrEmpty(msg.PairedResponseContent))
        {
            if (responseStart < 0) responseStart = result.Count;
            // stdout renders on the normal body background whatever the exit code — its red counterpart is
            // the stderr block below, so the two stay visually distinct.
            RespMode mode = ResponseModeFor(name, ExtractStringArg(msg.Content, "file_path"), msg.PairedResponseContent!);
            EmitResponseLines(result, msg.PairedResponseContent, mode, fileLang, respBodyBgAnsi, w);
        }

        if (!string.IsNullOrEmpty(msg.PairedResponseError))
        {
            if (responseStart < 0) responseStart = result.Count;
            errorStart = result.Count;
            foreach (string errLine in msg.PairedResponseError.Split('\n'))
                result.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(errLine), fileLang, DisplayScreen.Palette.ErrBodyBgAnsi));
        }

        for (int i = bodyStart; i < result.Count; i++)
            result[i] = "  " + result[i];

        if (result.Count == 0)
            result.Add(name);
        return result;
    }

    private enum RespMode { Code, Bash, Markdown, Plain, EditDiff }

    // Picks how a tool's response body is rendered. File reads stay on the code path (highlighted, columns
    // aligned) unless the file is actually markdown — a .md file, or a body that reads as markdown — in which
    // case it flows through the markdown pipeline. Shell output is always shell-highlighted. Everything else
    // (web fetches, searches, subagent returns) is rendered as markdown when it looks like markdown, and
    // otherwise preserved verbatim line-for-line so plain output (citations, grep hits) keeps its newlines
    // and indentation instead of being mangled by the markdown parser.
    private static RespMode ResponseModeFor(string toolName, string filePath, string content)
    {
        switch (toolName)
        {
            case "edit_file":
                return RespMode.EditDiff;
            case "read_file":
            case "write_file":
            case "edit_file_replace":
            case "edit_file_insert":
                if (MarkdownAnsi.IsMarkdownFile(filePath))
                    return RespMode.Markdown;
                // A known code extension stays on the code path; only when the extension is unknown do we
                // fall back to sniffing the body, so a stray '#' comment in a .cs file can't flip it.
                if (string.IsNullOrEmpty(MarkdownAnsi.GuessLang(filePath)) && MarkdownAnsi.LooksLikeMarkdown(content))
                    return RespMode.Markdown;
                return RespMode.Code;
            case "ls":
                return RespMode.Code;
            case "bash":
            case "readonly_bash":
                return RespMode.Bash;
            default:
                return MarkdownAnsi.LooksLikeMarkdown(content) ? RespMode.Markdown : RespMode.Plain;
        }
    }

    // Emits a tool response into result. Markdown mode interprets and word-wraps prose; Plain mode preserves
    // each source line verbatim (newlines and indentation intact, no parsing); the code/shell modes emit one
    // highlighted line per source line. All non-markdown modes leave columns un-wrapped so output scrolls
    // horizontally rather than reflowing.
    private static void EmitResponseLines(List<string> result, string content, RespMode mode, string fileLang, string bgAnsi, int w)
    {
        if (mode == RespMode.Markdown)
        {
            foreach (RenderLine mdLine in MarkdownAnsi.Render(ExpandTabs(content), FrameType.Output, w))
            {
                if (mdLine.NoWrap)
                {
                    result.Add(mdLine.Text);
                    continue;
                }
                foreach (string wrapped in AnsiString.WordWrapIndented(mdLine.Text, w))
                    result.Add(wrapped);
            }
        }
        else if (mode == RespMode.EditDiff)
        {
            EmitEditDiffLines(result, content);
        }
        else if (mode == RespMode.Plain)
        {
            foreach (string respLine in content.Replace("\r\n", "\n").Split('\n'))
                result.Add(ExpandTabs(respLine));
        }
        else
        {
            string lang = mode == RespMode.Bash ? "bash" : fileLang;
            foreach (string respLine in content.Replace("\r\n", "\n").Split('\n'))
                result.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(respLine), lang, bgAnsi));
        }
    }

    // Colorizes the edit_file echo (a unified diff produced by the Agent). Context rows ('|') use the
    // file-body palette so they sit with the read/write blocks; removed ('-') and added ('+') rows get
    // tinted red/green backgrounds. When a run of removed rows is followed by an equal-length run of
    // added rows, the rows are paired and the changed span within each line is highlighted brighter so
    // small straight edits read at a glance. Rows are not wrapped — like code, the diff scrolls wide.
    private static void EmitEditDiffLines(List<string> result, string content)
    {
        string[] raw = content.Replace("\r\n", "\n").Split('\n');
        int n = raw.Length;

        // Parse each line into (marker, gutter, body). The gutter is the fixed-width "NNNNN x " prefix
        // (5-digit number, space, marker, space). Non-diff lines (the header) get marker '\0'.
        char[] markers = new char[n];
        string[] gutters = new string[n];
        string[] bodies = new string[n];
        for (int i = 0; i < n; i++)
        {
            string line = raw[i];
            if (line.Length >= 8 && line[5] == ' ' && line[7] == ' '
             && (line[6] == '|' || line[6] == '-' || line[6] == '+'))
            {
                markers[i] = line[6];
                gutters[i] = line.Substring(0, 8);
                bodies[i]  = ExpandTabs(line.Substring(8));
            }
            else
            {
                markers[i] = '\0';
                gutters[i] = string.Empty;
                bodies[i]  = line;
            }
        }

        int idx = 0;
        while (idx < n)
        {
            char m = markers[idx];
            if (m == '\0')
            {
                result.Add($"\x1b[38;2;160;160;160m{bodies[idx]}");
                idx++;
            }
            else if (m == '|')
            {
                result.Add(DisplayScreen.Palette.BodyAnsi + gutters[idx] + bodies[idx]);
                idx++;
            }
            else
            {
                // Gather the removed run, then the added run that follows it.
                int delStart = idx;
                while (idx < n && markers[idx] == '-') idx++;
                int delCount = idx - delStart;
                int addStart = idx;
                while (idx < n && markers[idx] == '+') idx++;
                int addCount = idx - addStart;

                bool pair = delCount > 0 && delCount == addCount;
                for (int k = 0; k < delCount; k++)
                {
                    string? counterpart = pair ? bodies[addStart + k] : null;
                    result.Add(BuildDiffRow(gutters[delStart + k], bodies[delStart + k], counterpart, isAdd: false));
                }
                for (int k = 0; k < addCount; k++)
                {
                    string? counterpart = pair ? bodies[delStart + k] : null;
                    result.Add(BuildDiffRow(gutters[addStart + k], bodies[addStart + k], counterpart, isAdd: true));
                }
            }
        }
    }

    // Builds one removed/added diff row. With a counterpart line, the shared head and tail render on the
    // row's base tint and only the differing middle gets the brighter highlight background.
    private static string BuildDiffRow(string gutter, string body, string? counterpart, bool isAdd)
    {
        string baseAnsi = isAdd ? DisplayScreen.Palette.DiffAddAnsi   : DisplayScreen.Palette.DiffDelAnsi;
        string hiAnsi   = isAdd ? DisplayScreen.Palette.DiffAddHiAnsi : DisplayScreen.Palette.DiffDelHiAnsi;

        if (counterpart == null)
            return baseAnsi + gutter + body;

        int cp = CommonPrefixLength(body, counterpart);
        int cs = CommonSuffixLength(body, counterpart, cp);
        if (cp + cs >= body.Length)
            return baseAnsi + gutter + body;

        string head = body.Substring(0, cp);
        string mid  = body.Substring(cp, body.Length - cs - cp);
        string tail = body.Substring(body.Length - cs);
        return baseAnsi + gutter + head + hiAnsi + mid + baseAnsi + tail;
    }

    private static int CommonPrefixLength(string a, string b)
    {
        int max = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < max && a[i] == b[i]) i++;
        return i;
    }

    private static int CommonSuffixLength(string a, string b, int prefix)
    {
        int max = Math.Min(a.Length, b.Length) - prefix;
        int i = 0;
        while (i < max && a[a.Length - 1 - i] == b[b.Length - 1 - i]) i++;
        return i;
    }

    // True for string arguments that carry free-form markdown prose rather than code, paths, or short
    // scalars. These are rendered through the markdown pipeline in the expanded tool-call body.
    private static bool IsProseArg(string toolName, string propName)
    {
        if (toolName == "return_to_caller" && propName.Equals("output", StringComparison.OrdinalIgnoreCase)) return true;
        if (toolName == "finish_review" && propName.Equals("comments", StringComparison.OrdinalIgnoreCase)) return true;
        // task_complete's single argument is the whole review/integration summary the caller receives — it
        // is the message itself, so render it as markdown prose rather than a labelled "results …" property.
        if (toolName == "task_complete" && propName.Equals("results_of_review_work", StringComparison.OrdinalIgnoreCase)) return true;
        if (toolName == "state_transition" && propName.Equals("context", StringComparison.OrdinalIgnoreCase)) return true;
        // The delegation tools' prompt is a natural-language briefing — render it as prose, not as a labelled
        // "prompt <value>" property, so the expanded block reads as the instruction itself.
        if ((toolName == "assign_work" || toolName == "review_work") && propName.Equals("prompt", StringComparison.OrdinalIgnoreCase)) return true;
        // The goal is the body of the call — render it as prose itself, not as a labelled "goal <value>" property.
        if (toolName == "find_relevant_file_sections" && propName.Equals("goal", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static HashSet<string> SummaryPropertiesFor(string toolName)
    {
        HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (toolName)
        {
            case "state_transition":
                set.Add("statement"); break;
            case "read_file":
                set.Add("file_path"); set.Add("offset"); set.Add("lines"); break;
            case "find_relevant_file_sections":
                // file_path is in the header and offset is noise; the body shows the goal.
                set.Add("file_path"); set.Add("offset"); break;
            case "write_file":
            case "edit_file_replace":
            case "edit_file_insert":
                set.Add("file_path"); break;
            case "bash":
            case "readonly_bash":
                // timeout_seconds is noise in the expanded block — the command is what matters.
                set.Add("command"); set.Add("timeout_seconds"); break;
            case "ls":
                set.Add("folder"); break;
            case "search_web":
                set.Add("query"); break;
            case "fetch_url":
                set.Add("url"); break;
        }
        return set;
    }

    private static string FormatToolCallSummary(string content, string? pairedResponse)
    {
        int paren = content.IndexOf('(');
        if (paren < 0) return content.Replace('\n', ' ');

        string name = content.Substring(0, paren).Trim();
        string argsJson = content.Substring(paren + 1);
        if (argsJson.Length > 0 && argsJson[argsJson.Length - 1] == ')')
            argsJson = argsJson.Substring(0, argsJson.Length - 1);

        JsonElement root = default;
        bool parsed = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            root = doc.RootElement.Clone();
            parsed = true;
        }
        catch { }

        string Get(string key)
        {
            if (!parsed) return string.Empty;
            if (!root.TryGetProperty(key, out JsonElement el)) return string.Empty;
            // Schema args can be typed (numbers, bools) not just strings, so read the raw
            // text for anything that is not a string rather than forcing GetString(), which
            // throws on a Number/Boolean element.
            if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? string.Empty;
            if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined) return string.Empty;
            return el.GetRawText();
        }

        string label = name.Replace('_', ' ');
        int respLineCount = CountLines(pairedResponse);
        int writeLineCount = (name == "write_file" || name == "edit_file_replace" || name == "edit_file_insert")
            ? CountLines(Get("content") + Get("new_text"))
            : 0;
        string summary = name switch
        {
            "read_file"                                                => BuildReadFileSummary(label, Get("file_path"), Get("offset"), Get("lines"), respLineCount),
            "write_file" or "edit_file_replace" or "edit_file_insert"  => BuildWriteFileSummary(label, Get("file_path"), writeLineCount),
            "bash" or "readonly_bash"                                  => BuildRunCommandSummary(label, Get("command")),
            "search_web"                                               => BuildPathSummary(label, Get("query"), respLineCount),
            "fetch_url"                                               => BuildPathSummary(label, Get("url"), respLineCount),
            "return_to_caller"                                         => BuildLineCountSummary(label, CountLines(Get("output"))),
            "finish_review"                                            => BuildLineCountSummary(label, CountLines(Get("comments"))),
            "state_transition"                                         => $"{label} {Get("statement")}",
            _                                                          => BuildGenericSummary(label, parsed ? root : default, parsed)
        };

        // The collapsed summary must stay a single visual line. Anything past the first newline
        // wraps and corrupts the surrounding block layout, so cut there and re-terminate any open
        // ANSI styling so color does not bleed into the rest of the row.
        int firstNewline = summary.IndexOf('\n');
        if (firstNewline >= 0)
            summary = summary.Substring(0, firstNewline) + DisplayScreen.Palette.ResetAnsi;

        return summary;
    }

    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') count++;
        if (text[text.Length - 1] == '\n') count--;
        return Math.Max(1, count);
    }

    private static string ExtractStringArg(string content, string argName)
    {
        int paren = content.IndexOf('(');
        if (paren < 0) return string.Empty;
        string argsJson = content.Substring(paren + 1);
        if (argsJson.Length > 0 && argsJson[argsJson.Length - 1] == ')')
            argsJson = argsJson.Substring(0, argsJson.Length - 1);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty(argName, out JsonElement el) && el.ValueKind == JsonValueKind.String)
                return el.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static string BuildLineCountSummary(string label, int lineCount)
    {
        string tail = lineCount > 0 ? $" ({lineCount} lines)" : string.Empty;
        return label + tail;
    }

    private static string BuildPathSummary(string label, string path, int respLineCount)
    {
        if (string.IsNullOrEmpty(path)) return label;
        string tail = respLineCount > 0 ? $" ({respLineCount} lines)" : string.Empty;
        return $"{label} {DisplayScreen.Palette.FileNameAnsi}{path}{DisplayScreen.Palette.ResetAnsi}{tail}";
    }

    private static string BuildWriteFileSummary(string label, string path, int writeLineCount)
    {
        if (string.IsNullOrEmpty(path)) return label;
        string tail = writeLineCount > 0 ? $" ({writeLineCount} lines)" : string.Empty;
        return $"{label} {DisplayScreen.Palette.FileNameAnsi}{path}{DisplayScreen.Palette.ResetAnsi}{tail}";
    }

    private static string BuildReadFileSummary(string label, string path, string offset, string lines, int respLineCount)
    {
        if (string.IsNullOrEmpty(path)) return label;
        string tail = respLineCount > 0 ? $" ({respLineCount} lines)" : string.Empty;
        if (!string.IsNullOrEmpty(offset) && !string.IsNullOrEmpty(lines))
        {
            int.TryParse(offset, out int start);
            int.TryParse(lines, out int count);
            int end = start + count - 1;
            return $"{label} {DisplayScreen.Palette.FileNameAnsi}{path}{DisplayScreen.Palette.ResetAnsi}  [{offset}-{end}]{tail}";
        }
        if (!string.IsNullOrEmpty(offset))
            return $"{label} {DisplayScreen.Palette.FileNameAnsi}{path}{DisplayScreen.Palette.ResetAnsi}  [from {offset}]{tail}";
        return $"{label} {DisplayScreen.Palette.FileNameAnsi}{path}{DisplayScreen.Palette.ResetAnsi}{tail}";
    }

    private static string BuildRunCommandSummary(string label, string command)
    {
        if (string.IsNullOrEmpty(command)) return "$";
        int nl = command.IndexOf('\n');
        string first = nl >= 0 ? command.Substring(0, nl).TrimEnd() : command;
        return $"$ {first}";
    }

    private static string BuildGenericSummary(string label, JsonElement root, bool parsed)
    {
        if (!parsed) return label;
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                string val = prop.Value.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(val))
                    return $"{label} {DisplayScreen.Palette.FileNameAnsi}{val}{DisplayScreen.Palette.ResetAnsi}";
            }
        }
        return label;
    }
}
