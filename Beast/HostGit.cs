using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;


// Git run on this machine, for what Beast decides about sessions without their agent: which repository a
// folder belongs to, how much unfinished work a worktree holds, and removing one. The worktree checkouts are
// made inside the agent's container, so their .git pointer names container paths (/git/.git/worktrees/...);
// host git reaches them by translating that pointer to the repository's real .git folder instead.
public static class HostGit
{
	public readonly struct Result
	{
		public readonly int    ExitCode;
		public readonly string StdOut;
		public readonly string StdErr;

		public Result(int exitCode, string stdOut, string stdErr)
		{
			ExitCode = exitCode;
			StdOut   = stdOut;
			StdErr   = stdErr;
		}

		public bool Ok => ExitCode == 0;
	}

	// What a worktree session holds that is not in the repository's checked-out branch, for deciding whether
	// it can be finished or is safe to delete. Readable is false (with Error) when git could not tell.
	public sealed class WorktreeStatus
	{
		public bool   Readable;
		public string Error = string.Empty;
		public bool   Checkout; // the worktree has been checked out at all
		public string Base = string.Empty;
		public int    Ahead;  // commits on the worktree's branch that Base does not have
		public bool   Merged; // the branch is wholly contained in Base
		public string DiffStat = string.Empty;
		public int    Modified;
		public int    Added;
		public int    Deleted;

		public int  Uncommitted => Modified + Added + Deleted;
		public bool Finishable  => Readable && Merged && Uncommitted == 0;

		// A few lines for a question about this worktree.
		public List<string> Describe(string branch)
		{
			List<string> lines = new List<string>();
			if (!Readable)
			{
				lines.Add($"Could not read the worktree's state: {Error}");
			}
			else
			{
				if (!Checkout)
					lines.Add("The worktree was never checked out.");
				lines.Add(Ahead == 0 ? $"Branch '{branch}' has no commits that '{Base}' lacks." : $"Branch '{branch}' has {Ahead} commit(s) not in '{Base}'{(DiffStat.Length > 0 ? ": " + DiffStat : string.Empty)}.");
				lines.Add(Uncommitted == 0 ? "No uncommitted changes." : $"Uncommitted: {Modified} modified, {Added} new, {Deleted} deleted file(s).");
			}
			return lines;
		}
	}

	// Runs git with these arguments. The user's global and system git config are left out so git here sees
	// a repository the way the agent's git in the container does (no autocrlf of the host's own, for one),
	// with safe.directory opened since the checkout was written by the container. Never throws: no git on
	// this machine reads as exit code -1.
	public static Result Run(string? workDir, List<string> args)
	{
		Result result = new Result(-1, string.Empty, "git is not installed on this machine");
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName               = "git",
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				RedirectStandardInput  = true,
				UseShellExecute        = false,
				CreateNoWindow         = true
			};
			if (workDir != null)
				psi.WorkingDirectory = workDir;
			psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
			psi.Environment["GIT_CONFIG_GLOBAL"]   = Path.Combine(Path.GetTempPath(), "beast-no-global-gitconfig");
			psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
			psi.ArgumentList.Add("-c");
			psi.ArgumentList.Add("safe.directory=*");
			psi.ArgumentList.Add("--no-optional-locks");
			foreach (string arg in args)
				psi.ArgumentList.Add(arg);

