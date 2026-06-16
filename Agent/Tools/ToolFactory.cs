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

        if (webSearchConfig?.Openrouter != null && webSearchConfig.Openrouter.Enabled)
        {
            WebSearchOpenrouter webSearch = new(webSearchConfig.Openrouter.BuildModel());
            Register(tools, "search_web",
                "Search the web using a natural language query.",
                Params(
                    Req("query", "string", "Describe what you are searching for and request specific details if possible.")),
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

        Register(tools, "ls",
            "List a folder's contents. CWD is the repo root at /workspace/.",
            Params(
                Req("folder", "string", "Folder to list.")),
            async (args, toolCallId, ct, transport, sessionId) =>
            {
                string folder = Str(args, "folder");
                return await ShellTools.LsAsync(toolCallId, folder, ct);
            });

        return tools;
    }

    // Creates the read_file tool. Like fetch_url it needs the registry/roleService and the current session,
    // because the first read of a file in a session is interpreted by the Explorer role (see ReadFileExplorer)
    // rather than returned raw. The ReadFileExplorer is shared so its per-session "already read" set persists
    // across turns; the same instance is handed to the root and to subagents.
    public static Tool CreateReadFileTool(ReadFileExplorer explorer, LlmRegistry registry, RoleService roleService, Func<Session> currentSession)
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "read_file",
                    Description = "Read a file. The first read of a file is interpreted by the Explorer role, which returns citations relevant to your goal; later reads of the same file return its raw contents. CWD is the repo root at /workspace/.",
                    Parameters = Params(
                        Req("file_path", "string", "File path"),
                        Req("goal", "string", "What you are trying to find or understand in this file. Used to focus the citations returned on the first read."),
                        Req("offset", "string", "Starting line number (1 based)"),
                        Opt("lines", "string", "Number of lines to read. Empty means to the end of the file."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string filePath = Str(args, "file_path");
                string goal = Str(args, "goal");
                string offset = Str(args, "offset");
                string lines = Str(args, "lines");
                return await explorer.ReadAsync(toolCallId, filePath, offset, lines, goal, registry, roleService, transport, currentSession(), maxOutputTokens, ct);
            }
        };
    }

    // Creates the fetch_url tool. It always routes through the Web role inside WebFetch.FetchRawAsync, which
    // fetches the page and interprets it, returning only what the objective asks for. Injected by name like
    // the other in-code tools, since it needs the registry/roleService and the current session.
    public static Tool CreateFetchUrlTool(LlmRegistry registry, RoleService roleService, Func<Session> currentSession)
    {
        WebFetch webFetch = new WebFetch();
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "fetch_url",
                    Description = "Fetch a web page and get back only the information you ask for. The page is read by the Web role, which returns just what your objective describes.",
                    Parameters = Params(
                        Req("url", "string", "The fully-formed URL to fetch content from."),
                        Req("objective", "string", "Explain exactly what you are looking for and how that information will be used, so only that is returned."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string url = Str(args, "url");
                string objective = Str(args, "objective");
                return await webFetch.FetchRawAsync(toolCallId, url, objective, registry, roleService, transport, currentSession(), maxOutputTokens, ct);
            }
        };
    }

    // Creates the delegation tool. The root agent adds this to its tool list (it is never role-assignable
    // and never given to a child, so subagents cannot recursively spawn). runSubagent launches the child
    // session, runs it to completion, and returns its final result fit to the caller's budget. roleNames
    // is listed in the role argument description so the model picks a valid role.
    public static Tool CreateSubagentTool(IEnumerable<Role> subagentRoles, Func<string, string, int, CancellationToken, Task<(bool ok, string text, int responseTokens)>> runSubagent)
    {
        string roleList = string.Empty;
		foreach (Role r in subagentRoles)
		{
			roleList += $"{r.Name} -> {r.Description}\n";
		}

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

                (bool ok, string text, int responseTokens) = await runSubagent(role, prompt, maxOutputTokens, ct);
                if (!ok)
                    return new ToolResult(toolCallId, string.Empty, text, 1, Math.Max(1, ToolDispatch.EstimateTokens(text)));

                return new ToolResult(toolCallId, text, string.Empty, 0, Math.Max(1, responseTokens));
            }
        };
    }

    // Creates the Task agent's termination tool. onComplete receives the final status; SessionRunner
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

    // Creates the Default agent's tool for kicking off a task. onStart receives the objective and the new
    // branch name and runs the role transition's exit/enter hooks; it returns string.Empty on success or
    // the hook error otherwise. On error the task does not start and the error is returned to the model as
    // the tool result. branchContext (current branch + existing worktrees) goes in the branch argument's
    // description so the model picks a name that does not collide.
    public static Tool CreateStartTaskTool(string branchContext, Func<string, string, CancellationToken, Task<string>> onStart)
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "start_task",
                    Description = "Begin working a task. This starts a new Task session, providing an objective that it will work on.",
                    Parameters = Params(
                        Req("objective", "string", "Describe the task, written as a clear instruction for the agent that will carry it out."),
                        Req("branch", "string", "Name for the git worktree branch this task should run on. Choose one of these to continue work, or a different name to start fresh:\n" + branchContext))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string objective = Str(args, "objective");
                string branch = Str(args, "branch");
                if (string.IsNullOrWhiteSpace(objective))
                    return new ToolResult(toolCallId, string.Empty, "Error: start_task requires an objective.", 1, 0);
                if (string.IsNullOrWhiteSpace(branch))
                    return new ToolResult(toolCallId, string.Empty, "Error: start_task requires a branch name.", 1, 0);

                string error = await onStart(objective, branch, ct);
                if (!string.IsNullOrEmpty(error))
                    return new ToolResult(toolCallId, string.Empty, error, 1, ToolDispatch.EstimateTokens(error));

                string ack = "Starting task.";
                return new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack));
            }
        };
    }

    // Creates the Reviewer's termination tool. onFinish receives the approval flag and the review comments;
    // SubagentRunner adds this to a Reviewer child's tool list, reads what onFinish captured, and on approval
    // integrates the worktree branch into main before returning the review to the caller.
    public static Tool CreateFinishReviewTool(Action<bool, string> onFinish)
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "finish_review",
                    Description = "Call this to finish the review. approved=true accepts the change and triggers an automatic commit and rebase of the worktree branch onto main (linear history, no merge commit — you do not run any git yourself); approved=false rejects it for the developer to fix.",
                    Parameters = Params(
                        Req("approved", "boolean", "True to accept the change, false to reject it."),
                        Req("comments", "string", "Your review: what you checked and why you approved, or exactly what the developer must fix. This string is the entire response the caller receives."))
                }
            },
            Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                bool approved = Bool(args, "approved");
                string comments = Str(args, "comments");
                onFinish(approved, comments);
                string ack = approved ? "Review approved." : "Review rejected.";
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

    private static bool Bool(JsonObject args, string key)
    {
        if (args.TryGetPropertyValue(key, out JsonNode? node) && node != null && bool.TryParse(node.ToString(), out bool v)) return v;
        return false;
    }
}
