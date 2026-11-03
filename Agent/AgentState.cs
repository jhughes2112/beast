using System;
using System.IO;


// Where the agent keeps its own state for a session: saved conversations, logs, and the attachments the
// client stages (see MediaIntake.StagingFolder). Beast passes a folder in the session's own directory
// under its sessions folder (the --state-dir startup argument, bound at /root/.beast-state, which the
// sandboxed shell cannot see), so nothing of the agent's is left in the user's project and finishing or
// deleting a session removes it in one go. A run without the argument (native or debugger) keeps it in
// the working folder's .beast.
public static class AgentState
{
	private static string _root = string.Empty;

	public static string Root => _root.Length > 0 ? _root : Path.Combine(Environment.CurrentDirectory, ".beast");

	public static string SessionsDir => Path.Combine(Root, "sessions");

	public static string LogsDir => Path.Combine(Root, "logs");

	public static void Initialize(string root)
	{
		_root = root;
	}
}