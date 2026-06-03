using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;


// Builds the tool dictionary with explicit registrations — no reflection.
public static class ToolFactory
{
    public static Dictionary<string, Tool> Build(WebSearchConfig? webSearchConfig)
    {
        Dictionary<string, Tool> tools = new(StringComparer.OrdinalIgnoreCase);

        Register(tools, "glob",
            "Search for files matching a glob pattern.",
            Params(
                Req("pattern", "string", "Glob pattern to match (e.g. **/*.cs)."),
                Req("path", "string", "Directory to search in.")),
            async (args, ct, transport) =>
            {
                string pattern = Str(args, "pattern");
                string path = Str(args, "path");
                return await SearchTools.GlobAsync(pattern, path, ct);
            });

        Register(tools, "grep",
            "Search file contents for a pattern.",
            Params(
                Req("path", "string", "Directory or file path to search."),
                Req("pattern", "string", "Text or regex pattern to search for."),
                Opt("context_lines", "integer", "Number of context lines to include around each match.")),
            async (args, ct, transport) =>
            {
                string path = Str(args, "path");
                string pattern = Str(args, "pattern");
                int? contextLines = IntOpt(args, "context_lines");
                return await SearchTools.GrepAsync(path, pattern, contextLines, ct);
            });

        Register(tools, "list_directory",
            "List files and directories at a path.",
            Params(
                Req("path", "string", "Directory path to list."),
                Opt("pattern", "string", "Optional glob pattern to filter results.")),
            async (args, ct, transport) =>
            {
                string path = Str(args, "path");
                string? pattern = StrOpt(args, "pattern");
                return await SearchTools.ListDirectoryAsync(path, pattern, ct);
            });

        WebFetch webFetch = new();
        Register(tools, "fetch_page",
            "Fetch the text content of a web page.",
            Params(
                Req("url", "string", "The fully-formed URL to fetch content from.")),
            async (args, ct, transport) =>
            {
                string url = Str(args, "url");
                return await webFetch.FetchPageAsync(url, ct);
            });

        if (webSearchConfig?.Openrouter != null && webSearchConfig.Openrouter.Enabled)
        {
            WebSearchOpenrouter webSearch = new(webSearchConfig.Openrouter.BuildModel());
            Register(tools, "search_web",
                "Search the web using a natural language question or search query.",
                Params(
                    Req("query", "string", "The search query or natural language question to answer using the web.")),
                async (args, ct, transport) =>
                {
                    string query = Str(args, "query");
                    return await webSearch.SearchWebAsync(query, transport, ct);
                });
        }

        Register(tools, "bash",
            "Execute a shell command.",
            Params(
                Req("command", "string", "The shell command to execute."),
                Opt("working_dir", "string", "Optional working directory for the command."),
                Opt("timeout_seconds", "integer", "Optional timeout in seconds.")),
            async (args, ct, transport) =>
            {
                string command = Str(args, "command");
                string? workingDir = StrOpt(args, "working_dir");
                int? timeoutSeconds = IntOpt(args, "timeout_seconds");
                return await ShellTools.BashAsync(command, workingDir, timeoutSeconds, ct);
            });

        Register(tools, "read_file",
            "Reads a file in modified cat -n format with hash anchors per line. CWD is /workspace/",
            Params(
                Req("file_path", "string", "File path"),
                Opt("offset", "string", "Starting line number (1 based)"),
                Opt("lines", "string", "Number of lines to read. Empty means to the end of the file.")),
            async (args, ct, transport) =>
            {
                string filePath = Str(args, "file_path");
                string offset = Str(args, "offset");
                string lines = Str(args, "lines");
                return await FileTools.ReadFileAsync(filePath, offset, lines, ct);
            });

        Register(tools, "write_file",
            "Create a new file or overwrite an existing one. If the file already exists, you must read_file first. Prefer edit_file for partial changes. Only create files required by the task. Temporary files should go in /tmp/",
            Params(
                Req("file_path", "string", "File path"),
                Req("content", "string", "Complete file contents")),
            async (args, ct, transport) =>
            {
                string filePath = Str(args, "file_path");
                string content = Str(args, "content");
                return await FileTools.WriteFileAsync(filePath, content, ct);
            });

        Register(tools, "edit_file_replace",
            "Replace a block of text defined by the start and end line:hash anchors. CWD is /workspace/",
            Params(
                Req("file_path", "string", "File path"),
                Req("start_anchor", "string", "Start anchor"),
                Req("end_anchor", "string", "End anchor"),
                Req("new_text", "string", "Replacement text")),
            async (args, ct, transport) =>
            {
                string filePath = Str(args, "file_path");
                string startAnchor = Str(args, "start_anchor");
                string endAnchor = Str(args, "end_anchor");
                string newText = Str(args, "new_text");
                return await FileTools.EditFileReplaceAsync(filePath, startAnchor, endAnchor, newText, ct);
            });

        Register(tools, "edit_file_insert",
            "Insert a line of text AFTER the indicated line:hash anchor. CWD is /workspace/",
            Params(
                Req("file_path", "string", "File path"),
                Req("anchor", "string", "Line anchor"),
                Req("new_text", "string", "Text to insert")),
            async (args, ct, transport) =>
            {
                string filePath = Str(args, "file_path");
                string anchor = Str(args, "anchor");
                string newText = Str(args, "new_text");
                return await FileTools.EditFileInsertAsync(filePath, anchor, newText, ct);
            });

        return tools;
    }

