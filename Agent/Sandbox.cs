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
// own state in .beast, the git repository, the main checkout — and it alone commits (commit_and_rebase).
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
// with .beast off limits and nothing inside a top-level dot-folder writable.
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

	public static bool ShellUserActive => _shellUserActive;

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
	// the original argument, which a symlink swapped in afterwards could redirect. allowAttachments
	// opens the one reading hole in .beast: dropped files the model is told it can inspect again.
	public static (string? Path, string Error) Confine(string path, bool write, bool allowAttachments)
	{
		string? resolved = null;
		string  error    = string.Empty;

		string real   = ResolveLinks(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(Environment.CurrentDirectory, path));
		string root   = Root;
		string beast  = System.IO.Path.Combine( root, ".beast");
		string staged = System.IO.Path.Combine(beast, "attachments");
		string temp   = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath());

		if (!IsUnder(real, root) && !IsUnder(real, temp))
		{
			error = $"Error: {path} is outside the workspace ({root}). File tools only work inside the workspace and {temp}.";
		}
		else if (IsUnder(real, beast) && !(allowAttachments && !write && IsUnder(real, staged)))
		{
			error = $"Error: {path} is inside .beast, which holds the agent's own state and is off limits.";
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
			string bash  = psi.FileName;
			psi.FileName = "setpriv";
			psi.ArgumentList.Insert(0,                   bash);
			psi.ArgumentList.Insert(0,        "--init-groups");
			psi.ArgumentList.Insert(0, $"--regid={ShellUser}");
			psi.ArgumentList.Insert(0, $"--reuid={ShellUser}");
			psi.Environment["HOME"]    = ShellHome;
			psi.Environment["USER"]    = ShellUser;
			psi.Environment["LOGNAME"] = ShellUser;
			if (!restricted)
			{
				string current          = psi.Environment.TryGetValue("PATH", out string? p) && p != null ? p : "/usr/local/bin:/usr/bin:/bin";
				psi.Environment["PATH"] = $"{ShellHome}/.local/bin:{ShellHome}/.npm-global/bin:{ShellHome}/.dotnet/tools:{current}";
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
	//      and every other dot-folder, with no exceptions — except .beast, which is root's alone, and
	//      dot FILES (.gitignore, .editorconfig), which stay the shell user's like every other file.
	//      A dot-symlink is sealed as a link; one the shell makes later is removed (SealScript).
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
			"    .beast) ;;\n" +
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
			"mkdir -p \"$W/.beast\"; chown root:root \"$W/.beast\"; chmod 700 \"$W/.beast\"\n" +
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