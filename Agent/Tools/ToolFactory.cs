using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Builds all tools for a given role in one call. Every parameter is used by at least one tool;
// parameters for tools not in role.Tools are never called and may be null.
// Helper roles (Explorer, WebFetch, WebSearch) and their LlmServices are resolved upfront; a tool
// is silently omitted if its helper role or service is unavailable.
// Special tools (assign_work, stop_work, review_work, terminators) require their corresponding
// callbacks to be non-null or they are silently omitted — the role declaring a tool name is
// necessary but not sufficient; the caller must also supply the live callback.
public static class ToolFactory
{
	// The per-agent read_file window. Bounded so a single read cannot flood the context.
	private const int ReadFileMaxLines = 500;

	public static Tool[] BuildForRole(
		Role role,
		LlmRegistry? registry,
		RoleService? roleService,
		Session? session,
		WebSearchConfig? webSearchConfig,
		bool workInProgress,
		Func<string, int, CancellationToken, Task<(bool ok, string text, int responseTokens)>>? runDeveloper,
		Action? onWorkAssigned,
		Action? onStopWork,
		Func<string, int, CancellationToken, Task<(bool ok, string text, int responseTokens)>>? runReview,
		Action<string>? onTaskComplete,
		Action<bool, string>? onFinishReview,
		Action<string>? onReturnToCaller)
	{
		List<Tool> tools = new List<Tool>();

		// Resolve helper roles and check model availability upfront. A tool is omitted when its role is
		// unconfigured or no model is available. GetModelForRole is a lightweight availability check —
		// the actual LlmService is created inside HelperSession when the tool is called.
		Role? explorerRole = roleService?.GetRole("Explorer");
		bool explorerReady = explorerRole != null && registry?.GetModelForRole(explorerRole, string.Empty, 0) != null;
		Role? webFetchRole = roleService?.GetRole("WebFetch");
		bool webFetchReady = webFetchRole != null && registry?.GetModelForRole(webFetchRole, string.Empty, 0) != null;
		Role? webSearchRole = roleService?.GetRole("WebSearch");

		if (role.Tools.Contains("bash"))
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

		if (role.Tools.Contains("readonly_bash"))
			Register(tools, "readonly_bash",
				"Read-only bash for inspecting the repo without changing it. Runs in a restricted shell, so several things FAIL with errors — do not use them: no redirection of any stream (>, >>, and stderr redirects like 2>, 2>&1, 2>/dev/null are all rejected), no cd, no running a program by an explicit path, and PATH is read-only so you cannot extend it (export PATH=... fails). Only a curated read-only toolset (cat, ls, grep, git, head/tail, wc, sort, diff, jq, ...) is on PATH; a 'command not found' means it is not installed here, not that you should look harder. CWD is the repo root at /workspace/.",
				Params(
					Req("command", "string", "Read-only shell command to execute"),
					Opt("timeout_seconds", "integer", "Timeout in seconds (default 120).")),
				async (args, toolCallId, ct, transport, sessionId) =>
				{
					string command = Str(args, "command");
					int? timeoutSeconds = IntOpt(args, "timeout_seconds");
					return await ShellTools.ReadonlyBashAsync(toolCallId, command, timeoutSeconds, ct);
				});

		if (role.Tools.Contains("write_file"))
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

		if (role.Tools.Contains("edit_file"))
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

		if (role.Tools.Contains("ls"))
			Register(tools, "ls",
				"List a folder's contents. CWD is the repo root at /workspace/.",
				Params(
					Req("folder", "string", "Folder to list.")),
				async (args, toolCallId, ct, transport, sessionId) =>
				{
					string folder = Str(args, "folder");
					return await ShellTools.LsAsync(toolCallId, folder, ct);
				});

		if (role.Tools.Contains("read_file"))
			tools.Add(new Tool
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
			});

