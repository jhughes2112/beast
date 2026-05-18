using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;


// Wire protocol for the Anthropic Messages API.
// Unlike OpenAI-compatible APIs, Anthropic uses x-api-key auth, a top-level system field,
// max_tokens is required, and the response shape uses content blocks with stop_reason.
public class ProtocolAnthropic : IProtocol
{
    private const string AnthropicVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private bool _streamingSupported = true;

    public async Task<ProviderCallResult> ExecuteAsync(
        LlmModel model,
        List<ConversationMessage> messages,
        List<ToolDefinition> tools,
        int maxCompletionTokens,
        Dictionary<string, string> extraHeaders,
        Dictionary<string, JsonNode?> extraPayload,
        IStreamingMessage? stream,
        CancellationToken cancellationToken)
    {
        (string? system, List<JsonObject> anthropicMessages) = BuildMessages(messages);

        JsonObject body = BuildBody(model, system, anthropicMessages, tools, maxCompletionTokens, extraPayload);

        if (stream != null && _streamingSupported)
        {
            ProviderCallResult? streamResult = await ExecuteStreamingAsync(model, body, extraHeaders, stream, cancellationToken);
            if (streamResult != null) return streamResult;
            // null means the provider rejected streaming; fall through to non-streaming
        }

        return await ExecuteBlockingAsync(model, body, extraHeaders, cancellationToken);
    }

    private static JsonObject BuildBody(LlmModel model, string? system, List<JsonObject> anthropicMessages, List<ToolDefinition> tools, int maxCompletionTokens, Dictionary<string, JsonNode?> extraPayload)
    {
        JsonObject body = new JsonObject();
        body["model"] = model.Config.Id;
        body["max_tokens"] = maxCompletionTokens > 0 ? maxCompletionTokens : 4096;

        if (system != null)
        {
            body["system"] = system;
        }

        JsonArray msgArray = new JsonArray();
        foreach (JsonObject item in anthropicMessages)
        {
            msgArray.Add(item);
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

    private static async Task<ProviderCallResult> ExecuteBlockingAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, CancellationToken cancellationToken)
    {
        HttpResponseMessage httpResponse;
        string responseBody;
        try
        {
            httpResponse = await ProtocolHelpers.GetClient().SendAsync(BuildRequest(model, body, extraHeaders), cancellationToken);
            responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProviderCallResult.Failed(ex.Message);
        }

        if (httpResponse.IsSuccessStatusCode)
        {
            AnthropicResponse? response = JsonSerializer.Deserialize<AnthropicResponse>(responseBody, JsonOptions);
            if (response == null || response.Content.Count == 0)
            {
                return ProviderCallResult.Failed("Empty response from Anthropic API");
            }

            return ProviderCallResult.Succeeded(BuildPayload(model, response));
        }

        if (ProtocolHelpers.IsRateLimited(httpResponse, responseBody))
        {
            return ProviderCallResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, responseBody));
        }

        int statusCode = (int)httpResponse.StatusCode;
        if (statusCode >= 500 || statusCode == 401 || statusCode == 403)
        {
            return ProviderCallResult.PermanentFailure($"HTTP {statusCode}: {responseBody}");
        }

