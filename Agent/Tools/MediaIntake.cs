using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Turns files the user dropped into the input box into something the conversation can carry.
//
// The client stages each dropped file into the shared .beast/attachments folder (the one directory
// mounted into the container, so a file dragged from anywhere on the host is readable by the agent)
// and sends its staged name ahead of the message text. This resolves those into either real
// attachments on the user turn or text standing in for them:
//
//   - the current model declares the modality  -> attach it, so the model's attention runs over
//     the media itself and it stays re-askable for the rest of the conversation
//   - it does not                              -> the file is NOT sent; UnsupportedDisplay is set
//     and the caller alerts the user to switch models
//   - the file is text                         -> inlined verbatim
//   - nothing can read it                      -> a one-line note naming the type and size
//
// There is deliberately no describe-it-with-another-model fallback. A description is text, and
// text does not do what native media input does; quietly swapping one for the other would leave
// the model reasoning over a transcription while the user believed it had seen the thing.
public static class MediaIntake
{
	// Staged files above this are refused: a base64 payload of this size already dwarfs most
	// context windows, and the failure is clearer here than as a provider-side rejection.
	private const long MaxAttachmentBytes = 16 * 1024 * 1024;

	// Inlined text is capped so dropping a huge log cannot swallow the whole window.
	private const int MaxInlineTextChars = 40000;

	// The folder dropped files are staged into, under the WORKSPACE rather than the user's home:
	// the workspace is the other bind-mounted folder, and it is deleted when the worktree is
	// finished, so a dropped screenshot lives exactly as long as the work it belonged to instead
	// of accumulating in the user profile forever.
	public static string StagingFolder(string workspaceRoot)
	{
		return Path.Combine(workspaceRoot, ".beast", "attachments");
	}

	// The agent's own view of it. Its working directory IS the workspace (/workspace in the
	// container, the project folder on a native run).
	public static string StagingFolder()
	{
		return StagingFolder(Environment.CurrentDirectory);
	}

	// Creates the staging folder and marks it ignored. The folder lives inside a git checkout, so
	// without this a dropped screenshot shows up as an untracked file and can be swept into a
	// commit. A "*" gitignore inside the folder covers its contents and itself, independent of
	// whatever the surrounding project's ignore rules happen to be.
	public static string EnsureStagingFolder(string workspaceRoot)
	{
		string folder = StagingFolder(workspaceRoot);
		Directory.CreateDirectory(folder);
		try
		{
			string ignore = Path.Combine(folder, ".gitignore");
			if (!File.Exists(ignore))
				File.WriteAllText(ignore, "# Beast-generated: dropped attachments are transient local state.\n*\n");
		}
		catch (Exception)
		{
		}
		return folder;
	}

	// Strips the "[ path ... ]" markers the client inserts, leaving what the user actually wrote.
	// The marker exists so the client knows what to stage; once the file is attached it is pure
	// noise to the model — the image is right there in the message, and repeating its path (twice,
	// with an "[attached …]" note) spent tokens telling the model something it can see.
	public static string StripPathMarkers(string text)
	{
		return StripPathMarkers(text, null);
	}

	// stagedPaths, when given, limits the strip to markers whose path actually reached the agent
	// as a staged attachment. A marker for a file the client FAILED to stage stays in the text —
	// stripping it too made that file vanish without a trace, so the model (and the transcript)
	// never learned it was ever asked about.
	public static string StripPathMarkers(string text, ISet<string>? stagedPaths)
	{
		const string open  = "[ path ";
		const string close = " ]";

		StringBuilder clean = new StringBuilder();
		int           scan  = 0;
		while (true)
		{
			int start = text.IndexOf(open, scan, StringComparison.Ordinal);
			if (start < 0)
				break;
			int end = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
			if (end < 0)
				break;

			if (stagedPaths != null)
			{
				string markerPath = text.Substring(start + open.Length, end - start - open.Length).Trim();
				if (!stagedPaths.Contains(markerPath))
				{
					// Not one of ours to remove: keep the marker and continue past it.
					clean.Append(text, scan, end + close.Length - scan);
					scan = end + close.Length;
					continue;
				}
			}

			clean.Append(text, scan, start - scan);
			scan = end + close.Length;

			// The spaces that surrounded the marker would otherwise collapse into a doubled gap.
			while (scan < text.Length && text[scan] == ' ' && clean.Length > 0 && clean[clean.Length - 1] == ' ')
				scan++;
		}
		clean.Append(text, scan, text.Length - scan);
		return clean.ToString().Trim();
	}

