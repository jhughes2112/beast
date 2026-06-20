using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Builds the tool dictionary with explicit registrations — no reflection.
public static class ToolFactory
{
    // Builds the role-assignable tool set. The delegation and terminator tools are not registered here:
    // they are created by their Create* factories and added to a session's tool list in code (the root gets
    // assign_work; SubagentRunner gives the Developer review_work / commit_and_rebase and each child its
    // terminator).
    public static Dictionary<string, Tool> Build()
    {
        Dictionary<string, Tool> tools = new(StringComparer.OrdinalIgnoreCase);

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

        Register(tools, "readonly_bash",
            "Read-only bash for inspecting the repo without changing it. Runs in a restricted shell: no output redirection (>, >>), no cd, no running programs by an explicit path, and only a curated read-only toolset (cat, ls, grep, git, head/tail, wc, sort, diff, jq, ...) is on PATH. CWD is the repo root at /workspace/.",
            Params(
                Req("command", "string", "Read-only shell command to execute"),
                Opt("timeout_seconds", "integer", "Timeout in seconds (default 120).")),
            async (args, toolCallId, ct, transport, sessionId) =>
            {
                string command = Str(args, "command");
                int? timeoutSeconds = IntOpt(args, "timeout_seconds");
                return await ShellTools.ReadonlyBashAsync(toolCallId, command, timeoutSeconds, ct);
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

    // A raw read_file window cap for the main agents: large enough to read a file in a few reads, bounded so a
    // single read cannot flood the calling agent's context. Lines past it are reported, never errored.
    private const int ReadFileMaxLines = 500;

    // Creates the read_file tool: a plain, raw reader. It returns the requested window of a file verbatim and
    // does nothing else, so unlike fetch_url it needs no registry/role/session. For a goal-focused concept map
    // of a file, use find_relevant_file_sections (see CreateSummarizeFileTool) instead.
    public static Tool CreateReadFileTool()
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "read_file",
                    Description = "Read a file's raw contents. Returns up to 500 lines starting at offset. CWD is the repo root at /workspace/.",
                    Parameters = Params(
                        Req("file_path", "string", "File path"),
                        Opt("offset", "integer", "Starting line number (1 based). Omit for the beginning of the file."),
                        Opt("lines", "integer", "Number of lines to read. Omit to read to the end of the file (capped at 500)."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string filePath = Str(args, "file_path");
                string offset = Str(args, "offset");
                string lines = Str(args, "lines");
                ToolResult raw = await FileTools.ReadFileAsync(toolCallId, filePath, offset, lines, ReadFileMaxLines, false, ct);
                return ToolDispatch.MeasureRawResult(raw, maxOutputTokens);
            }
        };
    }

    // Creates the find_relevant_file_sections tool. It reads a file window and routes it through the Explorer role (see
    // FileSummarizer), returning a goal-focused concept map of cited line ranges rather than the raw contents,
    // so like fetch_url it needs the registry/roleService and the current session. The summarizer is stateless;
    // the root and each subagent make their own.
    public static Tool CreateSummarizeFileTool(FileSummarizer summarizer, LlmRegistry registry, RoleService roleService, Func<Session> currentSession)
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "find_relevant_file_sections",
                    Description = "Find the sections of a file relevant to a goal: returns a concept map — cited line ranges with the functions and symbols in each — so you can target follow-up read_file calls. Small files (under 50 lines or 2KB) are returned whole. CWD is the repo root at /workspace/.",
                    Parameters = Params(
                        Req("file_path", "string", "File path"),
                        Req("goal", "string", "What you are trying to find or understand in this file. Used to focus the citations returned."),
                        Opt("offset", "integer", "Starting line number (1 based) for the window to digest. Omit for the beginning of the file."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string filePath = Str(args, "file_path");
                string goal = Str(args, "goal");
                string offset = Str(args, "offset");
                return await summarizer.SummarizeAsync(toolCallId, filePath, offset, goal, registry, roleService, transport, currentSession(), maxOutputTokens, ct);
            }
        };
    }

    // Creates the fetch_url tool. It always routes through the WebFetch role inside WebFetch.FetchRawAsync, which
    // fetches the page and interprets it, returning only what the goal asks for. Injected by name like
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
                    Description = "Fetch a web page and get back only the information you ask for. The page is read by the WebFetch role, which returns just what your goal describes.",
                    Parameters = Params(
                        Req("url", "string", "The fully-formed URL to fetch content from."),
                        Req("goal", "string", "Explain exactly what you are looking for and how that information will be used, so only that is returned."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string url = Str(args, "url");
                string goal = Str(args, "goal");
                return await webFetch.FetchRawAsync(toolCallId, url, goal, registry, roleService, transport, currentSession(), maxOutputTokens, ct);
            }
        };
    }

