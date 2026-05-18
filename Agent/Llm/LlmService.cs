using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


public enum LlmExitReason
{
	Completed, ContextFull, Failed, Interrupted
}

// Result returned by LlmService after running a conversation to completion.
public class LlmResult
{
	public LlmExitReason ExitReason { get; }
	public bool Success => ExitReason == LlmExitReason.Completed || ExitReason == LlmExitReason.ContextFull;  // technically context full means it responded but NOW we are out of context
	public string ErrorMessage { get; }

	public LlmResult(LlmExitReason exitReason, string errorMessage)
	{
		ExitReason = exitReason;
		ErrorMessage = errorMessage;
	}
}

// Manages an LLM provider and drives a conversation to completion with tool calling.
public class LlmService
{
	private ProtocolProxy _handler = null!;  // this gets set in UpdateModel during the constructor, it's never null
	private LlmModel _model;
	private DateTimeOffset _availableAt = DateTimeOffset.MinValue;
	public LlmModel Model => _model;
	public bool IsAvailable => DateTimeOffset.UtcNow >= _availableAt;

	public LlmService(LlmModel model)
	{
		_model = model;
		UpdateModel(model);
	}

	// Updates the model reference and rebuilds the handler.
	public void UpdateModel(LlmModel model)
	{
		_model = model;
		_handler = new ProtocolProxy(model);
	}

	// Runs the conversation in a loop until idle, tool-exit, or fatal error.
	public async Task<LlmResult> RunToCompletionAsync(BeastSession conversation, Tool[] tools, int reserveTokens, ITransportServer transport, CancellationToken cancellationToken)
	{
		if (!IsAvailable)
		{
			if (_availableAt == DateTimeOffset.MaxValue)
			{
				return new LlmResult(LlmExitReason.Failed, $"LLM {_model.Config.Name} is permanently down");
			}

			return new LlmResult(LlmExitReason.Failed, $"LLM {_model.Config.Name} is unavailable until {_availableAt:u}");
		}

		return await ExecuteConversationAsync(conversation, tools, reserveTokens, transport, cancellationToken);
	}

