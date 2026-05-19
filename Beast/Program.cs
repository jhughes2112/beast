using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// Beast CLI -- launches the Agent docker container and communicates with it over stdio.
public class Program
{
    public static async Task<int> Main(string[] args)
    {
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

        // Agent receives all switches except Beast-only ones.
        List<string> agentSwitches = new List<string>();
        foreach (string sw in switches)
        {
            if (sw != "/help" && sw != "/h" && sw != "/verbose")
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
        IDisplay display = nonInteractive ? new DisplayConsole(log, verbose) : new DisplayTui(initialMode);
        await using BeastApp app = new BeastApp("beastagent", messages, display, log);
        return await app.Run();
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
        Console.WriteLine("  --test            Run Beast transport tests locally");
        Console.WriteLine("  --verbose         Show diagnostic debug output from the Agent");
        Console.WriteLine("  --help            Show this help");
    }
}
