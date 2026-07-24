using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// One provider's answer to a search. Ok=false means Error explains why; Cost is the provider's
// documented search fee for the call (model tokens are billed by the provider on top and are not
// counted here — no response reports them in a form we could price).
public class WebSearchAnswer
{
	public bool Ok { get; }
	public string Text { get; }
	public string Error { get; }
	public decimal Cost { get; }

	// HTTP status of a failed call, 0 when the failure was not an HTTP response. Carried so the
	// caller can recognise a 402 (credits exhausted) and tell the human, which is the one search
	// failure no amount of falling back can actually fix.
	public int HttpStatus { get; }

	private WebSearchAnswer(bool ok, string text, string error, decimal cost, int httpStatus)
	{
		Ok = ok;
		Text = text;
		Error = error;
		Cost = cost;
		HttpStatus = httpStatus;
	}

	public static WebSearchAnswer Success(string text, decimal cost)
	{
		return new WebSearchAnswer(true, text, string.Empty, cost, 0);
	}

	// A failed call still carries the cost when the provider billed before failing; callers that
	// fall through to another provider roll this up either way.
	public static WebSearchAnswer Failure(string error, decimal cost)
	{
		return new WebSearchAnswer(false, string.Empty, error, cost, 0);
	}

	public static WebSearchAnswer FailureHttp(string error, decimal cost, int httpStatus)
	{
		return new WebSearchAnswer(false, string.Empty, error, cost, httpStatus);
	}
}

// A web-search backend. Each provider owns its own wire format end to end — request body,
// authentication, and response shape — and talks to its API directly rather than borrowing the
// conversation protocol classes: these are one-shot search calls whose server-side search tools
// have nothing to do with the agent's message chain, and threading them through ProtocolAnthropic
// or ProtocolResponses would tangle two unrelated concerns.
public abstract class WebSearchProvider
{
	// Stable id persisted in settings (openrouter, perplexity, xai, anthropic, openai, gemini).
	public abstract string Id { get; }

	public abstract string DisplayName { get; }

	// Host whose configured endpoint supplies this provider's API key. One key, entered once,
	// serves both the models on that endpoint and this search provider.
	public abstract string Domain { get; }

	// Documented price in USD per 1000 searches. Drives cheapest-first selection; approximate by
	// nature (providers meter searches, sources, or requests differently), so it ranks rather
	// than bills.
	public abstract decimal PricePerThousand { get; }

	// Search model used when the settings entry does not override it.
	public abstract string DefaultModel { get; }

	// systemPrompt is the WebSearch role's instruction, so the role stays the one place search
	// behavior is customized no matter which provider serves the call. Each provider passes it in
	// whatever field its API calls a system instruction.
	public abstract Task<WebSearchAnswer> SearchAsync(string query, string goal, string systemPrompt, string apiKey, string model, int maxOutputTokens, CancellationToken ct);

	// The system-message entry for the OpenAI-shaped chat APIs, omitted when the role has none.
	protected static void PrependSystem(JsonArray messages, string systemPrompt)
	{
		if (!string.IsNullOrWhiteSpace(systemPrompt))
			messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
	}

	// Per-call fee derived from the published per-1000 price.
	protected decimal SearchFee => PricePerThousand / 1000m;

	// The single prompt every provider sends. The query drives retrieval and the goal steers what
	// comes back, matching how the caller is told to fill the two arguments.
	protected static string BuildPrompt(string query, string goal)
	{
		return $"Search the web for: {query}\n\nWhat the caller needs from the results:\n{goal}\n\nAnswer only with what the goal asks for, drawn from current web sources, and list the URLs you used.";
	}