		if (role.Tools.Contains("find_relevant_file_sections") && explorerReady && session != null)
		{
			FileSummarizer summarizer = new FileSummarizer();
			tools.Add(new Tool
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
					// registry! is safe: explorerService being non-null implies registry was non-null at build time.
					return await summarizer.SummarizeAsync(toolCallId, filePath, offset, goal, explorerRole!, registry!, transport, session, maxOutputTokens, ct);
				}
			});
		}

		if (role.Tools.Contains("fetch_url") && webFetchReady && session != null)
		{
			WebFetch webFetch = new WebFetch();
			tools.Add(new Tool
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
					// registry! is safe: webFetchService being non-null implies registry was non-null at build time.
					return await webFetch.FetchRawAsync(toolCallId, url, goal, webFetchRole!, registry!, transport, session, maxOutputTokens, ct);
				}
			});
		}

		if (role.Tools.Contains("search_web") && webSearchConfig?.Openrouter != null && webSearchConfig.Openrouter.Enabled && webSearchRole != null && session != null)
		{
			WebSearchOpenrouter webSearch = new WebSearchOpenrouter(webSearchConfig.Openrouter.BuildModel());
			tools.Add(new Tool
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
					return await webSearch.SearchWebAsync(toolCallId, query, goal, webSearchRole, transport, session, maxOutputTokens, ct);
				}
			});
		}

		if (role.Tools.Contains("assign_work") && runDeveloper != null && onWorkAssigned != null)
			tools.Add(new Tool
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

					onWorkAssigned();

					(bool ok, string text, int responseTokens) = await runDeveloper(prompt, maxOutputTokens, ct);
					if (!ok)
						return new ToolResult(toolCallId, string.Empty, text, 1, Math.Max(1, ToolDispatch.EstimateTokens(text)));

					return new ToolResult(toolCallId, text, string.Empty, 0, Math.Max(1, responseTokens));
				}
			});

		// stop_work is exposed only while the delegation loop is active; paired with assign_work so a
		// role that cannot delegate never sees it.
		if (workInProgress && role.Tools.Contains("assign_work") && onStopWork != null)
			tools.Add(new Tool
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
					onStopWork();
					string ack = "Work loop stopped.";
					return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
				}
			});

		if (role.Tools.Contains("review_work") && runReview != null)
			tools.Add(new Tool
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
			});

		if (role.Tools.Contains("commit_and_rebase"))
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "commit_and_rebase",
						Description = "Commit all changes in your worktree, then integrate them: rebase your branch onto the base branch (linear history, no merge commit), and fast-forward the base onto your branch. Call this after an approved review. On a conflict the rebase stops with the conflicted files listed — resolve them, run 'git rebase --continue' with bash, then call this again to finish.",
						Parameters = Params(
							Req("message", "string", "The commit message describing the work."))
					}
				},
				Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
				{
					string message = Str(args, "message");
					if (string.IsNullOrWhiteSpace(message))
						return new ToolResult(toolCallId, string.Empty, "Error: commit_and_rebase requires a commit 'message'.", 1, 0);

					(bool ok, string transcript) = await GitTools.CommitAndRebaseAsync(message, ct);
					int tokens = Math.Max(1, ToolDispatch.EstimateTokens(transcript));
					if (!ok)
						return new ToolResult(toolCallId, string.Empty, transcript, 1, tokens);

					return new ToolResult(toolCallId, transcript, string.Empty, 0, tokens);
				}
			});

		// Terminators are mutually exclusive; the role declares at most one. task_complete and finish_review are
		// checked by name; return_to_caller is the fallback for any subagent that declares neither.
		if (role.Tools.Contains("task_complete") && onTaskComplete != null)
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "task_complete",
						Description = "Declare your work finished and return to the agent that delegated it. Before calling this, get your work reviewed with review_work and integrated with commit_and_rebase, then summarize the outcome below.",
						Parameters = Params(
							Req("results_of_review_work", "string", "The review outcome and the integration status from commit_and_rebase. This string is the entire response the caller receives."),
							Req("success", "boolean", "True if the task was completed successfully; false if it failed or was blocked."))
					}
				},
				Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
				{
					string results = Str(args, "results_of_review_work");
					bool success = BoolOpt(args, "success") ?? true;
					session!.SetTerminationStatus(success ? SessionStatus.Success : SessionStatus.Failure);
					onTaskComplete(results);
					string ack = "Task marked complete.";
					return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
				}
			});
		else if (role.Tools.Contains("finish_review") && onFinishReview != null)
			tools.Add(new Tool
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
					session!.SetTerminationStatus(approved.Value ? SessionStatus.Success : SessionStatus.Failure);
					onFinishReview(approved.Value, comments);
					string ack = approved.Value ? "Review approved." : "Review rejected.";
					return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
				}
			});
		else if (onReturnToCaller != null)
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "return_to_caller",
						Description = "Call this to declare your task finished.",
						Parameters = Params(
							Req("output", "string", "This string is the entire response the caller receives. Include high-level details like what files were modified, changes to interfaces required,  tests passed, or any other changes to the project that the orchestrator agent should know about."),
							Req("success", "boolean", "True if the task was completed successfully; false if it failed or was blocked."))
					}
				},
				Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
				{
					string output = Str(args, "output");
					bool success = BoolOpt(args, "success") ?? true;
					session!.SetTerminationStatus(success ? SessionStatus.Success : SessionStatus.Failure);
					onReturnToCaller(output);
					string ack = "Returned to caller.";
					return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
				}
			});

		return tools.ToArray();
	}

	// Registers a tool in the list, wrapping the handler with MeasureRawResult so output is always
	// fit to the caller's token budget. Used only for tools whose handlers return raw ToolResults.
	private static void Register(List<Tool> tools, string name, string description, JsonObject parameters, Func<JsonObject, string, CancellationToken, ITransportServer, string, Task<ToolResult>> handler)
	{
		tools.Add(new Tool
		{
			Definition = new ToolDefinition
			{
				Type = "function",
				Function = new FunctionDefinition { Name = name, Description = description, Parameters = parameters }
			},
			Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
			{
				ToolResult raw = await handler(args, toolCallId, ct, transport, sessionId);
				return ToolDispatch.MeasureRawResult(raw, maxOutputTokens);
			}
		});
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
			if (isRequired)
				required.Add(name);
		}

		JsonObject schema = new()
		{
			["type"] = "object",
			["properties"] = properties
		};

		if (required.Count > 0)
			schema["required"] = required;

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
		if (args.TryGetPropertyValue(key, out JsonNode? node) && node != null)
			return node.ToString();
		return string.Empty;
	}

	private static int? IntOpt(JsonObject args, string key)
	{
		if (args.TryGetPropertyValue(key, out JsonNode? node) && node != null && int.TryParse(node.ToString(), out int v))
			return v;
		return null;
	}

	// Returns null when the argument is missing or not a parseable boolean, so callers can surface a malformed
	// value loudly instead of having it silently collapse to false.
	private static bool? BoolOpt(JsonObject args, string key)
	{
		if (args.TryGetPropertyValue(key, out JsonNode? node) && node != null && bool.TryParse(node.ToString(), out bool v))
			return v;
		return null;
	}
}