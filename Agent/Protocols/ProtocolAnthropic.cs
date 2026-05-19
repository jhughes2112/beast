using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// ── Anthropic Messages API ────────────────────────────────────────────────────
// Wire protocol requires strict user→assistant alternation; two consecutive
// user messages will cause a 400 error. The system prompt is a top-level
// parameter — never put it inside the messages array. Messages must begin
// with a user turn; the array cannot lead with an assistant message.
// Tool results must be wrapped in a user-role message as a tool_result
// content block, not sent as plain text.
//
// Reads native messages from session.AnthropicState. The producing assistant turn is
// appended in native form (preserving thinking blocks with signatures and any unknown
// block types Anthropic returns) and then fanned out semantically to the other listeners.
public class ProtocolAnthropic : IProtocol
{
    private const string AnthropicVersion = "2023-06-01";

    // Pluggable logger — tests redirect this to ctx.Log; default is silent.
    public static Action<string> Log = _ => { };

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
        ListenerAnthropic listener = ((ListenerBundle)bundle).Get<ListenerAnthropic>()!;
        JsonObject body = BuildBody(listener, model, tools, maxCompletionTokens, extraPayload);

        if (_streamingSupported)
        {
            ProtocolResult? streamResult = await ExecuteStreamingAsync(model, body, extraHeaders, listener, bundle, transport, cancellationToken);
            if (streamResult != null) return streamResult;
        }