	// POSTs a JSON body and parses the response. Never throws: transport faults, non-success
	// statuses, and unparseable bodies all come back as an error string, because one provider
	// failing must let the caller fall through to the next rather than kill the tool call.
	protected static async Task<(JsonNode? Root, string Error, int Status)> PostJsonAsync(
		string url,
		JsonObject body,
		Dictionary<string, string> headers,
		CancellationToken ct)
	{
		JsonNode? root = null;
		string error = string.Empty;
		int status = 0;
		try
		{
			using HttpClient http = new HttpClient();
			http.Timeout = TimeSpan.FromSeconds(180);

			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
			request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
			foreach ((string name, string value) in headers)
			{
				if (!string.IsNullOrEmpty(value))
					request.Headers.TryAddWithoutValidation(name, value);
			}

			using HttpResponseMessage response = await http.SendAsync(request, ct);
			string text = await response.Content.ReadAsStringAsync(ct);
			status = (int)response.StatusCode;
			if (!response.IsSuccessStatusCode)
				error = $"HTTP {status}: {Clip(text, 400)}";
			else
				root = JsonNode.Parse(text);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			error = ex.Message;
		}
		return (root, error, status);
	}

	// Type-checked child access: indexing a non-object node throws, and provider responses grow
	// and change shape without notice.
	protected static JsonNode? Child(JsonNode? node, string key)
	{
		if (node is JsonObject obj && obj.TryGetPropertyValue(key, out JsonNode? value))
			return value;
		return null;
	}

	protected static JsonArray? Array(JsonNode? node)
	{
		return node as JsonArray;
	}

	protected static string Text(JsonNode? node)
	{
		if (node == null)
			return string.Empty;
		try
		{
			return node.GetValue<string>() ?? string.Empty;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	// The per-call output ceiling. The caller's tool budget governs, with a floor so a tight
	// budget cannot truncate a search answer into uselessness before it is even measured.
	protected static int OutputCap(int maxOutputTokens)
	{
		return maxOutputTokens > 1024 ? maxOutputTokens : 4096;
	}

	// Extracts the assistant text (and any inline url_citation annotations) from an OpenAI-shaped
	// chat completion — the response format OpenRouter, Perplexity, and xAI all answer in.
	protected static string ChatCompletionText(JsonNode? root, out List<string> urls)
	{
		urls = new List<string>();

		JsonArray? choices = Array(Child(root, "choices"));
		if (choices == null || choices.Count == 0)
			return string.Empty;

		JsonNode? message = Child(choices[0], "message");
		string answer = Text(Child(message, "content")).Trim();

		JsonArray? annotations = Array(Child(message, "annotations"));
		if (annotations != null)
		{
			foreach (JsonNode? annotation in annotations)
			{
				JsonNode? citation = Child(annotation, "url_citation");
				AddUrl(urls, Text(Child(citation, "url")));
			}
		}

		return answer;
	}

	protected static void AddUrl(List<string> urls, string url)
	{
		if (url.Length > 0)
			urls.Add(url);
	}

	// Appends a deduplicated source list so every provider's answer carries its citations, even
	// the ones that return them beside the text instead of inside it.
	protected static string AppendSources(string answer, List<string> urls)
	{
		if (urls.Count == 0)
			return answer;

		StringBuilder sb = new StringBuilder(answer);
		sb.Append("\n\nSources:\n");
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string url in urls)
		{
			if (url.Length > 0 && seen.Add(url))
				sb.Append("- ").Append(url).Append('\n');
		}
		return sb.ToString();
	}

	private static string Clip(string text, int max)
	{
		if (text.Length <= max)
			return text;
		return text.Substring(0, max) + "…";
	}
}

// Every provider Beast knows how to search with, and the rules for turning settings into a live,
// ordered, key-resolved list.
public static class WebSearchRegistry
{
	// Constructed once: providers are stateless, so one instance each is enough.
	private static readonly WebSearchProvider[] kAll =
	{
		new WebSearchOpenRouter(),
		new WebSearchPerplexity(),
		new WebSearchAnthropic(),
		new WebSearchOpenAi(),
		new WebSearchXai(),
		new WebSearchGemini()
	};

