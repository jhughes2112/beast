using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

// Beast CLI -- launches the Agent docker container and communicates with it over stdio.
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.Title = "Beast";
        List<string> switches = new List<string>();
        string? prompt = null;

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
        bool showHelp = switches.Contains("/help") || switches.Contains("/h");
        bool runBeastTests = switches.Contains("/test");
        bool verbose = switches.Contains("/verbose");

        // --worktree <name> selects (or creates) the worktree without showing the menu. Beast-only.
        string? worktreeArg = null;
        foreach (string sw in switches)
        {
            if (sw == "/worktree" || sw.StartsWith("/worktree ", StringComparison.Ordinal))
            {
                int sp = sw.IndexOf(' ');
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
            Console.WriteLine($"=== Beast Tests: {ctx.Passed} passed, {ctx.Failed} failed ===");
            if (ctx.Failed > 0)
                return 1;
        }

        // Build the ordered message list: switches, then prompt, then /quit if non-interactive.
        bool nonInteractive = prompt != null || agentSwitches.Count > 0;
        List<string> messages = new List<string>(agentSwitches);
        if (prompt != null)
            messages.Add(prompt);
        if (nonInteractive)
            messages.Add("/quit");

        Log log = new Log(verbose);
        CollapseMode initialMode = verbose ? CollapseMode.Verbose : CollapseMode.Minimized;
        IDisplay display = nonInteractive ? new DisplayConsole(log, verbose) : new DisplayScreen(initialMode);

        // /debug — connect to an already-running agent on port 13131 (start Agent separately in VS).
        bool useDebug = switches.Contains("/debug");

        ILauncher agentContext;
        string agentName;
        Worktrees.Selection? worktree = null;
        if (useDebug)
        {
            agentContext = new LaunchDebug();
            agentName = "beast_debug";
        }
        else
        {
            string cwd = Directory.GetCurrentDirectory();

            // Parse settings.json and roles.json here on the host, before the worktree menu takes over the
            // screen, so a bad config file shows a located error on the normal terminal instead of dying
            // silently inside the Docker container (whose stderr is lost when the container is reaped).
            if (!PreflightConfig(cwd))
                return 1;

            Worktrees.Selection? sel = await ResolveWorktree(cwd, worktreeArg, nonInteractive);
            if (sel == null)
                return 0;   // user cancelled the worktree menu
            worktree = sel;
            agentContext = new LaunchDocker("beastagent", log, sel.Value);
            agentName = Worktrees.ContainerName(sel.Value);
        }

        await using BeastApp app = new BeastApp(agentContext, messages, display, log, agentName, worktree);
        return await app.Run();
    }

    // Menu sentinel for "run in the current folder, ephemerally". The colons can never survive SanitizeName,
    // so this value cannot collide with a chosen or typed worktree name.
    private const string EphemeralChoice = "::current-folder::";

    // Resolves what this launch attaches to. A worktree name (explicit --worktree <name>) creates/attaches a
    // git worktree with a saved session; "none", or no name on a non-interactive run, runs ephemerally in the
    // current folder with no git and no saved session; an interactive run with no name shows the chooser, which
    // itself offers the current-folder option alongside the existing worktrees. Returns null only on cancel.
    private static async Task<Worktrees.Selection?> ResolveWorktree(string cwd, string? nameArg, bool nonInteractive)
    {
        if (string.Equals(nameArg, "none", StringComparison.OrdinalIgnoreCase))
            return Worktrees.Selection.ForCurrentFolder(cwd);

        if (!string.IsNullOrWhiteSpace(nameArg))
            return Worktrees.Ensure(cwd, nameArg!);

        if (nonInteractive)
            return Worktrees.Selection.ForCurrentFolder(cwd);

        List<Worktrees.Info> existing = Worktrees.List(cwd);
        HashSet<string> running = await LaunchDocker.RunningWorktreeNamesAsync();

        List<SelectMenu.Item> items = new List<SelectMenu.Item>();
        items.Add(new SelectMenu.Item("Current folder (ephemeral, no git)", EphemeralChoice, string.Empty, false));
        foreach (Worktrees.Info info in existing)
        {
            bool inUse = running.Contains(info.Name);
            items.Add(new SelectMenu.Item(info.Name, info.Name, inUse ? "in use" : string.Empty, inUse));
        }

        // Pre-select the most recent worktree if any exist, otherwise the current-folder option.
        int initialIndex = existing.Count > 0 ? 1 : 0;
        string suggestion = existing.Count == 0 ? "work" : string.Empty;
        SelectMenu.Result result = SelectMenu.Choose($"Beast — choose a worktree  ({cwd})", items, "Create new worktree", suggestion, initialIndex, LaunchNotes.Pick());
        if (result.Cancelled)
            return null;
        if (!result.IsNew && string.Equals(result.Value, EphemeralChoice, StringComparison.Ordinal))
            return Worktrees.Selection.ForCurrentFolder(cwd);

        return Worktrees.Ensure(cwd, result.Value);
    }

    // Loads settings.json and roles.json from the same locations the agent uses (project .beast first,
    // then ~/.beast for settings), letting their loaders validate the JSON. Both loaders print a friendly,
    // line-located message to stderr and throw ConfigException on a parse/load failure; we catch it and
    // return false so the launch aborts visibly rather than the container crash-looping out of sight.
    private static bool PreflightConfig(string cwd)
    {
        bool ok;
        try
        {
            SettingsService settings = new SettingsService(cwd);
            RoleService roles = new RoleService(cwd);
            ok = settings != null && roles != null;
        }
        catch (ConfigException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Aborting: the agent was not launched because the configuration above is invalid.");
            ok = false;
        }
        return ok;
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
        Console.WriteLine("  --worktree <name> Create/attach a git worktree and run there, with a session saved in it");
        Console.WriteLine("  --test            Run Beast transport tests locally");
        Console.WriteLine("  --debug           Connect to an agent already running on port 13131 (for VS debugging).");
        Console.WriteLine("  --verbose         Show diagnostic debug output from the Agent");
        Console.WriteLine("  --help            Show this help");
    }
}
