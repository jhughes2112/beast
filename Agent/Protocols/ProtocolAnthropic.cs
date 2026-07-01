using Anthropic.SDK;
using Anthropic.SDK.Common;
using Anthropic.SDK.Extensions;
using Anthropic.SDK.Messaging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AnthropicSystemMessage = Anthropic.SDK.Messaging.SystemMessage;

// -- Anthropic Messages API (native SDK) ---------------------------------------
// This protocol talks to Claude exclusively through Anthropic.SDK. The SDK's own
// List<Message> is the protocol-native runtime state: each completed assistant turn
// is appended verbatim as res.Message, which preserves the signed ThinkingContent and
// ToolUseContent blocks intact across turns in a live session. That signed state is
// exactly what a raw-HTTP reconstruction would destroy.
//
// The canonical ChatCompletions store remains ground truth and is kept in sync through
// the semantic OnAssistantTurn fan-out. Rehydrate (session load or protocol switch)
// starts from empty native state and does the best it can from canonical: it strips
// thinking (canonical thinking is unsigned and Anthropic would reject it), maps
// tool_calls to ToolUseContent and tool results to ToolResultContent, and enforces the
// strict user/assistant alternation the wire protocol requires. Once a real signed turn
// is produced live, that native message supersedes the lossy reconstruction.
//
// ProbeAsync stays a raw-HTTP detection call; it only classifies the endpoint and never
// participates in a real conversation.
public class ProtocolAnthropic
{
	private const string AnthropicVersion = "2023-06-01";

	// Native runtime state: the SDK message chain plus the system prompt text. Both are
	// in-memory only and rebuilt from canonical by Rehydrate.
	private readonly List<Message> _native = new List<Message>();
	private string _system = string.Empty;

	// Rebuilds the native SDK message chain from canonical, stripping thinking and enforcing
	// user/assistant alternation. Called by ProtocolProxy right after creating or switching in.
	public void Rehydrate(IReadOnlyList<CanonicalMessage> messages)
	{
		_native.Clear();
		_system = string.Empty;

		foreach (CanonicalMessage msg in messages)
		{
			if (msg is SystemMessage sm)
			{
				_system = sm.Text;
			}
			else if (msg is UserMessage um)
			{
				AppendContent(RoleType.User, new TextContent { Text = um.Text });
			}
			else if (msg is ToolResultMessage tr)
			{
				ToolResultContent toolResult = new ToolResultContent
				{
					ToolUseId = tr.ToolCallId,
					Content = new List<ContentBase> { new TextContent { Text = tr.Content } }
				};
				AppendContent(RoleType.User, toolResult);
			}
			else if (msg is AssistantMessage am)
			{
				if (!string.IsNullOrEmpty(am.Text))
					AppendContent(RoleType.Assistant, new TextContent { Text = am.Text });
				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					ToolUseContent use = new ToolUseContent
					{
						Id = tc.Id,
						Name = tc.Name,
						Input = ParseInput(tc.ArgumentsJson)
					};
					AppendContent(RoleType.Assistant, use);
				}
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
		AppendContent(RoleType.User, new TextContent { Text = text });
	}

	// A completed assistant turn from replay or another protocol. We reconstruct a native
	// assistant message without signature; thinking is intentionally dropped because an
	// unsigned thinking block cannot be replayed to Anthropic.
	public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
	{
		if (!string.IsNullOrEmpty(text))
		{
			AppendContent(RoleType.Assistant, new TextContent { Text = text });
		}

		foreach (SemanticToolCall tc in toolCalls)
		{
			ToolUseContent use = new ToolUseContent
			{
				Id = tc.Id,
				Name = tc.Name,
				Input = ParseInput(tc.ArgumentsJson)
			};
			AppendContent(RoleType.Assistant, use);
		}
	}

	public void OnToolResult(ToolResult result)
	{
		string content = result.StdOut;
		if (!string.IsNullOrEmpty(result.StdErr))
		{
			content = content + "\nstderr: " + result.StdErr;
		}
		ToolResultContent toolResult = new ToolResultContent
		{
			ToolUseId = result.Id,
			Content = new List<ContentBase> { new TextContent { Text = content } }
		};
		AppendContent(RoleType.User, toolResult);
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
			AnthropicClient client = BuildClient(model, extraHeaders, logger);
			MessageParameters parameters = BuildParameters(model, tools, forcedToolName, maxCompletionTokens, extraPayload);

			parameters.Stream = true;
			return await ExecuteStreamingAsync(client, parameters, model, bundle, onProgress, logger, cancellationToken);
		}
		catch (Exception ex)
		{
			return logger.ProtocolFailure(
				ProtocolResult.Transient(ex.ToString(), null),
				model, DetectedProtocol.Anthropic, "Exception", null, ex.Message, null, ex);
		}
	}

