using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// One small real request, to find out whether a model's edited settings work before the user is relying
// on them. Reaching the model and being answered is the whole test: the reply is never read, and a
// reasoning model that spends all 16 tokens thinking has still answered.
internal static class ModelTrial
{
	private const int kMaxOutputTokens = 16;

	// Rate-limit retries are not bounded by the per-request timeout, so the whole attempt gets a deadline.
	private const int kDeadlineMarginSeconds = 30;

	// Returns why the model could not answer; empty when it did.
	public static async Task<string> RunAsync(LlmService service, string sessionId, CancellationToken ct)
	{
		int seconds = ProtocolHelpers.RequestTimeoutSeconds(service.Model) + kDeadlineMarginSeconds;
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(TimeSpan.FromSeconds(seconds));

		// Throwaway stage session reusing the caller's ID: nothing is announced or saved, and the request
		// lands in that session's log. Silent, so a failure is reported here and not as a chat alert.
		TransportSilent quiet = new TransportSilent();
		BeastSession    data  = new BeastSession(sessionId, "model test", service.Model.ConfigId, "ModelTest",
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session stage = new Session(data, "Reply briefly.", quiet, false);
		stage.MarkStagePrompt();
		stage.UpdateModel(service.Model);
		stage.Bundle.OnUserMessage("Hello World");

		ProtocolResult result = await service.RunToCompletionAsync(stage, Array.Empty<Tool>(), null, 0, kMaxOutputTokens, false, quiet, deadline.Token);

		string error = string.Empty;
		if (result.Outcome != ProtocolCallOutcome.Success)
		{
			if (deadline.IsCancellationRequested)
				error = $"No answer within {seconds}s.";
			else
				error = string.IsNullOrEmpty(result.ErrorMessage) ? result.Outcome.ToString() : result.ErrorMessage;
		}
		return error;
	}
}