using System;
using System.Collections.Generic;
using System.IO;
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

	public async Task<ToolResult> InspectAsync(
		string            toolCallId,
		string            filePath,
		string            goal,
		bool              useSubagent,
		Role?             mediaRole,
		LlmRegistry       registry,
		Session           session,
		ITransportServer  transport,
		int               maxOutputTokens,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(filePath))
			return new ToolResult(toolCallId, string.Empty, "Error: file_path cannot be empty", 1, 0);
		if (!File.Exists(filePath))
			return new ToolResult(toolCallId, string.Empty, $"Error: File not found: {filePath}", 1, 0);

		(MediaKind kind, string mimeType) = MediaKinds.Classify(filePath);
		if (kind == MediaKind.Text || kind == MediaKind.Unknown)
			return new ToolResult(toolCallId, string.Empty, $"Error: '{Path.GetExtension(filePath)}' is not media this tool can interpret. Images, audio, and video only — use read_file for text.", 1, 0);

		long fileBytes = new FileInfo(filePath).Length;
		if (fileBytes > MaxFileBytes)
			return new ToolResult(toolCallId, string.Empty, $"Error: {filePath} is {fileBytes / (1024 * 1024)}MB; the limit is {MaxFileBytes / (1024 * 1024)}MB.", 1, 0);

		MediaProbe probe    = await MediaProbe.ProbeAsync(filePath, ct);
		string     fullPath = Path.GetFullPath(filePath);
		string?    sizeNote = LargeNote(filePath, kind, fileBytes, probe);
		// A second request for the same path is consent to send whatever is there now, so the
		// model can shrink a file in place and call again.
		if (sizeNote != null && session.MarkLargeMediaSeen(fullPath))
			return new ToolResult(toolCallId, sizeNote, string.Empty, 0, ToolDispatch.EstimateTokens(sizeNote));

		LlmModel? current = registry.GetModel(session.Model);
		if (!useSubagent && current != null && MediaKinds.Supports(current.Config, kind))
		{
			string facts = Describe(kind, fileBytes, probe);
			string text  = $"{filePath} ({facts}) is attached to this result for you to examine directly.";
			return new ToolResult(toolCallId, text, string.Empty, 0, ToolDispatch.EstimateTokens(text), fullPath, mimeType);
		}

		// Delegating: the goal is what the reader answers, so without one there is nothing to ask.
		if (string.IsNullOrWhiteSpace(goal))
		{
			string why = useSubagent
				? "use_subagent was set"
				: $"the current model does not declare '{MediaKinds.Modality(kind)}' input, so a MediaReader subagent will look instead";
			return new ToolResult(toolCallId, string.Empty, $"Error: {why}; a goal is required saying exactly what to extract from {filePath}.", 1, 0);
		}

		// The subagent path is the only one that needs the MediaReader role.
		if (mediaRole == null)
			return new ToolResult(toolCallId, string.Empty, "Error: no MediaReader role is configured, so the file cannot be handed to a subagent. Add the role in roles.json, or switch to a model that accepts this input with /model.", 1, 0);

		// Candidates come from the MediaReader role's own list, in its order — the user arranges
		// that order in /role, and it beats any cheapest-first guess.
		List<LlmModel> capable = MediaKinds.CapableModels(registry, kind, mediaRole.Models);
		if (capable.Count == 0)
			return new ToolResult(toolCallId, string.Empty, $"Error: no model in the MediaReader role declares '{MediaKinds.Modality(kind)}' input. Enable one with /config (its modalities are discovered or set there).", 1, 0);

		return await InspectWithModelsAsync(toolCallId, filePath, goal, mediaRole, capable, registry, session, transport, maxOutputTokens, ct);
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
	private static string? LargeNote(string filePath, MediaKind kind, long fileBytes, MediaProbe probe)
	{
		string? note  = null;
		string  facts = Describe(kind, fileBytes, probe);

		if (kind == MediaKind.Image)
		{
			bool large = fileBytes > LargeImageBytes || probe.Width > LargeImageEdge || probe.Height > LargeImageEdge;
			if (large)
			{
				note = $"{filePath} is large ({facts}). Nothing was sent. Providers downscale images past about {LargeImageEdge} px on the long edge, "
					+ "and every pixel sent costs context on every later turn. Either shrink or crop it first "
					+ $"(e.g. ffmpeg -i \"{filePath}\" -vf \"scale={LargeImageEdge}:{LargeImageEdge}:force_original_aspect_ratio=decrease\" out.png) and inspect that, "
					+ "or call inspect_media on this same path again to send it as-is.";
			}
		}
		else
		{
			bool large = fileBytes > LargeClipBytes || probe.DurationSeconds > LargeDurationSeconds;
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

	// Runs the file past the given candidate models, in the caller's preference order, stopping at the first one that
	// answers. Falling through matters because a declared modality is only a claim: a model that
	// rejects the attachment at request time should cost the caller a retry on the next candidate,
	// not the whole call.
	public async Task<ToolResult> InspectWithModelsAsync(
		string            toolCallId,
		string            filePath,
		string            goal,
		Role              mediaRole,
		List<LlmModel>    candidates,
		LlmRegistry       registry,
		Session           session,
		ITransportServer  transport,
		int               maxOutputTokens,
		CancellationToken ct)
	{
		(MediaKind kind, string mimeType) = MediaKinds.Classify(filePath);
		byte[] bytes                      = await File.ReadAllBytesAsync(filePath, ct);
		string data                       = Convert.ToBase64String(bytes);

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
			string prompt = $"Goal: {goal}\nFile: {filePath}\n\nThe media file is attached.";
			stage.Bundle.Canonical.OnUserMessageWithAttachments(prompt, new List<MediaAttachment> { new MediaAttachment(mimeType, data) });

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