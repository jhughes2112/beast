using System;


// The reasoning/thinking level configured for a model, in ascending order. None disables extended
// thinking entirely.
public enum ReasoningLevel
{
	None,
	Minimal,
	Low,
	Medium,
	High,
	Max
}

// Maps the single human word from settings (reasoningEffort) onto each provider's native reasoning
// control. The user only ever deals in words (none..max); the per-provider token budgets and effort
// strings are produced here and never surfaced. Each provider works differently, so there is one
// translator per wire shape: Anthropic budgets tokens, the OpenAI Responses and Chat Completions APIs
// take an effort word, and Chat Completions additionally needs softer fallbacks (see ProtocolChatCompletions).
public static class ReasoningEffort
{
	// Parses a settings word into a level. Common synonyms are accepted; anything unrecognized reads as
	// None so a typo quietly disables thinking instead of erroring the whole model.
	public static ReasoningLevel Parse(string? word)
	{
		ReasoningLevel level;
		string normalized = (word ?? string.Empty).Trim().ToLowerInvariant();
		switch (normalized)
		{
			case "minimal":
			case "min":
			case "minimum":
			case "lowest":
				level = ReasoningLevel.Minimal;
				break;
			case "low":
				level = ReasoningLevel.Low;
				break;
			case "medium":
			case "med":
			case "default":
			case "normal":
				level = ReasoningLevel.Medium;
				break;
			case "high":
				level = ReasoningLevel.High;
				break;
			case "max":
			case "maximum":
			case "highest":
			case "xhigh":
				level = ReasoningLevel.Max;
				break;
			default:
				level = ReasoningLevel.None;
				break;
		}
		return level;
	}

	// The canonical word shown after the model name while it is thinking. None shows nothing.
	public static string DisplayWord(string? word)
	{
		ReasoningLevel level = Parse(word);
		string result;
		switch (level)
		{
			case ReasoningLevel.Minimal:
				result = "minimal";
				break;
			case ReasoningLevel.Low:
				result = "low";
				break;
			case ReasoningLevel.Medium:
				result = "medium";
				break;
			case ReasoningLevel.High:
				result = "high";
				break;
			case ReasoningLevel.Max:
				result = "max";
				break;
			default:
				result = string.Empty;
				break;
		}
		return result;
	}

	// " (high)" style suffix appended to the model name in the status display; empty when thinking is off.
	public static string DisplaySuffix(string? word)
	{
		string display = DisplayWord(word);
		string result = string.IsNullOrEmpty(display) ? string.Empty : $" ({display})";
		return result;
	}

	// The OpenAI-style effort token shared by the Responses API (reasoning.effort) and Chat Completions
	// (reasoning_effort). Returns null for None so the field is omitted. Max collapses to "high": no
	// portable token above it exists across providers.
	public static string? OpenAiEffort(string? word)
	{
		ReasoningLevel level = Parse(word);
		string? result;
		switch (level)
		{
			case ReasoningLevel.Minimal:
				result = "minimal";
				break;
			case ReasoningLevel.Low:
				result = "low";
				break;
			case ReasoningLevel.Medium:
				result = "medium";
				break;
			case ReasoningLevel.High:
				result = "high";
				break;
			case ReasoningLevel.Max:
				result = "high";
				break;
			default:
				result = null;
				break;
		}
		return result;
	}

	// The Anthropic thinking budget in tokens, clamped to leave room for the visible answer within the
	// turn's max_tokens. Returns 0 when thinking is off or there is no room. Anthropic requires a budget
	// of at least 1024 and strictly less than max_tokens.
	public static int AnthropicBudget(string? word, int maxTokens)
	{
		ReasoningLevel level = Parse(word);
		int target;
		switch (level)
		{
			case ReasoningLevel.Minimal:
				target = 1024;
				break;
			case ReasoningLevel.Low:
				target = 4096;
				break;
			case ReasoningLevel.Medium:
				target = 8192;
				break;
			case ReasoningLevel.High:
				target = 16384;
				break;
			case ReasoningLevel.Max:
				target = 32000;
				break;
			default:
				target = 0;
				break;
		}

		int budget;
		if (target == 0 || maxTokens <= 1536)
		{
			budget = 0;
		}
		else
		{
			// Leave at least 512 tokens for the answer, and never drop below the 1024 minimum.
			int ceiling = maxTokens - 512;
			budget = target > ceiling ? ceiling : target;
			if (budget < 1024)
				budget = 1024;
		}
		return budget;
	}
}