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
//   - the current model declares the modality  -> attach it, so the model sees the actual pixels
//     and can be re-asked about it for the rest of the conversation
//   - it does not                              -> the cheapest enabled model that CAN see it is
//     asked to describe it, and the description rides along as text
//   - the file is text                         -> inlined verbatim
//   - nothing can read it                      -> a one-line note naming the type and size
//
// Attaching to the current model is preferred wherever possible because it is the only option that
// survives follow-up questions: a description answers once and forgets.
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

	// Removes a staged copy once it has been folded into a turn. Best-effort: a file left behind by
	// a crash is harmless, and failing the turn over a locked temp file would not be.
	public static void DiscardStaged(string stagedName)
	{
		try
		{
			string staged = Path.Combine(StagingFolder(), stagedName);
			if (File.Exists(staged))
				File.Delete(staged);
		}
		catch (Exception)
		{
		}
	}

	// Resolves one staged file. stagedName is the file's name inside the staging folder;
	// originalPath is where it came from on the user's machine, used for display and for the
	// model's benefit (it may be able to read it directly if it lives in the workspace).
	public static async Task<(string Text, MediaAttachment? Attachment, bool Retain)> ResolveAsync(
		string stagedName,
		string originalPath,
		Session session,
		LlmRegistry registry,
		CancellationToken ct)
	{
		string staged = Path.Combine(StagingFolder(), stagedName);
		string display = string.IsNullOrEmpty(originalPath) ? stagedName : originalPath;

		if (!File.Exists(staged))
			return ($"[attachment {display} could not be read: the staged copy is missing]", null, false);

		long bytes = new FileInfo(staged).Length;
		(MediaKind kind, string mimeType) = MediaKinds.Classify(originalPath.Length > 0 ? originalPath : stagedName);

		if (kind == MediaKind.Text)
			return (await InlineTextAsync(staged, display, ct), null, false);

		if (kind == MediaKind.Unknown)
			return ($"[attachment {display}: {bytes} bytes of an unrecognized type — no model can read it directly]", null, false);

		if (bytes > MaxAttachmentBytes)
			return ($"[attachment {display} is {bytes / (1024 * 1024)}MB, over the {MaxAttachmentBytes / (1024 * 1024)}MB limit — not sent]", null, false);

		// The session's own model gets first refusal: attaching beats describing whenever it works.
		LlmModel? current = registry.GetModel(session.Model);
		if (current != null && MediaKinds.Supports(current.Config, kind))
		{
			string data = Convert.ToBase64String(await File.ReadAllBytesAsync(staged, ct));
			return ($"[attached {display}]", new MediaAttachment(mimeType, data), false);
		}

		// Otherwise the file cannot ride along, and describing it HERE would be wrong twice over:
		// the description would appear before the user's own message (it is produced while that
		// message is still being assembled), and it would arrive as unattributed assistant text
		// for work no one can see happening. Point the model at inspect_media instead — it does
		// the same job through the cheapest capable model, but as a visible tool call, in order.
		string modality = MediaKinds.Modality(kind);
		if (MediaKinds.CapableModels(registry, kind).Count == 0)
		{
			return ($"[attached {display} — neither the current model nor any other enabled model accepts '{modality}' input, so it could not be read. "
				+ "Enable one with /config, or tell the user what you would need.]", null, false);
		}

		// The staged copy is what inspect_media must open: the original path may be outside the
		// workspace and unreadable from here.
		return ($"[attached {display} — the current model cannot accept '{modality}' input. "
			+ $"Call inspect_media with file_path \"{staged}\" and a goal describing what you need from it; a capable model will read it and answer.]", null, true);
	}

	private static async Task<string> InlineTextAsync(string staged, string display, CancellationToken ct)
	{
		string content = await File.ReadAllTextAsync(staged, ct);
		bool clipped = content.Length > MaxInlineTextChars;
		if (clipped)
			content = content.Substring(0, MaxInlineTextChars);

		StringBuilder sb = new StringBuilder();
		sb.Append("[attached ").Append(display).Append(clipped ? " — truncated]" : "]").Append('\n');
		sb.Append(content);
		if (clipped)
			sb.Append("\n[…truncated; read the original file for the rest]");
		return sb.ToString();
	}

}
