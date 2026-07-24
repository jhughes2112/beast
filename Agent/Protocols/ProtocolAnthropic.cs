using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// -- Anthropic Messages API (raw HTTP) ---------------------------------------
// Speaks the Messages API directly over HttpClient: request bodies are built as JsonObject and
// responses are assembled from the SSE stream by hand — no SDK. The wire-format JSON itself is
// the native runtime state, which makes "replay the signed thinking blocks verbatim" literal:
// the content blocks that arrived on the stream are stored exactly as received and sent back
// identically on later turns. That signed state is what a semantic reconstruction would destroy.
//
// The canonical store remains ground truth and is kept in sync through the semantic
// OnAssistantTurn fan-out. Rehydrate (session load or protocol switch) starts from empty native
// state and does the best it can from canonical: it strips thinking (canonical thinking is
// unsigned and Anthropic would reject it), maps tool_calls to tool_use blocks and tool results
// to tool_result blocks, and enforces the strict user/assistant alternation the wire protocol
// requires. Once a real signed turn is produced live, that native message supersedes the lossy
// reconstruction.
public class ProtocolAnthropic
{
	private const string AnthropicVersion = "2023-06-01";

	// Native runtime state: wire-format message objects ({"role","content":[blocks]}) plus the
	// system prompt text. Both are in-memory only and rebuilt from canonical by Rehydrate.
	private readonly JsonArray _native = new JsonArray();
	private string _system = string.Empty;

	// The live turn just produced by this protocol, held until the session commits it. The commit
	// fan-out routes OnAssistantTurn back through this protocol, which consumes this verbatim
	// wire-format message (signed thinking and tool-use blocks intact) instead of reconstructing
	// from semantics — reconstructing would append the same blocks a second time and duplicate
	// tool_use ids. A turn the caller drops (unrepairable tool call, interrupt) is never
	// committed, so the pending message is simply discarded when the next turn replaces it.
	private JsonObject? _pendingNative;

	// Rebuilds the native wire-format message chain from canonical, stripping thinking and
	// enforcing user/assistant alternation. Called by ProtocolProxy right after creating or
	// switching in.
	public void Rehydrate(IReadOnlyList<CanonicalMessage> messages)
	{
		_native.Clear();
		_system = string.Empty;
		_pendingNative = null;

		foreach (CanonicalMessage msg in messages)
		{
			if (msg is SystemMessage sm)
			{
				_system = sm.Text;
			}
			else if (msg is UserMessage um)
			{
				AppendContent("user", TextBlock(um.Text));
			}
			else if (msg is ToolResultMessage tr)
			{
				AppendContent("user", ToolResultBlock(tr.ToolCallId, tr.Content));
			}
			else if (msg is AssistantMessage am)
			{
				if (!string.IsNullOrEmpty(am.Text))
					AppendContent("assistant", TextBlock(am.Text));
				foreach (SemanticToolCall tc in am.ToolCalls)
					AppendContent("assistant", ToolUseBlock(tc.Id, tc.Name, tc.ArgumentsJson));
				// thinking is dropped - unsigned thinking cannot be replayed to Anthropic
			}
		}
	}

	public void OnSystemMessage(string text)
	{
		_system = text;
	}

	public void OnUserMessage(string text)
	{
		AppendContent("user", TextBlock(text));
	}

	// A completed assistant turn. When this protocol produced the turn live, the pending
	// wire-format message is appended verbatim so its signed thinking and tool-use blocks
	// survive intact. Otherwise (replay or another protocol) the turn is reconstructed without
	// signature; thinking is intentionally dropped because an unsigned thinking block cannot be
	// replayed to Anthropic.
	public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
	{
		if (_pendingNative != null)
		{
			// The committed calls may have been repaired in place (fuzzy name correction, argument
			// fixups) after this message was captured. Write the repairs back into the matching
			// tool-use blocks so the wire history matches canonical; signatures cover only the
			// thinking blocks, which stay untouched.
			JsonArray content = (JsonArray)_pendingNative["content"]!;
			foreach (SemanticToolCall tc in toolCalls)
			{
				foreach (JsonNode? block in content)
				{
					if (block is JsonObject obj
						&& obj["type"]?.GetValue<string>() == "tool_use"
						&& obj["id"]?.GetValue<string>() == tc.Id)
					{
						obj["name"] = tc.Name;
						obj["input"] = ParseInput(tc.ArgumentsJson);
					}
				}
			}

			_native.Add((JsonNode)_pendingNative);
			_pendingNative = null;
		}
		else
		{
			if (!string.IsNullOrEmpty(text))
				AppendContent("assistant", TextBlock(text));

			foreach (SemanticToolCall tc in toolCalls)
				AppendContent("assistant", ToolUseBlock(tc.Id, tc.Name, tc.ArgumentsJson));
		}
	}