    // Creates the search_web tool, or null when web search is not configured/enabled. It makes one bare call to
    // the OpenRouter search model (inside WebSearchOpenrouter.SearchWebAsync) and returns that model's answer as
    // is, so it is injected by name and needs the roleService and the current session. The search model is built
    // once from settings here.
    public static Tool? CreateSearchWebTool(WebSearchConfig? webSearchConfig, RoleService roleService, Func<Session> currentSession)
    {
        if (webSearchConfig?.Openrouter == null || !webSearchConfig.Openrouter.Enabled)
            return null;

        WebSearchOpenrouter webSearch = new WebSearchOpenrouter(webSearchConfig.Openrouter.BuildModel());
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "search_web",
                    Description = "Search the web using a natural language query. A live web-search model retrieves and answers in one step; its answer is returned to you verbatim.",
                    Parameters = Params(
                        Req("query", "string", "Describe what should be retrieved from the web in enough detail that the top five hits will all be directly relevant to the task at hand."),
                        Req("goal", "string", "Provide a prompt to an agent so that it can return exactly and only what is necessary from the web pages. If you know exactly what you're looking for ask for it here."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string query = Str(args, "query");
                string goal = Str(args, "goal");
                return await webSearch.SearchWebAsync(toolCallId, query, goal, roleService, transport, currentSession(), maxOutputTokens, ct);
            }
        };
    }

    // A window cap for the Web helper's read_file: large enough to read a saved page in a few reads, bounded
    // so a single read cannot flood the helper's own context. Lines past it are reported, never errored.
    private const int WebHelperReadMaxLines = 2000;

    // Resolves a helper role's declared tool names (Role.Tools) to the tool instances a HelperSession runs.
    // This is a deliberately small, curated set distinct from the main registry: a helper sub-session must
    // not recurse (so read_file is the raw line-numbered reader, never the Explorer-backed one) and must not
    // hold write access. Names the helper set does not know are skipped, so editing a helper role in
    // roles.json can never wire in a registry tool that would be unsafe here. return_to_caller is added by
    // HelperSession on top of the result. A role with no tools (e.g. Explorer) gets an empty array.
    public static Tool[] BuildHelperTools(IReadOnlyList<string> toolNames)
    {
        Dictionary<string, Tool> available = new(StringComparer.OrdinalIgnoreCase);
        foreach (Tool tool in CreateWebHelperTools())
            available[tool.Definition.Function.Name] = tool;

        List<Tool> resolved = new List<Tool>();
        foreach (string name in toolNames)
        {
            if (available.TryGetValue(name, out Tool? tool))
                resolved.Add(tool);
        }
        return resolved.ToArray();
    }

    // Builds the tools a helper session may work with (see BuildHelperTools): a raw read_file (line-numbered
    // window, no Explorer round-trip) and bash, so the WebFetch role can parse, strip, grep, or download the files
    // a fetch saved to /tmp/.
    private static Tool[] CreateWebHelperTools()
    {
        Tool readFile = new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "read_file",
                    Description = "Read a line-numbered window of a file. Fetched pages are saved under /tmp/; CWD is /workspace/.",
                    Parameters = Params(
                        Req("file_path", "string", "File path"),
                        Opt("offset", "integer", "Starting line number (1 based). Omit for the beginning of the file."),
                        Opt("lines", "integer", "Number of lines to read. Omit to read to the end of the file (capped)."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string filePath = Str(args, "file_path");
                string offset = Str(args, "offset");
                string lines = Str(args, "lines");
                ToolResult raw = await FileTools.ReadFileAsync(toolCallId, filePath, offset, lines, WebHelperReadMaxLines, true, ct);
                return ToolDispatch.MeasureRawResult(raw, maxOutputTokens);
            }
        };

        Tool bash = new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "bash",
                    Description = "Standard bash command. Use it to inspect, parse, or download files (cat, grep, head, jq, curl, wget, ...). CWD is /workspace/.",
                    Parameters = Params(
                        Req("command", "string", "Shell command to execute"),
                        Opt("timeout_seconds", "integer", "Timeout in seconds (default 120)."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string command = Str(args, "command");
                int? timeoutSeconds = IntOpt(args, "timeout_seconds");
                ToolResult raw = await ShellTools.BashAsync(toolCallId, command, timeoutSeconds, ct);
                return ToolDispatch.MeasureRawResult(raw, maxOutputTokens);
            }
        };

        return new Tool[] { readFile, bash };
    }

