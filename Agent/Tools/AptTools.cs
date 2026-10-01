using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Backs apt_install: installs Ubuntu packages for the model, which cannot run apt-get itself. The
// model's shell runs as the unprivileged sandbox user, and handing it apt-get (or sudo apt-get) would
// hand it root — an apt option or a local .deb's install script runs anything. So the agent runs the
// install as root on the model's behalf, and the only input taken is a list of package names checked
// against Debian's naming rules: no options, no paths, no version or release pins, so every package
// comes from the image's configured repositories. What is installed lives in the container and goes
// away with it.
public static class AptTools
{
	private const int MaxPackages    = 20;
	private const int TimeoutSeconds = 900;
	private const int MaxOutputLines = 40;

	// dpkg allows one installer at a time; parallel sessions queue here instead of failing on its lock.
	private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

	// The image ships package lists, but they age; the first install of a container refreshes them.
	private static bool _listsRefreshed;

	public static async Task<ToolResult> InstallAsync(string toolCallId, string packages, CancellationToken ct)
	{
		(List<string> names, string invalid) = Parse(packages);

		ToolResult result;
		if (invalid.Length > 0)
		{
			result = new ToolResult(toolCallId, string.Empty, $"Error: not a valid package name: {invalid}. Give plain Debian package names only (lowercase letters, digits, and + - .), no options, paths, or version pins.", 1, 0);
		}
		else if (names.Count == 0)
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: name at least one package to install.", 1, 0);
		}
		else if (names.Count > MaxPackages)
		{
			result = new ToolResult(toolCallId, string.Empty, $"Error: at most {MaxPackages} packages per call.", 1, 0);
		}
		else
		{
			await _gate.WaitAsync(ct);
			try
			{
				string     update  = _listsRefreshed ? string.Empty : "apt-get update -qq || exit 1\n";
				string     install = $"apt-get install -y --no-install-recommends {string.Join(' ', names)}\n";
				ToolResult run     = await ShellTools.AgentBashAsync(toolCallId, update + install, TimeoutSeconds, ct);
				if (run.ExitCode == 0)
					_listsRefreshed = true;
				result = new ToolResult(toolCallId, Tail(run.StdOut), Tail(run.StdErr), run.ExitCode, 0);
			}
			finally
			{
				_gate.Release();
			}
		}
		return result;
	}

	// Splits on whitespace and commas. A name must start with a lowercase letter or digit and hold only
	// lowercase letters, digits, '+', '-' and '.', which is Debian policy — and which also rules out
	// anything apt-get would read as an option, a path, a pin (=version, /release) or an architecture.
	private static (List<string> Names, string Invalid) Parse(string packages)
	{
		List<string> names   = new List<string>();
		string       invalid = string.Empty;
		foreach (string raw in packages.Split(new char[] { ' ', '\t', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries))
		{
			bool valid = char.IsAsciiLetterLower(raw[0]) || char.IsAsciiDigit(raw[0]);
			foreach (char c in raw)
			{
				if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '+' && c != '-' && c != '.')
					valid = false;
			}

			if (valid && !names.Contains(raw))
				names.Add(raw);
			else if (!valid && invalid.Length == 0)
				invalid = raw;
		}
		return (names, invalid);
	}

	// The end of apt's output is where the verdict is; the download chatter before it is noise.
	private static string Tail(string text)
	{
		string[] lines = text.Replace("\r\n", "\n").Split('\n');
		string   tail  = text;
		if (lines.Length > MaxOutputLines)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append($"[...{lines.Length - MaxOutputLines} earlier lines omitted]\n");
			for (int i = lines.Length - MaxOutputLines; i < lines.Length; i++)
				sb.Append(lines[i]).Append('\n');
			tail = sb.ToString().TrimEnd();
		}
		return tail;
	}
}