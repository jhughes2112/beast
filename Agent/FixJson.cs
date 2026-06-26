using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;


// Six-stage healing pipeline for malformed LLM tool-call arguments, applied in priority order:
//   1. Prose/markdown wrapping — strip code fences, extract first JSON object (always runs first)
//   2. Structural repair      — close unclosed brackets, strip trailing commas/colons
//   3. Wrong tool name        — Levenshtein fuzzy match against known tool names
//   4. Wrong argument types   — coerce where unambiguous (e.g. "3" → 3 for integer)
//   5. Missing required args  — surface as a typed error (no safe default)
//   6. Extra/hallucinated args — strip silently
//
// Counters for each applied fix are tracked globally (mark/struct/name/type/extra).
// A log callback receives one line per fix: "json-heal [tag]: mark/struct/name/type/extra"
public static class FixJson
{
	private static int _markdownStrips;
	private static int _structuralRepairs;
	private static int _toolNameCorrections;
	private static int _argNameCorrections;
	private static int _typeCoercions;
	private static int _extraArgsStripped;

	// Resets all counters to zero. Call at the start of each test run so per-run diagnostics don't
	// accumulate across runs in the same process.
	public static void ResetCounters()
	{
		_markdownStrips = 0;
		_structuralRepairs = 0;
		_toolNameCorrections = 0;
		_argNameCorrections = 0;
		_typeCoercions = 0;
		_extraArgsStripped = 0;
	}

	// Returns a slash-separated snapshot of all counters: mark/struct/name/arg/type/extra
	public static string GetCounters()
		=> $"{_markdownStrips}/{_structuralRepairs}/{_toolNameCorrections}/{_argNameCorrections}/{_typeCoercions}/{_extraArgsStripped}";

	// Simple parse: strips markdown and repairs structural truncation. No schema awareness.
	public static JsonObject? TryParseObject(string json)
	{
		JsonObject? result = null;

		if (!string.IsNullOrWhiteSpace(json))
		{
			string cleaned = StripMarkdown(json, null);
			result = ParseObject(cleaned);

			if (result == null)
			{
				string normalized = FixNonStandardSyntax(cleaned);
				result = ParseObject(normalized);

				if (result == null)
				{
					string? repaired = Repair(normalized, null);
					if (repaired != null)
						result = ParseObject(repaired);
				}
			}
		}

		return result;
	}

	// Full pipeline: markdown → parse → non-standard syntax → structural repair → schema fixups.
	// Returns (obj, null) on success (coercions and extra-arg strips may have been silently applied).
	// Returns (null, error) on hard failure (unparseable after all repairs, or missing required arg).
	// log is called once per applied fix with a one-line status string.
	public static (JsonObject? args, string? error) TryParseWithSchema(string json, FunctionDefinition schema, Action<string>? log)
	{
		// Stage 1: strip markdown/prose wrapping (always first regardless of outcome)
		string cleaned = StripMarkdown(json, log);

		// Stage 2: try exact parse, then non-standard syntax fix, then structural repair
		JsonObject? obj = ParseObject(cleaned);

		if (obj == null)
		{
			string normalized = FixNonStandardSyntax(cleaned);
			obj = ParseObject(normalized);

			if (obj == null && IsLikelyTruncated(normalized))
			{
				string? repaired = Repair(normalized, log);
				if (repaired != null)
					obj = ParseObject(repaired);
			}
		}

		if (obj == null)
		{
			string kind = IsLikelyTruncated(cleaned) ? "truncated" : "malformed";
			return (null, $"Error: JSON arguments are {kind} and could not be repaired: {json}");
		}

		// Schema-based fixups: reconcile misnamed arguments to their canonical names FIRST (so a casing or
		// underscore slip is kept and counts toward the required-args check rather than being stripped),
		// then coerce types and strip genuinely-extra args.
		JsonObject? parameters = schema.Parameters;
		ReconcileArgNames(obj, parameters, log);
		ApplyTypeCoercions(obj, parameters, log);
		StripExtraArgs(obj, parameters, log);

		// Stage 5: missing required args (hard error — no safe default)
		string? missingError = CheckRequiredArgs(obj, schema.Name, parameters);
		if (missingError != null)
			return (null, missingError);

		return (obj, null);
	}

