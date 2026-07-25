using System;
using System.Collections.Generic;


// What a file is, for the purpose of getting it in front of a model.
public enum MediaKind
{
	Image,
	Audio,
	Video,
	// Readable as text: inlined directly, no model capability required.
	Text,
	// Neither renderable nor readable — reported by name and size only.
	Unknown
}

// Maps file extensions to a media kind and wire mime type. One table, shared by the drag-and-drop
// intake and the inspect_media tool, so both agree on what a file is.
public static class MediaKinds
{
	private static readonly Dictionary<string, string> kImage = new(StringComparer.OrdinalIgnoreCase)
	{
		{ ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" },
		{ ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".bmp", "image/bmp" }
	};

	private static readonly Dictionary<string, string> kAudio = new(StringComparer.OrdinalIgnoreCase)
	{
		{ ".wav", "audio/wav" }, { ".mp3", "audio/mp3" }, { ".m4a", "audio/m4a" },
		{ ".ogg", "audio/ogg" }, { ".flac", "audio/flac" }
	};

	private static readonly Dictionary<string, string> kVideo = new(StringComparer.OrdinalIgnoreCase)
	{
		{ ".mp4", "video/mp4" },   { ".mov", "video/quicktime" }, { ".webm", "video/webm" },
		{ ".mpeg", "video/mpeg" }, { ".mpg", "video/mpeg" }
	};

	// Extensions safe to inline verbatim. Deliberately a list rather than a binary sniff: a file
	// that merely looks textual can still be a multi-megabyte blob of noise.
	private static readonly HashSet<string> kText = new(StringComparer.OrdinalIgnoreCase)
	{
		".txt", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
		".csv", ".tsv", ".log", ".sql", ".sh", ".bash", ".ps1", ".bat", ".cmd",
		".cs", ".c", ".h", ".cpp", ".hpp", ".java", ".js", ".jsx", ".ts", ".tsx", ".py", ".rb", ".go",
		".rs", ".php", ".swift", ".kt", ".scala", ".lua", ".pl", ".r", ".m",
		".html", ".htm", ".css", ".scss", ".less", ".vue", ".svelte",
		".csproj", ".sln", ".slnx", ".props", ".targets", ".gradle", ".gitignore", ".editorconfig", ".dockerfile"
	};

	// Classifies by extension. MimeType is empty for Text and Unknown, which never go on the wire
	// as media.
	public static (MediaKind Kind, string MimeType) Classify(string path)
	{
		string extension = System.IO.Path.GetExtension(path);

		if (kImage.TryGetValue(extension, out string? imageMime))
			return (MediaKind.Image, imageMime);
		if (kAudio.TryGetValue(extension, out string? audioMime))
			return (MediaKind.Audio, audioMime);
		if (kVideo.TryGetValue(extension, out string? videoMime))
			return (MediaKind.Video, videoMime);
		if (kText.Contains(extension))
			return (MediaKind.Text, string.Empty);

		// An extensionless file (Dockerfile, Makefile, LICENSE) is far more often text than binary.
		if (extension.Length == 0)
			return (MediaKind.Text, string.Empty);

		return (MediaKind.Unknown, string.Empty);
	}

	// The ModelConfig.Input modality a kind requires, empty when no model capability is involved.
	public static string Modality(MediaKind kind)
	{
		switch (kind)
		{
			case MediaKind.Image:
				return "image";
			case MediaKind.Audio:
				return "audio";
			case MediaKind.Video:
				return "video";
			default:
				return string.Empty;
		}
	}

	// True when the model declares the modality this kind needs. A model that declares nothing is
	// treated as text-only rather than assumed capable: guessing wrong costs a failed turn.
	public static bool Supports(ModelConfig config, MediaKind kind)
	{
		string modality = Modality(kind);
		if (modality.Length == 0)
			return false;

		foreach (string input in config.Input)
		{
			if (string.Equals(input, modality, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	// The models from a role's list that can take this kind, IN THE ROLE'S ORDER. The role's
	// ordering is the user's stated preference (arranged in /role), so it wins over any
	// cheapest-first heuristic — and selecting outside the role would suggest or use models the
	// role's own machinery (e.g. /model) refuses.
	public static List<LlmModel> CapableModels(LlmRegistry registry, MediaKind kind, IReadOnlyList<string> roleModelIds)
	{
		List<LlmModel> capable = new List<LlmModel>();
		foreach (string modelId in roleModelIds)
		{
			LlmModel? model = registry.GetModel(modelId);
			if (model != null && Supports(model.Config, kind))
				capable.Add(model);
		}
		return capable;
	}
}