using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Web search via the OpenRouter plugin API.
// Routes through ProtocolChatCompletions directly — no endpoint probe needed.
// LlmModel is built once from settings at construction time.
public class WebSearchOpenrouter
{
    private readonly LlmModel _model;
    private readonly ProtocolChatCompletions _protocol = new ProtocolChatCompletions();

    public WebSearchOpenrouter(LlmModel model)
    {
        _model = model;
    }

    [Description("Search the web using OpenRouter's web search plugin. The query can be a natural language question or instruction, not just keywords — e.g. 'Show me how to call the Foo API and explain each parameter'.")]
    public async Task<ToolResult> SearchWebAsync(
        [Description("The search query or natural language question to answer using the web.")] string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult("Error: Search query cannot be empty.", false);

        try
        {
            (Dictionary<string, string> headers, Dictionary<string, JsonNode?> payload) = ProtocolProxy.BuildExtras(_model.Extras, _model.Endpoint);

            List<ConversationMessage> messages = new()
            {
                new ConversationMessage { Role = "user", Content = query }
            };

            int maxTokens = GetIntExtra("max_tokens", 4096);  // this is the default, you can adjust it in the extras payload config

            ProviderCallResult result = await _protocol.ExecuteAsync(
                _model, messages, new List<ToolDefinition>(), maxTokens, headers, payload, null, cancellationToken);

            if (result.Outcome == ProviderCallOutcome.Success)
            {
                return new ToolResult(ParseResponse(result.Payload!.Message), false);
            }
            else if (result.Outcome == ProviderCallOutcome.RateLimited)
            {
                return new ToolResult("Error: OpenRouter rate limited the search request. Retry after " + result.RetryAfter, false);
            }
            else
            {
                return new ToolResult("Error: OpenRouter search failed: " + result.ErrorMessage, false);
            }
        }
        catch (OperationCanceledException)
        {
            return new ToolResult("Error: Search request timed out or cancelled for query: " + query, false);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult("Error: Network error during search: " + ex.Message, false);
        }
        catch (Exception ex)
        {
            return new ToolResult("Error: Search failed: " + ex.Message, false);
        }
    }

    // Reads an integer value from the model extras, falling back to the given default.
    private int GetIntExtra(string key, int defaultValue)
    {
        if (_model.Extras.TryGetValue(key, out JsonNode? node) &&
            node is JsonValue jv &&
            jv.TryGetValue<int>(out int v))
        {
            return v;
        }
        return defaultValue;
    }

    private static string ParseResponse(ConversationMessage message)
    {
        string content = message.Content ?? "";

        if (message.Annotations == null || message.Annotations.Count == 0)
            return string.IsNullOrWhiteSpace(content) ? "No search results found." : content;

        // Collect citation references to append after the answer.
        List<string> citations = new();
        int index = 1;
        foreach (JsonElement annotation in message.Annotations)
        {
            if (!annotation.TryGetProperty("url_citation", out JsonElement citation)) continue;

            string title = citation.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? "" : "";
            string url = citation.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";

            citations.Add($"[{index}] {title} — {url}");
            index++;
        }

        if (citations.Count == 0 || string.IsNullOrWhiteSpace(content))
            return string.IsNullOrWhiteSpace(content) ? "No search results found." : content;

        StringBuilder sb = new();
        sb.AppendLine(content);
        sb.AppendLine();
        sb.AppendLine("Sources:");
        foreach (string c in citations)
            sb.AppendLine(c);
        return sb.ToString().TrimEnd();
    }
}
