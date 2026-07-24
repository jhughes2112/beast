using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// OpenRouter's web plugin: any chat model gains retrieval, billed per result. The cheapest of the
// integrated options, and provider-neutral on the model side.
public class WebSearchOpenRouter : WebSearchProvider
{
	public override string Id => "openrouter";
	public override string DisplayName => "OpenRouter";
	public override string Domain => "openrouter.ai";
	public override decimal PricePerThousand => 4m;
	// The free-model router: always resolves to SOME zero-cost model, so the default never rots
	// when an individual free model is renamed or retired. Only the plugin's results are billed.
	public override string DefaultModel => "openrouter/free";

	public override async Task<WebSearchAnswer> SearchAsync(string query, string goal, string systemPrompt, string apiKey, string model, int maxOutputTokens, CancellationToken ct)
	{
		JsonArray messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = BuildPrompt(query, goal) });
		PrependSystem(messages, systemPrompt);
		JsonObject body = new JsonObject
		{
			["model"] = model,
			["plugins"] = new JsonArray(new JsonObject { ["id"] = "web" }),
			["max_tokens"] = OutputCap(maxOutputTokens),
			["messages"] = messages
		};
		Dictionary<string, string> headers = new Dictionary<string, string> { { "Authorization", $"Bearer {apiKey}" } };

		(JsonNode? root, string error, int status) = await PostJsonAsync("https://openrouter.ai/api/v1/chat/completions", body, headers, ct);
		if (error.Length > 0)
			return WebSearchAnswer.FailureHttp($"OpenRouter search failed: {error}", 0m, status);

		string answer = ChatCompletionText(root, out List<string> urls);
		if (answer.Length == 0)
			return WebSearchAnswer.Failure("OpenRouter returned no search answer.", SearchFee);
		return WebSearchAnswer.Success(AppendSources(answer, urls), SearchFee);
	}
}

// Perplexity Sonar: retrieval is part of every completion, no tool wiring, citations always
// returned. Purpose-built for exactly the one-call search-and-digest shape this tool wants.
public class WebSearchPerplexity : WebSearchProvider
{
	public override string Id => "perplexity";
	public override string DisplayName => "Perplexity";
	public override string Domain => "api.perplexity.ai";
	public override decimal PricePerThousand => 5m;
	public override string DefaultModel => "sonar";

	public override async Task<WebSearchAnswer> SearchAsync(string query, string goal, string systemPrompt, string apiKey, string model, int maxOutputTokens, CancellationToken ct)
	{
		JsonArray messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = BuildPrompt(query, goal) });
		PrependSystem(messages, systemPrompt);
		JsonObject body = new JsonObject
		{
			["model"] = model,
			["max_tokens"] = OutputCap(maxOutputTokens),
			["messages"] = messages
		};
		Dictionary<string, string> headers = new Dictionary<string, string> { { "Authorization", $"Bearer {apiKey}" } };

		(JsonNode? root, string error, int status) = await PostJsonAsync("https://api.perplexity.ai/chat/completions", body, headers, ct);
		if (error.Length > 0)
			return WebSearchAnswer.FailureHttp($"Perplexity search failed: {error}", 0m, status);

		string answer = ChatCompletionText(root, out List<string> urls);

		// Sonar reports its sources beside the message rather than inside it, under either key
		// depending on API vintage.
		JsonArray? results = Array(Child(root, "search_results"));
		if (results != null)
		{
			foreach (JsonNode? result in results)
				AddUrl(urls, Text(Child(result, "url")));
		}
		JsonArray? citations = Array(Child(root, "citations"));
		if (citations != null)
		{
			foreach (JsonNode? citation in citations)
				AddUrl(urls, Text(citation));
		}

		if (answer.Length == 0)
			return WebSearchAnswer.Failure("Perplexity returned no search answer.", SearchFee);
		return WebSearchAnswer.Success(AppendSources(answer, urls), SearchFee);
	}
}

// Anthropic's server-side web_search tool on the Messages API: Claude runs the searches itself
// mid-turn and answers with citations.
public class WebSearchAnthropic : WebSearchProvider
{
	public override string Id => "anthropic";
	public override string DisplayName => "Anthropic";
	public override string Domain => "api.anthropic.com";
	public override decimal PricePerThousand => 10m;
	public override string DefaultModel => "claude-haiku-4-5-20251001";

