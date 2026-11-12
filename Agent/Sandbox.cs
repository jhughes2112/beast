using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Keeps what the model can reach apart from what only the agent may touch.
//
// Two users. The agent is root: it can see and change everything — the API keys in /root/.beast, its
// own state (conversations, logs, staged attachments) in /root/.beast-state, the git repository, the
// main checkout — and it alone commits (commit_and_rebase).
// Everything the MODEL can cause to run — bash, readonly_bash, ls — runs as ShellUser, a normal user
// that owns the workspace's working files and nothing else:
//   - every top-level dot-folder (.git, .github, .vscode, .venv, ...) is root's and read-only to it,
//     and one it creates is handed to root as soon as the command that made it finishes, so there is
//     no corner of .git (a hook, config, a planted commondir) or editor/CI config it can write — the
//     things the host's git and VS Code execute without asking;
//   - its git is read-only (status, diff, log); the agent records its work;
//   - the workspace folder is sticky, the same rule /tmp uses: it may create files there, but only
//     delete or rename its own, so it cannot move a root-owned folder aside and put its own in place.
//
// The in-process file tools cannot lean on permissions — they run inside the root agent — so they
// resolve every path through its symlinks and are confined to the workspace and the temp folder,
// with the agent's state and a workspace .beast off limits (save reading staged attachments) and
// nothing inside a top-level dot-folder writable.
//
// Where there is no ShellUser (a native debug run, the test harness on Windows) processes run as
// whoever started the agent and the lockdown is skipped; the path confinement always applies.
public static class Sandbox
{
	public const string ShellUser = "beast";
	public const string ShellHome = "/home/beast";

	private static string _root = string.Empty;
	private static bool   _shellUserActive;
	private static uint   _uid;
	private static uint   _gid;

	// Repeatable time for seeded runs. A listing or a script that shows the real time hands every run
	// different text, so a seeded run could never repeat. Time is counted instead: a fixed date plus one
	// minute per tool call. clockfs (Agent/Docker/clockfs.c), mounted over the workspace and the temp
	// folder at the first seeded round, gives every change there the counted time; libfaketime gives it
	// to whatever the model's shell asks the clock.
	//
	// The date is kFileClockBase, a constant: one taken from the calendar moved every few weeks, and a run
	// started after the move showed the model different dates from the run it was meant to repeat. Changing
	// it starts a new series; seeded runs only repeat runs made with the same value. Once the real date
	// passes it, files from outside the sandbox can be newer than those made inside, and a certificate
	// issued after it reads as not yet valid to the shell; move it forward between series if that bites.
	public const  long   kFileClockBase   = 1795392000; // Monday 23 November 2026, 00:00 UTC
	private const string kFileClockPath   = "/run/beast-fileclock";
	private const string kClockFsPath     = "/opt/agent-bins/clockfs";
	private const string kFakeTimeLibrary = "/opt/agent-bins/libfaketime.so.1";
	private const string kUnfakedBinDir   = "/opt/agent-bins/unfaked";
	private const string kFuseDevice      = "/dev/fuse";

	// The clock's date and its tool call count, saved so a resumed session's time carries on.
	public const string kClockBaseCounter    = "clock_base";
	public const string kClockMinutesCounter = "clock_minutes";

