using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Builds the tool dictionary with explicit registrations — no reflection.
public static class ToolFactory
{
    private const int MaxResponseLength = 160000;
    private const int TruncateHeadLength = 80000;
    private const int TruncateTailLength = 80000;

    public static Dictionary<string, Tool> Build(WebSearchConfig? webSearchConfig)
    {
        Dictionary<string, Tool> tools = new(StringComparer.OrdinalIgnoreCase);

        Register(tools, "glob",
            "Search for files matching a glob pattern.",
            Params(
                Req("pattern", "string", "Glob pattern to match (e.g. **/*.cs)."),
                Req("path", "string", "Directory to search in.")),
            async (args, ct) =>
            {
                string pattern = Str(args, "pattern");
                string path = Str(args, "path");
                return Truncate(await SearchTools.GlobAsync(pattern, path));
            });

        Register(tools, "grep",
            "Search file contents for a pattern.",
            Params(
                Req("path", "string", "Directory or file path to search."),
                Req("pattern", "string", "Text or regex pattern to search for."),
                Opt("context_lines", "integer", "Number of context lines to include around each match.")),
            async (args, ct) =>
            {
                string path = Str(args, "path");
                string pattern = Str(args, "pattern");
                int? contextLines = IntOpt(args, "context_lines");
                return Truncate(await SearchTools.GrepAsync(path, pattern, contextLines));
            });

        Register(tools, "list_directory",
            "List files and directories at a path.",
            Params(
                Req("path", "string", "Directory path to list."),
                Opt("pattern", "string", "Optional glob pattern to filter results.")),
            async (args, ct) =>
            {
                string path = Str(args, "path");
                string? pattern = StrOpt(args, "pattern");
                return Truncate(await SearchTools.ListDirectoryAsync(path, pattern));
            });

        WebFetch webFetch = new();
        Register(tools, "fetch_page",
            "Fetch the text content of a web page.",
            Params(
                Req("url", "string", "The fully-formed URL to fetch content from.")),
            async (args, ct) =>
            {
                string url = Str(args, "url");
                return Truncate(await webFetch.FetchPageAsync(url, ct));
            });

        if (webSearchConfig?.Openrouter != null && webSearchConfig.Openrouter.Enabled)
        {
            WebSearchOpenrouter webSearch = new(webSearchConfig.Openrouter.BuildModel());
            Register(tools, "search_web",
                "Search the web using a natural language question or search query.",
                Params(
                    Req("query", "string", "The search query or natural language question to answer using the web.")),
                async (args, ct) =>
                {
                    string query = Str(args, "query");
                    return Truncate(await webSearch.SearchWebAsync(query, ct));
                });
        }

        Register(tools, "bash",
            "Execute a shell command.",
            Params(
                Req("command", "string", "The shell command to execute."),
                Opt("working_dir", "string", "Optional working directory for the command."),
                Opt("timeout_seconds", "integer", "Optional timeout in seconds.")),
            async (args, ct) =>
            {
                string command = Str(args, "command");
                string? workingDir = StrOpt(args, "working_dir");
                int? timeoutSeconds = IntOpt(args, "timeout_seconds");
                return Truncate(await ShellTools.BashAsync(command, workingDir, timeoutSeconds, ct));
            });

        Register(tools, "read_file",
            "Read the contents of a file.",
            Params(
                Req("file_path", "string", "Absolute path to the file to read."),
                Opt("offset", "string", "The line number to start reading from (1 based). Only provide if the file is too large to read at once."),
                Opt("lines", "string", "The number of lines to read. Only provide if the file is too large to read at once.")),
            async (args, ct) =>
            {
                string filePath = Str(args, "file_path");
                string offset = Str(args, "offset");
                string lines = Str(args, "lines");
                return Truncate(await FileTools.ReadFileAsync(filePath, offset, lines, ct));
            });

        Register(tools, "write_file",
            "Create a new file or overwrite an existing one. If the file already exists, you must read_file first. Prefer edit_file for partial changes. Never create files not required by the task.",
            Params(
                Req("file_path", "string", "The exact full path to the file to create or overwrite. Absolute paths only."),
                Req("content", "string", "The complete content to write to the file. This replaces the entire file contents.")),
            async (args, ct) =>
            {
                string filePath = Str(args, "file_path");
                string content = Str(args, "content");
                return Truncate(await FileTools.WriteFileAsync(filePath, content, ct));
            });

        Register(tools, "edit_file",
            "Apply multiple line-anchored edits to a file. The edits parameter is a JSON string representing an ordered array of operations.",
            Params(
                Req("file_path", "string", "Absolute path to the file to modify."),
                Req("edits", "string", "A JSON string encoding an ordered array of edit operations (see tool description).")),
            async (args, ct) =>
            {
                string filePath = Str(args, "file_path");
                string edits = Str(args, "edits");
                return Truncate(await FileTools.EditFileAsync(filePath, edits, ct));
            });

        Register(tools, "edit_file_replace",
            "Apply a single replace_lines edit to a file.",
            Params(
                Req("file_path", "string", "Absolute path to the file to modify."),
                Req("start_anchor", "string", "Start anchor in the form '<line>:<hh>'."),
                Req("end_anchor", "string", "End anchor in the form '<line>:<hh>'."),
                Req("new_text", "string", "Replacement text to insert between the anchors.")),
            async (args, ct) =>
            {
                string filePath = Str(args, "file_path");
                string startAnchor = Str(args, "start_anchor");
                string endAnchor = Str(args, "end_anchor");
                string newText = Str(args, "new_text");
                return Truncate(await FileTools.EditFileReplaceAsync(filePath, startAnchor, endAnchor, newText, ct));
            });

        Register(tools, "edit_file_insert",
            "Apply a single insert_after edit to a file.",
            Params(
                Req("file_path", "string", "Absolute path to the file to modify."),
                Req("anchor", "string", "Anchor in the form '<line>:<hh>'."),
                Req("new_text", "string", "Text to insert after the anchor.")),
            async (args, ct) =>
            {
                string filePath = Str(args, "file_path");
                string anchor = Str(args, "anchor");
                string newText = Str(args, "new_text");
                return Truncate(await FileTools.EditFileInsertAsync(filePath, anchor, newText, ct));
            });

        return tools;
    }

    private static void Register(Dictionary<string, Tool> tools, string name, string description, JsonObject parameters, Func<JsonObject, CancellationToken, Task<ToolResult>> handler)
    {
        tools[name] = new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition { Name = name, Description = description, Parameters = parameters }
            },
            Handler = handler
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

    private static ToolResult Truncate(ToolResult result)
    {
        if (result.MessageHandled || result.Response.Length <= MaxResponseLength) return result;

        int omittedCount = result.Response.Length - TruncateHeadLength - TruncateTailLength;
        string head = result.Response.Substring(0, TruncateHeadLength);
        string tail = result.Response.Substring(result.Response.Length - TruncateTailLength);
        return new ToolResult($"{head}\n\n... [{omittedCount} characters omitted] ...\n\n{tail}", result.MessageHandled);
    }
}
