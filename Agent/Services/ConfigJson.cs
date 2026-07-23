using System;

// ConfigException signals an unrecoverable config parse/load error after a friendly message has
// already been printed, so the top level can exit without dumping a call stack. The tolerant
// parse options for the hand-edited config files live in BeastJson.Config.
public class ConfigException : Exception
{
	public ConfigException(string message) : base(message)
	{
	}
}