using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// -- OpenAI Chat Completions API -----------------------------------------------
// Wire protocol requires strict userâ†’assistant alternation; two consecutive
// user messages will cause a 400 error. The system message, if present,
// should be the first entry in the array. Tool call results must use the
// "tool" role (not "user") and must immediately follow the assistant message
// that issued the tool call - the ordering is load-bearing.
//
// This protocol maintains its own separate copy of messages (_native) that are sent to the
// server each turn. It must never retain thinking blocks. The native state is rebuilt from
// canonical by Rehydrate and kept in sync through the IProtocolListener events. Thinking is
// intentionally stripped because the server should not see unsigned thinking blocks.
public class ProtocolChatCompletions
{
	private bool _parallelToolCallsSupported = true;
	private bool _streamingSupported = true;

	// Server-stated affordable completion cap, learned from an OpenRouter 402 ("can only afford
	// N tokens"). 0 = no cap known. Applied to every subsequent request of this instance so a
	// low credit balance shrinks max_tokens instead of killing the model outright.
	private int _affordableMaxTokens;

	// The credit alert is raised once per protocol instance — the first clamp tells the human;
	// subsequent shrinks only update the status line.
	private bool _creditAlertSent;

	// Backends disagree on how reasoning effort is requested, so it is advertised softly with adaptive
	// fallback (see TryAdaptToError): 0 = the OpenAI-standard "reasoning_effort" string, 1 = a "reasoning":
	// { "effort": ... } object (OpenRouter and several gateways), 2 = give up because the server accepts
	// neither. Each step is taken only after the server rejects the previous form with a 400.
	private int _reasoningMode = 0;

	// Servers disagree on how a forced tool call is requested. Advanced adaptively by ExecuteAsync when a
	// forced turn returns no tool call: 0 = the OpenAI object form ({"type":"function","function":{"name":X}}),
	// 1 = the "required" string honored by llama.cpp and others that reject the object form and silently fall
	// back to "auto". Unlike _reasoningMode the rejection is not a 400 — the server answers 200 with no call —
	// so it is detected by the missing call, not an error. Cached for the life of this protocol instance so the
	// working form is reused on later turns of the same session.
	private int _toolChoiceMode = 0;

	// Native runtime state: the message chain that will be sent to the server. Thinking blocks
	// are never included. This is in-memory only and rebuilt from canonical by Rehydrate.
	private readonly JsonArray _native = new JsonArray();

	// Rebuilds the native message chain from canonical. Thinking is intentionally dropped.
	// Called by ProtocolProxy right after creating or switching in.
	public void Rehydrate(IReadOnlyList<CanonicalMessage> messages)
	{
		_native.Clear();
		foreach (CanonicalMessage msg in messages)
		{
			JsonObject? native = ToNativeMessage(msg);
			if (native != null)
				_native.Add((JsonNode)native);
		}
	}

