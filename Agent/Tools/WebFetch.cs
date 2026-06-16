using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

// Fetches a web page and returns only what the objective asks for. The page is always interpreted by the
// Web role; the raw page is never returned to the caller.
public class WebFetch
{
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // The Web role digests a page and returns one answer; it needs a working turn at most before finalizing.
    private const int MaxTurns = 2;

    // Fetches the page and interprets it with the Web role: a throwaway sub-session is seeded with the URL,
    // the objective, and the page content, and what it passes to return_to_caller (only what the objective
    // asked for) is returned. Cost rolls up into the calling session. Everything is contained here.
    public async Task<ToolResult> FetchRawAsync(
        string toolCallId,
        string url,
        string objective,
        LlmRegistry registry,
        RoleService roleService,
        ITransportServer transport,
        Session parent,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ToolResult(toolCallId, string.Empty, "Error: URL cannot be empty.", 1, 0);

        if (string.IsNullOrWhiteSpace(objective))
            return new ToolResult(toolCallId, string.Empty, "Error: objective cannot be empty.", 1, 0);

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return new ToolResult(toolCallId, string.Empty, "Error: Invalid URL format: " + url, 1, 0);

        Role? webRole = roleService.GetRole("Web");
        if (webRole == null)
            return new ToolResult(toolCallId, string.Empty, "Error: Web role is not defined.", 1, 0);

        LlmService? service = registry.CreateService(webRole, string.Empty, 0);
        if (service == null)
            return new ToolResult(toolCallId, string.Empty, "Error: no model available for the Web role.", 1, 0);

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(DefaultTimeout);

            HttpResponseMessage response = await SharedHttpClient.GetAsync(uri, cts.Token);
            if (!response.IsSuccessStatusCode)
                return new ToolResult(toolCallId, string.Empty, "Error: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase, 1, 0);

            string html = await response.Content.ReadAsStringAsync(cts.Token);

            // Interpret the page with the Web role: it returns only what the objective asks for via
            // return_to_caller, with a working turn available before the terminator is forced.
            string seed = $"URL: {url}\nObjective: {objective}\n\nPage content:\n{html}";
            (bool ok, string answer, int tokens) = await HelperSession.RunAsync(parent, webRole, service, "fetch_url", seed, MaxTurns, maxOutputTokens, transport, cancellationToken);
            if (!ok)
                return new ToolResult(toolCallId, string.Empty, "Error: the Web role failed to interpret " + url, 1, 0);

            return new ToolResult(toolCallId, answer, string.Empty, 0, Math.Max(1, tokens));
        }
        catch (OperationCanceledException)
        {
            return new ToolResult(toolCallId, string.Empty, "Error: Request timed out or cancelled for URL: " + url, 1, 0);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult(toolCallId, string.Empty, "Error: Network error fetching URL " + url + ": " + ex.Message, 1, 0);
        }
        catch (Exception ex)
        {
            return new ToolResult(toolCallId, string.Empty, "Error: Failed to fetch URL " + url + ": " + ex.Message, 1, 0);
        }
    }
}
