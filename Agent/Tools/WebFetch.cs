using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Backs the fetch_url tool, and does nothing a curl and a few converters would not: it downloads the resource
// into a numbered folder under /tmp/work, makes readable views of it in code (an HTML page's text and its tag
// outline, a PDF's text, a binary file's type), and lists what it saved. No model reads anything here. The
// result says what was saved and nothing about what to do with it: the caller's tools already say that.
public class WebFetch
{
	private static readonly HttpClient SharedHttpClient = new();
	private static readonly TimeSpan   DefaultTimeout   = TimeSpan.FromSeconds(120);

	// Anything bigger is refused rather than held in memory.
	private const long MaxDownloadBytes = 50L * 1024 * 1024;

	// Where fetched files go: the scratch folder a seeded run's prompt sends the model to.
	private const string kScratchFolder = "/tmp/work";

	private static readonly object kFolderGate = new();

	public static async Task<ToolResult> FetchAsync(string toolCallId, string url, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(url))
			return new ToolResult(toolCallId, string.Empty, "Error: URL cannot be empty.", 1, 0);

		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			return new ToolResult(toolCallId, string.Empty, "Error: not an http or https URL: " + url, 1, 0);

		string directory = ReserveFolder();
		try
		{
			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			cts.CancelAfter(DefaultTimeout);

			// Read only the headers first so the size can be checked before any body is pulled.
			using HttpResponseMessage response = await SharedHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
			if (!response.IsSuccessStatusCode)
				return new ToolResult(toolCallId, string.Empty, $"Error: HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {url}", 1, 0);

			long? declared               = response.Content.Headers.ContentLength;
			(byte[]? bytes, bool tooBig) = declared > MaxDownloadBytes ? (null, true) : await ReadCappedAsync(response, cts.Token);
			if (tooBig)
				return new ToolResult(toolCallId, string.Empty, $"Error: {url} is over {MaxDownloadBytes / (1024 * 1024)} MB, so it was not downloaded.", 1, 0);

			// The server's Content-Disposition filename, when present, is the resource's real name.
			string? contentType   = response.Content.Headers.ContentType?.MediaType;
			string? suggestedName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
			string  landed        = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
			string  redirect      = landed != uri.AbsoluteUri ? $", redirected to {landed}" : string.Empty;

			string listing = await SaveFilesAsync(directory, landed, contentType, bytes!, suggestedName, cts.Token);
			string summary = $"Downloaded {url}{redirect}: HTTP {(int)response.StatusCode}, {contentType ?? "no content type"}, {Count(bytes!.Length)} bytes. Saved in {directory}:\n{listing.TrimEnd('\n')}";
			return new ToolResult(toolCallId, summary, string.Empty, 0, 0);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new ToolResult(toolCallId, string.Empty, $"Error: {url} did not finish downloading within {DefaultTimeout.TotalSeconds} seconds.", 1, 0);
		}
		catch (HttpRequestException ex)
		{
			return new ToolResult(toolCallId, string.Empty, $"Error: could not download {url}: {Describe(ex)}", 1, 0);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return new ToolResult(toolCallId, string.Empty, $"Error: could not save {url}: {Describe(ex)}", 1, 0);
		}
	}

	// Saves the bytes and the views made from them, and lists each saved file with its size and what it is for.
	// The folder goes to the shell user, so the caller's shell can write its own extractions beside them.
	private static async Task<string> SaveFilesAsync(string directory, string url, string? contentType, byte[] bytes, string? suggestedName, CancellationToken ct)
	{
		StringBuilder listing = new StringBuilder(SaveFiles(directory, url, contentType, bytes, suggestedName));
		string        kind    = ClassifyContent(contentType, url, bytes);
		string        rawPath = Path.Combine(directory, FileNameFor(url, kind, suggestedName));
		if (kind == "pdf")
		{
			string     textPath  = Path.Combine(directory, "text.txt");
			ToolResult extracted = await ShellTools.AgentBashAsync("fetch_pdf", $"pdftotext -layout {Quote(rawPath)} {Quote(textPath)}", 60, ct);
			listing.Append(extracted.ExitCode == 0 && File.Exists(textPath)
				? Entry(textPath, File.ReadAllBytes(textPath), "the PDF's text, from pdftotext -layout")
				: $"- pdftotext could not read the PDF: {(extracted.StdErr + extracted.StdOut).Trim()}\n");
		}
		else if (kind == "binary")
		{
			ToolResult type = await ShellTools.AgentBashAsync("fetch_type", $"file -b {Quote(rawPath)}", 30, ct);
			listing.Append($"- It is binary: {type.StdOut.Trim()}\n");
		}

		GiveToShellUser(directory);
		return listing.ToString();
	}

	private static void GiveToShellUser(string directory)
	{
		foreach (string file in Directory.GetFiles(directory))
			Sandbox.GiveToShellUser(file);
		Sandbox.GiveToShellUser(directory);
	}

	// The part of saving that needs no other program: the exact bytes, and for HTML its text and its tag outline.
	private static string SaveFiles(string directory, string url, string? contentType, byte[] bytes, string? suggestedName)
	{
		string kind = ClassifyContent(contentType, url, bytes);
		PrepareFolder(directory);

		StringBuilder listing = new StringBuilder();
		string        rawPath = Path.Combine(directory, FileNameFor(url, kind, suggestedName));
		File.WriteAllBytes(rawPath, bytes);
		listing.Append(Entry(rawPath, kind == "binary" || kind == "pdf" ? null : bytes, "the exact bytes as downloaded", bytes.Length));

		if (kind == "html")
		{
			string html = Encoding.UTF8.GetString(bytes);

			string textPath = Path.Combine(directory, "text.txt");
			File.WriteAllText(textPath, WrapAtWhitespace(StripHtmlTags(html)));
			listing.Append(Entry(textPath, File.ReadAllBytes(textPath), $"the page's text: tags, scripts and styles removed, entities decoded, wrapped at {MaxLineLength} characters"));

			string outlinePath = Path.Combine(directory, "outline.html");
			File.WriteAllText(outlinePath, BuildHtmlStructure(html));
			listing.Append(Entry(outlinePath, File.ReadAllBytes(outlinePath), "the page's tags with the text removed, one per line, for finding a section"));
		}
		GiveToShellUser(directory);
		return listing.ToString();
	}

	// One line of the listing: the path, its size, its line count when it is text, and what it is.
	private static string Entry(string path, byte[]? text, string what, long size = -1)
	{
		long   bytes = size >= 0 ? size : text?.Length ?? 0;
		string lines = text != null ? $", {Count(CountLines(text))} lines" : string.Empty;
		return $"- {path}  ({Count(bytes)} bytes{lines}): {what}\n";
	}

	private static int CountLines(byte[] text)
	{
		int count = 0;
		foreach (byte b in text)
		{
			if (b == (byte)'\n')
				count++;
		}
		if (text.Length > 0 && text[^1] != (byte)'\n')
			count++;
		return count;
	}

	private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

	private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

	// A one-line reason for a failed fetch. The full exception carries a dozen frames of HttpClient
	// plumbing that say nothing about why the URL failed — a DNS miss or a refused connection reads
	// identically in the stack — so the innermost message is reported instead. It costs the model a
	// line of context rather than a screenful, and reads as a fact about the URL, not a crash.
	private static string Describe(Exception ex)
	{
		Exception innermost = ex;
		while (innermost.InnerException != null)
			innermost = innermost.InnerException;

		if (ReferenceEquals(innermost, ex) || string.Equals(innermost.Message, ex.Message, StringComparison.Ordinal))
			return ex.Message;
		return $"{ex.Message} ({innermost.Message})";
	}

	// Reads the response body into memory, abandoning it the moment it crosses MaxDownloadBytes so an
	// unbounded resource can never be pulled in full. Returns the bytes when within the cap, or tooBig=true.
	private static async Task<(byte[]? bytes, bool tooBig)> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		using Stream stream       = await response.Content.ReadAsStreamAsync(cancellationToken);
		using MemoryStream buffer = new MemoryStream();
		byte[] chunk              = new byte[81920];
		long   total              = 0;

		int read;
		while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
		{
			total += read;
			if (total > MaxDownloadBytes)
				return (null, true);
			buffer.Write(chunk, 0, read);
		}

		return (buffer.ToArray(), false);
	}

	// Picks the next numbered folder for a fetch. The path is in the tool's result, and a seeded run must see the
	// same result, so calls take their numbers in the order they start, before any download. The count
	// is saved, so a resumed session carries on. Numbers already on disk are skipped: a session from before it was
	// saved starts at 1 again, and earlier files may be in use.
	private static string ReserveFolder()
	{
		lock (kFolderGate)
		{
			string directory;
			do
			{
				directory = Path.Combine(kScratchFolder, $"fetch_{SessionService.NextCounter("fetch")}");
			}
			while (Directory.Exists(directory) || IsFileOrLink(directory));
			return directory;
		}
	}

	private static bool IsFileOrLink(string path)
	{
		return new FileInfo(path).LinkTarget != null || File.Exists(path);
	}

	// Makes the reserved folder empty and ready. The shell can write under the scratch folder and the agent is root,
	// so whatever the model's shell put at these paths is cleared first: a link there would send root's writes
	// wherever it points.
	private static void PrepareFolder(string directory)
	{
		if (IsFileOrLink(kScratchFolder))
			File.Delete(kScratchFolder);
		if (!Directory.Exists(kScratchFolder))
		{
			Directory.CreateDirectory(kScratchFolder);
			Sandbox.GiveToShellUser(kScratchFolder);
		}

		if (IsFileOrLink(directory))
			File.Delete(directory);
		else if (Directory.Exists(directory))
			Directory.Delete(directory, true);
		Directory.CreateDirectory(directory);
	}

	// Classifies the content so the right views are built and the caller knows what it is looking at. The
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

		string path  = url;
		int    query = path.IndexOf('?');
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
		// it must be classified as binary so it gets `file`'s description and a note to extract it rather than
		// being read as text. Checked before the text sniff, which would otherwise misread it.
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

	// Names the raw file after the resource's own filename so the caller sees what the thing is actually
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
		string path  = url;
		int    query = path.IndexOf('?');
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

	// File extension for the raw view, matching the classification so on-disk tooling (and the caller) can tell
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
		int           i  = 0;
		int           n  = html.Length;
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
		int           i  = 0;
		int           n  = html.Length;
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
		string closeTag   = "</" + tagName;
		int    closeIndex = html.IndexOf(closeTag, openIndex, StringComparison.OrdinalIgnoreCase);
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
		StringBuilder sb           = new StringBuilder(text.Length);
		bool          pendingSpace = false;
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

		StringBuilder sb    = new StringBuilder(text.Length + text.Length / MaxLineLength + 1);
		int           start = 0;
		int           n     = text.Length;
		while (start < n)
		{
			int remaining = n - start;
			if (remaining <= MaxLineLength)
			{
				sb.Append(text, start, remaining);
				break;
			}

			int limit   = start + MaxLineLength;
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