	private MessageParameters BuildParameters(LlmModel model, List<ToolDefinition> tools, string? forcedToolName, int? maxCompletionTokens, Dictionary<string, JsonNode?> extraPayload)
	{
		MessageParameters parameters = new MessageParameters
		{
			Model = model.Config.Id,
			Messages = _native,
			Temperature = 1.0m
		};

		// Anthropic requires a positive max_tokens; it cannot be omitted like the OpenAI protocols.
		// Prefer the caller's computed budget, otherwise fall back to the model's declared limit.
		int maxTokens = maxCompletionTokens > 0 ? maxCompletionTokens.Value : model.Config.MaxOutputTokens;
		if (maxTokens <= 0)
			maxTokens = 8192;
		parameters.MaxTokens = maxTokens;

		if (!string.IsNullOrEmpty(_system))
		{
			parameters.System = new List<AnthropicSystemMessage> { new AnthropicSystemMessage(_system) };
		}

		if (tools.Count > 0)
		{
			parameters.Tools = BuildTools(tools);

			// Force a specific tool when asked, require any tool for the AnyTool sentinel; otherwise
			// the model chooses (SDK default).
			if (forcedToolName == ProtocolProxy.AnyTool)
			{
				parameters.ToolChoice = new ToolChoice { Type = ToolChoiceType.Any };
			}
			else if (!string.IsNullOrEmpty(forcedToolName))
			{
				parameters.ToolChoice = new ToolChoice { Type = ToolChoiceType.Tool, Name = forcedToolName };
			}
		}

		ThinkingParameters? thinking = BuildThinking(model, extraPayload, parameters.MaxTokens);
		if (thinking != null)
		{
			parameters.Thinking = thinking;
		}

		return parameters;
	}

	// Resolves the thinking budget from the model's friendly reasoningEffort word, clamped against this
	// turn's max_tokens. An explicit "thinking" object in extras is the raw escape hatch and overrides
	// the word. Enabled only when a positive budget results.
	private static ThinkingParameters? BuildThinking(LlmModel model, Dictionary<string, JsonNode?> extraPayload, int maxTokens)
	{
		int budget = ReasoningEffort.AnthropicBudget(model.Config.ReasoningEffort, maxTokens);

		if (extraPayload.TryGetValue("thinking", out JsonNode? payloadThinking) && payloadThinking != null)
		{
			int extrasBudget = payloadThinking["budget_tokens"]?.GetValue<int>() ?? 0;
			if (extrasBudget > 0)
				budget = extrasBudget;
		}

		ThinkingParameters? result = null;
		if (budget > 0)
		{
			result = new ThinkingParameters { BudgetTokens = budget };
		}

		return result;
	}

	private static List<Anthropic.SDK.Common.Tool> BuildTools(List<ToolDefinition> tools)
	{
		List<Anthropic.SDK.Common.Tool> arr = new List<Anthropic.SDK.Common.Tool>();
		foreach (ToolDefinition tool in tools)
		{
			JsonNode schema = tool.Function.Parameters?.DeepClone() ?? new JsonObject();
			arr.Add(new Function(tool.Function.Name, tool.Function.Description, schema));
		}

		return arr;
	}

	// Intercepts the outgoing SDK request to log the exact wire payload before forwarding it.
	private sealed class QueryLoggingHandler : DelegatingHandler
	{
		private readonly SessionLogger _logger;
		private readonly string _modelName;
		private readonly string _endpoint;

