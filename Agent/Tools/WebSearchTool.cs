using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Backs the internet_search tool. Holds the providers that resolved to a usable key this run,
// cheapest first, and spends the caller's money on the least expensive one that works: a provider
// that fails outright (bad key, outage, model retired) hands off to the next rather than failing
// the whole call, since the alternative is an agent stuck without search over one bad endpoint.
// Each provider owns its own wire format; this only chooses between them and rolls up the fee.
public class WebSearchTool
{
	private readonly List<(WebSearchProvider Provider, string ApiKey, string Model)> _providers;

	// The WebSearch role's system prompt, handed to whichever provider serves the call, so search
	// behavior stays customizable in roles.json regardless of which backend answers.
	private readonly string _systemPrompt;

	// Providers already alerted about exhausted credits, so the banner is raised once rather than
	// on every search for the rest of the run.
	private readonly HashSet<string> _creditAlerted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public WebSearchTool(List<(WebSearchProvider Provider, string ApiKey, string Model)> providers, string systemPrompt)
	{
		_providers = providers;
		_systemPrompt = systemPrompt;
	}

	public async Task<ToolResult> SearchAsync(
		string toolCallId,
		string query,
		string goal,
		Session parent,
		ITransportServer transport,
		string sessionId,
		int maxOutputTokens,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(query))
			return new ToolResult(toolCallId, string.Empty, "Error: Search query cannot be empty.", 1, 0);
		if (string.IsNullOrWhiteSpace(goal))
			return new ToolResult(toolCallId, string.Empty, "Error: Search goal cannot be empty.", 1, 0);

		StringBuilder failures = new StringBuilder();
		foreach ((WebSearchProvider provider, string apiKey, string model) in _providers)
		{
			WebSearchAnswer answer;
			try
			{
				answer = await provider.SearchAsync(query, goal, _systemPrompt, apiKey, model, maxOutputTokens, ct);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				// A provider that throws is a failed provider, not a failed tool call.
				Console.Error.WriteLine($"[WebSearchTool] {provider.Id} threw {ex.GetType().Name}: {ex}");
				answer = WebSearchAnswer.Failure($"{provider.DisplayName}: {ex.Message}", 0m);
			}

			// The fee is spent whether or not the answer came back usable.
			if (answer.Cost > 0m)
				parent.RecordCost(answer.Cost);

			if (answer.Ok)
			{
				string text = $"{answer.Text}\n\n[searched via {provider.DisplayName}]";
				return new ToolResult(toolCallId, text, string.Empty, 0, ToolDispatch.EstimateTokens(text));
			}

			// Credits exhausted is the one search failure no fallback can fix: the next provider
			// keeps the work moving, but a human has to top the account up, so say so loudly the
			// same way a model-side 402 does. Alerted once per provider per tool instance so a
			// busy session does not paper itself with banners.
			if (answer.HttpStatus == 402 && _creditAlerted.Add(provider.Id))
			{
				transport.Alert(sessionId,
					$"Web search provider '{provider.DisplayName}' is out of credits (HTTP 402). "
					+ "Searching continues on the next enabled provider — which may cost more per search — until a human adds credits.");
			}

			if (failures.Length > 0)
				failures.Append("; ");
			failures.Append(answer.Error);
		}

		string detail = failures.Length > 0 ? failures.ToString() : "no web search provider is configured";
		return new ToolResult(toolCallId, string.Empty, $"Error: web search failed for \"{query}\": {detail}", 1, 0);
	}
}