	private async Task<LlmResult> ExecuteConversationAsync(BeastSession conversation, Tool[] tools, int reserveTokens, ITransportServer transport, CancellationToken cancellationToken)
	{
		LlmModel model = _model;
		LlmResult finalResult = new LlmResult(LlmExitReason.Completed, "");

		try
		{
			List<ToolDefinition> toolDefs = new List<ToolDefinition>();
			for (int i = 0; i < tools.Length; i++)
			{
				toolDefs.Add(tools[i].Definition);
			}

			int emptyResponseCount = 0;
			const int kMaxEmptyResponses = 10;

			// This loop will continue until we hit a break statement.  That happens whenever the model returns a response
			// that doesn't trigger any tool calls (i.e. it's done), or if we encounter an error or context limit.
			// ProcessAssistantResponseAsync returns (null, true) when tool calls were dispatched (model made progress),
			// (null, false) when the turn was empty (no content, no tool calls), and (LlmResult, _) to terminate.
			for (; ; )
			{
				if (model.Config.ContextWindow - conversation.GetUsedTokenCount() <= reserveTokens)
				{
					finalResult = new LlmResult(LlmExitReason.ContextFull, "Context limit reached");
					break;
				}

				cancellationToken.ThrowIfCancellationRequested();

				List<ConversationMessage> requestMessages = conversation.Messages;
				if (requestMessages.Count > 0)
				{
					string lastRole = requestMessages[requestMessages.Count - 1].Role;
					if (lastRole != "user" && lastRole != "tool")
					{
						requestMessages = new List<ConversationMessage>(requestMessages);
						requestMessages.Add(new ConversationMessage { Role = "user", Content = "" });
					}
				}

				int maxCompletionTokens = ComputeMaxCompletionTokens(conversation, model.Config.ContextWindow, reserveTokens) ?? 0;

				ProviderCallResult callResult = await _handler.ExecuteAsync(requestMessages, toolDefs, maxCompletionTokens, transport, cancellationToken);

				if (callResult.Outcome == ProviderCallOutcome.Success)
				{
					ProviderCallPayload payload = callResult.Payload!;
					conversation.TotalCost += payload.Cost;
					conversation.LastTokenUsage = payload.Usage;

					(LlmResult? terminalResult, bool toolsDispatched) = await ProcessAssistantResponseAsync(payload.Message, tools, conversation, transport, cancellationToken);
					if (terminalResult != null)
					{
						finalResult = terminalResult;
						break;
					}

					// toolsDispatched=true means the model made progress via tool calls; reset the idle counter.
					// toolsDispatched=false means a genuinely empty turn with no content and no tool calls.
					if (toolsDispatched)
					{
						emptyResponseCount = 0;
					}
					else
					{
						emptyResponseCount++;
						if (emptyResponseCount >= kMaxEmptyResponses)
						{
							finalResult = new LlmResult(LlmExitReason.Failed, $"Model returned {kMaxEmptyResponses} consecutive empty responses.");
							break;
						}
					}
				}
				else if (callResult.Outcome == ProviderCallOutcome.RateLimited)
				{
					_availableAt = callResult.RetryAfter ?? DateTimeOffset.UtcNow.AddSeconds(5);
					finalResult = new LlmResult(LlmExitReason.Failed, $"Rate limited, retry after {_availableAt:u}");
					break;
				}
				else if (callResult.Outcome == ProviderCallOutcome.PermanentFailure)
				{
					_availableAt = DateTimeOffset.MaxValue;
					finalResult = new LlmResult(LlmExitReason.Failed, callResult.ErrorMessage);
					break;
				}
				else
				{
					finalResult = new LlmResult(LlmExitReason.Failed, callResult.ErrorMessage);
					break;
				}
			}
		}
		catch (OperationCanceledException ex)
		{
			if (!cancellationToken.IsCancellationRequested)
			{
				finalResult = new LlmResult(LlmExitReason.Interrupted, "Interrupted by user");
			}
			else
			{
				string reason = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
				finalResult = new LlmResult(LlmExitReason.Interrupted, $"Cancelled: {reason}");
				throw;
			}
		}

		return finalResult;
	}

