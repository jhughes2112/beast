using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Shared tool-call dispatch for the root SessionRunner and the SubagentRunner. Stateless: given a
// completed assistant payload, it runs that turn's tool calls one at a time in call order, settles the session's
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

	// The ONLY ceiling on a single raw tool output, and deliberately a fixed one: no grep over a huge
	// log is worth 190k tokens whatever the window has free. It is a statement about the output's own
	// worth, not about the caller's remaining room — sizing it from the room left is what produced
	// impossible demands ("answer in six tokens") near the top of the window.
	public const int MaxRawOutputTokens = 16000;

	// Stamps a raw (server-unmeasured) tool result with an estimated token count and, only here,
	// truncates it to maxOutputTokens tightened by the sanity ceiling. Results produced by a
	// sub-session carry the provider's exact measurement and never pass through this. The estimate is
	// taken over the content exactly as CanonicalConversation renders it, so it matches the context cost.
	public static ToolResult MeasureRawResult(ToolResult raw, int maxOutputTokens)
	{
		string content = raw.StdOut;
		if (!string.IsNullOrEmpty(raw.StdErr))
			content = content + "\nstderr: " + raw.StdErr;

		int estimated = EstimateTokens(content);
		int cap       = maxOutputTokens > 0 ? Math.Min(maxOutputTokens, MaxRawOutputTokens) : MaxRawOutputTokens;

		if (estimated > cap)
		{
			// Over the budget and unmeasurable: fold the rendered content into StdOut, clip it to
			// the budget, and flag the clip so the model knows the output is partial and why.
			const string notice = "\n[Output truncated — narrow the request (fewer lines, a tighter pattern, head/tail the output) to see the rest.]";
			int    charBudget = cap * CharsPerToken;
			int    keep       = Math.Max(0, charBudget - notice.Length);
			string clipped    = (content.Length > keep ? content.Substring(0, keep) : content) + notice;
			return new ToolResult(raw.Id, clipped, string.Empty, raw.ExitCode, EstimateTokens(clipped));
		}

		return new ToolResult(raw.Id, raw.StdOut, raw.StdErr, raw.ExitCode, estimated);
	}

	// Runs a call on its own clock and adds what it worked to the session. Calls in a round each add their own.
	private static async Task<ToolResult> ExecuteTimedAsync(SemanticToolCall toolCall, Dictionary<string, Tool> toolLookup, Session session, int maxOutputTokens, ITransportServer transport, CancellationToken ct)
	{
		ToolClock clock   = new ToolClock();
		ToolClock.Current = clock;
		try
		{
			return await ExecuteToolAsync(toolCall, toolLookup, session.Id, maxOutputTokens, transport, ct);
		}
		finally
		{
			session.RecordWork(clock.WorkedMs);
		}
	}

	// Dispatches every tool call in the payload, filling payload.ToolResults in call order. Returns
	// true when at least one tool ran (the assistant turn continues), false when the turn carried no
	// tool calls (the turn is complete). seeded is for a seeded model, so a run can repeat: the sandbox's file times
	// and the clock its shell sees follow the tool call count instead of the real clock. Seeded or not, calls run one
	// at a time in call order, because calls that overlap reach shared files and the model's endpoint in whatever
	// order they happen to get there.
	public static async Task<bool> DispatchAsync(ProtocolCallPayload payload, Tool[] tools, Session session, ITransportServer transport, CancellationToken ct, bool seeded = false)
	{
		if (payload.ToolCalls.Count == 0)
			return false;

		// Build the lookup once and reuse across both FixToolCalls and ExecuteToolAsync so each call
		// resolves in O(1) instead of rescanning the tool array.
		Dictionary<string, Tool> toolLookup = new Dictionary<string, Tool>(tools.Length);
		foreach (Tool t in tools)
			toolLookup[t.Definition.Function.Name] = t;

		List<SemanticToolCall> toolCalls = new List<SemanticToolCall>(payload.ToolCalls);

		if (seeded)
		{
			string clockFailure = await Sandbox.AdvanceFileClockAsync(toolCalls.Count, ct);
			if (clockFailure.Length > 0)
			{
				transport.Status(session.Id, clockFailure);
				session.QueryLog.Note(clockFailure);
			}
		}

		// Every call gets the same fixed ceiling — a property of what any single raw output is worth,
		// not of how much room the caller happens to have left. Sizing outputs from the remaining
		// window is what asked a review to fit in six tokens; the round is charged to the budget
		// after the fact instead, and an over-window conversation compacts on the next pass.
		//
		// A round runs in call order. A model writes its calls in the order it means them: a shell command
		// that makes a page, the screenshot of that page, the inspect of that screenshot, all in one turn.
		// Started together, the inspect looked for the picture before the browser had saved it ("File not
		// found") and the screenshot looked for the page before the shell had written it ("There is no file
		// at"). So every call runs alone, after the one before it has finished: a round that always comes out
		// the same is worth more than the time overlapping saved.
		Task<ToolResult>[] tasks = new Task<ToolResult>[toolCalls.Count];
		for (int i = 0; i < toolCalls.Count; i++)
		{
			tasks[i] = ExecuteTimedAsync(toolCalls[i], toolLookup, session, MaxRawOutputTokens, transport, ct);
			// WhenAny only waits. A failure stays in the task, for the loop below to handle like any other.
			await Task.WhenAny(tasks[i]);
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
				// A genuine user/parent cancel: record a result for this call and remember to
				// propagate once every task is drained, so the dispatch loop's cancellation guard still
				// fires. The slot MUST be filled — an unanswered call is a malformed conversation the
				// strict providers reject wholesale.
				cancelled = true;
				payload.ToolResults.Add(Error(toolCalls[i].Id, "[interrupted by user]"));

				// The underlying work may not have stopped — a subagent asked to do something long
				// keeps going. Its eventual output cannot be observed HERE (this task is terminal;
				// it will never transition to completed), so the orchestrator's completion callback
				// delivers a late-finishing child's result to the parent session instead.
			}
			catch (Exception ex)
			{
				// Backstop: ExecuteToolAsync should already have turned this into a result. If anything ever
				// escapes it, keep that from discarding the rest of the round and surface the reason here.
				Console.Error.WriteLine($"[ToolDispatch] Tool '{toolCalls[i].Name}' escaped dispatch with {ex.GetType().Name}: {ex}");
				payload.ToolResults.Add(Error(toolCalls[i].Id, $"Tool '{toolCalls[i].Name}' threw exception:\n{ex}"));
			}
		}

		// Charge the round at the size it actually came back, so the next request is sized against
		// what the conversation now really holds. A sub-session's reply carries its provider's exact
		// measurement; a raw handler's output carries its own estimate.
		int roundTokens = 0;
		foreach (ToolResult settled in payload.ToolResults)
			roundTokens += settled.MeasuredOutputTokens;
		session.Budget.ChargeToolResults(roundTokens);
		session.SendStats();

		// Every task is drained now, so no tool is still touching the session. Only a genuine cancel
		// propagates — after all siblings are settled — so the caller's cancellation guard parks/unwinds
		// exactly as before; a per-tool failure never gets that far.
		if (cancelled)
			throw new OperationCanceledException(ct);

		return true;
	}

	// Runs a single tool call. By the time dispatch runs, FixToolCalls has already corrected the name and
	// repaired the arguments before commit, so this does a plain exact lookup and parses the vetted
	// arguments directly — no fuzzy matching, no argument repair. Never throws except on a genuine cancel
	// (ct cancelled), which propagates so the dispatch loop's cancellation guard can handle it. Every other
	// failure — an unknown tool, or the handler itself blowing up — becomes an error ToolResult so the model
	// can see it, and the reason is logged rather than lost: a dropped exception here is what silently
	// unwound the whole subtree as a fake cancel.
	public static async Task<ToolResult> ExecuteToolAsync(SemanticToolCall toolCall, Dictionary<string, Tool> toolLookup, string sessionId, int maxOutputTokens, ITransportServer transport, CancellationToken ct)
	{
		ToolResult result;
		try
		{
			Tool? matchedTool = null;
			toolLookup.TryGetValue(toolCall.Name, out matchedTool);

			if (matchedTool == null)
			{
				result = string.Equals(toolCall.Name, MechanicalCompaction.MemoryToolName, StringComparison.Ordinal)
					? Error(toolCall.Id, $"Error: {toolCall.Name} is a record of an earlier tool call, not a tool. Call the real tool instead.")
					: Error(toolCall.Id, $"Error: Tool '{toolCall.Name}' not found in available tools.");
			}
			else
			{
				JsonObject? argsObj = JsonNode.Parse(toolCall.ArgumentsJson) as JsonObject;
				if (argsObj == null)
				{
					result = Error(toolCall.Id, $"Error: Tool '{toolCall.Name}' has unusable arguments: {toolCall.ArgumentsJson}");
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

	// Repairs an assistant turn's tool calls IN PLACE before the turn is committed: corrects a near-miss
	// tool name to the real one, and normalizes/repairs each call's arguments against the tool's schema,
	// writing both back into the payload so the committed turn (canonical and protocol-native) carries clean
	// calls instead of the raw model output. Returns null when every call is now dispatchable; otherwise a
	// reason naming the calls whose arguments cannot satisfy the schema, so the caller can treat the turn as
	// a transient failure and re-request. A call whose name does not resolve at all keeps its name — dispatch
	// reports it as not-found so the model gets that feedback rather than a silent re-roll — but its
	// arguments are still repaired to valid JSON, since they are committed and replayed to the provider.
	public static string? FixToolCalls(ProtocolCallPayload payload, Dictionary<string, Tool> toolLookup)
	{
		string broken = string.Empty;
		foreach (SemanticToolCall toolCall in payload.ToolCalls)
		{
			// Resolve the tool by exact name, then by a fuzzy correction for a near-miss. This is the only
			// place name correction happens; dispatch later trusts the pinned name with a plain lookup.
			Tool? matchedTool = null;
			toolLookup.TryGetValue(toolCall.Name, out matchedTool);
			if (matchedTool == null)
			{
				string[] knownNames = new string[toolLookup.Count];
				toolLookup.Keys.CopyTo(knownNames, 0);

				string? correctedName = FixJson.FuzzyMatchToolName(toolCall.Name, knownNames, 3, null);
				if (correctedName != null)
				{
					toolLookup.TryGetValue(correctedName, out matchedTool);
				}
			}

			if (matchedTool == null)
			{
				// Genuinely unknown tool: left for dispatch to report as not-found — but its arguments are
				// still committed and replayed on every later request, and strict providers reject the whole
				// conversation when they are not valid JSON (a truncated stream leaves an unterminated
				// string). Repair structurally — there is no schema to check against — and when even that
				// fails, pin an empty object so the committed turn always carries parseable arguments.
				JsonObject? repairedArgs = FixJson.TryParseObject(toolCall.ArgumentsJson);
				toolCall.ArgumentsJson   = repairedArgs != null ? repairedArgs.ToJsonString() : "{}";
				continue;
			}

			// Pin the (possibly corrected) name so it is persisted, not re-derived on every dispatch.
			toolCall.Name = matchedTool.Definition.Function.Name;

			(JsonObject? argsObj, string? argError) = FixJson.TryParseWithSchema(toolCall.ArgumentsJson, matchedTool.Definition.Function, null);
			if (argsObj == null || argError != null)
			{
				string reason = $"'{toolCall.Name}': {argError ?? "arguments could not be repaired to match the schema"}";
				broken        = broken.Length == 0 ? reason : broken + "; " + reason;
				continue;
			}

			// Persist the repaired arguments so the committed turn — and the next request — is clean.
			toolCall.ArgumentsJson = argsObj.ToJsonString();
		}

		return broken.Length == 0 ? null : "Tool call(s) could not be repaired: " + broken;
	}

	// Builds an error tool result (content in StdErr, non-zero exit) with an estimated token count so
	// it carries a real measurement like every other result, never a zero placeholder.
	private static ToolResult Error(string id, string message)
	{
		return new ToolResult(id, string.Empty, message, 1, EstimateTokens(message));
	}
}