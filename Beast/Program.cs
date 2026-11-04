using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

// Beast CLI -- launches the Agent docker container and communicates with it over stdio.
public class Program
{
	// Held for the life of the process; see Main.
	private static PosixSignalRegistration? _ignoreSuspend;

	public static async Task<int> Main(string[] args)
	{
		// Beast is an app, not a shell job: Ctrl+Z never suspends it on macOS/Linux, where a stopped beast
		// would leave its agent waiting and its terminal modes half set. Windows has no such signal.
		if (!OperatingSystem.IsWindows())
			_ignoreSuspend = PosixSignalRegistration.Create(PosixSignal.SIGTSTP, context => context.Cancel = true);

		Console.Title         = "Beast";
		List<string> switches = new List<string>();
		string?      prompt   = null;

		// First pass: normalize all args into slash-prefixed switches and collect prompt.
		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];

			// Normalize to /name by stripping leading dashes/slashes.
			string normalized = "/" + arg.TrimStart('/', '-');

			if (normalized == "/p" || normalized == "/prompt")
			{
				List<string> parts = new List<string>();
				for (int j = i + 1; j < args.Length; j++)
					parts.Add(args[j]);
				if (parts.Count > 0)
					prompt = string.Join(" ", parts);
				break;
			}
			else if (arg.StartsWith("/") || arg.StartsWith("-"))
			{
				switches.Add(normalized);
			}
			else if (switches.Count > 0)
			{
				// Bare word following a switch: append as its value.
				switches[switches.Count - 1] = $"{switches[switches.Count - 1]} {arg}";
			}
		}

		// Second pass: check for switches that need special Beast-side handling.
		bool showHelp      = switches.Contains("/help") || switches.Contains("/h");
		bool runBeastTests = switches.Contains("/test");
		bool verbose       = switches.Contains("/verbose");

		// --worktree <name> selects (or creates) the worktree without showing the menu. Beast-only.
		string? worktreeArg = null;
		foreach (string sw in switches)
		{
			if (sw == "/worktree" || sw.StartsWith("/worktree ", StringComparison.Ordinal))
			{
				int sp      = sw.IndexOf(' ');
				worktreeArg = sp >= 0 ? sw.Substring(sp + 1).Trim() : null;
			}
		}

		// Agent receives all switches except Beast-only ones.
		List<string> agentSwitches = new List<string>();
		foreach (string sw in switches)
		{
			bool beastOnly = sw == "/help" || sw == "/h" || sw == "/verbose" || sw == "/debug"
				|| sw == "/worktree" || sw.StartsWith("/worktree ", StringComparison.Ordinal);
			if (!beastOnly)
				agentSwitches.Add(sw);
		}

		if (showHelp)
		{
			PrintHelp();
			return 0;
		}

		if (runBeastTests)
		{
			Console.WriteLine("=== Running Beast Tests ===");
			TestContext ctx = new TestContext(new TransportClientConsole());
			TransportTests.Test(ctx);
			CharWidthTests.Test(ctx);
			CharWidthTests.TestPathMarkers(ctx);
			HostCommandRunnerTests.Test(ctx);
			SessionTests.Test(ctx);
			MarkdownBlockTests.Test(ctx);
			ConfigOverlayTests.Test(ctx);
			Console.WriteLine($"=== Beast Tests: {ctx.Passed} passed, {ctx.Failed} failed ===");
			return ctx.Failed > 0 ? 1 : 0;
		}

		// Build the ordered message list: switches, then prompt, then /quit if non-interactive.
		bool         nonInteractive = prompt != null || agentSwitches.Count > 0;
		List<string> messages       = new List<string>(agentSwitches);
		if (prompt != null)
			messages.Add(prompt);
		if (nonInteractive)
			messages.Add("/quit");

		ClientLog    log         = new ClientLog(verbose);
		CollapseMode initialMode = verbose ? CollapseMode.Verbose : CollapseMode.Minimized;
		IDisplay     display     = nonInteractive ? new DisplayConsole(log, verbose) : new DisplayScreen(initialMode);

		// /debug — connect to an already-running agent on port 13131 (start Agent separately in VS).
		bool useDebug = switches.Contains("/debug");

		ILauncher      agentContext;
		string         agentName;
		SessionRecord? session = null;
		if (useDebug)
		{
			agentContext = new LaunchDebug();
			agentName    = "beast_debug";
		}
		else
		{
			string cwd = Directory.GetCurrentDirectory();

			try
			{
				// Preflight: construct SettingsService and RoleService here on the host, before the session
				// menu takes over the screen. The services handle config errors forgivingly (log warning
				// and use defaults), and create defaults if files don't exist. Any warnings about
				// malformed config files appear on the host terminal before the Docker container starts.
				_ = new SettingsService();
				_ = new RoleService(cwd);
			}
			catch (ConfigException)
			{
				return 1;
			}

			List<LaunchDocker.AgentContainer> agents = await LaunchDocker.ListAgentsAsync(AgentImage);
			(SessionRecord? chosen, string error)    = ResolveSession(cwd, worktreeArg, nonInteractive, agents);
			if (error.Length > 0)
			{
				LeaveMenuScreen();
				Console.Error.WriteLine(error);
				return 1;
			}
			if (chosen == null)
				return 0; // user left the session menu

			// The menu may have stopped or deleted containers; pair the session with its container as it is now.
			agents = await LaunchDocker.ListAgentsAsync(AgentImage);
			LaunchDocker.AgentContainer? existing = ContainerOf(chosen, agents);
			if (existing != null && existing.Running)
			{
				LeaveMenuScreen();
				Console.Error.WriteLine($"Session '{chosen.Describe()}' of {chosen.Folder} is busy: its agent (container {existing.Name}) is running for another Beast window. Close that window, or stop the agent from the session menu.");
				return 1;
			}
			SessionStore.Touch(chosen);
			session      = chosen;
			agentName    = existing != null ? existing.Name : chosen.ContainerName;
			agentContext = new LaunchDocker(AgentImage, log, chosen, existing);
		}

		await using BeastApp app = new BeastApp(agentContext, messages, display, log, agentName, session);
		return await app.Run();
	}

	private const string AgentImage = "beastagent";

	// The session menu leaves its screen up for the launch to take over; an error after it prints on the
	// normal screen instead. Harmless when the menu never showed.
	private static void LeaveMenuScreen()
	{
		Console.Write("\x1b[?7h\x1b[?1049l");
		Console.CursorVisible = true;
	}

	// Menu values for the rows that are not an existing session. The colons can never survive
	// SanitizeWorktreeName, so they cannot collide with a typed worktree name.
	private const string BareChoice    = "::bare::";
	private const string SessionPrefix = "::session::";

	// Resolves which session this launch runs. Sessions live in SessionStore.Root and can be resumed from
	// anywhere; new ones are made for the folder Beast runs in: its one bare session (the agent works on the
	// folder in place), or, inside a git repository, a worktree session (the agent works on its own checkout
	// of the repository's root, on a branch of the worktree's name). Returns the session, null when the user
	// left the menu, or an error to print.
	//   - --worktree none, and a non-interactive run without --worktree: this folder's bare session.
	//   - --worktree <name>: that worktree session of this folder's repository, made if it does not exist.
	//   - Otherwise the session menu, unless all it could offer is this folder's bare session.
	private static (SessionRecord? Session, string Error) ResolveSession(string cwd, string? nameArg, bool nonInteractive, List<LaunchDocker.AgentContainer> agents)
	{
		List<SessionRecord> sessions = SessionStore.List();
		string              repo     = HostGit.RepoRoot(cwd);
		SessionRecord?      bare     = SessionStore.FindBare(sessions, cwd);
		SessionRecord?      session  = null;
		string              error    = string.Empty;

		if (string.Equals(nameArg, "none", StringComparison.OrdinalIgnoreCase) || (nonInteractive && string.IsNullOrWhiteSpace(nameArg)) || (repo.Length == 0 && string.IsNullOrWhiteSpace(nameArg) && OnlyBareHere(sessions, bare, agents)))
		{
			(session, error) = BareSession(cwd, bare, agents);
		}
		else if (!string.IsNullOrWhiteSpace(nameArg))
		{
			if (repo.Length == 0)
				error = $"--worktree needs a git repository, and {cwd} is not in one.";
			else
				(session, error) = WorktreeSession(repo, nameArg, sessions, agents);
		}
		else
		{
			SelectMenu.Result result = ShowSessionMenu(cwd, repo, AgentImage);
			if (!result.Cancelled)
			{
				sessions = SessionStore.List();
				if (result.IsNew)
				{
					(session, error) = WorktreeSession(repo, result.Value, sessions, agents);
				}
				else if (result.Value == BareChoice)
				{
					(session, error) = BareSession(cwd, SessionStore.FindBare(sessions, cwd), agents);
				}
				else
				{
					string id = result.Value.Substring(SessionPrefix.Length);
					foreach (SessionRecord s in sessions)
					{
						if (s.Id == id)
							session = s;
					}
					if (session == null)
						error = "That session is gone.";
				}
			}
		}
		return (session, error);
	}

	// Whether this folder's bare session is all a menu could offer: no other session and no stray container.
	private static bool OnlyBareHere(List<SessionRecord> sessions, SessionRecord? bare, List<LaunchDocker.AgentContainer> agents)
	{
		return sessions.Count == (bare != null ? 1 : 0) && agents.Count == (bare != null && ContainerOf(bare, agents) != null ? 1 : 0);
	}

	// The folder's bare session, made when it has none (refused for a folder that must not be handed over).
	private static (SessionRecord? Session, string Error) BareSession(string folder, SessionRecord? bare, List<LaunchDocker.AgentContainer> agents)
	{
		SessionRecord? session = bare;
		string         error   = string.Empty;
		if (session == null)
		{
			error = UnsafeWorkspaceReason(folder);
			if (error.Length == 0)
				session = SessionStore.Create(folder, string.Empty, ContainerNames(agents));
		}
		return (session, error);
	}

	// The repository's worktree session of that name, made when it does not exist yet.
	private static (SessionRecord? Session, string Error) WorktreeSession(string repo, string name, List<SessionRecord> sessions, List<LaunchDocker.AgentContainer> agents)
	{
		string         safe    = SessionStore.SanitizeWorktreeName(name);
		SessionRecord? session = SessionStore.FindWorktree(sessions, repo, safe);
		string         error   = string.Empty;
		if (session == null)
		{
			error = UnsafeWorkspaceReason(repo);
			if (error.Length == 0)
				session = SessionStore.Create(repo, safe, ContainerNames(agents));
		}
		return (session, error);
	}

	private static HashSet<string> ContainerNames(List<LaunchDocker.AgentContainer> agents)
	{
		HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
		foreach (LaunchDocker.AgentContainer agent in agents)
			names.Add(agent.Name);
		return names;
	}

	// The session's agent container, found by its session label, or null.
	private static LaunchDocker.AgentContainer? ContainerOf(SessionRecord session, List<LaunchDocker.AgentContainer> agents)
	{
		LaunchDocker.AgentContainer? found = null;
		foreach (LaunchDocker.AgentContainer agent in agents)
		{
			if (found == null && agent.SessionId == session.Id)
				found = agent;
		}
		return found;
	}

	// A session's state for the menu: busy (its agent is running, so a Beast window should be using it) or
	// stopped (nobody is working on it; starting it starts its agent again, or makes a fresh one).
	private static string SessionNote(SessionRecord session, List<LaunchDocker.AgentContainer> agents)
	{
		LaunchDocker.AgentContainer? agent = ContainerOf(session, agents);
		return agent == null ? "stopped, no agent" : agent.Running ? "busy" : "stopped";
	}

	// The session menu. This folder's sessions come first (the repository's worktree sessions and the folder's
	// bare session) with rows to make new ones, then every other session, which can be resumed from here
	// too, then any agent container that belongs to no session. Enter starts a stopped session; S stops a
	// busy one's agent (the Beast window using it loses its connection); Del deletes, asking first, and for
	// a worktree says what unfinished work it holds.
	private static SelectMenu.Result ShowSessionMenu(string cwd, string repo, string image)
	{
		List<SessionRecord>               sessions = SessionStore.List();
		List<LaunchDocker.AgentContainer> agents   = LaunchDocker.ListAgentsAsync(image).GetAwaiter().GetResult();
		List<SelectMenu.Item>             items    = new List<SelectMenu.Item>();
		SessionRecord?                    bare     = SessionStore.FindBare(sessions, cwd);
		int initial = -1;

		items.Add(new SelectMenu.Item(repo.Length > 0 ? $"Here: {cwd}  (repository {repo})" : $"Here: {cwd}", string.Empty, string.Empty, true) { IsHeader = true });
		List<SessionRecord> elsewhere = new List<SessionRecord>();
		foreach (SessionRecord session in sessions)
		{
			bool here = session.IsBare ? SessionStore.SameFolder(session.Folder, cwd) : repo.Length > 0 && SessionStore.SameFolder(session.Folder, repo);
			if (here)
			{
				if (initial < 0)
					initial = items.Count;
				items.Add(SessionItem(session, session.IsBare ? "Bare session (this folder, no worktree)" : $"Worktree session: {session.Worktree}", agents));
			}
			else
			{
				elsewhere.Add(session);
			}
		}

		string repoRefusal = repo.Length > 0 ? UnsafeWhat(repo) : string.Empty;
		string cwdRefusal  = bare == null ? UnsafeWhat(cwd) : string.Empty;
		if (repo.Length > 0 && repoRefusal.Length == 0)
		{
			if (initial < 0)
				initial = items.Count;
			items.Add(new SelectMenu.Item("New worktree session", string.Empty, string.Empty, false) { IsCreate = true });
		}
		if (bare == null && cwdRefusal.Length == 0)
		{
			if (initial < 0)
				initial = items.Count;
			items.Add(new SelectMenu.Item("＋ Bare session in this folder", BareChoice, string.Empty, false));
		}
		if (repoRefusal.Length > 0 || cwdRefusal.Length > 0)
			items.Add(new SelectMenu.Item("  No new session here: " + (repoRefusal.Length > 0 ? $"the repository is {repoRefusal}" : $"this folder is {cwdRefusal}"), string.Empty, string.Empty, true) { IsHeader = true });

		if (elsewhere.Count > 0)
		{
			items.Add(new SelectMenu.Item(string.Empty, string.Empty, string.Empty, true) { IsHeader = true });
			items.Add(new SelectMenu.Item("Elsewhere", string.Empty, string.Empty, true) { IsHeader = true });
			foreach (SessionRecord session in elsewhere)
				items.Add(SessionItem(session, $"{session.Folder}  ·  {session.Describe()}", agents));
		}

		List<LaunchDocker.AgentContainer> strays = new List<LaunchDocker.AgentContainer>();
		foreach (LaunchDocker.AgentContainer agent in agents)
		{
			bool owned = false;
			foreach (SessionRecord session in sessions)
			{
				if (agent.SessionId == session.Id)
					owned = true;
			}
			if (!owned)
				strays.Add(agent);
		}
		if (strays.Count > 0)
		{
			items.Add(new SelectMenu.Item(string.Empty, string.Empty, string.Empty, true) { IsHeader = true });
			items.Add(new SelectMenu.Item("Agent containers that belong to no session", string.Empty, string.Empty, true) { IsHeader = true });
			foreach (LaunchDocker.AgentContainer agent in strays)
				items.Add(new SelectMenu.Item($"container {agent.Name}", string.Empty, agent.Running ? "running" : "stopped", true) { Tag = agent });
		}

		return SelectMenu.Choose("Beast — sessions", "↑/↓ select · Enter start · S stop a busy agent · Del delete · Esc quit",
			items, "work", Math.Max(0, initial), LaunchNotes.Pick(), (item, key) => OnMenuKey(item, key, image));
	}

	private static SelectMenu.Item SessionItem(SessionRecord session, string label, List<LaunchDocker.AgentContainer> agents)
	{
		return new SelectMenu.Item(label, SessionPrefix + session.Id, SessionNote(session, agents), false) { Tag = session };
	}

	// Enter, S and Del on a session menu row. The container list is read fresh for each, since another Beast
	// window may have started or stopped an agent while the menu was up.
	private static SelectMenu.KeyResult? OnMenuKey(SelectMenu.Item item, ConsoleKeyInfo key, string image)
	{
		SelectMenu.KeyResult? result = null;
		// Letters are matched by character: with VT input on, Windows Terminal sends them as char records with no
		// virtual-key code, so key.Key is never ConsoleKey.S there (Del still arrives as a decoded sequence).
		bool stop   = key.Key == ConsoleKey.S || key.KeyChar == 's' || key.KeyChar == 'S';
		bool delete = key.Key == ConsoleKey.Delete;
		if (key.Key == ConsoleKey.Enter || stop || delete)
		{
			List<LaunchDocker.AgentContainer> agents = LaunchDocker.ListAgentsAsync(image).GetAwaiter().GetResult();
			if (item.Tag is SessionRecord session)
			{
				LaunchDocker.AgentContainer? agent = ContainerOf(session, agents);
				item.Note = SessionNote(session, agents);
				if (key.Key == ConsoleKey.Enter && agent != null && agent.Running)
					result = new SelectMenu.KeyResult($"'{session.Describe()}' is busy: a Beast window is using it. Press S to stop its agent.", true, false);
				else if (stop)
					result = StopFromMenu(agent, session.Describe(), item);
				else if (delete)
					result = DeleteSessionFromMenu(session, agent);
			}
			else if (item.Tag is LaunchDocker.AgentContainer stray)
			{
				LaunchDocker.AgentContainer? now = null;
				foreach (LaunchDocker.AgentContainer agent in agents)
				{
					if (agent.Id == stray.Id)
						now = agent;
				}
				if (now == null)
					result = new SelectMenu.KeyResult("That container is already gone.", false, true);
				else if (stop)
					result = StopFromMenu(now, $"container {now.Name}", item);
				else if (delete && now.Running)
					result = new SelectMenu.KeyResult("It is running; press S to stop it first.", true, false);
				else if (delete)
					result = Outcome(LaunchDocker.RemoveStoppedAsync(now).GetAwaiter().GetResult(), $"Deleted container {now.Name}.", true);
				else
					result = new SelectMenu.KeyResult("This container belongs to no session; Del deletes it.", true, false);
			}
		}
		return result;
	}

	private static SelectMenu.KeyResult Outcome(string error, string done, bool remove)
	{
		return error.Length > 0 ? new SelectMenu.KeyResult(error, true, false) : new SelectMenu.KeyResult(done, false, remove);
	}

	// Stops a busy session's agent, after asking: the Beast window using it loses its connection.
	private static SelectMenu.KeyResult StopFromMenu(LaunchDocker.AgentContainer? agent, string what, SelectMenu.Item item)
	{
		SelectMenu.KeyResult result = new SelectMenu.KeyResult($"'{what}' is not running.", true, false);
		if (agent != null && agent.Running)
		{
			List<string> lines = new List<string>
			{
				$"Stop the agent of '{what}'?",
				string.Empty,
				"The Beast window using it loses its connection. Nothing is lost: the session can be started again."
			};
			result = new SelectMenu.KeyResult(string.Empty, false, false);
			if (SelectMenu.Ask("Stop agent", lines, new List<string> { "&Stop", "&Cancel" }, 1, 1) == 0)
			{
				SelectMenu.ShowStatus($"Stopping the agent of '{what}'…");
				string error = LaunchDocker.StopAgentAsync(agent).GetAwaiter().GetResult();
				result       = Outcome(error, $"Stopped the agent of '{what}'.", false);
				if (error.Length == 0)
					item.Note = "stopped";
			}
		}
		return result;
	}

	// Deletes a stopped session, asking how much: only its agent's container (the session stays, and its next
	// start makes a fresh agent), or everything, the worktree and its conversations included. For a worktree
	// the question says what unfinished work it holds, so nothing is thrown away blind.
	private static SelectMenu.KeyResult DeleteSessionFromMenu(SessionRecord session, LaunchDocker.AgentContainer? agent)
	{
		SelectMenu.KeyResult result = new SelectMenu.KeyResult(string.Empty, false, false);
		if (agent != null && agent.Running)
		{
			result = new SelectMenu.KeyResult($"'{session.Describe()}' is busy; press S to stop its agent first.", true, false);
		}
		else
		{
			List<string> lines = new List<string> { $"{session.Describe()} of {session.Folder}", string.Empty };
			if (session.IsBare)
			{
				lines.Add("The files in the folder are yours and are never touched.");
			}
			else
			{
				SelectMenu.ShowStatus("Reading the worktree's state…");
				lines.AddRange(HostGit.Status(session).Describe(session.Worktree));
			}
			lines.Add(string.Empty);
			if (agent != null)
				lines.Add("Agent only: delete the agent's container (what was installed in it). The session stays, and starting it makes a fresh agent.");
			lines.Add(session.IsBare
				? "Everything: also forget the session, its conversations and logs."
				: "Everything: also delete the worktree (uncommitted changes are lost), its conversations and logs, and its branch if merged. An unmerged branch is kept, so its commits are not lost.");

			List<string> labels    = agent != null ? new List<string> { "Delete &agent only", "Delete &everything", "&Cancel" } : new List<string> { "Delete &everything", "&Cancel" };
			int          cancel    = labels.Count - 1;
			int          choice    = SelectMenu.Ask("Delete session", lines, labels, cancel, cancel);
			bool         agentOnly = agent != null && choice == 0;
			bool         all       = choice == cancel - 1;
			if (agentOnly)
			{
				result = Outcome(LaunchDocker.RemoveStoppedAsync(agent!).GetAwaiter().GetResult(), $"Deleted the agent of '{session.Describe()}'; starting it makes a fresh one.", false);
			}
			else if (all)
			{
				string error = agent != null ? LaunchDocker.RemoveStoppedAsync(agent).GetAwaiter().GetResult() : string.Empty;
				string note  = string.Empty;
				if (error.Length == 0 && !session.IsBare)
					(note, error) = HostGit.RemoveWorktree(session);
				if (error.Length == 0)
					error = SessionStore.DeleteDir(session);
				result = Outcome(error, $"Deleted '{session.Describe()}' of {session.Folder}{(note.Length > 0 ? "; " + note : string.Empty)}.", true);
			}
		}
		return result;
	}

	private static void PrintHelp()
	{
		Console.WriteLine("beast - CLI host for the Agent docker container");
		Console.WriteLine();
		Console.WriteLine("Usage:");
		Console.WriteLine("  beast [/command1 /command2 ...] [-p <prompt text>]");
		Console.WriteLine();
		Console.WriteLine("Options:");
		Console.WriteLine("  <switch>          Any command switch forwarded to the agent container");
		Console.WriteLine("  -p <text>         Prompt text; everything after -p is treated as the prompt");
		Console.WriteLine("  --worktree <name> Start (or make) that worktree session of this repository, without the menu; none for the bare session");
		Console.WriteLine("  --test            Run Beast transport tests locally");
		Console.WriteLine("  --debug           Connect to an agent already running on port 13131 (for VS debugging).");
		Console.WriteLine("  --verbose         Show diagnostic debug output from the Agent");
		Console.WriteLine("  --help            Show this help");
	}

	// The folder Beast runs in is mounted into the agent's container as its workspace, read-write, and the
	// sandbox takes ownership of every file in it. That is never acceptable for the home folder (SSH keys,
	// browser profiles, cloud credentials, and ~/.beast itself), a folder above it, or a drive root.
	// Nor for Beast's own folder (~/.beast: the keys, and every session's worktree and conversations), or the
	// sessions folder when sessionsDir moves it elsewhere, or a folder that contains it.
	// Returns why this folder is refused, or empty when it is fine.
	private static string UnsafeWorkspaceReason(string cwd)
	{
		string what   = UnsafeWhat(cwd);
		string reason = string.Empty;
		if (what.Length > 0)
			reason = $"Beast will not make a session for {SessionStore.NormalizeFolder(cwd)}: it is {what}. That folder would be handed to the agent as its workspace to read and change, so it has to be a project folder that is fully yours. Run Beast from a project folder instead.";
		return reason;
	}

	// What makes the folder unfit to be a workspace ("your home folder"), or empty when it is fine.
	private static string UnsafeWhat(string cwd)
	{
		StringComparison cmp      = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		string           folder   = SessionStore.NormalizeFolder(cwd);
		string           home     = SessionStore.NormalizeFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
		string           beast    = Path.Combine(home, ".beast");
		string           sessions = SessionStore.NormalizeFolder(SessionStore.Root);
		string           root     = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(folder) ?? string.Empty);

		string what = string.Empty;
		if (string.Equals(folder, home, cmp))
			what = "your home folder";
		else if (home.StartsWith(folder + Path.DirectorySeparatorChar, cmp))
			what = "a folder that contains your home folder";
		else if (string.Equals(folder, beast, cmp) || folder.StartsWith(beast + Path.DirectorySeparatorChar, cmp))
			what = "inside Beast's own folder (~/.beast)";
		else if (string.Equals(folder, sessions, cmp) || folder.StartsWith(sessions + Path.DirectorySeparatorChar, cmp))
			what = "inside Beast's sessions folder (sessionsDir)";
		else if (sessions.StartsWith(folder + Path.DirectorySeparatorChar, cmp))
			what = "a folder that contains Beast's sessions folder (sessionsDir)";
		else if (string.Equals(folder, root, cmp) || folder.Length == 0)
			what = "the root of a drive";
		else if (InSystemFolder(folder, cmp))
			what = "part of the operating system or its installed programs";
		else if (!CanWrite(folder))
			what = "a folder you cannot write to";
		return what;
	}

	// Inside a folder that belongs to the operating system or installed programs. Checked by location as
	// well as by writing, because an elevated Beast could write to them.
	private static bool InSystemFolder(string folder, StringComparison cmp)
	{
		List<string> system = new List<string>();
		if (OperatingSystem.IsWindows())
		{
			system.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
			system.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
			system.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
			system.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
			system.Add(Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty);
		}
		else
		{
			system.AddRange(new string[] { "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/sbin", "/sys", "/usr", "/System", "/Library", "/Applications" });
		}

		bool inside = false;
		foreach (string path in system)
		{
			if (path.Length > 0)
			{
				string sys = Path.TrimEndingDirectorySeparator(path);
				if (string.Equals(folder, sys, cmp) || folder.StartsWith(sys + Path.DirectorySeparatorChar, cmp))
					inside = true;
			}
		}
		return inside;
	}

	// Whether this user can create files here: a probe file that deletes itself when closed.
	private static bool CanWrite(string folder)
	{
		bool writable = false;
		try
		{
			string probe = Path.Combine(folder, $".beast-write-check-{Guid.NewGuid():N}");
			using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
				writable = true;
		}
		catch (UnauthorizedAccessException)
		{
		}
		catch (IOException)
		{
		}
		return writable;
	}
}