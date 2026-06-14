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
// Routes through ProtocolProxy directly — no endpoint probe needed.
// LlmModel is built once from settings at construction time.
public class WebSearchOpenrouter
{
    private readonly LlmModel _model;

    public WebSearchOpenrouter(LlmModel model)
    {
        _model = model;
    }

    [Description("Search the web using OpenRouter's web search plugin. The query can be a natural language question or instruction, not just keywords — e.g. 'Show me how to call the Foo API and explain each parameter'.")]
    public async Task<ToolResult> SearchWebAsync(
		string toolCallId,
        [Description("The search query or natural language question to answer using the web.")] string query,
        ITransportServer transport,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult(toolCallId, string.Empty, "Error: Search query cannot be empty.", 1, 0);

        try
        {
            List<CanonicalMessage> messages = new List<CanonicalMessage>();
            ListenerBundle bundle = new ListenerBundle(new CanonicalConversation(messages), null);
            bundle.OnUserMessage(query);

            int maxTokens = GetIntExtra("max_tokens", 4096);  // this is the default, you can adjust it in the extras payload config

            ProtocolProxy proxy = new ProtocolProxy(_model);
            ProtocolResult result = await proxy.ExecuteAsync(bundle, new List<ToolDefinition>(), null, maxTokens, (i, o, c) => { }, transport, sessionId, null, cancellationToken);

            if (result.Outcome == ProtocolCallOutcome.Success)
            {
                string content = result.Payload!.AssistantText ?? string.Empty;
                return new ToolResult(toolCallId, string.IsNullOrWhiteSpace(content) ? "No search results found." : content, string.Empty, 0, 0);
            }
            else if (result.Outcome == ProtocolCallOutcome.RateLimited)
            {
                return new ToolResult(toolCallId, string.Empty, "Error: OpenRouter rate limited the search request. Retry after " + result.RetryAfter, 1, 0);
            }
            else
            {
                return new ToolResult(toolCallId, string.Empty, "Error: OpenRouter search failed: " + result.ErrorMessage, 1, 0);
            }
        }
        catch (OperationCanceledException)
        {
            return new ToolResult(toolCallId, string.Empty, "Error: Search request timed out or cancelled for query: " + query, 1, 0);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult(toolCallId, string.Empty, "Error: Network error during search: " + ex.Message, 1, 0);
        }
        catch (Exception ex)
        {
            return new ToolResult(toolCallId, string.Empty, "Error: Search failed: " + ex.Message, 1, 0);
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
}

