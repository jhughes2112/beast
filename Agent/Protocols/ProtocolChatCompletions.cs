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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private bool _parallelToolCallsSupported = true;
    private bool _streamingSupported = true;

    // Backends disagree on how reasoning effort is requested, so it is advertised softly with adaptive
    // fallback (see TryAdaptToError): 0 = the OpenAI-standard "reasoning_effort" string, 1 = a "reasoning":
    // { "effort": ... } object (OpenRouter and several gateways), 2 = give up because the server accepts
    // neither. Each step is taken only after the server rejects the previous form with a 400.
    private int _reasoningMode = 0;

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
            if (native != null) _native.Add(native);
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
                    tcArr.Add(tcObj);
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
        _native.Add(msg);
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
                tcArr.Add(tcObj);
            }
            msg["tool_calls"] = tcArr;
        }

        _native.Add(msg);
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
        _native.Add(msg);
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
        ITransportServer transport,
        string sessionId,
        QueryLogger? queryLogger,
        CancellationToken cancellationToken)
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

        for (;;)
        {
            JsonObject body = BuildRequestBody(model, tools, forcedToolName, maxCompletionTokens);
            if (!logged) { logged = true; queryLogger?.Write(model.Config.Name, model.Endpoint, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }

            if (_streamingSupported)
            {
                ProtocolResult? streamResult = await ExecuteStreamingAsync(model, body, extraHeaders, extraPayload, bundle, onProgress, transport, sessionId, cancellationToken);
                if (streamResult != null)
                {
                    if (IsEmptyTurn(streamResult) && emptyRetries < kMaxEmptyRetries)
                    {
                        emptyRetries++;
                        transport.Status(sessionId, $"Empty response, retrying ({emptyRetries}/{kMaxEmptyRetries})");
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
                httpResponse = await PostAsync(model, body, extraHeaders, extraPayload, transport, sessionId, cancellationToken);
                responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                return ProtocolResult.Transient(ex.Message);
            }
            catch (Exception ex)
            {
                return ProtocolResult.Transient(ex.Message);
            }

            if (httpResponse.IsSuccessStatusCode)
            {
                JsonNode? root = JsonNode.Parse(responseBody);
                if (root == null)
                {
                    return ProtocolResult.Transient("Empty response from API");
                }

                string? errMsg = root["error"]?["message"]?.GetValue<string>();
                JsonArray? choices = root["choices"]?.AsArray();
                if (choices == null || choices.Count == 0 || errMsg != null)
                {
                    return ProtocolResult.Transient(errMsg ?? "Empty response from API");
                }

                JsonNode? messageNode = choices[0]?["message"];
                if (messageNode is not JsonObject messageObj)
                {
                    return ProtocolResult.Transient("Response missing message object");
                }

                (string assistantText, List<SemanticToolCall> toolCalls) = ExtractSemantic(messageObj);
                string thinking = ExtractThinking(messageObj);



                string finishReason = choices[0]?["finish_reason"]?.GetValue<string>() ?? string.Empty;
                if (toolCalls.Count > 0) finishReason = "tool_calls";

                (TokenUsageInfo usage, decimal cost) = ExtractUsage(root, model);

                List<ToolResult> emptyResults = new List<ToolResult>();
                ProtocolResult success = ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, thinking, toolCalls, emptyResults, finishReason, usage, cost));
                if (IsEmptyTurn(success) && emptyRetries < kMaxEmptyRetries)
                {
                    emptyRetries++;
                    transport.Status(sessionId, $"Empty response, retrying ({emptyRetries}/{kMaxEmptyRetries})");
                    continue;
                }
                return success;
            }

            bool reasoningConfigured = ReasoningEffort.OpenAiEffort(model.Config.ReasoningEffort) != null;
            if (TryAdaptToError(httpResponse, responseBody, reasoningConfigured))
            {
                continue;
            }

            if (ProtocolHelpers.IsRateLimited(httpResponse, responseBody))
            {
                return ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, responseBody));
            }

            int statusCode = (int)httpResponse.StatusCode;
            if (statusCode == 401 || statusCode == 403)
                return ProtocolResult.Failed($"HTTP {statusCode}: {responseBody}");
            return ProtocolResult.Transient($"HTTP {statusCode}: {responseBody}");
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
            if (n != null) messages.Add(n.DeepClone());
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
                messages.Add(filler);
            }
        }

        body["messages"] = messages;

        if (tools.Count > 0)
        {
            JsonArray toolsArr = new JsonArray();
            foreach (ToolDefinition td in tools)
            {
                toolsArr.Add(JsonNode.Parse(JsonSerializer.Serialize(td, JsonOptions)));
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
                JsonObject fn = new JsonObject();
                fn["name"] = forcedToolName;
                JsonObject choice = new JsonObject();
                choice["type"] = "function";
                choice["function"] = fn;
                body["tool_choice"] = choice;
            }
            else if (_parallelToolCallsSupported)
            {
                body["parallel_tool_calls"] = true;
            }
        }

        body["seed"] = Random.Shared.Next();
        if (maxCompletionTokens > 0) body["max_completion_tokens"] = maxCompletionTokens.Value;

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

    private async Task<HttpResponseMessage> PostAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, Dictionary<string, JsonNode?> extraPayload, ITransportServer transport, string sessionId, CancellationToken cancellationToken)
    {
        string url = model.Endpoint;

        JsonObject obj = (JsonObject)body.DeepClone();
        foreach (KeyValuePair<string, JsonNode?> kv in extraPayload)
        {
            obj[kv.Key] = kv.Value?.DeepClone();
        }

        string requestJson = obj.ToJsonString();

        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");

        foreach (KeyValuePair<string, string> kv in extraHeaders)
        {
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        return await ProtocolHelpers.GetClient().SendAsync(req, cancellationToken);
    }

    private async Task<ProtocolResult?> ExecuteStreamingAsync(
        LlmModel model,
        JsonObject body,
        Dictionary<string, string> extraHeaders,
        Dictionary<string, JsonNode?> extraPayload,
        ListenerBundle bundle,
        LiveUsageProgress onProgress,
        ITransportServer transport,
        string sessionId,
        CancellationToken cancellationToken)
    {
        string url = model.Endpoint;

        JsonObject obj = (JsonObject)body.DeepClone();
        obj["stream"] = true;
        obj["stream_options"] = new JsonObject { ["include_usage"] = true };

        foreach (KeyValuePair<string, JsonNode?> kv in extraPayload)
        {
            obj[kv.Key] = kv.Value?.DeepClone();
        }

        string requestJson = obj.ToJsonString();

        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");

        foreach (KeyValuePair<string, string> kv in extraHeaders)
        {
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return ProtocolResult.Transient(ex.Message);
        }
        catch (Exception ex)
        {
            return ProtocolResult.Transient(ex.Message);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            string errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            int statusCode = (int)httpResponse.StatusCode;

            if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
            {
                _streamingSupported = false;
                return null;
            }

            if (ProtocolHelpers.IsRateLimited(httpResponse, errorBody))
            {
                return ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, errorBody));
            }

            if (statusCode == 401 || statusCode == 403)
                return ProtocolResult.Failed($"HTTP {statusCode}: {errorBody}");
            return ProtocolResult.Transient($"HTTP {statusCode}: {errorBody}");
        }

        StringBuilder contentBuilder = new StringBuilder();
        StringBuilder reasoningBuilder = new StringBuilder();
        List<StreamingToolCall> toolCallAccumulators = new List<StreamingToolCall>();
        string finishReason = string.Empty;
        JsonNode? usageNodeFinal = null;
        string? openStreamTag = null;
        int streamedCharCount = 0;
        int livePromptTokens = 0;

        try
        {
            using (Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken))
            using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
            {
                while (true)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null) break;
                    if (!line.StartsWith("data: ")) continue;

                    string data = line.Substring(6);
                    if (data == "[DONE]") break;

                    JsonNode? chunkNode = JsonNode.Parse(data);
                    if (chunkNode == null) continue;

                    JsonNode? usageNode = chunkNode["usage"];
                    if (usageNode != null)
                    {
                        usageNodeFinal = usageNode;
                        int? promptTokens = usageNode["prompt_tokens"]?.GetValue<int?>();
                        if (promptTokens.HasValue && promptTokens.Value > 0)
                        {
                            livePromptTokens = promptTokens.Value;
                        }
                    }

                    JsonArray? choices = chunkNode["choices"]?.AsArray();
                    if (choices == null || choices.Count == 0) continue;

                    JsonNode? delta = choices[0]?["delta"];
                    if (delta == null) continue;

                    string? fr = choices[0]?["finish_reason"]?.GetValue<string>();
                    if (fr != null) finishReason = fr;

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
                        EmitProgress(model, livePromptTokens, streamedCharCount, onProgress);
                    }

                    string? contentDelta = delta["content"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(contentDelta))
                    {
                        if (openStreamTag != StreamTag.Assistant)
                        {
                            if (openStreamTag != null)
                            {
                                bundle.Transport?.OnStreamEnd(openStreamTag);
                            }
                            bundle.Transport?.OnStreamStart(StreamTag.Assistant);
                            openStreamTag = StreamTag.Assistant;
                        }

                        contentBuilder.Append(contentDelta);
                        bundle.Transport?.OnStreamChunk(StreamTag.Assistant, contentDelta);
                        streamedCharCount += contentDelta.Length;
                        EmitProgress(model, livePromptTokens, streamedCharCount, onProgress);
                    }

                    JsonArray? tcDeltas = delta["tool_calls"]?.AsArray();
                    if (tcDeltas != null)
                    {
                        foreach (JsonNode? tcNode in tcDeltas)
                        {
                            if (tcNode == null) continue;
                            int index = tcNode["index"]?.GetValue<int>() ?? 0;

                            while (toolCallAccumulators.Count <= index)
                            {
                                toolCallAccumulators.Add(new StreamingToolCall());
                            }

                            StreamingToolCall acc = toolCallAccumulators[index];
                            if (tcNode["id"] != null) acc.Id = tcNode["id"]!.GetValue<string>();
                            if (tcNode["function"]?["name"] != null) acc.Name = tcNode["function"]!["name"]!.GetValue<string>();
                            string? argDelta = tcNode["function"]?["arguments"]?.GetValue<string>();
                            if (argDelta != null) acc.Arguments.Append(argDelta);
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
            return ProtocolResult.Transient(ex.Message);
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
    private static void EmitProgress(LlmModel model, int livePromptTokens, int streamedCharCount, LiveUsageProgress onProgress)
    {
        int estimatedOutputTokens = streamedCharCount / 4;
        decimal estimatedCost = (livePromptTokens / 1_000_000m) * model.Config.Cost.Input
                              + (estimatedOutputTokens / 1_000_000m) * model.Config.Cost.Output;
        onProgress(livePromptTokens, estimatedOutputTokens, estimatedCost);
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

    // Pulls semantic content and tool calls out of a non-streaming message object.
    private static (string assistantText, List<SemanticToolCall> toolCalls) ExtractSemantic(JsonObject messageObj)
    {
        string text = messageObj["content"]?.GetValue<string>() ?? string.Empty;

        List<SemanticToolCall> tcs = new List<SemanticToolCall>();
        JsonArray? tcArr = messageObj["tool_calls"]?.AsArray();
        if (tcArr != null)
        {
            foreach (JsonNode? n in tcArr)
            {
                if (n == null) continue;
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
        if (!string.IsNullOrEmpty(reasoningContent)) return reasoningContent;

        JsonArray? details = messageObj["reasoning_details"]?.AsArray();
        if (details != null)
        {
            StringBuilder sb = new StringBuilder();
            foreach (JsonNode? item in details)
            {
                string? text = item?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text)) sb.Append(text);
            }
            if (sb.Length > 0) return sb.ToString();
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
        if (usageNode == null) return (usage, cost);

        int totalPromptTokens = usageNode["prompt_tokens"]?.GetValue<int>() ?? 0;
        usage.CompletionTokens = usageNode["completion_tokens"]?.GetValue<int>() ?? 0;

        int cachedTokens = usageNode["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0;
        int freshPromptTokens = totalPromptTokens - cachedTokens;

        // Store only fresh prompt tokens for session accumulation
        usage.PromptTokens = freshPromptTokens;

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
            cost += (freshPromptTokens / 1_000_000m) * model.Config.Cost.Input;
            cost += (cachedTokens / 1_000_000m) * model.Config.Cost.CacheRead;
            cost += (usage.CompletionTokens / 1_000_000m) * model.Config.Cost.Output;
        }

        return (usage, cost);
    }

    private bool TryAdaptToError(HttpResponseMessage response, string responseBody, bool reasoningConfigured)
    {
        int statusCode = (int)response.StatusCode;
        if (statusCode < 400 || statusCode >= 500 || statusCode == 429) return false;

        string lowerBody = responseBody.ToLowerInvariant();

        // A server that rejects the reasoning hint gets the next softer form, then no hint at all. Checked
        // before the parallel-tool guard so a model can adapt reasoning even after parallel calls are off.
        if (reasoningConfigured && _reasoningMode < 2 && lowerBody.Contains("reasoning"))
        {
            _reasoningMode++;
            return true;
        }

        if (!_parallelToolCallsSupported) return false;

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
}

