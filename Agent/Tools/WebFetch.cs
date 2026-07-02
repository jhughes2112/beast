using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Fetches a web resource and lets the WebFetch role interpret it. Rather than stripping the page and feeding the
// result back (which mangles non-HTML content and floods the context on large pages), the resource is treated
// like a file: it is downloaded to /tmp/, classified, and offered to the WebFetch role as a set of files — the raw
// bytes plus, for HTML, a stripped-text view and a tag-skeleton view. The WebFetch role is given read_file and bash
// so it can inspect, parse, or grep those files and returns only what the objective asks for. Resources over
// the auto-download limit are not fetched here; the WebFetch role is told the size and can download them itself.
public class WebFetch
{
	private static readonly HttpClient SharedHttpClient = new();
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

	// The WebFetch role now does real work — reading and parsing the saved files — so it gets several turns;
	// the session's wind-down forces the terminator once these run out so the run always finishes.
	private const int MaxWorkTurns = 8;

	// Resources larger than this are not auto-downloaded: the WebFetch role is told the size and can fetch them
	// itself with curl/wget via bash if the objective actually needs them.
	private const long MaxAutoDownloadBytes = 1_048_576;

	// Fetches the resource and interprets it with the WebFetch role: a child session is seeded with the URL,
	// the objective, and the paths of the saved files, then returns only what the objective asked for via
	// return_to_caller. Cost rolls up into the calling session.
	// webFetchRole is pre-resolved by BuildForRole; the spawn delegate runs it as a child session.
	public async Task<ToolResult> FetchRawAsync(
		string toolCallId,
		string url,
		string objective,
		Role webFetchRole,
		SpawnSubagent spawn,
		int maxOutputTokens,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(url))
			return new ToolResult(toolCallId, string.Empty, "Error: URL cannot be empty.", 1, 0);

