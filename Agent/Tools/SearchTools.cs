using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;


public static class SearchTools
{
	public static async Task<ToolResult> GlobAsync(
		string pattern,
		string path)
	{
		if (string.IsNullOrWhiteSpace(pattern))
		{
			return new ToolResult("Error: pattern cannot be empty.", false);
		}

		if (string.IsNullOrWhiteSpace(path))
		{
			return new ToolResult("Error: path cannot be empty.", false);
		}

		try
		{
			string searchPath = path;

			if (!Directory.Exists(searchPath))
			{
				string workspacePath = Path.Combine("/workspace", searchPath);
				if (!Directory.Exists(workspacePath))
				{
					return new ToolResult("Error: Directory not found: " + searchPath, false);
				}
				searchPath = workspacePath;
			}

			List<string> files = new List<string>();
			foreach (string f in Directory.GetFiles(searchPath, "*", SearchOption.AllDirectories))
			{
				string relativePath = f;
				try { relativePath = Path.GetRelativePath(searchPath, f); } catch { }
				if (GlobMatch(relativePath, pattern))
				{
					files.Add(f);
				}
			}

			files.Sort(StringComparer.Ordinal);

			if (files.Count == 0)
			{
				return new ToolResult("No files found matching '" + pattern + "' in " + searchPath, false);
			}

			StringBuilder sb = new StringBuilder();
			sb.AppendLine("Found " + files.Count + " file(s) matching '" + pattern + "' in " + searchPath + ":");
			foreach (string f in files)
			{
				sb.AppendLine("  " + f.Replace('\\', '/'));
			}

			return new ToolResult(sb.ToString().TrimEnd(), false);
		}
		catch (Exception ex)
		{
			return new ToolResult("Error: " + ex.Message, false);
		}
	}