    // Creates the Default agent's tool for handing a concrete unit of work to the Developer. runDeveloper
    // launches the Developer subagent (in a git worktree), runs it to completion, and returns its final
    // report fit to the caller's budget. Like review_work it targets one fixed role and takes no role
    // argument; the Developer reviews and integrates its own work, so the Default only delegates and waits.
    public static Tool CreateAssignWorkTool(Func<string, int, CancellationToken, Task<(bool ok, string text, int responseTokens)>> runDeveloper, Action onWorkAssigned)
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "assign_work",
                    Description = "Hand a concrete unit of work to the Developer subagent. It works in a git worktree, gets the change reviewed and integrated, and returns a report. After this, you stay in a work loop — you are re-prompted each turn to assign the next unit of work — until you call stop_work.",
                    Parameters = Params(
                        Req("prompt", "string", "The task for the Developer, written in natural language as if you were the user instructing it."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string prompt = Str(args, "prompt");
                if (string.IsNullOrWhiteSpace(prompt))
                    return new ToolResult(toolCallId, string.Empty, "Error: assign_work requires a 'prompt'.", 1, 0);

                // Delegating real work enters the work loop; stop_work is what leaves it.
                onWorkAssigned();

                (bool ok, string text, int responseTokens) = await runDeveloper(prompt, maxOutputTokens, ct);
                if (!ok)
                    return new ToolResult(toolCallId, string.Empty, text, 1, Math.Max(1, ToolDispatch.EstimateTokens(text)));

                return new ToolResult(toolCallId, text, string.Empty, 0, Math.Max(1, responseTokens));
            }
        };
    }

    // Creates the stop_work tool, the counterpart to assign_work. It is exposed only while a delegation loop
    // is active (the work-in-progress flag is set); calling it runs onStop to clear that flag, which ends the
    // end-of-turn re-prompting so the session idles and returns control to the user. The required summary
    // makes the model state what was accomplished before it stops.
    public static Tool CreateStopWorkTool(Action onStop)
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "stop_work",
                    Description = "End the work loop and hand control back to the user. Call this once all delegated work is complete (or should not continue). Until you call it, you are re-prompted after each turn to assign the next unit of work.",
                    Parameters = Params(
                        Req("summary", "string", "A brief summary of what was accomplished across the delegated work, or why the work is stopping."))
                }
            },
            Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                onStop();
                string ack = "Work loop stopped.";
                return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
            }
        };
    }

    // Preserved in case we reintroduce free-form delegation: the generic subagent tool let the root pick any
    // subagent role by name. It is no longer wired up — the Default uses assign_work (Developer) and the
    // Developer uses review_work (Reviewer), so there is no caller that needs an arbitrary role choice.
    // public static Tool CreateSubagentTool(IEnumerable<Role> subagentRoles, Func<string, string, int, CancellationToken, Task<(bool ok, string text, int responseTokens)>> runSubagent)
    // {
    //     string roleList = string.Empty;
    //     foreach (Role r in subagentRoles)
    //     {
    //         roleList += $"{r.Name} -> {r.Description}\n";
    //     }
    //
    //     return new Tool
    //     {
    //         Definition = new ToolDefinition
    //         {
    //             Type = "function",
    //             Function = new FunctionDefinition
    //             {
    //                 Name = "subagent",
    //                 Description = "Instruct a subagent to do work. The subagent gets a git worktree that you name with the provided role and tools, works to completion, and returns an update.",
    //                 Parameters = Params(
    //                     Req("role", "string", $"The role to assign the child agent. It receives that role's system prompt and tool set. Valid roles: {roleList}."),
    //                     Req("prompt", "string", "The task for the child agent, written in natural language as if you were the user instructing it."))
    //             }
    //         },
    //         Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
    //         {
    //             string role = Str(args, "role");
    //             string prompt = Str(args, "prompt");
    //             if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(prompt))
    //                 return new ToolResult(toolCallId, string.Empty, "Error: subagent requires both 'role' and 'prompt'.", 1, 0);
    //
    //             (bool ok, string text, int responseTokens) = await runSubagent(role, prompt, maxOutputTokens, ct);
    //             if (!ok)
    //                 return new ToolResult(toolCallId, string.Empty, text, 1, Math.Max(1, ToolDispatch.EstimateTokens(text)));
    //
    //             return new ToolResult(toolCallId, text, string.Empty, 0, Math.Max(1, responseTokens));
    //         }
    //     };
    // }

    // Creates the Developer subagent's termination tool. onComplete receives the final result; SubagentRunner
    // adds this to the Developer's tool list (selected by the role declaring task_complete) and reads what
    // onComplete captured to return to the caller. Its required argument is the result of review_work, which
    // strongly steers the Developer to get the work reviewed and integrated before finishing.
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
                    Description = "Declare your work finished and return to the agent that delegated it. Before calling this, get your work reviewed with review_work and integrated with commit_and_rebase, then summarize the outcome below.",
                    Parameters = Params(
                        Req("results_of_review_work", "string", "The review outcome and the integration status from commit_and_rebase. This string is the entire response the caller receives."))
                }
            },
            Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string results = Str(args, "results_of_review_work");
                onComplete(results);
                string ack = "Task marked complete.";
                return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
            }
        };
    }

    // Creates the Developer's review_work tool: it spawns a Reviewer subagent on the Developer's worktree and
    // returns the review to the Developer. runReview launches the Reviewer (via SubagentRunner) parented to the
    // calling Developer's sub-session; the review is feedback only — the Developer integrates approved work
    // itself with commit_and_rebase. Like assign_work it always targets one fixed role and takes no role
    // argument.
    public static Tool CreateReviewWorkTool(Func<string, int, CancellationToken, Task<(bool ok, string text, int responseTokens)>> runReview)
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "review_work",
                    Description = "Ask the Reviewer to inspect your changes. Returns its verdict: approved, or rejected with comments to address. The review does not commit anything — once approved, integrate the work yourself with commit_and_rebase.",
                    Parameters = Params(
                        Req("prompt", "string", "What you changed and what the reviewer should check, written in natural language as if you were instructing it."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string prompt = Str(args, "prompt");
                if (string.IsNullOrWhiteSpace(prompt))
                    return new ToolResult(toolCallId, string.Empty, "Error: review_work requires a 'prompt'.", 1, 0);

                (bool ok, string text, int responseTokens) = await runReview(prompt, maxOutputTokens, ct);
                if (!ok)
                    return new ToolResult(toolCallId, string.Empty, text, 1, Math.Max(1, ToolDispatch.EstimateTokens(text)));

                return new ToolResult(toolCallId, text, string.Empty, 0, Math.Max(1, responseTokens));
            }
        };
    }

    // Creates the Developer's commit_and_rebase tool: it commits the worktree with the given message and
    // integrates it onto the base branch via a strictly linear rebase (see CommitAndRebaseAsync). The Developer
    // calls this explicitly after an approved review, so the git transcript is visible in its session and a
    // conflict comes straight back to it to resolve. Selected by the role declaring commit_and_rebase.
    public static Tool CreateCommitAndRebaseTool()
    {
        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "commit_and_rebase",
                    Description = "Commit all changes in your worktree, then integrate them: fast-forward the base branch from its remote, rebase your branch onto it (linear history, no merge commit), and fast-forward the base onto your branch. Call this after an approved review. On a conflict the rebase stops with the conflicted files listed — resolve them, run 'git rebase --continue' with bash, then call this again to finish.",
                    Parameters = Params(
                        Req("message", "string", "The commit message describing the work."))
                }
            },
            Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                string message = Str(args, "message");
                if (string.IsNullOrWhiteSpace(message))
                    return new ToolResult(toolCallId, string.Empty, "Error: commit_and_rebase requires a commit 'message'.", 1, 0);

                (bool ok, string transcript) = await CommitAndRebaseAsync(message, ct);
                int tokens = Math.Max(1, ToolDispatch.EstimateTokens(transcript));
                if (!ok)
                    return new ToolResult(toolCallId, string.Empty, transcript, 1, tokens);

                return new ToolResult(toolCallId, transcript, string.Empty, 0, tokens);
            }
        };
    }

    // Commits the current worktree and integrates it with a strictly linear, rebase-based flow — never a merge
    // commit. It commits everything onto the feature branch B, finds the base branch A (the branch checked out in
    // the primary worktree, reached with git -C since B's worktree cannot check A out). If A has an upstream, A is
    // first updated with a rebasing pull so it carries any commits other agents landed. Then B is rebased onto A,
    // putting A's commits underneath B's recent ones, and A is fast-forwarded onto B. If A has an upstream, A is
    // pushed. B's worktree is never moved, so there is no switch back. The base is derived from the primary
    // worktree (git --git-common-dir), not guessed as "main" or read from origin/HEAD, so it is correct for any
    // base branch and with or without a remote. The commit message is passed base64-encoded so arbitrary text
    // cannot break out of the script. A rebase conflict is left in place (not aborted) with the conflicted files
    // listed, so the Developer can resolve it directly and call again. Returns (ok, transcript): ok is false on
    // any git failure, with the transcript explaining why.
    private static async Task<(bool ok, string transcript)> CommitAndRebaseAsync(string message, CancellationToken ct)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        string script =
            "branch=$(git rev-parse --abbrev-ref HEAD)\n" +
            "common=$(git rev-parse --git-common-dir)\n" +
            "main_wt=$(cd \"$common\" && cd .. && pwd)\n" +
            "base=$(git -C \"$main_wt\" rev-parse --abbrev-ref HEAD)\n" +
            "if [ -z \"$base\" ] || [ \"$base\" = \"$branch\" ]; then echo \"Could not determine a distinct base branch (base='$base', feature='$branch').\"; exit 1; fi\n" +
            "echo \"Committing work on '$branch'...\"\n" +
            "echo '" + encoded + "' | base64 -d > /tmp/beast_commit_msg\n" +
            "git add -A\n" +
            "git commit -F /tmp/beast_commit_msg || echo '(nothing to commit)'\n" +
            "if git -C \"$main_wt\" rev-parse --abbrev-ref --symbolic-full-name @{u} >/dev/null 2>&1; then\n" +
            "  echo \"Updating base '$base' from its remote (rebasing pull)...\"\n" +
            "  git -C \"$main_wt\" pull --rebase || { echo \"Could not update '$base' from its remote.\"; exit 1; }\n" +
            "else\n" +
            "  echo \"Base '$base' has no remote; integrating against local '$base'.\"\n" +
            "fi\n" +
            "echo \"Rebasing '$branch' onto '$base' (linear history, no merge commit)...\"\n" +
            "if ! git rebase \"$base\"; then\n" +
            "  echo \"Rebase of '$branch' onto '$base' hit conflicts. Conflicted files:\"\n" +
            "  git diff --name-only --diff-filter=U\n" +
            "  echo \"Resolve each, 'git add' it, run 'git rebase --continue', then call commit_and_rebase again to fast-forward '$base'.\"\n" +
            "  exit 1\n" +
            "fi\n" +
            "echo \"Fast-forwarding base '$base' onto '$branch'...\"\n" +
            "git -C \"$main_wt\" merge --ff-only \"$branch\" || { echo \"Could not fast-forward '$base' (its worktree may have uncommitted changes).\"; exit 1; }\n" +
            "if git -C \"$main_wt\" rev-parse --abbrev-ref --symbolic-full-name @{u} >/dev/null 2>&1; then\n" +
            "  echo \"Pushing base '$base' to its remote...\"\n" +
            "  git -C \"$main_wt\" push || { echo \"Integrated locally but could not push '$base'.\"; exit 1; }\n" +
            "fi\n";

        ToolResult result = await ShellTools.BashAsync("commit_and_rebase", script, null, ct);

        StringBuilder sb = new StringBuilder();
        if (!string.IsNullOrEmpty(result.StdOut))
            sb.Append(result.StdOut);
        if (!string.IsNullOrEmpty(result.StdErr))
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(result.StdErr);
        }

        bool ok = result.ExitCode == 0;
        if (!ok)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append($"[commit_and_rebase failed: git exited {result.ExitCode}]");
        }

        string transcript = sb.Length > 0 ? sb.ToString() : "[commit_and_rebase produced no output]";
        return (ok, transcript);
    }

    // Creates the Reviewer's termination tool. onFinish receives the approval flag and the review comments;
    // SubagentRunner adds this to a Reviewer child's tool list and reads what onFinish captured to return the
    // verdict to the Developer. The Reviewer never integrates — the Developer commits and rebases after approval.
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
                    Description = "Call this to finish the review. approved=true accepts the change; approved=false rejects it with comments. You only review — after approval the developer commits and integrates the work itself, so you never run git.",
                    Parameters = Params(
                        Req("approved", "boolean", "True to accept the change, false to reject it."),
                        Req("comments", "string", "Your review: what you checked and why you approved, or exactly what the developer must fix. This string is the entire response the caller receives."))
                }
            },
            Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
            {
                bool? approved = BoolOpt(args, "approved");
                if (approved == null)
                    return Task.FromResult(new ToolResult(toolCallId, string.Empty, "Error: finish_review requires 'approved' to be the boolean true or false.", 1, 0));

                string comments = Str(args, "comments");
                onFinish(approved.Value, comments);
                string ack = approved.Value ? "Review approved." : "Review rejected.";
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

    // Returns null when the argument is missing or not a parseable boolean, so callers can surface a malformed
    // value loudly instead of having it silently collapse to false.
    private static bool? BoolOpt(JsonObject args, string key)
    {
        if (args.TryGetPropertyValue(key, out JsonNode? node) && node != null && bool.TryParse(node.ToString(), out bool v)) return v;
        return null;
    }
}