	public void OnToolResult(ToolResult result)
	{
		string content = result.StdOut;
		if (!string.IsNullOrEmpty(result.StdErr))
		{
			content = content + "\nstderr: " + result.StdErr;
		}
		AppendContent("user", ToolResultBlock(result.Id, content));
	}

	public async Task<ProtocolResult> ExecuteAsync(
		LlmModel model,
		ListenerBundle bundle,
		List<ToolDefinition> tools,
		string? forcedToolName,
		int? maxCompletionTokens,
		Dictionary<string, string> extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		LiveUsageProgress onProgress,
		SessionLogger logger,
		CancellationToken cancellationToken)
	{
		try
		{
			// Anthropic requires a positive max_tokens; it cannot be omitted like the OpenAI
			// protocols. Prefer the caller's computed budget, otherwise the model's declared limit.
			int maxTokens = maxCompletionTokens > 0 ? maxCompletionTokens.Value : model.Config.MaxOutputTokens;
			if (maxTokens <= 0)
				maxTokens = 8192;

			JsonObject body = BuildRequestBody(model, tools, forcedToolName, maxTokens, true, extraPayload);
			body["stream"] = true;
			logger.Write(model.Config.Name, model.Endpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

			using HttpRequestMessage request = BuildRequest(model.Endpoint, body, model, extraHeaders);
			using HttpResponseMessage response = await ProtocolHelpers.GetClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
				int statusCode = (int)response.StatusCode;

				if (statusCode == 429 || ProtocolHelpers.IsRateLimited(response, responseBody))
				{
					return logger.ProtocolFailure(
						ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(response, responseBody)),
						model, DetectedProtocol.Anthropic, "RateLimited", statusCode, responseBody, responseBody, null);
				}
				if (ProtocolHelpers.IsPermanentClientError(statusCode))
				{
					if (ProtocolHelpers.IsContextOverflow(responseBody))
					{
						return ProtocolHelpers.ContextOverflowFailure("Anthropic", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId);
					}
					return ProtocolHelpers.Failure("Anthropic", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId);
				}
				return ProtocolHelpers.TransientFailure("Anthropic", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId, response);
			}

			return await ReadStreamAsync(response, model, bundle, onProgress, logger, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			ProtocolResult? timeout = ProtocolHelpers.TimeoutOrRethrow(cancellationToken, model.Config.Name);
			if (timeout != null)
				return timeout;
			throw;
		}
		catch (HttpRequestException ex)
		{
			return logger.ProtocolFailure(
				ProtocolResult.Transient(ex.ToString(), null),
				model, DetectedProtocol.Anthropic, "NetworkError", null, ex.Message, null, ex);
		}
		catch (Exception ex)
		{
			return logger.ProtocolFailure(
				ProtocolResult.Transient(ex.ToString(), null),
				model, DetectedProtocol.Anthropic, "Exception", null, ex.Message, null, ex);
		}
	}

	// Accumulates one streamed content block. The raw start-event object is kept so unknown and
	// exotic block types (redacted_thinking, future additions) survive verbatim; the string
	// builders take the deltas so a long text block is not rebuilt into JSON on every chunk.
	private sealed class BlockBuilder
	{
		public readonly string Type;
		public readonly JsonObject Raw;
		public readonly StringBuilder Text = new StringBuilder();
		public readonly StringBuilder Thinking = new StringBuilder();
		public readonly StringBuilder Signature = new StringBuilder();
		public readonly StringBuilder InputJson = new StringBuilder();

		public BlockBuilder(string type, JsonObject raw)
		{
			Type = type;
			Raw = raw;
		}
	}

