// Beast CLI -- launches the Agent docker container and communicates with it over stdio.
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool showHelp = false;
        bool runBeastTests = false;
        List<string> agentSwitches = new List<string>();
        string? prompt = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "--help" || arg == "-h")
            {
                showHelp = true;
            }
            else if (arg == "--test")
            {
                runBeastTests = true;
            }
            else if (arg == "-p" || arg == "--prompt")
            {
                // Everything from here to the end is the prompt.
                List<string> parts = new List<string>();
                for (int j = i + 1; j < args.Length; j++)
                    parts.Add(args[j]);
                if (parts.Count > 0)
                    prompt = string.Join(" ", parts);
                break;
            }
            else if (arg.StartsWith("/") || arg.StartsWith("-"))
            {
                // New switch: forward to the agent container.
                // Translate leading double-dash to slash so the agent recognizes commands like --test -> /test.
                string sw = arg;
                if (sw.StartsWith("--"))
                    sw = "/" + sw.Substring(2);
                agentSwitches.Add(sw);
            }
            else if (agentSwitches.Count > 0)
            {
                // Bare word following a switch: append to the previous switch as its value.
                agentSwitches[agentSwitches.Count - 1] = $"{agentSwitches[agentSwitches.Count - 1]} {arg}";
            }
        }

        if (showHelp)
        {
            PrintHelp();
            return 0;
        }

        if (runBeastTests)
        {
            Console.WriteLine("=== Running Beast Tests ===");
            TestContext ctx = new TestContext(new ConsoleTransport());
            TransportTests.Test(ctx);
            Console.WriteLine($"=== Beast Tests: {ctx.Passed} passed, {ctx.Failed} failed ===");
            return ctx.Failed > 0 ? 1 : 0;
        }

        await using BeastApp app = new BeastApp("beastagent", agentSwitches, prompt);
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
        Console.WriteLine("  --help            Show this help");
    }
}
