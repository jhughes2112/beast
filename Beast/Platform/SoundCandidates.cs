using System.Collections.Generic;
using System.IO;


// Sound settings are lists of candidate paths so one settings.json serves every machine: the first path
// that exists on this one is the sound it plays.
internal static class SoundCandidates
{
	internal static string FirstExisting(IReadOnlyList<string> candidates)
	{
		string found = string.Empty;
		foreach (string path in candidates)
		{
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				found = path;
				break;
			}
		}
		return found;
	}
}