using System.IO;
using System.Threading;
using System.Threading.Tasks;


// At agent startup the container binds the real repo to /git and the per-launch worktree folder (possibly
// empty) to /workspace. This creates or re-attaches the git worktree at /workspace for the launch's branch
// (passed as a startup argument, never an env var) before anything reads the working tree. A no-op when
// /git is absent or no branch was passed (debug or native runs without the split mount). Returns ok=false
// when /workspace could not be made a usable checkout, so the agent aborts rather than operate on an empty
// or broken directory.
public static class WorktreeBootstrap
{
	public static async Task<(bool ok, string detail)> EnsureAsync(string branch, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(branch) || !Directory.Exists("/git"))
			return (true, string.Empty);

		// /workspace already a checkout -> repair the gitdir pointers (idempotent). Otherwise add the
		// worktree: attach to the branch if it exists, else create it. A stale registration for the path
		// (folder wiped without /finish) is cleared and retried once. branch is sanitized to a folder/branch
		// charset host-side, so single-quoting it here is safe.
		string script =
			"set -e\n" +
			"branch='" + branch + "'\n" +
			"add() {\n" +
			"  if git -C /git show-ref --verify --quiet \"refs/heads/$branch\"; then\n" +
			"    git -C /git worktree add /workspace \"$branch\"\n" +
			"  else\n" +
			"    git -C /git worktree add /workspace -b \"$branch\"\n" +
			"  fi\n" +
			"}\n" +
			"if [ -e /workspace/.git ]; then\n" +
			"  git -C /git worktree repair /workspace || true\n" +
			"else\n" +
			"  if ! add; then\n" +
			"    git -C /git worktree remove --force /workspace 2>/dev/null || true\n" +
			"    git -C /git worktree prune || true\n" +
			"    add\n" +
			"  fi\n" +
			"fi\n";

		ToolResult result = await ShellTools.BashAsync("worktree_bootstrap", script, null, ct);
		string detail = result.StdErr;
		if (!string.IsNullOrEmpty(result.StdOut))
			detail = string.IsNullOrEmpty(detail) ? result.StdOut : detail + "\n" + result.StdOut;
		return (result.ExitCode == 0, detail);
	}
}