using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Fetches a web resource and lets the Web role interpret it. Rather than stripping the page and feeding the
// result back (which mangles non-HTML content and floods the context on large pages), the resource is treated
// like a file: it is downloaded to /tmp/, classified, and offered to the Web role as a set of files — the raw
// bytes plus, for HTML, a stripped-text view and a tag-skeleton view. The Web role is given read_file and bash
// so it can inspect, parse, or grep those files and returns only what the objective asks for. Resources over
// the auto-download limit are not fetched here; the Web role is told the size and can download them itself.
public class WebFetch
{
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // The Web role now does real work — reading and parsing the saved files — so it gets several turns; the
    // terminator is forced on the last one so the loop always finishes (see HelperSession).
    private const int MaxTurns = 8;

    // Resources larger than this are not auto-downloaded: the Web role is told the size and can fetch them
    // itself with curl/wget via bash if the objective actually needs them.
    private const long MaxAutoDownloadBytes = 1_048_576;

    // Fetches the resource and interprets it with the Web role: a throwaway sub-session is seeded with the URL,
    // the objective, and the paths of the saved files, then returns only what the objective asked for via
    // return_to_caller. Cost rolls up into the calling session. Everything is contained here.
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

            // Read only the headers first so the size can be checked before any body is pulled.
            HttpResponseMessage response = await SharedHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                return new ToolResult(toolCallId, string.Empty, "Error: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase, 1, 0);

            string? contentType = response.Content.Headers.ContentType?.MediaType;
            long? declaredLength = response.Content.Headers.ContentLength;

            string seed;
            if (declaredLength.HasValue && declaredLength.Value > MaxAutoDownloadBytes)
            {
                // Known to be too big from the header alone: do not download it here.
                seed = BuildTooBigSeed(url, objective, declaredLength.Value);
            }
            else
            {
                // Stream the body but stop if it crosses the cap (the length may be unknown up front).
                (byte[]? bytes, bool tooBig) = await ReadCappedAsync(response, cts.Token);
                if (tooBig)
                    seed = BuildTooBigSeed(url, objective, declaredLength ?? (MaxAutoDownloadBytes + 1));
                else
                    seed = BuildFilesSeed(url, objective, contentType, bytes!);
            }

            (bool ok, string answer, int tokens) = await HelperSession.RunAsync(parent, webRole, service, $"fetch_url {url}", seed, MaxTurns, maxOutputTokens, ToolFactory.BuildHelperTools(webRole.Tools), transport, cancellationToken);
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

    // Reads the response body into memory, abandoning it the moment it crosses MaxAutoDownloadBytes so an
    // unbounded resource can never be pulled in full. Returns the bytes when within the cap, or tooBig=true.
    private static async Task<(byte[]? bytes, bool tooBig)> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        long total = 0;

        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxAutoDownloadBytes)
                return (null, true);
            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), false);
    }

    // Saves the downloaded resource to a fresh /tmp/ directory and builds the seed the Web role reads: the
    // raw bytes always, plus a stripped-text view and a tag-skeleton view when the content is HTML. The seed
    // lists each saved file with its size and what it is good for, so the role can read the right one.
    private static string BuildFilesSeed(string url, string objective, string? contentType, byte[] bytes)
    {
        string classification = ClassifyContent(contentType, url, bytes);
        string directory = Path.Combine("/tmp", "fetch_" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(directory);

        StringBuilder manifest = new StringBuilder();

        string rawPath = Path.Combine(directory, "raw." + ExtensionFor(classification));
        File.WriteAllBytes(rawPath, bytes);
        manifest.Append($"- {rawPath}  ({bytes.Length} bytes) — the exact bytes as fetched\n");

        if (classification == "html")
        {
            string html = Encoding.UTF8.GetString(bytes);

            string stripped = StripHtmlTags(html);
            string strippedPath = Path.Combine(directory, "stripped.txt");
            File.WriteAllText(strippedPath, stripped);
            manifest.Append($"- {strippedPath}  ({stripped.Length} chars) — tags removed, entities decoded, whitespace collapsed; best for reading prose\n");

            string structure = BuildHtmlStructure(html);
            string structurePath = Path.Combine(directory, "structure.html");
            File.WriteAllText(structurePath, structure);
            manifest.Append($"- {structurePath}  ({structure.Length} chars) — tag skeleton with text removed; best for understanding layout or locating a section\n");
        }

        return $"URL: {url}\nObjective: {objective}\nContent type: {classification}{(contentType != null ? $" ({contentType})" : string.Empty)}\n\n"
            + "The resource was downloaded and saved. Use read_file and bash to inspect these files, then return only what the objective asks for:\n"
            + manifest.ToString();
    }

    // Seed for a resource over the auto-download limit: the role is told the size and how to fetch it itself.
    private static string BuildTooBigSeed(string url, string objective, long sizeBytes)
    {
        return $"URL: {url}\nObjective: {objective}\n\n"
            + $"This resource is {sizeBytes} bytes (over the {MaxAutoDownloadBytes}-byte auto-download limit) and was not downloaded. "
            + $"If the objective needs its contents, download it yourself with bash, e.g. `curl -sL '{url}' -o /tmp/page` (or wget), then inspect it with read_file and bash. "
            + "Otherwise return what you can determine without it.";
    }

    // Classifies the content so the right views are built and the role knows what it is looking at. The
    // declared content type wins; failing that the URL extension; failing that a sniff of the leading bytes.
    private static string ClassifyContent(string? contentType, string url, byte[] bytes)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            string lowered = contentType.ToLowerInvariant();
            if (lowered.Contains("html")) return "html";
            if (lowered.Contains("json")) return "json";
            if (lowered.Contains("xml")) return "xml";
            if (lowered.Contains("text/")) return "text";
        }

        string path = url;
        int query = path.IndexOf('?');
        if (query >= 0)
            path = path.Substring(0, query);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".html" || extension == ".htm") return "html";
        if (extension == ".json") return "json";
        if (extension == ".xml") return "xml";
        if (extension == ".txt" || extension == ".md" || extension == ".csv") return "text";

        // Sniff the leading non-whitespace characters of the decoded text.
        string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart();
        if (head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return "html";
        if (head.StartsWith("{") || head.StartsWith("["))
            return "json";
        if (head.StartsWith("<"))
            return "xml";
        return "text";
    }

    // File extension for the raw view, matching the classification so on-disk tooling (and the role) can tell
    // what it is. Unknown content keeps a neutral extension.
    private static string ExtensionFor(string classification)
    {
        string extension;
        if (classification == "html") extension = "html";
        else if (classification == "json") extension = "json";
        else if (classification == "xml") extension = "xml";
        else extension = "txt";
        return extension;
    }

    // Strips HTML to readable text: drops <script> and <style> bodies entirely, removes every other tag,
    // decodes HTML entities, and collapses runs of whitespace. Manual scan rather than regex so script/style
    // removal is reliable. Empty input yields empty output.
    private static string StripHtmlTags(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        StringBuilder sb = new StringBuilder(html.Length);
        int i = 0;
        int n = html.Length;
        while (i < n)
        {
            if (html[i] == '<')
            {
                if (StartsWithTag(html, i + 1, "script"))
                {
                    i = SkipToEndTag(html, i, "script");
                    continue;
                }
                if (StartsWithTag(html, i + 1, "style"))
                {
                    i = SkipToEndTag(html, i, "style");
                    continue;
                }

                int close = html.IndexOf('>', i);
                if (close < 0)
                    break;
                i = close + 1;
                // A tag boundary separates text runs, so emit a space to keep words apart.
                sb.Append(' ');
            }
            else
            {
                sb.Append(html[i]);
                i++;
            }
        }

        string decoded = WebUtility.HtmlDecode(sb.ToString());
        return CollapseWhitespace(decoded);
    }

    // Builds a tag-only skeleton of the page: every tag on its own line, text and script/style bodies removed.
    // This gives a compact map of the page's structure for locating a section before reading the prose view.
    private static string BuildHtmlStructure(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        StringBuilder sb = new StringBuilder();
        int i = 0;
        int n = html.Length;
        while (i < n)
        {
            if (html[i] == '<')
            {
                if (StartsWithTag(html, i + 1, "script"))
                {
                    i = SkipToEndTag(html, i, "script");
                    continue;
                }
                if (StartsWithTag(html, i + 1, "style"))
                {
                    i = SkipToEndTag(html, i, "style");
                    continue;
                }

                int close = html.IndexOf('>', i);
                if (close < 0)
                    break;
                sb.Append(html, i, close - i + 1);
                sb.Append('\n');
                i = close + 1;
            }
            else
            {
                i++;
            }
        }

        return sb.ToString();
    }

    // True when the characters at start begin the named tag (case-insensitive) followed by a boundary —
    // whitespace, '>', or '/'. Used to recognize <script>/<style> openers without matching <scripting> etc.
    private static bool StartsWithTag(string html, int start, string tagName)
    {
        if (start + tagName.Length > html.Length)
            return false;

        for (int i = 0; i < tagName.Length; i++)
        {
            if (char.ToLowerInvariant(html[start + i]) != tagName[i])
                return false;
        }

        int after = start + tagName.Length;
        if (after >= html.Length)
            return true;
        char c = html[after];
        return char.IsWhiteSpace(c) || c == '>' || c == '/';
    }

    // Returns the index just past the matching </tagName> for a tag opened at openIndex, or the end of the
    // string when no closing tag is found. Both the opening tag and its body are skipped by the caller.
    private static int SkipToEndTag(string html, int openIndex, string tagName)
    {
        string closeTag = "</" + tagName;
        int closeIndex = html.IndexOf(closeTag, openIndex, StringComparison.OrdinalIgnoreCase);
        if (closeIndex < 0)
            return html.Length;

        int gt = html.IndexOf('>', closeIndex);
        if (gt < 0)
            return html.Length;
        return gt + 1;
    }

    // Collapses every run of whitespace to a single space and trims the ends.
    private static string CollapseWhitespace(string text)
    {
        StringBuilder sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
            else
            {
                if (pendingSpace && sb.Length > 0)
                    sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
