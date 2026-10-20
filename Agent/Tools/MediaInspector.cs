using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Backs the inspect_media tool. Two ways to get a file in front of a model:
//  - Native: the session's current model declares the file's modality, so the result carries the
//    file itself and the model's own attention runs over the pixels. This is the default whenever
//    it is possible — a description from another model is never a substitute for seeing.
//  - Subagent: the current model cannot take the modality, or the caller asked for delegation
//    (a long recording where only a transcript is wanted, bulk extraction where holding the
//    payload in the caller's window is the wrong economics). The file is attached to a throwaway
//    MediaReader session — the same stage-session pattern the Summarizer uses — and the tool
//    returns the goal-directed text it produces. Model selection is capability-driven: the first
//    model in the MediaReader role whose declared input modalities cover the file's kind is used.
// Capability declarations come from /config discovery, and the truth stays with the provider: a
// model that turns out not to accept the attachment fails the call, and that failure is the
// tool's error result.
public class MediaInspector
{
	// Attachments above this size are refused outright: they would dwarf any context window.
	private const long MaxFileBytes = 16 * 1024 * 1024;

	// Above these the first request for a file reports its size instead of sending it, so the
	// model can resize or clip with ffmpeg first. Providers downscale images past roughly this
	// edge anyway, so pixels beyond it are paid for and thrown away.
	private const int    LargeImageEdge       = 1568;
	private const long   LargeImageBytes      = 2 * 1024 * 1024;
	private const double LargeDurationSeconds = 60;
	private const long   LargeClipBytes       = 4 * 1024 * 1024;

	// ffmpeg's default scaler is bicubic, which softens a shrunk image. Lanczos keeps edges and fine
	// text crisp, accurate_rnd and full_chroma_int avoid banding and colour smear. Part of a copy's
	// identity, so copies made with an older filter are not reused.
	private const string ScaleFlags = "lanczos+accurate_rnd+full_chroma_int";

	// One requested file as this call resolved it: the path the model named, the file actually sent
	// (a downscaled copy when max_dimension shrank it), and the physical facts of what is sent.
	private sealed record Item(string Requested, string SendPath, MediaKind Kind, string MimeType, long Bytes, MediaProbe Probe, string ResizedFrom);

	// filePaths are shown together, in order, so the model can compare them. maxDimension (0 = keep
	// the original) downscales any image whose longer edge exceeds it, aspect ratio kept.
	public async Task<ToolResult> InspectAsync(
		string                toolCallId,
		IReadOnlyList<string> filePaths,
		int                   maxDimension,
		string                goal,
		bool                  useSubagent,
		Role?                 mediaRole,
		LlmRegistry           registry,
		Session               session,
		ITransportServer      transport,
		int                   maxOutputTokens,
		CancellationToken     ct)
	{
		if (filePaths.Count == 0)
			return new ToolResult(toolCallId, string.Empty, "Error: file_paths must name at least one file", 1, 0);
		if (maxDimension < 0)
			return new ToolResult(toolCallId, string.Empty, "Error: max_dimension must be a positive number of pixels", 1, 0);

		// Every file must resolve before any is sent: a comparison missing one side is not the one asked for.
		List<Item> items = new List<Item>();
		foreach (string filePath in filePaths)
		{
			(Item? item, string error) = await ResolveAsync(filePath, maxDimension, ct);
			if (item == null)
				return new ToolResult(toolCallId, string.Empty, error, 1, 0);
			items.Add(item);
		}

		// A second request for the same path is consent to send whatever is there now, so the
		// model can shrink a file in place and call again.
		List<string> sizeNotes = new List<string>();
		foreach (Item item in items)
		{
			string? sizeNote = LargeNote(item);
			if (sizeNote != null && session.MarkLargeMediaSeen(item.SendPath))
				sizeNotes.Add(sizeNote);
		}
		if (sizeNotes.Count > 0)
		{
			string notes = string.Join("\n", sizeNotes);
			if (items.Count > 1)
				notes = $"{notes}\nNothing was sent for any of the {items.Count} files.";
			return new ToolResult(toolCallId, notes, string.Empty, 0, ToolDispatch.EstimateTokens(notes));
		}

		List<MediaKind> kinds = new List<MediaKind>();
		List<MediaFile> files = new List<MediaFile>();
		foreach (Item item in items)
		{
			if (!kinds.Contains(item.Kind))
				kinds.Add(item.Kind);
			files.Add(new MediaFile(item.SendPath, item.MimeType));
		}

		LlmModel? current = registry.GetModel(session.Model);
		if (!useSubagent && current != null && SupportsAll(current.Config, kinds))
		{
			// Superseded media leaves the context (see MediaBudget): say so now, while the model can still act on it.
			string text = $"{Listing(items)}\n{(items.Count > 1 ? $"These {items.Count} files are" : "This file is")} attached to this result, in this order, for you to examine directly. "
				+ "They stay in view while you reason about them and are removed from context only when you next call inspect_media, so note anything you will need later; "
				+ "call inspect_media on them again if you need to see them again.";
			return new ToolResult(toolCallId, text, string.Empty, 0, ToolDispatch.EstimateTokens(text), files);
		}

		// Delegating: the goal is what the reader answers, so without one there is nothing to ask.
		if (string.IsNullOrWhiteSpace(goal))
		{
			string why = useSubagent
				? "use_subagent was set"
				: $"the current model does not declare {Modalities(kinds)} input, so a MediaReader subagent will look instead";
			return new ToolResult(toolCallId, string.Empty, $"Error: {why}; a goal is required saying exactly what to extract from {string.Join(", ", filePaths)}.", 1, 0);
		}

		// The subagent path is the only one that needs the MediaReader role.
		if (mediaRole == null)
			return new ToolResult(toolCallId, string.Empty, "Error: no MediaReader role is configured, so the files cannot be handed to a subagent. Add the role in roles.json, or switch to a model that accepts this input with /model.", 1, 0);

		// Candidates come from the MediaReader role's own list, in its order — the user arranges
		// that order in /role, and it beats any cheapest-first guess. One reader sees every file,
		// so it must take every kind among them.
		List<LlmModel> capable = new List<LlmModel>();
		foreach (LlmModel model in MediaKinds.CapableModels(registry, kinds[0], mediaRole.Models))
		{
			if (SupportsAll(model.Config, kinds))
				capable.Add(model);
		}
		if (capable.Count == 0)
			return new ToolResult(toolCallId, string.Empty, $"Error: no model in the MediaReader role declares {Modalities(kinds)} input. Enable one with /config (its modalities are discovered or set there).", 1, 0);

		return await InspectWithModelsAsync(toolCallId, files, goal, mediaRole, capable, registry, session, transport, maxOutputTokens, ct);
	}