	// Returns (null, toolsDispatched) to continue looping, or (LlmResult, false) to terminate.
	private async Task<(LlmResult? terminalResult, bool toolsDispatched)> ProcessAssistantResponseAsync(ConversationMessage assistantMessage, Tool[] tools, BeastSession conversation, ITransportServer transport, CancellationToken ct)
	{
		// Phase 1: Normalize content — treat whitespace-only as absent.
		if (assistantMessage.Content != null)
		{
			assistantMessage.Content = assistantMessage.Content.Trim();
			if (assistantMessage.Content.Length == 0)
			{
				assistantMessage.Content = null;
			}
		}

		// If the model returned nothing at all, ignore this turn and keep looping.
		bool hasToolCalls = assistantMessage.ToolCalls != null && assistantMessage.ToolCalls.Count > 0;
		if (assistantMessage.Content == null && !hasToolCalls)
		{
			return (null, false);
		}

		// Tool calls require a non-null content string (wire format expectation).
		if (hasToolCalls && assistantMessage.Content == null)
		{
			assistantMessage.Content = "";
		}

		// Normalize any properly-structured tool calls (trim names, fix empty args, etc.).
		if (hasToolCalls)
		{
			NormalizeToolCalls(assistantMessage.ToolCalls!);
		}

		// Phase 2: XML tool call fallback — some models emit tool calls as <tool_call>{...}</tool_call>
		// blocks inside the content rather than in the structured tool_calls field. Parse those out,
		// inject them as real tool calls, and surface any parse errors back to the model as user messages.
		List<string> xmlParseErrors = new();
		if (!hasToolCalls && !string.IsNullOrEmpty(assistantMessage.Content))
		{
			(List<ConversationToolCall> xmlToolCalls, List<string> xmlErrors) = TryParseXmlToolCalls(assistantMessage.Content, tools);

			if (xmlToolCalls.Count > 0)
			{
				NormalizeToolCalls(xmlToolCalls);
				assistantMessage.ToolCalls = xmlToolCalls;
			}

			xmlParseErrors = xmlErrors;
		}

		// Phase 3: Commit the assistant turn and any parse feedback to the conversation.
		conversation.AddAssistantMessage(assistantMessage);

		foreach (string error in xmlParseErrors)
		{
			conversation.AddUserMessage(error);
		}

		// Phase 4: If there are no tool calls the model is done; stream content to the client and return.
		// Otherwise execute all tool calls in parallel, append their results, and return null so the
		// loop continues for the model to respond to the tool results.
		hasToolCalls = assistantMessage.ToolCalls != null && assistantMessage.ToolCalls.Count > 0;
		if (!hasToolCalls)
		{
			// Send content to the client as it completes each turn. For most models this is the full
			// response in one shot; true token streaming would require protocol-level support.
			if (!string.IsNullOrEmpty(assistantMessage.Content))
			{
				transport.Output(assistantMessage.Content);
			}
			return (new LlmResult(LlmExitReason.Completed, ""), false);
		}

		List<ConversationToolCall> toolCalls = assistantMessage.ToolCalls!;
		(string toolName, ToolResult toolResult)[] completedTools = new (string, ToolResult)[toolCalls.Count];

		Task[] tasks = new Task[toolCalls.Count];
		for (int i = 0; i < toolCalls.Count; i++)
		{
			int index = i;
			ConversationToolCall toolCall = toolCalls[index];
			tasks[index] = Task.Run(async () =>
			{
				ToolResult toolResult = await ExecuteToolAsync(toolCall, tools, ct);
				completedTools[index] = (toolCall.Function.Name, toolResult);
			}, ct);
		}

		await Task.WhenAll(tasks);

		for (int i = 0; i < completedTools.Length; i++)
		{
			if (!completedTools[i].toolResult.MessageHandled)
			{
				conversation.AddToolMessage(toolCalls[i].Id, completedTools[i].toolResult.Response);
			}
		}

		return (null, true);
	}

	private async Task<ToolResult> ExecuteToolAsync(ConversationToolCall toolCall, Tool[] tools, CancellationToken ct)
	{
		Tool? matchedTool = null;
		foreach (Tool t in tools)
		{
			if (t.Definition.Function.Name == toolCall.Function.Name)
			{
				matchedTool = t;
				break;
			}
		}

		if (matchedTool == null)
		{
			return new ToolResult($"Error: Tool '{toolCall.Function.Name}' not found in available tools.", false);
		}

		JsonObject? argsObj;
		try
		{
			argsObj = JsonNode.Parse(toolCall.Function.Arguments)?.AsObject();
		}
		catch (JsonException)
		{
			argsObj = null;
		}

		if (argsObj == null)
		{
			return new ToolResult($"Error: Tool '{toolCall.Function.Name}' received malformed arguments: {toolCall.Function.Arguments}", false);
		}

		return await matchedTool.Handler(argsObj, ct);
	}

	private static void NormalizeToolCalls(List<ConversationToolCall> toolCalls)
	{
		foreach (ConversationToolCall tc in toolCalls)
		{
			tc.Id = Guid.NewGuid().ToString();
			tc.Function.Name = tc.Function.Name.Trim();

			if (string.IsNullOrWhiteSpace(tc.Function.Arguments))
			{
				tc.Function.Arguments = "{}";
			}

			JsonObject argsObj = JsonNode.Parse(tc.Function.Arguments)!.AsObject();
			List<(string key, JsonNode? value)> entries = new();
			foreach (KeyValuePair<string, JsonNode?> kvp in argsObj)
			{
				entries.Add((kvp.Key.Trim(), kvp.Value));
			}

			JsonObject trimmed = new();
			foreach ((string key, JsonNode? value) in entries)
			{
				trimmed[key] = value?.DeepClone();
			}

			tc.Function.Arguments = trimmed.ToJsonString();
		}
	}

