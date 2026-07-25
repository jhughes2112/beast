using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


// Backs the inspect_media tool. Reads an image or audio file, attaches it to a throwaway
// MediaReader session (the same stage-session pattern the Summarizer uses — the caller's
// conversation is never touched and needs no media-capable model of its own), and returns the
// goal-directed text the MediaReader produces. Model selection is capability-driven: the first
// model in the MediaReader role whose declared input modalities cover the file's kind is used;
// if none declares it, the tool reports that instead of sending media a model cannot see.
// Capability declarations come from /config discovery, and the truth stays with the provider: a
// model that turns out not to accept the attachment fails the call, and that failure is the
// tool's error result.
public class MediaInspector
{
	// Attachments above this size are refused outright: they would dwarf any context window.
	private const long MaxFileBytes = 16 * 1024 * 1024;

	public async Task<ToolResult> InspectAsync(
		string            toolCallId,
		string            filePath,
		string            goal,
		Role              mediaRole,
		LlmRegistry       registry,
		Session           session,
		ITransportServer  transport,
		int               maxOutputTokens,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(filePath))
			return new ToolResult(toolCallId, string.Empty, "Error: file_path cannot be empty", 1, 0);
		if (string.IsNullOrWhiteSpace(goal))
			return new ToolResult(toolCallId, string.Empty, "Error: goal cannot be empty", 1, 0);
		if (!File.Exists(filePath))
			return new ToolResult(toolCallId, string.Empty, $"Error: File not found: {filePath}", 1, 0);

		(MediaKind kind, string mimeType) = MediaKinds.Classify(filePath);
		if (kind == MediaKind.Text || kind == MediaKind.Unknown)
			return new ToolResult(toolCallId, string.Empty, $"Error: '{Path.GetExtension(filePath)}' is not media this tool can interpret. Images, audio, and video only — use read_file for text.", 1, 0);

		long fileBytes = new FileInfo(filePath).Length;
		if (fileBytes > MaxFileBytes)
			return new ToolResult(toolCallId, string.Empty, $"Error: {filePath} is {fileBytes / (1024 * 1024)}MB; the limit is {MaxFileBytes / (1024 * 1024)}MB.", 1, 0);

		// Candidates come from the MediaReader role's own list, in its order — the user arranges
		// that order in /role, and it beats any cheapest-first guess.
		List<LlmModel> capable = MediaKinds.CapableModels(registry, kind, mediaRole.Models);
		if (capable.Count == 0)
			return new ToolResult(toolCallId, string.Empty, $"Error: no model in the MediaReader role declares '{MediaKinds.Modality(kind)}' input. Enable one with /config (its modalities are discovered or set there).", 1, 0);

		return await InspectWithModelsAsync(toolCallId, filePath, goal, mediaRole, capable, registry, session, transport, maxOutputTokens, ct);
	}

	// Runs the file past the given candidate models, in the caller's preference order, stopping at the first one that
	// answers. Falling through matters because a declared modality is only a claim: a model that
	// rejects the attachment at request time should cost the caller a retry on the next candidate,
	// not the whole call. Shared with the drag-and-drop intake path.
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
				string.Empty, 0, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
			Session stage = new Session(stageData, mediaRole.SystemPrompt, quiet, session.IsSubagent);
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