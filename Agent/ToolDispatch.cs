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

		// Run every call in parallel. ExecuteToolAsync owns each call's outcome and returns a real
		// ToolResult for every non-cancel failure; it throws only on a genuine cancel (ct cancelled).
		Task<ToolResult>[] tasks = new Task<ToolResult>[toolCalls.Count];
		for (int i = 0; i < toolCalls.Count; i++)
		{
			tasks[i] = ExecuteToolAsync(toolCalls[i], tools, session.Id, perToolBudget, transport, ct);
		}

		// Drain each call independently rather than letting Task.WhenAll make the whole round hostage to
		// the first task that throws: one call faulting or being cancelled must never discard a sibling's
		// completed result. Fill ToolResults in call order so CommitToolResults fans them out in the same
		// order as the calls. The round's full reservation stays charged against the budget until the next
		// provider response measures the real context size — we never settle it to a per-tool estimate.
		payload.ToolResults.Clear();
		bool cancelled = false;
		for (int i = 0; i < toolCalls.Count; i++)
		{
			try
			{
				payload.ToolResults.Add(await tasks[i]);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				// A genuine user/parent cancel: record a cancelled result for this call and remember to
				// propagate once every task is drained, so the dispatch loop's cancellation guard still fires.
				cancelled = true;
				payload.ToolResults.Add(Error(toolCalls[i].Id, "Tool call was cancelled."));
			}
			catch (Exception ex)
			{
				// Backstop: ExecuteToolAsync should already have turned this into a result. If anything ever
				// escapes it, keep that from discarding the rest of the round and surface the reason here.
				Console.Error.WriteLine($"[ToolDispatch] Tool '{toolCalls[i].Name}' escaped dispatch with {ex.GetType().Name}: {ex}");
				payload.ToolResults.Add(Error(toolCalls[i].Id, $"Tool '{toolCalls[i].Name}' threw exception:\n{ex}"));
			}
		}

		// Every task is drained now, so no tool is still touching the session. Only a genuine cancel
		// propagates — after all siblings are settled — so the caller's cancellation guard parks/unwinds
		// exactly as before; a per-tool failure never gets that far.
		if (cancelled)
			throw new OperationCanceledException(ct);

		return true;
	}

	// Resolves a single tool call to its handler (exact match, then fuzzy name correction), repairs
	// the arguments against the schema, and runs the handler. Never throws except on a genuine cancel
	// (ct cancelled), which propagates so the dispatch loop's cancellation guard can handle it. Every
	// other failure — name match, argument repair, or the handler itself blowing up — becomes an error
	// ToolResult so the model can see and correct it, and the reason is logged rather than lost: a
	// dropped exception here is what silently unwound the whole subtree as a fake cancel.
	public static async Task<ToolResult> ExecuteToolAsync(SemanticToolCall toolCall, Tool[] tools, string sessionId, int maxOutputTokens, ITransportServer transport, CancellationToken ct)
	{
		ToolResult result;
		try
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
				result = Error(toolCall.Id, $"Error: Tool '{toolCall.Name}' not found in available tools.");
			}
			else
			{
				(JsonObject? argsObj, string? argError) = FixJson.TryParseWithSchema(toolCall.ArgumentsJson, matchedTool.Definition.Function, fixLog);

				if (argsObj == null || argError != null)
				{
					result = Error(toolCall.Id, argError ?? $"Error: Tool '{toolCall.Name}' received malformed arguments: {toolCall.ArgumentsJson}");
				}
				else
				{
					// ToolCall framing is already emitted by the TransportListener when the assistant turn is
					// fanned out by the producing protocol; ToolResponse framing is emitted when the tool result
					// is fanned out via Bundle.OnToolResult at commit. Just run the handler here.
					result = await matchedTool.Handler(argsObj, toolCall.Id, ct, transport, sessionId, maxOutputTokens);
				}
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// A genuine user/parent cancel tied to our token: let it propagate so the dispatch loop's
			// cancellation guard handles it (park for steering, or unwind the subtree).
			throw;
		}
		catch (OperationCanceledException ex)
		{
			// An OCE NOT tied to our token — an internal/library timeout surfacing as a cancel. Surface it
			// as an error result with its reason instead of letting it masquerade as a real cancel and
			// silently unwind the dispatch loop.
			Console.Error.WriteLine($"[ToolDispatch] Tool '{toolCall.Name}' raised a non-cancel OperationCanceledException: {ex}");
			transport.Error(sessionId, $"Tool '{toolCall.Name}' was aborted by an internal timeout:\n{ex}");
			result = Error(toolCall.Id, $"Error: Tool '{toolCall.Name}' timed out:\n{ex}");
		}
		catch (Exception ex)
		{
			// Any other failure — including an argument-repair pass that threw, or a bug in the handler —
			// becomes an error result the model can act on, with the reason logged server-side so a future
			// occurrence is diagnosable instead of vanishing as a lost task fault.
			Console.Error.WriteLine($"[ToolDispatch] Tool '{toolCall.Name}' threw {ex.GetType().Name}: {ex}");
			transport.Error(sessionId, $"Tool '{toolCall.Name}' threw {ex.GetType().Name}:\n{ex}");
			result = Error(toolCall.Id, $"Tool '{toolCall.Name}' threw exception:\n{ex}");
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