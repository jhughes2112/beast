using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


public static class GitTools
{
	// Serializes every commit/rebase/fast-forward flow in this process. Parallel Developers share
	// one repository: without this, one call's `git add -A` can sweep up another's in-progress
	// changes, or land mid-rebase and see a half-rewritten branch. Unique message files fixed the
	// message clobbering; this fixes the index and branch races.
	private static readonly SemaphoreSlim _gitGate = new SemaphoreSlim(1, 1);

	// Whether commit_and_rebase can work at all here, decided once at startup by ProbeAsync. False when
	// the workspace is not a git repository or git has no committer identity (the host passes it in as
	// GIT_AUTHOR_*/GIT_COMMITTER_* when it has one). ToolFactory then leaves the tool out entirely, so
	// the Developer is never told to commit where no commit can succeed.
	private static bool   _commitAvailable = false;
	private static string _commitDetail    = "commit_and_rebase has not been probed.";

	public static bool   CommitAvailable => _commitAvailable;
	public static string CommitDetail    => _commitDetail;

	// Runs the two preconditions as the agent (the repo is root's). `git var GIT_COMMITTER_IDENT` is git's
	// own identity resolution: it fails exactly when a commit would fail with "Author identity unknown".
	public static async Task ProbeAsync(CancellationToken ct)
	{
		string script =
			"if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then echo 'not a git repository'; exit 1; fi\n" +
			"if ! git var GIT_COMMITTER_IDENT >/dev/null 2>&1; then echo 'no git user.name/user.email (set them on the host and relaunch)'; exit 1; fi\n" +
			"git var GIT_COMMITTER_IDENT\n";
		ToolResult result = await ShellTools.AgentBashAsync("git_probe", script, null, ct);
		string     output = string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr.Trim() : result.StdOut.Trim();
		_commitAvailable  = result.ExitCode == 0;
		_commitDetail     = _commitAvailable ? $"commit_and_rebase available as {output}" : $"commit_and_rebase disabled: {output}";
	}

