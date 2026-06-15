using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Builds the tool dictionary with explicit registrations — no reflection.
public static class ToolFactory
{
    // Builds the role-assignable tool set. The two delegation tools (subagent, return_to_caller) are
    // created by CreateSubagentTool / CreateReturnToCallerTool and added to a session's tool list in
    // code (the root gets subagent; SubagentRunner gives each child return_to_caller).
    public static Dictionary<string, Tool> Build(WebSearchConfig? webSearchConfig)
    {
        Dictionary<string, Tool> tools = new(StringComparer.OrdinalIgnoreCase);

        WebFetch webFetch = new();
        Register(tools, "fetch_page",
            "Fetch the contents of a web page at the specified URL. Returns the text content with HTML tags stripped.",
            Params(
                Req("url", "string", "The fully-formed URL to fetch content from.")),
            async (args, toolCallId, ct, transport, sessionId) =>
            {
                string url = Str(args, "url");
                return await webFetch.FetchPageAsync(toolCallId, url, ct);
            });

        if (webSearchConfig?.Openrouter != null && webSearchConfig.Openrouter.Enabled)
        {
            WebSearchOpenrouter webSearch = new(webSearchConfig.Openrouter.BuildModel());
            Register(tools, "search_web",
                "Search the web using a natural language query.",
                Params(
                    Req("query", "string", "Query or natural language question to answer using the web.")),
                async (args, toolCallId, ct, transport, sessionId) =>
                {
                    string query = Str(args, "query");
                    return await webSearch.SearchWebAsync(toolCallId, query, transport, sessionId, ct);
                });
        }

        Register(tools, "bash",
            "Standard bash command. CWD is at the root of the repo at /workspace/",
            Params(
                Req("command", "string", "Shell command to execute"),
                Opt("timeout_seconds", "integer", "Timeout in seconds (default 120).")),
            async (args, toolCallId, ct, transport, sessionId) =>
            {
                string command = Str(args, "command");
                int? timeoutSeconds = IntOpt(args, "timeout_seconds");
                return await ShellTools.BashAsync(toolCallId, command, timeoutSeconds, ct);
            });

        Register(tools, "read_file",
            "Read a file and return its contents. CWD is at the root of the repo at /workspace/",
            Params(
                Req("file_path", "string", "File path"),
                Req("offset", "string", "Starting line number (1 based)"),
                Opt("lines", "string", "Number of lines to read. Empty means to the end of the file.")),
            async (args, toolCallId, ct, transport, sessionId) =>
            {
                string filePath = Str(args, "file_path");
                string offset = Str(args, "offset");
                string lines = Str(args, "lines");
                return await FileTools.ReadFileAsync(toolCallId, filePath, offset, lines, ct);
            });

        Register(tools, "write_file",
            "Create a new file or overwrite an existing one (if you used read_file already). CWD is /workspace/ but temporary files should go in /tmp/",
            Params(
                Req("file_path", "string", "File path"),
                Req("content", "string", "Complete file contents")),
            async (args, toolCallId, ct, transport, sessionId) =>
            {
                string filePath = Str(args, "file_path");
                string content = Str(args, "content");
                return await FileTools.WriteFileAsync(toolCallId, filePath, content, ct);
            });

        Register(tools, "edit_file",
            "Replace old_text with new_text in a file. Tries exact match first; if not found, retries ignoring all whitespace. CWD is at the root of the repo at /workspace/",
            Params(
                Req("file_path", "string", "File path"),
                Req("old_text", "string", "Text to find and replace"),
                Req("new_text", "string", "Replacement text")),
            async (args, toolCallId, ct, transport, sessionId) =>
            {
                string filePath = Str(args, "file_path");
                string oldText = Str(args, "old_text");
                string newText = Str(args, "new_text");
                return await FileTools.EditFileAsync(toolCallId, filePath, oldText, newText, ct);
            });

        return tools;
    }

    // Creates the delegation tool. The root agent adds this to its tool list (it is never role-assignable
    // and never given to a child, so subagents cannot recursively spawn). runSubagent launches the child
    // session, runs it to completion, and returns its final result fit to the caller's budget. roleNames
    // is listed in the role argument description so the model picks a valid role.
    public static Tool CreateSubagentTool(IEnumerable<string> roleNames, Func<string, string, int, CancellationToken, Task<(string? text, int responseTokens)>> runSubagent)
    {
        string roleList = string.Join(", ", roleNames);

        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "subagent",
                    Description = "Instruct a subagent to do work. The subagent gets a git worktree that you name with the provided role and tools, works to completion, and returns an update.",
                    Parameters = Params(
                        Req("role", "string", $"The role to assign the child agent. It receives that role's system prompt and tool set. Valid roles: {roleList}."),
                        Req("prompt", "string", "The task for the child agent, written in natural language as if you were the user instructing it."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string role = Str(args, "role");
                string prompt = Str(args, "prompt");
                if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(prompt))
                    return new ToolResult(toolCallId, string.Empty, "Error: subagent requires both 'role' and 'prompt'.", 1, 0);

                (string? text, int responseTokens) = await runSubagent(role, prompt, maxOutputTokens, ct);
                if (text == null)
                    return new ToolResult(toolCallId, string.Empty, $"Error: could not start subagent for role '{role}' (unknown role or no available model).", 1, 0);

                return new ToolResult(toolCallId, text, string.Empty, 0, Math.Max(1, responseTokens));
            }
        };
    }

    // Creates the root agent's termination tool. onComplete receives the final status; SessionRunner
    // adds this to the root's tool list and stops keeping the model on task once it is called.
    public static Tool CreateTaskCompleteTool(Action<string> onComplete)
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "task_complete",
                    Description = "Declare this task finished and stop work.",
                    Parameters = Params(
                        Req("status", "string", "Provide proof this task is complete with any final summary or details the user should read."))
                }
            },
            Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string status = Str(args, "status");
                onComplete(status);
                string ack = "Task marked complete.";
                return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
            }
        };
    }

    // Creates the explicit termination tool a subagent calls to finish. onReturn receives the final
    // output; SubagentRunner adds this to each child's tool list and reads what onReturn captured.
    public static Tool CreateReturnToCallerTool(Action<string> onReturn)
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "return_to_caller",
                    Description = "Call this to declare your task finished.",
                    Parameters = Params(
                        Req("output", "string", "This string is the entire response the caller receives. Include high-level details like what files were modified, changes to interfaces required,  tests passed, or any other changes to the project that the orchestrator agent should know about."))
                }
            },
            Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string output = Str(args, "output");
                onReturn(output);
                string ack = "Returned to caller.";
                return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
            }
        };
    }

    private static void Register(Dictionary<string, Tool> tools, string name, string description, JsonObject parameters, Func<JsonObject, string, CancellationToken, ITransportServer, string, Task<ToolResult>> handler)
    {
        tools[name] = new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition { Name = name, Description = description, Parameters = parameters }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                // Raw tool output is server-unmeasured: estimate its size and truncate to the budget.
                ToolResult raw = await handler(args, toolCallId, ct, transport, sessionId);
                return ToolDispatch.MeasureRawResult(raw, maxOutputTokens);
            }
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