	private static readonly System.Text.RegularExpressions.Regex kHttpDateLine = new System.Text.RegularExpressions.Regex(
		@"^(<?[ \t]*date:[ \t]*)[A-Z]{3}, \d{2} [A-Z]{3} \d{4} \d{2}:\d{2}:\d{2} GMT",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	// One HTTP response's status line and headers, as curl -i, -I, -D or -v prints them. Headers are only touched
	// inside one of these, so a line such as "age: 30" in a script's own output stays.
	private static readonly System.Text.RegularExpressions.Regex kHttpHeaderBlock = new System.Text.RegularExpressions.Regex(
		@"^<?[ \t]*HTTP/\d(?:\.\d)?[ \t]+\d{3}[^\n]*\n(?:<?[ \t]*[A-Za-z0-9-]+:[^\n]*(?:\n|\z))*",
		System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	// Headers that report the request rather than the resource: how long a cache has held it, which server or
	// trace answered, and when the cached copy runs out.
	private static readonly System.Text.RegularExpressions.Regex kVolatileHeader = new System.Text.RegularExpressions.Regex(
		@"^<?[ \t]*(?:age|expires|cf-ray|cf-cache-status|request-id|traceparent|server-timing|report-to|nel|set-cookie|x-cache|x-cache-hits|x-served-by|x-timer|x-request-id|x-amz-request-id|x-amz-id-2|x-amz-cf-id|x-amz-cf-pop|x-fastly-request-id|x-github-request-id|x-vercel-id|x-vercel-cache|x-nf-request-id|x-ms-request-id):[^\n]*(?:\n|\z)",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	// The start of an `ls -l` line, up to and including its date: mode, links, owner and maybe group, size
	// (bytes, -h's 1.2K, or a device's "10, 229"), then the date in --full-time, long-iso or default style.
	private static readonly System.Text.RegularExpressions.Regex kLsDate = new System.Text.RegularExpressions.Regex(
		@"^([-bcdlpsD][-rwxsStT]{9}[.+@]?[ \t]+\d+[ \t]+(?:\S+[ \t]+){1,2}(?:\d+,[ \t]*)?\d[\d.,]*[KMGTPE]?[ \t]+)" +
		@"(?:(?<full>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+ [+-]\d{4})|(?<iso>\d{4}-\d{2}-\d{2} \d{2}:\d{2})|(?<plain>[A-Z][a-z]{2} [ \d]\d (?: \d{4}|\d{2}:\d{2})))(?= )",
		System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	// The tag Chromium puts on each message from a graphics context: the context's address in memory.
	private static readonly System.Text.RegularExpressions.Regex kGraphicsContextTag = new System.Text.RegularExpressions.Regex(
		@"\[\.(WebGL|WebGPU)-0x[0-9a-fA-F]+\]",
		System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	// The semaphore and shared memory libfaketime names after the process that made them, as its own
	// warnings print them ("sem_name was /faketime_sem_63"): a process id, which every process started in
	// the container before (an earlier /clear'd conversation's included) moves on.
	private static readonly System.Text.RegularExpressions.Regex kFakeTimeName = new System.Text.RegularExpressions.Regex(
		@"faketime_(sem|shm)_\d+",
		System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	private static readonly SemaphoreSlim _fileClockGate = new SemaphoreSlim(1, 1);
	private static          long          _fileClockBase;
	private static          long          _fileClockMinutes;
	private static          bool          _fileClockFailed;
	private static volatile string?       _fakeTime;

	public static bool ShellUserActive => _shellUserActive;

	// Whether this container can mount clockfs at all; one created by an older Beast has no fuse device.
	public static bool FileClockAvailable => _shellUserActive && File.Exists(kClockFsPath) && File.Exists(kFuseDevice);

	// The counted time now, in unix seconds; 0 until the first seeded round.
	public static long FileClockNow => _fileClockBase + _fileClockMinutes * 60;

	// Whether a seeded run has started counting time in this container.
	public static bool CountingTime => _fakeTime != null;

	private static string Root => _root.Length > 0 ? _root : Environment.CurrentDirectory;

	// Called once at startup with the workspace root. The shell user is only used when the agent is
	// root on Linux and the image created the account.
	public static void Initialize(string workspaceRoot)
	{
		_root            = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
		_shellUserActive = false;
		if (OperatingSystem.IsLinux() && Environment.UserName == "root")
		{
			(bool found, uint uid, uint gid) = LookUpUser(ShellUser);
			_shellUserActive                 = found;
			_uid = uid;
			_gid = gid;
		}
	}

	// Applies the ownership and permission layout described above. Idempotent, and re-run on every
	// start rather than trusting what an earlier container left behind. Returns a one-line summary.
	public static async Task<string> LockDownAsync(CancellationToken ct)
	{
		string summary = "skipped (no shell user)";
		if (_shellUserActive)
		{
			ToolResult result = await ShellTools.AgentBashAsync("sandbox_lockdown", LockDownScript(Root), 600, ct);
			string     names  = Listed(result.StdOut, "LOCKED ");
			summary           = result.ExitCode == 0
				? $"workspace locked down; root-owned dot-folders: {(names.Length > 0 ? names : "none")}"
				: $"lockdown FAILED (exit {result.ExitCode}): {result.StdErr}";
		}
		return summary;
	}

	// Hands any top-level dot-folder that is not root's to root, read-only — run after every command
	// the model's shell runs, since that is the only way one can appear. A dot-symlink the shell made
	// is removed instead: locking the link would not stop the host's editor following it into the
	// shell user's files. Only each entry's own owner is checked, so this costs one short root shell.
	// Returns a note for the model naming what was taken, or empty when nothing was.
	public static async Task<string> SealNewDotFoldersAsync(CancellationToken ct)
	{
		string note = string.Empty;
		if (_shellUserActive)
		{
			ToolResult result  = await ShellTools.AgentBashAsync("sandbox_seal", SealScript(Root), 120, ct);
			string     locked  = Listed(result.StdOut, "LOCKED ");
			string     removed = Listed(result.StdOut, "REMOVED ");
			if (locked.Length > 0)
				note = $"[sandbox: {locked} now belongs to the agent and is read-only for you — top-level folders whose names start with '.' always do. Use a name without the dot (e.g. venv/, not .venv/).]";
			if (removed.Length > 0)
				note = (note.Length > 0 ? note + "\n" : string.Empty) + $"[sandbox: the symlink {removed} was removed — top-level names starting with '.' belong to the agent.]";
		}
		return note;
	}

	// Counts a round of tool calls, one minute each; parallel calls share the time the round ends on, so
	// which of them finishes first cannot matter. The first round starts the clock: it stamps the workspace
	// folder and its parent, whose times come from when the container started and show up as the . and ..
	// lines of a listing, and everything in the temp folder, which only this container ever used; then it
	// mounts clockfs on both. The workspace's own files keep their times. Temp goes first in case the
	// workspace is ever inside it. Returns a message
	// the first time the clock cannot start, and empty otherwise: a failure only costs repeatability, so
	// it must be seen but never fails the tool call. No-op without a shell user.
	public static async Task<string> AdvanceFileClockAsync(int toolCalls, CancellationToken ct)
	{
		string failure = string.Empty;
		if (_shellUserActive && !_fileClockFailed)
		{
			await _fileClockGate.WaitAsync(ct);
			try
			{
				if (_fileClockBase == 0)
					failure = await StartFileClockAsync(ct);
				if (failure.Length == 0)
				{
					_fileClockMinutes += toolCalls;
					SessionService.SetCounter(kClockMinutesCounter, _fileClockMinutes);
					WriteFileClock(FileClockNow);
					_fakeTime = DateTimeOffset.FromUnixTimeSeconds(FileClockNow).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
				}
			}
			finally
			{
				_fileClockGate.Release();
			}
		}
		return failure;
	}

	private static async Task<string> StartFileClockAsync(CancellationToken ct)
	{
		string failure = string.Empty;
		if (!FileClockAvailable)
		{
			failure = $"This container cannot repeat file times: it has no {kFuseDevice} or {kClockFsPath}. Start a new session so it is created with them.";
		}
		else
		{
			// A resumed session carries on from its saved time, rather than starting over and running backwards.
			long   saved   = SessionService.GetCounter(kClockBaseCounter);
			long   start   = saved > 0 ? saved : kFileClockBase;
			long   minutes = saved > 0 ? SessionService.GetCounter(kClockMinutesCounter) : 0;
			long   stamp   = start + minutes * 60;
			string quoted  = Root.Replace("'", "'\\''");
			WriteFileClock(stamp);
			// The temp folder's files all take the counted time; the workspace's keep their own, even when the
			// workspace is inside the temp folder (a native run on a project under /tmp), so that branch is pruned.
			ToolResult result = await ShellTools.AgentBashAsync("sandbox_file_clock",
				$"W='{quoted}'\nT='{TempFolder()}'\n" +
				$"find \"$T\" -xdev -path \"$W\" -prune -o -exec touch -h -d '@{stamp}' {{}} + 2>/dev/null\n" +
				$"touch -h -d '@{stamp}' \"$W\" \"$(dirname \"$W\")\" && {kClockFsPath} \"$T\" {kFileClockPath} && {kClockFsPath} \"$W\" {kFileClockPath}\n", 120, ct);
			if (result.ExitCode != 0)
			{
				failure = $"Could not mount clockfs, so this run's file times are real and it will not repeat: {result.StdOut} {result.StdErr}".TrimEnd();
			}
			else
			{
				// This process's working folder is still the real one underneath. A path is resolved
				// afresh, so this is what puts its own relative paths on the mount too.
				_fileClockBase    = start;
				_fileClockMinutes = minutes;
				SessionService.SetCounter(kClockBaseCounter, start);
				Directory.SetCurrentDirectory(Root);
			}
		}
		_fileClockFailed = failure.Length > 0;
		return failure;
	}

	// While time is being counted, what a shell command most often prints that differs from run to run is
	// given a fixed form, rather than being removed from output the model may be parsing:
	//   - An HTTP Date header (curl -i, -I, or -v's "< date:"): a server reports its own clock, which no
	//     local clock can reach. It reads the counted time.
	//   - A CDN's or cache's per-request HTTP headers (age, cf-ray and the like) have no fixed form that means
	//     anything, so these alone are removed.
	//   - The date column of `ls -l`: files outside clockfs's mounts, and any the workspace already held,
	//     carry real times. Every line reads the counted time, and the files keep the times they have.
	//   - Chromium's graphics context tag: an address that memory layout randomization changes every run.
	//   - libfaketime's semaphore and shared memory names: a process id. They read faketime_sem and faketime_shm.
	public static string SteadyShellOutput(string text)
	{
		string result = text;
		if (_fakeTime != null && text.Length > 0)
		{
			System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
			DateTimeOffset                   now       = DateTimeOffset.FromUnixTimeSeconds(FileClockNow);

			string http = now.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'",       invariant);
			string full = now.ToString("yyyy-MM-dd HH:mm:ss'.000000000 +0000'", invariant);
			string iso  = now.ToString("yyyy-MM-dd HH:mm",                      invariant);
			string ls   = $"{now.ToString("MMM", invariant)} {now.Day,2} {now.ToString("HH:mm", invariant)}";

			result = kHttpDateLine.Replace(text, match => match.Groups[1].Value + http);
			result = kHttpHeaderBlock.Replace(result, match => kVolatileHeader.Replace(match.Value, string.Empty));
			result = kLsDate.Replace(result, match => match.Groups[1].Value + (match.Groups["full"].Success ? full : match.Groups["iso"].Success ? iso : ls));
			result = kGraphicsContextTag.Replace(result, match => $"[.{match.Groups[1].Value}-0x0]");
			result = kFakeTimeName.Replace(result, match => $"faketime_{match.Groups[1].Value}");
		}
		return result;
	}

	private static string TempFolder()
	{
		return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath()).Replace("'", "'\\''");
	}

	// In place and never truncated: clockfs reads this on every change, and an empty read means real time.
	private static void WriteFileClock(long epoch)
	{
		byte[] text             = Encoding.ASCII.GetBytes(epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
		using FileStream stream = new FileStream(kFileClockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
		stream.Write(text, 0, text.Length);
	}

	// Takes clockfs back off the workspace. The caller must have left the workspace first. For tests:
	// a running agent keeps the mount until its container stops.
	public static async Task StopFileClockAsync(CancellationToken ct)
	{
		await ShellTools.AgentBashAsync("sandbox_file_clock", $"umount '{Root.Replace("'", "'\\''")}'; umount -l '{TempFolder()}'", 60, ct);
		_fileClockBase    = 0;
		_fileClockMinutes = 0;
		SessionService.SetCounter(   kClockBaseCounter, 0);
		SessionService.SetCounter(kClockMinutesCounter, 0);
		_fileClockFailed = false;
		_fakeTime        = null;
	}

	// The names on lines of a script's output that start with marker, comma-separated.
	private static string Listed(string output, string marker)
	{
		List<string> names = new List<string>();
		foreach (string line in output.Split('\n'))
		{
			if (line.StartsWith(marker, StringComparison.Ordinal))
				names.Add(line.Substring(marker.Length).Trim());
		}
		return string.Join(", ", names);
	}

	// Resolves a tool's path argument to the real file it names, following every symlink, and checks
	// it against the confinement rules. On success Path is what the caller must use for the I/O — never
	// the original argument, which a symlink swapped in afterwards could redirect. The agent's state
	// folder is off limits, and so is a .beast folder in the workspace (a project's own roles.json);
	// allowAttachments opens one reading hole: the staged attachments the model is told it can
	// inspect again, which live in the state folder (outside the workspace, in the container). The other
	// is always open: the compaction logs, where a compacted conversation is told its old tool calls are.
	public static (string? Path, string Error) Confine(string path, bool write, bool allowAttachments)
	{
		string? resolved = null;
		string  error    = string.Empty;

		string real       = ResolveLinks(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(Environment.CurrentDirectory, path));
		string root       = Root;
		string state      = ResolveLinks(System.IO.Path.GetFullPath(AgentState.Root));
		string staged     = ResolveLinks(System.IO.Path.GetFullPath(MediaIntake.StagingFolder()));
		string compaction = ResolveLinks(System.IO.Path.GetFullPath(AgentState.CompactionLogsDir));
		string beast      = System.IO.Path.Combine(root, ".beast");
		string temp       = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath());

		if (allowAttachments && !write && IsUnder(real, staged))
		{
			resolved = real;
		}
		else if (!write && IsUnder(real, compaction))
		{
			resolved = real;
		}
		else if (IsUnder(real, state) || IsUnder(real, beast))
		{
			error = $"Error: {path} is in the agent's own state (.beast), which is off limits.";
		}
		else if (!IsUnder(real, root) && !IsUnder(real, temp))
		{
			error = $"Error: {path} is outside the workspace ({root}). File tools only work inside the workspace and {temp}.";
		}
		else if (write && IsUnder(real, root) && IsLockedForWrite(real, root))
		{
			error = $"Error: {path} is in a protected folder that is read-only for you: top-level folders whose names start with '.' belong to the agent. Changes there must be made by a human.";
		}
		else
		{
			resolved = real;
		}
		return (resolved, error);
	}

	// Hands a file or folder the agent just created to the shell user, so the model's shell can edit
	// or delete what write_file made. No-op without a shell user.
	public static void GiveToShellUser(string path)
	{
		if (_shellUserActive)
			chown(Encoding.UTF8.GetBytes(path + "\0"), _uid, _gid);
	}

	// Adds the shell user's identity to a process start: setpriv drops to it before bash runs, and
	// HOME, USER and PATH are the ones its tools expect (pip --user, npm's global prefix, cargo, go).
	// No-op without a shell user, so native runs launch bash directly.
	public static void RunAsShellUser(System.Diagnostics.ProcessStartInfo psi, bool restricted)
	{
		if (_shellUserActive)
		{
			string bash     = psi.FileName;
			string fakeTime = _fakeTime ?? string.Empty;
			psi.FileName    = "setpriv";
			psi.ArgumentList.Insert(0, bash);
			// Loaded after setpriv changes user, through env: libfaketime makes a semaphore named after
			// the process, and one made while still root could not be opened again as the shell user.
			if (fakeTime.Length > 0)
			{
				psi.ArgumentList.Insert(0, $"LD_PRELOAD={kFakeTimeLibrary}");
				psi.ArgumentList.Insert(0, "/usr/bin/env");
			}
			psi.ArgumentList.Insert(0, "--init-groups");
			// The container holds SYS_ADMIN only so the agent can mount clockfs. Dropped from the bounding
			// set, nothing the model runs can get it back, not even through a setuid program.
			psi.ArgumentList.Insert(0, "--bounding-set=-sys_admin");
			psi.ArgumentList.Insert(0, $"--regid={ShellUser}");
			psi.ArgumentList.Insert(0, $"--reuid={ShellUser}");
			psi.Environment["HOME"]    = ShellHome;
			psi.Environment["USER"]    = ShellUser;
			psi.Environment["LOGNAME"] = ShellUser;

			string? unfaked = fakeTime.Length > 0 ? kUnfakedBinDir + ":" : null;
			if (fakeTime.Length > 0)
			{
				// NO_FAKE_STAT leaves file times to clockfs: faked, every file reads as now and make never
				// rebuilds. FAKE_UTIME stops a plain `touch` turning "now" into the real time on its way there.
				psi.Environment["FAKETIME"]                     = fakeTime;
				psi.Environment["FAKETIME_DONT_FAKE_MONOTONIC"] = "1";
				psi.Environment["NO_FAKE_STAT"]                 = "1";
				psi.Environment["FAKE_UTIME"]                   = "1";
				psi.Environment["TZ"] = "UTC";
			}
			if (!restricted)
			{
				string current          = psi.Environment.TryGetValue("PATH", out string? p) && p != null ? p : "/usr/local/bin:/usr/bin:/bin";
				psi.Environment["PATH"] = $"{unfaked}{ShellHome}/.local/bin:{ShellHome}/.npm-global/bin:{ShellHome}/.dotnet/tools:{current}";
			}
		}
	}

	// Anything inside a top-level dot-folder, existing or not yet created, and the .git entry itself
	// (a worktree's pointer file). Top-level dot FILES (.gitignore, .editorconfig) stay writable.
	private static bool IsLockedForWrite(string real, string root)
	{
		string relative = System.IO.Path.GetRelativePath(root, real);
		int    slash    = relative.IndexOfAny(new char[] { '/', '\\' });
		string top      = slash >= 0 ? relative.Substring(0, slash) : relative;
		bool   inside   = slash >= 0 && top.StartsWith('.');
		return top == ".git" || inside;
	}

	private static bool IsUnder(string path, string folder)
	{
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		string           prefix     = folder.EndsWith(System.IO.Path.DirectorySeparatorChar) ? folder : folder + System.IO.Path.DirectorySeparatorChar;
		return string.Equals(path, folder, comparison) || path.StartsWith(prefix, comparison);
	}

	// The real path a path names: each existing component that is a symlink is replaced by its target
	// (relative targets against the folder holding the link), ".." steps up from the already-real
	// prefix, and components that do not exist yet (a file about to be written) are kept as given.
	// Link chains are followed up to a fixed depth, then the path is taken as it stands.
	private static string ResolveLinks(string path)
	{
		string       rootPart = System.IO.Path.GetPathRoot(path) ?? string.Empty;
		List<string> pending  = SplitParts(path.Substring(rootPart.Length));
		string       resolved = rootPart;
		int          hops     = 0;

		while (pending.Count > 0)
		{
			string name = pending[0];
			pending.RemoveAt(0);

			if (name == ".")
				continue;
			if (name == "..")
			{
				string? parent = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(resolved));
				resolved       = parent ?? resolved;
				continue;
			}

			string  next   = System.IO.Path.Combine(resolved, name);
			string? target = hops < 40 ? LinkTarget(next) : null;
			if (target != null)
			{
				hops++;
				List<string> rest = pending;
				if (System.IO.Path.IsPathRooted(target))
				{
					resolved = System.IO.Path.GetPathRoot(target) ?? rootPart;
					pending  = SplitParts(target.Substring(resolved.Length));
				}
				else
				{
					pending = SplitParts(target);
				}
				pending.AddRange(rest);
			}
			else
			{
				resolved = next;
			}
		}
		return resolved;
	}

	private static string? LinkTarget(string path)
	{
		string? target = null;
		try
		{
			FileInfo info = new FileInfo(path);
			if (info.Attributes != (FileAttributes)(-1) && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
				target = info.LinkTarget;
		}
		catch (Exception)
		{
		}
		return target;
	}

	private static List<string> SplitParts(string path)
	{
		List<string> parts = new List<string>();
		foreach (string part in path.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
			parts.Add(part);
		return parts;
	}

	// uid and gid from /etc/passwd; found is false when the account does not exist.
	private static (bool Found, uint Uid, uint Gid) LookUpUser(string name)
	{
		(bool, uint, uint) result = (false, 0, 0);
		try
		{
			foreach (string line in File.ReadAllLines("/etc/passwd"))
			{
				string[] fields = line.Split(':');
				if (fields.Length > 3 && fields[0] == name && uint.TryParse(fields[2], out uint uid) && uint.TryParse(fields[3], out uint gid))
					result = (true, uid, gid);
			}
		}
		catch (Exception)
		{
		}
		return result;
	}

	[DllImport("libc", SetLastError = true)]
	private static extern int chown(byte[] path, uint owner, uint group);

	// Shell functions both scripts share. give: hand a tree to the shell user. seal: make a tree root's,
	// readable but not writable by anyone else (root keeps its own write bit: on Docker Desktop's
	// bind mounts root cannot write a file it owns once that bit is gone). Each only touches entries
	// that are not already right, so a re-run over a big repository walks it but changes little.
	private static string SharedFunctions(string root)
	{
		string quoted = root.Replace("'", "'\\''");
		return
			"set -u\n" +
			$"W='{quoted}'\n" +
			$"U={ShellUser}\n" +
			"give() { find \"$1\" -xdev ! -user \"$U\" -exec chown -h \"$U:$U\" {} +; }\n" +
			"seal() { find \"$1\" -xdev ! -user root -exec chown -h root:root {} +; find \"$1\" -xdev ! -type l -perm /go=w -exec chmod go-w {} +; }\n";
	}

	// The lockdown, as root. In order:
	//   1. Every top-level entry of the workspace whose name starts with '.' is root's (sealed) — .git
	//      and every other dot-folder, with no exceptions — except a .beast folder, which is root's
	//      alone (a project's own roles.json; it is never created here, the agent's state lives in
	//      /root/.beast-state), and dot FILES (.gitignore, .editorconfig), which stay the shell user's
	//      like every other file. A dot-symlink is sealed as a link; one the shell makes later is
	//      removed (SealScript).
	//   2. The git dir is wholly root's. In a worktree that is the main repository's .git, found by
	//      reading the root-owned pointer file rather than by running git — root never runs git over
	//      metadata the shell might have touched. A commondir file in it is removed: git follows one
	//      to take its config from elsewhere, and only a linked worktree's own folder should have one.
	//   3. The main checkout a worktree integrates into is root's alone: its folder can only be passed
	//      through (to reach .git), and every other entry in it is 0700, so the shell user can neither
	//      read nor change it. Only the agent's commit_and_rebase touches it.
	//   4. The workspace folder is sticky and root's: the shell user creates freely but can only delete
	//      or rename what it owns, so the sealed entries above stay put.
	// Re-run after every commit_and_rebase, which writes the working tree as root.
	private static string LockDownScript(string root)
	{
		return
			SharedFunctions(root) +
			"for e in \"$W\"/* \"$W\"/.[!.]* \"$W\"/..?*; do\n" +
			"  [ -e \"$e\" ] || [ -L \"$e\" ] || continue\n" +
			"  n=${e##*/}\n" +
			"  case \"$n\" in\n" +
			"    .beast) if [ -d \"$e\" ] && [ ! -L \"$e\" ]; then chown root:root \"$e\"; chmod 700 \"$e\"; else seal \"$e\"; fi; echo \"LOCKED $n\" ;;\n" +
			"    .git) seal \"$e\"; echo \"LOCKED $n\" ;;\n" +
			"    .*) if [ -d \"$e\" ] || [ -L \"$e\" ]; then seal \"$e\"; echo \"LOCKED $n\"; else give \"$e\"; fi ;;\n" +
			"    *) give \"$e\" ;;\n" +
			"  esac\n" +
			"done\n" +
			"common=\"$W/.git\"\n" +
			"main=\"\"\n" +
			"if [ -f \"$W/.git\" ]; then\n" +
			"  wt=$(sed -n 's/^gitdir: //p' \"$W/.git\")\n" +
			"  case \"$wt\" in */.git/worktrees/*) common=${wt%/worktrees/*}; main=${common%/.git}; printf '../..\\n' > \"$wt/commondir\" ;; esac\n" +
			"fi\n" +
			"if [ -d \"$common\" ]; then rm -f \"$common/commondir\"; seal \"$common\"; fi\n" +
			"if [ -n \"$main\" ]; then\n" +
			"  chown root:root \"$main\"; chmod 711 \"$main\"\n" +
			"  for e in \"$main\"/* \"$main\"/.[!.]* \"$main\"/..?*; do\n" +
			"    [ -e \"$e\" ] || [ -L \"$e\" ] || continue\n" +
			"    [ \"${e##*/}\" = .git ] && continue\n" +
			"    chown -h root:root \"$e\"\n" +
			"    [ -L \"$e\" ] || chmod go-rwx \"$e\"\n" +
			"  done\n" +
			"fi\n" +
			"chown root:root \"$W\"; chmod 1777 \"$W\"\n" +
			// Docker hands the fuse device over world-writable; only the agent ever mounts anything.
			"[ -e /dev/fuse ] && chmod 600 /dev/fuse\n" +
			"exit 0\n";
	}

	// After a command of the model's shell: a top-level dot-folder it created is sealed, a dot-symlink
	// it created is removed. Root-owned entries are already sealed and the shell cannot write inside
	// them, so only each entry's own owner needs checking.
	private static string SealScript(string root)
	{
		return
			SharedFunctions(root) +
			"for e in \"$W\"/.[!.]* \"$W\"/..?*; do\n" +
			"  [ -e \"$e\" ] || [ -L \"$e\" ] || continue\n" +
			"  n=${e##*/}\n" +
			"  [ \"$(stat -c %u \"$e\")\" = 0 ] && continue\n" +
			"  if [ -L \"$e\" ]; then rm -f \"$e\"; echo \"REMOVED $n\"\n" +
			"  elif [ -d \"$e\" ]; then seal \"$e\"; echo \"LOCKED $n\"; fi\n" +
			"done\n" +
			"exit 0\n";
	}
}