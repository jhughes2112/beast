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


// Wire protocol for OpenAI-compatible /chat/completions endpoints.
public class ProtocolChatCompletions : IProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Pluggable logger — tests redirect this to ctx.Log; default writes to stderr.
    public static Action<string> Log = line => Console.Error.WriteLine(line);

    private bool _parallelToolCallsSupported = true;
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
        for (;;)
        {
            ChatCompletionRequest request = BuildRequest(model, messages, tools, maxCompletionTokens);

            if (stream != null && _streamingSupported)
            {
                ProviderCallResult? streamResult = await ExecuteStreamingAsync(model, request, extraHeaders, extraPayload, stream, cancellationToken);
                if (streamResult != null) return streamResult;
                // null means the provider rejected streaming; fall through to non-streaming
            }

            HttpResponseMessage httpResponse;
            string responseBody;
            try
            {
                httpResponse = await PostAsync(model, request, extraHeaders, extraPayload, cancellationToken);
                    responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                    Log($"[http] RSP {(int)httpResponse.StatusCode}");
                    Log(responseBody);
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
                ChatCompletionResponse? response = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions);
                if (response == null || response.Choices.Count == 0 || response.Error != null)
                {
                    return ProviderCallResult.Failed(response?.Error?.Message ?? "Empty response from API");
                }

                return ProviderCallResult.Succeeded(BuildPayload(model, response));
            }

            if (TryAdaptToError(httpResponse, responseBody))
            {
                continue;
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
    }

    // Sends an empty-body POST to detect whether this endpoint speaks ChatCompletions.
    // A 400 with a recognizable error shape means it does; a 404 means it does not.
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

            // Any 4xx other than 404 with a JSON body indicates the path exists.
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

    private ChatCompletionRequest BuildRequest(LlmModel model, List<ConversationMessage> messages, List<ToolDefinition> tools, int maxCompletionTokens)
    {
        return new ChatCompletionRequest
        {
            Model = model.Config.Id,
            Messages = messages,
            Tools = tools.Count > 0 ? tools : null,
            ParallelToolCalls = tools.Count > 0 && _parallelToolCallsSupported ? true : null,
            Seed = Random.Shared.Next(),
            MaxCompletionTokens = maxCompletionTokens > 0 ? maxCompletionTokens : null
        };
    }

    private async Task<HttpResponseMessage> PostAsync(LlmModel model, ChatCompletionRequest request, Dictionary<string, string> extraHeaders, Dictionary<string, JsonNode?> extraPayload, CancellationToken cancellationToken)
    {
        string url = model.Endpoint;

        JsonNode? node = JsonNode.Parse(JsonSerializer.Serialize(request, JsonOptions));
        JsonObject obj = node!.AsObject();

        foreach (KeyValuePair<string, JsonNode?> kv in extraPayload)
        {
            obj[kv.Key] = kv.Value?.DeepClone();
        }

        string requestJson = obj.ToJsonString();
        Log($"[http] POST {url}");
        Log("[http] >>>");
        Log(requestJson);
        Log("[http] <<<");

        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");

        foreach (KeyValuePair<string, string> kv in extraHeaders)
        {
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        return await ProtocolHelpers.GetClient().SendAsync(req, cancellationToken);
    }

    private async Task<ProviderCallResult?> ExecuteStreamingAsync(
        LlmModel model,
        ChatCompletionRequest request,
        Dictionary<string, string> extraHeaders,
        Dictionary<string, JsonNode?> extraPayload,
        IStreamingMessage stream,
        CancellationToken cancellationToken)
    {
        string url = model.Endpoint;

        JsonNode? node = JsonNode.Parse(JsonSerializer.Serialize(request, JsonOptions));
        JsonObject obj = node!.AsObject();
        obj["stream"] = true;
        obj["stream_options"] = new JsonObject { ["include_usage"] = true };

        foreach (KeyValuePair<string, JsonNode?> kv in extraPayload)
        {
            obj[kv.Key] = kv.Value?.DeepClone();
        }

        string requestJson = obj.ToJsonString();
        Log($"[http] POST {url} (streaming)");
        Log("[http] >>>");
        Log(requestJson);
        Log("[http] <<<");

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
        catch (Exception ex)
        {
            return ProviderCallResult.Failed(ex.Message);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            string errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            int statusCode = (int)httpResponse.StatusCode;
            Log($"[http] RSP {statusCode} (streaming error)");
            Log(errorBody);

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

        StringBuilder contentBuilder = new StringBuilder();
        List<StreamingToolCall> toolCallAccumulators = new List<StreamingToolCall>();
        string finishReason = "";
        ChatCompletionUsage? usage = null;

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
                    usage = JsonSerializer.Deserialize<ChatCompletionUsage>(usageNode.ToJsonString(), JsonOptions);
                }

                JsonArray? choices = chunkNode["choices"]?.AsArray();
                if (choices == null || choices.Count == 0) continue;

                JsonNode? delta = choices[0]?["delta"];
                if (delta == null) continue;

                string? fr = choices[0]?["finish_reason"]?.GetValue<string>();
                if (fr != null) finishReason = fr;

                string? contentDelta = delta["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(contentDelta))
                {
                    if (contentBuilder.Length == 0)
                    {
                        stream.StreamStart(StreamTag.Assistant);
                    }

                    contentBuilder.Append(contentDelta);
                    stream.StreamChunk(contentDelta);
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

        ConversationMessage message = new ConversationMessage { Role = "assistant" };
        if (contentBuilder.Length > 0)
        {
            stream.StreamEnd(StreamTag.Assistant);
        }

        if (toolCallAccumulators.Count > 0)
        {
            message.ToolCalls = new List<ConversationToolCall>();
            foreach (StreamingToolCall acc in toolCallAccumulators)
            {
                message.ToolCalls.Add(new ConversationToolCall
                {
                    Id = acc.Id,
                    Type = "function",
                    Function = new ConversationFunctionCall { Name = acc.Name, Arguments = acc.Arguments.ToString() }
                });
            }

            finishReason = "tool_calls";
        }
        else
        {
            message.Content = contentBuilder.ToString();
        }

        TokenUsageInfo tokenUsage = new TokenUsageInfo();
        decimal cost = 0m;
        if (usage != null)
        {
            tokenUsage = new TokenUsageInfo
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens
            };

            if (usage.Cost.HasValue)
            {
                cost = usage.Cost.Value;
            }
            else
            {
                cost += (usage.PromptTokens / 1_000_000m) * model.Config.Cost.Input;
                cost += (usage.CompletionTokens / 1_000_000m) * model.Config.Cost.Output;
            }
        }

        return ProviderCallResult.Succeeded(new ProviderCallPayload(message, finishReason, tokenUsage, cost));
    }

    private sealed class StreamingToolCall
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Arguments = new StringBuilder();
    }

    private static ProviderCallPayload BuildPayload(LlmModel model, ChatCompletionResponse response)
    {
        ConversationMessage message = response.Choices[0].Message;
        string finishReason = response.Choices[0].FinishReason ?? "";

        TokenUsageInfo usage = new TokenUsageInfo();
        decimal cost = 0m;

        if (response.Usage != null)
        {
            usage = new TokenUsageInfo
            {
                PromptTokens = response.Usage.PromptTokens,
                CompletionTokens = response.Usage.CompletionTokens,
                TotalTokens = response.Usage.TotalTokens
            };

            if (response.Usage.Cost.HasValue)
            {
                cost = response.Usage.Cost.Value;
            }
            else
            {
                cost += (response.Usage.PromptTokens / 1_000_000m) * model.Config.Cost.Input;
                cost += (response.Usage.CompletionTokens / 1_000_000m) * model.Config.Cost.Output;
            }
        }

        return new ProviderCallPayload(message, finishReason, usage, cost);
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

// ChatCompletions wire types — used only by ProtocolChatCompletions.

class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ConversationMessage> Messages { get; set; } = new();
    [JsonPropertyName("tools")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ToolDefinition>? Tools { get; set; }
    [JsonPropertyName("parallel_tool_calls")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ParallelToolCalls { get; set; }
    [JsonPropertyName("temperature")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Temperature { get; set; }
    [JsonPropertyName("top_p")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? TopP { get; set; }
    [JsonPropertyName("frequency_penalty")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FrequencyPenalty { get; set; }
    [JsonPropertyName("seed")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Seed { get; set; }
    [JsonPropertyName("max_completion_tokens")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxCompletionTokens { get; set; }
}

class ChatCompletionResponse
{
    [JsonPropertyName("choices")] public List<ChatCompletionChoice> Choices { get; set; } = new();
    [JsonPropertyName("usage")] public ChatCompletionUsage? Usage { get; set; }
    [JsonPropertyName("error")] public ChatCompletionError? Error { get; set; }
}

class ChatCompletionChoice
{
    [JsonPropertyName("message")] public ConversationMessage Message { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

class ChatCompletionUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
    [JsonPropertyName("cost")] public decimal? Cost { get; set; }
}

class ChatCompletionError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}