	// Confines, classifies and measures one requested file, downscaling an image past maxDimension.
	// Returns the item, or null and the error to report.
	private static async Task<(Item? Item, string Error)> ResolveAsync(string filePath, int maxDimension, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(filePath))
			return (null, "Error: file_paths cannot contain an empty path");

		// The resolved path, symlinks followed, is the one read. Dropped attachments in .beast are
		// the one place outside the model's reach it may look, since it is told it can look again.
		(string? confined, string denied) = Sandbox.Confine(filePath, false, true);
		if (confined == null)
			return (null, denied);
		if (!File.Exists(confined))
			return (null, $"Error: File not found: {filePath}");

		(MediaKind kind, string mimeType) = MediaKinds.Classify(filePath);
		if (kind == MediaKind.Text || kind == MediaKind.Unknown)
			return (null, $"Error: '{Path.GetExtension(filePath)}' ({filePath}) is not media this tool can interpret. Images, audio, and video only — use read_file for text.");

		long fileBytes = new FileInfo(confined).Length;
		if (fileBytes > MaxFileBytes)
			return (null, $"Error: {filePath} is {fileBytes / (1024 * 1024)}MB; the limit is {MaxFileBytes / (1024 * 1024)}MB.");

		MediaProbe probe = await MediaProbe.ProbeAsync(confined, ct);
		if (kind != MediaKind.Image || maxDimension == 0 || (probe.Width <= maxDimension && probe.Height <= maxDimension))
			return (new Item(filePath, confined, kind, mimeType, fileBytes, probe, string.Empty), string.Empty);

		(string? scaled, string scaledMime, string error) = await DownscaleAsync(confined, maxDimension, ct);
		if (scaled == null)
			return (null, $"Error: {filePath} could not be downscaled to {maxDimension} px: {error}");

