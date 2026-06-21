using System;
using System.Collections.Generic;
using System.Text.Json;


// Renders individual conversation message blocks into BlockLayer (collapsed + expanded Screen pair).
// All methods are stateless and take only what they need — easy to test or disable in isolation.
internal static class BlockRenderer
{
    // Builds (or rebuilds) one BlockLayer for a single DisplayMessage.
    // plainText skips markdown rendering (used for streaming slots where content changes every frame).
    internal static BlockLayer Build(DisplayMessage msg, int w, bool plainText)
    {
        bool isToolCall = msg.Type == FrameType.ToolCall;
        bool isError   = isToolCall && msg.PairedResponseIsError;
        // The header line is red whenever the response shows any error content, not only on a non-zero
        // exit: the stderr field (PairedResponseError) is always rendered red below, so a command that
        // writes to stderr but still exits 0 gets a red body and the header must match it. The stdout
        // body keeps its normal color (driven by isError) — only the header reflects stderr's presence.
        bool headerError = isError || (isToolCall && !string.IsNullOrEmpty(msg.PairedResponseError));
        (Rgb fg, Rgb? bg) = ColorsForType(msg.Type, headerError);
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

        int availW = Math.Max(1, w - prefix.Length);
        bool truncated = AnsiString.VisibleLength(summary) > availW - 1;

        string respAnsi   = isError ? DisplayScreen.Palette.ErrBodyAnsi   : DisplayScreen.Palette.BodyAnsi;
        string respBgAnsi = isError ? DisplayScreen.Palette.ErrBodyBgAnsi : DisplayScreen.Palette.BodyBgAnsi;
        string fileLang = string.Empty;
        if (!isError && (toolName == "read_file" || toolName == "write_file"
                      || toolName == "edit_file_replace" || toolName == "edit_file_insert"))
            fileLang = MarkdownAnsi.GuessLang(ExtractStringArg(msg.Content, "file_path"));

        List<string> collapsedLines = new List<string>();
        collapsedLines.Add(prefix + AnsiString.TruncateVisible(summary, availW));

        if (isToolCall && !string.IsNullOrEmpty(msg.PairedResponseContent))
        {
            string[] respLines = msg.PairedResponseContent!.Replace("\r\n", "\n").Split('\n');
            if (toolName == "bash")
            {
                int start = Math.Max(0, respLines.Length - 5);
                for (int i = start; i < respLines.Length; i++)
                    collapsedLines.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(respLines[i]), "bash", respBgAnsi));
            }
            else if (toolName == "read_file")
            {
                int end = Math.Min(respLines.Length, 5);
                for (int i = 0; i < end; i++)
                    collapsedLines.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(respLines[i]), fileLang, respBgAnsi));
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
                    collapsedLines.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(wlines[i]), fileLang, respBgAnsi));
            }
        }

        List<string> ansiLines = plainText
            ? RenderMessageRowsRaw(msg, w)
            : (isToolCall ? RenderToolCallRows(msg, w) : RenderMessageRows(msg, w));

        bool needsEllipsis = truncated || ansiLines.Count > collapsedLines.Count;
        if (needsEllipsis)
            collapsedLines[0] = prefix + AnsiString.TruncateVisible(summary, availW - 1) + "…";

        Cell rowBg = new Cell(' ', fg, bg, CellStyle.None);
        Screen collapsed = new Screen(w, collapsedLines.Count + spacer, rowBg);
        for (int r = 0; r < collapsedLines.Count; r++)
        {
            (int endX, Rgb? cFg, Rgb? cBg) = AnsiToScreen.WriteLine(collapsed, 0, r, collapsedLines[r], fg, bg);
            AnsiToScreen.PadRowBackground(collapsed, endX, r, cFg, cBg);
        }

        int expandedRows = Math.Max(1, ansiLines.Count);
        Screen expanded = new Screen(w, expandedRows + spacer, rowBg);
        for (int r = 0; r < ansiLines.Count; r++)
        {
            (int endCx, Rgb? eFg, Rgb? eBg) = AnsiToScreen.WriteLine(expanded, 0, r, ansiLines[r], fg, bg);
            AnsiToScreen.PadRowBackground(expanded, endCx, r, eFg, eBg);
        }

        if (isToolCall && !isError && msg.ToolDuration.HasValue && msg.ToolDuration.Value.TotalSeconds >= 0.1)
        {
            string tag = $"Took {msg.ToolDuration.Value.TotalSeconds:F1}s";
            StampRightOnRow(collapsed, 0, tag, fg, bg);
            StampRightOnRow(expanded,  0, tag, fg, bg);
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
        bool useMarkdown = msg.Type == FrameType.Output || msg.Type == FrameType.User;
        bool wordWrap = msg.Type == FrameType.Output || msg.Type == FrameType.User
                     || msg.Type == FrameType.System || msg.Type == FrameType.Thinking;

        if (useMarkdown)
        {
            List<string> mdLines = MarkdownAnsi.Render(ExpandTabs(msg.Content), msg.Type, w);
            bool firstLine = true;
            foreach (string mdLine in mdLines)
            {
                string full = firstLine ? prefix + mdLine : mdLine;
                firstLine = false;
                List<string> wrappedLines = wordWrap ? AnsiString.WordWrapIndented(full, w) : AnsiString.Wrap(full, w);
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
                List<string> wrappedLines = wordWrap ? AnsiString.WordWrapIndented(full, w) : AnsiString.Wrap(full, w);
                foreach (string wl in wrappedLines)
                    result.Add(wl);
            }
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

    private static List<string> RenderToolCallRows(DisplayMessage msg, int w)
    {
        List<string> result = new List<string>();
        string content = msg.Content;

        int paren = content.IndexOf('(');
        string name = paren >= 0 ? content.Substring(0, paren).Trim() : content;
        string argsJson = paren >= 0 ? content.Substring(paren + 1) : string.Empty;
        if (argsJson.Length > 0 && argsJson[argsJson.Length - 1] == ')')
            argsJson = argsJson.Substring(0, argsJson.Length - 1);

        string summary = FormatToolCallSummary(content, msg.PairedResponseContent);
        foreach (string wrappedLine in AnsiString.WordWrap(summary, w))
            result.Add(wrappedLine);

        HashSet<string> summaryProps = SummaryPropertiesFor(name);

        string respBodyAnsi   = msg.PairedResponseIsError ? DisplayScreen.Palette.ErrBodyAnsi   : DisplayScreen.Palette.BodyAnsi;
        string respBodyBgAnsi = msg.PairedResponseIsError ? DisplayScreen.Palette.ErrBodyBgAnsi : DisplayScreen.Palette.BodyBgAnsi;
        string fileLang = MarkdownAnsi.GuessLang(ExtractStringArg(msg.Content, "file_path"));

        if (!string.IsNullOrWhiteSpace(argsJson))
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
                        foreach (string mdLine in MarkdownAnsi.Render(ExpandTabs(bp.Val), FrameType.Output, w))
                        {
                            foreach (string wrapped in AnsiString.WordWrapIndented(mdLine, w))
                                result.Add(wrapped);
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
            string respBgAnsi = msg.PairedResponseIsError ? DisplayScreen.Palette.ErrBodyBgAnsi : respBodyBgAnsi;
            RespMode mode = msg.PairedResponseIsError ? RespMode.Code : ResponseModeFor(name);
            EmitResponseLines(result, msg.PairedResponseContent, mode, fileLang, respBgAnsi, w);
        }

        if (!string.IsNullOrEmpty(msg.PairedResponseError))
        {
            foreach (string errLine in msg.PairedResponseError.Split('\n'))
                result.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(errLine), fileLang, DisplayScreen.Palette.ErrBodyBgAnsi));
        }

        if (result.Count == 0)
            result.Add(name);
        return result;
    }

    private enum RespMode { Code, Bash, Markdown }

    // Picks how a tool's response body is rendered. File and shell output stay as un-wrapped highlighted
    // lines (code columns line up; wide output is meant to scroll horizontally). Everything else — web
    // fetches, searches, subagent returns — is prose, so it flows through the markdown pipeline.
    private static RespMode ResponseModeFor(string toolName)
    {
        switch (toolName)
        {
            case "read_file":
            case "write_file":
            case "edit_file":
            case "edit_file_replace":
            case "edit_file_insert":
            case "ls":
                return RespMode.Code;
            case "bash":
                return RespMode.Bash;
            default:
                return RespMode.Markdown;
        }
    }

    // Emits a tool response into result. Markdown mode interprets and word-wraps prose; the other modes
    // emit one highlighted line per source line (no wrap), keeping code and shell columns intact.
    private static void EmitResponseLines(List<string> result, string content, RespMode mode, string fileLang, string bgAnsi, int w)
    {
        if (mode == RespMode.Markdown)
        {
            foreach (string mdLine in MarkdownAnsi.Render(ExpandTabs(content), FrameType.Output, w))
            {
                foreach (string wrapped in AnsiString.WordWrapIndented(mdLine, w))
                    result.Add(wrapped);
            }
        }
        else
        {
            string lang = mode == RespMode.Bash ? "bash" : fileLang;
            foreach (string respLine in content.Replace("\r\n", "\n").Split('\n'))
                result.Add(MarkdownAnsi.SyntaxHighlight(ExpandTabs(respLine), lang, bgAnsi));
        }
    }

    // True for string arguments that carry free-form markdown prose rather than code, paths, or short
    // scalars. These are rendered through the markdown pipeline in the expanded tool-call body.
    private static bool IsProseArg(string toolName, string propName)
    {
        if (toolName == "return_to_caller" && propName.Equals("output", StringComparison.OrdinalIgnoreCase)) return true;
        if (toolName == "finish_review" && propName.Equals("comments", StringComparison.OrdinalIgnoreCase)) return true;
        if (toolName == "state_transition" && propName.Equals("context", StringComparison.OrdinalIgnoreCase)) return true;
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
            case "write_file":
            case "edit_file_replace":
            case "edit_file_insert":
                set.Add("file_path"); break;
            case "bash":
                set.Add("command"); break;
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
            "bash"                                                     => BuildRunCommandSummary(label, Get("command")),
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