	// Converts a typed canonical message to an OpenAI ChatCompletions wire object.
	// Returns null for message types that have no native representation (none currently).
	private static JsonObject? ToNativeMessage(CanonicalMessage msg)
	{
		if (msg is SystemMessage sm)
		{
			JsonObject obj = new JsonObject();
			obj["role"] = "system";
			obj["content"] = sm.Text;
			return obj;
		}
		if (msg is UserMessage um)
		{
			JsonObject obj = new JsonObject();
			obj["role"] = "user";
			obj["content"] = um.Text;
			return obj;
		}
		if (msg is AssistantMessage am)
		{
			JsonObject obj = new JsonObject();
			obj["role"] = "assistant";
			obj["content"] = am.Text ?? string.Empty;
			if (am.ToolCalls.Count > 0)
			{
				JsonArray tcArr = new JsonArray();
				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					JsonObject tcObj = new JsonObject();
					tcObj["id"] = tc.Id;
					tcObj["type"] = "function";
					JsonObject fn = new JsonObject();
					fn["name"] = tc.Name;
					fn["arguments"] = tc.ArgumentsJson;
					tcObj["function"] = fn;
					tcArr.Add((JsonNode)tcObj);
				}
				obj["tool_calls"] = tcArr;
			}
			return obj;
		}
		if (msg is ToolResultMessage tr)
		{
			JsonObject obj = new JsonObject();
			obj["role"] = "tool";
			obj["content"] = tr.Content;
			obj["tool_call_id"] = tr.ToolCallId;
			return obj;
		}
		return null;
	}

	public void OnSystemMessage(string text)
	{
		// If a system message already exists at the head, update it in-place.
		if (_native.Count > 0 && _native[0]?["role"]?.GetValue<string>() == "system")
		{
			_native[0]!["content"] = text;
			return;
		}

		JsonObject msg = new JsonObject();
		msg["role"] = "system";
		msg["content"] = text;
		_native.Insert(0, msg);
	}

	public void OnUserMessage(string text)
	{
		// If the last item is already a user message, merge text in-place.
		int count = _native.Count;
		if (count > 0)
		{
			JsonNode? last = _native[count - 1];
			if (last != null && last["role"]?.GetValue<string>() == "user")
			{
				string? existing = last["content"]?.GetValue<string>();
				last["content"] = string.IsNullOrEmpty(existing) ? text : existing + "\n" + text;
				return;
			}
		}

		JsonObject msg = new JsonObject();
		msg["role"] = "user";
		msg["content"] = text;
		_native.Add((JsonNode)msg);
	}

	// A completed assistant turn from replay or another protocol. Thinking is dropped because
	// it must never be sent to the server.
	public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
	{
		JsonObject msg = new JsonObject();
		msg["role"] = "assistant";
		msg["content"] = text ?? string.Empty;

		if (toolCalls.Count > 0)
		{
			JsonArray tcArr = new JsonArray();
			foreach (SemanticToolCall tc in toolCalls)
			{
				JsonObject tcObj = new JsonObject();
				tcObj["id"] = tc.Id;
				tcObj["type"] = "function";
				JsonObject fn = new JsonObject();
				fn["name"] = tc.Name;
				fn["arguments"] = tc.ArgumentsJson;
				tcObj["function"] = fn;
				tcArr.Add((JsonNode)tcObj);
			}
			msg["tool_calls"] = tcArr;
		}

		_native.Add((JsonNode)msg);
	}

	public void OnToolResult(ToolResult result)
	{
		JsonObject msg = new JsonObject();
		msg["role"] = "tool";
		string content = result.StdOut;
		if (!string.IsNullOrEmpty(result.StdErr))
		{
			content = content + "\nstderr: " + result.StdErr;
		}
		msg["content"] = content;
		msg["tool_call_id"] = result.Id;
		_native.Add((JsonNode)msg);
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
			bool logged = false;

			// A successful response that carries neither assistant text nor a tool call is a dead turn: the
			// model emitted only thinking — typically an XML tool call buried in its reasoning that this
			// protocol never parsed as a real call. Some local/open ChatCompletions models do this; the
			// hosted Anthropic and Responses protocols effectively never do, which is why this lives here.
			// Treat it like the other fixable malformations handled in this loop: discard it and re-post (a
			// fresh seed is generated each build, so the retry is a genuinely new sample) up to the cap,
			// rather than surfacing an empty turn the caller cannot act on.
			int emptyRetries = 0;
			const int kMaxEmptyRetries = 3;

			for (; ; )
			{
				JsonObject body = BuildRequestBody(model, tools, forcedToolName, maxCompletionTokens);
				if (!logged)
				{ logged = true; logger.Write(model.Config.Name, model.Endpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }

				if (_streamingSupported)
				{
					ProtocolResult? streamResult = await ExecuteStreamingAsync(model, body, tools, extraHeaders, extraPayload, bundle, onProgress, logger, cancellationToken);
					if (streamResult != null)
					{
						if (ShouldAdaptToolChoice(streamResult, forcedToolName, tools))
						{
							_toolChoiceMode = 1;
							bundle.Transport?.Status("Forced tool call not honored; retrying with tool_choice=required");
							continue;
						}
						if (IsEmptyTurn(streamResult) && emptyRetries < kMaxEmptyRetries)
						{
							emptyRetries++;
							bundle.Transport?.Status($"Empty response, retrying ({emptyRetries}/{kMaxEmptyRetries})");
							continue;
						}
						return streamResult;
					}
					// null means the provider rejected streaming; fall through to non-streaming
				}

				HttpResponseMessage httpResponse;
				string responseBody;
				try
				{
					httpResponse = await PostAsync(model, body, extraHeaders, extraPayload, cancellationToken);
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
					return logger.ProtocolFailure(
						ProtocolResult.Transient(ex.ToString(), null),
						model, DetectedProtocol.ChatCompletions, "NetworkError",
						ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null, ex.Message, null, ex);
				}
				catch (Exception ex)
				{
					return logger.ProtocolFailure(
						ProtocolResult.Transient(ex.ToString(), null),
						model, DetectedProtocol.ChatCompletions, "Exception", null, ex.Message, null, ex);
				}

				if (httpResponse.IsSuccessStatusCode)
				{
					JsonNode? root = JsonNode.Parse(responseBody);
					if (root == null)
					{
						return ProtocolResult.Transient("Empty response from API", null);
					}

					string? errMsg = root["error"]?["message"]?.GetValue<string>();
					JsonArray? choices = root["choices"]?.AsArray();
					if (choices == null || choices.Count == 0 || errMsg != null)
					{
						return ProtocolResult.Transient(errMsg ?? "Empty response from API", null);
					}

					JsonNode? messageNode = choices[0]?["message"];
					if (messageNode is not JsonObject messageObj)
					{
						return ProtocolResult.Transient("Response missing message object", null);
					}

					(string assistantText, List<SemanticToolCall> toolCalls) = ExtractSemantic(messageObj);
					string thinking = ExtractThinking(messageObj);

					// Salvage tool calls a template-mismatched local model emitted as literal
					// <tool_call> text instead of the native tool_calls array. Only blocks naming
					// a tool offered this turn are extracted; the rest remain prose.
					if (toolCalls.Count == 0)
						assistantText = ExtractXmlToolCalls(assistantText, tools, toolCalls);

					string finishReason = choices[0]?["finish_reason"]?.GetValue<string>() ?? string.Empty;
					if (toolCalls.Count > 0)
						finishReason = "tool_calls";

					(TokenUsageInfo usage, decimal cost) = ExtractUsage(root, model);

					List<ToolResult> emptyResults = new List<ToolResult>();
					ProtocolResult success = ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, thinking, toolCalls, emptyResults, finishReason, usage, cost));
					if (ShouldAdaptToolChoice(success, forcedToolName, tools))
					{
						_toolChoiceMode = 1;
						bundle.Transport?.Status("Forced tool call not honored; retrying with tool_choice=required");
						continue;
					}
					if (IsEmptyTurn(success) && emptyRetries < kMaxEmptyRetries)
					{
						emptyRetries++;
						bundle.Transport?.Status($"Empty response, retrying ({emptyRetries}/{kMaxEmptyRetries})");
						continue;
					}
					return success;
				}

				bool reasoningConfigured = ReasoningEffort.OpenAiEffort(model.Config.ReasoningEffort) != null;
				if (TryAdaptToError(httpResponse, responseBody, reasoningConfigured))
				{
					continue;
				}

				// OpenRouter credit gating: a 402 naming the affordable completion size is a SIZING
				// problem, not a dead model — treating it as permanent failure cascaded every paid
				// model onto the free fallback the moment credits ran low. Adopt the server-stated
				// cap (minus margin, since the balance keeps draining) and retry. Only a strictly
				// smaller cap re-arms the retry, so the loop always converges.
				if ((int)httpResponse.StatusCode == 402)
				{
					int affordable = ParseAffordableTokens(responseBody);
					int capped = affordable - affordable / 10;
					if (capped > 0 && (_affordableMaxTokens == 0 || capped < _affordableMaxTokens))
					{
						_affordableMaxTokens = capped;
						if (!_creditAlertSent)
						{
							_creditAlertSent = true;
							bundle.Transport?.Alert(
								$"Provider credits are nearly exhausted: {model.Config.Name} can only afford ~{affordable} output tokens per request. "
								+ "Work continues with clamped responses, but quality and length will suffer until a human adds credits "
								+ "(for OpenRouter: https://openrouter.ai/settings/credits).");
						}
						bundle.Transport?.Status($"Provider credits limit completion size (~{affordable} tokens); retrying with max_tokens={capped}");
						continue;
					}
				}

				if (ProtocolHelpers.IsRateLimited(httpResponse, responseBody))
				{
					string rateLimitMessage = string.IsNullOrEmpty(responseBody)
						? $"HTTP {(int)httpResponse.StatusCode} with empty response body. Endpoint: {model.Endpoint}"
						: responseBody;
					return logger.ProtocolFailure(
						ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, responseBody)),
						model, DetectedProtocol.ChatCompletions, "RateLimited",
						(int)httpResponse.StatusCode, rateLimitMessage, responseBody, null);
				}

				int statusCode = (int)httpResponse.StatusCode;
				// A 4xx other than the 429 handled above (and the genuinely retryable 408/425) is a permanent
				// client error: the request itself is bad, so retrying just burns the transient budget and then
				// surfaces as a misleading "rate limited". Fail fast with the body so the real cause is visible;
				// 5xx and the retryable 4xx stay transient.
				if (ProtocolHelpers.IsPermanentClientError(statusCode))
				{
					if (ProtocolHelpers.IsContextOverflow(responseBody))
					{
						return ProtocolHelpers.ContextOverflowFailure("ChatCompletions", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId);
					}
					return ProtocolHelpers.Failure("ChatCompletions", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId);
				}
				return ProtocolHelpers.TransientFailure("ChatCompletions", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId,
httpResponse);
			}
		}
		catch (Exception ex)
		{
			return logger.ProtocolFailure(
				ProtocolResult.Transient(ex.ToString(), null),
				model, DetectedProtocol.ChatCompletions, "Exception", null, ex.Message, null, ex);
		}
	}

	private JsonObject BuildRequestBody(LlmModel model, List<ToolDefinition> tools, string? forcedToolName, int? maxCompletionTokens)
	{
		JsonObject body = new JsonObject();
		body["model"] = model.Config.Id;

		// System prompts arrive through the bundle as semantic events (see BeastSession.RaiseSystemPrompt),
		// so the messages array is sent verbatim with no prepended global system block.
		JsonArray messages = new JsonArray();

		foreach (JsonNode? n in _native)
		{
			if (n != null)
				messages.Add(n.DeepClone());
		}

		// If the last message is neither user nor tool, append an empty user turn so the API will respond.
		if (messages.Count > 0)
		{
			JsonNode? last = messages[messages.Count - 1];
			string? lastRole = last?["role"]?.GetValue<string>();
			if (lastRole != "user" && lastRole != "tool")
			{
				JsonObject filler = new JsonObject();
				filler["role"] = "user";
				filler["content"] = string.Empty;
				messages.Add((JsonNode)filler);
			}
		}

		body["messages"] = messages;

		if (tools.Count > 0)
		{
			// Hand-built nodes (no reflection serialization) so the path stays Native-AOT clean.
			JsonArray toolsArr = new JsonArray();
			foreach (ToolDefinition td in tools)
			{
				JsonObject fn = new JsonObject();
				fn["name"] = td.Function.Name;
				fn["description"] = td.Function.Description;
				fn["parameters"] = td.Function.Parameters.DeepClone();

				JsonObject toolObj = new JsonObject();
				toolObj["type"] = td.Type;
				toolObj["function"] = fn;
				toolsArr.Add((JsonNode)toolObj);
			}
			body["tools"] = toolsArr;

			// Force a specific tool when asked, require any tool for the AnyTool sentinel; otherwise
			// leave choice to the model. A forced single tool implies one call, so parallel tool
			// calls are not advertised in that case.
			if (forcedToolName == ProtocolProxy.AnyTool)
			{
				body["tool_choice"] = "required";
			}
			else if (!string.IsNullOrEmpty(forcedToolName))
			{
				// Force a specific tool. Servers encode this differently and some (llama.cpp) reject the
				// OpenAI object form outright — they expect a plain string and silently fall back to "auto"
				// otherwise, dropping the call. _toolChoiceMode (advanced by ExecuteAsync when a forced turn
				// produces no call) picks the form: mode 0 is the object form, mode 1 the "required" string.
				// With only one tool on offer the object form disambiguates nothing that "required" doesn't,
				// so start straight at the string and skip a wasted round trip.
				if (_toolChoiceMode == 0 && tools.Count > 1)
				{
					JsonObject fn = new JsonObject();
					fn["name"] = forcedToolName;
					JsonObject choice = new JsonObject();
					choice["type"] = "function";
					choice["function"] = fn;
					body["tool_choice"] = choice;
				}
				else
				{
					body["tool_choice"] = "required";
				}
			}
			else if (_parallelToolCallsSupported)
			{
				body["parallel_tool_calls"] = true;
			}
		}

		body["seed"] = Random.Shared.Next();
		// The learned affordable cap (from a credit-gating 402) tightens whatever the caller asked
		// for, and applies even when the caller left the size unbounded.
		if (maxCompletionTokens.HasValue)
		{
			int cap = maxCompletionTokens.Value;
			if (_affordableMaxTokens > 0 && _affordableMaxTokens < cap)
				cap = _affordableMaxTokens;
			body["max_completion_tokens"] = cap;
		}
		else if (_affordableMaxTokens > 0)
		{
			body["max_completion_tokens"] = _affordableMaxTokens;
		}

		// Advertise the friendly reasoningEffort word in whichever form the backend currently accepts.
		// _reasoningMode is advanced by TryAdaptToError as forms are rejected; mode 2 sends nothing.
		string? effort = ReasoningEffort.OpenAiEffort(model.Config.ReasoningEffort);
		if (effort != null)
		{
			if (_reasoningMode == 0)
			{
				body["reasoning_effort"] = effort;
			}
			else if (_reasoningMode == 1)
			{
				JsonObject reasoning = new JsonObject();
				reasoning["effort"] = effort;
				body["reasoning"] = reasoning;
			}
		}

		return body;
	}

	// Pulls the token count out of OpenRouter's credit-gating 402 message ("...but can only
	// afford 6546. To increase..."). Returns 0 when the phrase is absent or malformed.
	private static int ParseAffordableTokens(string responseBody)
	{
		const string marker = "can only afford ";
		int result = 0;
		int idx = responseBody.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		if (idx >= 0)
		{
			int start = idx + marker.Length;
			int end = start;
			while (end < responseBody.Length && char.IsDigit(responseBody[end]))
				end++;
			if (end > start)
				int.TryParse(responseBody.Substring(start, end - start), out result);
		}
		return result;
	}

	private async Task<HttpResponseMessage> PostAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, Dictionary<string, JsonNode?> extraPayload, CancellationToken cancellationToken)
	{
		string url = model.Endpoint;

		JsonObject obj = (JsonObject)body.DeepClone();
		foreach ((string name, JsonNode? value) in extraPayload)
		{
			obj[name] = value?.DeepClone();
		}

		string requestJson = obj.ToJsonString();

		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
		req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
		req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");

		foreach ((string name, string value) in extraHeaders)
		{
			req.Headers.TryAddWithoutValidation(name, value);
		}

		return await ProtocolHelpers.GetClient().SendAsync(req, cancellationToken);
	}

	private async Task<ProtocolResult?> ExecuteStreamingAsync(
		LlmModel model,
		JsonObject body,
		List<ToolDefinition> tools,
		Dictionary<string, string> extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		ListenerBundle bundle,
		LiveUsageProgress onProgress,
		SessionLogger logger,
		CancellationToken cancellationToken)
	{
		string url = model.Endpoint;

		JsonObject obj = (JsonObject)body.DeepClone();
		obj["stream"] = true;
		obj["stream_options"] = new JsonObject { ["include_usage"] = true };

		foreach ((string name, JsonNode? value) in extraPayload)
		{
			obj[name] = value?.DeepClone();
		}

		string requestJson = obj.ToJsonString();

		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
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
			return logger.ProtocolFailure(
				ProtocolResult.Transient(ex.ToString(), null),
				model, DetectedProtocol.ChatCompletions, "NetworkError",
				ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null, ex.Message, null, ex);
		}
		catch (Exception ex)
		{
			return logger.ProtocolFailure(
				ProtocolResult.Transient(ex.ToString(), null),
				model, DetectedProtocol.ChatCompletions, "Exception", null, ex.Message, null, ex);
		}

		if (!httpResponse.IsSuccessStatusCode)
		{
			string errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
			int statusCode = (int)httpResponse.StatusCode;
			string errorLogMessage = string.IsNullOrEmpty(errorBody)
				? $"HTTP {statusCode} with empty response body. Endpoint: {url}"
				: errorBody;

			// A 4xx other than the 429 handled above is a permanent client error in the streaming path:
			// the provider rejects streaming for this model, so we disable it and fall through to
			// non-streaming. Note: unlike the non-streaming path (which excludes 408/425 as retryable),
			// any non-429 4xx here means streaming is not supported — the caller retries non-streaming.
			if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
			{
				// Exception: a credit-gating 402 is a request-SIZING rejection, not a streaming
				// capability signal. Keep streaming enabled; the non-streaming fallback surfaces
				// the same 402 to the main loop's affordable-cap adaptation, and the resized
				// retry streams again.
				if (statusCode != 402 || ParseAffordableTokens(errorBody) == 0)
					_streamingSupported = false;
				logger.ProtocolFailure(
					model, DetectedProtocol.ChatCompletions,
					statusCode == 401 || statusCode == 403 ? "AuthFailure" : "ClientError",
					statusCode, errorLogMessage, errorBody, null);
				return null;
			}

			if (ProtocolHelpers.IsRateLimited(httpResponse, errorBody))
			{
				return logger.ProtocolFailure(
					ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, errorBody)),
					model, DetectedProtocol.ChatCompletions, "RateLimited",
					statusCode, errorLogMessage, errorBody, null);
			}

			logger.ProtocolFailure(
				model, DetectedProtocol.ChatCompletions,
				statusCode >= 500 ? "ServerError" : "Transient",
				statusCode, errorLogMessage, errorBody, null);
			return ProtocolResult.Transient($"HTTP {statusCode}: {errorBody}", ProtocolHelpers.TryGetRetryAfter(httpResponse, errorBody));
		}

		StringBuilder contentBuilder = new StringBuilder();
		StringBuilder reasoningBuilder = new StringBuilder();
		List<StreamingToolCall> toolCallAccumulators = new List<StreamingToolCall>();
		string finishReason = string.Empty;
		JsonNode? usageNodeFinal = null;
		string? openStreamTag = null;
		int streamedCharCount = 0;
		int livePromptTokens = 0;
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

					JsonNode? chunkNode = JsonNode.Parse(data);
					if (chunkNode == null)
						continue;

					JsonNode? usageNode = chunkNode["usage"];
					if (usageNode != null)
					{
						usageNodeFinal = usageNode;
						int? promptTokens = usageNode["prompt_tokens"]?.GetValue<int?>();
						if (promptTokens.HasValue && promptTokens.Value > 0)
						{
							livePromptTokens = promptTokens.Value;
						}
						int? cached = usageNode["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int?>();
						if (cached.HasValue && cached.Value > 0)
						{
							liveCachedTokens = cached.Value;
						}
					}

					JsonArray? choices = chunkNode["choices"]?.AsArray();
					if (choices == null || choices.Count == 0)
						continue;

					JsonNode? delta = choices[0]?["delta"];
					if (delta == null)
						continue;

					string? fr = choices[0]?["finish_reason"]?.GetValue<string>();
					if (fr != null)
						finishReason = fr;

					string? reasoningDelta = delta["reasoning_content"]?.GetValue<string>()
										  ?? delta["reasoning"]?.GetValue<string>();
					if (!string.IsNullOrEmpty(reasoningDelta))
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

						reasoningBuilder.Append(reasoningDelta);
						bundle.Transport?.OnStreamChunk(StreamTag.Thinking, reasoningDelta);
						streamedCharCount += reasoningDelta.Length;
						EmitProgress(model, livePromptTokens, streamedCharCount, onProgress, liveCachedTokens);
					}

					string? contentDelta = delta["content"]?.GetValue<string>();
					if (!string.IsNullOrEmpty(contentDelta))
					{
						// Always accumulate the committed text and progress; only gate what reaches the client.
						contentBuilder.Append(contentDelta);
						streamedCharCount += contentDelta.Length;
						EmitProgress(model, livePromptTokens, streamedCharCount, onProgress, liveCachedTokens);

						// Don't open the assistant output block on leading whitespace: a thinking+tool-call
						// turn that emits a stray newline would otherwise leave an empty block. Wait for the
						// first non-whitespace text; once open, stream every delta including whitespace.
						bool assistantOpen = openStreamTag == StreamTag.Assistant;
						if (assistantOpen || !string.IsNullOrWhiteSpace(contentDelta))
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
							bundle.Transport?.OnStreamChunk(StreamTag.Assistant, contentDelta);
						}
					}

					JsonArray? tcDeltas = delta["tool_calls"]?.AsArray();
					if (tcDeltas != null)
					{
						foreach (JsonNode? tcNode in tcDeltas)
						{
							if (tcNode == null)
								continue;
							int index = tcNode["index"]?.GetValue<int>() ?? 0;

							while (toolCallAccumulators.Count <= index)
							{
								toolCallAccumulators.Add(new StreamingToolCall());
							}

							StreamingToolCall acc = toolCallAccumulators[index];
							if (tcNode["id"] != null)
								acc.Id = tcNode["id"]!.GetValue<string>();
							if (tcNode["function"]?["name"] != null)
								acc.Name = tcNode["function"]!["name"]!.GetValue<string>();
							string? argDelta = tcNode["function"]?["arguments"]?.GetValue<string>();
							if (argDelta != null)
								acc.Arguments.Append(argDelta);
						}
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

		string assistantText = contentBuilder.ToString();
		string thinking = reasoningBuilder.ToString();

		List<SemanticToolCall> semanticToolCalls = new List<SemanticToolCall>();
		if (toolCallAccumulators.Count > 0)
		{
			foreach (StreamingToolCall acc in toolCallAccumulators)
			{
				semanticToolCalls.Add(new SemanticToolCall { Id = acc.Id, Name = acc.Name, ArgumentsJson = acc.Arguments.ToString() });
			}
			finishReason = "tool_calls";
		}

		// Salvage tool calls a template-mismatched local model emitted as literal <tool_call>
		// text instead of the native tool_calls array. Only blocks naming a tool offered this
		// turn are extracted; the rest remain prose. (The raw text already streamed to the
		// display; the committed turn carries the cleaned text and the real calls.)
		if (semanticToolCalls.Count == 0)
		{
			assistantText = ExtractXmlToolCalls(assistantText, tools, semanticToolCalls);
			if (semanticToolCalls.Count > 0)
				finishReason = "tool_calls";
		}



		(TokenUsageInfo tokenUsage, decimal cost) = ExtractUsageFromNode(usageNodeFinal, model);

		List<ToolResult> emptyResults = new List<ToolResult>();
		return ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, thinking, semanticToolCalls, emptyResults, finishReason, tokenUsage, cost));
	}

	private sealed class StreamingToolCall
	{
		public string Id = string.Empty;
		public string Name = string.Empty;
		public StringBuilder Arguments = new StringBuilder();
	}

	// Emits live usage progress during streaming. ChatCompletions reports prompt_tokens in
	// the usage object (when stream_options.include_usage is set), typically in the final
	// chunk. Until then, livePromptTokens is 0. Output tokens are estimated from streamed
	// character count (chars/4). The committed usage will correct both at end-of-turn.
	private static void EmitProgress(LlmModel model, int livePromptTokens, int streamedCharCount, LiveUsageProgress onProgress, int liveCachedTokens = 0)
	{
		int estimatedOutputTokens = streamedCharCount / 4;
		decimal estimatedCost = (livePromptTokens / 1_000_000m) * model.Config.Cost.Input
							  + (estimatedOutputTokens / 1_000_000m) * model.Config.Cost.Output;
		onProgress(livePromptTokens, estimatedOutputTokens, estimatedCost, liveCachedTokens);
	}

	// A successful turn that produced no tool call and no assistant text — only thinking, or nothing.
	// There is nothing for the caller to act on, so the loop discards and re-posts it.
	private static bool IsEmptyTurn(ProtocolResult result)
	{
		return result.Outcome == ProtocolCallOutcome.Success
			&& result.Payload != null
			&& result.Payload.ToolCalls.Count == 0
			&& string.IsNullOrWhiteSpace(result.Payload.AssistantText);
	}

	// A forced turn that returned no tool call means the server did not honor the tool_choice form — some
	// accept only the "required" string and silently drop the OpenAI object form to "auto". True only while
	// an unused encoding remains: the object form (mode 0, more than one tool) can still fall back to
	// "required". AnyTool and the single-tool case already send "required", so there is nothing left to try.
	private bool ShouldAdaptToolChoice(ProtocolResult result, string? forcedToolName, List<ToolDefinition> tools)
	{
		return _toolChoiceMode == 0
			&& !string.IsNullOrEmpty(forcedToolName)
			&& forcedToolName != ProtocolProxy.AnyTool
			&& tools.Count > 1
			&& result.Outcome == ProtocolCallOutcome.Success
			&& result.Payload != null
			&& result.Payload.ToolCalls.Count == 0;
	}

	// Pulls semantic content and tool calls out of a non-streaming message object.
	// Some local models (llama-server with a mismatched chat template, Hermes/Qwen formats) print
	// their tool calls as literal text — <tool_call>{"name":"x","arguments":{...}}</tool_call> —
	// instead of populating the native tool_calls array. Extracts a block into toolCalls (with a
	// synthesized id) ONLY when it is well-formed AND names a tool actually offered this turn;
	// everything else — malformed blocks, unknown tool names, quoted examples — stays in the text
	// verbatim. Never optimistically extract and then error: a block that doesn't match a real
	// tool is treated as prose, not as a failed call.
	private static string ExtractXmlToolCalls(string text, List<ToolDefinition> tools, List<SemanticToolCall> toolCalls)
	{
		const string open = "<tool_call>";
		const string close = "</tool_call>";

		string remaining = text;
		if (remaining.Contains(open, StringComparison.Ordinal))
		{
			StringBuilder cleaned = new StringBuilder();
			int pos = 0;
			for (; ; )
			{
				int start = remaining.IndexOf(open, pos, StringComparison.Ordinal);
				if (start < 0)
				{
					cleaned.Append(remaining, pos, remaining.Length - pos);
					break;
				}
				int end = remaining.IndexOf(close, start + open.Length, StringComparison.Ordinal);
				if (end < 0)
				{
					cleaned.Append(remaining, pos, remaining.Length - pos);
					break;
				}

				string inner = remaining.Substring(start + open.Length, end - start - open.Length).Trim();
				SemanticToolCall? call = ParseXmlToolCall(inner);
				if (call != null && IsOfferedTool(tools, call.Name))
				{
					toolCalls.Add(call);
					cleaned.Append(remaining, pos, start - pos);
				}
				else
				{
					// Unparseable or not a real tool: keep the block verbatim as ordinary text.
					cleaned.Append(remaining, pos, end + close.Length - pos);
				}
				pos = end + close.Length;
			}
			remaining = cleaned.ToString().Trim();
		}
		return remaining;
	}

	// Exact-name membership in this turn's advertised tool set — salvage never fuzzy-matches.
	private static bool IsOfferedTool(List<ToolDefinition> tools, string name)
	{
		foreach (ToolDefinition td in tools)
		{
			if (string.Equals(td.Function.Name, name, StringComparison.Ordinal))
				return true;
		}
		return false;
	}

	// Parses one <tool_call> body in either dialect local templates emit:
	//   Hermes/JSON:  {"name": "x", "arguments": {...}}
	//   Qwen-Coder:   <function=x> <parameter=key>\nvalue\n</parameter> ... </function>
	// Returns null when neither shape matches.
	private static SemanticToolCall? ParseXmlToolCall(string inner)
	{
		SemanticToolCall? call = null;
		if (inner.StartsWith("{", StringComparison.Ordinal))
		{
			try
			{
				JsonNode? node = JsonNode.Parse(inner);
				string? name = node?["name"]?.GetValue<string>();
				if (node is JsonObject && !string.IsNullOrEmpty(name))
				{
					JsonNode? args = node["arguments"];
					string argsJson;
					if (args is JsonObject argsObj)
						argsJson = argsObj.ToJsonString();
					else if (args is JsonValue value && value.TryGetValue(out string? argsText))
						argsJson = argsText ?? "{}";
					else
						argsJson = "{}";

					call = MakeXmlCall(name!, argsJson);
				}
			}
			catch (JsonException)
			{
			}
		}
		else
		{
			call = ParseFunctionXmlToolCall(inner);
		}
		return call;
	}

	// The Qwen-Coder function form: <function=NAME> then <parameter=KEY> blocks whose value is the
	// raw text up to </parameter>. Exactly one leading and trailing newline is stripped from each
	// value (the template's framing) so multi-line content like file bodies survives verbatim.
	private static SemanticToolCall? ParseFunctionXmlToolCall(string inner)
	{
		const string functionOpen = "<function=";
		const string parameterOpen = "<parameter=";
		const string parameterClose = "</parameter>";

		SemanticToolCall? call = null;
		int fnStart = inner.IndexOf(functionOpen, StringComparison.Ordinal);
		int fnNameEnd = fnStart >= 0 ? inner.IndexOf('>', fnStart + functionOpen.Length) : -1;
		if (fnNameEnd > 0)
		{
			string name = inner.Substring(fnStart + functionOpen.Length, fnNameEnd - fnStart - functionOpen.Length).Trim();
			if (name.Length > 0)
			{
				JsonObject args = new JsonObject();
				int pos = fnNameEnd + 1;
				for (; ; )
				{
					int pStart = inner.IndexOf(parameterOpen, pos, StringComparison.Ordinal);
					if (pStart < 0)
						break;
					int pNameEnd = inner.IndexOf('>', pStart + parameterOpen.Length);
					if (pNameEnd < 0)
						break;
					int pClose = inner.IndexOf(parameterClose, pNameEnd + 1, StringComparison.Ordinal);
					if (pClose < 0)
						break;

					string key = inner.Substring(pStart + parameterOpen.Length, pNameEnd - pStart - parameterOpen.Length).Trim();
					string value = inner.Substring(pNameEnd + 1, pClose - pNameEnd - 1);
					if (value.StartsWith("\n", StringComparison.Ordinal))
						value = value.Substring(1);
					if (value.EndsWith("\n", StringComparison.Ordinal))
						value = value.Substring(0, value.Length - 1);
					if (key.Length > 0)
						args[key] = value;

					pos = pClose + parameterClose.Length;
				}
				call = MakeXmlCall(name, args.ToJsonString());
			}
		}
		return call;
	}

	private static SemanticToolCall MakeXmlCall(string name, string argsJson)
	{
		return new SemanticToolCall
		{
			Id = $"xmltc_{Guid.NewGuid():N}".Substring(0, 24),
			Name = name,
			ArgumentsJson = argsJson
		};
	}

	private static (string assistantText, List<SemanticToolCall> toolCalls) ExtractSemantic(JsonObject messageObj)
	{
		string text = messageObj["content"]?.GetValue<string>() ?? string.Empty;

		List<SemanticToolCall> tcs = new List<SemanticToolCall>();
		JsonArray? tcArr = messageObj["tool_calls"]?.AsArray();
		if (tcArr != null)
		{
			foreach (JsonNode? n in tcArr)
			{
				if (n == null)
					continue;
				string id = n["id"]?.GetValue<string>() ?? string.Empty;
				string name = n["function"]?["name"]?.GetValue<string>() ?? string.Empty;
				string argsJson = n["function"]?["arguments"]?.GetValue<string>() ?? string.Empty;
				tcs.Add(new SemanticToolCall { Id = id, Name = name, ArgumentsJson = argsJson });
			}
		}

		return (text, tcs);
	}

	private static string ExtractThinking(JsonObject messageObj)
	{
		string? reasoningContent = messageObj["reasoning_content"]?.GetValue<string>();
		if (!string.IsNullOrEmpty(reasoningContent))
			return reasoningContent;

		JsonArray? details = messageObj["reasoning_details"]?.AsArray();
		if (details != null)
		{
			StringBuilder sb = new StringBuilder();
			foreach (JsonNode? item in details)
			{
				string? text = item?["text"]?.GetValue<string>();
				if (!string.IsNullOrEmpty(text))
					sb.Append(text);
			}
			if (sb.Length > 0)
				return sb.ToString();
		}

		return string.Empty;
	}

	private static (TokenUsageInfo usage, decimal cost) ExtractUsage(JsonNode root, LlmModel model)
	{
		return ExtractUsageFromNode(root["usage"], model);
	}

	private static (TokenUsageInfo usage, decimal cost) ExtractUsageFromNode(JsonNode? usageNode, LlmModel model)
	{
		TokenUsageInfo usage = new TokenUsageInfo();
		decimal cost = 0m;
		if (usageNode == null)
			return (usage, cost);

		int totalPromptTokens = usageNode["prompt_tokens"]?.GetValue<int>() ?? 0;
		usage.CompletionTokens = usageNode["completion_tokens"]?.GetValue<int>() ?? 0;

		int cachedTokens = usageNode["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0;

		// prompt_tokens already INCLUDES cached tokens — this is the full context the provider processed
		usage.PromptTokens = totalPromptTokens;
		usage.CachedTokens = cachedTokens;

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
			int freshPromptTokens = totalPromptTokens - cachedTokens;
			cost += (freshPromptTokens / 1_000_000m) * model.Config.Cost.Input;
			cost += (cachedTokens / 1_000_000m) * model.Config.Cost.CacheRead;
			cost += (usage.CompletionTokens / 1_000_000m) * model.Config.Cost.Output;
		}

		return (usage, cost);
	}

	private bool TryAdaptToError(HttpResponseMessage response, string responseBody, bool reasoningConfigured)
	{
		int statusCode = (int)response.StatusCode;
		if (statusCode < 400 || statusCode >= 500 || statusCode == 429)
			return false;

		string lowerBody = responseBody.ToLowerInvariant();

		// A server that rejects the reasoning hint gets the next softer form, then no hint at all. Checked
		// before the parallel-tool guard so a model can adapt reasoning even after parallel calls are off.
		if (reasoningConfigured && _reasoningMode < 2 && lowerBody.Contains("reasoning"))
		{
			_reasoningMode++;
			return true;
		}

		if (!_parallelToolCallsSupported)
			return false;

		if (lowerBody.Contains("parallel_tool_calls") || lowerBody.Contains("parallel tool calls"))
		{
			_parallelToolCallsSupported = false;
			return true;
		}

		if (statusCode == 400 && (lowerBody.Contains("upstream_error") || lowerBody.Contains("provider returned error")))
		{
			_parallelToolCallsSupported = false;
			return true;
		}

		return false;
	}

	// Tracer call: sends the same request with max_completion_tokens=1 to get accurate token counts
	// without generating a meaningful response. Returns token usage info from the provider, or error status.
	public async Task<TracerResult> ExecuteTracerAsync(
		LlmModel model,
		List<ToolDefinition> tools,
		string? forcedToolName,
		Dictionary<string, string> extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		SessionLogger logger,
		CancellationToken cancellationToken)
	{
		try
		{
			JsonObject body = BuildRequestBody(model, tools, forcedToolName, 1);

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
			catch (HttpRequestException ex)
			{
				return TracerResult.Failed(ex.ToString());
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
					return TracerResult.Failed("Empty response from API");

				JsonNode? usageNode = root["usage"];
				if (usageNode == null)
					return TracerResult.Failed("No usage info in tracer response");

				int promptTokens = usageNode["prompt_tokens"]?.GetValue<int>() ?? 0;
				int cachedTokens = usageNode["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0;

				return TracerResult.Success(promptTokens, cachedTokens);
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
		catch (Exception ex)
		{
			return TracerResult.Failed(ex.ToString());
		}
	}

	// No dedicated token-counting endpoint for Chat Completions.
	// Delegates to the existing ExecuteTracerAsync logic (max_completion_tokens=1).
	public async Task<TracerResult> CountTokensAsync(
		LlmModel model,
		List<ToolDefinition> tools,
		string? forcedToolName,
		Dictionary<string, string> extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		SessionLogger logger,
		CancellationToken cancellationToken)
	{
		return await ExecuteTracerAsync(model, tools, forcedToolName, extraHeaders, extraPayload, logger, cancellationToken);
	}
}