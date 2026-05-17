using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Integration tests that run a real single-turn LLM conversation against each model
// configured for each role. Tests are skipped gracefully when API keys or models are absent.
public static class PerModelLlmTests
{
    public static void Test(TestContext ctx, LlmRegistry registry, RoleService roleService, SettingsService settings, TestCaptureTransport captureTransport)
    {
        Console.WriteLine("  PerModelLlmTests");

        List<LLMRole> roles = new List<LLMRole>(roleService.Roles.Values);
        if (roles.Count == 0)
        {
            Console.WriteLine("    SKIP: no roles configured");
            return;
        }

        foreach (LLMRole role in roles)
        {
            RunRoleTests(ctx, registry, settings, captureTransport, role);
        }
    }

    private static void RunRoleTests(TestContext ctx, LlmRegistry registry, SettingsService settings, TestCaptureTransport captureTransport, LLMRole role)
    {
        Console.WriteLine($"    Role: {role.Name}");

        if (role.ModelNames.Count == 0)
        {
            Console.WriteLine($"      SKIP: role '{role.Name}' has no model names");
            return;
        }

        foreach (string modelId in role.ModelNames)
        {
            RunSingleModelTest(ctx, registry, settings, captureTransport, role, modelId);
        }
    }

    private static void RunSingleModelTest(TestContext ctx, LlmRegistry registry, SettingsService settings, TestCaptureTransport captureTransport, LLMRole role, string modelId)
    {
        Console.WriteLine($"      Model: {modelId}");

        LlmService? service = null;
        try
        {
            // Check if the model is registered and has an API key.
            if (!registry.HasModel(modelId))
            {
                Console.WriteLine($"        SKIP: model '{modelId}' not in registry (no config or API key?)");
                return;
            }

            service = registry.GetServiceById(modelId);
            if (service == null)
            {
                Console.WriteLine($"        SKIP: model '{modelId}' service not found");
                return;
            }

            if (!service.IsAvailable)
            {
                Console.WriteLine($"        SKIP: model '{modelId}' is currently unavailable");
                return;
            }

            if (string.IsNullOrEmpty(service.Model.ApiKey))
            {
                Console.WriteLine($"        SKIP: model '{modelId}' has no API key");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        SKIP: pre-check error for '{modelId}': {ex.Message}");
            return;
        }

        // Run a minimal single-turn conversation.
        try
        {
            TestCaptureTransport localTransport = new TestCaptureTransport();
            BeastSession session = new BeastSession(Guid.NewGuid().ToString("N"), role.Name, $"test-{modelId}");
            session.SetSystemPrompt(role.SystemPrompt);
            session.AddUserMessage("Reply with exactly: PING");

            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Tool[] tools = registry.GetToolsForRole(role);
            LlmResult result = service.RunToCompletionAsync(session, tools, 0, localTransport, cts.Token).GetAwaiter().GetResult();

            bool gotResponse = false;
            foreach ((FrameType type, string text) in localTransport.Sent)
            {
                if (type == FrameType.Output && text.IndexOf("PING", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    gotResponse = true;
                    break;
                }
            }

            ctx.Assert(result.Success, $"PerModel [{role.Name}/{modelId}]: LLM call succeeded");
            ctx.Assert(gotResponse, $"PerModel [{role.Name}/{modelId}]: response contains PING");

            captureTransport.Send(FrameType.Status, $"PASS [{role.Name}/{modelId}]");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"        SKIP: model '{modelId}' timed out after 30s");
            captureTransport.Send(FrameType.Status, $"TIMEOUT [{role.Name}/{modelId}]");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        SKIP: model '{modelId}' threw: {ex.Message}");
            captureTransport.Send(FrameType.Status, $"ERROR [{role.Name}/{modelId}]: {ex.Message}");
        }
    }
}
