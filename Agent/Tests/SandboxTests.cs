using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


// Path confinement for the in-process file tools, and apt_install's input checks. These run
// anywhere; the ownership lockdown and the shell user need the container, where they are exercised
// by the container check rather than here.
public static class SandboxTests
{
	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  SandboxTests");

		string workspace = Path.Combine(Path.GetTempPath(), $"beast-sandbox-{Guid.NewGuid():N}", "workspace");
		Directory.CreateDirectory(Path.Combine(workspace, ".beast", "attachments"));
		Directory.CreateDirectory(Path.Combine(workspace, ".git"));
		Directory.CreateDirectory(Path.Combine(workspace, "src"));
		File.WriteAllText(                     Path.Combine(workspace, "src", "a.txt"), "hello");
		File.WriteAllText(Path.Combine(workspace, ".beast", "attachments", "shot.png"), "png");
		File.WriteAllText(          Path.Combine(workspace, ".beast", "settings.json"), "{\"apiKey\":\"secret\"}");

		string previous = Environment.CurrentDirectory;
		try
		{
			Directory.SetCurrentDirectory(workspace);
			Sandbox.Initialize(workspace);
			TestConfinement(ctx, workspace);
			TestSymlinkEscape(ctx, workspace);
			await TestToolsRefuseAsync(ctx, workspace);
			await TestAptNamesAsync(ctx);
			if (Sandbox.ShellUserActive)
			{
				await TestShellUserAsync(ctx, workspace);
				// Under the starting folder, not the temp folder: run the suite from a bind mount
				// (-v <host folder>:/work -w /work) to test the permission rules that apply there.
				string scratch = Path.Combine(previous, $"beast-integrate-{Guid.NewGuid():N}");
				try
				{
					await TestAgentIntegratesAsync(ctx, scratch);
				}
				finally
				{
					await ShellTools.AgentBashAsync("cleanup", $"rm -rf '{scratch}'", null, CancellationToken.None);
				}
			}
			else
			{
				ctx.Log("    (shell-user checks skipped: not root in the container image)");
			}
		}
		finally
		{
			Directory.SetCurrentDirectory(previous);
			Sandbox.Initialize(previous);
			Directory.Delete(Path.GetDirectoryName(workspace)!, true);
		}
	}

	private static void TestConfinement(TestContext ctx, string workspace)
	{
		ctx.Assert(Sandbox.Confine("src/a.txt", false, false).Path != null, "Confine: a relative path in the workspace is allowed");
		ctx.Assert(      Sandbox.Confine(Path.Combine(workspace, "src", "new.txt"), true, false).Path != null, "Confine: a new file in the workspace may be written");
		ctx.Assert(Sandbox.Confine(Path.Combine(Path.GetTempPath(), "scratch.txt"), true, false).Path != null, "Confine: the temp folder is allowed");

		ctx.Assert(Sandbox.Confine("../../../../../../etc/passwd", false, false).Path == null, "Confine: .. cannot climb out of the workspace");
		ctx.Assert(Sandbox.Confine(OperatingSystem.IsWindows() ? "C:\\Windows\\win.ini" : "/root/.beast/settings.json", false, false).Path == null, "Confine: an absolute path elsewhere is refused");

		ctx.Assert(       Sandbox.Confine(".beast/settings.json", false, false).Path == null, "Confine: .beast is off limits for reading");
		ctx.Assert(Sandbox.Confine(".beast/attachments/shot.png", false, false).Path == null, "Confine: attachments are off limits to the general tools");
		ctx.Assert( Sandbox.Confine(".beast/attachments/shot.png", false, true).Path != null, "Confine: inspect_media may read a dropped attachment");
		ctx.Assert(         Sandbox.Confine(".beast/attachments/shot.png", true, true).Path == null, "Confine: but never write one");
		ctx.Assert(Sandbox.Confine(".beast/attachments/../settings.json", false, true).Path == null, "Confine: .. out of attachments is caught");

		ctx.Assert(            Sandbox.Confine(".git/config", false, false).Path != null, "Confine: git control files may be read");
		ctx.Assert(             Sandbox.Confine(".git/config", true, false).Path == null, "Confine: but not written by the file tools");
		ctx.Assert(                    Sandbox.Confine(".git", true, false).Path == null, "Confine: nor the .git entry itself (a worktree's pointer file)");
		ctx.Assert(Sandbox.Confine(".github/workflows/ci.yml", true, false).Path == null, "Confine: nothing inside a top-level dot-folder may be written");
		ctx.Assert(     Sandbox.Confine(".brand-new/file.txt", true, false).Path == null, "Confine: including one that does not exist yet");
		ctx.Assert(              Sandbox.Confine(".gitignore", true, false).Path != null, "Confine: top-level dot FILES stay writable");
		ctx.Assert(    Sandbox.Confine("src/.hidden/file.txt", true, false).Path != null, "Confine: dot-folders below the top level are ordinary folders");
	}

	// A symlink the model's shell plants inside the workspace must not carry the file tools out of it.
	// The targets are system files, outside both the workspace and the temp folder (which is itself
	// allowed, and on Linux holds this test's workspace). Skipped where the process may not create
	// symlinks (Windows without developer mode).
	private static void TestSymlinkEscape(TestContext ctx, string workspace)
	{
		string secretDir  = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : "/etc";
		string secretFile = Path.Combine(secretDir, OperatingSystem.IsWindows() ? "win.ini" : "passwd");
		string src        = Path.Combine(workspace, "src");

		bool linked = false;
		try
		{
			File.CreateSymbolicLink(Path.Combine(src, "innocent.txt"), secretFile);
			Directory.CreateSymbolicLink(Path.Combine(src, "dirlink"), secretDir);
			File.CreateSymbolicLink(Path.Combine(src, "relative.txt"), Path.GetRelativePath(src, secretFile));
			File.CreateSymbolicLink(Path.Combine(src, "inside.txt"), "a.txt");
			linked = true;
		}
		catch (Exception)
		{
			ctx.Log("    (symlink checks skipped: this process cannot create symlinks)");
		}

		if (linked)
		{
			ctx.Assert(Sandbox.Confine("src/innocent.txt", false, false).Path == null, "Symlink: a file link out of the workspace is refused");
			ctx.Assert(Sandbox.Confine($"src/dirlink/{Path.GetFileName(secretFile)}", false, false).Path == null, "Symlink: a folder link out of the workspace is refused");
			ctx.Assert(  Sandbox.Confine("src/relative.txt", false, false).Path == null, "Symlink: a relative link climbing out is refused");
			ctx.Assert(Sandbox.Confine("src/dirlink/new.txt", true, false).Path == null, "Symlink: a new file under a linked folder is refused");
			string? inside = Sandbox.Confine("src/inside.txt", false, false).Path;
			ctx.AssertEqual(Path.Combine(workspace, "src", "a.txt"), inside ?? string.Empty, "Symlink: a link within the workspace resolves to its target");
		}
	}

	private static async Task TestToolsRefuseAsync(TestContext ctx, string workspace)
	{
		ToolResult read = await FileTools.ReadFileAsync("t1", ".beast/settings.json", string.Empty, string.Empty, 500, false, CancellationToken.None);
		ctx.Assert(read.ExitCode != 0 && !read.StdOut.Contains("secret", StringComparison.Ordinal), "Tools: read_file refuses .beast");

		ToolResult write = await FileTools.WriteFileAsync("t2", ".git/hooks/post-checkout", "evil", CancellationToken.None);
		ctx.Assert(write.ExitCode != 0 && !File.Exists(Path.Combine(workspace, ".git", "hooks", "post-checkout")), "Tools: write_file refuses a git hook");

		ToolResult ok = await FileTools.WriteFileAsync("t3", "src/deep/new/file.txt", "fine", CancellationToken.None);
		ctx.Assert(ok.ExitCode == 0 && File.Exists(Path.Combine(workspace, "src", "deep", "new", "file.txt")), "Tools: write_file still creates files and folders in the workspace");
	}

	// Inside the image, as root: the model's shells are the shell user, the agent's own is root, the
	// keys' folder is out of reach, and what the file tools create belongs to the shell user.
	private static async Task TestShellUserAsync(TestContext ctx, string workspace)
	{
		// The workspace root as the lockdown leaves it: root's, sticky, open to the shell user.
		await ShellTools.AgentBashAsync("u0", $"chmod 1777 '{workspace}'", null, CancellationToken.None);

		ToolResult bash = await ShellTools.BashAsync("u1", "id -un; echo $HOME", null, CancellationToken.None);
		ctx.AssertEqual($"{Sandbox.ShellUser}\n{Sandbox.ShellHome}", bash.StdOut.Trim(), "ShellUser: bash runs as the shell user with its home");

		ToolResult restricted = await ShellTools.ReadonlyBashAsync("u2", "whoami", null, CancellationToken.None);
		ctx.AssertEqual(Sandbox.ShellUser, restricted.StdOut.Trim(), "ShellUser: readonly_bash too");

		ToolResult agent = await ShellTools.AgentBashAsync("u3", "id -un", null, CancellationToken.None);
		ctx.AssertEqual("root", agent.StdOut.Trim(), "ShellUser: the agent's own shell stays root");

		ToolResult keys = await ShellTools.BashAsync("u4", "ls /root", null, CancellationToken.None);
		ctx.Assert(keys.ExitCode != 0, "ShellUser: /root (the API keys) cannot be read");

		ToolResult apt = await ShellTools.BashAsync("u5", "apt-get install -y --no-install-recommends cowsay", 120, CancellationToken.None);
		ctx.Assert(apt.ExitCode != 0, "ShellUser: apt-get itself does not work for the model's shell");

		await FileTools.WriteFileAsync("u6", "made/by/tool.txt", "x", CancellationToken.None);
		ToolResult owner = await ShellTools.AgentBashAsync("u7", $"stat -c %U '{Path.Combine(workspace, "made", "by", "tool.txt")}' '{Path.Combine(workspace, "made")}'", null, CancellationToken.None);
		ctx.AssertEqual($"{Sandbox.ShellUser}\n{Sandbox.ShellUser}", owner.StdOut.Trim(), "ShellUser: files and folders write_file creates belong to the shell user");

		ToolResult edit = await ShellTools.BashAsync("u8", "echo more >> made/by/tool.txt && rm -r made", null, CancellationToken.None);
		ctx.AssertEqual(0, edit.ExitCode, "ShellUser: so the model's shell can change and delete them");
	}

	// The worktree flow end to end: the model's shell works in the worktree's files but cannot change
	// the repository at all, the agent records and integrates the work as root (continuing a rebase
	// the model resolved), and the main checkout stays out of the shell user's reach.
	private static async Task TestAgentIntegratesAsync(TestContext ctx, string scratch)
	{
		// The worktree sits outside the main checkout, as /workspace does beside /git in the container
		// (on the host it lives under the main checkout's .beast, which the container never goes through).
		string     main  = Path.Combine(scratch, "main");
		string     wt    = Path.Combine(scratch, "wt");
		ToolResult setup = await ShellTools.AgentBashAsync("g0",
			$"set -e; mkdir -p '{main}'; cd '{main}'; git init -q -b main; git config user.email agent@example.com; git config user.name agent; " +
			$"echo hello > README.md; mkdir -p .github; echo 'on: push' > .github/ci.yml; git add -A; git commit -qm init; git worktree add -q '{wt}' -b feature",
			null, CancellationToken.None);
		ctx.AssertEqual(0, setup.ExitCode, $"Integrate: scratch repo and worktree created {setup.StdErr}");

		string previous = Environment.CurrentDirectory;
		try
		{
			Directory.SetCurrentDirectory(wt);
			Sandbox.Initialize(wt);
			await Sandbox.LockDownAsync(CancellationToken.None);

			// The model works in the files; its git can look but not change anything.
			ToolResult work = await ShellTools.BashAsync("g1", "echo change > feature.txt && git status --short", null, CancellationToken.None);
			ctx.Assert(work.ExitCode == 0 && work.StdOut.Contains("feature.txt", StringComparison.Ordinal), $"Integrate: the shell user edits and git status sees it {work.StdErr}");

			ToolResult commit = await ShellTools.BashAsync("g2", "git add -A && git commit -qm 'model commit'", null, CancellationToken.None);
			ctx.Assert(commit.ExitCode != 0, "Integrate: the shell user cannot commit — the repository is the agent's");

			string     gitdir  = Path.Combine(main, ".git");
			ToolResult attacks = await ShellTools.BashAsync("g3",
				$"echo /tmp/evil > '{gitdir}/commondir'; echo evil > '{gitdir}/hooks/post-checkout'; git config core.fsmonitor evil; " +
				"git update-ref refs/heads/main HEAD~0; echo evil | git hash-object -w --stdin; " +
				$"ls '{gitdir}' | grep -c commondir; ls '{gitdir}/hooks' | grep -vc sample",
				null, CancellationToken.None);
			ToolResult planted = await ShellTools.AgentBashAsync("g4", $"ls '{gitdir}/commondir' '{gitdir}/hooks/post-checkout' 2>/dev/null | wc -l; git -C '{main}' config core.fsmonitor || true", null, CancellationToken.None);
			ctx.AssertEqual("0", planted.StdOut.Trim(), $"Integrate: nothing can be planted in .git (commondir, hooks, config) — {attacks.StdErr}");

			ToolResult peek = await ShellTools.BashAsync("g5", $"cat '{main}/README.md' || ls '{main}'", null, CancellationToken.None);
			ctx.Assert(peek.ExitCode != 0, "Integrate: the main checkout is out of the shell user's reach");

			(bool ok, string transcript) = await GitTools.CommitAndRebaseAsync("more work", CancellationToken.None);
			ctx.Assert(ok, $"Integrate: the agent commits, rebases and fast-forwards main as root — {transcript}");

			ToolResult landed = await ShellTools.AgentBashAsync("g6", $"cat '{main}/feature.txt'", null, CancellationToken.None);
			ctx.AssertEqual("change", landed.StdOut.Trim(), "Integrate: the work reached the main checkout");

			// A conflict: main moves on underneath, the model's next change collides with it, the agent
			// stops and says so, the model fixes the file, and the agent's next call finishes the job.
			await ShellTools.AgentBashAsync("g7", $"cd '{main}' && echo 'from main' > README.md && git commit -qam 'main edit'", null, CancellationToken.None);
			await ShellTools.BashAsync("g8", "echo 'from worktree' > README.md", null, CancellationToken.None);
			(bool clash, string stopped) = await GitTools.CommitAndRebaseAsync("worktree edit", CancellationToken.None);
			ctx.Assert(!clash && stopped.Contains("stopped on conflicts", StringComparison.Ordinal), $"Integrate: a rebase conflict stops and names the files — {stopped}");

			ToolResult resolve = await ShellTools.BashAsync("g9", "printf 'from both\\n' > README.md", null, CancellationToken.None);
			ctx.AssertEqual(0, resolve.ExitCode, $"Integrate: the shell user can edit the files the rebase wrote {resolve.StdErr}");
			(bool resumed, string continued) = await GitTools.CommitAndRebaseAsync("worktree edit", CancellationToken.None);
			ctx.Assert(resumed, $"Integrate: calling again continues the rebase and integrates — {continued}");

			ToolResult merged = await ShellTools.AgentBashAsync("g10", $"cat '{main}/README.md'", null, CancellationToken.None);
			ctx.AssertEqual("from both", merged.StdOut.Trim(), "Integrate: the resolution reached the main checkout");
		}
		finally
		{
			Directory.SetCurrentDirectory(previous);
			Sandbox.Initialize(previous);
		}
	}

	private static async Task TestAptNamesAsync(TestContext ctx)
	{
		string[] refused = new string[] { "-o APT::Update::Pre-Invoke::=id", "./evil.deb", "/tmp/x.deb", "curl=7.0", "curl/noble", "Curl", "curl;id", "$(id)", "graphviz:amd64" };
		foreach (string packages in refused)
		{
			ToolResult result = await AptTools.InstallAsync("apt", packages, CancellationToken.None);
			ctx.Assert(result.ExitCode != 0 && result.StdErr.Contains("not a valid package name", StringComparison.Ordinal), $"apt_install: refuses '{packages}' before running anything");
		}
		ToolResult empty = await AptTools.InstallAsync("apt", "  ,  ", CancellationToken.None);
		ctx.Assert(empty.ExitCode != 0, "apt_install: refuses an empty list");
	}
}