	private (List<ConversationToolCall> calls, List<string> errors) TryParseXmlToolCalls(string content, Tool[] tools)
	{
		List<ConversationToolCall> result = new();
		List<string> errors = new();
		string[] tagNames = ["tool_call", "function_call"];

		foreach (string tagName in tagNames)
		{
			string openTag = $"<{tagName}>";
			string closeTag = $"</{tagName}>";
			int searchStart = 0;

			while (searchStart < content.Length)
			{
				int openIndex = content.IndexOf(openTag, searchStart, StringComparison.OrdinalIgnoreCase);
				if (openIndex < 0) break;

				int contentStart = openIndex + openTag.Length;
				int closeIndex = content.IndexOf(closeTag, contentStart, StringComparison.OrdinalIgnoreCase);
				if (closeIndex < 0) break;

				string inner = content.Substring(contentStart, closeIndex - contentStart).Trim();
				searchStart = closeIndex + closeTag.Length;

				(ConversationToolCall? toolCall, string? error) = TryParseXmlToolCallJson(inner, tools);
				if (toolCall != null) result.Add(toolCall);
				else if (error != null) errors.Add(error);
			}
		}

		return (result, errors);
	}

	private (ConversationToolCall? call, string? error) TryParseXmlToolCallJson(string json, Tool[] tools)
	{
		try
		{
			JsonObject? obj = JsonNode.Parse(json)?.AsObject();
			if (obj == null) return (null, $"Tool call contained invalid JSON: {json}");

			string? name = null;
			if (obj.TryGetPropertyValue("name", out JsonNode? nameNode))
			{
				name = nameNode?.GetValue<string>();
			}

			if (string.IsNullOrEmpty(name)) return (null, "Tool call is missing a name.");

			JsonObject args = new();
			if (obj.TryGetPropertyValue("arguments", out JsonNode? argsNode) || obj.TryGetPropertyValue("parameters", out argsNode))
			{
				JsonObject? parsed = argsNode?.AsObject();
				if (parsed != null) args = parsed;
			}

			Tool? matchedTool = null;
			foreach (Tool tool in tools)
			{
				if (tool.Definition.Function.Name == name)
				{
					matchedTool = tool;
					break;
				}
			}

			if (matchedTool == null) return (null, $"Tool '{name}' is not available.");

			JsonObject? definedProperties = matchedTool.Definition.Function.Parameters["properties"]?.AsObject();

			foreach (KeyValuePair<string, JsonNode?> arg in args)
			{
				if (definedProperties == null || !definedProperties.ContainsKey(arg.Key))
				{
					return (null, $"Tool '{name}' does not have a parameter named '{arg.Key}'.");
				}
			}

			return (new ConversationToolCall
			{
				Id = $"xmltc_{Guid.NewGuid():N}",
				Type = "function",
				Function = new ConversationFunctionCall
				{
					Name = name,
					Arguments = args.ToJsonString()
				}
			}, null);
		}
		catch (JsonException ex)
		{
			return (null, $"Tool call contained invalid JSON: {ex.Message}");
		}
	}

	// Computes the max completion tokens to request, reserving space for compaction.
	internal int? ComputeMaxCompletionTokens(BeastSession conversation, int contextLength, int reserveTokens)
	{
		const int DefaultCompletion = 65536;

		int usedTokens = conversation.GetUsedTokenCount();
		long available = contextLength - usedTokens - reserveTokens;
		if (available <= 0) return 0;

		return (int)Math.Min(available, DefaultCompletion);
	}
}