	// Stage 3: fuzzy-matches input against known tool names using Levenshtein distance.
	// Returns the corrected name (≤ threshold edits away) or null. Increments counter and logs on match.
	public static string? FuzzyMatchToolName(string input, string[] knownNames, int threshold, Action<string>? log)
	{
		string inputLower = input.ToLowerInvariant();
		string? bestMatch = null;
		int bestDistance = int.MaxValue;

		foreach (string known in knownNames)
		{
			int dist = LevenshteinDistance(inputLower, known.ToLowerInvariant());
			if (dist < bestDistance)
			{
				bestDistance = dist;
				bestMatch = known;
			}
		}

		string? result = null;

		// Distance is measured case-insensitively, so a case-only mismatch (e.g. "Bash" vs "bash") scores 0.
		// The caller already failed an exact (case-sensitive) match, so accept distance 0 too and correct the
		// casing — but only when the name actually differs ordinally, so a truly-identical input still yields
		// no correction.
		if (bestMatch != null && bestDistance <= threshold && !string.Equals(input, bestMatch, StringComparison.Ordinal))
		{
			Interlocked.Increment(ref _toolNameCorrections);
			log?.Invoke($"json-heal [name] '{input}' → '{bestMatch}': {GetCounters()}");
			result = bestMatch;
		}

		return result;
	}

	// ─── Pipeline stages ──────────────────────────────────────────────────────

	private static JsonObject? ParseObject(string json)
	{
		JsonObject? result = null;
		try
		{
			result = JsonNode.Parse(json)?.AsObject();
		}
		catch (JsonException)
		{
		}
		return result;
	}

	// Strips markdown code fences and any prose prefix before the first { or [.
	// Only increments/logs when the string actually changes.
	private static string StripMarkdown(string json, Action<string>? log)
	{
		string t = json.Trim();

		if (t.StartsWith("```", StringComparison.Ordinal))
		{
			int newline = t.IndexOf('\n');
			if (newline >= 0)
			{
				t = t.Substring(newline + 1).Trim();
				int closeFence = t.LastIndexOf("```", StringComparison.Ordinal);
				if (closeFence >= 0)
					t = t.Substring(0, closeFence).Trim();
			}
		}

		// Strip any leading prose before the first { or [
		int objStart = t.IndexOf('{');
		int arrStart = t.IndexOf('[');
		int start = -1;

		if (objStart >= 0 && (arrStart < 0 || objStart < arrStart))
			start = objStart;
		else if (arrStart >= 0)
			start = arrStart;

		if (start > 0)
			t = t.Substring(start);

		if (!string.Equals(t, json.Trim(), StringComparison.Ordinal))
		{
			Interlocked.Increment(ref _markdownStrips);
			log?.Invoke($"json-heal [mark]: {GetCounters()}");
		}

		return t;
	}

