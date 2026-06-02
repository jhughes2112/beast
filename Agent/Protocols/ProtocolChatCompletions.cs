using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// ── OpenAI Chat Completions API ───────────────────────────────────────────────
// Wire protocol requires strict user→assistant alternation; two consecutive
// user messages will cause a 400 error. The system message, if present,
// should be the first entry in the array. Tool call results must use the
// "tool" role (not "user") and must immediately follow the assistant message
// that issued the tool call — the ordering is load-bearing.
//
// ChatCompletions is unique among the executing protocols: its native wire state IS the
// canonical ChatCompletions array held by ListenerChatCompletions. It therefore reads its
// request directly from bundle.Canonical and keeps no separate copy. Its IProtocolListener
// members are no-ops (the canonical store already records every event via the fan-out) and
// Rehydrate is a no-op for the same reason. On a successful turn the assistant message is
// written to canonical state through the semantic fan-out path, so the recorded turn is
// normalized identically to every other protocol.
public class ProtocolChatCompletions : IProtocolListener
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    // Pluggable logger — tests redirect this to ctx.Log; default is silent.
    public static Action<string> Log = _ => { };

    private bool _parallelToolCallsSupported = true;
    private bool _streamingSupported = true;

    // Canonical is the native state; nothing to seed. No-op.
    public void Rehydrate(JsonArray canonical) { }

    // The canonical store records all semantic events; this protocol holds no separate state.
    public void OnSystemMessage(IProtocolListener sender, string text) { }
    public void OnUserMessage(IProtocolListener sender, string text) { }
    public void OnAssistantTurn(IProtocolListener sender, string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls) { }
    public void OnToolResult(IProtocolListener sender, string toolCallId, string content) { }
    public void OnStreamStart(IProtocolListener sender, string tag) { }
    public void OnStreamChunk(IProtocolListener sender, string tag, string chunk) { }
    public void OnStreamEnd(IProtocolListener sender, string tag) { }
    public void OnClear() { }


    public async Task<ProtocolResult> ExecuteAsync(
        LlmModel model,
        ListenerBundle bundle,
        List<ToolDefinition> tools,
        int? maxCompletionTokens,
        Dictionary<string, string> extraHeaders,
        Dictionary<string, JsonNode?> extraPayload,
        LiveUsageProgress onProgress,
        ITransportServer transport,
        CancellationToken cancellationToken)
    {
        ListenerChatCompletions listener = bundle.Canonical!;
        for (;;)
        {
            JsonObject body = BuildRequestBody(model, listener, tools, maxCompletionTokens);

            if (_streamingSupported)
            {
                ProtocolResult? streamResult = await ExecuteStreamingAsync(model, body, extraHeaders, extraPayload, listener, bundle, transport, cancellationToken);
                if (streamResult != null) return streamResult;
                // null means the provider rejected streaming; fall through to non-streaming
            }

            HttpResponseMessage httpResponse;
            string responseBody;
            try
            {
                httpResponse = await PostAsync(model, body, extraHeaders, extraPayload, transport, cancellationToken);
                responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                string rspLine = $"[http] RSP {(int)httpResponse.StatusCode}";
                Log(rspLine); transport.Debug(rspLine);
                Log(responseBody); transport.Debug(responseBody);
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
                return ProtocolResult.Failed(ex.Message);
            }

            if (httpResponse.IsSuccessStatusCode)
            {
                JsonNode? root = JsonNode.Parse(responseBody);
                if (root == null)
                {
                    return ProtocolResult.Failed("Empty response from API");
                }

                string? errMsg = root["error"]?["message"]?.GetValue<string>();
                JsonArray? choices = root["choices"]?.AsArray();
                if (choices == null || choices.Count == 0 || errMsg != null)
                {
                    return ProtocolResult.Failed(errMsg ?? "Empty response from API");
                }

                JsonNode? messageNode = choices[0]?["message"];
                if (messageNode is not JsonObject messageObj)
                {
                    return ProtocolResult.Failed("Response missing message object");
                }

                (string assistantText, List<SemanticToolCall> toolCalls) = ExtractSemantic(messageObj);
                string thinking = ExtractThinking(messageObj);

                // Fan-out to all listeners including our own canonical store, which records the
                // normalized assistant turn. Thinking is broadcast but not persisted in canonical.
                bundle.OnAssistantTurn(null!, assistantText, thinking, toolCalls);

                string finishReason = choices[0]?["finish_reason"]?.GetValue<string>() ?? string.Empty;
                if (toolCalls.Count > 0) finishReason = "tool_calls";

                (TokenUsageInfo usage, decimal cost) = ExtractUsage(root, model);

                return ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, toolCalls, finishReason, usage, cost));
            }

            if (TryAdaptToError(httpResponse, responseBody))
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

    // Sends an empty-body POST to detect whether this endpoint speaks ChatCompletions.
    public static async Task<ProbeResult> ProbeAsync(string apiKey, string endpoint)
    {
        try
        {
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            HttpResponseMessage response = await ProtocolHelpers.GetProbeClient().SendAsync(req);
            string body = await response.Content.ReadAsStringAsync();
            int status = (int)response.StatusCode;

            if (status == 404) return ProbeResult.NotSupported("404 on /chat/completions");

            if (status >= 400 && status < 500 && body.Contains("\"error\""))
            {
                return ProbeResult.Supported();
            }

            return ProbeResult.NotSupported($"Unexpected status {status}");
        }
        catch (Exception ex)
        {
            return ProbeResult.Unreachable(ex.Message);
        }
    }

    private JsonObject BuildRequestBody(LlmModel model, ListenerChatCompletions listener, List<ToolDefinition> tools, int? maxCompletionTokens)
    {
        JsonObject body = new JsonObject();
        body["model"] = model.Config.Id;

        // System prompts arrive through the bundle as semantic events (see BeastSession.RaiseSystemPrompt),
        // so the messages array is sent verbatim with no prepended global system block.
        JsonArray messages = new JsonArray();

        foreach (JsonNode? n in listener.State)
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
            if (_parallelToolCallsSupported) body["parallel_tool_calls"] = true;
        }

        body["seed"] = Random.Shared.Next();
        if (maxCompletionTokens > 0) body["max_completion_tokens"] = maxCompletionTokens.Value;

        return body;
    }

    private async Task<HttpResponseMessage> PostAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, Dictionary<string, JsonNode?> extraPayload, ITransportServer transport, CancellationToken cancellationToken)
    {
        string url = model.Endpoint;

        JsonObject obj = (JsonObject)body.DeepClone();
        foreach (KeyValuePair<string, JsonNode?> kv in extraPayload)
        {
            obj[kv.Key] = kv.Value?.DeepClone();
        }

        string requestJson = obj.ToJsonString();
        string postLine = $"[http] POST {url}";
        Log(postLine); transport.Debug(postLine);
        Log(requestJson); transport.Debug(requestJson);

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
        ListenerChatCompletions listener,
        ListenerBundle bundle,
        ITransportServer transport,
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
        string postLine = $"[http] POST {url} (streaming)";
        Log(postLine); transport.Debug(postLine);
        Log(requestJson); transport.Debug(requestJson);

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
            return ProtocolResult.Failed(ex.Message);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            string errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            int statusCode = (int)httpResponse.StatusCode;
            string errLine = $"[http] RSP {statusCode} (streaming error)";
            Log(errLine); transport.Debug(errLine);
            Log(errorBody); transport.Debug(errorBody);

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
                        if (openStreamTag != null) bundle.OnStreamEnd(listener, openStreamTag);
                            bundle.OnStreamStart(listener, StreamTag.Thinking);
                            openStreamTag = StreamTag.Thinking;
                        }

                        reasoningBuilder.Append(reasoningDelta);
                        bundle.OnStreamChunk(listener, StreamTag.Thinking, reasoningDelta);
                }

                string? contentDelta = delta["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(contentDelta))
                {
                    if (openStreamTag != StreamTag.Assistant)
                    {
                        if (openStreamTag != null) bundle.OnStreamEnd(listener, openStreamTag);
                            bundle.OnStreamStart(listener, StreamTag.Assistant);
                            openStreamTag = StreamTag.Assistant;
                        }

                        contentBuilder.Append(contentDelta);
                        bundle.OnStreamChunk(listener, StreamTag.Assistant, contentDelta);
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

        if (openStreamTag != null)
        {
            bundle.OnStreamEnd(listener, openStreamTag);
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

        // Fan-out to all listeners including our own canonical store, which records the
        // normalized assistant turn. The transport listener emits the committed Thinking +
        // Output frames that replace the live stream block on the client side. Thinking is
        // broadcast but not persisted in canonical state.
        bundle.OnAssistantTurn(null!, assistantText, thinking, semanticToolCalls);

        (TokenUsageInfo tokenUsage, decimal cost) = ExtractUsageFromNode(usageNodeFinal, model);

        return ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, semanticToolCalls, finishReason, tokenUsage, cost));
    }

    private sealed class StreamingToolCall
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public StringBuilder Arguments = new StringBuilder();
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

    private static void EmitNonStreamingThinking(ITransportServer transport, JsonNode root)
    {
        JsonNode? messageNode = root["choices"]?[0]?["message"];
        if (messageNode is not JsonObject messageObj) return;
        string thinking = ExtractThinking(messageObj);
        if (!string.IsNullOrEmpty(thinking)) transport.Thinking(thinking);
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

        usage.PromptTokens = usageNode["prompt_tokens"]?.GetValue<int>() ?? 0;
        usage.CompletionTokens = usageNode["completion_tokens"]?.GetValue<int>() ?? 0;
        usage.TotalTokens = usageNode["total_tokens"]?.GetValue<int>() ?? (usage.PromptTokens + usage.CompletionTokens);

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
            cost += (usage.PromptTokens / 1_000_000m) * model.Config.Cost.Input;
            cost += (usage.CompletionTokens / 1_000_000m) * model.Config.Cost.Output;
        }

        return (usage, cost);
    }

    private bool TryAdaptToError(HttpResponseMessage response, string responseBody)
    {
        int statusCode = (int)response.StatusCode;
        if (statusCode < 400 || statusCode >= 500 || statusCode == 429) return false;
        if (!_parallelToolCallsSupported) return false;

        string lowerBody = responseBody.ToLowerInvariant();

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