	public override async Task<WebSearchAnswer> SearchAsync(string query, string goal, string systemPrompt, string apiKey, string model, int maxOutputTokens, CancellationToken ct)
	{
		JsonObject body = new JsonObject
		{
			["model"] = model,
			["max_tokens"] = OutputCap(maxOutputTokens),
			["tools"] = new JsonArray(new JsonObject
			{
				["type"] = "web_search_20250305",
				["name"] = "web_search",
				["max_uses"] = 5
			}),
			["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = BuildPrompt(query, goal) })
		};
		// Anthropic takes the system instruction as a top-level field, not a message.
		if (!string.IsNullOrWhiteSpace(systemPrompt))
			body["system"] = systemPrompt;
		Dictionary<string, string> headers = new Dictionary<string, string>
		{
			{ "x-api-key", apiKey },
			{ "anthropic-version", "2023-06-01" }
		};

		(JsonNode? root, string error, int status) = await PostJsonAsync("https://api.anthropic.com/v1/messages", body, headers, ct);
		if (error.Length > 0)
			return WebSearchAnswer.FailureHttp($"Anthropic search failed: {error}", 0m, status);

		// The reply interleaves text blocks with the server tool's own blocks; the text blocks are
		// the answer, and web_search_tool_result blocks carry the pages it consulted.
		StringBuilder sb = new StringBuilder();
		List<string> urls = new List<string>();
		JsonArray? content = Array(Child(root, "content"));
		if (content != null)
		{
			foreach (JsonNode? block in content)
			{
				string type = Text(Child(block, "type"));
				if (type == "text")
				{
					string text = Text(Child(block, "text"));
					if (text.Length > 0)
						sb.Append(text);
				}
				else if (type == "web_search_tool_result")
				{
					JsonArray? results = Array(Child(block, "content"));
					if (results != null)
					{
						foreach (JsonNode? result in results)
							AddUrl(urls, Text(Child(result, "url")));
					}
				}
			}
		}

		string answer = sb.ToString().Trim();
		if (answer.Length == 0)
			return WebSearchAnswer.Failure("Anthropic returned no search answer.", SearchFee);
		return WebSearchAnswer.Success(AppendSources(answer, urls), SearchFee);
	}
}

// OpenAI's built-in web_search tool on the Responses API.
public class WebSearchOpenAi : WebSearchProvider
{
	public override string Id => "openai";
	public override string DisplayName => "OpenAI";
	public override string Domain => "api.openai.com";
	public override decimal PricePerThousand => 10m;
	public override string DefaultModel => "gpt-5-mini";

	public override async Task<WebSearchAnswer> SearchAsync(string query, string goal, string systemPrompt, string apiKey, string model, int maxOutputTokens, CancellationToken ct)
	{
		JsonObject body = new JsonObject
		{
			["model"] = model,
			["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search" }),
			["max_output_tokens"] = OutputCap(maxOutputTokens),
			["input"] = BuildPrompt(query, goal)
		};
		// The Responses API calls the system instruction "instructions".
		if (!string.IsNullOrWhiteSpace(systemPrompt))
			body["instructions"] = systemPrompt;
		Dictionary<string, string> headers = new Dictionary<string, string> { { "Authorization", $"Bearer {apiKey}" } };

		(JsonNode? root, string error, int status) = await PostJsonAsync("https://api.openai.com/v1/responses", body, headers, ct);
		if (error.Length > 0)
			return WebSearchAnswer.FailureHttp($"OpenAI search failed: {error}", 0m, status);

		// The output array mixes the search calls with the message; only output_text parts inside
		// message items are the answer, and their annotations carry the cited URLs.
		StringBuilder sb = new StringBuilder();
		List<string> urls = new List<string>();
		JsonArray? output = Array(Child(root, "output"));
		if (output != null)
		{
			foreach (JsonNode? item in output)
			{
				if (Text(Child(item, "type")) != "message")
					continue;

				JsonArray? parts = Array(Child(item, "content"));
				if (parts == null)
					continue;

				foreach (JsonNode? part in parts)
				{
					if (Text(Child(part, "type")) != "output_text")
						continue;

					string text = Text(Child(part, "text"));
					if (text.Length > 0)
						sb.Append(text);

					JsonArray? annotations = Array(Child(part, "annotations"));
					if (annotations != null)
					{
						foreach (JsonNode? annotation in annotations)
							AddUrl(urls, Text(Child(annotation, "url")));
					}
				}
			}
		}

		string answer = sb.ToString().Trim();
		if (answer.Length == 0)
			return WebSearchAnswer.Failure("OpenAI returned no search answer.", SearchFee);
		return WebSearchAnswer.Success(AppendSources(answer, urls), SearchFee);
	}
}

