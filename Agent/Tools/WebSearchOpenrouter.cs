using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


// Web search via the OpenRouter plugin API.
// Requires an OpenrouterSearchConfig from settings.
public class WebSearchOpenrouter
{
    private readonly OpenrouterSearchConfig _config;
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

    public WebSearchOpenrouter(OpenrouterSearchConfig config)
    {
        _config = config;
    }

    [Description("Search the web for information via OpenRouter's web search plugin. Returns a list of search results with titles, URLs, and snippets.")]
    public async Task<ToolResult> SearchWebAsync(
        [Description("The search query to use.")] string query,
        [Description("Maximum number of results to return (1-20). Pass empty string for default of 5.")] string maxResults,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult("Error: Search query cannot be empty.", false);

        int limit = 5;
        if (!string.IsNullOrWhiteSpace(maxResults) && int.TryParse(maxResults, out int parsed))
            limit = Math.Clamp(parsed, 1, 20);

        try
        {
            string result = await SearchAsync(query, limit, cancellationToken);
            return new ToolResult(result, false);
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

    private async Task<string> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        string pluginJson = "{\"id\":\"web\",\"max_results\":" + maxResults + "}";
        string requestJson = "{\"model\":\"" + _config.Model + "\",\"messages\":[{\"role\":\"user\",\"content\":" + JsonSerializer.Serialize(query) + "}],\"plugins\":[" + pluginJson + "],\"temperature\":0,\"max_tokens\":1024}";

        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint.TrimEnd('/') + "/chat/completions");
        request.Headers.Add("Authorization", "Bearer " + _config.ApiKey);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(SearchTimeout);
        HttpResponseMessage response = await SharedHttpClient.SendAsync(request, cts.Token);
        string responseBody = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode)
            return "Error: OpenRouter returned HTTP " + (int)response.StatusCode + ": " + responseBody;

        return ParseResponse(responseBody);
    }

    private static string ParseResponse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("error", out JsonElement errorEl))
        {
            string errorMsg = errorEl.TryGetProperty("message", out JsonElement msgEl) ? msgEl.GetString() ?? "Unknown error" : "Unknown error";
            return "Error: OpenRouter search failed: " + errorMsg;
        }

        string content = "";
        List<string> citations = new();

        if (root.TryGetProperty("choices", out JsonElement choices))
        {
            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out JsonElement message))
                {
                    if (message.TryGetProperty("content", out JsonElement contentEl))
                        content = contentEl.GetString() ?? "";

                    if (message.TryGetProperty("annotations", out JsonElement annotations))
                    {
                        int index = 1;
                        foreach (JsonElement annotation in annotations.EnumerateArray())
                        {
                            if (annotation.TryGetProperty("url_citation", out JsonElement citation))
                            {
                                string title = citation.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? "" : "";
                                string url = citation.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
                                string snippet = citation.TryGetProperty("content", out JsonElement s) ? s.GetString() ?? "" : "";

                                StringBuilder entry = new StringBuilder();
                                entry.AppendLine(index + ". " + title);
                                entry.AppendLine("   URL: " + url);
                                if (!string.IsNullOrEmpty(snippet))
                                {
                                    if (snippet.Length > 300) snippet = snippet.Substring(0, 300) + "...";
                                    entry.AppendLine("   " + snippet);
                                }
                                citations.Add(entry.ToString().TrimEnd());
                                index++;
                            }
                        }
                    }
                }
                break;
            }
        }

        if (citations.Count > 0)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string c in citations)
            {
                sb.AppendLine(c);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine("---");
                sb.AppendLine(content);
            }
            return sb.ToString().TrimEnd();
        }

        return string.IsNullOrWhiteSpace(content) ? "No search results found." : content;
    }
}
