using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Shared tool-call dispatch for the root SessionRunner and the SubagentRunner. Stateless: given a
// completed assistant payload, it runs that turn's tool calls in parallel, settles the session's
// response budget against the measured sizes, and writes the results back into the payload so the
// caller can commit them. Name fuzzy-matching and argument repair mirror what the producing protocol
// expects. Transport framing for the tool call and its result is emitted by the bundle fan-out (the
// assistant turn was fanned out at commit, the result is fanned out at CommitToolResults), so this
// only runs the handlers.
public static class ToolDispatch
{
	// The codebase's standard rough character-to-token ratio, matching the protocols' live-stream
	// estimate. Only ever used when no provider measurement exists (raw tool output, error results).
	public const int CharsPerToken = 4;

	// Estimates a token count for text the provider never measured. Never returns zero: a result
	// always occupies at least some context, and MeasuredOutputTokens is never left unallocated.
	public static int EstimateTokens(string text)
	{
		if (string.IsNullOrEmpty(text))
			return 1;
		return Math.Max(1, (text.Length + CharsPerToken - 1) / CharsPerToken);
	}

	// Stamps a raw (server-unmeasured) tool result with an estimated token count and, only here,
	// truncates it to maxOutputTokens. Results produced by a sub-session carry the provider's exact
	// measurement and never pass through this. The estimate is taken over the content exactly as
	// CanonicalConversation renders it, so it matches the context cost.
	public static ToolResult MeasureRawResult(ToolResult raw, int maxOutputTokens)
	{
		string content = raw.StdOut;
		if (!string.IsNullOrEmpty(raw.StdErr))
			content = content + "\nstderr: " + raw.StdErr;

		int estimated = EstimateTokens(content);

		if (maxOutputTokens > 0 && estimated > maxOutputTokens)
		{
			// Over the caller's budget and unmeasurable: fold the rendered content into StdOut,
			// clip it to the budget, and flag the clip so the model knows the output is partial.
			const string notice = "\n[truncated to fit caller budget]";
			int charBudget = maxOutputTokens * CharsPerToken;
			int keep = Math.Max(0, charBudget - notice.Length);
			string clipped = (content.Length > keep ? content.Substring(0, keep) : content) + notice;
			return new ToolResult(raw.Id, clipped, string.Empty, raw.ExitCode, EstimateTokens(clipped));
		}

		return new ToolResult(raw.Id, raw.StdOut, raw.StdErr, raw.ExitCode, estimated);
	}

	// Dispatches every tool call in the payload, filling payload.ToolResults in call order. Returns
	// true when at least one tool ran (the assistant turn continues), false when the turn carried no
	// tool calls (the turn is complete).
	public static async Task<bool> DispatchAsync(ProtocolCallPayload payload, Tool[] tools, Session session, ITransportServer transport, CancellationToken ct)
	{
		if (payload.ToolCalls.Count == 0)
			return false;

		List<SemanticToolCall> toolCalls = new List<SemanticToolCall>(payload.ToolCalls);

		// Allocate this round's tool-response budget from the window; the budget splits it evenly
		// across the parallel calls and records the whole round as pending.
		int perToolBudget = session.Budget.ReserveToolResponses(toolCalls.Count);

		List<ToolResult> completedTools = new List<ToolResult>();

		Task[] tasks = new Task[toolCalls.Count];
		for (int i = 0; i < toolCalls.Count; i++)
		{
			completedTools.Add(Error(toolCalls[i].Id, "Tool call failed."));
			int index = i;
			SemanticToolCall toolCall = toolCalls[index];
			tasks[index] = ExecuteToolAsync(toolCall, tools, session.Id, perToolBudget, transport, ct)
				.ContinueWith(t => completedTools[index] = t.Result, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
		}

		await Task.WhenAll(tasks);

		// The payload's ToolResults arrives empty from the protocol; fill it in call order so
		// CommitToolResults fans the results out in the same order as the calls. The round's full
		// reservation stays charged against the budget until the next provider response measures the
		// real context size — we never settle it down to an estimate of what each tool returned.
		payload.ToolResults.Clear();
		for (int i = 0; i < toolCalls.Count; i++)
		{
			payload.ToolResults.Add(completedTools[i]);
		}

		return true;
	}

	// Resolves a single tool call to its handler (exact match, then fuzzy name correction), repairs
	// the arguments against the schema, and runs the handler. Never throws: every failure path
	// returns an error ToolResult so the model can see and correct it within the turn.
	public static async Task<ToolResult> ExecuteToolAsync(SemanticToolCall toolCall, Tool[] tools, string sessionId, int maxOutputTokens, ITransportServer transport, CancellationToken ct)
	{
		Action<string> fixLog = msg => transport.Status(sessionId, msg);

		Tool? matchedTool = null;
		foreach (Tool t in tools)
		{
			if (t.Definition.Function.Name == toolCall.Name)
			{
				matchedTool = t;
				break;
			}
		}

		// Stage 3: fuzzy name correction when exact match fails
		if (matchedTool == null)
		{
			string[] knownNames = new string[tools.Length];
			for (int i = 0; i < tools.Length; i++)
				knownNames[i] = tools[i].Definition.Function.Name;

			string? correctedName = FixJson.FuzzyMatchToolName(toolCall.Name, knownNames, 3, fixLog);
			if (correctedName != null)
			{
				foreach (Tool t in tools)
				{
					if (t.Definition.Function.Name == correctedName)
					{
						matchedTool = t;
						break;
					}
				}
			}
		}

		if (matchedTool == null)
		{
			return Error(toolCall.Id, $"Error: Tool '{toolCall.Name}' not found in available tools.");
		}

		(JsonObject? argsObj, string? argError) = FixJson.TryParseWithSchema(toolCall.ArgumentsJson, matchedTool.Definition.Function, fixLog);

		if (argsObj == null || argError != null)
		{
			return Error(toolCall.Id, argError ?? $"Error: Tool '{toolCall.Name}' received malformed arguments: {toolCall.ArgumentsJson}");
		}

		// ToolCall framing is already emitted by the TransportListener when the assistant turn is
		// fanned out by the producing protocol; ToolResponse framing is emitted when the tool result
		// is fanned out via Bundle.OnToolResult at commit. Just run the handler here.
		ToolResult result;
		try
		{
			result = await matchedTool.Handler(argsObj, toolCall.Id, ct, transport, sessionId, maxOutputTokens);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			result = Error(toolCall.Id, $"Tool '{toolCall.Name}' threw exception: {ex.Message}");
		}
		return result;
	}

	// Builds an error tool result (content in StdErr, non-zero exit) with an estimated token count so
	// it carries a real measurement like every other result, never a zero placeholder.
	private static ToolResult Error(string id, string message)
	{
		return new ToolResult(id, string.Empty, message, 1, EstimateTokens(message));
	}
}