using System;
using System.Collections.Generic;
using System.IO;
using System.Text;


// Host-side bookkeeping for per-launch git worktrees. Each Beast launch binds one worktree folder to
// /workspace in the container; the real repo (cwd) is bound to /git. Worktrees live under
// <repo>/.beast/worktrees/<name>, inside the project itself: this keeps them on the same drive as the repo
// and lets the project cover every worktree with one .gitignore entry (.beast/). The folder name doubles
// as the git branch name.
public static class Worktrees
{
    // The chosen worktree for a launch: the folder name (also the branch), its host path, and the repo it
    // belongs to. Passed to LaunchDocker to build the two binds and the deterministic container name.
    public readonly struct Selection
    {
        public readonly string Name;
        public readonly string Branch;
        public readonly string HostPath;
        public readonly string RepoCwd;

        public Selection(string name, string hostPath, string repoCwd)
        {
            Name = name;
            Branch = name;
            HostPath = hostPath;
            RepoCwd = repoCwd;
        }
    }

    // An existing worktree folder and when it was last touched, for the launch menu ordering.
    public readonly struct Info
    {
        public readonly string Name;
        public readonly DateTime LastUsedUtc;

        public Info(string name, DateTime lastUsedUtc)
        {
            Name = name;
            LastUsedUtc = lastUsedUtc;
        }
    }

    // The directory holding all of a repo's worktrees: <repo>/.beast/worktrees. Kept inside the project so the
    // worktrees stay on the repo's drive and a single .gitignore entry (.beast/) covers them all.
    public static string RepoDir(string cwd)
    {
        return Path.Combine(cwd, ".beast", "worktrees");
    }

    // Restricts a user-entered name to a folder- and branch-safe charset (letters, digits, '.', '_', '-'),
    // collapsing other runs to '-'. The same string is used for the folder and the git branch, so '/' is
    // not allowed here. Empty input becomes "work".
    public static string SanitizeName(string name)
    {
        StringBuilder sb = new StringBuilder(name.Length);
        bool lastDash = false;
        foreach (char c in name.Trim())
        {
            bool ok = char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-';
            if (ok)
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        string s = sb.ToString().Trim('-', '.');
        return s.Length == 0 ? "work" : s;
    }

    // Existing worktree folders for this repo, most-recently-used first (by folder write time) so the menu
    // can pre-select the one the user most likely wants. Returns empty when none exist yet.
    public static List<Info> List(string cwd)
    {
        List<Info> result = new List<Info>();
        string dir = RepoDir(cwd);
        if (!Directory.Exists(dir))
            return result;

        foreach (string path in Directory.GetDirectories(dir))
        {
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
                continue;
            result.Add(new Info(name, Directory.GetLastWriteTimeUtc(path)));
        }

        result.Sort((a, b) => b.LastUsedUtc.CompareTo(a.LastUsedUtc));
        return result;
    }

    // Builds the selection for a name, creating the (empty) worktree folder so it can be bind-mounted.
    // The agent runs `git worktree add` into it once the container is up. Creating the worktree also brings
    // the project's .beast folder into being, so its .gitignore is ensured here in the same step.
    public static Selection Ensure(string cwd, string name)
    {
        string safe = SanitizeName(name);
        string hostPath = Path.Combine(RepoDir(cwd), safe);
        Directory.CreateDirectory(hostPath);
        EnsureBeastGitignore(cwd);
        return new Selection(safe, hostPath, cwd);
    }

    // Drops a .gitignore into the project's .beast folder so the generated worktrees and saved sessions stay
    // out of git without the project having to add them by hand. Written only when missing, so a project can
    // edit it freely. Best effort: a failure here never blocks a launch.
    public static void EnsureBeastGitignore(string cwd)
    {
        string beastDir = Path.Combine(cwd, ".beast");
        string gitignorePath = Path.Combine(beastDir, ".gitignore");
        try
        {
            Directory.CreateDirectory(beastDir);
            if (File.Exists(gitignorePath))
                return;

            // Worktrees hold per-launch checkouts; sessions hold saved conversations. Both are local state,
            // not source. The agent writes its sessions under a worktree, so "worktrees/" already covers that
            // copy; "sessions/" covers a session folder written directly in the repo (e.g. a non-worktree run).
            string contents = "# Beast-generated: per-launch worktrees and saved sessions are local state, not source.\nworktrees/\nsessions/\n";
            File.WriteAllText(gitignorePath, contents);
        }
        catch
        {
        }
    }

    // The deterministic container name for a worktree: beast_<name>. A running container with this name
    // means the worktree is currently occupied, which the launch menu surfaces and the launcher enforces.
    public static string ContainerName(string worktreeName)
    {
        return "beast_" + worktreeName;
    }

    // Removes a worktree's host folder after the agent has detached it via `git worktree remove`. Best
    // effort: a folder still in use or already gone is not fatal.
    public static void RemoveFolder(string cwd, string name)
    {
        string hostPath = Path.Combine(RepoDir(cwd), SanitizeName(name));
        try
        {
            if (Directory.Exists(hostPath))
                Directory.Delete(hostPath, true);
        }
        catch
        {
        }
    }
}