	// Reads the SSE stream, assembling the assistant message block-by-block while streaming text
	// and thinking deltas to the client, then commits the turn.
	private async Task<ProtocolResult> ReadStreamAsync(HttpResponseMessage response, LlmModel model, ListenerBundle bundle, LiveUsageProgress onProgress, SessionLogger logger, CancellationToken cancellationToken)
	{
		SortedDictionary<int, BlockBuilder> blocks = new SortedDictionary<int, BlockBuilder>();
		string? openStreamTag = null;
		int freshInputTokens = 0;
		int cacheCreationTokens = 0;
		int cacheReadTokens = 0;
		int outputTokens = 0;
		string stopReason = "end_turn";
		int streamedChars = 0;
		bool sawEvent = false;

		try
		{
			using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			using StreamReader reader = new StreamReader(stream);

			for (; ; )
			{
				string? line = await reader.ReadLineAsync(cancellationToken);
				if (line == null)
					break;
				if (!line.StartsWith("data: ", StringComparison.Ordinal))
					continue;

				JsonNode? evt = JsonNode.Parse(line.Substring(6));
				string? type = evt?["type"]?.GetValue<string>();
				if (type == null)
					continue;
				sawEvent = true;

				if (type == "message_start")
				{
					JsonNode? usage = evt!["message"]?["usage"];
					freshInputTokens = usage?["input_tokens"]?.GetValue<int>() ?? 0;
					cacheCreationTokens = usage?["cache_creation_input_tokens"]?.GetValue<int>() ?? 0;
					cacheReadTokens = usage?["cache_read_input_tokens"]?.GetValue<int>() ?? 0;
				}
				else if (type == "content_block_start")
				{
					int index = evt!["index"]?.GetValue<int>() ?? blocks.Count;
					JsonObject raw = evt["content_block"] as JsonObject ?? new JsonObject();
					string blockType = raw["type"]?.GetValue<string>() ?? "text";
					blocks[index] = new BlockBuilder(blockType, (JsonObject)raw.DeepClone());
				}
				else if (type == "content_block_delta")
				{
					int index = evt!["index"]?.GetValue<int>() ?? 0;
					if (!blocks.TryGetValue(index, out BlockBuilder? block))
						continue;

					JsonNode? delta = evt["delta"];
					string? deltaType = delta?["type"]?.GetValue<string>();
					if (deltaType == "text_delta")
					{
						string text = delta?["text"]?.GetValue<string>() ?? string.Empty;
						block.Text.Append(text);
						streamedChars += text.Length;

						// Don't open the assistant output block on leading whitespace: a turn that is
						// only thinking plus a tool call may still emit a stray newline, and opening
						// here would leave an empty block behind. Once open, every delta streams.
						bool assistantOpen = openStreamTag == StreamTag.Assistant;
						if (assistantOpen || !string.IsNullOrWhiteSpace(text))
						{
							if (!assistantOpen)
							{
								if (openStreamTag != null)
									bundle.Transport?.OnStreamEnd(openStreamTag);
								bundle.Transport?.OnStreamStart(StreamTag.Assistant);
								openStreamTag = StreamTag.Assistant;
							}
							bundle.Transport?.OnStreamChunk(StreamTag.Assistant, text);
						}
					}
					else if (deltaType == "thinking_delta")
					{
						string thinkingText = delta?["thinking"]?.GetValue<string>() ?? string.Empty;
						block.Thinking.Append(thinkingText);
						streamedChars += thinkingText.Length;
						if (openStreamTag != StreamTag.Thinking)
						{
							if (openStreamTag != null)
								bundle.Transport?.OnStreamEnd(openStreamTag);
							bundle.Transport?.OnStreamStart(StreamTag.Thinking);
							openStreamTag = StreamTag.Thinking;
						}
						bundle.Transport?.OnStreamChunk(StreamTag.Thinking, thinkingText);
					}
					else if (deltaType == "signature_delta")
					{
						block.Signature.Append(delta?["signature"]?.GetValue<string>() ?? string.Empty);
					}
					else if (deltaType == "input_json_delta")
					{
						string partial = delta?["partial_json"]?.GetValue<string>() ?? string.Empty;
						block.InputJson.Append(partial);
						streamedChars += partial.Length;
					}
				}
				else if (type == "message_delta")
				{
					string? reason = evt!["delta"]?["stop_reason"]?.GetValue<string>();
					if (reason != null)
						stopReason = reason;
					int reported = evt["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
					if (reported > 0)
						outputTokens = reported;
				}
				else if (type == "error")
				{
					// Mid-stream server error (e.g. overloaded_error). Retryable unless it is a
					// context overflow, which can never succeed verbatim.
					string message = evt!["error"]?.ToJsonString() ?? "Unknown streaming error";
					if (ProtocolHelpers.IsContextOverflow(message))
					{
						return logger.ProtocolFailure(
							ProtocolResult.ContextFull(message),
							model, DetectedProtocol.Anthropic, "ContextOverflow", null, message, null, null);
					}
					return logger.ProtocolFailure(
						ProtocolResult.Transient(message, null),
						model, DetectedProtocol.Anthropic, "StreamError", null, message, null, null);
				}
				// message_stop, content_block_stop, ping: nothing to do — blocks finalize below.

				// Provisional running usage for this turn. Anthropic only reports output tokens on
				// the final message_delta, so during the body of the stream we estimate from
				// accumulated streamed characters (~4 chars/token) for continuous motion.
				int liveOutput = outputTokens > 0 ? outputTokens : streamedChars / 4;
				int liveCached = cacheReadTokens + cacheCreationTokens;
				decimal liveCost = (freshInputTokens / 1_000_000m) * model.Config.Cost.Input
								 + (liveOutput / 1_000_000m) * model.Config.Cost.Output;
				onProgress(freshInputTokens, liveOutput, liveCost, liveCached);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return logger.ProtocolFailure(
				ProtocolResult.Transient(ex.ToString(), null),
				model, DetectedProtocol.Anthropic, "StreamError", null, ex.Message, null, ex);
		}
		finally
		{
			if (openStreamTag != null)
				bundle.Transport?.OnStreamEnd(openStreamTag);
		}

		if (!sawEvent)
		{
			return ProtocolResult.Transient("Empty response from Anthropic API", null);
		}

		return CommitStreamedResponse(blocks, freshInputTokens, cacheCreationTokens, cacheReadTokens, outputTokens, stopReason, model);
	}

	// Finalizes the streamed blocks into the wire-format assistant message (held as pending until
	// the session commits it) and the semantic payload.
	private ProtocolResult CommitStreamedResponse(SortedDictionary<int, BlockBuilder> blocks, int freshInputTokens, int cacheCreationTokens, int cacheReadTokens, int outputTokens, string stopReason, LlmModel model)
	{
		JsonArray content = new JsonArray();
		StringBuilder textBuilder = new StringBuilder();
		StringBuilder thinkingBuilder = new StringBuilder();
		List<SemanticToolCall> toolCalls = new List<SemanticToolCall>();

		foreach ((int _, BlockBuilder block) in blocks)
		{
			if (block.Type == "text")
			{
				string text = block.Text.ToString();
				textBuilder.Append(text);
				content.Add((JsonNode)TextBlock(text));
			}
			else if (block.Type == "thinking")
			{
				string thinking = block.Thinking.ToString();
				thinkingBuilder.Append(thinking);
				JsonObject thinkingBlock = new JsonObject();
				thinkingBlock["type"] = "thinking";
				thinkingBlock["thinking"] = thinking;
				thinkingBlock["signature"] = block.Signature.ToString();
				content.Add((JsonNode)thinkingBlock);
			}
			else if (block.Type == "tool_use")
			{
				string id = block.Raw["id"]?.GetValue<string>() ?? string.Empty;
				string name = block.Raw["name"]?.GetValue<string>() ?? string.Empty;
				string argsJson = block.InputJson.Length > 0 ? block.InputJson.ToString() : (block.Raw["input"]?.ToJsonString() ?? "{}");
				toolCalls.Add(new SemanticToolCall { Id = id, Name = name, ArgumentsJson = argsJson });

				JsonObject use = new JsonObject();
				use["type"] = "tool_use";
				use["id"] = id;
				use["name"] = name;
				use["input"] = ParseInput(argsJson);
				content.Add((JsonNode)use);
			}
			else
			{
				// Unknown or exotic block (redacted_thinking, future types): preserve the raw
				// start-event object verbatim so replay sends back exactly what arrived.
				content.Add(block.Raw.DeepClone());
			}
		}

		// Held as pending, NOT added to _native here: the session's commit fan-out delivers this
		// turn back via OnAssistantTurn, which appends the pending message exactly once. Adding it
		// here as well would double-append every live turn and duplicate tool_use ids.
		JsonObject assistant = new JsonObject();
		assistant["role"] = "assistant";
		assistant["content"] = content;
		_pendingNative = assistant;

		// Anthropic's input_tokens excludes cache reads/writes; the full context is the sum of all
		// input components plus output.
		int totalInputTokens = freshInputTokens + cacheCreationTokens + cacheReadTokens;

		TokenUsageInfo usage = new TokenUsageInfo
		{
			PromptTokens = totalInputTokens,
			CompletionTokens = outputTokens,
			CachedTokens = cacheReadTokens + cacheCreationTokens
		};

		// Cost from the model's configured per-million pricing, billing cache writes and reads at
		// their own rates. Unconfigured pricing (all zeros) simply costs zero — there is no SDK
		// fallback table anymore, and guessing prices would violate "counts come from the server".
		decimal cost = (freshInputTokens / 1_000_000m) * model.Config.Cost.Input
					 + (cacheCreationTokens / 1_000_000m) * model.Config.Cost.CacheWrite
					 + (cacheReadTokens / 1_000_000m) * model.Config.Cost.CacheRead
					 + (outputTokens / 1_000_000m) * model.Config.Cost.Output;

		string finishReason = toolCalls.Count > 0 ? "tool_calls" : stopReason;
		List<ToolResult> emptyResults = new List<ToolResult>();
		return ProtocolResult.Succeeded(new ProtocolCallPayload(textBuilder.ToString(), thinkingBuilder.ToString(), toolCalls, emptyResults, finishReason, usage, cost));
	}

	// Token counting call: uses Anthropic's dedicated /count_tokens endpoint (side-effect-free).
	// Falls back to the legacy tracer (max_tokens=1) if the count endpoint is unavailable (e.g.
	// OpenRouter).
	public async Task<TracerResult> CountTokensAsync(
		LlmModel model,
		List<ToolDefinition> tools,
		string? forcedToolName,
		Dictionary<string, string> extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		SessionLogger logger,
		CancellationToken cancellationToken)
	{
		// Build the count endpoint URL: model.Endpoint ends with /messages, count endpoint is
		// /messages/count_tokens.
		string countEndpoint = model.Endpoint.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)
			? model.Endpoint + "/count_tokens"
			: model.Endpoint.TrimEnd('/') + "/count_tokens";

		// The count body mirrors /messages but carries no max_tokens, stream, or thinking.
		JsonObject body = BuildRequestBody(model, tools, forcedToolName, 0, false, extraPayload);
		logger.Write(model.Config.Name, countEndpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		HttpResponseMessage httpResponse;
		string responseBody;
		try
		{
			using HttpRequestMessage req = BuildRequest(countEndpoint, body, model, extraHeaders);
			httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			ProtocolResult? timeout = ProtocolHelpers.TimeoutOrRethrow(cancellationToken, model.Config.Name);
			if (timeout != null)
				return TracerResult.Failed(timeout.ErrorMessage);
			throw;
		}
		catch (Exception ex)
		{
			return TracerResult.Failed(ex.ToString());
		}

		int statusCode = (int)httpResponse.StatusCode;

		if (httpResponse.IsSuccessStatusCode)
		{
			JsonNode? root = JsonNode.Parse(responseBody);
			if (root == null)
				return TracerResult.Failed("Empty response from count_tokens API");

			// count_tokens reports the TOTAL prompt size in input_tokens; no cache split exists here.
			int inputTokens = root["input_tokens"]?.GetValue<int>() ?? 0;
			return TracerResult.Success(inputTokens, 0);
		}

		// Count endpoint unavailable (404) or other 4xx — fall back to legacy tracer
		if (statusCode >= 400 && statusCode < 500)
		{
			return await ExecuteTracerAsync(model, tools, forcedToolName, extraHeaders, extraPayload, logger, cancellationToken);
		}

		if (statusCode == 429 || ProtocolHelpers.IsRateLimited(httpResponse, responseBody))
		{
			return TracerResult.Failed($"Rate limited: {responseBody}");
		}

		return TracerResult.Failed($"HTTP {statusCode}: {responseBody}");
	}

	// Tracer call: sends the real request with max_tokens=1 to get accurate input token counts
	// without generating a meaningful response. Kept as fallback for providers without
	// /count_tokens (e.g. OpenRouter).
	public async Task<TracerResult> ExecuteTracerAsync(
		LlmModel model,
		List<ToolDefinition> tools,
		string? forcedToolName,
		Dictionary<string, string> extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		SessionLogger logger,
		CancellationToken cancellationToken)
	{
		JsonObject body = BuildRequestBody(model, tools, forcedToolName, 1, false, extraPayload);
		logger.Write(model.Config.Name, model.Endpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		HttpResponseMessage httpResponse;
		string responseBody;
		try
		{
			using HttpRequestMessage req = BuildRequest(model.Endpoint, body, model, extraHeaders);
			httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			ProtocolResult? timeout = ProtocolHelpers.TimeoutOrRethrow(cancellationToken, model.Config.Name);
			if (timeout != null)
				return TracerResult.Failed(timeout.ErrorMessage);
			throw;
		}
		catch (Exception ex)
		{
			return TracerResult.Failed(ex.ToString());
		}

		int statusCode = (int)httpResponse.StatusCode;

		if (httpResponse.IsSuccessStatusCode)
		{
			JsonNode? root = JsonNode.Parse(responseBody);
			JsonNode? usageNode = root?["usage"];
			if (usageNode == null)
				return TracerResult.Failed("No usage info in tracer response");

			// Anthropic's input_tokens EXCLUDES cache reads/writes; TracerResult.InputTokens is
			// defined as the TOTAL prompt size, so fold the cache components in here.
			int inputTokens = usageNode["input_tokens"]?.GetValue<int>() ?? 0;
			int cacheWriteTokens = usageNode["cache_creation_input_tokens"]?.GetValue<int>() ?? 0;
			int cacheReadTokens = usageNode["cache_read_input_tokens"]?.GetValue<int>() ?? 0;
			int cachedTokens = cacheWriteTokens + cacheReadTokens;

			return TracerResult.Success(inputTokens + cachedTokens, cachedTokens);
		}

		// 4xx (non-429, non-retryable) — distinguish actual context overflow from parameter errors
		if (ProtocolHelpers.IsPermanentClientError(statusCode))
		{
			if (ProtocolHelpers.IsContextOverflow(responseBody) || responseBody.ToLowerInvariant().Contains("max_tokens"))
			{
				return TracerResult.ContextExceeded(statusCode);
			}
			return TracerResult.FailedHttp(statusCode, $"HTTP {statusCode}: {responseBody}");
		}

		if (statusCode == 429 || ProtocolHelpers.IsRateLimited(httpResponse, responseBody))
		{
			return TracerResult.Failed($"Rate limited: {responseBody}");
		}

		return TracerResult.Failed($"HTTP {statusCode}: {responseBody}");
	}

	// ---- Request construction ----

	// Builds a /messages request body from the native state. withMaxTokensAndThinking is true for
	// the real streaming call (max_tokens required, thinking budget resolved) and false for
	// count_tokens (which rejects both); the tracer passes maxTokens=1 with thinking off.
	private JsonObject BuildRequestBody(LlmModel model, List<ToolDefinition> tools, string? forcedToolName, int maxTokens, bool withThinking, Dictionary<string, JsonNode?> extraPayload)
	{
		JsonObject body = new JsonObject();
		body["model"] = model.Config.Id;
		if (maxTokens > 0)
		{
			// count_tokens (maxTokens == 0) rejects extra inputs — max_tokens and temperature are
			// only legal on the real /messages call.
			body["max_tokens"] = maxTokens;
			body["temperature"] = 1.0;
		}
		body["messages"] = CloneMessages();

		if (!string.IsNullOrEmpty(_system))
		{
			body["system"] = _system;
		}

		if (tools.Count > 0)
		{
			JsonArray toolsArr = new JsonArray();
			foreach (ToolDefinition td in tools)
			{
				JsonObject t = new JsonObject();
				t["name"] = td.Function.Name;
				if (!string.IsNullOrEmpty(td.Function.Description))
					t["description"] = td.Function.Description;
				if (td.Function.Parameters != null)
					t["input_schema"] = td.Function.Parameters.DeepClone();
				toolsArr.Add((JsonNode)t);
			}
			body["tools"] = toolsArr;

			// Force a specific tool when asked, require any tool for the AnyTool sentinel;
			// otherwise the model chooses (API default, no tool_choice sent).
			if (forcedToolName == ProtocolProxy.AnyTool)
			{
				JsonObject tc = new JsonObject();
				tc["type"] = "any";
				body["tool_choice"] = tc;
			}
			else if (!string.IsNullOrEmpty(forcedToolName))
			{
				JsonObject tc = new JsonObject();
				tc["type"] = "tool";
				tc["name"] = forcedToolName;
				body["tool_choice"] = tc;
			}
		}

		// Thinking is incompatible with a forced tool_choice ("any"/"tool") at the API level, so a
		// wind-down turn that forces the terminator runs without it rather than 400ing.
		if (withThinking && string.IsNullOrEmpty(forcedToolName))
		{
			int budget = ReasoningEffort.AnthropicBudget(model.Config.ReasoningEffort, maxTokens);
			if (budget > 0)
			{
				JsonObject thinking = new JsonObject();
				thinking["type"] = "enabled";
				thinking["budget_tokens"] = budget;
				body["thinking"] = thinking;
			}
		}

		// Merge extras verbatim as top-level fields (an explicit "thinking" object here overrides
		// the word-derived budget above). count_tokens/tracer callers must not receive stream or
		// thinking, and max_tokens is owned by the caller's budget math.
		foreach ((string name, JsonNode? value) in extraPayload)
		{
			if (name == "stream" || name == "max_tokens")
				continue;
			if (!withThinking && name == "thinking")
				continue;
			body[name] = value?.DeepClone();
		}

		return body;
	}

	// Clones the native chain for a request, merging consecutive same-role messages so the wire
	// always sees strict user/assistant alternation even if a pending-consumed turn landed next
	// to a reconstructed one.
	private JsonArray CloneMessages()
	{
		JsonArray messages = new JsonArray();
		foreach (JsonNode? node in _native)
		{
			if (node is not JsonObject msg)
				continue;

			string role = msg["role"]?.GetValue<string>() ?? "user";
			if (messages.Count > 0)
			{
				JsonObject last = (JsonObject)messages[messages.Count - 1]!;
				if (last["role"]?.GetValue<string>() == role)
				{
					JsonArray lastContent = (JsonArray)last["content"]!;
					foreach (JsonNode? block in (JsonArray)msg["content"]!)
					{
						if (block != null)
							lastContent.Add(block.DeepClone());
					}
					continue;
				}
			}
			messages.Add(msg.DeepClone());
		}
		return messages;
	}

	// One request shape for every endpoint: both auth header styles are sent — api.anthropic.com
	// reads x-api-key while Bearer satisfies OpenRouter-style gateways — plus the model's extras.
	private static HttpRequestMessage BuildRequest(string endpoint, JsonObject body, LlmModel model, Dictionary<string, string> extraHeaders)
	{
		HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
		request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		request.Headers.TryAddWithoutValidation("x-api-key", model.ApiKey);
		request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
		request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

		foreach ((string name, string value) in extraHeaders)
		{
			request.Headers.TryAddWithoutValidation(name, value);
		}

		return request;
	}

	// ---- Wire-format block builders ----

	private static JsonObject TextBlock(string text)
	{
		JsonObject block = new JsonObject();
		block["type"] = "text";
		block["text"] = text;
		return block;
	}

	private static JsonObject ToolUseBlock(string id, string name, string argsJson)
	{
		JsonObject block = new JsonObject();
		block["type"] = "tool_use";
		block["id"] = id;
		block["name"] = name;
		block["input"] = ParseInput(argsJson);
		return block;
	}

	private static JsonObject ToolResultBlock(string toolUseId, string content)
	{
		JsonObject block = new JsonObject();
		block["type"] = "tool_result";
		block["tool_use_id"] = toolUseId;
		block["content"] = new JsonArray(TextBlock(content));
		return block;
	}

	// Appends content to the trailing message when it shares the role, otherwise starts a new
	// message. This collapses consecutive same-role blocks into the alternation Anthropic requires.
	private void AppendContent(string role, JsonObject block)
	{
		if (_native.Count > 0)
		{
			JsonObject last = (JsonObject)_native[_native.Count - 1]!;
			if (last["role"]?.GetValue<string>() == role)
			{
				((JsonArray)last["content"]!).Add((JsonNode)block);
				return;
			}
		}

		JsonObject msg = new JsonObject();
		msg["role"] = role;
		msg["content"] = new JsonArray(block);
		_native.Add((JsonNode)msg);
	}

	private static JsonNode ParseInput(string argsJson)
	{
		JsonNode? parsed = null;
		if (!string.IsNullOrEmpty(argsJson))
		{
			try
			{ parsed = JsonNode.Parse(argsJson); }
			catch (JsonException) { parsed = null; }
		}
		return parsed ?? new JsonObject();
	}
}
