using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// ── OpenAI Responses API ──────────────────────────────────────────────────────
// Wire protocol is stateful by default: passing previous_response_id continues
// a server-managed conversation thread, so do not also replay the full message
// history or the context will be duplicated. Either use stateful chaining via
// previous_response_id, or send the full history yourself — never both.
// Mixing the two approaches within a session produces subtle, hard-to-debug
// context corruption.
//
// Reads native input items from session.ResponsesState. Each completed turn's output
// items are appended verbatim to that state as the producer's native record and fanned
// out semantically to the other protocol listeners.
public class ProtocolResponses : IProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

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
        ListenerResponses listener = ((ListenerBundle)bundle).Get<ListenerResponses>()!;
        JsonObject body = BuildBody(model, listener, tools, maxCompletionTokens, extraPayload);

        if (_streamingSupported)
        {
            ProtocolResult? streamResult = await ExecuteStreamingAsync(model, body, extraHeaders, listener, bundle, transport, cancellationToken);
            if (streamResult != null) return streamResult;
        }

        string requestJson = body.ToJsonString();
        string postLine = $"[http] POST {model.Endpoint}";
        Log(postLine); transport.Debug(postLine);
        Log(requestJson); transport.Debug(requestJson);

        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
        req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
        foreach (KeyValuePair<string, string> kv in extraHeaders)
        {
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        HttpResponseMessage httpResponse;
        string responseBody;
        try
        {
            httpResponse = await ProtocolHelpers.GetClient().SendAsync(req, cancellationToken);
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
            if (root == null) return ProtocolResult.Failed("Empty response from Responses API");

            return CommitResponse(listener, bundle, root, model);
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

    private static JsonObject BuildBody(LlmModel model, ListenerResponses listener, List<ToolDefinition> tools, int maxCompletionTokens, Dictionary<string, JsonNode?> extraPayload)
    {
        JsonObject body = new JsonObject();
        body["model"] = model.Config.Id;

        JsonArray input = new JsonArray();
        foreach (JsonNode? n in listener.State)
        {
            if (n != null) input.Add(n.DeepClone());
        }
        body["input"] = input;
        body["max_output_tokens"] = maxCompletionTokens > 0 ? maxCompletionTokens : 4096;

        // System prompts arrive through the bundle as semantic events (see BeastSession.RaiseSystemPrompt),
        // so `instructions` is not set — the Responses input array carries them itself.

        if (tools.Count > 0)
        {
            JsonArray toolsArr = new JsonArray();
            foreach (ToolDefinition td in tools)
            {
                JsonObject t = new JsonObject();
                t["type"] = "function";
                t["name"] = td.Function.Name;
                if (!string.IsNullOrEmpty(td.Function.Description)) t["description"] = td.Function.Description;
                if (td.Function.Parameters != null) t["parameters"] = td.Function.Parameters.DeepClone();
                toolsArr.Add(t);
            }
            body["tools"] = toolsArr;
            body["tool_choice"] = "auto";
        }

        foreach (KeyValuePair<string, JsonNode?> kv in extraPayload)
        {
            body[kv.Key] = kv.Value?.DeepClone();
        }

        return body;
    }

    private async Task<ProtocolResult?> ExecuteStreamingAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, ListenerResponses listener, IProtocolListener bundle, ITransportServer transport, CancellationToken cancellationToken)
    {
        JsonObject streamBody = (JsonObject)body.DeepClone();
        streamBody["stream"] = true;

        string requestJson = streamBody.ToJsonString();
        string postLine = $"[http] POST {model.Endpoint} (streaming)";
        Log(postLine); transport.Debug(postLine);
        Log(requestJson); transport.Debug(requestJson);

        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
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

        JsonNode? finalResponseNode = null;
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

                JsonNode? eventNode = JsonNode.Parse(data);
                if (eventNode == null) continue;

                string? eventType = eventNode["type"]?.GetValue<string>();

                if (eventType == "response.output_text.delta")
                {
                    string? delta = eventNode["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        if (openStreamTag != StreamTag.Assistant)
                        {
                            if (openStreamTag != null) bundle.OnStreamEnd(listener, openStreamTag);
                            bundle.OnStreamStart(listener, StreamTag.Assistant);
                            openStreamTag = StreamTag.Assistant;
                        }
                        bundle.OnStreamChunk(listener, StreamTag.Assistant, delta);
                    }
                }
                else if (eventType == "response.reasoning_summary_text.delta")
                {
                    string? delta = eventNode["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        if (openStreamTag != StreamTag.Thinking)
                        {
                            if (openStreamTag != null) bundle.OnStreamEnd(listener, openStreamTag);
                            bundle.OnStreamStart(listener, StreamTag.Thinking);
                            openStreamTag = StreamTag.Thinking;
                        }
                        bundle.OnStreamChunk(listener, StreamTag.Thinking, delta);
                    }
                }
                else if (eventType == "response.completed" || eventType == "response.done")
                {
                    finalResponseNode = eventNode["response"];
                    break;
                }
            }
        }

        if (openStreamTag != null)
        {
            bundle.OnStreamEnd(listener, openStreamTag);
        }

        if (finalResponseNode != null)
        {
            return CommitResponse(listener, bundle, finalResponseNode, model);
        }

        return ProtocolResult.Failed("Stream ended without a response.completed event");
    }

    // Appends each native output item to ResponsesState verbatim, then raises a single semantic
    // assistant turn through the bundle for fan-out to peer protocols and the transport.
    private static ProtocolResult CommitResponse(ListenerResponses listener, IProtocolListener bundle, JsonNode responseRoot, LlmModel model)
    {
        JsonArray? output = responseRoot["output"]?.AsArray();
        if (output == null || output.Count == 0)
        {
            string? errMsg = responseRoot["error"]?["message"]?.GetValue<string>();
            return ProtocolResult.Failed(errMsg ?? "Empty response from Responses API");
        }

        StringBuilder assistantTextBuilder = new StringBuilder();
        StringBuilder thinkingBuilder = new StringBuilder();
        List<SemanticToolCall> toolCalls = new List<SemanticToolCall>();

        foreach (JsonNode? item in output)
        {
            if (item == null) continue;
            listener.AppendNativeItem((JsonObject)item.DeepClone());

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
                        if (!string.IsNullOrEmpty(text)) thinkingBuilder.Append(text);
                    }
                }
            }
        }

        string assistantText = assistantTextBuilder.ToString();
        string thinking = thinkingBuilder.ToString();

        bundle.OnAssistantTurn(listener, assistantText, thinking, toolCalls);

        string finishReason = toolCalls.Count > 0 ? "tool_calls" : "stop";

        (TokenUsageInfo usage, decimal cost) = ExtractUsage(responseRoot, model);

        return ProtocolResult.Succeeded(new ProtocolCallPayload(assistantText, toolCalls, finishReason, usage, cost));
    }

    private static (TokenUsageInfo usage, decimal cost) ExtractUsage(JsonNode responseRoot, LlmModel model)
    {
        TokenUsageInfo usage = new TokenUsageInfo();
        decimal cost = 0m;
        JsonNode? usageNode = responseRoot["usage"];
        if (usageNode == null) return (usage, cost);

        usage.PromptTokens = usageNode["input_tokens"]?.GetValue<int>() ?? 0;
        usage.CompletionTokens = usageNode["output_tokens"]?.GetValue<int>() ?? 0;
        usage.TotalTokens = usageNode["total_tokens"]?.GetValue<int>() ?? (usage.PromptTokens + usage.CompletionTokens);

        cost += (usage.PromptTokens / 1_000_000m) * model.Config.Cost.Input;
        cost += (usage.CompletionTokens / 1_000_000m) * model.Config.Cost.Output;

        return (usage, cost);
    }

    public static async Task<ProbeResult> ProbeAsync(string apiKey, string endpoint)
    {
        try
        {
            string probeJson = "{\"model\":\"test\",\"input\":\"\",\"max_output_tokens\":1}";
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Content = new StringContent(probeJson, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await ProtocolHelpers.GetProbeClient().SendAsync(req);
            string body = await response.Content.ReadAsStringAsync();
            int status = (int)response.StatusCode;

            if (status == 404) return ProbeResult.NotSupported("404 on /responses");

            if (body.Contains("\"object\":\"response\""))
            {
                if (body.Contains("\"type\":\"message\"") || body.Contains("\"type\":\"function_call\""))
                    return ProbeResult.Supported();
                return ProbeResult.NotSupported("/responses: object:response but no message/function_call output");
            }

            if (status >= 400 && status < 500 && body.Contains("\"error\"") && !body.Contains("messages"))
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
