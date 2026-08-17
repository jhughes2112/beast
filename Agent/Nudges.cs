using System;


// The steering strings the system injects as user messages to keep a model working: end-of-turn
// nudges, wind-down orders, and invalid-tool-call corrections. They
// exist to whip the CURRENT turn along and carry no information once that turn is past, so
// mechanical compaction strips them from a successor's history. Builders and recognizers live
// together here so a reworded template can never desynchronize from the matcher that identifies it.
public static class Nudges
{
	// Stable prefixes of the generated templates below; matching is StartsWith because the full
	// strings embed terminator names and token counts. "That output is about " no longer has a
	// builder — replies are no longer rewritten to fit a caller's budget — but sessions written by
	// earlier versions still carry it, and it is just as strippable now as it was then.
	private static readonly string[] kPrefixes = new string[]
	{
		"Continue the task, then call the ",
		"You are out of working turns.",
		"That output is about ",
		"A tool call in your previous response was invalid",
		"The conversation was compacted to free context.",
	};

	public static string ContinueTask(string terminatorName)
	{
		return $"Continue the task, then call the {terminatorName} tool with your final result to finish.";
	}

	public static string OutOfTurns(string terminatorName)
	{
		return $"You are out of working turns. Call the {terminatorName} tool now with your final result, "
			+ "preserving the key details (file paths, line numbers, names, key output).";
	}

	// Injected as the last message of a compaction successor that was mid-work: the elided history
	// ends on satisfied tool results, which asks the model for nothing, so without this the session
	// would park waiting for the user exactly when it should have carried on.
	public static string ResumeAfterCompaction()
	{
		return "The conversation was compacted to free context. Continue the work from where it left off.";
	}

	public static string InvalidToolCall(string problem)
	{
		return $"A tool call in your previous response was invalid and was discarded — it never ran. {problem}. Call the tool again with every required argument supplied correctly.";
	}

	// True when the text is one of the injected steering messages: a generated template above or
	// the role's own end-of-turn prompt (passed in because the role owns that text).
	public static bool IsNudge(string text, string endOfTurnPrompt)
	{
		bool nudge = !string.IsNullOrEmpty(endOfTurnPrompt) && string.Equals(text, endOfTurnPrompt, StringComparison.Ordinal);
		if (!nudge)
		{
			foreach (string prefix in kPrefixes)
			{
				if (text.StartsWith(prefix, StringComparison.Ordinal))
				{
					nudge = true;
					break;
				}
			}
		}
		return nudge;
	}
}