	public static async Task<ToolResult> GrepAsync(
		string path,
		string pattern,
		int? contextLines)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return new ToolResult("Error: path cannot be empty.", false);
		}

		if (string.IsNullOrWhiteSpace(pattern))
		{
			return new ToolResult("Error: pattern cannot be empty.", false);
		}

		try
		{
			string searchPath = path;

			if (!Directory.Exists(searchPath))
			{
				string workspacePath = Path.Combine("/workspace", searchPath);
				if (!Directory.Exists(workspacePath))
				{
					return new ToolResult("Error: Directory not found: " + searchPath, false);
				}
				searchPath = workspacePath;
			}

			Regex regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

			Dictionary<string, List<(int lineNum, string line)>> fileMatches = new Dictionary<string, List<(int, string)>>();
			string[] files = Directory.GetFiles(searchPath, "*", SearchOption.AllDirectories);
			Array.Sort(files, StringComparer.Ordinal);

			foreach (string file in files)
			{
				try
				{
					string[] lines = File.ReadAllLines(file);
					List<(int, string)> matchesInFile = new List<(int, string)>();

					for (int i = 0; i < lines.Length; i++)
					{
						if (regex.IsMatch(lines[i]))
						{
							matchesInFile.Add((i + 1, lines[i].TrimEnd()));
						}
					}

					if (matchesInFile.Count > 0)
					{
						fileMatches[file] = matchesInFile;
					}
				}
				catch { }
			}

			StringBuilder sb = new StringBuilder();
			int totalMatches = 0;
			foreach (KeyValuePair<string, List<(int, string)>> kvp in fileMatches)
			{
				totalMatches += kvp.Value.Count;
			}

			sb.AppendLine("Found " + totalMatches + " match(es) in " + fileMatches.Count + " file(s) matching '" + pattern + "' in " + searchPath + ":");

			foreach (KeyValuePair<string, List<(int, string)>> kvp in fileMatches)
			{
				string relativePath = kvp.Key;
				try
				{
					relativePath = Path.GetRelativePath(searchPath, kvp.Key).Replace('\\', '/');
				}
				catch { }

				sb.AppendLine("  " + relativePath + ":");
				foreach ((int lineNum, string line) in kvp.Value)
				{
					string display = line.Length > 200 ? line.Substring(0, 200) + "…" : line;
					sb.AppendLine("    " + lineNum + ": " + display);
				}
			}

			return new ToolResult(sb.ToString().TrimEnd(), false);
		}
		catch (Exception ex)
		{
			return new ToolResult("Error: " + ex.Message, false);
		}
	}

	public static async Task<ToolResult> ListDirectoryAsync(
		string path,
		string? pattern)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return new ToolResult("Error: path cannot be empty.", false);
		}

		try
		{
			string searchPath = path;

			if (!Directory.Exists(searchPath))
			{
				string workspacePath = Path.Combine("/workspace", searchPath);
				if (!Directory.Exists(workspacePath))
				{
					return new ToolResult("Error: Directory not found: " + searchPath, false);
				}
				searchPath = workspacePath;
			}

			StringBuilder sb = new StringBuilder();
			sb.AppendLine("Contents of " + searchPath + ":");

			string[] dirs = Directory.GetDirectories(searchPath);
			Array.Sort(dirs, StringComparer.Ordinal);
			foreach (string dir in dirs)
			{
				sb.AppendLine("  [DIR]  " + Path.GetFileName(dir) + "/");
			}

			string[] files;
			if (string.IsNullOrWhiteSpace(pattern))
			{
				files = Directory.GetFiles(searchPath);
			}
			else
			{
				files = Directory.GetFiles(searchPath, pattern);
			}

			Array.Sort(files, StringComparer.Ordinal);
			foreach (string file in files)
			{
				sb.AppendLine("  [FILE] " + Path.GetFileName(file));
			}

			return new ToolResult(sb.ToString().TrimEnd(), false);
		}
		catch (Exception ex)
		{
			return new ToolResult("Error: " + ex.Message, false);
		}
	}

	// Matches a file path against a glob pattern.
	// ** matches across directory boundaries; * and ? match within a single segment.
	// For non-** patterns, only the filename is matched (not the full path).
	public static bool GlobMatch(string filePath, string pattern)
	{
		if (string.IsNullOrWhiteSpace(pattern))
		{
			return true;
		}

		string path = filePath.Replace('\\', '/');
		string pat = pattern.Replace('\\', '/');

		if (pat.Contains("**"))
		{
			return MatchDoubleStar(path, pat);
		}

		// No ** — simple patterns like *.cs should not match nested paths.
		if (path.Contains("/"))
		{
			return false;
		}
		return MatchSegment(path, pat);
	}

	private static bool MatchDoubleStar(string path, string pattern)
	{
		int dsIdx = pattern.IndexOf("**");

		string prefix = "";
		if (dsIdx > 0)
		{
			prefix = pattern.Substring(0, dsIdx).TrimEnd('/');
		}

		string suffix = "";
		if (dsIdx + 2 < pattern.Length)
		{
			suffix = pattern.Substring(dsIdx + 2).TrimStart('/');
		}

		// Check prefix: path must start with prefix up to a / boundary.
		if (!string.IsNullOrEmpty(prefix))
		{
			if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (path.Length > prefix.Length && path[prefix.Length] != '/')
			{
				return false;
			}
		}

		// No suffix — anything after prefix matches.
		if (string.IsNullOrEmpty(suffix))
		{
			return true;
		}

		// Get the part of the path after the prefix.
		string remaining = path;
		if (!string.IsNullOrEmpty(prefix))
		{
			remaining = path.Substring(prefix.Length).TrimStart('/');
		}

		// If suffix has no /, match against the filename only.
		if (!suffix.Contains("/"))
		{
			string fileName = GetFileName(remaining);
			return MatchSegment(fileName, suffix);
		}

		// Suffix has / — match remaining path against suffix using segment matching.
		return MatchSegmentPaths(remaining, suffix);
	}

	// Matches two paths segment-by-segment (for patterns with / like src/**/*.cs).
	private static bool MatchSegmentPaths(string path, string pattern)
	{
		string[] pSegs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
		string[] sSegs = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

		if (pSegs.Length < sSegs.Length)
		{
			return false;
		}

		// Match the last N segments of the path against the pattern segments.
		int offset = pSegs.Length - sSegs.Length;
		for (int i = 0; i < sSegs.Length; i++)
		{
			if (!MatchSegment(pSegs[offset + i], sSegs[i]))
			{
				return false;
			}
		}

		return true;
	}

	private static string GetFileName(string path)
	{
		int lastSlash = path.LastIndexOf('/');
		if (lastSlash >= 0 && lastSlash < path.Length - 1)
		{
			return path.Substring(lastSlash + 1);
		}
		return path;
	}

	// Matches a single path segment against a single pattern segment (no /).
	private static bool MatchSegment(string text, string pattern)
	{
		return SegMatch(text, 0, pattern, 0);
	}

	private static bool SegMatch(string text, int t, string pat, int p)
	{
		while (p < pat.Length)
		{
			char c = pat[p];
			if (c == '*')
			{
				p++;
				while (p < pat.Length && pat[p] == '*')
				{
					p++;
				}
				if (p == pat.Length)
				{
					return true;
				}
				for (int i = t; i <= text.Length; i++)
				{
					if (SegMatch(text, i, pat, p))
					{
						return true;
					}
				}
				return false;
			}
			else if (c == '?')
			{
				if (t >= text.Length)
				{
					return false;
				}
				t++;
				p++;
			}
			else
			{
				if (t >= text.Length)
				{
					return false;
				}
				if (char.ToLowerInvariant(text[t]) != char.ToLowerInvariant(c))
				{
					return false;
				}
				t++;
				p++;
			}
		}
		return t == text.Length;
	}
}