	// A staged name is a bare file name inside the staging folder, nothing more. It arrives over
	// the user-accessible /attach command, so anything path-like is rejected rather than combined:
	// a rooted path would REPLACE the staging prefix and ".." segments would escape it, after which
	// resolve would read — and the discard that follows every consumed attachment would DELETE —
	// an arbitrary file the agent can reach. Null when the name is not a plain file name.
	private static string? SafeStagedPath(string stagedName)
	{
		string? staged = null;
		if (stagedName.Length > 0
			&& string.Equals(stagedName, Path.GetFileName(stagedName), StringComparison.Ordinal)
			&& stagedName != "." && stagedName != "..")
		{
			staged = Path.Combine(StagingFolder(), stagedName);
		}
		return staged;
	}

	// Removes a staged copy once it has been folded into a turn. Best-effort: a file left behind by
	// a crash is harmless, and failing the turn over a locked temp file would not be.
	public static void DiscardStaged(string stagedName)
	{
		try
		{
			string? staged = SafeStagedPath(stagedName);
			if (staged != null && File.Exists(staged))
				File.Delete(staged);
		}
		catch (Exception)
		{
		}
	}

	// Resolves one staged file. stagedName is the file's name inside the staging folder;
	// originalPath is where it came from on the user's machine, used for display and for the
	// model's benefit (it may be able to read it directly if it lives in the workspace).
	public static async Task<(string Text, MediaAttachment? Attachment, string InspectPath, MediaKind Kind)> ResolveAsync(
		string            stagedName,
		string            originalPath,
		Session           session,
		LlmRegistry       registry,
		CancellationToken ct)
	{
		string? staged  = SafeStagedPath(stagedName);
		string  display = string.IsNullOrEmpty(originalPath) ? stagedName : originalPath;

		// A path-like name is treated exactly like a missing file: the client only ever sends bare
		// names, so anything else is a hand-typed /attach trying to read outside the staging folder.
		if (staged == null || !File.Exists(staged))
			return ($"[attachment {display} could not be read: the staged copy is missing]", null, string.Empty, MediaKind.Unknown);

		long bytes                        = new FileInfo(staged).Length;
		(MediaKind kind, string mimeType) = MediaKinds.Classify(originalPath.Length > 0 ? originalPath : stagedName);

		if (kind == MediaKind.Text)
			return (await InlineTextAsync(staged, display, ct), null, string.Empty, MediaKind.Text);

		if (kind == MediaKind.Unknown)
			return ($"[attachment {display}: {bytes} bytes of an unrecognized type — no model can read it directly]", null, string.Empty, MediaKind.Unknown);

		if (bytes > MaxAttachmentBytes)
			return ($"[attachment {display} is {bytes / (1024 * 1024)}MB, over the {MaxAttachmentBytes / (1024 * 1024)}MB limit — not sent]", null, string.Empty, kind);

		// The session's own model gets first refusal: attaching beats describing whenever it works.
		LlmModel? current = registry.GetModel(session.Model);
		if (current != null && MediaKinds.Supports(current.Config, kind))
		{
			string data = Convert.ToBase64String(await File.ReadAllBytesAsync(staged, ct));
			// No note: the attachment IS the evidence, and naming the file again only adds tokens.
			return (string.Empty, new MediaAttachment(mimeType, data), string.Empty, kind);
		}

		// Otherwise the file cannot ride along, and there is no good substitute: a description
		// produced by another model is text, and text is not what native media input buys — the
		// point of attaching an image is that the model's attention runs over the image itself,
		// which no transcription reproduces. So the file is simply not sent, and the caller raises
		// a banner telling the user to switch to a model that can take it.
		//
		// The model still gets one short line, because it can see the path marker in the user's
		// message and would otherwise be left guessing what happened to it.
		string modality = MediaKinds.Modality(kind);
		return ($"[{display} was not sent: this model does not accept {modality} input]", null, display, kind);
	}

	private static async Task<string> InlineTextAsync(string staged, string display, CancellationToken ct)
	{
		// Bounded read: text files skip the byte cap (a huge log is still a legitimate drop), so
		// reading the whole file first would balloon memory only to throw all but the head away.
		// One character past the cap is read purely to know whether truncation happened.
		char[] buffer = new char[MaxInlineTextChars + 1];
		int    read   = 0;
		using (StreamReader reader = new StreamReader(staged))
		{
			while (read < buffer.Length)
			{
				int n = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
				if (n == 0)
					break;
				read += n;
			}
		}
		bool   clipped = read > MaxInlineTextChars;
		string content = new string(buffer, 0, clipped ? MaxInlineTextChars : read);

		StringBuilder sb = new StringBuilder();
		sb.Append("[attached ").Append(display).Append(clipped ? " — truncated]" : "]").Append('\n');
		sb.Append(content);
		if (clipped)
			sb.Append("\n[…truncated; read the original file for the rest]");
		return sb.ToString();
	}

}