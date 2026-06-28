using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


public static class GitTools
{
	// Commits the current worktree and integrates it with a strictly linear, rebase-based flow — never a merge
	// commit. It commits everything onto the feature branch B, finds the base branch A (the branch checked out in
	// the primary worktree, reached with git -C since B's worktree cannot check A out). B
	// is rebased onto A (local only, no remote operations), putting A's commits underneath B's recent ones,
	// and A is fast-forwarded onto B. B's worktree is never moved, so there is no switch back. The base is derived from the primary
	// worktree (git --git-common-dir), not guessed as "main" or read from origin/HEAD, so it is correct for any
	// base branch. The commit message is passed base64-encoded so arbitrary text
	// cannot break out of the script. A rebase conflict is left in place (not aborted) with the conflicted files
	// listed, so the Developer can resolve it directly and call again. Returns (ok, transcript): ok is false on
	// any git failure, with the transcript explaining why.
	//
	// Two non-worktree cases short-circuit to success before any rebase is attempted, so a project that is not
	// wired for the full flow still succeeds: (1) the folder is not a git repository at all (a /worktree none /
	// ephemeral run with no git), in which case nothing is done; (2) it is a git repo but there is no distinct
	// base branch (running directly on the base, not in a per-launch worktree), in which case the work is simply
	// committed in place. Only when a distinct base branch exists does the integration (rebase/ff) run.
	public static async Task<(bool ok, string transcript)> CommitAndRebaseAsync(string message, CancellationToken ct)
	{
		string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
		string script =
			"if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then echo 'Not a git repository; nothing to commit or integrate.'; exit 0; fi\n" +
			"branch=$(git rev-parse --abbrev-ref HEAD)\n" +
			"common=$(git rev-parse --git-common-dir)\n" +
			"main_wt=$(cd \"$common\" && cd .. && pwd)\n" +
			"base=$(git -C \"$main_wt\" rev-parse --abbrev-ref HEAD)\n" +
			"echo \"Committing work on '$branch'...\"\n" +
			"echo '" + encoded + "' | base64 -d > /tmp/beast_commit_msg\n" +
			"git add -A\n" +
			"git commit -F /tmp/beast_commit_msg || echo '(nothing to commit)'\n" +
			"if [ -z \"$base\" ] || [ \"$base\" = \"$branch\" ]; then echo \"Not running in a per-launch worktree (no distinct base branch); committed in place.\"; exit 0; fi\n" +
			"echo \"Integrating '$branch' onto local '$base'...\"\n" +
			"echo \"Rebasing '$branch' onto '$base' (linear history, no merge commit)...\"\n" +
			"if ! git rebase \"$base\"; then\n" +
			"  echo \"Rebase of '$branch' onto '$base' hit conflicts. Conflicted files:\"\n" +
			"  git diff --name-only --diff-filter=U\n" +
			"  echo \"Resolve each, 'git add' it, run 'git rebase --continue', then call commit_and_rebase again to fast-forward '$base'.\"\n" +
			"  exit 1\n" +
			"fi\n" +
			"echo \"Fast-forwarding base '$base' onto '$branch'...\"\n" +
			"git -C \"$main_wt\" merge --ff-only \"$branch\" || { echo \"Could not fast-forward '$base' (its worktree may have uncommitted changes).\"; exit 1; }\n";

		ToolResult result = await ShellTools.BashAsync("commit_and_rebase", script, null, ct);

		StringBuilder sb = new StringBuilder();
		if (!string.IsNullOrEmpty(result.StdOut))
			sb.Append(result.StdOut);
		if (!string.IsNullOrEmpty(result.StdErr))
		{
			if (sb.Length > 0)
				sb.Append('\n');
			sb.Append(result.StdErr);
		}

		bool ok = result.ExitCode == 0;
		if (!ok)
		{
			if (sb.Length > 0)
				sb.Append('\n');
			sb.Append($"[commit_and_rebase failed: git exited {result.ExitCode}]");
		}

		string transcript = sb.Length > 0 ? sb.ToString() : "[commit_and_rebase produced no output]";
		return (ok, transcript);
	}
}