	public static WebSearchProvider[] All => kAll;

	public static WebSearchProvider? Find(string id)
	{
		foreach (WebSearchProvider provider in kAll)
		{
			if (string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase))
				return provider;
		}
		return null;
	}

	// The API key for a provider, taken from whichever configured endpoint shares its domain —
	// auto endpoints first, then manual providers, then the legacy web-search block. Empty when
	// no endpoint carries that host, which is what disables the provider for the run.
	public static string ResolveApiKey(BeastSettings settings, WebSearchProvider provider)
	{
		foreach (AutoProviderConfig auto in settings.Auto)
		{
			if (HostMatches(auto.BaseUrl, provider.Domain) && !string.IsNullOrEmpty(auto.ApiKey))
				return auto.ApiKey;
		}
		foreach (ProviderConfig manual in settings.Providers)
		{
			if (HostMatches(manual.BaseUrl, provider.Domain) && !string.IsNullOrEmpty(manual.ApiKey))
				return manual.ApiKey;
		}

		// The pre-providers settings block still holds a usable OpenRouter key for upgraders.
		OpenrouterSearchConfig? legacy = settings.WebSearch?.Openrouter;
		if (legacy != null && string.Equals(provider.Id, "openrouter", StringComparison.OrdinalIgnoreCase)
			&& !string.IsNullOrEmpty(legacy.ApiKey) && !legacy.ApiKey.StartsWith("YOUR_", StringComparison.Ordinal))
			return legacy.ApiKey;

		return string.Empty;
	}

	// The providers that can actually run this turn — configured, enabled, and holding a resolved
	// key — cheapest first, so the caller simply takes the head of the list.
	public static List<(WebSearchProvider Provider, string ApiKey, string Model)> ResolveUsable(BeastSettings settings)
	{
		List<(WebSearchProvider Provider, string ApiKey, string Model)> usable = new List<(WebSearchProvider, string, string)>();

		foreach (WebSearchProviderConfig entry in EffectiveEntries(settings))
		{
			if (!entry.Enabled)
				continue;

			WebSearchProvider? provider = Find(entry.Provider);
			if (provider == null)
				continue;

			string apiKey = ResolveApiKey(settings, provider);
			if (apiKey.Length == 0)
				continue;

			string model = string.IsNullOrEmpty(entry.Model) ? provider.DefaultModel : entry.Model;
			usable.Add((provider, apiKey, model));
		}

		usable.Sort((a, b) => a.Provider.PricePerThousand.CompareTo(b.Provider.PricePerThousand));
		return usable;
	}

	// The configured entries, with the legacy openrouter block folded in as an implicit OpenRouter
	// entry when the new list does not mention it — so an existing settings file keeps searching
	// without an edit.
	public static List<WebSearchProviderConfig> EffectiveEntries(BeastSettings settings)
	{
		List<WebSearchProviderConfig> entries = new List<WebSearchProviderConfig>();
		bool hasOpenRouter = false;
		if (settings.WebSearch != null)
		{
			foreach (WebSearchProviderConfig entry in settings.WebSearch.Providers)
			{
				entries.Add(entry);
				if (string.Equals(entry.Provider, "openrouter", StringComparison.OrdinalIgnoreCase))
					hasOpenRouter = true;
			}
		}

		OpenrouterSearchConfig? legacy = settings.WebSearch?.Openrouter;
		if (!hasOpenRouter && legacy != null && legacy.Enabled)
			entries.Add(new WebSearchProviderConfig { Provider = "openrouter", Enabled = true, Model = legacy.Model });

		return entries;
	}

	private static bool HostMatches(string baseUrl, string domain)
	{
		if (string.IsNullOrEmpty(baseUrl))
			return false;
		if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out Uri? uri))
			return baseUrl.Contains(domain, StringComparison.OrdinalIgnoreCase);
		return uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
			|| uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
	}
}
