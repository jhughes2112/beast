// Beast CLI -- launches the Agent docker container and communicates with it over stdio.
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool showHelp = false;
        List<string> agentSwitches = new List<string>();
        string? prompt = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "--help" || arg == "-h")
            {
                showHelp = true;
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
                agentSwitches.Add(arg);
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
        Console.WriteLine("  --help            Show this help");
    }
}
