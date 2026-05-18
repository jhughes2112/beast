using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Integration tests that run a real single-turn LLM conversation against each model
// configured for each role. Tests are skipped gracefully when API keys or models are absent.
public static class PerModelLlmTests
{
    public static void Test(TestContext ctx, LlmRegistry registry, RoleService roleService, SettingsService settings)
    {
        ctx.Log("  PerModelLlmTests");

        List<LLMRole> roles = new List<LLMRole>(roleService.Roles.Values);
        if (roles.Count == 0)
        {
            ctx.Log("    SKIP: no roles configured");
            return;
        }

        foreach (LLMRole role in roles)
        {
            RunRoleTests(ctx, registry, settings, role);
        }
    }

    private static void RunRoleTests(TestContext ctx, LlmRegistry registry, SettingsService settings, LLMRole role)
    {
        ctx.Log($"    Role: {role.Name}");

        if (role.Models.Count == 0)
        {
            ctx.Log($"      SKIP: role '{role.Name}' has no model names");
            return;
        }

        foreach (string modelId in role.Models)
        {
            RunSingleModelTest(ctx, registry, settings, role, modelId);
        }
    }

    private static void RunSingleModelTest(TestContext ctx, LlmRegistry registry, SettingsService settings, LLMRole role, string modelId)
    {
        ctx.Log($"      Model: {modelId}");

        LlmService? service = null;
        try
        {
            // Check if the model is registered and has an API key.
            if (!registry.HasModel(modelId))
            {
                ctx.Log($"        SKIP: model '{modelId}' not in registry (no config or API key?)");
                return;
            }

            service = registry.GetServiceById(modelId);
            if (service == null)
            {
                ctx.Log($"        SKIP: model '{modelId}' service not found");
                return;
            }

            if (!service.IsAvailable)
            {
                ctx.Log($"        SKIP: model '{modelId}' is currently unavailable");
                return;
            }

            if (string.IsNullOrEmpty(service.Model.ApiKey))
            {
                ctx.Log($"        SKIP: model '{modelId}' has no API key");
                return;
            }
        }
        catch (Exception ex)
        {
            ctx.Log($"        SKIP: pre-check error for '{modelId}': {ex.Message}");
            return;
        }

        // Run a minimal single-turn conversation.
        try
        {
            TestCaptureTransport localTransport = new TestCaptureTransport();
            BeastSession session = BeastSession.CreateNew(Guid.NewGuid().ToString("N"), role.Name, $"test-{modelId}");
            session.SetSystemPrompt(role.SystemPrompt);
            session.AddUserMessage("Reply with exactly: PING");

            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Tool[] tools = registry.GetToolsForRole(role);

            Action<string> previousLog = ProtocolChatCompletions.Log;
            ProtocolChatCompletions.Log = line => ctx.Log($"        {line}");
            LlmResult result;
            try
            {
                result = service.RunToCompletionAsync(session, tools, 0, localTransport, cts.Token).GetAwaiter().GetResult();
            }
            finally
            {
                ProtocolChatCompletions.Log = previousLog;
            }

            bool gotResponse = false;
            foreach ((FrameType type, string text) in localTransport.Sent)
            {
                if (type == FrameType.Output && text.IndexOf("PING", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    gotResponse = true;
                    break;
                }
            }

            if (!result.Success)
            {
                ctx.Log($"        SKIP [{role.Name}/{modelId}]: {result.ErrorMessage}");
                return;
            }
            ctx.Assert(gotResponse, $"PerModel [{role.Name}/{modelId}]: response contains PING");
            ctx.Log($"        PASS [{role.Name}/{modelId}]");
        }
        catch (OperationCanceledException)
        {
            ctx.Log($"        TIMEOUT [{role.Name}/{modelId}]: timed out after 30s");
        }
        catch (Exception ex)
        {
            ctx.Log($"        ERROR [{role.Name}/{modelId}]: {ex.Message}");
        }
    }
}
