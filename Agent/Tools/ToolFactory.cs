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
		BeastSettings settings,
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
		{
			ToolConfig tc = settings.Tools["bash"];
			Register(tools, "bash", tc.Description, Params(Req("command", "string", tc.Parameters["command"]), Opt("timeout_seconds", "integer", tc.Parameters["timeout_seconds"])), 
				async (args, toolCallId, ct, transport, sessionId) =>
				{
					string command = Str(args, "command");
					int? timeoutSeconds = IntOpt(args, "timeout_seconds");
					return await ShellTools.BashAsync(toolCallId, command, timeoutSeconds, ct);
				});
		}

		if (role.Tools.Contains("readonly_bash"))
		{
			ToolConfig tc = settings.Tools["readonly_bash"];
			Register(tools, "readonly_bash", tc.Description, Params(Req("command", "string", tc.Parameters["command"]), Opt("timeout_seconds", "integer", tc.Parameters["timeout_seconds"])), 
				async (args, toolCallId, ct, transport, sessionId) =>
				{
					string command = Str(args, "command");
					int? timeoutSeconds = IntOpt(args, "timeout_seconds");
					return await ShellTools.ReadonlyBashAsync(toolCallId, command, timeoutSeconds, ct);
				});
		}

		if (role.Tools.Contains("write_file"))
		{
			ToolConfig tc = settings.Tools["write_file"];
			Register(tools, "write_file", tc.Description, Params(Req("file_path", "string", tc.Parameters["file_path"]), Req("content", "string", tc.Parameters["content"])), 
				async (args, toolCallId, ct, transport, sessionId) =>
				{
					string filePath = Str(args, "file_path");
					string content = Str(args, "content");
					return await FileTools.WriteFileAsync(toolCallId, filePath, content, ct);
				});
		}

		if (role.Tools.Contains("edit_file"))
		{
			ToolConfig tc = settings.Tools["edit_file"];
			Register(tools, "edit_file", tc.Description, Params(Req("file_path", "string", tc.Parameters["file_path"]), Req("old_text", "string", tc.Parameters["old_text"]), Req("new_text", "string", tc.Parameters["new_text"])), 
				async (args, toolCallId, ct, transport, sessionId) =>
				{
					string filePath = Str(args, "file_path");
					string oldText = Str(args, "old_text");
					string newText = Str(args, "new_text");
					return await FileTools.EditFileAsync(toolCallId, filePath, oldText, newText, ct);
				});
		}
		if (role.Tools.Contains("ls"))
		{
			ToolConfig tc = settings.Tools["ls"];
			Register(tools, "ls", tc.Description, Params(Req("folder", "string", tc.Parameters["folder"])), 
				async (args, toolCallId, ct, transport, sessionId) =>
				{
					string folder = Str(args, "folder");
					return await ShellTools.LsAsync(toolCallId, folder, ct);
				});
		}

		if (role.Tools.Contains("read_file"))
		{
			ToolConfig tc = settings.Tools["read_file"];
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "read_file",
						Description = tc.Description,
						Parameters = Params(
							Req("file_path", "string", tc.Parameters["file_path"]),
							Opt("offset", "integer", tc.Parameters["offset"]),
							Opt("lines", "integer", tc.Parameters["lines"])),
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
		}

		if (role.Tools.Contains("find_relevant_file_sections") && explorerReady && session != null)
		{
			ToolConfig tc = settings.Tools["find_relevant_file_sections"];
			FileSummarizer summarizer = new FileSummarizer();
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "find_relevant_file_sections",
						Description = tc.Description,
						Parameters = Params(
							Req("file_path", "string", tc.Parameters["file_path"]),
							Req("goal", "string", tc.Parameters["goal"]),
							Opt("offset", "integer", tc.Parameters["offset"]))
					}
				},
				Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
				{
					string filePath = Str(args, "file_path");
					string goal = Str(args, "goal");
					string offset = Str(args, "offset");
					// registry! is safe: explorerService being non-null implies registry was non-null at build time.
					return await summarizer.SummarizeAsync(settings, toolCallId, filePath, offset, goal, explorerRole!, registry!, transport, session, maxOutputTokens, ct);
				}
			});
		}

		if (role.Tools.Contains("fetch_url") && webFetchReady && session != null)
		{
			ToolConfig tc = settings.Tools["fetch_url"];
			WebFetch webFetch = new WebFetch();
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "fetch_url",
						Description = tc.Description,
						Parameters = Params(
							Req("url", "string", tc.Parameters["url"]),
							Req("goal", "string", tc.Parameters["goal"]))
					}
				},
				Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
				{
					string url = Str(args, "url");
					string goal = Str(args, "goal");
					// registry! is safe: webFetchService being non-null implies registry was non-null at build time.
					return await webFetch.FetchRawAsync(settings, toolCallId, url, goal, webFetchRole!, registry!, transport, session, maxOutputTokens, ct);
				}
			});
		}

		if (role.Tools.Contains("internet_search") && settings.WebSearch?.Openrouter != null && settings.WebSearch.Openrouter.Enabled && webSearchRole != null && session != null)
		{
			ToolConfig tc = settings.Tools["internet_search"];
			WebSearchOpenrouter webSearch = new WebSearchOpenrouter(settings.WebSearch.Openrouter.BuildModel());
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "internet_search",
						Description = tc.Description,
						Parameters = Params(
							Req("query", "string", tc.Parameters["query"]),
							Req("goal", "string", tc.Parameters["goal"]))
					}
				},
				Handler = async (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
				{
					string query = Str(args, "query");
					string goal = Str(args, "goal");
					return await webSearch.InternetSearchAsync(toolCallId, query, goal, webSearchRole, transport, session, maxOutputTokens, ct);
				}
			});
		}

		if (role.Tools.Contains("assign_work") && runDeveloper != null && onWorkAssigned != null)
		{
			ToolConfig tc = settings.Tools["assign_work"];
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "assign_work",
						Description = tc.Description,
						Parameters = Params(
							Req("prompt", "string", tc.Parameters["prompt"]))
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
		}

		// stop_work is exposed only while the delegation loop is active; paired with assign_work so a
		// role that cannot delegate never sees it.
		if (workInProgress && role.Tools.Contains("assign_work") && onStopWork != null)
		{
			ToolConfig tc = settings.Tools["stop_work"];
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "stop_work",
						Description = tc.Description,
						Parameters = Params(
							Req("summary", "string", tc.Parameters["summary"]))
					}
				},
				Handler = (args, toolCallId, ct, transport, sessionId, maxOutputTokens) =>
				{
					onStopWork();
					string ack = "Work loop stopped.";
					return Task.FromResult(new ToolResult(toolCallId, ack, string.Empty, 0, ToolDispatch.EstimateTokens(ack)));
				}
			});
		}

		if (role.Tools.Contains("review_work") && runReview != null)
		{
			ToolConfig tc = settings.Tools["review_work"];
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "review_work",
						Description = tc.Description,
						Parameters = Params(
							Req("prompt", "string", tc.Parameters["prompt"]))
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
		}

		if (role.Tools.Contains("commit_and_rebase"))
		{
			ToolConfig tc = settings.Tools["commit_and_rebase"];
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "commit_and_rebase",
						Description = tc.Description,
						Parameters = Params(
							Req("message", "string", tc.Parameters["message"]))
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
		}

		// Terminators are mutually exclusive; the role declares at most one. task_complete and finish_review are
		// checked by name; return_to_caller is the fallback for any subagent that declares neither.
		if (role.Tools.Contains("task_complete") && onTaskComplete != null)
		{
			ToolConfig tc = settings.Tools["task_complete"];
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "task_complete",
						Description = tc.Description,
						Parameters = Params(
							Req("results_of_review_work", "string", tc.Parameters["results_of_review_work"]),
							Req("success", "boolean", tc.Parameters["success"]))
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
		}
		else if (role.Tools.Contains("finish_review") && onFinishReview != null)
		{
			ToolConfig tc = settings.Tools["finish_review"];
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "finish_review",
						Description = tc.Description,
						Parameters = Params(
							Req("approved", "boolean", tc.Parameters["approved"]),
							Req("comments", "string", tc.Parameters["comments"]))
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
		}
		else if (onReturnToCaller != null)
		{
			ToolConfig tc = settings.Tools["return_to_caller"];
			tools.Add(new Tool
			{
				Definition = new ToolDefinition
				{
					Type = "function",
					Function = new FunctionDefinition
					{
						Name = "return_to_caller",
						Description = tc.Description,
						Parameters = Params(
							Req("output", "string", tc.Parameters["output"]),
							Req("success", "boolean", tc.Parameters["success"]))
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
		}

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
