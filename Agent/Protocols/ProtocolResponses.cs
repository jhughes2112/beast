using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

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

	// Whether to ask for reasoning summaries. A reasoning model spends most of a turn thinking and
	// emits nothing observable while it does — no text, no events the display can show — so the
	// client sits silent through the slowest and most expensive part of the turn. Summaries are the
	// only window the API offers into that (the raw chain of thought is never exposed), they stream
	// as they are produced, and they cost nothing beyond the reasoning tokens already billed.
	// Not every endpoint offers them: non-reasoning models reject the field outright, and OpenAI
	// gates them behind organization verification. Both refusals are a 400 naming the parameter, so
	// this is advertised optimistically and switched off for the life of the instance on rejection —
	// see IsSummaryRejection.
	private bool _summarySupported = true;

	// Set when a request is rejected over a reasoning feature rather than on its merits: the turn is
	// rebuilt without that feature and re-sent, instead of failing the model over it.
	private bool _retryAfterRefusal;

	// Whether this model accepts a reasoning effort at all. Effort now defaults to medium for models
	// nobody has configured (see ReasoningEffort.DefaultWord), which means it also reaches models that
	// cannot reason — they answer with a 400 naming the parameter. That refusal is the answer to a
	// question no catalog can answer, so it is honored here and written back as "none".
	private bool _reasoningSupported = true;

	// Whether the request just built actually carried a reasoning field. A 400 mentioning reasoning
	// cannot be blamed on a request that never asked for any.
	private bool _sentReasoning;

	// Native runtime state: the last server-issued response id. In-memory only, reset by Rehydrate.
	private string? _previousResponseId;
	// Full input array built once during Rehydrate for the first post-rehydration turn.
	// After successful send, cleared so subsequent turns use incremental chaining.
	private JsonArray? _rehydratedInput;
	// Incremental input items accumulated since the last successful send via IProtocolListener callbacks.
	// Used for chaining mode after _rehydratedInput is consumed.
	private JsonArray _deltaInput;
	// Set when a response commits while chaining is active: the assistant turn about to be fanned back
	// in through OnAssistantTurn is the one the server just produced, so it already lives in the
	// server-side thread. Cleared by that fan-out (or by Rehydrate) so a turn arriving any other way —
	// an interrupted turn, a replay, a turn from a response that carried no id — is still recorded.
	private bool _assistantTurnOnServer;

	// Where a refusal learned from the provider is reported so it outlives this instance. Null in
	// tests, which exercise the request shapes without a registry behind them.
	private readonly ModelCapabilitySink? _onCapabilityLearned;

	// Keeps each request's media within the provider's limits once it refuses (see MediaBudget).
	// Answered media is NOT removed here: the history lives server-side and is left as the server
	// holds it, and while chaining only the new items are in the request anyway.
	private readonly MediaBudget _media = new MediaBudget();

	// The ChatGPT subscription backend (ChatGptSubscription) keeps nothing server-side: it requires
	// store=false and has no previous_response_id over HTTP, so every request carries the whole
	// conversation, which this instance then holds in _rehydratedInput as a running history. It also
	// takes no max_output_tokens, has no token-count route, signs requests with a refreshed OAuth
	// token, and wants the system prompt as a developer message.
	private readonly bool _subscription;

	// Ties this conversation's requests together for the subscription backend's prompt cache.
	private readonly string _sessionKey = Guid.NewGuid().ToString();

	// Subscription only: the reasoning items of the response just committed, encrypted_content and
	// all, waiting for the assistant-turn fan-out to place them ahead of the turn they produced. With
	// store=false they are the only way the model keeps its reasoning across tool calls. Held rather
	// than appended at commit so a turn LlmService drops (an unrepairable tool call) leaves no trace.
	private JsonArray? _pendingReasoning;

	// The token-count route under /responses. OpenAI's is /input_tokens/count; Meta's is /input_tokens.
	// OpenAI's is tried first and a 404 moves on to Meta's; whichever answers — or that neither does —
	// is recorded on the endpoint's shared facts, so the probe happens once per endpoint.
	private const    string        kOpenAiCountRoute = "/input_tokens/count";
	private const    string        kMetaCountRoute   = "/input_tokens";
	private readonly EndpointFacts _endpointFacts;

	// Subscription only: a 401 refreshes the token and re-sends once per turn.
	private bool _refreshAuth;
	private bool _authRetried;

	public ProtocolResponses(ModelCapabilitySink? onCapabilityLearned, EndpointFacts endpointFacts, bool subscription)
	{
		_deltaInput          = new JsonArray();
		_onCapabilityLearned = onCapabilityLearned;
		_endpointFacts       = endpointFacts;
		_subscription        = subscription;
	}

	// The subscription backend takes the system prompt as a developer message, as the Codex CLI sends it.
	private string SystemRole => _subscription ? "developer" : "system";

	// Clears native chaining state so the next turn replays the full canonical history once.
	public void Rehydrate(IReadOnlyList<CanonicalMessage> messages)
	{
		_previousResponseId    = null;
		_assistantTurnOnServer = false;
		_pendingReasoning      = null;
		_deltaInput.Clear();

		JsonArray input = new JsonArray();
		foreach (CanonicalMessage msg in messages)
		{
			if (msg is SystemMessage sm)
			{
				input.Add((JsonNode)BuildMessageItem(SystemRole, "input_text", sm.Text));
			}
			else if (msg is UserMessage um)
			{
				input.Add((JsonNode)BuildUserItem(um.Text, um.Attachments));
			}
			else if (msg is AssistantMessage am)
			{
				if (!string.IsNullOrEmpty(am.Text))
					input.Add((JsonNode)BuildMessageItem("assistant", "output_text", am.Text));

				foreach (SemanticToolCall tc in am.ToolCalls)
				{
					input.Add((JsonNode)BuildFunctionCallItem(tc));
				}
			}
			else if (msg is ToolResultMessage tr)
			{
				input.Add((JsonNode)BuildToolOutputItem(tr.ToolCallId, tr.Content, tr.Media));
			}
		}
		_rehydratedInput = input;
	}

	// Track incremental changes to build deltas for chaining mode.
	public void OnSystemMessage(string text)
	{
		_deltaInput.Add((JsonNode)BuildMessageItem(SystemRole, "input_text", text));
	}

	public void OnUserMessage(string text)
	{
		_deltaInput.Add((JsonNode)BuildMessageItem("user", "input_text", text));
	}

	public void OnUserMessage(string text, IReadOnlyList<MediaAttachment> attachments)
	{
		_deltaInput.Add((JsonNode)BuildUserItem(text, attachments));
	}

	// Builds a user item and folds any attachments into its content array as input_image parts, so
	// a live turn and a rehydrated one produce the same shape. The Responses API has no audio or
	// video input part; those degrade to a text note rather than being dropped silently.
	private static JsonObject BuildUserItem(string text, IReadOnlyList<MediaAttachment>? attachments)
	{
		JsonObject item = BuildMessageItem("user", "input_text", text);
		if (attachments == null || attachments.Count == 0)
			return item;

		JsonArray content = (JsonArray)item["content"]!;

		// A message that is nothing but a dropped image has no words; drop the empty text part
		// rather than sending a blank block alongside the media.
		if (string.IsNullOrEmpty(text))
			content.Clear();
		foreach (MediaAttachment att in attachments)
		{
			if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
				content.Add((JsonNode)new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{att.MimeType};base64,{att.Base64Data}" });
			else
				content.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = $"[A {att.MimeType} attachment was supplied, but this provider does not accept that input type.]" });
		}
		return item;
	}

	public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
	{
		// When chaining, the server already holds this turn under previous_response_id. Echoing it back
		// duplicates the context, and the echoed function_call is a second, unanswered copy of a call the
		// server is still tracking — which it rejects with "No tool output found for function call ...".
		if (_assistantTurnOnServer)
		{
			_assistantTurnOnServer = false;
			return;
		}

		// The turn's own reasoning goes in ahead of it, exactly as the response returned it.
		if (_pendingReasoning != null)
		{
			foreach (JsonNode? item in _pendingReasoning)
				_deltaInput.Add(item!.DeepClone());
			_pendingReasoning = null;
		}

		if (!string.IsNullOrEmpty(text))
		{
			_deltaInput.Add((JsonNode)BuildMessageItem("assistant", "output_text", text));
		}

		foreach (SemanticToolCall tc in toolCalls)
		{
			_deltaInput.Add((JsonNode)BuildFunctionCallItem(tc));
		}
	}

	// An input-side function_call item. The "id" field is deliberately omitted: it names a server-owned
	// output item and only ever matches when the server minted it, so sending a synthesized one is at
	// best noise. Pairing runs on call_id, which is replayed exactly as the provider issued it.
	private static JsonObject BuildFunctionCallItem(SemanticToolCall tc)
	{
		JsonObject item   = new JsonObject();
		item["type"]      = "function_call";
		item["call_id"]   = tc.Id;
		item["name"]      = tc.Name;
		item["arguments"] = tc.ArgumentsJson;
		return item;
	}

	public void OnToolResult(ToolResult result)
	{
		string output = result.StdOut;
		if (!string.IsNullOrEmpty(result.StdErr))
		{
			output = output + "\nstderr: " + result.StdErr;
		}
		_deltaInput.Add((JsonNode)BuildToolOutputItem(result.Id, output, result.Media));
	}

	// A function_call_output item, shared by the live path and rehydrate. A text-only result keeps
	// the plain string output; one carrying images switches output to the part-array form with an
	// input_image per file after the text. The Responses API takes no audio or video in a tool
	// output, so those degrade to a text note, as they do on a user message.
	private static JsonObject BuildToolOutputItem(string callId, string output, IReadOnlyList<MediaFile>? media)
	{
		JsonObject item = new JsonObject();
		item["type"]    = "function_call_output";
		item["call_id"] = callId;

		if (media == null || media.Count == 0)
		{
			item["output"] = output;
			return item;
		}

		JsonArray parts = new JsonArray();
		if (!string.IsNullOrEmpty(output))
			parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = output });

		foreach (MediaFile file in media)
		{
			MediaAttachment? att = MediaKinds.LoadAttachment(file.Path, file.MimeType);
			if (att == null)
				parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = MediaKinds.MissingNote(file.Path) });
			else if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
				parts.Add((JsonNode)new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{att.MimeType};base64,{att.Base64Data}" });
			else
				parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = $"[A {att.MimeType} attachment was supplied, but this provider does not accept that input type.]" });
		}

		item["output"] = parts;
		return item;
	}

	public async Task<ProtocolResult> ExecuteAsync(
		LlmModel                   model,
		ListenerBundle             bundle,
		List<ToolDefinition>       tools,
		string?                    forcedToolName,
		int?                       maxCompletionTokens,
		Dictionary<string, string> extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		LiveUsageProgress             onProgress,
		SessionLogger                 logger,
		CancellationToken             cancellationToken)
	{
		_authRetried = false;
		try
		{
			// Loops only to re-send a turn the server refused over reasoning.summary or over its media.
			// _summarySupported is one-way and the media limits only shrink below what was refused, so
			// the rebuilt request cannot be refused for the same reason twice. (The subscription's 401
			// re-send is also one-shot: _authRetried.)
			for (; ; )
			{
				JsonObject body      = BuildBody(model, tools, forcedToolName, maxCompletionTokens, extraPayload);
				string?    mediaNote = _media.TakeNote();
				if (mediaNote != null)
					logger.Note(mediaNote);
				logger.Write(model.Config.Name, model.Endpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

				if (_streamingSupported)
				{
					ProtocolResult? streamResult = await ExecuteStreamingAsync(model, body, extraHeaders, bundle, onProgress, logger, cancellationToken);
					if (_retryAfterRefusal)
					{
						_retryAfterRefusal = false;
						continue;
					}
					if (streamResult != null)
						return streamResult;
				}

				// The subscription backend only streams; there is no non-streaming request to fall back to.
				if (_subscription)
					return ProtocolResult.Transient("The ChatGPT subscription stream ended without a result", null);

				string requestJson = body.ToJsonString();

				HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
				req.Content            = new StringContent(requestJson, Encoding.UTF8, "application/json");
				req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
				foreach ((string name, string value) in extraHeaders)
				{
					req.Headers.TryAddWithoutValidation(name, value);
				}

				HttpResponseMessage httpResponse;
				string              responseBody;
				try
				{
					// Non-streaming has no chunks to re-arm on, so this deadline is the whole request.
					using CancellationTokenSource requestCts = ProtocolHelpers.CreateRequestTimeout(model, cancellationToken);
					httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, requestCts.Token);
					responseBody = await httpResponse.Content.ReadAsStringAsync(requestCts.Token);
				}
				catch (OperationCanceledException)
				{
					ProtocolResult? timeout = ProtocolHelpers.TimeoutOrRethrow(cancellationToken, model);
					if (timeout != null)
						return timeout;
					throw;
				}
				catch (HttpRequestException ex)
				{
					logger.ProtocolFailure(
						model, DetectedProtocol.Responses, "NetworkError",
						ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null, ex.Message, null, ex);
					return ProtocolResult.Transient(ex.ToString(), null);
				}
				catch (Exception ex)
				{
					logger.ProtocolFailure(model, DetectedProtocol.Responses, "Exception", null, ex.Message, null, ex);
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
						model, DetectedProtocol.Responses, "RateLimited",
						(int)httpResponse.StatusCode, responseBody, responseBody, null);
					return ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, responseBody));
				}

				int statusCode = (int)httpResponse.StatusCode;

				// The server refused a reasoning feature, or the request's media, not the turn: drop or
				// trim it and re-send the turn.
				if (LearnReasoningRefusal(model, statusCode, responseBody, logger)
					|| _media.LearnFromRefusal(model, statusCode, responseBody, _onCapabilityLearned, logger))
					continue;

				// A 4xx other than the 429 handled above (and the genuinely retryable 408/425) is a permanent
				// client error: the request itself is bad, so retrying just burns the transient budget and then
				// surfaces as a misleading "rate limited". Fail fast with the body so the real cause is visible;
				// 5xx and the retryable 4xx stay transient.
				if (ProtocolHelpers.IsPermanentClientError(statusCode))
				{
					if (ProtocolHelpers.IsContextOverflow(responseBody))
					{
						return ProtocolHelpers.ContextOverflowFailure("Responses", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId);
					}
					return ProtocolHelpers.Failure("Responses", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId);
				}
				return ProtocolHelpers.TransientFailure("Responses", statusCode, responseBody, logger, model.Config.Name, model.Endpoint, model.ConfigId, httpResponse);
			}
		}
		catch (Exception ex)
		{
			logger.ProtocolFailure(model, DetectedProtocol.Responses, "Exception", null, ex.Message, null, ex);
			return ProtocolResult.Transient(ex.ToString(), null);
		}
	}

	private JsonObject BuildBody(LlmModel model, List<ToolDefinition> tools, string? forcedToolName, int? maxCompletionTokens, Dictionary<string, JsonNode?> extraPayload)
	{
		JsonObject body = new JsonObject();
		body["model"]   = _subscription ? ChatGptSubscription.ApiModel(model.Config.Id) : model.Config.Id;

		// Reasoning held for a turn that was never fanned out belongs to a dropped turn.
		_pendingReasoning = null;

		// If we have rehydrated input, send the full history once (no previous_response_id).
		// Otherwise, chain from the last response id and send only the delta items.
		// DeepClone is required: assigning a JsonNode to a body object parents it, and the same
		// node cannot be parented twice — re-use across turns throws InvalidOperationException.
		// The subscription sends its running history plus the delta every time, so the history is
		// all client-side and answered media can be dropped from it, as ChatCompletions does.
		JsonArray input;
		if (_subscription)
		{
			input = _rehydratedInput != null ? (JsonArray)_rehydratedInput.DeepClone() : new JsonArray();
			foreach (JsonNode? item in _deltaInput)
				input.Add(item!.DeepClone());
		}
		else
		{
			input = (JsonArray)(_rehydratedInput ?? _deltaInput).DeepClone();
		}
		_media.Apply(input, model.Config.MaxImages, "input_text", _subscription);
		body["input"] = input;
		if (!_subscription && _rehydratedInput == null && _previousResponseId != null)
		{
			body["previous_response_id"] = _previousResponseId;
		}

		// The subscription backend rejects max_output_tokens; its own limits bound the reply.
		if (maxCompletionTokens.HasValue && !_subscription)
			body["max_output_tokens"] = maxCompletionTokens.Value;

		if (_subscription)
		{
			body["store"]            = false;
			body["include"]          = new JsonArray("reasoning.encrypted_content");
			body["prompt_cache_key"] = _sessionKey;
		}

		if (tools.Count > 0)
		{
			JsonArray  toolsArr   = new JsonArray();
			JsonObject twebsearch = new JsonObject(); // the allows web search to happen internally on any OpenAI model, about a penny a search
			twebsearch["type"]    = "web_search";
			toolsArr.Add((JsonNode)twebsearch);

			foreach (ToolDefinition td in tools)
			{
				JsonObject t = new JsonObject();
				t["type"]    = "function";
				t["name"]    = td.Function.Name;
				if (!string.IsNullOrEmpty(td.Function.Description))
					t["description"] = td.Function.Description;
				if (td.Function.Parameters != null)
					t["parameters"] = td.Function.Parameters.DeepClone();
				toolsArr.Add((JsonNode)t);
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
				JsonObject choice   = new JsonObject();
				choice["type"]      = "function";
				choice["name"]      = forcedToolName;
				body["tool_choice"] = choice;
			}
			else
			{
				body["tool_choice"] = "auto";
			}
		}

		// Translate the friendly reasoningEffort word into the Responses-native reasoning.effort object,
		// and ask for a summary of the thinking so the turn is not silent while it happens. The summary
		// is requested even with no effort word configured: the reasoning models think at their default
		// effort whether or not this asks them to, so leaving it out buys silence and nothing else.
		// Applied before extras so an explicit "reasoning" block in extras can still override it.
		string?    effort    = _reasoningSupported ? ReasoningEffort.OpenAiEffort(model.Config.ReasoningEffort) : null;
		JsonObject reasoning = new JsonObject();
		if (effort != null)
			reasoning["effort"] = effort;
		if (_summarySupported && model.Config.ReasoningSummaries)
			reasoning["summary"] = "auto";
		if (reasoning.Count > 0)
			body["reasoning"] = reasoning;
		_sentReasoning = reasoning.Count > 0;

		foreach ((string name, JsonNode? value) in extraPayload)
		{
			body[name] = value?.DeepClone();
		}

		return body;
	}

	// True when a 4xx is the server refusing reasoning summaries specifically, rather than rejecting
	// the request on its merits. Two distinct refusals wear the same shape: a model that does not
	// reason at all calls the parameter unsupported, and OpenAI tells unverified organizations they
	// must verify before it will produce summaries. Either way the turn itself is fine — only the
	// summary has to go. Matched narrowly on the parameter name so an unrelated 400 is never
	// mistaken for one of these and silently retried.
	// True when a 4xx is the server rejecting the reasoning parameter itself — a model that does not
	// reason being asked to. Checked only after the summary case (which is narrower and shares the
	// wording) and only when the request actually carried a reasoning field, so an unrelated 400 that
	// happens to contain the word is never mistaken for one.
	private static bool IsReasoningRejection(string errorBody)
	{
		if (string.IsNullOrEmpty(errorBody))
			return false;

		string lower = errorBody.ToLowerInvariant();
		if (!lower.Contains("reasoning"))
			return false;

		return lower.Contains("unsupported")
			|| lower.Contains("not supported")
			|| lower.Contains("does not support")
			|| lower.Contains("unknown parameter")
			|| lower.Contains("unknown_parameter");
	}

	// Classifies a rejected request and, when the provider refused a reasoning feature rather than the
	// turn, switches that feature off for this instance, reports it so the live config and settings
	// carry it too, and says the turn should be re-sent. Returns false for every other 4xx, which the
	// caller then handles as the real failure it is.
	private bool LearnReasoningRefusal(LlmModel model, int statusCode, string errorBody, SessionLogger logger)
	{
		// Only a 4xx is the server refusing what was asked. A 5xx that happens to quote the request
		// back is an outage, and switching a feature off over one would be learning the wrong lesson
		// permanently.
		if (statusCode < 400 || statusCode >= 500)
			return false;

		if (_summarySupported && model.Config.ReasoningSummaries && IsSummaryRejection(errorBody))
		{
			_summarySupported = false;
			logger.Write(model.Config.Name, model.Endpoint, "[reasoning] endpoint refused thinking summaries; disabling them for this model and re-sending (its thinking will not be visible)");
			_onCapabilityLearned?.Invoke(model.ConfigId, null, false, null);
			return true;
		}

		if (_reasoningSupported && _sentReasoning && IsReasoningRejection(errorBody))
		{
			_reasoningSupported = false;
			logger.Write(model.Config.Name, model.Endpoint, "[reasoning] model does not accept a reasoning effort; recording it as none and re-sending");
			_onCapabilityLearned?.Invoke(model.ConfigId, "none", null, null);
			return true;
		}

		return false;
	}

	private static bool IsSummaryRejection(string errorBody)
	{
		if (string.IsNullOrEmpty(errorBody))
			return false;

		string lower = errorBody.ToLowerInvariant();
		return lower.Contains("reasoning.summary")
			|| lower.Contains("'summary'")
			|| lower.Contains("\"summary\"")
			|| lower.Contains("reasoning summaries")
			|| lower.Contains("reasoning summary");
	}

	private static JsonObject BuildMessageItem(string role, string blockType, string text)
	{
		JsonObject item    = new JsonObject();
		item["type"]       = "message";
		item["role"]       = role;
		JsonArray  content = new JsonArray();
		JsonObject block   = new JsonObject();
		block["type"]      = blockType;
		block["text"]      = text;
		content.Add((JsonNode)block);
		item["content"] = content;
		return item;
	}

	private async Task<ProtocolResult?> ExecuteStreamingAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, ListenerBundle bundle, LiveUsageProgress onProgress, SessionLogger logger, CancellationToken cancellationToken)
	{
		JsonObject streamBody = (JsonObject)body.DeepClone();
		streamBody["stream"]  = true;

		string requestJson = streamBody.ToJsonString();

		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
		req.Content            = new StringContent(requestJson, Encoding.UTF8, "application/json");
		if (_subscription)
		{
			// A sign-in only a person can renew is a hard failure (the human is alerted); anything
			// else — the auth service briefly unreachable — is worth a retry.
			ChatGptSubscription.Credentials creds = await ChatGptSubscription.ApplyHeadersAsync(req, _sessionKey, _refreshAuth, cancellationToken);
			_refreshAuth = false;
			if (creds.Error.Length > 0)
			{
				logger.ProtocolFailure(model, DetectedProtocol.Responses, "AuthFailure", null, creds.Error, null, null);
				return creds.NeedsLogin ? ProtocolResult.Failed(creds.Error) : ProtocolResult.Transient(creds.Error, null);
			}
			req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
		}
		else
		{
			req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
		}

		foreach ((string name, string value) in extraHeaders)
		{
			req.Headers.TryAddWithoutValidation(name, value);
		}

		// The streaming reader pushes this deadline out on every event, so it bounds silence only.
		using CancellationTokenSource requestCts = ProtocolHelpers.CreateRequestTimeout(model, cancellationToken);
		TimeSpan idleTimeout                     = TimeSpan.FromSeconds(ProtocolHelpers.RequestTimeoutSeconds(model));

		HttpResponseMessage httpResponse;
		try
		{
			httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, requestCts.Token);
		}
		catch (OperationCanceledException)
		{
			ProtocolResult? timeout = ProtocolHelpers.TimeoutOrRethrow(cancellationToken, model);
			if (timeout != null)
				return timeout;
			throw;
		}
		catch (HttpRequestException ex)
		{
			logger.ProtocolFailure(
				model, DetectedProtocol.Responses, "NetworkError",
				ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null, ex.Message, null, ex);
			return ProtocolResult.Transient(ex.ToString(), null);
		}
		catch (Exception ex)
		{
			logger.ProtocolFailure(model, DetectedProtocol.Responses, "Exception", null, ex.Message, null, ex);
			return ProtocolResult.Transient(ex.ToString(), null);
		}

		if (!httpResponse.IsSuccessStatusCode)
		{
			string errorBody  = await httpResponse.Content.ReadAsStringAsync(requestCts.Token);
			int    statusCode = (int)httpResponse.StatusCode;

			// Checked before the streaming-support verdict below: a request refused for asking after
			// thinking summaries (or for offering an effort the model cannot take, or for carrying more
			// media than the provider accepts) says nothing about
			// whether this endpoint can stream, and permanently giving up streaming over it would cost
			// far more than either was worth.
			if (LearnReasoningRefusal(model, statusCode, errorBody, logger)
				|| _media.LearnFromRefusal(model, statusCode, errorBody, _onCapabilityLearned, logger))
			{
				_retryAfterRefusal = true;
				return null;
			}

			// The subscription only streams, so no refusal here is a verdict on streaming support.
			if (_subscription)
				return SubscriptionFailure(model, httpResponse, statusCode, errorBody, logger);

			// An empty body says nothing about streaming support — it is what a busy server emits while
			// it unwinds a previous request, and latching streaming off over it costs the session the
			// incremental output the silence deadline is measured against. Retry it as a transient.
			if (statusCode >= 400 && statusCode < 500 && statusCode != 429 && string.IsNullOrEmpty(errorBody))
			{
				string emptyMessage = $"HTTP {statusCode} with empty response body. Endpoint: {model.Endpoint}";
				logger.ProtocolFailure(
					model, DetectedProtocol.Responses, "Transient",
					statusCode, emptyMessage, errorBody, null);
				return ProtocolResult.Transient(emptyMessage, null);
			}

			// A 4xx other than the 429 handled above is a permanent client error in the streaming path:
			// the provider rejects streaming for this model, so we disable it and fall through to
			// non-streaming. Note: unlike the non-streaming path (which excludes 408/425 as retryable),
			// any non-429 4xx here means streaming is not supported — the caller retries non-streaming.
			if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
			{
				_streamingSupported = false;
				logger.ProtocolFailure(
					model, DetectedProtocol.Responses,
					statusCode == 401 || statusCode == 403 ? "AuthFailure" : "ClientError",
					statusCode, errorBody, errorBody, null);
				return null;
			}

			if (ProtocolHelpers.IsRateLimited(httpResponse, errorBody))
			{
				logger.ProtocolFailure(
					model, DetectedProtocol.Responses, "RateLimited",
					statusCode, errorBody, errorBody, null);
				return ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, errorBody));
			}

			logger.ProtocolFailure(
				model, DetectedProtocol.Responses,
				statusCode >= 500 ? "ServerError" : "Transient",
				statusCode, errorBody, errorBody, null);
			return ProtocolResult.Transient($"HTTP {statusCode}: {errorBody}", ProtocolHelpers.TryGetRetryAfter(httpResponse, errorBody));
		}

		JsonNode? finalResponseNode = null;
		JsonNode? streamError       = null;
		string?   openStreamTag     = null;
		int       liveInputTokens   = 0;
		int       liveCachedTokens  = 0;

		// Every finished output item as it streams. The ChatGPT backend's response.completed can carry
		// an empty output (store=false), in which case these ARE the response.
		JsonArray streamedItems = new JsonArray();

		try
		{
			using (Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(requestCts.Token))
			using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
			{
				while (true)
				{
					string? line = await reader.ReadLineAsync(requestCts.Token);
					if (line == null)
						break;

					requestCts.CancelAfter(idleTimeout);

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
					// Two spellings of the same thing: hosted reasoning models emit a summary of their
					// thinking (the raw chain of thought is never exposed), while open-weight models
					// served over this API — gpt-oss and the local servers that imitate it — emit the
					// raw reasoning text directly. Both are the model thinking out loud, so both stream
					// to the same block.
					else if (eventType == "response.reasoning_summary_text.delta" || eventType == "response.reasoning_text.delta")
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
					// A long think arrives as several summary parts. They carry no separator of their
					// own, so without this the parts run together into one wall of text.
					else if (eventType == "response.reasoning_summary_part.added" && openStreamTag == StreamTag.Thinking)
					{
						bundle.Transport?.OnStreamChunk(StreamTag.Thinking, "\n\n");
					}
					else if (eventType == "response.output_item.done" && eventNode["item"] != null)
					{
						streamedItems.Add(eventNode["item"]!.DeepClone());
					}
					// An incomplete response is still a response: CommitResponse reads why it stopped.
					else if (eventType == "response.completed" || eventType == "response.done" || eventType == "response.incomplete")
					{
						finalResponseNode = eventNode["response"];
						break;
					}
					// A failure reported inside a 200 stream: the error rides on the response, or on
					// the event itself for a bare "error".
					else if (eventType == "response.failed" || eventType == "error")
					{
						streamError = eventNode["response"]?["error"] ?? eventNode["error"] ?? eventNode;
						break;
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
			// A stream that went silent past the deadline is a timeout, not a user cancel.
			ProtocolResult? timeout = ProtocolHelpers.TimeoutOrRethrow(cancellationToken, model);
			if (timeout != null)
				return timeout;
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

		if (streamError != null)
			return StreamFailure(model, streamError, httpResponse.Headers, logger);

		if (finalResponseNode != null)
		{
			JsonArray? output = finalResponseNode["output"] as JsonArray;
			if ((output == null || output.Count == 0) && streamedItems.Count > 0)
				finalResponseNode["output"] = streamedItems;
			return CommitResponse(bundle, finalResponseNode, model);
		}

		return ProtocolResult.Transient("Stream ended without a response.completed event", null);
	}

	// Classifies a failure reported inside the stream. Overflow compacts; a usage or rate limit waits
	// out its reset; anything else is retried as transient, as the Codex CLI does.
	private static ProtocolResult StreamFailure(LlmModel model, JsonNode error, HttpResponseHeaders headers, SessionLogger logger)
	{
		string code    = error["code"]?.ToString() ?? error["type"]?.ToString() ?? string.Empty;
		string message = error["message"]?.ToString() ?? error.ToJsonString();
		string detail  = code.Length > 0 ? $"{code}: {message}" : message;

		ProtocolResult result;
		if (code == "context_length_exceeded" || ProtocolHelpers.IsContextOverflow(message))
		{
			result = ProtocolHelpers.ContextOverflowFailure("Responses", 400, detail, logger, model.Config.Name, model.Endpoint, model.ConfigId);
		}
		else if (code == "usage_limit_reached" || code == "rate_limit_exceeded")
		{
			// The limit's own wording names the plan and reset only on the subscription; the API's says enough.
			JsonObject wrapped = new JsonObject();
			wrapped["error"]   = error.DeepClone();
			string body        = wrapped.ToJsonString();
			string usage       = ChatGptSubscription.IsEndpoint(model.Endpoint) ? ChatGptSubscription.DescribeLimit(headers, body, DateTimeOffset.UtcNow) : detail;
			logger.ProtocolFailure(model, DetectedProtocol.Responses, "RateLimited", 429, usage, error.ToJsonString(), null);
			result = ProtocolResult.RateLimited(ProtocolHelpers.ResetTimeFromBody(error.ToJsonString()), usage);
		}
		else
		{
			logger.ProtocolFailure(model, DetectedProtocol.Responses, "Transient", null, detail, error.ToJsonString(), null);
			result = ProtocolResult.Transient($"Stream failed: {detail}", null);
		}
		return result;
	}

	// Maps a refused subscription request. A 401 is first answered by refreshing the token and
	// re-sending once (null = re-send); the rest follow the non-streaming path's rules.
	private ProtocolResult? SubscriptionFailure(LlmModel model, HttpResponseMessage httpResponse, int statusCode, string errorBody, SessionLogger logger)
	{
		ProtocolResult? result;
		if (statusCode == 401 && !_authRetried)
		{
			_authRetried       = true;
			_refreshAuth       = true;
			_retryAfterRefusal = true;
			logger.ProtocolFailure(model, DetectedProtocol.Responses, "AuthFailure", statusCode, "Token rejected; refreshing and re-sending", errorBody, null);
			result = null;
		}
		else if (ProtocolHelpers.IsRateLimited(httpResponse, errorBody))
		{
			string usage = ChatGptSubscription.DescribeLimit(httpResponse.Headers, errorBody, DateTimeOffset.UtcNow);
			logger.ProtocolFailure(model, DetectedProtocol.Responses, "RateLimited", statusCode, usage, errorBody, null);
			result = ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, errorBody), usage);
		}
		else if (ProtocolHelpers.IsPermanentClientError(statusCode))
		{
			result = ProtocolHelpers.IsContextOverflow(errorBody)
				? ProtocolHelpers.ContextOverflowFailure("Responses", statusCode, errorBody, logger, model.Config.Name, model.Endpoint, model.ConfigId)
				: ProtocolHelpers.Failure("Responses", statusCode, errorBody, logger, model.Config.Name, model.Endpoint, model.ConfigId);
		}
		else
		{
			result = ProtocolHelpers.TransientFailure("Responses", statusCode, errorBody, logger, model.Config.Name, model.Endpoint, model.ConfigId, httpResponse);
		}
		return result;
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

		StringBuilder          assistantTextBuilder = new StringBuilder();
		StringBuilder          thinkingBuilder      = new StringBuilder();
		List<SemanticToolCall> toolCalls            = new List<SemanticToolCall>();

		foreach (JsonNode? item in output)
		{
			if (item == null)
				continue;

			string? type = item["type"]?.GetValue<string>();
			if (type == "function_call")
			{
				string id   = item["call_id"]?.GetValue<string>() ?? item["id"]?.GetValue<string>() ?? string.Empty;
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
						string? text      = block?["text"]?.GetValue<string>();
						if (blockType == "output_text" && !string.IsNullOrEmpty(text))
						{
							assistantTextBuilder.Append(text);
						}
					}
				}
			}
			else if (type == "reasoning")
			{
				// A reasoning item carries its text in "summary" (hosted models, summarizing a chain of
				// thought that is never itself exposed) or in "content" (open-weight models, which return
				// the raw reasoning). Reading only content left the committed thinking empty for every
				// hosted reasoning model, so both are collected.
				AppendReasoningText(item["summary"]?.AsArray(), thinkingBuilder);
				AppendReasoningText(item["content"]?.AsArray(), thinkingBuilder);
			}
		}

		string assistantText = assistantTextBuilder.ToString();
		string thinking      = thinkingBuilder.ToString();



		if (_subscription)
		{
			// Nothing lives server-side: what was just sent becomes the running history, and the fan-out
			// that follows records the turn itself, preceded by its reasoning.
			JsonArray history = _rehydratedInput ?? new JsonArray();
			foreach (JsonNode? item in _deltaInput)
				history.Add(item!.DeepClone());
			_rehydratedInput       = history;
			_pendingReasoning      = ReplayableReasoning(output);
			_assistantTurnOnServer = false;
			_deltaInput.Clear();
		}
		else
		{
			// Capture the server response id for next-turn chaining, then clear rehydrated input
			// and delta buffer so subsequent turns accumulate fresh deltas.
			// The id is in-memory only and never written into canonical state.
			_previousResponseId = responseRoot["id"]?.GetValue<string>();
			_rehydratedInput    = null;
			_deltaInput.Clear();

			// With an id to chain from, this turn is already in the server-side thread, so the fan-out that
			// follows must not append it to the next delta. Without one the next turn resends the delta on
			// its own, and the turn has to be in it.
			_assistantTurnOnServer = _previousResponseId != null;
		}

		// The Responses API reports output clipping via incomplete_details rather than a finish
		// reason; normalize it to "length" so callers detect cut-off replies uniformly.
		string  finishReason     = toolCalls.Count > 0 ? "tool_calls" : "stop";
		string? incompleteReason = responseRoot["incomplete_details"]?["reason"]?.GetValue<string>();
		if (toolCalls.Count == 0 && incompleteReason == "max_output_tokens")
			finishReason = "length";

		(TokenUsageInfo usage, decimal cost) = ExtractUsage(responseRoot, model);

		List<ToolResult> emptyResults = new List<ToolResult>();
		return ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, thinking, toolCalls, emptyResults, finishReason, usage, cost));
	}

	// The response's reasoning items as they can be sent back: encrypted_content and summary kept, the
	// id dropped — with store=false the server holds no item to resolve it against, and naming one is
	// a 404. Items without encrypted_content carry nothing the model can resume from.
	private static JsonArray ReplayableReasoning(JsonArray output)
	{
		JsonArray reasoning = new JsonArray();
		foreach (JsonNode? item in output)
		{
			if (item?["type"]?.GetValue<string>() != "reasoning" || item["encrypted_content"] == null)
				continue;

			JsonObject replay = new JsonObject
			{
				["type"]              = "reasoning",
				["encrypted_content"] = item["encrypted_content"]!.DeepClone(),
				["summary"]           = item["summary"]?.DeepClone() ?? new JsonArray()
			};
			reasoning.Add((JsonNode)replay);
		}
		return reasoning;
	}

	// Appends every text block of a reasoning item's part array, blank-line separated so multiple
	// parts read as the paragraphs they are — the same separation the stream inserts live.
	private static void AppendReasoningText(JsonArray? parts, StringBuilder builder)
	{
		if (parts == null)
			return;

		foreach (JsonNode? part in parts)
		{
			string? text = part?["text"]?.GetValue<string>();
			if (string.IsNullOrEmpty(text))
				continue;
			if (builder.Length > 0)
				builder.Append("\n\n");
			builder.Append(text);
		}
	}

	private static (TokenUsageInfo usage, decimal cost) ExtractUsage(JsonNode responseRoot, LlmModel model)
	{
		TokenUsageInfo usage     = new TokenUsageInfo();
		decimal        cost      = 0m;
		JsonNode?      usageNode = responseRoot["usage"];
		if (usageNode == null)
			return (usage, cost);

		int totalInputTokens   = usageNode["input_tokens"]?.GetValue<int>() ?? 0;
		usage.CompletionTokens = usageNode["output_tokens"]?.GetValue<int>() ?? 0;

		int cachedTokens = usageNode["input_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0;

		// input_tokens already INCLUDES cached tokens — this is the full context the provider processed
		usage.PromptTokens = totalInputTokens;
		usage.CachedTokens = cachedTokens;

		// Prefer a server-reported cost when present; otherwise calculate from fresh token counts.
		decimal?  reported = null;
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
			cost                += (freshInputTokens / 1_000_000m) * model.Config.Cost.Input;
			cost                += (cachedTokens / 1_000_000m) * model.Config.Cost.CacheRead;
			cost                += (usage.CompletionTokens / 1_000_000m) * model.Config.Cost.Output;
		}

		return (usage, cost);
	}

	// Token counting call: uses OpenAI's dedicated /responses/input_tokens/count endpoint (side-effect-free).
	// Falls back to the legacy tracer (max_output_tokens=1) if the count endpoint is unavailable.
	public async Task<TracerResult> CountTokensAsync(
		LlmModel                      model,
		List<ToolDefinition>          tools,
		string?                       forcedToolName,
		Dictionary<string, string>    extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		SessionLogger                 logger,
		CancellationToken             cancellationToken)
	{
		// The subscription backend has no count route, and a max_output_tokens=1 tracer is refused there
		// too. The first real turn's usage measures the conversation instead; the caller treats a failed
		// count as "not known yet".
		if (_subscription)
			return TracerResult.Failed("The ChatGPT subscription cannot count tokens ahead of a turn");

		// An endpoint already found to have no count route goes straight to the tracer.
		string known = _endpointFacts.ResponsesCountRoute;
		if (known == EndpointFacts.NoCountRoute)
			return await ExecuteTracerAsync(model, tools, forcedToolName, extraHeaders, extraPayload, logger, cancellationToken);
		string route = known.Length > 0 ? known : kOpenAiCountRoute;

		// Build the count endpoint URL: model.Endpoint ends with /responses, count endpoint is /responses<route>
		string countEndpoint = model.Endpoint;
		if (countEndpoint.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
		{
			countEndpoint += route;
		}
		else
		{
			// Fallback if the endpoint doesn't follow the expected pattern
			countEndpoint = model.Endpoint.TrimEnd('/') + route;
		}

		JsonObject body = BuildCountBody(model, tools, forcedToolName, extraPayload);
		logger.Write(model.Config.Name, countEndpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		string requestJson = body.ToJsonString();

		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, countEndpoint);
		req.Content            = new StringContent(requestJson, Encoding.UTF8, "application/json");
		req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
		foreach ((string name, string value) in extraHeaders)
		{
			req.Headers.TryAddWithoutValidation(name, value);
		}

		HttpResponseMessage httpResponse;
		string              responseBody;
		try
		{
			using CancellationTokenSource requestCts = ProtocolHelpers.CreateRequestTimeout(model, cancellationToken);
			httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, requestCts.Token);
			responseBody = await httpResponse.Content.ReadAsStringAsync(requestCts.Token);
		}
		catch (OperationCanceledException)
		{
			ProtocolResult? timeout = ProtocolHelpers.TimeoutOrRethrow(cancellationToken, model);
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
				return TracerResult.Failed("Empty response from count endpoint");

			if (known.Length == 0)
				_endpointFacts.RecordResponsesCountRoute(route);

			int inputTokens = root["input_tokens"]?.GetValue<int>() ?? 0;
			return TracerResult.Success(inputTokens, 0);
		}

		// The route is missing: OpenAI's moves on to Meta's spelling, and Meta's missing too means this
		// endpoint has none. A 404 that names the model is about the model, not the route, and teaches
		// nothing about the endpoint every other model on it shares.
		if (statusCode == 404 && !responseBody.Contains("model", StringComparison.OrdinalIgnoreCase))
		{
			if (route == kOpenAiCountRoute)
			{
				_endpointFacts.RecordResponsesCountRoute(kMetaCountRoute);
				return await CountTokensAsync(model, tools, forcedToolName, extraHeaders, extraPayload, logger, cancellationToken);
			}
			_endpointFacts.RecordResponsesCountRoute(EndpointFacts.NoCountRoute);
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

	// Builds the request body for the /responses/input_tokens/count endpoint.
	// Mirrors BuildBody but WITHOUT stream, max_output_tokens, tool_choice, or previous_response_id.
	private JsonObject BuildCountBody(LlmModel model, List<ToolDefinition> tools, string? forcedToolName, Dictionary<string, JsonNode?> extraPayload)
	{
		JsonObject body = new JsonObject();
		body["model"]   = model.Config.Id;

		// Build input array from rehydrated or delta state (no previous_response_id chaining for count)
		JsonArray input = new JsonArray();
		if (_rehydratedInput != null)
		{
			foreach (JsonNode? item in _rehydratedInput)
			{
				input.Add(item!.DeepClone());
			}
		}
		else
		{
			foreach (JsonNode? item in _deltaInput)
			{
				input.Add(item!.DeepClone());
			}
		}
		body["input"] = input;

		if (tools.Count > 0)
		{
			JsonArray  toolsArr   = new JsonArray();
			JsonObject twebsearch = new JsonObject();
			twebsearch["type"]    = "web_search";
			toolsArr.Add((JsonNode)twebsearch);

			foreach (ToolDefinition td in tools)
			{
				JsonObject t = new JsonObject();
				t["type"]    = "function";
				t["name"]    = td.Function.Name;
				if (!string.IsNullOrEmpty(td.Function.Description))
					t["description"] = td.Function.Description;
				if (td.Function.Parameters != null)
					t["parameters"] = td.Function.Parameters.DeepClone();
				toolsArr.Add((JsonNode)t);
			}
			body["tools"] = toolsArr;
			// tool_choice is intentionally omitted for count endpoint
		}

		// Translate the friendly reasoningEffort word into the Responses-native reasoning.effort object.
		// Summaries are never requested here: this endpoint only counts tokens, so there is no thinking
		// to watch and nothing to show.
		string? effort = _reasoningSupported ? ReasoningEffort.OpenAiEffort(model.Config.ReasoningEffort) : null;
		if (effort != null)
		{
			JsonObject reasoning = new JsonObject();
			reasoning["effort"]  = effort;
			body["reasoning"]    = reasoning;
		}

		// Merge extra payload (skip stream, max_output_tokens, tool_choice, previous_response_id)
		foreach ((string name, JsonNode? value) in extraPayload)
		{
			if (name == "stream" || name == "max_output_tokens" || name == "tool_choice" || name == "previous_response_id")
				continue;
			body[name] = value?.DeepClone();
		}

		return body;
	}

	// Tracer call: sends the same request with max_output_tokens=1 to get accurate token counts
	// without generating a meaningful response.
	// Kept as fallback for providers that don't support /responses/input_tokens/count.
	public async Task<TracerResult> ExecuteTracerAsync(
		LlmModel                      model,
		List<ToolDefinition>          tools,
		string?                       forcedToolName,
		Dictionary<string, string>    extraHeaders,
		Dictionary<string, JsonNode?> extraPayload,
		SessionLogger                 logger,
		CancellationToken             cancellationToken)
	{
		try
		{
			JsonObject body = BuildBody(model, tools, forcedToolName, 1, extraPayload);
			logger.Write(model.Config.Name, model.Endpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

			string requestJson = body.ToJsonString();

			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
			req.Content            = new StringContent(requestJson, Encoding.UTF8, "application/json");
			req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
			foreach ((string name, string value) in extraHeaders)
			{
				req.Headers.TryAddWithoutValidation(name, value);
			}

			HttpResponseMessage httpResponse;
			string              responseBody;
			try
			{
				using CancellationTokenSource requestCts = ProtocolHelpers.CreateRequestTimeout(model, cancellationToken);
				httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, requestCts.Token);
				responseBody = await httpResponse.Content.ReadAsStringAsync(requestCts.Token);
			}
			catch (OperationCanceledException)
			{
				ProtocolResult? timeout = ProtocolHelpers.TimeoutOrRethrow(cancellationToken, model);
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
					return TracerResult.Failed("Empty response from Responses API");

				// Responses API reports usage at the top level
				JsonNode? usageNode = root["usage"];
				if (usageNode == null)
					return TracerResult.Failed("No usage info in tracer response");

				int inputTokens  = usageNode["input_tokens"]?.GetValue<int>() ?? 0;
				int cachedTokens = usageNode["input_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0;

				return TracerResult.Success(inputTokens, cachedTokens);
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
}