using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// One model as reported by an endpoint's catalog. Zero/negative/null values mean the endpoint did
// not report that field — the /config picker asks the user for those at enable time, and the
// loader falls back to conservative defaults when nothing was ever supplied.
public class DiscoveredModel
{
	public string Id { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public int ContextWindow { get; set; } = 0;
	public int MaxOutputTokens { get; set; } = 0;
	// Per-million-token USD; -1 = unknown.
	public decimal CostInput { get; set; } = -1m;
	public decimal CostOutput { get; set; } = -1m;
	public decimal CostCacheRead { get; set; } = -1m;
	// Null = unknown; otherwise the declared input modalities ("text", "image", "audio").
	public List<string>? Modalities { get; set; } = null;

	// Unix epoch seconds the model was released, 0 = unknown. Drives newest-first sorting in the
	// /config picker — the latest models are almost always the ones being reached for.
	public long Created { get; set; } = 0;
}

// Fetches and normalizes a provider's model catalog. One lenient parser covers every
// OpenAI-compatible /models shape (OpenAI, OpenRouter, vLLM, LM Studio, Ollama, llama-server) by
// reading whichever metadata fields happen to exist; Anthropic gets its own headers and a
// versioned-in-code capability default since its API reports names only. llama-server's /props is
// consulted when /models left the window unknown, because it reports the REAL -c context size.
public static class ModelCatalog
{
	private static readonly TimeSpan kFetchTimeout = TimeSpan.FromSeconds(10);

	// Suffixes a configured request endpoint may carry; stripped to find the API root.
	private static readonly string[] kEndpointSuffixes = { "/chat/completions", "/completions", "/messages", "/responses" };

	// Returns the discovered catalog, or an empty list plus an error description on failure.
	public static async Task<(List<DiscoveredModel> Models, string Error)> FetchAsync(string baseUrl, string apiKey, CancellationToken ct)
	{
		List<DiscoveredModel> models = new List<DiscoveredModel>();
		string error = string.Empty;

		string apiRoot = ApiRoot(baseUrl);
		bool anthropic = apiRoot.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);

		using HttpClient http = new HttpClient();
		http.Timeout = kFetchTimeout;

		try
		{
			// Header setup lives inside the try: a pasted key with stray control characters
			// throws from the header validation, and that must surface as a catalog error, not
			// escape to the caller.
			if (anthropic)
			{
				if (!string.IsNullOrEmpty(apiKey))
					http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
				http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
			}
			else if (!string.IsNullOrEmpty(apiKey))
			{
				http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
			}

			string body = await http.GetStringAsync($"{apiRoot}/models", ct);
			JsonNode? root = JsonNode.Parse(body);
			JsonArray? data = AsArrayOrNull(root?["data"]) ?? AsArrayOrNull(root?["models"]) ?? AsArrayOrNull(root);
			if (data == null)
			{
				error = $"No model list in response from {apiRoot}/models";
			}
			else
			{
				foreach (JsonNode? entry in data)
				{
					if (entry == null)
						continue;

					// One malformed or shape-shifted entry must never kill the whole catalog —
					// providers add fields (and change their types) without notice.
					try
					{
						DiscoveredModel model = ParseEntry(entry, anthropic);
						if (model.Id.Length > 0)
							models.Add(model);
					}
					catch (Exception ex)
					{
						Console.Error.WriteLine($"[ModelCatalog] Skipping unparseable catalog entry from {apiRoot}: {ex.Message}");
					}
				}
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			error = $"Catalog fetch failed for {apiRoot}/models: {ex.Message}";
		}

		// xAI's inference API is OpenAI-compatible but its /models is names-only; the richer
		// /language-models endpoint reports modalities and full pricing (in cents per 100M
		// tokens). It does NOT report a context window, so that stays for the user to answer.
		if (error.Length == 0 && apiRoot.Contains("api.x.ai", StringComparison.OrdinalIgnoreCase))
			await ApplyXaiAsync(http, apiRoot, models, ct);

		// Local-server enrichment, tried only when the catalog left windows unknown so cloud
		// endpoints (which always report them) never eat the extra round-trips. Each probe hits a
		// side endpoint that identifies its server family by simply existing: llama-server /props
		// (real -c context and modalities), LM Studio /api/v0/models (loaded context and vision),
		// Ollama /api/show (capabilities; window only when the Modelfile pins num_ctx — the
		// trained context_length it also reports is NOT the served window and trusting it would
		// manufacture overflows, so an unpinned window stays unknown for the user to answer).
		if (error.Length == 0 && AnyWindowUnknown(models))
		{
			string serverRoot = ServerRoot(baseUrl);
			await ApplyServerPropsAsync(http, serverRoot, models, ct);
			if (AnyWindowUnknown(models))
				await ApplyLmStudioAsync(http, serverRoot, models, ct);
			if (AnyWindowUnknown(models))
				await ApplyOllamaAsync(http, serverRoot, models, ct);
		}

		return (models, error);
	}

