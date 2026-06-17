using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Runs an internal helper role (Web, Explorer) as a throwaway child session seeded with content to process,
// and returns what the model passes to return_to_caller. The model gets up to maxTurns: every turn that does
// not finish is nudged with the role's end-of-turn prompt, and return_to_caller is forced on the final turn
// so the loop always terminates. This is the shared spine behind WebFetch and ReadFileExplorer — both seed a
// role with content and want one clean returned answer, optionally after working turns. extraTools are the
// tools the helper may call while working (ReadFileExplorer passes none; WebFetch passes bash and read_file
// so the Web role can inspect the files a fetch saved); return_to_caller is always added on top of them.
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

		string lastAssistantText = string.Empty;
		int tokens = 0;

		session.SendBusy();
		try
		{
			for (int turn = 1; turn <= maxTurns; turn++)
			{
				// Force the terminator on the last allotted turn so the loop cannot run on; earlier turns
				// leave it optional so the model can do a working turn before it must finalize.
				bool lastTurn = turn == maxTurns;
				string? forcedToolName = lastTurn ? "return_to_caller" : null;

				ProtocolResult result = await service.RunToCompletionAsync(session, tools, forcedToolName, 0, maxOutputTokens, transport, cancellationToken);
				if (result.Outcome != ProtocolCallOutcome.Success)
					return (false, string.Empty, 0);

				session.CommitAssistantTurn(result.Payload!);
				lastAssistantText = result.Payload!.AssistantText;

				bool hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, tools, session, transport, cancellationToken);
				if (hasToolCalls)
					session.CommitToolResults(result.Payload!);

				tokens = session.LastTokenUsage?.CompletionTokens ?? tokens;

				if (returned != null)
					return (true, returned, Math.Max(1, tokens));

				// A turn that ended without finishing: nudge it with the role's end-of-turn prompt to call
				// return_to_caller next time. Never reached on the last turn (the terminator was forced).
				if (!hasToolCalls && !lastTurn)
					session.AddUserMessage(role.EndOfTurnPrompt);
			}

			// The forced final turn still did not call return_to_caller: fall back to its assistant text so
			// the caller gets something usable rather than nothing.
			int fallbackTokens = tokens > 0 ? tokens : ToolDispatch.EstimateTokens(lastAssistantText);
			return (true, lastAssistantText, Math.Max(1, fallbackTokens));
		}
		finally
		{
			// Cost is spent regardless of how the run ended; roll it up into the calling session.
			parent.RecordCost(session.TotalCost);
			session.SendIdle();
		}
	}
}