    private static async Task<ToolResult> TruncateIfNeeded(Task<ToolResult> resultTask, CancellationToken cancellationToken)
    {
        const int MaxContentLength = 4000;
        const int HeadLength = 1024;
        const int TailLength = 1024;

        ToolResult result = await resultTask;

        if (result.Response.Length <= MaxContentLength)
        {
            return result;
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"beast_tool_output_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempPath, result.Response, cancellationToken);

        string head = result.Response.Substring(0, HeadLength);
        string tail = result.Response.Substring(result.Response.Length - TailLength, TailLength);

        int skippedLength = result.Response.Length - HeadLength - TailLength;

        StringBuilder truncated = new StringBuilder();
        truncated.Append(head);
        truncated.AppendLine();
        truncated.AppendLine();
        truncated.AppendLine($"... [{skippedLength} characters omitted] ...");
        truncated.AppendLine();
        truncated.Append(tail);
        truncated.AppendLine();
        truncated.AppendLine();
        truncated.AppendLine($"Full output saved to: {tempPath}");

        return new ToolResult(truncated.ToString(), result.MessageHandled);
    }

    private static void Register(Dictionary<string, Tool> tools, string name, string description, JsonObject parameters, Func<JsonObject, CancellationToken, ITransportServer, Task<ToolResult>> handler)
    {
        tools[name] = new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition { Name = name, Description = description, Parameters = parameters }
            },
            Handler = async (args, ct, transport) => await TruncateIfNeeded(handler(args, ct, transport), ct)
        };						   
    }

    private static JsonObject Params(params JsonObject[] props)
    {
        JsonObject properties = new();
        JsonArray required = new();

        foreach (JsonObject prop in props)
        {
            string name = (string)prop["_name"]!;
            bool isRequired = (bool)prop["_required"]!;
            prop.Remove("_name");
            prop.Remove("_required");
            properties[name] = prop;
            if (isRequired) required.Add(name);
        }

        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0) schema["required"] = required;

        return schema;
    }

    private static JsonObject Req(string name, string type, string description)
    {
        return new JsonObject { ["_name"] = name, ["_required"] = true, ["type"] = type, ["description"] = description };
    }

    private static JsonObject Opt(string name, string type, string description)
    {
        return new JsonObject { ["_name"] = name, ["_required"] = false, ["type"] = type, ["description"] = description };
    }

    private static string Str(JsonObject args, string key)
    {
        if (args.TryGetPropertyValue(key, out JsonNode? node) && node != null) return node.ToString();
        return string.Empty;
    }

    private static string? StrOpt(JsonObject args, string key)
    {
        if (args.TryGetPropertyValue(key, out JsonNode? node) && node != null) return node.ToString();
        return null;
    }

    private static int? IntOpt(JsonObject args, string key)
    {
        if (args.TryGetPropertyValue(key, out JsonNode? node) && node != null && int.TryParse(node.ToString(), out int v)) return v;
        return null;
    }
}