	// Converts single-quoted strings and unquoted object keys to standard double-quoted JSON.
	private static string FixNonStandardSyntax(string json)
	{
		StringBuilder sb = new StringBuilder(json.Length + 16);
		bool inDoubleStr = false;
		bool inSingleStr = false;
		bool escaped = false;
		bool expectingKey = false;

		for (int i = 0; i < json.Length; i++)
		{
			char c = json[i];

			if (escaped)
			{
				escaped = false;
				sb.Append(c);
				continue;
			}

			if (c == '\\' && (inDoubleStr || inSingleStr))
			{
				escaped = true;
				sb.Append(c);
				continue;
			}

			if (inDoubleStr)
			{
				sb.Append(c);
				if (c == '"')
					inDoubleStr = false;
				continue;
			}

			if (inSingleStr)
			{
				if (c == '\'')
				{
					sb.Append('"');
					inSingleStr = false;
				}
				else if (c == '"')
				{
					sb.Append("\\\"");
				}
				else
				{
					sb.Append(c);
				}
				continue;
			}

			// NORMAL mode
			if (c == '"')
			{ inDoubleStr = true; expectingKey = false; sb.Append(c); continue; }
			if (c == '\'')
			{ inSingleStr = true; expectingKey = false; sb.Append('"'); continue; }
			if (c == '{')
			{ expectingKey = true; sb.Append(c); continue; }
			if (c == '[')
			{ expectingKey = false; sb.Append(c); continue; }
			if (c == '}' || c == ']')
			{ expectingKey = false; sb.Append(c); continue; }
			if (c == ',')
			{ expectingKey = true; sb.Append(c); continue; }
			if (c == ':')
			{ expectingKey = false; sb.Append(c); continue; }

			// Unquoted key: letter/underscore/$ immediately followed (after whitespace) by ':'
			if (expectingKey && (char.IsLetter(c) || c == '_' || c == '$'))
			{
				int keyStart = i;
				while (i < json.Length && (char.IsLetterOrDigit(json[i]) || json[i] == '_' || json[i] == '$'))
					i++;
				int keyEnd = i;

				int j = i;
				while (j < json.Length && char.IsWhiteSpace(json[j]))
					j++;

				if (j < json.Length && json[j] == ':')
				{
					sb.Append('"');
					sb.Append(json, keyStart, keyEnd - keyStart);
					sb.Append('"');
				}
				else
				{
					sb.Append(json, keyStart, keyEnd - keyStart);
				}

				i--;  // compensate for the for-loop i++
				expectingKey = false;
				continue;
			}

			sb.Append(c);
		}

		return sb.ToString();
	}

	// Returns true if json has more open brackets than close brackets, or ends inside a string —
	// a strong signal that the output was cut off mid-stream.
	private static bool IsLikelyTruncated(string json)
	{
		int depth = 0;
		bool inStr = false;
		bool esc = false;

		foreach (char c in json)
		{
			if (esc)
			{ esc = false; continue; }
			if (c == '\\' && inStr)
			{ esc = true; continue; }
			if (c == '"')
			{ inStr = !inStr; continue; }
			if (inStr)
				continue;
			if (c == '{' || c == '[')
				depth++;
			else if (c == '}' || c == ']')
				depth--;
		}

		return depth > 0 || inStr;
	}

	// Closes unclosed strings and brackets; strips trailing structural garbage.
	// Returns null when nothing needs closing (structurally complete, some other error).
	internal static string? Repair(string json, Action<string>? log)
	{
		Stack<char> openers = new Stack<char>();
		bool inString = false;
		bool escaped = false;

		for (int i = 0; i < json.Length; i++)
		{
			char c = json[i];
			if (escaped)
			{ escaped = false; continue; }
			if (c == '\\' && inString)
			{ escaped = true; continue; }
			if (c == '"')
			{ inString = !inString; continue; }
			if (inString)
				continue;

			if (c == '{' || c == '[')
			{
				openers.Push(c);
			}
			else if (c == '}' || c == ']')
			{
				if (openers.Count == 0)
					return null;
				char open = openers.Pop();
				if ((c == '}') != (open == '{'))
					return null;
			}
		}

		string? result = null;

		if (openers.Count > 0 || inString)
		{
			string work = json.TrimEnd();
			if (inString)
				work += '"';
			work = StripTrailingStructural(work);

			StringBuilder sb = new StringBuilder(work);
			foreach (char open in openers)
				sb.Append(open == '{' ? '}' : ']');

			result = sb.ToString();
			Interlocked.Increment(ref _structuralRepairs);
			log?.Invoke($"json-heal [struct]: {GetCounters()}");
		}

		return result;
	}

	private static string StripTrailingStructural(string work)
	{
		bool changed = true;
		while (changed && work.Length > 0)
		{
			changed = false;
			string t = work.TrimEnd();
			if (t.Length == 0)
				break;

			char last = t[t.Length - 1];
			if (last == ',')
			{
				work = t.Substring(0, t.Length - 1);
				changed = true;
			}
			else if (last == ':')
			{
				string afterColon = t.Substring(0, t.Length - 1).TrimEnd();
				work = StripTrailingString(afterColon);
				changed = true;
			}
		}
		return work;
	}