		MediaProbe scaledProbe = await MediaProbe.ProbeAsync(scaled, ct);
		return (new Item(filePath, scaled, kind, scaledMime, new FileInfo(scaled).Length, scaledProbe, $"{probe.Width}x{probe.Height}"), string.Empty);
	}

	// Writes a copy of an image whose longer edge is maxDimension, aspect ratio kept, beside the
	// dropped attachments: the conversation refers to media by path, so the copy must outlive this
	// call for a rehydrate to send it again. The name is fixed by the source's identity and the
	// size asked for, so asking again reuses the copy. A JPEG stays a JPEG; anything else becomes
	// a PNG (the first frame, for an animation). Returns the copy and its mime type, or null and why.
	private static async Task<(string? Path, string MimeType, string Error)> DownscaleAsync(string source, int maxDimension, CancellationToken ct)
	{
		string   extension = Path.GetExtension(source).ToLowerInvariant();
		bool     jpeg      = extension == ".jpg" || extension == ".jpeg";
		FileInfo info      = new FileInfo(source);
		byte[]   identity  = SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{ScaleFlags}"));
		string   name      = $"{Path.GetFileNameWithoutExtension(source)}-{Convert.ToHexString(identity, 0, 4).ToLowerInvariant()}-max{maxDimension}{(jpeg ? ".jpg" : ".png")}";
		string   target    = Path.Combine(MediaIntake.EnsureStagingFolder(Environment.CurrentDirectory), name);
		string   mimeType  = jpeg ? "image/jpeg" : "image/png";
		string   error     = string.Empty;

		if (!File.Exists(target))
		{
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName               = "ffmpeg",
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				UseShellExecute        = false,
				CreateNoWindow         = true
			};
			foreach (string arg in new string[] { "-v", "error", "-y", "-i", source, "-vf", $"scale={maxDimension}:{maxDimension}:force_original_aspect_ratio=decrease:flags={ScaleFlags}", "-frames:v", "1" })
				psi.ArgumentList.Add(arg);
			if (jpeg)
			{
				psi.ArgumentList.Add("-q:v");
				psi.ArgumentList.Add("2");
			}
			psi.ArgumentList.Add(target);

			try
			{
				using Process process = new Process { StartInfo = psi };
				process.Start();
				Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
				Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
				await process.WaitForExitAsync(ct);
				await stdout;
				string errors = await stderr;
				if (process.ExitCode != 0 || !File.Exists(target))
				{
					error = errors.Trim().Length > 0 ? errors.Trim() : $"ffmpeg exited with {process.ExitCode}";
					File.Delete(target);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				error = $"ffmpeg could not run: {ex.Message}";
			}
		}
		return (error.Length == 0 ? target : null, mimeType, error);
	}

	private static bool SupportsAll(ModelConfig config, List<MediaKind> kinds)
	{
		bool all = true;
		foreach (MediaKind kind in kinds)
			all = all && MediaKinds.Supports(config, kind);
		return all;
	}

	// "'image' and 'audio'" — the modalities a reader would need.
	private static string Modalities(List<MediaKind> kinds)
	{
		List<string> names = new List<string>();
		foreach (MediaKind kind in kinds)
			names.Add($"'{MediaKinds.Modality(kind)}'");
		return string.Join(" and ", names);
	}

	// One line per file: its path and the facts of what is sent, numbered when there are several.
	private static string Listing(List<Item> items)
	{
		List<string> lines = new List<string>();
		for (int i = 0; i < items.Count; i++)
		{
			Item   item    = items[i];
			string resized = item.ResizedFrom.Length > 0 ? $", downscaled from {item.ResizedFrom}" : string.Empty;
			string line    = $"{item.Requested} ({Describe(item.Kind, item.Bytes, item.Probe)}{resized})";
			lines.Add(items.Count > 1 ? $"{i + 1}. {line}" : line);
		}
		return string.Join("\n", lines);
	}

	// The physical facts a model can act on: pixel size for images, duration for clips, bytes for both.
	private static string Describe(MediaKind kind, long fileBytes, MediaProbe probe)
	{
		string size = fileBytes >= 1024 * 1024
			? $"{fileBytes / (1024.0 * 1024.0):F1} MB"
			: $"{Math.Max(1, fileBytes / 1024)} KB";

		string facts;
		if (kind == MediaKind.Image)
			facts = probe.Width > 0 ? $"{probe.Width}x{probe.Height} px, {size}" : size;
		else
			facts = probe.DurationSeconds > 0 ? $"{probe.DurationSeconds:F0} s, {size}" : size;
		return facts;
	}

	// The once-per-file warning for media big enough to matter, or null when it is fine to send.
	// Judged on what would be sent, so an image already downscaled by max_dimension passes.
	private static string? LargeNote(Item item)
	{
		string? note     = null;
		string  filePath = item.Requested;
		string  facts    = Describe(item.Kind, item.Bytes, item.Probe);

		if (item.Kind == MediaKind.Image)
		{
			bool large = item.Bytes > LargeImageBytes || item.Probe.Width > LargeImageEdge || item.Probe.Height > LargeImageEdge;
			if (large)
			{
				note = $"{filePath} is large ({facts}). Nothing was sent. Providers downscale images past about {LargeImageEdge} px on the long edge, "
					+ "and every pixel sent costs context on every later turn. Either call inspect_media again with "
					+ $"max_dimension (e.g. {LargeImageEdge}, or smaller when fine detail does not matter) to send a downscaled copy, crop it first, "
					+ "or call inspect_media on this same path again to send it as-is.";
			}
		}
		else
		{
			bool large = item.Bytes > LargeClipBytes || item.Probe.DurationSeconds > LargeDurationSeconds;
			if (large)
			{
				note = $"{filePath} is long ({facts}). Nothing was sent. A recording costs context in proportion to its length on every later turn. "
					+ $"Either clip the part you need first (e.g. ffmpeg -ss 0 -t {LargeDurationSeconds:F0} -i \"{filePath}\" out{Path.GetExtension(filePath)}), "
					+ "ask a subagent for a transcript or summary (use_subagent=true with a goal), "
					+ "or call inspect_media on this same path again to send it as-is.";
			}
		}
		return note;
	}

	// Runs the files past the given candidate models, in the caller's preference order, stopping at the first one that
	// answers. Falling through matters because a declared modality is only a claim: a model that
	// rejects the attachment at request time should cost the caller a retry on the next candidate,
	// not the whole call. Every file goes to the same reader together, so it can compare them.
	public async Task<ToolResult> InspectWithModelsAsync(
		string                   toolCallId,
		IReadOnlyList<MediaFile> files,
		string                   goal,
		Role                     mediaRole,
		List<LlmModel>           candidates,
		LlmRegistry              registry,
		Session                  session,
		ITransportServer         transport,
		int                      maxOutputTokens,
		CancellationToken        ct)
	{
		List<MediaAttachment> attachments = new List<MediaAttachment>();
		List<string>          paths       = new List<string>();
		foreach (MediaFile file in files)
		{
			byte[] bytes = await File.ReadAllBytesAsync(file.Path, ct);
			attachments.Add(new MediaAttachment(file.MimeType, Convert.ToBase64String(bytes)));
			paths.Add(file.Path);
		}
		string filePath = string.Join(", ", paths);

		// The stage session runs silently: its answer is this tool's result and belongs in the tool
		// block, in call order. Streaming it to the caller's transcript put a description on screen
		// as unattributed assistant text — and, for a drag-and-drop, before the user's own message.
		TransportSilent quiet = new TransportSilent();

		string failures = string.Empty;
		foreach (LlmModel model in candidates)
		{
			LlmService? service = registry.CreateServiceById(model.ConfigId, 0);
			if (service == null)
				continue;

			// Throwaway stage session reusing the caller's ID: nothing is announced or saved, and
			// cost rolls up to the real session.
			BeastSession stageData = new BeastSession(session.Id, session.DisplayName, service.Model.ConfigId, mediaRole.Name,
				string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
			Session stage = new Session(stageData, mediaRole.SystemPrompt, quiet, session.IsSubagent);
			stage.MarkStagePrompt();
			stage.UpdateModel(service.Model);
			string prompt = files.Count > 1
				? $"Goal: {goal}\nFiles: {filePath}\n\nThe {files.Count} media files are attached in that order."
				: $"Goal: {goal}\nFile: {filePath}\n\nThe media file is attached.";
			stage.Bundle.Canonical.OnUserMessageWithAttachments(prompt, attachments);

			ProtocolResult result = await service.RunToCompletionAsync(stage, Array.Empty<Tool>(), null, 0, maxOutputTokens, false, quiet, ct);
			session.RecordCost(stage.TotalCost);

			if (result.Outcome == ProtocolCallOutcome.Success)
			{
				// Name the model that actually looked: the answer is second-hand, and both the
				// caller and the user reading the tool block should know whose eyes produced it.
				string answer = $"{result.Payload!.AssistantText}\n\n[read by {service.Model.Config.Name}]";
				return new ToolResult(toolCallId, answer, string.Empty, 0, Math.Max(1, result.Payload.Usage.CompletionTokens));
			}

			// Try-and-see is the last word on capability: a provider rejecting the attachment is
			// recorded and the next-cheapest candidate gets a turn.
			string detail = string.IsNullOrEmpty(result.ErrorMessage) ? result.Outcome.ToString() : result.ErrorMessage;
			string reason = $"{service.Model.Config.Name}: {detail}";
			failures      = failures.Length == 0 ? reason : failures + "; " + reason;
		}

		string summary = failures.Length == 0 ? "no capable model was available" : failures;
		return new ToolResult(toolCallId, string.Empty, $"Error: {filePath} could not be interpreted: {summary}", 1, 0);
	}
}