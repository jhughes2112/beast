using System;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


// Fetches web pages and returns their text content with HTML stripped.
public class WebFetch
{
    private readonly WebCache _webCache;
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public WebFetch(WebCache webCache)
    {
        _webCache = webCache;
    }

    [Description("Fetch the contents of a web page at the specified URL. Returns the text content with HTML tags stripped.")]
    public async Task<ToolResult> FetchPageAsync(
        [Description("The fully-formed URL to fetch content from.")] string url,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(url))
            return new ToolResult("Error: URL cannot be empty.", false);

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return new ToolResult("Error: Invalid URL format: " + url, false);

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(DefaultTimeout);

            string text = await _webCache.GetOrFetchAsync(url, async () =>
            {
                HttpResponseMessage response = await SharedHttpClient.GetAsync(uri, cts.Token);

                if (!response.IsSuccessStatusCode)
                    return "Error: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase;

                string html = await response.Content.ReadAsStringAsync(cts.Token);
                string stripped = StripHtmlTags(html);

                if (stripped.Length > 50000)
                    stripped = stripped.Substring(0, 50000) + "\n\n[Content truncated at 50000 characters]";

                return stripped;
            }, null);

            if (string.IsNullOrWhiteSpace(text))
                return new ToolResult("Error: No readable text content found at URL: " + url, false);

            if (text.StartsWith("Error:"))
                return new ToolResult(text, false);

            return new ToolResult(text, false);
        }
        catch (OperationCanceledException)
        {
            return new ToolResult("Error: Request timed out or cancelled for URL: " + url, false);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult("Error: Network error fetching URL " + url + ": " + ex.Message, false);
        }
        catch (Exception ex)
        {
            return new ToolResult("Error: Failed to fetch URL " + url + ": " + ex.Message, false);
        }
    }

    private static string StripHtmlTags(string html)
    {
        html = Regex.Replace(html, "<script[^>]*>[\\s\\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<style[^>]*>[\\s\\S]*?</style>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<[^>]+>", " ");
        html = WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, "\\s+", " ");
        html = Regex.Replace(html, "(\\r?\\n\\s*){3,}", "\n\n");
        return html.Trim();
    }
}
