using System;
using System.Collections.Generic;


// A model's reasoning effort is one of ITS OWN options, stored and sent exactly as written. The options
// come from the model's endpoint wherever it lists them (ModelCatalog: Anthropic's capabilities.effort,
// OpenRouter's supported_efforts, a server's reasoning.efforts, the ChatGPT backend's and the Claude Code
// CLI's per-model levels), and otherwise from what the protocol itself takes (StandardWords,
// AnthropicBudgetWords, ClaudeCodeWords). There is no shared ladder of levels to translate between: Qwen3.8
// offers low, medium and xhigh, an OpenAI model may stop at high, an Anthropic one goes to max, and each
// is shown and sent its own words. Only Anthropic's budgeted thinking needs a number, so its protocol
// names map to budget_tokens here.
//
// "none" is the one word that is not an option: it turns thinking off, which each protocol states its
// own way (no effort field, enable_thinking false), and is offered unless the endpoint says the model's
// thinking cannot be turned off.
public static class ReasoningEffort
{
	public const string Off = "none";

	// The words every Chat Completions and Responses endpoint takes, for a model whose endpoint lists none.
	private static readonly string[] kStandardWords = { "low", "medium", "high" };

	// Anthropic's effort words in its own order, lowest first: the order capabilities.effort's keys do not
	// carry, and the levels the Claude Code CLI's --effort takes.
	private static readonly string[] kAnthropicOrder = { "low", "medium", "high", "xhigh", "max" };

	// Anthropic's budgeted thinking (thinking.type enabled, budget_tokens) for a model whose catalog lists no
	// effort words. Each name is also an adaptive-thinking effort word, so the setting still holds when a
	// model turns out to want output_config.effort instead (ProtocolAnthropic.WantsAdaptiveThinking).
	private static readonly (string Name, int Tokens)[] kAnthropicBudgets = { ("low", 4096), ("medium", 8192), ("high", 16384), ("max", 32000) };

	public static List<string> StandardWords()
	{
		return new List<string>(kStandardWords);
	}

	public static List<string> ClaudeCodeWords()
	{
		return new List<string>(kAnthropicOrder);
	}

	public static List<string> AnthropicBudgetWords()
	{
		List<string> names = new List<string>();
		foreach ((string name, int _) in kAnthropicBudgets)
			names.Add(name);
		return names;
	}

	// Anthropic words lowest first: the known ones in Anthropic's order, then any word it adds later, as given.
	public static List<string> InAnthropicOrder(IEnumerable<string> words)
	{
		List<string> given  = new List<string>(words);
		List<string> result = new List<string>();
		foreach (string known in kAnthropicOrder)
		{
			if (given.Contains(known))
				result.Add(known);
		}
		foreach (string word in given)
		{
			if (!result.Contains(word))
				result.Add(word);
		}
		return result;
	}

	public static bool IsOff(string? word)
	{
		return string.IsNullOrEmpty(word) || string.Equals(word, Off, StringComparison.OrdinalIgnoreCase);
	}

	// The option a model thinks at when nobody has chosen one: the endpoint's stated default when it is one
	// of the options, else the option that names the middle (medium, med, mid, middle and the like), else
	// the middle option by position. Off for a model with no options.
	public static string DefaultOf(IReadOnlyList<string> options, string? stated)
	{
		string? middle = null;
		foreach (string option in options)
		{
			if (middle == null && NamesMiddle(option))
				middle = option;
		}

		string result;
		if (options.Count == 0)
			result = Off;
		else if (!string.IsNullOrEmpty(stated) && Contains(options, stated))
			result = Spelled(options, stated);
		else if (middle != null)
			result = middle;
		else
			result = options[options.Count / 2];
		return result;
	}

	// True for an option whose word says it is the middle setting: medium, med, mid, middle, midrange,
	// moderate, normal, standard or balanced.
	private static bool NamesMiddle(string option)
	{
		string word = option.Trim().ToLowerInvariant();
		return word.StartsWith("med", StringComparison.Ordinal) || word.StartsWith("mid", StringComparison.Ordinal)
			|| word == "moderate" || word == "normal" || word == "standard" || word == "balanced";
	}

	// The setting in force: none when chosen and allowed, the chosen option as the model spells it, or the
	// default for a blank setting or one the model does not offer (an older setting, or another model's word).
	public static string InForce(string? setting, IReadOnlyList<string> options, string defaultOption, bool thinkingRequired)
	{
		string result;
		if (string.Equals(setting, Off, StringComparison.OrdinalIgnoreCase) && (!thinkingRequired || options.Count == 0))
			result = Off;
		else if (!string.IsNullOrEmpty(setting) && Contains(options, setting))
			result = Spelled(options, setting);
		else
			result = defaultOption;
		return result;
	}

	// Makes a model's effort concrete as it is loaded: its options (the endpoint's, else its protocol's
	// words) and its setting settled to none or one of them. Every reader downstream then sends the
	// setting as it stands.
	public static void Settle(ModelConfig config, List<string> protocolWords)
	{
		config.EffortsListed      = config.EffortsListed || config.ReasoningEfforts != null;
		config.ReasoningEfforts ??= protocolWords;
		config.ReasoningEffort    = InForce(config.ReasoningEffort, config.ReasoningEfforts, DefaultOf(config.ReasoningEfforts, config.DefaultEffort), config.ThinkingRequired);
	}

	// What /config's toggle and /effort offer: none first (unless the model's thinking cannot be turned
	// off), then the model's options, lowest first.
	public static List<string> Choices(IReadOnlyList<string> options, bool thinkingRequired)
	{
		List<string> choices = new List<string>();
		if (!thinkingRequired || options.Count == 0)
			choices.Add(Off);
		choices.AddRange(options);
		return choices;
	}

	// The effort word to send: the setting itself, or null when thinking is off (the field is then omitted
	// or stated off, as each protocol does).
	public static string? Sent(string? setting)
	{
		return IsOff(setting) ? null : setting;
	}

	// " (high)" style suffix appended to the model name in the status display; empty when thinking is off.
	public static string DisplaySuffix(string? setting)
	{
		return IsOff(setting) ? string.Empty : $" ({setting})";
	}

	// The Anthropic thinking budget in tokens for one of its budget names, clamped to leave room for the
	// visible answer within the turn's max_tokens. Returns 0 when thinking is off or there is no room.
	// Anthropic requires a budget of at least 1024 and strictly less than max_tokens. A word that is not a
	// budget name (a model's own effort word, met before its catalog was read) gets medium's budget.
	public static int AnthropicBudget(string? setting, int maxTokens)
	{
		int target = 0;
		if (!IsOff(setting))
		{
			target = 8192;
			foreach ((string name, int tokens) in kAnthropicBudgets)
			{
				if (string.Equals(name, setting, StringComparison.OrdinalIgnoreCase))
					target = tokens;
			}
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
			budget      = target > ceiling ? ceiling : target;
			if (budget < 1024)
				budget = 1024;
		}
		return budget;
	}

	private static bool Contains(IReadOnlyList<string> options, string word)
	{
		bool found = false;
		foreach (string option in options)
		{
			if (string.Equals(option, word, StringComparison.OrdinalIgnoreCase))
				found = true;
		}
		return found;
	}

	// The option as the model spells it, for a word matched without regard to case.
	private static string Spelled(IReadOnlyList<string> options, string word)
	{
		string result = word;
		foreach (string option in options)
		{
			if (string.Equals(option, word, StringComparison.OrdinalIgnoreCase))
				result = option;
		}
		return result;
	}
}