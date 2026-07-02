using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Runs an internal helper role (Explorer, WebFetch) as a child session seeded with content to process, and
// returns what the model passes to return_to_caller. The model gets up to maxTurns. Because some models and
// OpenAI-compatible servers mishandle one tool_choice form but honor another, the constraint is cycled each
// turn: force the specific terminator, then force any tool, then leave it on auto, repeating. A turn that
// produces no tool call is nudged with the role's end-of-turn prompt. The answer normally arrives through
// return_to_caller; if the model burns every turn without ever calling it, the run does not fail — it salvages
// the model's last assistant message and returns that, flagged so the caller knows tool calling went wrong.
public static class HelperSession
{
	public static async Task<(bool ok, string output, int responseTokens)> RunAsync(
		BeastSettings settings,
		Session parent,
		Role role,
		LlmRegistry registry,
		string displayName,
		string seedMessage,
		int maxTurns,
		int maxOutputTokens,
		ITransportServer transport,
		CancellationToken cancellationToken)
	{
		// Allocate the child id and immediately persist the parent so its bumped counter reaches disk
		// before this (non-ephemeral) helper writes its own file. Without it a reload restores the old counter
		// and the next child reissues this id, overwriting the file. Root parent updates lastSession; a
		// subagent parent does not. Skipped for an ephemeral parent, whose children are never saved anyway.
		string childId = parent.AllocateChildId();
		if (!parent.Ephemeral)
			SessionService.Save(parent.Data);

		LlmService? service = registry.CreateService(role, string.Empty, 0);
		if (service == null)
		{
			string errmsg = $"[HelperSession] {role.Name} failed to produce an LlmService on start";
			Console.Error.WriteLine(errmsg);
			return (false, errmsg, 0);
		}

		BeastSession data = new BeastSession(childId, displayName, service.Model.ConfigId, role.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral);
		Session session = new Session(data, role.SystemPrompt, transport, true);
		session.UpdateModel(service.Model);

		// The constructor no longer displays the system prompt; a helper session has no other replay path,
		// so emit its (system-only) history now. The seed user message displays when flushed during the run.
		session.ReplayToTransport();
		session.AddUserMessage(seedMessage);
		session.AnnounceToClient();
		parent.AddChild(session);

		// return_to_caller terminates the run; all working tools for the role plus the terminator are
		// built in one call. Helper roles never delegate further, so all delegation callbacks are null.
		// A list rather than a single variable so parallel return_to_caller calls are all captured;
		// if the model calls it more than once in one turn the outputs are merged at the check below.
		List<string> returnedOutputs = new List<string>();
		Tool[] tools = ToolFactory.BuildForRole(settings, role, null, null, session, null, false, null, null, null, null, null, null, output => returnedOutputs.Add(output));

		int tokens = 0;
		string lastAssistantText = string.Empty;
		// Tracks the intended final status; overridden to Success on clean exits, Incomplete on cancel.
		// The terminal tool handler sets session.Status directly when called; this fallback applies
		// only if the run ends without ever reaching the terminator (failure or exhaustion).
		SessionStatus finalStatus = SessionStatus.Failure;

		// This helper's own cancellation scope, linked to the caller's token so a /cancel targeting the caller
		// (or any ancestor) still cascades down and stops the helper, while a /cancel targeting the helper alone
		// stays contained here and the caller keeps running. Registered so Interrupt can reach a running tool.
		using CancellationTokenSource scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		session.SetDispatchScope(scope);

		session.SendBusy();
		try
		{
			for (int turn = 1; turn <= maxTurns; turn++)
			{
				// Cycle the tool-call constraint each turn so a model or server that mishandles one form gets
				// another shot at a different one: force the specific terminator, then force any tool, then
				// leave it on auto, repeating. Whatever environment the model works best in, one of these lands.
				string? forcedToolName;
				switch ((turn - 1) % 3)
				{
					case 0:
						forcedToolName = "return_to_caller";
						break;
					case 1:
						forcedToolName = ProtocolProxy.AnyTool;
						break;
					default:
						forcedToolName = null;
						break;
				}

				ProtocolResult result = await service.RunToCompletionAsync(session, tools, forcedToolName, 0, maxOutputTokens, transport, scope.Token);
				if (result.Outcome == ProtocolCallOutcome.TooManyRetries)
				{
					// Sustained-rate-limited on this model. Swap in the next usable model from the role's list
					// (like /model) and retry the same turn; don't spend a turn on the swap. Give up only when
					// the list is exhausted, which falls through to the genuine-failure return below.
					LlmService? fallback = registry.CreateFallbackService(service, 0);
					if (fallback != null)
					{
						service = fallback;
						session.UpdateModel(fallback.Model);
						transport.Status(session.Id, $"Rate limited; falling back to {service.Model.Config.Name}");
						turn--;
						continue;
					}
				}
				if (result.Outcome != ProtocolCallOutcome.Success)
				{
					// A /cancel hand back a "cancelled by the user" answer so the caller is unblocked and keeps
					// going, rather than hanging on this helper. Anything else is a genuine failure — return the
					// outcome (and any error text) as the reason so the caller surfaces it instead of a blank fail.
					if (scope.IsCancellationRequested)
					{
						finalStatus = SessionStatus.Incomplete;
						return (true, "Cancelled by the user.", 1);
					}
					string failure = string.IsNullOrEmpty(result.ErrorMessage) ? result.Outcome.ToString() : $"{result.Outcome}: {result.ErrorMessage}";
					Console.Error.WriteLine($"[HelperSession] {role.Name} sub-session {session.Id} failed on turn {turn}: {failure}");
					return (false, failure, 0);
				}

				session.CommitAssistantTurn(result.Payload!);

				// Remember the latest non-empty assistant text in case the model never calls return_to_caller.
				if (!string.IsNullOrWhiteSpace(result.Payload!.AssistantText))
					lastAssistantText = result.Payload!.AssistantText;

				bool hasToolCalls;
				try
				{
					hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, tools, session, transport, scope.Token);
				}
				catch (OperationCanceledException) when (scope.IsCancellationRequested)
				{
					finalStatus = SessionStatus.Incomplete;
					return (true, "Cancelled by the user.", 1);
				}
				if (hasToolCalls)
				{
					session.CommitToolResults(result.Payload!);
				}

				tokens = session.LastTokenUsage?.CompletionTokens ?? tokens;

				if (returnedOutputs.Count > 0)
				{
					string returned = returnedOutputs.Count == 1
						? returnedOutputs[0]
						: string.Join("\n---\n", returnedOutputs);
					return (true, returned, Math.Max(1, tokens));
				}

				// A turn that produced no tool call: nudge it with the role's end-of-turn prompt to call
				// return_to_caller next time.
				if (!hasToolCalls)
					session.AddUserMessage(role.EndOfTurnPrompt);
			}

			// Every turn is spent and return_to_caller was never called — some models and servers simply will
			// not emit a tool call however the choice is constrained. Rather than throw the work away, salvage
			// the model's last assistant message and hand it back flagged, so the caller still gets the content.
			if (!string.IsNullOrWhiteSpace(lastAssistantText))
			{
				finalStatus = SessionStatus.Incomplete;
				return (true, $"This model had a problem calling tools, but here's what it output: {lastAssistantText}", Math.Max(1, tokens));
			}

			Console.Error.WriteLine($"[HelperSession] {role.Name} sub-session {session.Id} exhausted all {maxTurns} turns without producing a result.");
			return (false, $"the {role.Name} role used all {maxTurns} turns without producing a result", 0);
		}
		finally
		{
			session.SetDispatchScope(null);

			// Cost is spent regardless of how the run ended; roll it up into the calling session.
			parent.RecordCost(session.TotalCost);

			// Set status only when the terminal tool did not already set it (i.e. it was never called).
			if (session.Status == SessionStatus.Ongoing)
				session.SetTerminationStatus(finalStatus);

			// Persist the helper session unless it inherited an ephemeral parent (a no-worktree root): a
			// non-ephemeral helper is a real saved conversation, so it survives a reload and stays in the
			// session tree, exactly like a subagent session. The Ephemeral flag is the single switch.
			if (!session.Ephemeral)
				SessionService.Save(session.Data);

			session.SendStats();
			session.SendIdle();
		}
	}
}