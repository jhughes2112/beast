using System;
using System.Collections.Generic;
using System.IO;


// HostGit against a real repository in a temporary folder, with worktrees laid out the way the agent's
// container leaves them: the checkout's .git pointer names /git/.git/worktrees/<n>, and the repository's
// record of it names /workspace/.git. Covers reading what a worktree holds and removing one, which is what
// the session menu's delete and Finish-after-a-lost-connection rely on.
public static class SessionTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  SessionTests");
		ctx.Assert(SessionStore.SanitizeWorktreeName(" fix login/bug ") == "fix-login-bug", "SanitizeWorktreeName: spaces and slashes collapse to '-'");
		ctx.Assert(                SessionStore.SanitizeWorktreeName("::bare::") == "bare", "SanitizeWorktreeName: menu values cannot survive it");
		BareSessions(ctx);

		string temp = Path.Combine(Path.GetTempPath(), $"beast-sessions-{Guid.NewGuid():N}");
		string repo = Path.Combine(temp, "repo");
		Directory.CreateDirectory(Path.Combine(repo, "sub"));
		try
		{
			if (!Git(repo, "init", "-q", "-b", "main").Ok)
			{
				ctx.Log("    (git is not installed; skipped)");
				return;
			}
			File.WriteAllText(          Path.Combine(repo, "a.txt"), "one\n");
			File.WriteAllText(Path.Combine(repo, "sub", "keep.txt"), "x\n");
			Git(repo, "add", "-A");
			Commit(repo, "init");

			ctx.Assert(SessionStore.SameFolder(HostGit.RepoRoot(Path.Combine(repo, "sub")), repo), "RepoRoot: a subfolder resolves to the repository's root");
			ctx.Assert(HostGit.RepoRoot(temp).Length == 0, "RepoRoot: a folder in no repository is empty");

			// A worktree with unfinished work: an edit, a new file, then a commit main lacks.
			SessionRecord          busy  = ContainerWorktree(temp, repo, "busy", "feat");
			HostGit.WorktreeStatus clean = HostGit.Status(busy);
			ctx.Assert(      clean.Readable && clean.Checkout && clean.Base == "main", $"Status: reads a container-made worktree ({clean.Error})");
			ctx.Assert(clean.Finishable && clean.Ahead == 0 && clean.Uncommitted == 0, "Status: a fresh worktree is finishable");

			File.WriteAllText(Path.Combine(busy.WorktreePath, "a.txt"), "two\n");
			File.WriteAllText(Path.Combine(busy.WorktreePath, "b.txt"), "new\n");
			File.Delete(Path.Combine(busy.WorktreePath, "sub", "keep.txt"));
			HostGit.WorktreeStatus dirty = HostGit.Status(busy);
			ctx.Assert(dirty.Modified == 1 && dirty.Added == 1 && dirty.Deleted == 1, $"Status: counts modified/new/deleted ({dirty.Modified}/{dirty.Added}/{dirty.Deleted})");
			ctx.Assert(!dirty.Finishable, "Status: uncommitted changes are not finishable");

			string admin = HostGit.AdminDir(busy);
			Git(null, "--git-dir=" + admin, "--work-tree=" + busy.WorktreePath, "add", "-A");
			Commit(null, "work", "--git-dir=" + admin, "--work-tree=" + busy.WorktreePath);
			HostGit.WorktreeStatus ahead = HostGit.Status(busy);
			ctx.Assert(ahead.Ahead == 1 && !ahead.Merged && ahead.Uncommitted == 0 && ahead.DiffStat.Contains("3 files changed"), $"Status: an unmerged commit ({ahead.Ahead}, {ahead.DiffStat})");
			ctx.Assert(!ahead.Finishable, "Status: an unmerged branch is not finishable");
			ctx.Assert(string.Join(" ", ahead.Describe("feat")).Contains("1 commit(s) not in 'main'"), "Describe: says how many commits main lacks");

			(string kept, string keptError) = HostGit.RemoveWorktree(busy);
			ctx.Assert(         keptError.Length == 0 && !Directory.Exists(busy.WorktreePath) && !Directory.Exists(admin), $"RemoveWorktree: checkout and registration gone ({keptError})");
			ctx.Assert(kept.Contains("kept branch") && Git(repo, "show-ref", "--verify", "--quiet", "refs/heads/feat").Ok, "RemoveWorktree: an unmerged branch is kept");
			ctx.Assert(                       !Git(repo, "worktree", "list", "--porcelain").StdOut.Contains("/workspace"), "RemoveWorktree: git no longer lists it");

			// A worktree whose branch is merged: removing it deletes the branch too.
			SessionRecord done                    = ContainerWorktree(temp, repo, "done", "merged");
			(string deleted, string deletedError) = HostGit.RemoveWorktree(done);
			ctx.Assert(deletedError.Length == 0 && deleted.Contains("deleted branch") && !Git(repo, "show-ref", "--verify", "--quiet", "refs/heads/merged").Ok, $"RemoveWorktree: a merged branch is deleted ({deleted}{deletedError})");

			// Never checked out (the agent never started): nothing to read in the checkout, nothing to remove but the folder.
			SessionRecord never = new SessionRecord("never", Path.Combine(temp, "never"), repo, "never", DateTime.UtcNow, DateTime.UtcNow);
			Directory.CreateDirectory(never.WorktreePath);
			HostGit.WorktreeStatus none = HostGit.Status(never);
			ctx.Assert(none.Readable && !none.Checkout && none.Finishable, "Status: a worktree never checked out is finishable");
			ctx.Assert(HostGit.RemoveWorktree(never).Error.Length == 0 && !Directory.Exists(never.WorktreePath), "RemoveWorktree: a never-checked-out worktree's folder goes");
		}
		finally
		{
			SessionStore.DeleteTree(temp);
		}
	}

	// A folder can have several bare sessions, told apart by name, and a record from before names still reads.
	private static void BareSessions(TestContext ctx)
	{
		string temp   = Path.Combine(Path.GetTempPath(), $"beast-bare-{Guid.NewGuid():N}");
		string folder = Path.Combine(              temp, "project");
		try
		{
			DateTime            now      = DateTime.UtcNow;
			SessionRecord       unnamed  = new SessionRecord("old",  Path.Combine(temp, "old"),  folder, string.Empty, now, now);
			SessionRecord       named    = new SessionRecord("two", Path.Combine(temp, "two"),                      folder, string.Empty, now, now, "session-1");
			SessionRecord       other    = new SessionRecord("far", Path.Combine(temp, "far"), Path.Combine(temp, "other"), string.Empty, now, now, "session-2");
			SessionRecord       worktree = new SessionRecord("tree", Path.Combine(temp, "tree"), folder, "session-2", now, now);
			List<SessionRecord> sessions = new List<SessionRecord> { named, unnamed, other, worktree };

			ctx.Assert(SessionStore.FindBare(sessions, folder) == named, "Bare sessions: without a name, the first listed, which is the most recently used");
			ctx.Assert(SessionStore.FindBareNamed(sessions, folder, "session-1") == named, "Bare sessions: found by name");
			ctx.Assert(SessionStore.FindBareNamed(sessions, folder, "session-2") == null, "Bare sessions: another folder's, or a worktree of that name, is not this folder's");
			ctx.AssertEqual("session-2", SessionStore.FreeBareName(sessions, folder), "Bare sessions: the suggested name is one this folder has not used");
			ctx.AssertEqual("session-1", SessionStore.FreeBareName(new List<SessionRecord> { unnamed }, folder), "Bare sessions: an unnamed session takes no name");
			ctx.AssertEqual("bare session session-1",   named.Describe(), "Bare sessions: a named one reads with its name");
			ctx.AssertEqual("bare session",           unnamed.Describe(), "Bare sessions: an unnamed one reads as before");

			SessionStore.Save(named);
			SessionRecord? read = SessionStore.Read(named.Dir);
			ctx.Assert(read != null && read.Name == "session-1" && read.Id == "two" && read.Dir == named.Dir && read.IsBare, "Bare sessions: the name is saved and read back");

			Directory.CreateDirectory(unnamed.Dir);
			File.WriteAllText(Path.Combine(unnamed.Dir, "session.json"), $"{{\"folder\":{System.Text.Json.JsonSerializer.Serialize(folder)},\"worktree\":\"\"}}");
			SessionRecord? before = SessionStore.Read(unnamed.Dir);
			ctx.Assert(before != null && before.Name.Length == 0 && before.IsBare, "Bare sessions: a record with no name still reads");
		}
		finally
		{
			SessionStore.DeleteTree(temp);
		}
	}

	// A worktree session whose checkout looks like the container made it.
	private static SessionRecord ContainerWorktree(string temp, string repo, string id, string branch)
	{
		SessionRecord session = new SessionRecord(id, Path.Combine(temp, id), repo, branch, DateTime.UtcNow, DateTime.UtcNow);
		Directory.CreateDirectory(session.Dir);
		Git(repo, "worktree", "add", "-q", session.WorktreePath, "-b", branch);
		string name = Path.GetFileName(session.WorktreePath);
		File.SetAttributes(Path.Combine(session.WorktreePath, ".git"), FileAttributes.Normal); // host git hides it on Windows
		File.WriteAllText(             Path.Combine(session.WorktreePath, ".git"), $"gitdir: /git/.git/worktrees/{name}\n");
		File.WriteAllText(Path.Combine(repo, ".git", "worktrees", name, "gitdir"), "/workspace/.git\n");
		return session;
	}

	private static HostGit.Result Git(string? workDir, params string[] args)
	{
		return HostGit.Run(workDir, new List<string>(args));
	}

	private static void Commit(string? workDir, string message, params string[] before)
	{
		List<string> args = new List<string>(before) { "-c", "user.name=Beast Test", "-c", "user.email=test@example.invalid", "commit", "-q", "-m", message };
		HostGit.Run(workDir, args);
	}
}