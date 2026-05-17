// Beast CLI -- launches the Agent docker container and communicates with it over stdio.
public class Program
{
    public static int Main(string[] args)
    {
        string? prompt = null;
        bool showHelp = false;
        bool runTests = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--test":
                    runTests = true;
                    break;
                case "-p":
                case "--prompt":
                    if (i + 1 < args.Length)
                        prompt = args[++i];
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    if (!args[i].StartsWith("-"))
                        prompt = args[i];
                    break;
            }
        }

        if (runTests)
        {
            Console.WriteLine("=== Running Agent Tests ===");
            int agentResult = TestRunner.RunAll();

            Console.WriteLine("=== Running Beast Transport Tests ===");
            TestContext beastCtx = new TestContext();
            TransportTests.Test(beastCtx);
            Console.WriteLine($"=== Beast Transport Tests: {beastCtx.Passed} passed, {beastCtx.Failed} failed ===");
            int beastResult = beastCtx.Failed > 0 ? 1 : 0;

            return agentResult != 0 ? agentResult : beastResult;
        }

        if (showHelp)
        {
            PrintHelp();
            return 0;
        }

        using BeastApp app = new BeastApp("beastagent", prompt);
        app.Run();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("beast - CLI host for the Agent docker container");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  beast [--prompt <text>]");
        Console.WriteLine("  beast --test");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --prompt <text>   Optional initial prompt to send");
        Console.WriteLine("  --test            Run agent unit tests");
        Console.WriteLine("  --help            Show this help");
    }
}
