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

        WebFetch webFetch = new();
        Register(tools, "fetch_page",
            "Fetch the contents of a web page at the specified URL. Returns the text content with HTML tags stripped.",
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
                "Search the web using OpenRouter's web search plugin. The query can be a natural language question or instruction, not just keywords — e.g. 'Show me how to call the Foo API and explain each parameter'.",
                Params(
                    Req("query", "string", "The search query or natural language question to answer using the web.")),
                async (args, ct, transport) =>
                {
                    string query = Str(args, "query");
                    return await webSearch.SearchWebAsync(query, transport, ct);
                });
        }

        Register(tools, "bash",
            "Standard bash command. CWD is /workspace/",
            Params(
                Req("command", "string", "Shell command to execute"),
                Opt("working_dir", "string", "Optional working directory for the command."),
                Opt("timeout_seconds", "integer", "Timeout in seconds (default 60).")),
            async (args, ct, transport) =>
            {
                string command = Str(args, "command");
                int? timeoutSeconds = IntOpt(args, "timeout_seconds");
                return await ShellTools.BashAsync(command, timeoutSeconds, ct);
            });

        Register(tools, "read_file",
            "Read a file and return its contents. CWD is /workspace/",
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
            "Create a new file or overwrite an existing one. If the file already exists, you must read_file first. Prefer edit_file for partial changes. Only create files required by the task. CWD is /workspace/ but temporary files should go in /tmp/",
            Params(
                Req("file_path", "string", "File path"),
                Req("content", "string", "Complete file contents")),
            async (args, ct, transport) =>
            {
                string filePath = Str(args, "file_path");
                string content = Str(args, "content");
                return await FileTools.WriteFileAsync(filePath, content, ct);
            });

        Register(tools, "edit_file",
            "Replace old_text with new_text in a file. Tries exact match first; if not found, retries with all whitespace stripped from both old_text and the file to find the region, then replaces it. CWD is /workspace/",
            Params(
                Req("file_path", "string", "File path"),
                Req("old_text", "string", "Text to find and replace"),
                Req("new_text", "string", "Replacement text")),
            async (args, ct, transport) =>
            {
                string filePath = Str(args, "file_path");
                string oldText = Str(args, "old_text");
                string newText = Str(args, "new_text");
                return await FileTools.EditFileAsync(filePath, oldText, newText, ct);
            });

        return tools;
    }

    private static async Task<ToolResult> TruncateIfNeeded(Task<ToolResult> resultTask, CancellationToken cancellationToken)
    {
        const int MaxContentLength = 5000;
            const int HeadLength = 2500;
            const int TailLength = 2500;

            ToolResult result = await resultTask;

            // Determine which content to truncate based on exit code
            string fullContent = result.ExitCode == 0 ? result.StdOut : result.StdErr;

            if (fullContent.Length <= MaxContentLength)
            {
                return result;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"beast_tool_output_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempPath, fullContent, cancellationToken);

            string head = fullContent.Substring(0, HeadLength);
            string tail = fullContent.Substring(fullContent.Length - TailLength, TailLength);

            int skippedLength = fullContent.Length - HeadLength - TailLength;

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

            // Return truncated version in the appropriate field
            if (result.ExitCode == 0)
            {
                return new ToolResult(truncated.ToString(), result.StdErr, result.ExitCode);
            }
            else
            {
                return new ToolResult(result.StdOut, truncated.ToString(), result.ExitCode);
            }
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

    private static int? IntOpt(JsonObject args, string key)
    {
        if (args.TryGetPropertyValue(key, out JsonNode? node) && node != null && int.TryParse(node.ToString(), out int v)) return v;
        return null;
    }
}