        return ProviderCallResult.Failed($"HTTP {statusCode}: {responseBody}");
    }

    private async Task<ProviderCallResult?> ExecuteStreamingAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, IStreamingMessage stream, CancellationToken cancellationToken)
    {
        JsonObject streamBody = JsonNode.Parse(body.ToJsonString())!.AsObject();
        streamBody["stream"] = true;

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
            return ProviderCallResult.Failed(ex.Message);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            string errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            int statusCode = (int)httpResponse.StatusCode;

            if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
            {
                // Provider rejected streaming; disable it for this instance and signal fallback.
                _streamingSupported = false;
                return null;
            }

            if (ProtocolHelpers.IsRateLimited(httpResponse, errorBody))
            {
                return ProviderCallResult.RateLimited(ProtocolHelpers.ComputeRetryAfterTime(httpResponse, errorBody));
            }

            if (statusCode >= 500 || statusCode == 401 || statusCode == 403)
            {
                return ProviderCallResult.PermanentFailure($"HTTP {statusCode}: {errorBody}");
            }

            return ProviderCallResult.Failed($"HTTP {statusCode}: {errorBody}");
        }

        // Anthropic SSE events:
        //   message_start        — message.usage.input_tokens
        //   content_block_start  — index, content_block.type ("text" or "tool_use"), .id/.name for tool_use
        //   content_block_delta  — index, delta.type ("text_delta" or "input_json_delta"), delta.text / delta.partial_json
        //   content_block_stop   — index
        //   message_delta        — delta.stop_reason, usage.output_tokens
        //   message_stop         — end of stream
        StringBuilder contentBuilder = new StringBuilder();
        Dictionary<int, AnthropicStreamingToolCall> toolCallAccumulators = new Dictionary<int, AnthropicStreamingToolCall>();
        string stopReason = "end_turn";
        int inputTokens = 0;
        int outputTokens = 0;

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
                    string? blockType = node["content_block"]?["type"]?.GetValue<string>();

                    if (blockType == "tool_use")
                    {
                        AnthropicStreamingToolCall acc = new AnthropicStreamingToolCall();
                        acc.Id = node["content_block"]?["id"]?.GetValue<string>() ?? "";
                        acc.Name = node["content_block"]?["name"]?.GetValue<string>() ?? "";
                        toolCallAccumulators[index] = acc;
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
                            if (contentBuilder.Length == 0)
                            {
                                stream.StreamStart(StreamTag.Assistant);
                            }

                            contentBuilder.Append(text);
                            stream.StreamChunk(text);
                        }
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        string? partialJson = node["delta"]?["partial_json"]?.GetValue<string>();
                        if (partialJson != null && toolCallAccumulators.TryGetValue(index, out AnthropicStreamingToolCall? acc))
                        {
                            acc.Arguments.Append(partialJson);
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

        if (contentBuilder.Length > 0)
        {
            stream.StreamEnd(StreamTag.Assistant);
        }

        ConversationMessage message = new ConversationMessage { Role = "assistant" };
        string finishReason;
        if (toolCallAccumulators.Count > 0)
        {
            message.ToolCalls = new List<ConversationToolCall>();
            foreach (AnthropicStreamingToolCall acc in toolCallAccumulators.Values)
            {
                message.ToolCalls.Add(new ConversationToolCall
                {
                    Id = acc.Id,
                    Type = "function",
                    Function = new ConversationFunctionCall { Name = acc.Name, Arguments = acc.Arguments.ToString() }
                });
            }

            message.Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : "";
            finishReason = "tool_calls";
        }
        else
        {
            message.Content = contentBuilder.ToString();
            finishReason = stopReason;
        }

        TokenUsageInfo usage = new TokenUsageInfo
        {
            PromptTokens = inputTokens,
            CompletionTokens = outputTokens,
            TotalTokens = inputTokens + outputTokens
        };

        decimal cost = (inputTokens / 1_000_000m) * model.Config.Cost.Input
                     + (outputTokens / 1_000_000m) * model.Config.Cost.Output;

        return ProviderCallResult.Succeeded(new ProviderCallPayload(message, finishReason, usage, cost));
    }

    private sealed class AnthropicStreamingToolCall
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Arguments = new StringBuilder();
    }

    // Sends an empty-body POST to detect whether this endpoint speaks the Anthropic Messages API.
    // Anthropic always returns a structured error on /v1/messages with a missing-field 400;
    // the error type field distinguishes it from OpenAI-shaped errors.
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

            // Anthropic errors have a top-level "type":"error" wrapper: {"type":"error","error":{"type":"invalid_request_error",...}}
            // OpenAI-compatible servers use a flat shape without that wrapper: {"error":{"type":"invalid_request_error",...}}
            if (status >= 400 && status < 500 && body.Contains("\"type\":\"error\"") && body.Contains("\"invalid_request_error\""))
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

    // Extracts the system prompt and converts the remaining messages into Anthropic's format.
    // Anthropic requires strictly alternating user/assistant turns, so consecutive tool results
    // (role == "tool") are merged into a single user message as tool_result content blocks.
    private static (string? system, List<JsonObject> messages) BuildMessages(List<ConversationMessage> messages)
    {
        string? system = null;
        List<JsonObject> result = new List<JsonObject>();

        foreach (ConversationMessage msg in messages)
        {
            if (msg.Role == "system")
            {
                // Concatenate multiple system messages if they exist.
                system = system == null ? msg.Content : $"{system}\n\n{msg.Content}";
                continue;
            }

            if (msg.Role == "tool")
            {
                // Merge consecutive tool results into the last user message's content array.
                JsonObject? lastUser = result.Count > 0 && result[result.Count - 1]["role"]?.GetValue<string>() == "user"
                    ? result[result.Count - 1]
                    : null;

                if (lastUser == null)
                {
                    lastUser = new JsonObject();
                    lastUser["role"] = "user";
                    lastUser["content"] = new JsonArray();
                    result.Add(lastUser);
                }

                JsonArray content = lastUser["content"]!.AsArray();
                JsonObject block = new JsonObject();
                block["type"] = "tool_result";
                block["tool_use_id"] = msg.ToolCallId ?? "";
                block["content"] = msg.Content ?? "";
                content.Add(block);
                continue;
            }

            if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                JsonArray content = new JsonArray();

                if (!string.IsNullOrEmpty(msg.Content))
                {
                    JsonObject textBlock = new JsonObject();
                    textBlock["type"] = "text";
                    textBlock["text"] = msg.Content;
                    content.Add(textBlock);
                }

                foreach (ConversationToolCall tc in msg.ToolCalls)
                {
                    JsonObject toolBlock = new JsonObject();
                    toolBlock["type"] = "tool_use";
                    toolBlock["id"] = tc.Id;
                    toolBlock["name"] = tc.Function.Name;
                    toolBlock["input"] = JsonNode.Parse(tc.Function.Arguments) ?? new JsonObject();
                    content.Add(toolBlock);
                }

                JsonObject assistantMsg = new JsonObject();
                assistantMsg["role"] = "assistant";
                assistantMsg["content"] = content;
                result.Add(assistantMsg);
                continue;
            }

            if (msg.Role == "assistant")
            {
                JsonObject assistantMsg = new JsonObject();
                assistantMsg["role"] = "assistant";
                assistantMsg["content"] = msg.Content ?? "";
                result.Add(assistantMsg);
                continue;
            }

            // user messages
            JsonObject userMsg = new JsonObject();
            userMsg["role"] = "user";
            userMsg["content"] = msg.Content ?? "";
            result.Add(userMsg);
        }

        return (system, result);
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

    private static ProviderCallPayload BuildPayload(LlmModel model, AnthropicResponse response)
    {
        List<ConversationToolCall> toolCalls = new List<ConversationToolCall>();
        string? assistantText = null;

        foreach (AnthropicContentBlock block in response.Content)
        {
            if (block.Type == "tool_use")
            {
                toolCalls.Add(new ConversationToolCall
                {
                    Id = block.Id ?? "",
                    Type = "function",
                    Function = new ConversationFunctionCall
                    {
                        Name = block.Name ?? "",
                        Arguments = block.Input?.ToJsonString() ?? "{}"
                    }
                });
            }
            else if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
            {
                assistantText = block.Text;
            }
        }

        ConversationMessage message = new ConversationMessage { Role = "assistant" };
        string finishReason;
        if (toolCalls.Count > 0)
        {
            message.ToolCalls = toolCalls;
            message.Content = assistantText ?? "";
            finishReason = "tool_calls";
        }
        else
        {
            message.Content = assistantText;
            finishReason = response.StopReason ?? "end_turn";
        }

        TokenUsageInfo usage = new TokenUsageInfo();
        decimal cost = 0m;

        if (response.Usage != null)
        {
            usage = new TokenUsageInfo
            {
                PromptTokens = response.Usage.InputTokens,
                CompletionTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.InputTokens + response.Usage.OutputTokens
            };

            cost += (response.Usage.InputTokens / 1_000_000m) * model.Config.Cost.Input;
            cost += (response.Usage.OutputTokens / 1_000_000m) * model.Config.Cost.Output;
        }

        return new ProviderCallPayload(message, finishReason, usage, cost);
    }
}

// Anthropic wire types — used only by ProtocolAnthropic.

class AnthropicResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public List<AnthropicContentBlock> Content { get; set; } = new();
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("usage")] public AnthropicUsage? Usage { get; set; }
}

class AnthropicContentBlock
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("input")] public JsonObject? Input { get; set; }
}

class AnthropicUsage
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
}