		if (string.IsNullOrWhiteSpace(objective))
			return new ToolResult(toolCallId, string.Empty, "Error: objective cannot be empty.", 1, 0);

		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
			return new ToolResult(toolCallId, string.Empty, "Error: Invalid URL format: " + url, 1, 0);

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
				{
					// The server's Content-Disposition filename, when present, is the authoritative name
					// for the resource — prefer it over the URL segment so the raw file keeps its real name.
					string? suggestedName = response.Content.Headers.ContentDisposition?.FileNameStar
						?? response.Content.Headers.ContentDisposition?.FileName;
					seed = BuildFilesSeed(url, objective, contentType, bytes!, suggestedName);
				}
			}

			(bool ok, string answer, int tokens) = await spawn(webFetchRole.Name, $"fetch_url {url}", seed, MaxWorkTurns, maxOutputTokens, cancellationToken);
			if (!ok)
			{
				string detail = string.IsNullOrEmpty(answer) ? "no reason reported" : answer;
				return new ToolResult(toolCallId, string.Empty, $"Error: the WebFetch role failed to interpret {url}: {detail}", 1, 0);
			}

			return new ToolResult(toolCallId, answer, string.Empty, 0, Math.Max(1, tokens));
		}
		catch (OperationCanceledException)
		{
			return new ToolResult(toolCallId, string.Empty, "Error: Request timed out or cancelled for URL: " + url, 1, 0);
		}
		catch (HttpRequestException ex)
		{
			return new ToolResult(toolCallId, string.Empty, "Error: Network error fetching URL " + url + ": " + ex, 1, 0);
		}
		catch (Exception ex)
		{
			return new ToolResult(toolCallId, string.Empty, "Error: Failed to fetch URL " + url + ": " + ex, 1, 0);
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

	// Saves the downloaded resource to a fresh /tmp/ directory and builds the seed the WebFetch role reads: the
	// raw bytes always, plus a stripped-text view and a tag-skeleton view when the content is HTML. The seed
	// lists each saved file with its size and what it is good for, so the role can read the right one.
	private static string BuildFilesSeed(string url, string objective, string? contentType, byte[] bytes, string? suggestedName)
	{
		string classification = ClassifyContent(contentType, url, bytes);
		string directory = Path.Combine("/tmp", "fetch_" + Guid.NewGuid().ToString("N").Substring(0, 12));
		Directory.CreateDirectory(directory);

		StringBuilder manifest = new StringBuilder();

		string rawName = FileNameFor(url, classification, suggestedName);
		string rawPath = Path.Combine(directory, rawName);
		File.WriteAllBytes(rawPath, bytes);
		manifest.Append($"- {rawPath}  ({bytes.Length} bytes) — \"{rawName}\", the exact bytes as fetched\n");

		if (classification == "html")
		{
			string html = Encoding.UTF8.GetString(bytes);

			string stripped = WrapAtWhitespace(StripHtmlTags(html));
			string strippedPath = Path.Combine(directory, "stripped.txt");
			File.WriteAllText(strippedPath, stripped);
			manifest.Append($"- {strippedPath}  ({stripped.Length} chars) — tags removed, entities decoded, whitespace collapsed; best for reading prose\n");

			string structure = BuildHtmlStructure(html);
			string structurePath = Path.Combine(directory, "structure.html");
			File.WriteAllText(structurePath, structure);
			manifest.Append($"- {structurePath}  ({structure.Length} chars) — tag skeleton with text removed; best for understanding layout or locating a section\n");
		}
		else if (classification == "pdf")
		{
			// PDFs are binary — read_file on the raw bytes is useless. Extract a readable text view with
			// pdftotext (poppler-utils, installed in the image). -layout keeps columns/tables roughly intact.
			string textPath = Path.Combine(directory, "extracted.txt");
			manifest.Append($"- {textPath} — readable text extracted from the PDF. It does not exist yet; create it first by running `pdftotext -layout \"{rawPath}\" \"{textPath}\"` with bash, then read it.\n");
		}
		else if (classification == "binary")
		{
			// Binary payload (archive, image, office doc): read_file shows only garbage. Identify it and
			// extract with the matching tool — all installed in the image — then read the extracted text.
			manifest.Append($"- The raw file is binary. Run `file \"{rawPath}\"` to identify it, then extract with the matching tool before reading: unzip/7z for archives, tar for tarballs, pandoc for documents (docx/odt/rtf/epub), pdftotext for PDFs. Reading the raw bytes directly will not work.\n");
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
			if (lowered.Contains("html"))
				return "html";
			if (lowered.Contains("pdf"))
				return "pdf";
			if (lowered.Contains("json"))
				return "json";
			if (lowered.Contains("xml"))
				return "xml";
			if (lowered.Contains("text/"))
				return "text";
		}

		string path = url;
		int query = path.IndexOf('?');
		if (query >= 0)
			path = path.Substring(0, query);
		string extension = Path.GetExtension(path).ToLowerInvariant();
		if (extension == ".html" || extension == ".htm")
			return "html";
		if (extension == ".pdf")
			return "pdf";
		if (extension == ".json")
			return "json";
		if (extension == ".xml")
			return "xml";
		if (extension == ".txt" || extension == ".md" || extension == ".csv")
			return "text";

		// A PDF always begins with the "%PDF-" magic regardless of declared type or extension.
		if (bytes.Length >= 5 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' && bytes[4] == '-')
			return "pdf";

		// A NUL byte in the leading window means the payload is not text (archive, image, office doc):
		// it must be classified as binary so the role is steered to `file` and an extractor rather than
		// told to read_file raw bytes. Checked before the text sniff, which would otherwise misread it.
		if (LooksBinary(bytes))
			return "binary";

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

	// True when the leading bytes contain a NUL, the simplest reliable signal that content is binary
	// rather than text. Only the first kilobyte is scanned — enough to catch archive/image/office magic.
	private static bool LooksBinary(byte[] bytes)
	{
		int n = Math.Min(bytes.Length, 1024);
		for (int i = 0; i < n; i++)
		{
			if (bytes[i] == 0)
				return true;
		}
		return false;
	}

	// Names the raw file after the resource's own filename so the role sees what the thing is actually
	// called, not a generic "raw". Prefers the server's Content-Disposition name, then the URL's last path
	// segment. A name that already carries an extension is kept verbatim — we retain the original name as
	// fetched; only when there is no usable name or extension is one synthesized from the classification.
	private static string FileNameFor(string url, string classification, string? suggestedName)
	{
		string candidate = !string.IsNullOrWhiteSpace(suggestedName) ? suggestedName! : UrlLastSegment(url);

		// Strip anything that would be awkward on disk, keeping the name recognizable. This also drops path
		// separators, neutralizing any directory components or traversal in a Content-Disposition name.
		StringBuilder cleaned = new StringBuilder(candidate.Length);
		foreach (char c in candidate)
		{
			if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')
				cleaned.Append(c);
		}

		string name = cleaned.ToString().Trim('.');

		// A real filename that already has its own extension is kept exactly as fetched.
		if (name.Length > 0 && name.IndexOf('.') >= 0)
			return name;

		// No usable name or extension: synthesize one from the classification (binary gets a neutral .bin).
		if (name.Length == 0)
			name = "raw";
		string extension = classification == "binary" ? "bin" : ExtensionFor(classification);
		return name + "." + extension;
	}

	// The last path segment of a URL, with the query and fragment removed. Empty when the URL ends in a
	// slash or has no path (e.g. "https://host/"), in which case the caller synthesizes a name.
	private static string UrlLastSegment(string url)
	{
		string path = url;
		int query = path.IndexOf('?');
		if (query >= 0)
			path = path.Substring(0, query);
		int fragment = path.IndexOf('#');
		if (fragment >= 0)
			path = path.Substring(0, fragment);

		int slash = path.LastIndexOf('/');
		if (slash >= 0 && slash < path.Length - 1)
			return path.Substring(slash + 1);
		return string.Empty;
	}

	// File extension for the raw view, matching the classification so on-disk tooling (and the role) can tell
	// what it is. Unknown content keeps a neutral extension.
	private static string ExtensionFor(string classification)
	{
		string extension;
		if (classification == "html")
			extension = "html";
		else if (classification == "pdf")
			extension = "pdf";
		else if (classification == "json")
			extension = "json";
		else if (classification == "xml")
			extension = "xml";
		else
			extension = "txt";
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

	// The collapsed prose view is a single line; saved as-is it is unreadable and breaks line-oriented reads.
	// Wrap it so no line exceeds MaxLineLength, breaking at the last whitespace before the limit, or hard at
	// the limit when a single run of non-whitespace is longer so the loop always makes progress.
	private const int MaxLineLength = 160;

	private static string WrapAtWhitespace(string text)
	{
		if (string.IsNullOrEmpty(text))
			return text;

		StringBuilder sb = new StringBuilder(text.Length + text.Length / MaxLineLength + 1);
		int start = 0;
		int n = text.Length;
		while (start < n)
		{
			int remaining = n - start;
			if (remaining <= MaxLineLength)
			{
				sb.Append(text, start, remaining);
				break;
			}

			int limit = start + MaxLineLength;
			int breakAt = -1;
			for (int i = limit - 1; i > start; i--)
			{
				if (char.IsWhiteSpace(text[i]))
				{
					breakAt = i + 1;
					break;
				}
			}

			if (breakAt <= start)
				breakAt = limit;

			sb.Append(text, start, breakAt - start);
			sb.Append('\n');
			start = breakAt;
		}

		return sb.ToString();
	}
}