using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


// Fetches web pages and returns their text content with HTML stripped.
// Caches successful responses for 30 seconds to avoid hammering external sites.
public class WebFetch
{
    private record CacheEntry(string Content, DateTime ExpiresAt);

    private static readonly Regex ScriptRegex = new Regex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyleRegex = new Regex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
    private static readonly Regex BlankLinesRegex = new Regex(@"(\r?\n\s*){3,}", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

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

            if (_cache.TryGetValue(url, out CacheEntry? entry) && DateTime.UtcNow < entry.ExpiresAt)
                return new ToolResult(entry.Content, false);

            _cache.TryRemove(url, out _);

            HttpResponseMessage response = await SharedHttpClient.GetAsync(uri, cts.Token);

            if (!response.IsSuccessStatusCode)
                return new ToolResult("Error: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase, false);

            string html = await response.Content.ReadAsStringAsync(cts.Token);
            string text = StripHtmlTags(html);

            if (string.IsNullOrWhiteSpace(text))
                return new ToolResult("Error: No readable text content found at URL: " + url, false);

            _cache[url] = new CacheEntry(text, DateTime.UtcNow + CacheTtl);
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
        html = ScriptRegex.Replace(html, "");
        html = StyleRegex.Replace(html, "");
        html = TagRegex.Replace(html, " ");
        html = WebUtility.HtmlDecode(html);
        html = WhitespaceRegex.Replace(html, " ");
        html = BlankLinesRegex.Replace(html, "\n\n");
        return html.Trim();
    }
}