// xAI Live Search: Grok retrieves inside the completion, steered by search_parameters. Priced per
// SOURCE rather than per search, so a broad query costs several times the headline rate — which is
// why it ranks well below the others.
public class WebSearchXai : WebSearchProvider
{
	public override string Id => "xai";
	public override string DisplayName => "xAI";
	public override string Domain => "api.x.ai";
	public override decimal PricePerThousand => 25m;
	public override string DefaultModel => "grok-4-fast";

	public override async Task<WebSearchAnswer> SearchAsync(string query, string goal, string systemPrompt, string apiKey, string model, int maxOutputTokens, CancellationToken ct)
	{
		JsonArray messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = BuildPrompt(query, goal) });
		PrependSystem(messages, systemPrompt);
		JsonObject body = new JsonObject
		{
			["model"] = model,
			["max_tokens"] = OutputCap(maxOutputTokens),
			["search_parameters"] = new JsonObject
			{
				["mode"] = "on",
				["return_citations"] = true
			},
			["messages"] = messages
		};
		Dictionary<string, string> headers = new Dictionary<string, string> { { "Authorization", $"Bearer {apiKey}" } };

		(JsonNode? root, string error, int status) = await PostJsonAsync("https://api.x.ai/v1/chat/completions", body, headers, ct);
		if (error.Length > 0)
			return WebSearchAnswer.FailureHttp($"xAI search failed: {error}", 0m, status);

		string answer = ChatCompletionText(root, out List<string> urls);
		JsonArray? citations = Array(Child(root, "citations"));
		if (citations != null)
		{
			foreach (JsonNode? citation in citations)
				AddUrl(urls, Text(citation));
		}

		if (answer.Length == 0)
			return WebSearchAnswer.Failure("xAI returned no search answer.", SearchFee);
		return WebSearchAnswer.Success(AppendSources(answer, urls), SearchFee);
	}
}

// Gemini grounding with Google Search — real Google results, and the priciest of the integrated
// options. Uses the native generateContent endpoint: the OpenAI-compatible shim does not carry the
// google_search tool.
public class WebSearchGemini : WebSearchProvider
{
	public override string Id => "gemini";
	public override string DisplayName => "Google Gemini";
	public override string Domain => "generativelanguage.googleapis.com";
	public override decimal PricePerThousand => 35m;
	public override string DefaultModel => "gemini-2.5-flash";

	public override async Task<WebSearchAnswer> SearchAsync(string query, string goal, string systemPrompt, string apiKey, string model, int maxOutputTokens, CancellationToken ct)
	{
		JsonObject body = new JsonObject
		{
			["contents"] = new JsonArray(new JsonObject
			{
				["role"] = "user",
				["parts"] = new JsonArray(new JsonObject { ["text"] = BuildPrompt(query, goal) })
			}),
			["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() }),
			["generationConfig"] = new JsonObject { ["maxOutputTokens"] = OutputCap(maxOutputTokens) }
		};
		// Gemini takes the system instruction as its own content block.
		if (!string.IsNullOrWhiteSpace(systemPrompt))
		{
			body["systemInstruction"] = new JsonObject
			{
				["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt })
			};
		}
		Dictionary<string, string> headers = new Dictionary<string, string> { { "x-goog-api-key", apiKey } };

		string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
		(JsonNode? root, string error, int status) = await PostJsonAsync(url, body, headers, ct);
		if (error.Length > 0)
			return WebSearchAnswer.FailureHttp($"Gemini search failed: {error}", 0m, status);

		StringBuilder sb = new StringBuilder();
		List<string> urls = new List<string>();
		JsonArray? candidates = Array(Child(root, "candidates"));
		if (candidates != null && candidates.Count > 0)
		{
			JsonNode? candidate = candidates[0];
			JsonArray? parts = Array(Child(Child(candidate, "content"), "parts"));
			if (parts != null)
			{
				foreach (JsonNode? part in parts)
				{
					string text = Text(Child(part, "text"));
					if (text.Length > 0)
						sb.Append(text);
				}
			}

			// Grounding chunks name the pages the answer was drawn from.
			JsonArray? chunks = Array(Child(Child(candidate, "groundingMetadata"), "groundingChunks"));
			if (chunks != null)
			{
				foreach (JsonNode? chunk in chunks)
					AddUrl(urls, Text(Child(Child(chunk, "web"), "uri")));
			}
		}

		string answer = sb.ToString().Trim();
		if (answer.Length == 0)
			return WebSearchAnswer.Failure("Gemini returned no search answer.", SearchFee);
		return WebSearchAnswer.Success(AppendSources(answer, urls), SearchFee);
	}
}
