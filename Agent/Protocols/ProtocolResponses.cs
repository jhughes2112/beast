using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static SessionLoggerExtensions;

// OpenAI Responses API  
// Wire protocol is stateful by default: passing previous_response_id continues
// a server-managed conversation thread, so do not also replay the full message
// history or the context will be duplicated. Either use stateful chaining via
// previous_response_id, or send the full history yourself - never both.
// Mixing the two approaches within a session produces subtle, hard-to-debug
// context corruption.
//
// Reads canonical messages from bundle.Canonical and translates them into the Responses flat
// input-item shape. This protocol keeps one piece of native runtime state: previous_response_id.
// When the server returns a response id, the next turn chains from it by sending previous_response_id
// plus only the NEW input items appended since the last turn (never replaying the whole history).
// previous_response_id is in-memory only and is never written into canonical state. On Rehydrate
// (session load or protocol switch) the id is cleared so the next turn replays full history once.
public class ProtocolResponses
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = false
	};

	private bool _streamingSupported = true;

	// Native runtime state: the last server-issued response id. In-memory only, reset by Rehydrate.
	private string? _previousResponseId;
	// Full input array built once during Rehydrate for the first post-rehydration turn.
	// After successful send, cleared so subsequent turns use incremental chaining.
	private JsonArray? _rehydratedInput;
	// Incremental input items accumulated since the last successful send via IProtocolListener callbacks.
	// Used for chaining mode after _rehydratedInput is consumed.
	private JsonArray _deltaInput;

	public ProtocolResponses()
	{
		_deltaInput = new JsonArray();
	}

	// Clears native chaining state so the next turn replays the full canonical history once.
	public void Rehydrate(IReadOnlyList<CanonicalMessage> messages)
	{
		_previousResponseId = null;
		_deltaInput.Clear();

		JsonArray input = new JsonArray();
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is SystemMessage sm)
			{
				input.Add(BuildMessageItem("system", "input_text", sm.Text));
			}
			else if (msg is UserMessage um)
			{
				input.Add(BuildMessageItem("user", "input_text", um.Text));
			}
			else if (msg is AssistantMessage am)
			{
				if (!string.IsNullOrEmpty(am.Text))
					input.Add(BuildMessageItem("assistant", "output_text", am.Text));

				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					string id = ProtocolHelpers.NormalizeToolCallId(tc.Id);
					JsonObject item = new JsonObject();
					item["type"] = "function_call";
					item["id"] = id;
					item["call_id"] = id;
					item["name"] = tc.Name;
					item["arguments"] = tc.ArgumentsJson;
					input.Add(item);
				}
			}
			else if (msg is ToolResultMessage tr)
			{
				JsonObject item = new JsonObject();
				item["type"] = "function_call_output";
				item["call_id"] = ProtocolHelpers.NormalizeToolCallId(tr.ToolCallId);
				item["output"] = tr.Content;
				input.Add(item);
			}
		}
		_rehydratedInput = input;
	}

	// Track incremental changes to build deltas for chaining mode.
	public void OnSystemMessage(string text)
	{
		_deltaInput.Add(BuildMessageItem("system", "input_text", text));
	}

	public void OnUserMessage(string text)
	{
		_deltaInput.Add(BuildMessageItem("user", "input_text", text));
	}

	public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
	{
		if (!string.IsNullOrEmpty(text))
		{
			_deltaInput.Add(BuildMessageItem("assistant", "output_text", text));
		}

		foreach (SemanticToolCall tc in toolCalls)
		{
			string normalizedId = ProtocolHelpers.NormalizeToolCallId(tc.Id);
			JsonObject item = new JsonObject();
			item["type"] = "function_call";
			item["id"] = normalizedId;
			item["call_id"] = normalizedId;
			item["name"] = tc.Name;
			item["arguments"] = tc.ArgumentsJson;
			_deltaInput.Add(item);
		}
	}

	public void OnToolResult(ToolResult result)
	{
		JsonObject item = new JsonObject();
		item["type"] = "function_call_output";
		item["call_id"] = ProtocolHelpers.NormalizeToolCallId(result.Id);
		string output = result.StdOut;
		if (!string.IsNullOrEmpty(result.StdErr))
		{
			output = output + "\nstderr: " + result.StdErr;
		}
		item["output"] = output;
		_deltaInput.Add(item);
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
			JsonObject body = BuildBody(model, tools, forcedToolName, maxCompletionTokens, extraPayload);
			logger.Write(model.Config.Name, model.Endpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

			if (_streamingSupported)
			{
				ProtocolResult? streamResult = await ExecuteStreamingAsync(model, body, extraHeaders, bundle, onProgress, logger, cancellationToken);
				if (streamResult != null)
					return streamResult;
			}

			string requestJson = body.ToJsonString();

			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
			req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
			req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
			foreach ((string name, string value) in extraHeaders)
			{
				req.Headers.TryAddWithoutValidation(name, value);
			}

			HttpResponseMessage httpResponse;
			string responseBody;
			try
			{
				httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, cancellationToken);
				responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
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
				logger.ProtocolFailure(
					"NetworkError", ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null,
					ex.Message, null, ex,
					model, DetectedProtocol.Responses);
				return ProtocolResult.Transient(ex.ToString(), null);
			}
			catch (Exception ex)
			{
				logger.ProtocolFailure(
					"Exception", null,
					ex.Message, null, ex,
					model, DetectedProtocol.Responses);
				return ProtocolResult.Transient(ex.ToString(), null);
			}

			if (httpResponse.IsSuccessStatusCode)
			{
				JsonNode? root = JsonNode.Parse(responseBody);
				if (root == null)
					return ProtocolResult.Transient("Empty response from Responses API", null);

				return CommitResponse(bundle, root, model);
			}

			if (ProtocolHelpers.IsRateLimited(httpResponse, responseBody))
			{
				logger.ProtocolFailure(
					"RateLimited", (int)httpResponse.StatusCode,
					responseBody, responseBody, null,
					model, DetectedProtocol.Responses);
				return ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, responseBody));
			}

			int statusCode = (int)httpResponse.StatusCode;
			// A 4xx other than the 429 handled above (and the genuinely retryable 408/425) is a permanent
			// client error: the request itself is bad, so retrying just burns the transient budget and then
			// surfaces as a misleading "rate limited". Fail fast with the body so the real cause is visible;
			// 5xx and the retryable 4xx stay transient.
			if (ProtocolHelpers.IsPermanentClientError(statusCode))
			{
				return ProtocolHelpers.Failure("Responses", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId);
			}
			return ProtocolHelpers.TransientFailure("Responses", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId, httpResponse);
		}
		catch (Exception ex)
		{
			logger.ProtocolFailure(
				"Exception", null,
				ex.Message, null, ex,
				model, DetectedProtocol.Responses);
			return ProtocolResult.Transient(ex.ToString(), null);
		}
	}

	private JsonObject BuildBody(LlmModel model, List<ToolDefinition> tools, string? forcedToolName, int? maxCompletionTokens, Dictionary<string, JsonNode?> extraPayload)
	{
		JsonObject body = new JsonObject();
		body["model"] = model.Config.Id;

		// If we have rehydrated input, send the full history once (no previous_response_id).
		// Otherwise, chain from the last response id and send only the delta items.
		// DeepClone is required: assigning a JsonNode to a body object parents it, and the same
		// node cannot be parented twice — re-use across turns throws InvalidOperationException.
		if (_rehydratedInput != null)
		{
			body["input"] = _rehydratedInput.DeepClone();
		}
		else
		{
			body["input"] = _deltaInput.DeepClone();
			if (_previousResponseId != null)
			{
				body["previous_response_id"] = _previousResponseId;
			}
		}

		if (maxCompletionTokens > 0)
			body["max_output_tokens"] = maxCompletionTokens.Value;

		if (tools.Count > 0)
		{
			JsonArray toolsArr = new JsonArray();
			JsonObject twebsearch = new JsonObject();  // the allows web search to happen internally on any OpenAI model, about a penny a search
			twebsearch["type"] = "web_search";
			toolsArr.Add(twebsearch);

			foreach (ToolDefinition td in tools)
			{
				JsonObject t = new JsonObject();
				t["type"] = "function";
				t["name"] = td.Function.Name;
				if (!string.IsNullOrEmpty(td.Function.Description))
					t["description"] = td.Function.Description;
				if (td.Function.Parameters != null)
					t["parameters"] = td.Function.Parameters.DeepClone();
				toolsArr.Add(t);
			}
			body["tools"] = toolsArr;

			// Force a specific tool when asked, require any tool for the AnyTool sentinel; otherwise
			// leave the choice to the model.
			if (forcedToolName == ProtocolProxy.AnyTool)
			{
				body["tool_choice"] = "required";
			}
			else if (!string.IsNullOrEmpty(forcedToolName))
			{
				JsonObject choice = new JsonObject();
				choice["type"] = "function";
				choice["name"] = forcedToolName;
				body["tool_choice"] = choice;
			}
			else
			{
				body["tool_choice"] = "auto";
			}
		}

		// Translate the friendly reasoningEffort word into the Responses-native reasoning.effort object.
		// Applied before extras so an explicit "reasoning" block in extras can still override it.
		string? effort = ReasoningEffort.OpenAiEffort(model.Config.ReasoningEffort);
		if (effort != null)
		{
			JsonObject reasoning = new JsonObject();
			reasoning["effort"] = effort;
			body["reasoning"] = reasoning;
		}

		foreach ((string name, JsonNode? value) in extraPayload)
		{
			body[name] = value?.DeepClone();
		}

		return body;
	}

	private static JsonObject BuildMessageItem(string role, string blockType, string text)
	{
		JsonObject item = new JsonObject();
		item["type"] = "message";
		item["role"] = role;
		JsonArray content = new JsonArray();
		JsonObject block = new JsonObject();
		block["type"] = blockType;
		block["text"] = text;
		content.Add(block);
		item["content"] = content;
		return item;
	}

	private async Task<ProtocolResult?> ExecuteStreamingAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, ListenerBundle bundle, LiveUsageProgress onProgress, SessionLogger logger, CancellationToken cancellationToken)
	{
		JsonObject streamBody = (JsonObject)body.DeepClone();
		streamBody["stream"] = true;

		string requestJson = streamBody.ToJsonString();

		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
		req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
		req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");

		foreach ((string name, string value) in extraHeaders)
		{
			req.Headers.TryAddWithoutValidation(name, value);
		}

		HttpResponseMessage httpResponse;
		try
		{
			httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
			logger.ProtocolFailure(
				"NetworkError", ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null,
				ex.Message, null, ex,
				model, DetectedProtocol.Responses);
			return ProtocolResult.Transient(ex.ToString(), null);
		}
		catch (Exception ex)
		{
			logger.ProtocolFailure(
				"Exception", null,
				ex.Message, null, ex,
				model, DetectedProtocol.Responses);
			return ProtocolResult.Transient(ex.ToString(), null);
		}

		if (!httpResponse.IsSuccessStatusCode)
		{
			string errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
			int statusCode = (int)httpResponse.StatusCode;

			// A 4xx other than the 429 handled above is a permanent client error in the streaming path:
			// the provider rejects streaming for this model, so we disable it and fall through to
			// non-streaming. Note: unlike the non-streaming path (which excludes 408/425 as retryable),
			// any non-429 4xx here means streaming is not supported — the caller retries non-streaming.
			if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
			{
				_streamingSupported = false;
				logger.ProtocolFailure(
					statusCode == 401 || statusCode == 403 ? "AuthFailure" : "ClientError",
					statusCode, errorBody, errorBody, null,
					model, DetectedProtocol.Responses);
				return null;
			}

			if (ProtocolHelpers.IsRateLimited(httpResponse, errorBody))
			{
				logger.ProtocolFailure(
					"RateLimited", statusCode,
					errorBody, errorBody, null,
					model, DetectedProtocol.Responses);
				return ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, errorBody));
			}

			logger.ProtocolFailure(
				statusCode >= 500 ? "ServerError" : "Transient",
				statusCode, errorBody, errorBody, null,
				model, DetectedProtocol.Responses);
			return ProtocolResult.Transient($"HTTP {statusCode}: {errorBody}", ProtocolHelpers.TryGetRetryAfter(httpResponse, errorBody));
		}

		JsonNode? finalResponseNode = null;
		string? openStreamTag = null;
		int liveInputTokens = 0;
		int liveCachedTokens = 0;

		try
		{
			using (Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken))
			using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
			{
				while (true)
				{
					string? line = await reader.ReadLineAsync(cancellationToken);
					if (line == null)
						break;
					if (!line.StartsWith("data: "))
						continue;

					string data = line.Substring(6);
					if (data == "[DONE]")
						break;

					JsonNode? eventNode = JsonNode.Parse(data);
					if (eventNode == null)
						continue;

					string? eventType = eventNode["type"]?.GetValue<string>();

					// Any event that carries a response.usage.input_tokens establishes the input
					// baseline for live frames. response.created and response.in_progress provide
					// this before the first text delta, so input no longer counts up from zero.
					int? eventInputTokens = eventNode["response"]?["usage"]?["input_tokens"]?.GetValue<int?>();
					if (eventInputTokens.HasValue && eventInputTokens.Value > 0)
					{
						liveInputTokens = eventInputTokens.Value;
					}
					int? eventCachedTokens = eventNode["response"]?["usage"]?["input_tokens_details"]?["cached_tokens"]?.GetValue<int?>();
					if (eventCachedTokens.HasValue && eventCachedTokens.Value > 0)
					{
						liveCachedTokens = eventCachedTokens.Value;
					}

					if (eventType == "response.output_text.delta")
					{
						string? delta = eventNode["delta"]?.GetValue<string>();
						if (!string.IsNullOrEmpty(delta))
						{
							// Don't open the assistant output block on leading whitespace: a thinking+tool-call
							// turn that emits a stray newline would otherwise leave an empty block. Wait for the
							// first non-whitespace text; once open, stream every delta. Committed text comes from
							// the final response, so it is unaffected by what we skip here.
							bool assistantOpen = openStreamTag == StreamTag.Assistant;
							if (assistantOpen || !string.IsNullOrWhiteSpace(delta))
							{
								if (!assistantOpen)
								{
									if (openStreamTag != null)
									{
										bundle.Transport?.OnStreamEnd(openStreamTag);
									}
									bundle.Transport?.OnStreamStart(StreamTag.Assistant);
									openStreamTag = StreamTag.Assistant;
								}
								bundle.Transport?.OnStreamChunk(StreamTag.Assistant, delta);
							}
							EmitProgress(model, liveInputTokens, onProgress, liveCachedTokens);
						}
					}
					else if (eventType == "response.reasoning_summary_text.delta")
					{
						string? delta = eventNode["delta"]?.GetValue<string>();
						if (!string.IsNullOrEmpty(delta))
						{
							if (openStreamTag != StreamTag.Thinking)
							{
								if (openStreamTag != null)
								{
									bundle.Transport?.OnStreamEnd(openStreamTag);
								}
								bundle.Transport?.OnStreamStart(StreamTag.Thinking);
								openStreamTag = StreamTag.Thinking;
							}
							bundle.Transport?.OnStreamChunk(StreamTag.Thinking, delta);
							EmitProgress(model, liveInputTokens, onProgress, liveCachedTokens);
						}
					}
					else if (eventType == "response.completed" || eventType == "response.done")
					{
						finalResponseNode = eventNode["response"];
						break;
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return ProtocolResult.Transient(ex.ToString(), null);
		}
		finally
		{
			if (openStreamTag != null)
			{
				bundle.Transport?.OnStreamEnd(openStreamTag);
			}
		}

		if (finalResponseNode != null)
		{
			return CommitResponse(bundle, finalResponseNode, model);
		}

		return ProtocolResult.Transient("Stream ended without a response.completed event", null);
	}

	// The Responses SSE stream does not surface usage on text deltas, but response.created and
	// response.in_progress carry response.usage.input_tokens early, which the caller passes in as
	// liveInputTokens. Output is intentionally NOT estimated from streamed characters here: the
	// committed output_tokens includes hidden reasoning tokens that never appear in the stream, so
	// a streamedChars/4 estimate badly undercounts and the commit then snaps up by a large amount,
	// which reads as a double count on the client. Instead the live frame advances only the
	// authoritative input (and its cost), holding output at the session baseline until the
	// committed usage arrives at end-of-turn.
	private void EmitProgress(LlmModel model, int liveInputTokens, LiveUsageProgress onProgress, int liveCachedTokens = 0)
	{
		decimal liveCost = (liveInputTokens / 1_000_000m) * model.Config.Cost.Input;
		onProgress(liveInputTokens, 0, liveCost, liveCachedTokens);
	}

	// Raises a single semantic assistant turn through the bundle so the canonical store records
	// the normalized turn and the transport listener emits the committed frames. Captures the
	// server response id for the next turn's previous_response_id chaining. Clears rehydrated
	// input and delta accumulator after successful send.
	private ProtocolResult CommitResponse(ListenerBundle bundle, JsonNode responseRoot, LlmModel model)
	{
		JsonArray? output = responseRoot["output"]?.AsArray();
		if (output == null || output.Count == 0)
		{
			string? errMsg = responseRoot["error"]?["message"]?.GetValue<string>();
			return ProtocolResult.Transient(errMsg ?? "Empty response from Responses API", null);
		}

		StringBuilder assistantTextBuilder = new StringBuilder();
		StringBuilder thinkingBuilder = new StringBuilder();
		List<SemanticToolCall> toolCalls = new List<SemanticToolCall>();

		foreach (JsonNode? item in output)
		{
			if (item == null)
				continue;

			string? type = item["type"]?.GetValue<string>();
			if (type == "function_call")
			{
				string id = item["call_id"]?.GetValue<string>() ?? item["id"]?.GetValue<string>() ?? string.Empty;
				string name = item["name"]?.GetValue<string>() ?? string.Empty;
				string args = item["arguments"]?.GetValue<string>() ?? string.Empty;
				toolCalls.Add(new SemanticToolCall { Id = id, Name = name, ArgumentsJson = args });
			}
			else if (type == "message")
			{
				JsonArray? content = item["content"]?.AsArray();
				if (content != null)
				{
					foreach (JsonNode? block in content)
					{
						string? blockType = block?["type"]?.GetValue<string>();
						string? text = block?["text"]?.GetValue<string>();
						if (blockType == "output_text" && !string.IsNullOrEmpty(text))
						{
							assistantTextBuilder.Append(text);
						}
					}
				}
			}
			else if (type == "reasoning")
			{
				JsonArray? content = item["content"]?.AsArray();
				if (content != null)
				{
					foreach (JsonNode? block in content)
					{
						string? text = block?["text"]?.GetValue<string>();
						if (!string.IsNullOrEmpty(text))
							thinkingBuilder.Append(text);
					}
				}
			}
		}

		string assistantText = assistantTextBuilder.ToString();
		string thinking = thinkingBuilder.ToString();



		// Capture the server response id for next-turn chaining, then clear rehydrated input
		// and delta buffer so subsequent turns accumulate fresh deltas.
		// The id is in-memory only and never written into canonical state.
		_previousResponseId = responseRoot["id"]?.GetValue<string>();
		_rehydratedInput = null;
		_deltaInput.Clear();

		// The Responses API reports output clipping via incomplete_details rather than a finish
		// reason; normalize it to "length" so callers detect cut-off replies uniformly.
		string finishReason = toolCalls.Count > 0 ? "tool_calls" : "stop";
		string? incompleteReason = responseRoot["incomplete_details"]?["reason"]?.GetValue<string>();
		if (toolCalls.Count == 0 && incompleteReason == "max_output_tokens")
			finishReason = "length";

		(TokenUsageInfo usage, decimal cost) = ExtractUsage(responseRoot, model);

		List<ToolResult> emptyResults = new List<ToolResult>();
		return ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, thinking, toolCalls, emptyResults, finishReason, usage, cost));
	}

	private static (TokenUsageInfo usage, decimal cost) ExtractUsage(JsonNode responseRoot, LlmModel model)
	{
		TokenUsageInfo usage = new TokenUsageInfo();
		decimal cost = 0m;
		JsonNode? usageNode = responseRoot["usage"];
		if (usageNode == null)
			return (usage, cost);

		int totalInputTokens = usageNode["input_tokens"]?.GetValue<int>() ?? 0;
		usage.CompletionTokens = usageNode["output_tokens"]?.GetValue<int>() ?? 0;

		int cachedTokens = usageNode["input_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0;

		// input_tokens already INCLUDES cached tokens — this is the full context the provider processed
		usage.PromptTokens = totalInputTokens;
		usage.CachedTokens = cachedTokens;

		// Prefer a server-reported cost when present; otherwise calculate from fresh token counts.
		decimal? reported = null;
		JsonNode? costNode = usageNode["cost"];
		if (costNode is JsonValue cv && cv.TryGetValue<decimal>(out decimal dv))
		{
			reported = dv;
		}

		if (reported.HasValue)
		{
			cost = reported.Value;
		}
		else
		{
			int freshInputTokens = totalInputTokens - cachedTokens;
			cost += (freshInputTokens / 1_000_000m) * model.Config.Cost.Input;
			cost += (cachedTokens / 1_000_000m) * model.Config.Cost.CacheRead;
			cost += (usage.CompletionTokens / 1_000_000m) * model.Config.Cost.Output;
		}

		return (usage, cost);
	}

}