			using Process? proc = Process.Start(psi);
			if (proc != null)
			{
				proc.StandardInput.Close();
				Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
				Task<string> stderr = proc.StandardError.ReadToEndAsync();
				proc.WaitForExit();
				result = new Result(proc.ExitCode, stdout.Result.TrimEnd(), stderr.Result.Trim());
			}
		}
		catch (Win32Exception)
		{
		}
		return result;
	}

	// The root of the git repository folder is in, or empty when it is in none (or git is missing).
	public static string RepoRoot(string folder)
	{
		Result top  = Run(folder, new List<string> { "rev-parse", "--show-toplevel" });
		string root = string.Empty;
		if (top.Ok && top.StdOut.Length > 0)
			root = SessionStore.NormalizeFolder(top.StdOut);
		return root;
	}

	// The repository's administrative folder for this worktree (<repo>/.git/worktrees/<n>), read from the
	// worktree's .git pointer, which the container wrote as /git/.git/worktrees/<n>. Empty when the worktree
	// was never checked out or the pointer is not one of ours.
	public static string AdminDir(SessionRecord session)
	{
		string admin   = string.Empty;
		string pointer = Path.Combine(session.WorktreePath, ".git");
		try
		{
			if (File.Exists(pointer))
			{
				const string Prefix = "gitdir: /git/.git/worktrees/";
				string line = File.ReadAllText(pointer).Trim();
				if (line.StartsWith(Prefix, StringComparison.Ordinal))
				{
					string name = line.Substring(Prefix.Length).Trim();
					if (name.Length > 0 && name.IndexOfAny(new char[] { '/', '\\' }) < 0 && name != "." && name != "..")
						admin = Path.Combine(session.Folder, ".git", "worktrees", name);
				}
			}
		}
		catch (Exception)
		{
		}
		return admin;
	}

	// How much unfinished work the worktree session holds: commits its branch has that the repository's
	// checked-out branch lacks, and uncommitted changes in the checkout. "Finished" means what the agent's
	// /finish requires: no uncommitted changes and the branch wholly merged.
	public static WorktreeStatus Status(SessionRecord session)
	{
		WorktreeStatus status = new WorktreeStatus();
		string         repo   = session.Folder;
		string         branch = session.Worktree;

		Result head = Run(repo,                       new List<string> { "rev-parse", "--abbrev-ref", "HEAD" });
		Result has  = Run(repo, new List<string> { "show-ref", "--verify", "--quiet", "refs/heads/" + branch });
		if (!head.Ok)
		{
			status.Error = head.StdErr.Length > 0 ? head.StdErr : "git failed";
		}
		else
		{
			status.Readable = true;
			status.Base     = head.StdOut;
			status.Merged   = true;
			if (has.Ok)
			{
				Result ahead    = Run(repo,  new List<string> { "rev-list", "--count", status.Base + ".." + branch });
				Result ancestor = Run(repo,  new List<string> { "merge-base", "--is-ancestor", branch, status.Base });
				Result stat     = Run(repo, new List<string> { "diff", "--shortstat", status.Base + "..." + branch });
				int.TryParse(ahead.StdOut, out status.Ahead);
				status.Merged   = ancestor.Ok;
				status.DiffStat = stat.Ok ? stat.StdOut : string.Empty;
			}

			string admin = AdminDir(session);
			if (admin.Length > 0 && Directory.Exists(admin))
			{
				status.Checkout  = true;
				Result porcelain = Run(null, new List<string> { "--git-dir=" + admin, "--work-tree=" + session.WorktreePath, "status", "--porcelain" });
				if (!porcelain.Ok)
				{
					status.Readable = false;
					status.Error    = porcelain.StdErr.Length > 0 ? porcelain.StdErr : "git status failed";
				}
				else
				{
					foreach (string line in porcelain.StdOut.Split('\n'))
					{
						if (line.Length < 2)
							continue;
						string code = line.Substring(0, 2);
						if (code == "??" || code.Contains('A'))
							status.Added++;
						else if (code.Contains('D'))
							status.Deleted++;
						else
							status.Modified++;
					}
				}
			}
		}
		return status;
	}

	// Removes a worktree session's checkout from the repository: the checkout folder, the repository's
	// registration of it, and its branch when that is fully merged (an unmerged branch is kept, so its
	// commits survive). Returns what happened, for a status line; Error is set when something went wrong.
	public static (string Message, string Error) RemoveWorktree(SessionRecord session)
	{
		string admin   = AdminDir(session);
		string error   = SessionStore.DeleteTree(session.WorktreePath);
		string message = string.Empty;
		if (error.Length == 0 && admin.Length > 0)
			error = SessionStore.DeleteTree(admin);

		if (error.Length == 0)
		{
			Result exists = Run(session.Folder, new List<string> { "show-ref", "--verify", "--quiet", "refs/heads/" + session.Worktree });
			if (exists.Ok)
			{
				Result deleted = Run(session.Folder, new List<string> { "branch", "-d", session.Worktree });
				message        = deleted.Ok ? $"deleted branch '{session.Worktree}'" : $"kept branch '{session.Worktree}': it has commits that are not merged";
			}
		}
		return (message, error);
	}
}