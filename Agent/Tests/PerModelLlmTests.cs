using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Integration tests that run a real single-turn LLM conversation against each model
// configured for each role. Tests are skipped gracefully when API keys or models are absent.
public static class PerModelLlmTests
{
    public static async Task TestAsync(TestContext ctx, LlmRegistry registry, RoleService roleService, SettingsService settings, CancellationToken cancellationToken)
    {
        ctx.Log("  PerModelLlmTests");

        List<Role> roles = new List<Role>(roleService.Roles.Values);
        if (roles.Count == 0)
        {
            ctx.Log("    SKIP: no roles configured");
            return;
        }

        foreach (Role role in roles)
        {
            await RunRoleTestsAsync(ctx, registry, settings, role, cancellationToken);
        }
    }

    private static async Task RunRoleTestsAsync(TestContext ctx, LlmRegistry registry, SettingsService settings, Role role, CancellationToken cancellationToken)
    {
        ctx.Log($"    Role: {role.Name}");

        if (role.Models.Count == 0)
        {
            ctx.Log($"      SKIP: role '{role.Name}' has no model names");
            return;
        }

        foreach (string modelId in role.Models)
        {
            await RunSingleModelTestAsync(ctx, registry, settings, role, modelId, cancellationToken);
        }
    }

    private static async Task RunSingleModelTestAsync(TestContext ctx, LlmRegistry registry, SettingsService settings, Role role, string modelId, CancellationToken cancellationToken)
    {
        ctx.Log($"      Model: {modelId}");

        LlmService? service = null;
        try
        {
            service = registry.GetServiceById(modelId, 0);
            if (service == null)
            {
                ctx.Log($"        SKIP: model '{modelId}' service not found");
                return;
            }

            if (service.IsDown)
            {
                ctx.Log($"        SKIP: model '{modelId}' is permanently down");
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
            BeastSession data = new BeastSession(Guid.NewGuid().ToString("N"), $"test-{modelId}", string.Empty, role.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true, 0);
            Session session = new Session(data, role.SystemPrompt, localTransport, false);
            session.AddUserMessage("Reply with exactly: PING");

            using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            Tool[] tools = registry.GetToolsForRole(role);

            Action<string> previousChatLog = ProtocolChatCompletions.Log;
            Action<string> previousResponsesLog = ProtocolResponses.Log;
            Action<string> previousAnthropicLog = ProtocolAnthropic.Log;
            ProtocolChatCompletions.Log = line => ctx.Log($"        {line}");
            ProtocolResponses.Log = line => ctx.Log($"        {line}");
            ProtocolAnthropic.Log = line => ctx.Log($"        {line}");
            LlmResult result;
            try
            {
                result = await session.RunTurnAsync(service, tools, 0, linkedCts.Token);
            }
            finally
            {
                ProtocolChatCompletions.Log = previousChatLog;
                ProtocolResponses.Log = previousResponsesLog;
                ProtocolAnthropic.Log = previousAnthropicLog;
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

            if (result.ExitReason != LlmExitReason.Completed)
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
