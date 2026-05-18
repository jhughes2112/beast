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


// Responses API wire types — used only by ProtocolResponses.

class ResponsesTool
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; set; }
    [JsonPropertyName("parameters")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonObject? Parameters { get; set; }
}

class ResponsesError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}

class ResponsesApiResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("output")] public List<ResponsesOutputItem> Output { get; set; } = new();
    [JsonPropertyName("usage")] public ResponsesUsage? Usage { get; set; }
    [JsonPropertyName("error")] public ResponsesError? Error { get; set; }
}

class ResponsesOutputItem
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("call_id")] public string? CallId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("content")] public List<ResponsesContentBlock>? Content { get; set; }
}

class ResponsesContentBlock
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
}

class ResponsesUsage
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
}

// Wire protocol for the OpenAI-compatible /responses endpoint.
// The Responses API is stateless: every request must include the full conversation history
// as a flat list of typed input items. Tool calls and their results are represented as
// function_call / function_call_output items rather than as message role entries.
public class ProtocolResponses : IProtocol
{
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
        ITransportServer transport,
        CancellationToken cancellationToken)
    {
        (JsonArray input, string? instructions) = BuildInput(messages);
        List<ResponsesTool>? responsesTools = tools.Count > 0 ? BuildTools(tools) : null;

        JsonObject body = BuildBody(model, input, instructions, responsesTools, maxCompletionTokens, extraPayload);

        if (_streamingSupported)
        {
            ProviderCallResult? streamResult = await ExecuteStreamingAsync(model, body, extraHeaders, transport, cancellationToken);
            if (streamResult != null) return streamResult;
            // null means the provider rejected streaming; fall through to non-streaming
        }

        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
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
            ResponsesApiResponse? response = JsonSerializer.Deserialize<ResponsesApiResponse>(responseBody, JsonOptions);
            if (response == null || response.Output.Count == 0)
            {
                return ProviderCallResult.Failed("Empty response from Responses API");
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

    private static JsonObject BuildBody(LlmModel model, JsonArray input, string? instructions, List<ResponsesTool>? responsesTools, int maxCompletionTokens, Dictionary<string, JsonNode?> extraPayload)
    {
        JsonObject body = new JsonObject();
        body["model"] = model.Config.Id;
        body["input"] = input;
        body["max_output_tokens"] = maxCompletionTokens > 0 ? maxCompletionTokens : 4096;

        if (!string.IsNullOrEmpty(instructions))
        {
            body["instructions"] = instructions;
        }

        if (responsesTools != null)
        {
            body["tools"] = JsonNode.Parse(JsonSerializer.Serialize(responsesTools, JsonOptions));
            body["tool_choice"] = "auto";
        }

        foreach (KeyValuePair<string, JsonNode?> kv in extraPayload)
        {
            body[kv.Key] = kv.Value?.DeepClone();
        }

        return body;
    }

    private async Task<ProviderCallResult?> ExecuteStreamingAsync(LlmModel model, JsonObject body, Dictionary<string, string> extraHeaders, ITransportServer transport, CancellationToken cancellationToken)
    {
        JsonObject streamBody = JsonNode.Parse(body.ToJsonString())!.AsObject();
        streamBody["stream"] = true;

        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
        req.Content = new StringContent(streamBody.ToJsonString(), Encoding.UTF8, "application/json");
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

        // Responses API SSE events relevant to streaming:
        //   response.output_text.delta  — text delta, fields: item_id, output_index, content_index, delta
        //   response.function_call_arguments.delta — tool arg delta, fields: item_id, call_id, delta
        //   response.done — final event, field: response (full ResponsesApiResponse object)
        StringBuilder contentBuilder = new StringBuilder();
        ResponsesApiResponse? finalResponse = null;

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
                        if (contentBuilder.Length == 0)
                        {
                            transport.StreamStart(StreamTag.Assistant);
                        }

                        contentBuilder.Append(delta);
                        transport.StreamChunk(delta);
                    }
                }
                else if (eventType == "response.completed" || eventType == "response.done")
                {
                    JsonNode? responseNode = eventNode["response"];
                    if (responseNode != null)
                    {
                        finalResponse = JsonSerializer.Deserialize<ResponsesApiResponse>(responseNode.ToJsonString(), JsonOptions);
                    }

                    break;
                }
            }
        }

        if (contentBuilder.Length > 0)
        {
            transport.StreamEnd(StreamTag.Assistant);
        }

        if (finalResponse != null)
        {
            return ProviderCallResult.Succeeded(BuildPayload(model, finalResponse));
        }

        return ProviderCallResult.Failed("Stream ended without a response.completed event");
    }

    // Probes the endpoint to determine whether it speaks the Responses API.
    // Makes a minimal valid request and checks whether the response contains
    // usable output (message or function_call items) versus only reasoning stubs.
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

            // A working Responses API returns either:
            //   200 with "object":"response" containing message/function_call output items, or
            //   400 with a model-not-found or auth error (expected for model "test").
            // Some endpoints (e.g. llama.cpp) return 200 with only "reasoning" output items
            // which our protocol handler can't use — check for message/function_call.
            if (body.Contains("\"object\":\"response\""))
            {
                if (body.Contains("\"type\":\"message\"") || body.Contains("\"type\":\"function_call\""))
                    return ProbeResult.Supported();
                return ProbeResult.NotSupported("/responses: object:response but no message/function_call output");
            }

            // ChatCompletions servers complain "messages is required" when given our Responses-format probe.
            // Real Responses API servers reject on model name/auth — neither mentions "messages".
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

    // Converts the flat ConversationMessage list into Responses API input items.
    // The first system message is returned as instructions for the top-level field.
    // Subsequent system messages are emitted as role:system input items in sequence.
    // assistant messages without tool calls become message items with output_text content.
    // assistant messages with tool calls emit function_call items per call.
    // tool result messages (role == "tool") emit function_call_output items.
    private static (JsonArray Input, string? Instructions) BuildInput(List<ConversationMessage> messages)
    {
        JsonArray input = new JsonArray();
        string? instructions = null;

        foreach (ConversationMessage msg in messages)
        {
            if (msg.Role == "system")
            {
                if (instructions == null)
                {
                    // First system message becomes the top-level instructions field.
                    instructions = msg.Content;
                }
                else
                {
                    // Subsequent system messages are injected as input items at their position.
                    JsonObject item = new JsonObject();
                    item["type"] = "message";
                    item["role"] = "system";
                    JsonArray contentArr = new JsonArray();
                    JsonObject block = new JsonObject();
                    block["type"] = "input_text";
                    block["text"] = msg.Content ?? "";
                    contentArr.Add(block);
                    item["content"] = contentArr;
                    input.Add(item);
                }
            }
            else if (msg.Role == "tool")
            {
                JsonObject item = new JsonObject();
                item["type"] = "function_call_output";
                item["call_id"] = msg.ToolCallId ?? "";
                item["output"] = msg.Content ?? "";
                input.Add(item);
            }
            else if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                foreach (ConversationToolCall tc in msg.ToolCalls)
                {
                    JsonObject item = new JsonObject();
                    item["type"] = "function_call";
                    item["id"] = tc.Id;
                    item["call_id"] = tc.Id;
                    item["name"] = tc.Function.Name;
                    item["arguments"] = tc.Function.Arguments;
                    input.Add(item);
                }
            }
            else if (msg.Role == "assistant")
            {
                JsonObject item = new JsonObject();
                item["type"] = "message";
                item["role"] = "assistant";
                if (!string.IsNullOrEmpty(msg.Content))
                {
                    JsonArray contentArr = new JsonArray();
                    JsonObject block = new JsonObject();
                    block["type"] = "output_text";
                    block["text"] = msg.Content;
                    contentArr.Add(block);
                    item["content"] = contentArr;
                }
                input.Add(item);
            }
            else
            {
                JsonObject item = new JsonObject();
                item["type"] = "message";
                item["role"] = msg.Role;
                JsonArray contentArr = new JsonArray();
                JsonObject block = new JsonObject();
                block["type"] = "input_text";
                block["text"] = msg.Content ?? "";
                contentArr.Add(block);
                item["content"] = contentArr;
                input.Add(item);
            }
        }

        return (input, instructions);
    }

    private static List<ResponsesTool> BuildTools(List<ToolDefinition> tools)
    {
        List<ResponsesTool> result = new List<ResponsesTool>();
        foreach (ToolDefinition tool in tools)
        {
            result.Add(new ResponsesTool
            {
                Type = "function",
                Name = tool.Function.Name,
                Description = tool.Function.Description,
                Parameters = tool.Function.Parameters
            });
        }
        return result;
    }

    private static ProviderCallPayload BuildPayload(LlmModel model, ResponsesApiResponse response)
    {
        List<ConversationToolCall> toolCalls = new List<ConversationToolCall>();
        string? assistantText = null;

        foreach (ResponsesOutputItem item in response.Output)
        {
            if (item.Type == "function_call")
            {
                toolCalls.Add(new ConversationToolCall
                {
                    Id = item.CallId ?? item.Id ?? "",
                    Type = "function",
                    Function = new ConversationFunctionCall
                    {
                        Name = item.Name ?? "",
                        Arguments = item.Arguments ?? ""
                    }
                });
            }
            else if (item.Type == "message" && item.Content != null)
            {
                foreach (ResponsesContentBlock block in item.Content)
                {
                    if (block.Type == "output_text" && !string.IsNullOrEmpty(block.Text))
                    {
                        assistantText = block.Text;
                        break;
                    }
                }
            }
            else if (item.Type == "reasoning" && item.Content != null && assistantText == null)
            {
                foreach (ResponsesContentBlock block in item.Content)
                {
                    if (!string.IsNullOrEmpty(block.Text))
                    {
                        assistantText = block.Text;
                        break;
                    }
                }
            }
        }

        ConversationMessage message = new ConversationMessage { Role = "assistant" };
        string finishReason;
        if (toolCalls.Count > 0)
        {
            message.ToolCalls = toolCalls;
            finishReason = "tool_calls";
        }
        else
        {
            message.Content = assistantText;
            finishReason = "stop";
        }

        TokenUsageInfo usage = new TokenUsageInfo();
        decimal cost = 0m;

        if (response.Usage != null)
        {
            usage = new TokenUsageInfo
            {
                PromptTokens = response.Usage.InputTokens,
                CompletionTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            };
            cost += (response.Usage.InputTokens / 1_000_000m) * model.Config.Cost.Input;
            cost += (response.Usage.OutputTokens / 1_000_000m) * model.Config.Cost.Output;
        }

        return new ProviderCallPayload(message, finishReason, usage, cost);
    }
}