		public QueryLoggingHandler(SessionLogger logger, string modelName, string endpoint) : base(new HttpClientHandler())
		{
			_logger = logger;
			_modelName = modelName;
			_endpoint = endpoint;
		}

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.Content != null)
			{
				string body = await request.Content.ReadAsStringAsync(cancellationToken);
				_logger.Write(_modelName, _endpoint, body);
				request.Content = new StringContent(body, Encoding.UTF8, "application/json");
			}
			return await base.SendAsync(request, cancellationToken);
		}
	}

	// The SDK reads its API key from APIAuthentication. The user-provided endpoint is honored
	// exactly by routing the SDK's HttpClient at it; we never override user data.
	private static AnthropicClient BuildClient(LlmModel model, Dictionary<string, string> extraHeaders, SessionLogger logger)
	{
		HttpMessageHandler innerHandler = new QueryLoggingHandler(logger, model.Config.Name, model.Endpoint);

		HttpClient httpClient = new HttpClient(innerHandler)
		{
			BaseAddress = new Uri(model.Endpoint)
		};

		foreach ((string name, string value) in extraHeaders)
		{
			httpClient.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
		}

		return new AnthropicClient(new APIAuthentication(model.ApiKey), httpClient);
	}

	private async Task<ProtocolResult> ExecuteStreamingAsync(AnthropicClient client, MessageParameters parameters, LlmModel model, ListenerBundle bundle, LiveUsageProgress onProgress, SessionLogger logger, CancellationToken cancellationToken)
	{
		List<MessageResponse> outputs = new List<MessageResponse>();
		string? openStreamTag = null;
		int liveInputTokens = 0;
		int liveOutputTokens = 0;
		int liveCachedTokens = 0;
		int streamedChars = 0;

		try
		{
			await foreach (MessageResponse res in client.Messages.StreamClaudeMessageAsync(parameters, cancellationToken))
			{
				outputs.Add(res);

				if (res.StreamStartMessage?.Usage != null)
				{
					liveInputTokens = res.StreamStartMessage.Usage.InputTokens;
					liveCachedTokens = res.StreamStartMessage.Usage.CacheReadInputTokens + res.StreamStartMessage.Usage.CacheCreationInputTokens;
				}

				string? assistantDelta = res.Delta?.Text;
				if (!string.IsNullOrEmpty(assistantDelta))
				{
					streamedChars += assistantDelta.Length;
					// Don't open the assistant output block on leading whitespace: a turn that is only
					// thinking plus a tool call may still emit a stray newline, and opening here would leave an
					// empty block behind. Wait for the first non-whitespace text; once open, every delta
					// (including whitespace) streams normally. Committed text is assembled separately, so it
					// keeps any leading whitespace regardless.
					bool assistantOpen = openStreamTag == StreamTag.Assistant;
					if (assistantOpen || !string.IsNullOrWhiteSpace(assistantDelta))
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
						bundle.Transport?.OnStreamChunk(StreamTag.Assistant, assistantDelta);
					}
				}

				string? thinkingDelta = res.Delta?.Thinking;
				if (!string.IsNullOrEmpty(thinkingDelta))
				{
					streamedChars += thinkingDelta.Length;
					if (openStreamTag != StreamTag.Thinking)
					{
						if (openStreamTag != null)
						{
							bundle.Transport?.OnStreamEnd(openStreamTag);
						}
						bundle.Transport?.OnStreamStart(StreamTag.Thinking);
						openStreamTag = StreamTag.Thinking;
					}
					bundle.Transport?.OnStreamChunk(StreamTag.Thinking, thinkingDelta);
				}

				// Anthropic only reports output tokens on the final message_delta, so during the body
				// of the stream we estimate from accumulated streamed characters (~4 chars/token) to
				// give the live display continuous motion. The real count is preferred once it arrives.
				if (res.Usage != null && res.Usage.OutputTokens > 0)
				{
					liveOutputTokens = res.Usage.OutputTokens;
				}
				else
				{
					liveOutputTokens = streamedChars / 4;
				}

				// Provisional running usage for this turn. The cost is computed from the same config
				// pricing the commit path uses, so the live display and committed total agree. When
				// config pricing is zero the commit applies the SDK cost fallback instead, which is
				// the single intentional correction at end-of-turn.
				decimal liveCost = (liveInputTokens / 1_000_000m) * model.Config.Cost.Input
								 + (liveOutputTokens / 1_000_000m) * model.Config.Cost.Output;
				onProgress(liveInputTokens, liveOutputTokens, liveCost, liveCachedTokens);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return ClassifyException(ex, model, logger);
		}
		finally
		{
			if (openStreamTag != null)
			{
				bundle.Transport?.OnStreamEnd(openStreamTag);
			}
		}

		if (outputs.Count == 0)
		{
			return ProtocolResult.Transient("Empty response from Anthropic API", null);
		}

		return CommitStreamedResponse(outputs, model, bundle);
	}

	// Reassembles the assistant turn from the collected stream events. A streamed MessageResponse
	// is a delta event whose Message/Content are null; only new Message(outputs) reconstructs the
	// full assistant message with its signed thinking and tool-use blocks intact. Usage is read
	// from the message_start (input tokens) and the final message_delta (output tokens / stop).
	private ProtocolResult CommitStreamedResponse(List<MessageResponse> outputs, LlmModel model, ListenerBundle bundle)
	{
		Message assistant = new Message(outputs);
		_native.Add(assistant);

		(string assistantText, string thinking, List<SemanticToolCall> toolCalls) = ExtractSemanticFromContent(assistant.Content);

		int freshInputTokens = 0;
		int cacheCreationTokens = 0;
		int cacheReadTokens = 0;
		int outputTokens = 0;
		string stopReason = "end_turn";

		foreach (MessageResponse res in outputs)
		{
			if (res.StreamStartMessage?.Usage != null)
			{
				freshInputTokens = res.StreamStartMessage.Usage.InputTokens;
				cacheCreationTokens = res.StreamStartMessage.Usage.CacheCreationInputTokens;
				cacheReadTokens = res.StreamStartMessage.Usage.CacheReadInputTokens;
			}
			if (res.Usage != null)
			{
				outputTokens = res.Usage.OutputTokens;
			}
			if (res.Delta?.StopReason != null)
			{
				stopReason = res.Delta.StopReason;
			}
		}

		// Anthropic's input_tokens excludes cache reads/writes; the full context is the sum of all input components plus output.
		int totalInputTokens = freshInputTokens + cacheCreationTokens + cacheReadTokens;

		TokenUsageInfo usage = new TokenUsageInfo
		{
			PromptTokens = totalInputTokens,
			CompletionTokens = outputTokens,
			CachedTokens = cacheReadTokens + cacheCreationTokens
		};

		decimal cost = ResolveCost(freshInputTokens, cacheCreationTokens, cacheReadTokens, outputTokens, model, outputs.Count > 0 ? outputs[outputs.Count - 1] : null);

		string finishReason = toolCalls.Count > 0 ? "tool_calls" : stopReason;
		List<ToolResult> emptyResults = new List<ToolResult>();
		return ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, thinking, toolCalls, emptyResults, finishReason, usage, cost));
	}

	// Computes cost from the model's configured per-million pricing, billing cache writes and
	// reads at their own rates. When that yields zero (model pricing not configured), falls back
	// to the SDK's own cost calculation derived from the response usage and model id.
	private static decimal ResolveCost(int inputTokens, int cacheWriteTokens, int cacheReadTokens, int outputTokens, LlmModel model, MessageResponse? response)
	{
		decimal cost = (inputTokens / 1_000_000m) * model.Config.Cost.Input
					 + (cacheWriteTokens / 1_000_000m) * model.Config.Cost.CacheWrite
					 + (cacheReadTokens / 1_000_000m) * model.Config.Cost.CacheRead
					 + (outputTokens / 1_000_000m) * model.Config.Cost.Output;

		if (cost == 0m && response != null)
		{
			cost = (decimal)response.CalculateCost().TotalCostUsd;
		}

		return cost;
	}

	private static (string assistantText, string thinking, List<SemanticToolCall> toolCalls) ExtractSemanticFromContent(List<ContentBase>? content)
	{
		StringBuilder textBuilder = new StringBuilder();
		StringBuilder thinkingBuilder = new StringBuilder();
		List<SemanticToolCall> toolCalls = new List<SemanticToolCall>();

		if (content != null)
		{
			foreach (ContentBase block in content)
			{
				if (block is TextContent text)
				{
					if (!string.IsNullOrEmpty(text.Text))
						textBuilder.Append(text.Text);
				}
				else if (block is ThinkingContent thinkingBlock)
				{
					if (!string.IsNullOrEmpty(thinkingBlock.Thinking))
						thinkingBuilder.Append(thinkingBlock.Thinking);
				}
				else if (block is ToolUseContent use)
				{
					string args = use.Input != null ? use.Input.ToJsonString() : "{}";
					toolCalls.Add(new SemanticToolCall { Id = use.Id ?? string.Empty, Name = use.Name ?? string.Empty, ArgumentsJson = args });
				}
			}
		}

		return (textBuilder.ToString(), thinkingBuilder.ToString(), toolCalls);
	}

	// Appends content to the trailing message when it shares the role, otherwise starts a new
	// message. This collapses consecutive same-role blocks into the alternation Anthropic requires.
	private void AppendContent(RoleType role, ContentBase block)
	{
		if (_native.Count > 0)
		{
			Message last = _native[_native.Count - 1];
			if (last.Role == role)
			{
				last.Content.Add(block);
				return;
			}
		}

		Message msg = new Message
		{
			Role = role,
			Content = new List<ContentBase> { block }
		};
		_native.Add(msg);
	}

	private static JsonNode ParseInput(string argsJson)
	{
		JsonNode? parsed = null;
		if (!string.IsNullOrEmpty(argsJson))
		{
			try
			{ parsed = JsonNode.Parse(argsJson); }
			catch (System.Text.Json.JsonException) { parsed = null; }
		}
		return parsed ?? new JsonObject();
	}

	private static ProtocolResult ClassifyException(Exception ex, LlmModel model, SessionLogger logger)
	{
		ProtocolResult result;
		if (ex is HttpRequestException http && http.StatusCode.HasValue)
		{
			int status = (int)http.StatusCode.Value;
			if (status == 429)
			{
				result = logger.ProtocolFailure(
					ProtocolResult.RateLimited(null),
					model, DetectedProtocol.Anthropic, "RateLimited", status, ex.Message, null, ex);
			}
			else if (status == 401 || status == 403)
			{
				result = logger.ProtocolFailure(
					ProtocolResult.Failed($"HTTP {status}: {ex}"),
					model, DetectedProtocol.Anthropic, "AuthFailure", status, ex.Message, null, ex);
			}
			else if (status >= 400 && status < 500)
			{
				result = logger.ProtocolFailure(
					ProtocolResult.Transient($"HTTP {status}: {ex}", null),
					model, DetectedProtocol.Anthropic, "ClientError", status, ex.Message, null, ex);
			}
			else
			{
				result = logger.ProtocolFailure(
					ProtocolResult.Transient($"HTTP {status}: {ex}", null),
					model, DetectedProtocol.Anthropic, "ServerError", status, ex.Message, null, ex);
			}
		}
		else if (ex is HttpRequestException)
		{
			result = logger.ProtocolFailure(
				ProtocolResult.Transient(ex.ToString(), null),
				model, DetectedProtocol.Anthropic, "NetworkError", null, ex.Message, null, ex);
		}
		else
		{
			result = logger.ProtocolFailure(
				ProtocolResult.Transient(ex.ToString(), null),
				model, DetectedProtocol.Anthropic, "Exception", null, ex.Message, null, ex);
		}

		return result;
	}

	// Token counting call: uses Anthropic's dedicated /count_tokens endpoint (side-effect-free).
	// Falls back to the legacy tracer (max_tokens=1) if the count endpoint is unavailable (e.g. OpenRouter).
	public async Task<TracerResult> CountTokensAsync(
		LlmModel model,
		List<ToolDefinition> tools,
		string? forcedToolName,
		Dictionary<string, string> extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		SessionLogger logger,
		CancellationToken cancellationToken)
	{
		// Build the count endpoint URL: model.Endpoint ends with /messages, count endpoint is /messages/count_tokens
		string countEndpoint = model.Endpoint;
		if (countEndpoint.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
		{
			countEndpoint += "/count_tokens";
		}
		else
		{
			// Fallback if the endpoint doesn't follow the expected pattern
			countEndpoint = model.Endpoint.TrimEnd('/') + "/count_tokens";
		}

		JsonObject body = BuildCountBody(model, tools, forcedToolName, extraPayload);
		logger.Write(model.Config.Name, countEndpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		string requestJson = body.ToJsonString();

		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, countEndpoint);
		req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
		req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
		req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
		req.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-connect", "true");

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
				return TracerResult.Failed("Empty response from count_tokens API");

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

	// Builds the request body for the /count_tokens endpoint.
	// Mirrors /messages body but WITHOUT max_tokens, stream, or thinking.
	private JsonObject BuildCountBody(LlmModel model, List<ToolDefinition> tools, string? forcedToolName, Dictionary<string, JsonNode?> extraPayload)
	{
		JsonObject body = new JsonObject();
		body["model"] = model.Config.Id;

		// Build messages array from native state
		JsonArray messages = new JsonArray();
		foreach (Message msg in _native)
		{
			foreach (ContentBase content in msg.Content)
			{
				JsonObject msgObj = new JsonObject();
				if (msg.Role == RoleType.User)
					msgObj["role"] = "user";
				else
					msgObj["role"] = "assistant";

				JsonArray contentArr = new JsonArray();
				if (content is TextContent tc)
				{
					JsonObject textObj = new JsonObject();
					textObj["type"] = "text";
					textObj["text"] = tc.Text;
					contentArr.Add(textObj);
				}
				else if (content is ToolUseContent tuc)
				{
					JsonObject toolObj = new JsonObject();
					toolObj["type"] = "tool_use";
					toolObj["id"] = tuc.Id;
					toolObj["name"] = tuc.Name;
					if (tuc.Input != null)
						toolObj["input"] = tuc.Input;
					contentArr.Add(toolObj);
				}
				else if (content is ToolResultContent trc)
				{
					JsonObject toolResultObj = new JsonObject();
					toolResultObj["type"] = "tool_result";
					toolResultObj["tool_use_id"] = trc.ToolUseId;
					JsonArray innerContent = new JsonArray();
					foreach (ContentBase inner in trc.Content)
					{
						if (inner is TextContent itc)
						{
							JsonObject innerText = new JsonObject();
							innerText["type"] = "text";
							innerText["text"] = itc.Text;
							innerContent.Add(innerText);
						}
					}
					toolResultObj["content"] = innerContent;
					contentArr.Add(toolResultObj);
				}

				msgObj["content"] = contentArr;

				// Merge consecutive same-role content into single message
				if (messages.Count > 0)
				{
					JsonNode? last = messages[messages.Count - 1];
					string? lastRole = last?["role"]?.GetValue<string>();
					string thisRole = msg.Role == RoleType.User ? "user" : "assistant";
					if (lastRole == thisRole)
					{
						// Merge content arrays
						JsonArray lastContent = last!["content"]!.AsArray()!;
						foreach (JsonNode? cn in contentArr)
							lastContent.Add(cn);
						continue;
					}
				}
				messages.Add(msgObj);
			}
		}
		body["messages"] = messages;

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
				toolsArr.Add(t);
			}
			body["tools"] = toolsArr;

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

		// Merge extra payload (but skip thinking/thinking, max_tokens, stream which are not for count_tokens)
		foreach ((string name, JsonNode? value) in extraPayload)
		{
			if (name == "thinking" || name == "max_tokens" || name == "stream")
				continue;
			body[name] = value?.DeepClone();
		}

		return body;
	}

	// Tracer call: sends the same request with max_tokens=1 (minimum positive value Anthropic accepts)
	// to get accurate input token counts without generating a meaningful response.
	// Kept as fallback for providers that don't support /count_tokens (e.g., OpenRouter).
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
			JsonObject body = BuildTracerBody(model, tools, forcedToolName, extraPayload);
			logger.Write(model.Config.Name, model.Endpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

			string requestJson = body.ToJsonString();

			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
			req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
			req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
			req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
			req.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-connect", "true");

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

				int inputTokens = usageNode["input_tokens"]?.GetValue<int>() ?? 0;
				int cacheReadTokens = usageNode["cache_creation_input_tokens"]?.GetValue<int>() ?? 0;
				int cacheReadTokens2 = usageNode["cache_read_input_tokens"]?.GetValue<int>() ?? 0;
				int cachedTokens = cacheReadTokens + cacheReadTokens2;

				return TracerResult.Success(inputTokens, cachedTokens);
			}

			// 4xx (non-429, non-retryable) — distinguish actual context overflow from parameter errors
			if (ProtocolHelpers.IsPermanentClientError(statusCode))
			{
				string lowerBody = responseBody.ToLowerInvariant();
				if (lowerBody.Contains("context_length_exceeded") || lowerBody.Contains("maximum context length") || lowerBody.Contains("max_tokens"))
				{
					return TracerResult.ContextExceeded(statusCode);
				}
				return TracerResult.Failed($"HTTP {statusCode}: {responseBody}");
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

	// Build a minimal request body for the tracer call. Mirrors what the SDK would send but with
	// max_tokens=1 (minimum positive value Anthropic accepts) and no streaming.
	private JsonObject BuildTracerBody(LlmModel model, List<ToolDefinition> tools, string? forcedToolName, Dictionary<string, JsonNode?> extraPayload)
	{
		JsonObject body = new JsonObject();
		body["model"] = model.Config.Id;
		body["max_tokens"] = 1;

		// Build messages array from native state
		JsonArray messages = new JsonArray();
		foreach (Message msg in _native)
		{
			foreach (ContentBase content in msg.Content)
			{
				JsonObject msgObj = new JsonObject();
				if (msg.Role == RoleType.User)
					msgObj["role"] = "user";
				else
					msgObj["role"] = "assistant";

				JsonArray contentArr = new JsonArray();
				if (content is TextContent tc)
				{
					JsonObject textObj = new JsonObject();
					textObj["type"] = "text";
					textObj["text"] = tc.Text;
					contentArr.Add(textObj);
				}
				else if (content is ToolUseContent tuc)
				{
					JsonObject toolObj = new JsonObject();
					toolObj["type"] = "tool_use";
					toolObj["id"] = tuc.Id;
					toolObj["name"] = tuc.Name;
					if (tuc.Input != null)
						toolObj["input"] = tuc.Input;
					contentArr.Add(toolObj);
				}
				else if (content is ToolResultContent trc)
				{
					JsonObject toolResultObj = new JsonObject();
					toolResultObj["type"] = "tool_result";
					toolResultObj["tool_use_id"] = trc.ToolUseId;
					JsonArray innerContent = new JsonArray();
					foreach (ContentBase inner in trc.Content)
					{
						if (inner is TextContent itc)
						{
							JsonObject innerText = new JsonObject();
							innerText["type"] = "text";
							innerText["text"] = itc.Text;
							innerContent.Add(innerText);
						}
					}
					toolResultObj["content"] = innerContent;
					contentArr.Add(toolResultObj);
				}

				msgObj["content"] = contentArr;

				// Merge consecutive same-role content into single message
				if (messages.Count > 0)
				{
					JsonNode? last = messages[messages.Count - 1];
					string? lastRole = last?["role"]?.GetValue<string>();
					string thisRole = msg.Role == RoleType.User ? "user" : "assistant";
					if (lastRole == thisRole)
					{
						// Merge content arrays
						JsonArray lastContent = last!["content"]!.AsArray()!;
						foreach (JsonNode? cn in contentArr)
							lastContent.Add(cn);
						continue;
					}
				}
				messages.Add(msgObj);
			}
		}
		body["messages"] = messages;

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
				toolsArr.Add(t);
			}
			body["tools"] = toolsArr;

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

		// Merge extra payload
		foreach ((string name, JsonNode? value) in extraPayload)
		{
			body[name] = value?.DeepClone();
		}

		return body;
	}
}