        return await ExecuteBlockingAsync(model, body, extraHeaders, listener, bundle, transport, cancellationToken);
    }

    private static JsonObject BuildBody(ListenerAnthropic listener, LlmModel model, List<ToolDefinition> tools, int maxCompletionTokens, Dictionary<string, JsonNode?> extraPayload)
    {
        JsonObject body = new JsonObject();
        body["model"] = model.Config.Id;
        body["max_tokens"] = maxCompletionTokens > 0 ? maxCompletionTokens : 4096;

        if (!string.IsNullOrEmpty(listener.System))
        {
            body["system"] = listener.System;
        }

        JsonArray msgArray = new JsonArray();
        foreach (JsonNode? n in listener.Messages)
        {
            if (n != null) msgArray.Add(n.DeepClone());
        }
        body["messages"] = msgArray;

        if (tools.Count > 0)
        {
            body["tools"] = BuildTools(tools);
        }

        foreach (KeyValuePair<string, JsonNode?> kv in extraPayload)
        {
            body[kv.Key] = kv.Value?.DeepClone();
        }

        return body;
    }

    private static JsonArray BuildTools(List<ToolDefinition> tools)
    {
        JsonArray arr = new JsonArray();
        foreach (ToolDefinition tool in tools)
        {
            JsonObject t = new JsonObject();
            t["name"] = tool.Function.Name;
            if (!string.IsNullOrEmpty(tool.Function.Description))
            {
                t["description"] = tool.Function.Description;
            }

            t["input_schema"] = tool.Function.Parameters?.DeepClone() ?? new JsonObject();
            arr.Add(t);
        }

        return arr;
    }

    private static HttpRequestMessage BuildRequest(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders)
    {
        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-api-key", model.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        foreach (KeyValuePair<string, string> kv in extraHeaders)
        {
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        return req;
    }

    private static async Task<ProtocolResult> ExecuteBlockingAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, ListenerAnthropic listener, IProtocolListener bundle, ITransportServer transport, CancellationToken cancellationToken)
    {
        string requestJson = body.ToJsonString();
        string postLine = $"[http] POST {model.Endpoint}";
        Log(postLine); transport.Debug(postLine);
        Log(requestJson); transport.Debug(requestJson);

        HttpResponseMessage httpResponse;
        string responseBody;
        try
        {
            httpResponse = await ProtocolHelpers.GetClient().SendAsync(BuildRequest(model, body, extraHeaders), cancellationToken);
            responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            string rspLine = $"[http] RSP {(int)httpResponse.StatusCode}";
            Log(rspLine); transport.Debug(rspLine);
            Log(responseBody); transport.Debug(responseBody);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProtocolResult.Failed(ex.Message);
        }

        if (httpResponse.IsSuccessStatusCode)
        {
            JsonNode? root = JsonNode.Parse(responseBody);
            if (root == null) return ProtocolResult.Failed("Empty response from Anthropic API");

            JsonArray? content = root["content"]?.AsArray();
            if (content == null || content.Count == 0)
            {
                return ProtocolResult.Failed("Empty response from Anthropic API");
            }

            // Build native assistant message preserving every block verbatim.
            JsonObject nativeMsg = new JsonObject();
            nativeMsg["role"] = "assistant";
            nativeMsg["content"] = content.DeepClone();
            listener.AppendNativeAssistant(nativeMsg);

            (string assistantText, string thinking, List<SemanticToolCall> toolCalls) = ExtractSemantic(content);
            bundle.OnAssistantTurn(listener, assistantText, thinking, toolCalls);

            string finishReason = toolCalls.Count > 0 ? "tool_calls" : (root["stop_reason"]?.GetValue<string>() ?? "end_turn");
            (TokenUsageInfo usage, decimal cost) = ExtractUsage(root, model);

            return ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, toolCalls, finishReason, usage, cost));
        }

        if (ProtocolHelpers.IsRateLimited(httpResponse, responseBody))
        {
            return ProtocolResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, responseBody));
        }

        int statusCode = (int)httpResponse.StatusCode;
        if (statusCode >= 500 || statusCode == 401 || statusCode == 403)
        {
            return ProtocolResult.PermanentFailure($"HTTP {statusCode}: {responseBody}");
        }

        return ProtocolResult.Failed($"HTTP {statusCode}: {responseBody}");
    }

    private async Task<ProtocolResult?> ExecuteStreamingAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, ListenerAnthropic listener, IProtocolListener bundle, ITransportServer transport, CancellationToken cancellationToken)
    {
        JsonObject streamBody = (JsonObject)body.DeepClone();
        streamBody["stream"] = true;

        string requestJson = streamBody.ToJsonString();
        string postLine = $"[http] POST {model.Endpoint} (streaming)";
        Log(postLine); transport.Debug(postLine);
        Log(requestJson); transport.Debug(requestJson);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await ProtocolHelpers.GetClient().SendAsync(BuildRequest(model, streamBody, extraHeaders), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
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

            if (statusCode >= 500 || statusCode == 401 || statusCode == 403)
            {
                return ProtocolResult.PermanentFailure($"HTTP {statusCode}: {errorBody}");
            }

            return ProtocolResult.Failed($"HTTP {statusCode}: {errorBody}");
        }

        // Accumulate full native content blocks per index so we can write the final native
        // assistant message back into Anthropic state preserving block identity and signatures.
        Dictionary<int, JsonObject> blocksByIndex = new Dictionary<int, JsonObject>();
        Dictionary<int, StringBuilder> textBuilders = new Dictionary<int, StringBuilder>();
        Dictionary<int, StringBuilder> argBuilders = new Dictionary<int, StringBuilder>();
        List<int> blockOrder = new List<int>();

        string stopReason = "end_turn";
        int inputTokens = 0;
        int outputTokens = 0;
        string? openStreamTag = null;

        using (Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken))
        using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
        {
            string? eventType = null;

            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;

                if (line.StartsWith("event: "))
                {
                    eventType = line.Substring(7);
                    continue;
                }

                if (!line.StartsWith("data: ")) continue;

                string data = line.Substring(6);
                JsonNode? node = JsonNode.Parse(data);
                if (node == null) continue;

                if (eventType == "message_start")
                {
                    inputTokens = node["message"]?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
                }
                else if (eventType == "content_block_start")
                {
                    int index = node["index"]?.GetValue<int>() ?? 0;
                    JsonNode? cb = node["content_block"];
                    if (cb is JsonObject cbObj)
                    {
                        JsonObject native = (JsonObject)cbObj.DeepClone();
                        blocksByIndex[index] = native;
                        blockOrder.Add(index);

                        string? blockType = native["type"]?.GetValue<string>();
                        if (blockType == "text")
                        {
                            textBuilders[index] = new StringBuilder(native["text"]?.GetValue<string>() ?? string.Empty);
                        }
                        else if (blockType == "tool_use")
                        {
                            argBuilders[index] = new StringBuilder();
                        }
                        else if (blockType == "thinking")
                        {
                            textBuilders[index] = new StringBuilder(native["thinking"]?.GetValue<string>() ?? string.Empty);
                        }
                    }
                }
                else if (eventType == "content_block_delta")
                {
                    int index = node["index"]?.GetValue<int>() ?? 0;
                    string? deltaType = node["delta"]?["type"]?.GetValue<string>();

                    if (deltaType == "text_delta")
                    {
                        string? text = node["delta"]?["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (openStreamTag != StreamTag.Assistant)
                            {
                                if (openStreamTag != null) bundle.OnStreamEnd(listener, openStreamTag);
                                    bundle.OnStreamStart(listener, StreamTag.Assistant);
                                    openStreamTag = StreamTag.Assistant;
                                }

                                if (!textBuilders.TryGetValue(index, out StringBuilder? sb))
                                {
                                    sb = new StringBuilder();
                                    textBuilders[index] = sb;
                                }
                                sb.Append(text);
                                bundle.OnStreamChunk(listener, StreamTag.Assistant, text);
                        }
                    }
                    else if (deltaType == "thinking_delta")
                    {
                        string? text = node["delta"]?["thinking"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (openStreamTag != StreamTag.Thinking)
                            {
                                if (openStreamTag != null) bundle.OnStreamEnd(listener, openStreamTag);
                                    bundle.OnStreamStart(listener, StreamTag.Thinking);
                                    openStreamTag = StreamTag.Thinking;
                                }

                                if (!textBuilders.TryGetValue(index, out StringBuilder? sb))
                                {
                                    sb = new StringBuilder();
                                    textBuilders[index] = sb;
                                }
                                sb.Append(text);
                                bundle.OnStreamChunk(listener, StreamTag.Thinking, text);
                        }
                    }
                    else if (deltaType == "signature_delta")
                    {
                        string? sig = node["delta"]?["signature"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(sig) && blocksByIndex.TryGetValue(index, out JsonObject? blk))
                        {
                            string existing = blk["signature"]?.GetValue<string>() ?? string.Empty;
                            blk["signature"] = existing + sig;
                        }
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        string? partialJson = node["delta"]?["partial_json"]?.GetValue<string>();
                        if (partialJson != null)
                        {
                            if (!argBuilders.TryGetValue(index, out StringBuilder? sb))
                            {
                                sb = new StringBuilder();
                                argBuilders[index] = sb;
                            }
                            sb.Append(partialJson);
                        }
                    }
                }
                else if (eventType == "message_delta")
                {
                    stopReason = node["delta"]?["stop_reason"]?.GetValue<string>() ?? stopReason;
                    outputTokens = node["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
                }
                else if (eventType == "message_stop")
                {
                    break;
                }
            }
        }

        if (openStreamTag != null)
        {
            bundle.OnStreamEnd(listener, openStreamTag);
        }

        // Finalise native blocks: write accumulated text into text/thinking blocks and parse
        // tool_use arguments into input objects.
        JsonArray nativeContent = new JsonArray();
        StringBuilder assistantTextBuilder = new StringBuilder();
        StringBuilder thinkingBuilder = new StringBuilder();
        List<SemanticToolCall> toolCalls = new List<SemanticToolCall>();

        foreach (int idx in blockOrder)
        {
            if (!blocksByIndex.TryGetValue(idx, out JsonObject? block)) continue;
            string? type = block["type"]?.GetValue<string>();

            if (type == "text")
            {
                string text = textBuilders.TryGetValue(idx, out StringBuilder? tb) ? tb.ToString() : string.Empty;
                block["text"] = text;
                assistantTextBuilder.Append(text);
            }
            else if (type == "thinking")
            {
                string text = textBuilders.TryGetValue(idx, out StringBuilder? tb) ? tb.ToString() : string.Empty;
                block["thinking"] = text;
                thinkingBuilder.Append(text);
            }
            else if (type == "tool_use")
            {
                string argsJson = argBuilders.TryGetValue(idx, out StringBuilder? ab) ? ab.ToString() : string.Empty;
                JsonNode? parsed = null;
                if (!string.IsNullOrEmpty(argsJson))
                {
                    try { parsed = JsonNode.Parse(argsJson); } catch (JsonException) { parsed = null; }
                }
                block["input"] = parsed ?? new JsonObject();

                string id = block["id"]?.GetValue<string>() ?? string.Empty;
                string name = block["name"]?.GetValue<string>() ?? string.Empty;
                toolCalls.Add(new SemanticToolCall { Id = id, Name = name, ArgumentsJson = string.IsNullOrEmpty(argsJson) ? "{}" : argsJson });
            }

            nativeContent.Add(block);
        }

        string assistantText = assistantTextBuilder.ToString();
        string thinking = thinkingBuilder.ToString();

        JsonObject nativeMessage = new JsonObject();
        nativeMessage["role"] = "assistant";
        nativeMessage["content"] = nativeContent;
        listener.AppendNativeAssistant(nativeMessage);

        bundle.OnAssistantTurn(listener, assistantText, thinking, toolCalls);

        TokenUsageInfo usage = new TokenUsageInfo
        {
            PromptTokens = inputTokens,
            CompletionTokens = outputTokens,
            TotalTokens = inputTokens + outputTokens
        };

        decimal cost = (inputTokens / 1_000_000m) * model.Config.Cost.Input
                     + (outputTokens / 1_000_000m) * model.Config.Cost.Output;

        string finishReason = toolCalls.Count > 0 ? "tool_calls" : stopReason;

        return ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, toolCalls, finishReason, usage, cost));
    }

    private static (string assistantText, string thinking, List<SemanticToolCall> toolCalls) ExtractSemantic(JsonArray content)
    {
        StringBuilder textBuilder = new StringBuilder();
        StringBuilder thinkingBuilder = new StringBuilder();
        List<SemanticToolCall> toolCalls = new List<SemanticToolCall>();

        foreach (JsonNode? blockNode in content)
        {
            if (blockNode == null) continue;
            string? type = blockNode["type"]?.GetValue<string>();

            if (type == "text")
            {
                string? t = blockNode["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(t)) textBuilder.Append(t);
            }
            else if (type == "thinking")
            {
                string? t = blockNode["thinking"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(t)) thinkingBuilder.Append(t);
            }
            else if (type == "tool_use")
            {
                string id = blockNode["id"]?.GetValue<string>() ?? string.Empty;
                string name = blockNode["name"]?.GetValue<string>() ?? string.Empty;
                JsonNode? input = blockNode["input"];
                string args = input != null ? input.ToJsonString() : "{}";
                toolCalls.Add(new SemanticToolCall { Id = id, Name = name, ArgumentsJson = args });
            }
        }

        return (textBuilder.ToString(), thinkingBuilder.ToString(), toolCalls);
    }

    private static (TokenUsageInfo usage, decimal cost) ExtractUsage(JsonNode root, LlmModel model)
    {
        TokenUsageInfo usage = new TokenUsageInfo();
        decimal cost = 0m;
        JsonNode? usageNode = root["usage"];
        if (usageNode == null) return (usage, cost);

        usage.PromptTokens = usageNode["input_tokens"]?.GetValue<int>() ?? 0;
        usage.CompletionTokens = usageNode["output_tokens"]?.GetValue<int>() ?? 0;
        usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;

        cost += (usage.PromptTokens / 1_000_000m) * model.Config.Cost.Input;
        cost += (usage.CompletionTokens / 1_000_000m) * model.Config.Cost.Output;
        return (usage, cost);
    }

    public static async Task<ProbeResult> ProbeAsync(string apiKey, string endpoint)
    {
        try
        {
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            HttpResponseMessage response = await ProtocolHelpers.GetProbeClient().SendAsync(req);
            string body = await response.Content.ReadAsStringAsync();
            int status = (int)response.StatusCode;

            if (status == 404) return ProbeResult.NotSupported("404 on /messages");

            if (status >= 400 && status < 500 && body.Contains("\"type\":\"error\""))
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
}