	private static string StripTrailingString(string work)
	{
		string t = work.TrimEnd();
		string result = work;

		if (t.Length > 0 && t[t.Length - 1] == '"')
		{
			int closeIdx = t.Length - 1;
			int openIdx = -1;

			for (int i = closeIdx - 1; i >= 0; i--)
			{
				if (t[i] == '"')
				{
					int slashes = 0;
					for (int j = i - 1; j >= 0 && t[j] == '\\'; j--)
						slashes++;
					if (slashes % 2 == 0)
					{ openIdx = i; break; }
				}
			}

			if (openIdx >= 0)
			{
				string stripped = t.Substring(0, openIdx).TrimEnd();
				if (stripped.Length > 0 && stripped[stripped.Length - 1] == ',')
					stripped = stripped.Substring(0, stripped.Length - 1);
				result = stripped;
			}
		}

		return result;
	}

	// ─── Schema-based fixups ───────────────────────────────────────────────────

	// Stage 4: coerce string-typed values to their schema-declared type where safe and unambiguous.
	private static void ApplyTypeCoercions(JsonObject obj, JsonObject? parameters, Action<string>? log)
	{
		JsonObject? props = parameters?["properties"]?.AsObject();
		if (props == null)
			return;

		foreach ((string key, JsonNode? schema) in props)
		{
			string? schemaType = schema?["type"]?.GetValue<string>();
			if (schemaType == null || !obj.ContainsKey(key))
				continue;

			JsonNode? value = obj[key];
			if (value == null)
				continue;

			bool coerced = false;

			switch (schemaType)
			{
				case "integer":
					if (value is JsonValue intJv && intJv.TryGetValue(out string? intStr) &&
						int.TryParse(intStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
					{
						obj[key] = JsonValue.Create(intVal);
						coerced = true;
					}
					break;

				case "number":
					if (value is JsonValue numJv && numJv.TryGetValue(out string? numStr) &&
						double.TryParse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double dblVal))
					{
						obj[key] = JsonValue.Create(dblVal);
						coerced = true;
					}
					break;

				case "boolean":
					if (value is JsonValue boolJv)
					{
						bool? looseBool = ParseLooseBool(boolJv.ToString());
						if (looseBool != null)
						{
							obj[key] = JsonValue.Create(looseBool.Value);
							coerced = true;
						}
					}
					break;

				case "string":
					if (value is JsonValue strJv && !strJv.TryGetValue(out string? _))
					{
						obj[key] = JsonValue.Create(value.ToString());
						coerced = true;
					}
					break;
			}

			if (coerced)
			{
				Interlocked.Increment(ref _typeCoercions);
				log?.Invoke($"json-heal [type] '{key}' → {schemaType}: {GetCounters()}");
			}
		}
	}

	// Maps the common loose boolean spellings a model emits onto a real bool; null when it is not a
	// recognizable boolean (so the caller surfaces it rather than guessing). Handles JSON strings ("yes"),
	// the JSON literals true/false, and 1/0 whether sent as a number or a string.
	private static bool? ParseLooseBool(string raw)
	{
		string t = raw.Trim().ToLowerInvariant();
		if (t == "true" || t == "yes" || t == "y" || t == "1" || t == "on")
			return true;
		if (t == "false" || t == "no" || t == "n" || t == "0" || t == "off")
			return false;
		return null;
	}

	// Reconciles a misnamed argument key onto the schema property it most likely meant: a unique
	// case-insensitive match first, then a unique near-miss within a small edit distance. The key is renamed
	// to the canonical name so a casing/underscore slip (oldText, File_Path) is kept and satisfies the
	// required-args check instead of being stripped as extra. Only an unambiguous, not-already-present target
	// is taken — when the choice is uncertain the key is left alone (and later stripped) rather than mapped
	// wrong. This is the per-argument analogue of the tool-name fuzzy correction.
	private static void ReconcileArgNames(JsonObject obj, JsonObject? parameters, Action<string>? log)
	{
		JsonObject? props = parameters?["properties"]?.AsObject();
		if (props == null)
			return;

		List<string> schemaNames = new List<string>();
		foreach ((string name, JsonNode? _) in props)
			schemaNames.Add(name);

		// Canonical names already present (exact) are off-limits as rename targets.
		HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
		foreach ((string key, JsonNode? _) in obj)
		{
			if (props.ContainsKey(key))
				taken.Add(key);
		}

		// Collect renames first; the obj cannot be mutated while it is being enumerated.
		List<(string from, string to)> renames = new List<(string from, string to)>();
		foreach ((string key, JsonNode? _) in obj)
		{
			if (props.ContainsKey(key))
				continue;

			string? canonical = BestArgNameMatch(key, schemaNames, taken);
			if (canonical != null)
			{
				renames.Add((key, canonical));
				taken.Add(canonical);
			}
		}

		foreach ((string from, string to) in renames)
		{
			JsonNode? value = obj[from];
			obj.Remove(from);
			if (value != null)
				obj[to] = value.DeepClone();
			Interlocked.Increment(ref _argNameCorrections);
			log?.Invoke($"json-heal [arg] '{from}' → '{to}': {GetCounters()}");
		}
	}

	// Best schema property for a misnamed key: a unique case-insensitive match, else a unique fuzzy match
	// within a small edit distance (scaled to the name's length, capped at 3). Skips already-taken targets.
	// Returns null when there is no confident, unambiguous choice.
	private static string? BestArgNameMatch(string key, List<string> schemaNames, HashSet<string> taken)
	{
		string? caseMatch = null;
		int caseCount = 0;
		foreach (string name in schemaNames)
		{
			if (taken.Contains(name))
				continue;
			if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
			{
				caseMatch = name;
				caseCount++;
			}
		}
		if (caseCount == 1)
			return caseMatch;

		string lowerKey = key.ToLowerInvariant();
		string? best = null;
		int bestDist = int.MaxValue;
		int ties = 0;
		foreach (string name in schemaNames)
		{
			if (taken.Contains(name))
				continue;
			int threshold = Math.Max(1, Math.Min(3, name.Length / 3));
			int dist = LevenshteinDistance(lowerKey, name.ToLowerInvariant());
			if (dist > threshold)
				continue;
			if (dist < bestDist)
			{
				bestDist = dist;
				best = name;
				ties = 1;
			}
			else if (dist == bestDist)
			{
				ties++;
			}
		}

		return ties == 1 ? best : null;
	}

	// Stage 6: remove any keys not declared in the schema's properties.
	private static void StripExtraArgs(JsonObject obj, JsonObject? parameters, Action<string>? log)
	{
		JsonObject? props = parameters?["properties"]?.AsObject();
		if (props == null)
			return;

		List<string> toRemove = new List<string>();
		foreach ((string key, JsonNode? _) in obj)
		{
			if (!props.ContainsKey(key))
				toRemove.Add(key);
		}

		foreach (string key in toRemove)
		{
			obj.Remove(key);
			Interlocked.Increment(ref _extraArgsStripped);
			log?.Invoke($"json-heal [extra] stripped '{key}': {GetCounters()}");
		}
	}

	// Stage 5: returns an error message if any required argument is absent, null otherwise.
	private static string? CheckRequiredArgs(JsonObject obj, string toolName, JsonObject? parameters)
	{
		JsonArray? required = parameters?["required"]?.AsArray();
		if (required == null)
			return null;

		string? error = null;
		foreach (JsonNode? req in required)
		{
			if (req == null)
				continue;
			string fieldName = req.GetValue<string>();
			if (!string.IsNullOrEmpty(fieldName) && !obj.ContainsKey(fieldName))
			{
				error = $"Error: Tool '{toolName}' missing required argument '{fieldName}'";
				break;
			}
		}

		return error;
	}

	// ─── Levenshtein ──────────────────────────────────────────────────────────

	private static int LevenshteinDistance(string a, string b)
	{
		int m = a.Length;
		int n = b.Length;
		int[] prev = new int[n + 1];
		int[] curr = new int[n + 1];

		for (int j = 0; j <= n; j++)
			prev[j] = j;

		for (int i = 1; i <= m; i++)
		{
			curr[0] = i;
			for (int j = 1; j <= n; j++)
			{
				int cost = a[i - 1] == b[j - 1] ? 0 : 1;
				curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
			}
			int[] tmp = prev;
			prev = curr;
			curr = tmp;
		}

		return prev[n];
	}
}