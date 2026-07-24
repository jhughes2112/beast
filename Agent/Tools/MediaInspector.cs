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

	// (extension → mime) for the media kinds the wire formats can carry.
	private static readonly Dictionary<string, string> kMimeByExtension = new(StringComparer.OrdinalIgnoreCase)
	{
		{ ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" },
		{ ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".bmp", "image/bmp" },
		{ ".wav", "audio/wav" }, { ".mp3", "audio/mp3" }, { ".m4a", "audio/m4a" },
		{ ".ogg", "audio/ogg" }, { ".flac", "audio/flac" }
	};

	public async Task<ToolResult> InspectAsync(
		string toolCallId,
		string filePath,
		string goal,
		Role mediaRole,
		LlmRegistry registry,
		Session session,
		ITransportServer transport,
		int maxOutputTokens,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(filePath))
			return new ToolResult(toolCallId, string.Empty, "Error: file_path cannot be empty", 1, 0);
		if (string.IsNullOrWhiteSpace(goal))
			return new ToolResult(toolCallId, string.Empty, "Error: goal cannot be empty", 1, 0);
		if (!File.Exists(filePath))
			return new ToolResult(toolCallId, string.Empty, $"Error: File not found: {filePath}", 1, 0);

		string extension = Path.GetExtension(filePath);
		if (!kMimeByExtension.TryGetValue(extension, out string? mimeType))
			return new ToolResult(toolCallId, string.Empty, $"Error: unsupported media type '{extension}'. Supported: png, jpg, gif, webp, bmp, wav, mp3, m4a, ogg, flac.", 1, 0);

		long fileBytes = new FileInfo(filePath).Length;
		if (fileBytes > MaxFileBytes)
			return new ToolResult(toolCallId, string.Empty, $"Error: {filePath} is {fileBytes / (1024 * 1024)}MB; the limit is {MaxFileBytes / (1024 * 1024)}MB.", 1, 0);

		string modality = mimeType.StartsWith("audio/", StringComparison.Ordinal) ? "audio" : "image";
		LlmService? service = PickCapableService(mediaRole, registry, modality);
		if (service == null)
			return new ToolResult(toolCallId, string.Empty, $"Error: no enabled model declares '{modality}' input. Enable one with /config (its modalities are discovered or set there).", 1, 0);

		byte[] bytes = await File.ReadAllBytesAsync(filePath, ct);
		MediaAttachment attachment = new MediaAttachment(mimeType, Convert.ToBase64String(bytes));

		// Throwaway stage session reusing the caller's ID: the streamed answer renders in the
		// caller's view, nothing is announced or saved, and cost rolls up to the real session.
		BeastSession data = new BeastSession(session.Id, session.DisplayName, service.Model.ConfigId, mediaRole.Name,
			string.Empty, 0, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session stage = new Session(data, mediaRole.SystemPrompt, transport, session.IsSubagent);
		stage.UpdateModel(service.Model);
		string prompt = $"Goal: {goal}\nFile: {filePath}\n\nThe media file is attached.";
		stage.Bundle.Canonical.OnUserMessageWithAttachments(prompt, new List<MediaAttachment> { attachment });

		ProtocolResult result = await service.RunToCompletionAsync(stage, Array.Empty<Tool>(), null, 0, maxOutputTokens, false, transport, ct);
		session.RecordCost(stage.TotalCost);

		ToolResult final;
		if (result.Outcome == ProtocolCallOutcome.Success)
		{
			string answer = result.Payload!.AssistantText;
			final = new ToolResult(toolCallId, answer, string.Empty, 0, Math.Max(1, result.Payload.Usage.CompletionTokens));
		}
		else
		{
			// Try-and-see is the last word on capability: the provider rejecting the attachment
			// (or anything else terminal) surfaces here so the model — and the user — see why.
			string detail = string.IsNullOrEmpty(result.ErrorMessage) ? result.Outcome.ToString() : result.ErrorMessage;
			final = new ToolResult(toolCallId, string.Empty, $"Error: {service.Model.Config.Name} could not process {filePath}: {detail}", 1, 0);
		}
		return final;
	}

	// First model in the role's list whose declared input modalities include the required one.
	private static LlmService? PickCapableService(Role mediaRole, LlmRegistry registry, string modality)
	{
		foreach (string modelId in mediaRole.Models)
		{
			LlmModel? model = registry.GetModel(modelId);
			if (model == null)
				continue;

			bool capable = false;
			foreach (string input in model.Config.Input)
			{
				if (string.Equals(input, modality, StringComparison.OrdinalIgnoreCase))
				{
					capable = true;
					break;
				}
			}
			if (!capable)
				continue;

			LlmService? service = registry.CreateServiceById(modelId, 0);
			if (service != null)
				return service;
		}
		return null;
	}
}
