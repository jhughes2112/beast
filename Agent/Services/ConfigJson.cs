using System;
using System.Text.Json;

// Shared JSON handling for the user-edited config files (settings.json, roles.json). These are
// hand-maintained, so the reader tolerates trailing commas and // and /* */ comments rather than
// failing on them. ConfigException signals an unrecoverable parse/load error after a friendly
// message has already been printed, so the top level can exit without dumping a call stack.
public static class ConfigJson
{
	public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
	{
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};
}

public class ConfigException : Exception
{
	public ConfigException(string message) : base(message)
	{
	}
}