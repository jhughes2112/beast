using System;
using System.Collections.Generic;
using System.IO;


// Unit tests for WorkflowService, data model loading, and truth matching.
public static class WorkflowTests
{
    public static void Test(TestContext ctx)
    {
        ctx.Log("  WorkflowTests");
        TestDefaultWorkflowCreated(ctx);
        TestGetWorkflowByName(ctx);
        TestGetRoleForState(ctx);
        TestGetInitialStateName(ctx);
        TestMissingWorkflowReturnsNull(ctx);
        TestMissingStateReturnsNull(ctx);
        TestCaseInsensitiveLookup(ctx);
        TestMultiStateWorkflow(ctx);
        TestReload(ctx);
        TestStateEvaluatorRole(ctx);
        TestQueryAndTruths(ctx);
    }

    private static WorkflowService MakeService(string tempDir)
    {
        return new WorkflowService(tempDir);
    }

    private static void TestDefaultWorkflowCreated(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);
            Workflow? def = svc.GetWorkflow("default");
            ctx.AssertNotNull(def, "default workflow is created when dir does not exist");
            ctx.Assert(def!.States.Count > 0, "default workflow has at least one state");
            ctx.AssertEqual("default", def!.States[0].Name, "default state name is 'default'");
            ctx.AssertEqual("Default", def!.States[0].Role, "default state role is 'Default'");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestGetWorkflowByName(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);
            Workflow? found = svc.GetWorkflow("default");
            ctx.AssertNotNull(found, "GetWorkflow returns the default workflow");
            ctx.AssertNull(svc.GetWorkflow("nonexistent"), "GetWorkflow returns null for unknown workflow");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestGetRoleForState(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);
            string? role = svc.GetRoleForState("default", "default");
            ctx.AssertEqual("Default", role, "GetRoleForState returns 'Default' for default workflow/state");
            ctx.AssertNull(svc.GetRoleForState("nonexistent", "default"), "GetRoleForState null for unknown workflow");
            ctx.AssertNull(svc.GetRoleForState("default", "nonexistent"), "GetRoleForState null for unknown state");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestGetInitialStateName(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);
            string? first = svc.GetInitialStateName("default");
            ctx.AssertEqual("default", first, "GetInitialStateName returns 'default' for default workflow");
            ctx.AssertNull(svc.GetInitialStateName("nonexistent"), "GetInitialStateName null for unknown workflow");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestMissingWorkflowReturnsNull(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);
            ctx.AssertNull(svc.GetWorkflow("missing"), "missing workflow returns null");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestMissingStateReturnsNull(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);
            ctx.AssertNull(svc.GetRoleForState("default", "missing"), "missing state returns null role");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestCaseInsensitiveLookup(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);
            ctx.AssertNotNull(svc.GetWorkflow("DEFAULT"), "workflow lookup is case-insensitive (uppercase)");
            ctx.AssertNotNull(svc.GetWorkflow("Default"), "workflow lookup is case-insensitive (mixed)");
            string? role = svc.GetRoleForState("DEFAULT", "DEFAULT");
            ctx.AssertEqual("Default", role, "GetRoleForState is case-insensitive");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestMultiStateWorkflow(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);

            string wfDir = Path.Combine(tempDir, ".beast", "workflows");
            string wfJson = "{\"id\":\"multi\",\"stateEvaluatorRole\":\"Evaluator\",\"states\":[" +
                "{\"name\":\"plan\",\"role\":\"Planner\"}," +
                "{\"name\":\"code\",\"role\":\"Coder\"}" +
                "]}";
            File.WriteAllText(Path.Combine(wfDir, "multi.json"), wfJson);
            svc.Reload();

            Workflow? wf = svc.GetWorkflow("multi");
            ctx.AssertNotNull(wf, "multi-state workflow loaded");
            ctx.AssertEqual(2, wf!.States.Count, "multi-state workflow has 2 states");
            ctx.AssertEqual("plan", svc.GetInitialStateName("multi"), "initial state is 'plan'");
            ctx.AssertEqual("Planner", svc.GetRoleForState("multi", "plan"), "plan state role");
            ctx.AssertEqual("Coder", svc.GetRoleForState("multi", "code"), "code state role");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestReload(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);
            ctx.AssertNull(svc.GetWorkflow("fresh"), "fresh workflow not present before write");

            string wfDir = Path.Combine(tempDir, ".beast", "workflows");
            string wfJson = "{\"id\":\"fresh\",\"states\":[{\"name\":\"start\",\"role\":\"Test\"}]}";
            File.WriteAllText(Path.Combine(wfDir, "fresh.json"), wfJson);
            svc.Reload();

            ctx.AssertNotNull(svc.GetWorkflow("fresh"), "fresh workflow present after reload");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestStateEvaluatorRole(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);

            // Default workflow has empty StateEvaluatorRole.
            ctx.AssertNull(svc.GetStateEvaluatorRole("default"), "default workflow has no evaluator role");
            ctx.AssertNull(svc.GetStateEvaluatorRole("nonexistent"), "unknown workflow returns null evaluator role");

            string wfDir = Path.Combine(tempDir, ".beast", "workflows");
            string wfJson = "{\"id\":\"qa\",\"stateEvaluatorRole\":\"QAEvaluator\",\"states\":[{\"name\":\"test\",\"role\":\"Dev\"}]}";
            File.WriteAllText(Path.Combine(wfDir, "qa.json"), wfJson);
            svc.Reload();

            ctx.AssertEqual("QAEvaluator", svc.GetStateEvaluatorRole("qa"), "evaluator role loaded from JSON");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void TestQueryAndTruths(TestContext ctx)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            WorkflowService svc = MakeService(tempDir);

            string wfDir = Path.Combine(tempDir, ".beast", "workflows");
            string wfJson = "{\"id\":\"dev\",\"stateEvaluatorRole\":\"Evaluator\",\"states\":[{" +
                "\"name\":\"testing\"," +
                "\"role\":\"QAEngineer\"," +
                "\"query\":\"Run the test suite and check for errors.\"," +
                "\"truths\":{\"No errors\":\"CommitState\",\"Some errors\":\"FixState\"}" +
                "}]}";
            File.WriteAllText(Path.Combine(wfDir, "dev.json"), wfJson);
            svc.Reload();

            Workflow? wf = svc.GetWorkflow("dev");
            ctx.AssertNotNull(wf, "dev workflow loaded");
            WorkflowState? state = wf!.GetState("testing");
            ctx.AssertNotNull(state, "testing state found");
            ctx.AssertEqual("Run the test suite and check for errors.", state!.Query, "query loaded");
            ctx.Assert(state.Truths.Count == 2, "truths dictionary has 2 entries");
            ctx.Assert(state.Truths.ContainsKey("No errors"), "truth 'No errors' present");
            ctx.AssertEqual("CommitState", state.Truths["No errors"], "truth 'No errors' maps to CommitState");
            ctx.Assert(state.Truths.ContainsKey("Some errors"), "truth 'Some errors' present");
            ctx.AssertEqual("FixState", state.Truths["Some errors"], "truth 'Some errors' maps to FixState");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

}