	// Commits the current worktree and integrates it with a strictly linear, rebase-based flow — never a merge
	// commit. It commits everything onto the feature branch B, finds the base branch A (the branch checked out in
	// the primary worktree, reached with git -C since B's worktree cannot check A out). B
	// is rebased onto A (local only, no remote operations), putting A's commits underneath B's recent ones,
	// and A is fast-forwarded onto B. B's worktree is never moved, so there is no switch back. The base is derived from the primary
	// worktree (git --git-common-dir), not guessed as "main" or read from origin/HEAD, so it is correct for any
	// base branch. The commit message is passed base64-encoded so arbitrary text
	// cannot break out of the script. A rebase conflict is left in place (not aborted) with the conflicted files
	// listed; the Developer resolves the files and calls again, and this call continues the rebase — the
	// model's own git is read-only (the repository is root's, see Sandbox), so it cannot run
	// `git rebase --continue` itself. Returns (ok, transcript): ok is false on any git failure, with the
	// transcript explaining why.
	//
	// Two non-worktree cases short-circuit to success before any rebase is attempted, so a project that is not
	// wired for the full flow still succeeds: (1) the folder is not a git repository at all (a /worktree none /
	// ephemeral run with no git), in which case nothing is done; (2) it is a git repo but there is no distinct
	// base branch (running directly on the base, not in a per-launch worktree), in which case the work is simply
	// committed in place. Only when a distinct base branch exists does the integration (rebase/ff) run.
	public static async Task<(bool ok, string transcript)> CommitAndRebaseAsync(string message, CancellationToken ct)
	{
		string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
		string script  =
			"if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then echo 'Not a git repository; nothing to commit or integrate.'; exit 0; fi\n" +
			"gitdir=$(git rev-parse --git-dir)\n" +
			"common=$(git rev-parse --git-common-dir)\n" +
			"main_wt=$(cd \"$common\" && cd .. && pwd)\n" +
			"base=$(git -C \"$main_wt\" rev-parse --abbrev-ref HEAD)\n" +
			// A rebase left stopped on a conflict by an earlier call: HEAD is detached, so the branch
			// comes from the rebase's own record of it.
			"rb=''\n" +
			"for d in rebase-merge rebase-apply; do if [ -d \"$gitdir/$d\" ]; then rb=\"$gitdir/$d\"; fi; done\n" +
			"if [ -n \"$rb\" ]; then branch=$(sed 's#^refs/heads/##' \"$rb/head-name\"); else branch=$(git rev-parse --abbrev-ref HEAD); fi\n" +
			"conflicted() {\n" +
			"  echo \"Rebase of '$branch' onto '$base' stopped on conflicts. Conflicted files:\"\n" +
			"  git diff --name-only --diff-filter=U\n" +
			"  echo \"Resolve the conflict markers in each file, then call commit_and_rebase again to continue the rebase and fast-forward '$base'.\"\n" +
			"  exit 1\n" +
			"}\n" +
			// Unique per call: parallel Developers in the same container must not clobber each
			// other's message through a shared fixed path. The trap removes it however we exit.
			"msgfile=$(mktemp /tmp/beast_commit_msg.XXXXXX) || { echo 'mktemp failed.'; exit 1; }\n" +
			"trap 'rm -f \"$msgfile\"' EXIT\n" +
			"echo '" + encoded + "' | base64 -d > \"$msgfile\"\n" +
			// An empty index is the only legitimate "nothing happened" case; any other add/commit
			// failure (hooks, identity, index lock) must stop the flow, not read as success with the
			// work still uncommitted.
			// Still-conflicted files must be checked before staging, which marks them resolved.
			"if [ -n \"$rb\" ]; then for f in $(git diff --name-only --diff-filter=U); do if grep -qE '^(<<<<<<<|>>>>>>>)( |$)' \"$f\"; then conflicted; fi; done; fi\n" +
			"git add -A || { echo 'git add failed.'; exit 1; }\n" +
			ProtectedGuard("git diff --cached --name-only") +
			"if [ -n \"$rb\" ]; then\n" +
			"  echo \"Continuing the rebase of '$branch' onto '$base' with the resolved files...\"\n" +
			"  git -c core.editor=true rebase --continue || conflicted\n" +
			"else\n" +
			"  echo \"Committing work on '$branch'...\"\n" +
			"  if git diff --cached --quiet; then\n" +
			"    echo '(nothing to commit)'\n" +
			"  else\n" +
			"    git commit -F \"$msgfile\" || { echo 'git commit failed.'; exit 1; }\n" +
			"  fi\n" +
			"fi\n" +
			"if [ -z \"$base\" ] || [ \"$base\" = \"$branch\" ]; then echo \"Not running in a per-launch worktree (no distinct base branch); committed in place.\"; exit 0; fi\n" +
			ProtectedGuard("git diff --name-only \"$base\"...HEAD") +
			"echo \"Integrating '$branch' onto local '$base'...\"\n" +
			"echo \"Rebasing '$branch' onto '$base' (linear history, no merge commit)...\"\n" +
			"git rebase \"$base\" || conflicted\n" +
			"echo \"Fast-forwarding base '$base' onto '$branch'...\"\n" +
			"git -C \"$main_wt\" merge --ff-only \"$branch\" || { echo \"Could not fast-forward '$base' (its worktree may have uncommitted changes).\"; exit 1; }\n";

		// Root: the repository is root's and the model's git is read-only, so recording the work and
		// fast-forwarding the main checkout (which the model's shell cannot reach at all) is the agent's
		// job alone. Root never runs git over metadata the shell could have written. Files a rebase
		// wrote into the working tree as root are handed back to the shell user by re-running the
		// sandbox lockdown, so the model can keep editing them.
		ToolResult result;
		await _gitGate.WaitAsync(ct);
		try
		{
			result = await ShellTools.AgentBashAsync("commit_and_rebase", script, null, ct);
			await Sandbox.LockDownAsync(ct);
		}
		finally
		{
			_gitGate.Release();
		}

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

	// Script lines refusing to commit or integrate changes inside the top-level dot-folders (.github,
	// .vscode, ...), which belong to the agent under the sandbox: those are where CI and editors
	// execute from, so a change there is a human's to make. The model cannot write them, so this is a
	// backstop. listCommand prints the changed paths to check. Empty without the sandbox, where the
	// rule does not apply.
	private static string ProtectedGuard(string listCommand)
	{
		string guard = string.Empty;
		if (Sandbox.ShellUserActive)
		{
			guard =
				$"protected=$({listCommand} | grep -E '^\\.[^/]+/')\n" +
				"if [ -n \"$protected\" ]; then\n" +
				"  echo 'Refusing: these changes are in protected folders, which only a human may change:'\n" +
				"  echo \"$protected\"\n" +
				"  echo 'Remove them from the branch, then call commit_and_rebase again.'\n" +
				"  exit 1\n" +
				"fi\n";
		}
		return guard;
	}
}