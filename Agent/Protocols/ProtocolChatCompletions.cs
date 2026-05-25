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
// Reads native messages from ListenerChatCompletions.State. On a successful turn the
// returned assistant message is appended to that state as the producer's
// native record, then fanned out to the other protocol listeners as a semantic turn.
public class ProtocolChatCompletions : IProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    // Pluggable logger — tests redirect this to ctx.Log; default is silent.
    public static Action<string> Log = _ => { };

    private bool _parallelToolCallsSupported = true;
    private bool _streamingSupported = true;


    public async Task<ProtocolResult> ExecuteAsync(
        LlmModel model,
        IProtocolListener bundle,
        List<ToolDefinition> tools,
        int maxCompletionTokens,
        Dictionary<string, string> extraHeaders,
        Dictionary<string, JsonNode?> extraPayload,
        ITransportServer transport,
        CancellationToken cancellationToken)
    {
        ListenerChatCompletions listener = ((ListenerBundle)bundle).Get<ListenerChatCompletions>()!;
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

                JsonObject nativeAssistant = (JsonObject)messageObj.DeepClone();
                if (nativeAssistant["role"] == null) nativeAssistant["role"] = "assistant";

                listener.AppendNativeAssistant(nativeAssistant);

                (string assistantText, List<SemanticToolCall> toolCalls) = ExtractSemantic(messageObj);
                string thinking = ExtractThinking(messageObj);

                // Fan-out to peers (and the transport listener) — sender is our own listener so
                // ChatCompletions does not re-record the turn it just wrote natively.
                bundle.OnAssistantTurn(listener, assistantText, thinking, toolCalls);

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

    private JsonObject BuildRequestBody(LlmModel model, ListenerChatCompletions listener, List<ToolDefinition> tools, int maxCompletionTokens)
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
        if (maxCompletionTokens > 0) body["max_completion_tokens"] = maxCompletionTokens;

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
        IProtocolListener bundle,
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

        // Build native assistant message and append to ChatCompletions state.
        JsonObject native = new JsonObject();
        native["role"] = "assistant";
        native["content"] = assistantText;
        if (!string.IsNullOrEmpty(thinking))
        {
            native["reasoning_content"] = thinking;
        }

        List<SemanticToolCall> semanticToolCalls = new List<SemanticToolCall>();
        if (toolCallAccumulators.Count > 0)
        {
            JsonArray tcArr = new JsonArray();
            foreach (StreamingToolCall acc in toolCallAccumulators)
            {
                JsonObject tcObj = new JsonObject();
                tcObj["id"] = acc.Id;
                tcObj["type"] = "function";
                JsonObject fn = new JsonObject();
                fn["name"] = acc.Name;
                fn["arguments"] = acc.Arguments.ToString();
                tcObj["function"] = fn;
                tcArr.Add(tcObj);

                semanticToolCalls.Add(new SemanticToolCall { Id = acc.Id, Name = acc.Name, ArgumentsJson = acc.Arguments.ToString() });
            }
            native["tool_calls"] = tcArr;
            finishReason = "tool_calls";
        }

        listener.AppendNativeAssistant(native);

        // Fan-out to peers and the transport. The transport listener emits the committed
        // Thinking + Output frames that replace the live stream block on the client side.
        bundle.OnAssistantTurn(listener, assistantText, thinking, semanticToolCalls);

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
