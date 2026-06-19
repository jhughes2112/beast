using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Runs an internal helper role (Web, Explorer) as a throwaway child session seeded with content to process,
// and returns what the model passes to return_to_caller. The model gets up to maxTurns. Because some models
// and OpenAI-compatible servers mishandle one tool_choice form but honor another, the constraint is cycled
// each turn: force the specific terminator, then force any tool, then leave it on auto, repeating. A turn that
// produces no tool call is nudged with the role's end-of-turn prompt. The answer normally arrives through
// return_to_caller; if the model burns every turn without ever calling it, the run does not fail — it salvages
// the model's last assistant message and returns that, flagged so the caller knows tool calling went wrong.
// This is the shared spine behind WebFetch and ReadFileExplorer — both seed a role with content and want one
// returned answer, optionally after working turns. extraTools are the tools the helper may call while working
// (ReadFileExplorer passes none; WebFetch passes bash and read_file so the Web role can inspect the files a
// fetch saved); return_to_caller is always added on top of them.
public static class HelperSession
{
	public static async Task<(bool ok, string output, int responseTokens)> RunAsync(
		Session parent,
		Role role,
		LlmService service,
		string displayName,
		string seedMessage,
		int maxTurns,
		int maxOutputTokens,
		Tool[] extraTools,
		ITransportServer transport,
		CancellationToken cancellationToken)
	{
		BeastSession data = new BeastSession(parent.AllocateChildId(), displayName, service.Model.ConfigId, role.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral, 0);
		Session session = new Session(data, role.SystemPrompt, transport, true);

		// The constructor no longer displays the system prompt; a helper session has no other replay path,
		// so emit its (system-only) history now. The seed user message displays when flushed during the run.
		session.ReplayToTransport();
		session.AddUserMessage(seedMessage);
		session.AnnounceToClient();
		parent.AddChild(session);

		// return_to_caller terminates the run; the model finishes by calling it and its argument is the
		// answer. Any extraTools the role works with come first, with the terminator appended on top.
		string? returned = null;
		Tool terminator = ToolFactory.CreateReturnToCallerTool(output => returned = output);
		Tool[] tools = new Tool[extraTools.Length + 1];
		for (int i = 0; i < extraTools.Length; i++)
			tools[i] = extraTools[i];
		tools[extraTools.Length] = terminator;

		int tokens = 0;
		string lastAssistantText = string.Empty;

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
					case 0:  forcedToolName = "return_to_caller"; break;
					case 1:  forcedToolName = ProtocolProxy.AnyTool; break;
					default: forcedToolName = null; break;
				}

				ProtocolResult result = await service.RunToCompletionAsync(session, tools, forcedToolName, 0, maxOutputTokens, transport, cancellationToken);
				if (result.Outcome != ProtocolCallOutcome.Success)
					return (false, string.Empty, 0);

				session.CommitAssistantTurn(result.Payload!);

				// Remember the latest non-empty assistant text in case the model never calls return_to_caller.
				if (!string.IsNullOrWhiteSpace(result.Payload!.AssistantText))
					lastAssistantText = result.Payload!.AssistantText;

				bool hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, tools, session, transport, cancellationToken);
				if (hasToolCalls)
					session.CommitToolResults(result.Payload!);

				tokens = session.LastTokenUsage?.CompletionTokens ?? tokens;

				if (returned != null)
					return (true, returned, Math.Max(1, tokens));

				// A turn that produced no tool call: nudge it with the role's end-of-turn prompt to call
				// return_to_caller next time.
				if (!hasToolCalls)
					session.AddUserMessage(role.EndOfTurnPrompt);
			}

			// Every turn is spent and return_to_caller was never called — some models and servers simply will
			// not emit a tool call however the choice is constrained. Rather than throw the work away, salvage
			// the model's last assistant message and hand it back flagged, so the caller still gets the content.
			if (!string.IsNullOrWhiteSpace(lastAssistantText))
				return (true, $"This model had a problem calling tools, but here's what it output: {lastAssistantText}", Math.Max(1, tokens));

			return (false, string.Empty, 0);
		}
		finally
		{
			// Cost is spent regardless of how the run ended; roll it up into the calling session.
			parent.RecordCost(session.TotalCost);
			session.SendIdle();
		}
	}
}