	// Normalizes whatever the user typed (bare host, api root, or full request URL) into the
	// request endpoint the protocol layer should call. Anthropic gets /v1/messages; everything
	// else gets the OpenAI-compatible /chat/completions under its api root.
	public static string NormalizeRequestEndpoint(string url)
	{
		string trimmed = url.TrimEnd('/');
		foreach (string suffix in kEndpointSuffixes)
		{
			if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				return trimmed;
		}

		if (trimmed.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
			return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"{trimmed}/messages" : $"{trimmed}/v1/messages";

		return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("/api/", StringComparison.OrdinalIgnoreCase)
			? $"{trimmed}/chat/completions"
			: $"{trimmed}/v1/chat/completions";
	}

	// The api root: the configured URL with any request-path suffix stripped ("…/v1" style).
	private static string ApiRoot(string url)
	{
		string trimmed = NormalizeRequestEndpoint(url);
		foreach (string suffix in kEndpointSuffixes)
		{
			if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				return trimmed.Substring(0, trimmed.Length - suffix.Length);
		}
		return trimmed;
	}

	// Scheme + authority only, for server-level endpoints like llama-server's /props.
	private static string ServerRoot(string url)
	{
		if (Uri.TryCreate(NormalizeRequestEndpoint(url), UriKind.Absolute, out Uri? uri))
			return $"{uri.Scheme}://{uri.Authority}";
		return url.TrimEnd('/');
	}

	private static DiscoveredModel ParseEntry(JsonNode entry, bool anthropic)
	{
		DiscoveredModel model = new DiscoveredModel();
		model.Id = entry["id"]?.GetValue<string>() ?? entry["name"]?.GetValue<string>() ?? string.Empty;
		model.Name = entry["display_name"]?.GetValue<string>() ?? entry["name"]?.GetValue<string>() ?? model.Id;

		// Release date: OpenAI-style catalogs carry epoch seconds in "created"; Anthropic uses an
		// RFC3339 "created_at". Unknown stays 0 and sorts to the alphabetical tail.
		model.Created = ReadLong(entry, "created") ?? 0;
		if (model.Created == 0)
		{
			string? createdAt = null;
			try
			{
				createdAt = entry["created_at"]?.GetValue<string>();
			}
			catch (Exception)
			{
			}
			if (!string.IsNullOrEmpty(createdAt) && DateTimeOffset.TryParse(createdAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset parsed))
				model.Created = parsed.ToUnixTimeSeconds();
		}

		// Context window under whichever key this server family uses: OpenRouter context_length,
		// vLLM max_model_len, LM Studio max_context_length, llama-server (router mode) n_ctx,
		// Anthropic max_input_tokens. Zero-valued keys (Anthropic ships them as placeholders)
		// fall through to the next candidate rather than claiming the answer.
		model.ContextWindow = FirstPositive(
			ReadInt(entry, "context_length"),
			ReadInt(entry, "max_model_len"),
			ReadInt(entry, "max_context_length"),
			ReadInt(entry, "n_ctx"),
			ReadInt(entry, "max_input_tokens"));

		model.MaxOutputTokens = FirstPositive(
			ReadInt(entry["top_provider"], "max_completion_tokens"),
			ReadInt(entry, "max_tokens"));

		// OpenRouter pricing is USD per TOKEN as strings; normalize to per-million.
		JsonNode? pricing = entry["pricing"];
		if (pricing != null)
		{
			decimal? prompt = ReadDecimal(pricing, "prompt");
			decimal? completion = ReadDecimal(pricing, "completion");
			decimal? cacheRead = ReadDecimal(pricing, "input_cache_read");
			if (prompt.HasValue)
				model.CostInput = prompt.Value * 1_000_000m;
			if (completion.HasValue)
				model.CostOutput = completion.Value * 1_000_000m;
			if (cacheRead.HasValue)
				model.CostCacheRead = cacheRead.Value * 1_000_000m;
		}

		// Modalities: OpenRouter declares them outright; LM Studio/Ollama expose a capabilities
		// array where "vision" implies image input.
		JsonArray? declared = AsArrayOrNull(ReadNode(entry["architecture"], "input_modalities"));
		if (declared != null)
		{
			List<string> modalities = new List<string>();
			foreach (JsonNode? m in declared)
			{
				string? value = m?.GetValue<string>();
				if (!string.IsNullOrEmpty(value))
					modalities.Add(value);
			}
			if (modalities.Count > 0)
				model.Modalities = modalities;
		}
		else
		{
			JsonNode? capsNode = ReadNode(entry, "capabilities");
			JsonArray? capabilities = AsArrayOrNull(capsNode);
			if (capabilities != null)
			{
				// LM Studio/Ollama style: a flat string array where "vision" implies image input.
				List<string> modalities = new List<string> { "text" };
				foreach (JsonNode? c in capabilities)
				{
					string? value = c?.GetValue<string>();
					if (string.Equals(value, "vision", StringComparison.OrdinalIgnoreCase))
						modalities.Add("image");
					if (string.Equals(value, "audio", StringComparison.OrdinalIgnoreCase))
						modalities.Add("audio");
				}
				model.Modalities = modalities;
			}
			else if (capsNode is JsonObject capsObj)
			{
				// Anthropic style: nested {feature: {supported: bool}} objects. Only the *_input
				// features speak to modalities; claim them only when at least one is present, so
				// some other provider's differently-shaped capabilities object stays "unknown"
				// instead of being misread as text-only.
				bool image = ReadSupported(capsObj, "image_input");
				bool pdf = ReadSupported(capsObj, "pdf_input");
				bool audio = ReadSupported(capsObj, "audio_input");
				if (capsObj.ContainsKey("image_input") || capsObj.ContainsKey("pdf_input") || capsObj.ContainsKey("audio_input"))
				{
					List<string> modalities = new List<string> { "text" };
					if (image)
						modalities.Add("image");
					if (audio)
						modalities.Add("audio");
					if (pdf)
						modalities.Add("file");
					model.Modalities = modalities;
				}
			}
		}

		// Anthropic fallbacks for anything its catalog left unstated — 200k windows hold across
		// the current fleet, and text+image only fills in when the capabilities object was absent.
		if (anthropic)
		{
			if (model.ContextWindow == 0)
				model.ContextWindow = 200000;
			if (model.Modalities == null)
				model.Modalities = new List<string> { "text", "image" };
		}

		return model;
	}

	// True when capabilities[feature].supported == true in the nested Anthropic shape.
	private static bool ReadSupported(JsonObject capabilities, string feature)
	{
		JsonNode? value = ReadNode(ReadNode(capabilities, feature), "supported");
		try
		{
			return value != null && value.GetValue<bool>();
		}
		catch (Exception)
		{
			return false;
		}
	}

	// The first value that is present AND positive — zero-valued placeholder keys must not stop
	// the fallback chain the way a bare null-coalescing chain would.
	private static int FirstPositive(params int?[] candidates)
	{
		foreach (int? candidate in candidates)
		{
			if (candidate.HasValue && candidate.Value > 0)
				return candidate.Value;
		}
		return 0;
	}

	// Folds llama-server /props (n_ctx, modalities) into models the catalog left unknown.
	private static async Task ApplyServerPropsAsync(HttpClient http, string serverRoot, List<DiscoveredModel> models, CancellationToken ct)
	{
		try
		{
			string body = await http.GetStringAsync($"{serverRoot}/props", ct);
			JsonNode? props = JsonNode.Parse(body);
			if (props == null)
				return;

			int nCtx = ReadInt(props, "n_ctx") ?? ReadInt(props["default_generation_settings"], "n_ctx") ?? 0;

			List<string>? modalities = null;
			JsonNode? modNode = props["modalities"];
			if (modNode != null)
			{
				modalities = new List<string> { "text" };
				if (modNode["vision"]?.GetValue<bool>() == true)
					modalities.Add("image");
				if (modNode["audio"]?.GetValue<bool>() == true)
					modalities.Add("audio");
			}

			foreach (DiscoveredModel model in models)
			{
				if (model.ContextWindow == 0 && nCtx > 0)
					model.ContextWindow = nCtx;
				if (model.Modalities == null && modalities != null)
					model.Modalities = modalities;
				if (model.CostInput < 0)
					model.CostInput = 0m;
				if (model.CostOutput < 0)
					model.CostOutput = 0m;
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			// Not llama-server (404, connection refused on /props) — nothing to fold in.
		}
	}

	// Folds xAI's /v1/language-models metadata into the discovered set.
	private static async Task ApplyXaiAsync(HttpClient http, string apiRoot, List<DiscoveredModel> models, CancellationToken ct)
	{
		try
		{
			string body = await http.GetStringAsync($"{apiRoot}/language-models", ct);
			JsonNode? root = JsonNode.Parse(body);
			JsonArray? list = AsArrayOrNull(root?["models"]);
			if (list != null)
				ParseXaiList(list, models);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			// Endpoint unavailable — the models stay on the ask-at-enable path.
		}
	}

	// Pure merge of an xAI language-models list, matched by id or alias. Prices arrive in USD
	// cents per 100 MILLION tokens, so per-Mtok USD = value / 10,000.
	internal static void ParseXaiList(JsonArray list, List<DiscoveredModel> models)
	{
		foreach (JsonNode? entry in list)
		{
			if (entry == null)
				continue;

			// Collect this entry's id plus aliases for matching against discovered ids.
			List<string> names = new List<string>();
			string? id = entry["id"]?.GetValue<string>();
			if (!string.IsNullOrEmpty(id))
				names.Add(id);
			JsonArray? aliases = AsArrayOrNull(entry["aliases"]);
			if (aliases != null)
			{
				foreach (JsonNode? a in aliases)
				{
					string? alias = a?.GetValue<string>();
					if (!string.IsNullOrEmpty(alias))
						names.Add(alias);
				}
			}

			foreach (DiscoveredModel model in models)
			{
				bool match = false;
				foreach (string name in names)
				{
					if (string.Equals(name, model.Id, StringComparison.OrdinalIgnoreCase))
					{
						match = true;
						break;
					}
				}
				if (!match)
					continue;

				if (model.Modalities == null)
				{
					JsonArray? input = AsArrayOrNull(entry["input_modalities"]);
					if (input != null)
					{
						List<string> modalities = new List<string>();
						foreach (JsonNode? m in input)
						{
							string? value = m?.GetValue<string>();
							if (!string.IsNullOrEmpty(value))
								modalities.Add(value);
						}
						if (modalities.Count > 0)
							model.Modalities = modalities;
					}
				}

				if (model.CostInput < 0)
				{
					decimal? prompt = ReadDecimal(entry, "prompt_text_token_price");
					if (prompt.HasValue)
						model.CostInput = prompt.Value / 10_000m;
				}
				if (model.CostOutput < 0)
				{
					decimal? completion = ReadDecimal(entry, "completion_text_token_price");
					if (completion.HasValue)
						model.CostOutput = completion.Value / 10_000m;
				}
				if (model.CostCacheRead < 0)
				{
					decimal? cached = ReadDecimal(entry, "cached_prompt_text_token_price");
					if (cached.HasValue)
						model.CostCacheRead = cached.Value / 10_000m;
				}
			}
		}
	}

	// Folds LM Studio's /api/v0/models (loaded/max context, vision capability) into models the
	// catalog left unknown. The endpoint's existence identifies LM Studio; anything else 404s.
	private static async Task ApplyLmStudioAsync(HttpClient http, string serverRoot, List<DiscoveredModel> models, CancellationToken ct)
	{
		try
		{
			string body = await http.GetStringAsync($"{serverRoot}/api/v0/models", ct);
			JsonNode? root = JsonNode.Parse(body);
			JsonArray? data = AsArrayOrNull(root?["data"]);
			if (data != null)
				ParseLmStudioList(data, models);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			// Not LM Studio — nothing to fold in.
		}
	}

	// Pure merge of an LM Studio /api/v0/models list into the discovered set, matched by id.
	// loaded_context_length is the window the server is actually serving right now and beats the
	// model's max; the "vlm" type and a "vision" capability both mean image input.
	internal static void ParseLmStudioList(JsonArray data, List<DiscoveredModel> models)
	{
		foreach (JsonNode? entry in data)
		{
			if (entry == null)
				continue;
			string id = entry["id"]?.GetValue<string>() ?? string.Empty;
			if (id.Length == 0)
				continue;

			foreach (DiscoveredModel model in models)
			{
				if (!string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase))
					continue;

				if (model.ContextWindow == 0)
					model.ContextWindow = ReadInt(entry, "loaded_context_length") ?? ReadInt(entry, "max_context_length") ?? 0;

				if (model.Modalities == null)
				{
					bool vision = string.Equals(entry["type"]?.GetValue<string>(), "vlm", StringComparison.OrdinalIgnoreCase);
					JsonArray? capabilities = AsArrayOrNull(entry["capabilities"]);
					if (capabilities != null)
					{
						foreach (JsonNode? c in capabilities)
						{
							if (string.Equals(c?.GetValue<string>(), "vision", StringComparison.OrdinalIgnoreCase))
								vision = true;
						}
					}
					model.Modalities = vision ? new List<string> { "text", "image" } : new List<string> { "text" };
				}

				if (model.CostInput < 0)
					model.CostInput = 0m;
				if (model.CostOutput < 0)
					model.CostOutput = 0m;
				break;
			}
		}
	}

	// Caps the per-model /api/show round-trips so a machine hoarding hundreds of Ollama models
	// doesn't turn one catalog fetch into a minute of probing.
	private const int kMaxOllamaShowCalls = 50;

	// Folds Ollama's native metadata into models the catalog left unknown: /api/tags existing
	// identifies Ollama, then one POST /api/show per model reports capabilities and parameters.
	private static async Task ApplyOllamaAsync(HttpClient http, string serverRoot, List<DiscoveredModel> models, CancellationToken ct)
	{
		try
		{
			HttpResponseMessage tags = await http.GetAsync($"{serverRoot}/api/tags", ct);
			if (!tags.IsSuccessStatusCode)
				return;

			int calls = 0;
			foreach (DiscoveredModel model in models)
			{
				if (calls >= kMaxOllamaShowCalls)
					break;
				if (model.ContextWindow > 0 && model.Modalities != null)
					continue;

				calls++;
				JsonObject request = new JsonObject { ["model"] = model.Id };
				using StringContent content = new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
				HttpResponseMessage response = await http.PostAsync($"{serverRoot}/api/show", content, ct);
				if (!response.IsSuccessStatusCode)
					continue;

				string body = await response.Content.ReadAsStringAsync(ct);
				JsonNode? show = JsonNode.Parse(body);
				if (show != null)
					ParseOllamaShow(show, model);
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			// Not Ollama — nothing to fold in.
		}
	}

	// Pure merge of one Ollama /api/show response into a discovered model. Capabilities are
	// authoritative ("vision" means image input). The window is taken ONLY from an explicit
	// num_ctx in the Modelfile parameters: model_info's context_length is the TRAINED maximum,
	// not what the server will actually serve (num_ctx defaults far lower), and reporting it as
	// the window would manufacture context overflows — unknown stays unknown for the user.
	internal static void ParseOllamaShow(JsonNode show, DiscoveredModel model)
	{
		if (model.Modalities == null)
		{
			List<string> modalities = new List<string> { "text" };
			JsonArray? capabilities = AsArrayOrNull(show["capabilities"]);
			if (capabilities != null)
			{
				foreach (JsonNode? c in capabilities)
				{
					string? value = c?.GetValue<string>();
					if (string.Equals(value, "vision", StringComparison.OrdinalIgnoreCase))
						modalities.Add("image");
					if (string.Equals(value, "audio", StringComparison.OrdinalIgnoreCase))
						modalities.Add("audio");
				}
			}
			model.Modalities = modalities;
		}

		if (model.ContextWindow == 0)
		{
			// Modelfile parameters arrive as a flat "name value" text block, one per line.
			string parameters = show["parameters"]?.GetValue<string>() ?? string.Empty;
			foreach (string line in parameters.Split('\n'))
			{
				string trimmed = line.Trim();
				if (trimmed.StartsWith("num_ctx", StringComparison.OrdinalIgnoreCase))
				{
					string value = trimmed.Substring("num_ctx".Length).Trim();
					if (int.TryParse(value, out int numCtx) && numCtx > 0)
						model.ContextWindow = numCtx;
					break;
				}
			}
		}

		if (model.CostInput < 0)
			model.CostInput = 0m;
		if (model.CostOutput < 0)
			model.CostOutput = 0m;
	}

	private static bool AnyWindowUnknown(List<DiscoveredModel> models)
	{
		foreach (DiscoveredModel model in models)
		{
			if (model.ContextWindow == 0)
				return true;
		}
		return false;
	}

	// Type-checked array access. JsonNode.AsArray() THROWS when the node exists with another
	// shape, and providers change field types without notice (an object where an array was, a
	// bare-array response body) — catalog parsing must read leniently, never die on shape.
	private static JsonArray? AsArrayOrNull(JsonNode? node)
	{
		return node as JsonArray;
	}

	// Property access with the same lenience: indexing a NON-OBJECT node throws (a provider that
	// puts a string where an object was expected would kill the parse), so resolve through a
	// type-checked object first and return null for any other shape.
	private static JsonNode? ReadNode(JsonNode? node, string key)
	{
		if (node is JsonObject obj && obj.TryGetPropertyValue(key, out JsonNode? value))
			return value;
		return null;
	}

	private static long? ReadLong(JsonNode? node, string key)
	{
		JsonNode? value = ReadNode(node, key);
		if (value == null)
			return null;
		try
		{
			return value.GetValue<long>();
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static int? ReadInt(JsonNode? node, string key)
	{
		JsonNode? value = ReadNode(node, key);
		if (value == null)
			return null;
		try
		{
			return value.GetValue<int>();
		}
		catch (Exception)
		{
			return null;
		}
	}

	// Pricing values arrive as strings ("0.000003") on OpenRouter and numbers elsewhere.
	private static decimal? ReadDecimal(JsonNode? node, string key)
	{
		JsonNode? value = ReadNode(node, key);
		if (value == null)
			return null;
		try
		{
			return value.GetValue<decimal>();
		}
		catch (Exception)
		{
		}
		try
		{
			string? text = value.GetValue<string>();
			if (decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal parsed))
				return parsed;
		}
		catch (Exception)
		{
		}
		return null;
	}
}
