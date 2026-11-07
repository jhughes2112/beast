using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;


// Cost rolls up from child to parent as deltas, and the amount already reported is persisted on the
// child. It used to be held by whichever handler ran the child, and every new handler (a human
// re-prompting a finished subagent, a compaction predecessor, a reload) started from zero and billed
// the parent the child's whole total again.
public static class CostRollupTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  CostRollupTests");

		TestRepeatedReportsBillOnce(ctx);
		TestReportedAmountSurvivesReload(ctx);
		TestGrandchildSpendReachesRoot(ctx);
		TestLegacyFileReadsAsReported(ctx);
		TestWorkRollsUpLikeCost(ctx);
		TestToolsReportTheirWork(ctx).GetAwaiter().GetResult();
	}

	private static void TestWorkRollsUpLikeCost(TestContext ctx)
	{
		TestCaptureTransport transport = new TestCaptureTransport();
		Session              root      = BuildSession("p",   transport);
		Session              child     = BuildSession("p_1", transport);

		child.RecordWork(5000);
		ctx.AssertEqual(5000L, child.ReportWorkTo(root), "WorkTime: the first report carries the child's time");
		ctx.AssertEqual(   0L, child.ReportWorkTo(root), "WorkTime: reporting again with no new work carries nothing");

		string        json   = JsonSerializer.Serialize(child.Data, BeastJson.Persist.BeastSession);
		BeastSession? loaded = JsonSerializer.Deserialize(json, BeastJson.Persist.BeastSession);
		ctx.AssertEqual(5000L, loaded?.WorkedMs ?? 0, "WorkTime: a session's time is kept in its file");
		if (loaded != null)
		{
			Session reloaded = new Session(loaded, string.Empty, transport, false);
			ctx.AssertEqual(0L, reloaded.ReportWorkTo(root), "WorkTime: a reloaded child does not report its time again");
			reloaded.RecordWork(250);
			ctx.AssertEqual(250L, reloaded.ReportWorkTo(root), "WorkTime: a reloaded child reports only its new time");
		}
		ctx.AssertEqual(5250L, root.WorkedMs, "WorkTime: the parent holds the child's time exactly once");

		JsonObject node = JsonNode.Parse(json)!.AsObject();
		node.Remove("workedMs");
		node.Remove("workReportedToParent");
		BeastSession? older = JsonSerializer.Deserialize(node.ToJsonString(), BeastJson.Persist.BeastSession);
		ctx.AssertEqual(0L, older?.WorkedMs ?? -1, "WorkTime: a file from before it existed reads as 0");
	}

	// A tool that works for workMs, waits on something else for waitMs, and has sub-calls that return subMs.
	private static Tool Worker(string name, int workMs, int waitMs, long subMs)
	{
		return new Tool
		{
			Definition = new ToolDefinition { Type = "function", Function = new FunctionDefinition { Name = name, Description = name, Parameters = new JsonObject() } },
			Handler    = async (args, id, ct, transport, sessionId, maxOutputTokens) =>
			{
				await System.Threading.Tasks.Task.Delay(workMs, ct);
				using (ToolClock.Wait())
					await System.Threading.Tasks.Task.Delay(waitMs, ct);
				if (subMs > 0)
					ToolClock.Report(BuildSession("unused", new TestCaptureTransport()), subMs);
				return new ToolResult(id, "done", string.Empty, 0, 1);
			}
		};
	}

	private static async System.Threading.Tasks.Task<long> RoundAsync(params (string Name, int WorkMs, int WaitMs, long SubMs)[] calls)
	{
		Session                session = BuildSession("p", new TestCaptureTransport());
		List<Tool>             tools   = new List<Tool>();
		List<SemanticToolCall> list    = new List<SemanticToolCall>();
		foreach ((string name, int workMs, int waitMs, long subMs) in calls)
		{
			tools.Add(Worker(name, workMs, waitMs, subMs));
			list.Add(new SemanticToolCall { Id = "c_" + name, Name = name, ArgumentsJson = "{}" });
		}
		ProtocolCallPayload payload = new ProtocolCallPayload(string.Empty, string.Empty, list, new List<ToolResult>(), "tool_calls", new TokenUsageInfo(), 0m);
		await ToolDispatch.DispatchAsync(payload, tools.ToArray(), session, new TestCaptureTransport(), System.Threading.CancellationToken.None);
		return session.WorkedMs;
	}

	private static async System.Threading.Tasks.Task TestToolsReportTheirWork(TestContext ctx)
	{
		long plain = await RoundAsync(("bash", 300, 0, 0));
		ctx.Assert(plain >= 250 && plain < 1000, $"WorkTime: a tool's running time is counted (got {plain} ms)");
		long parallel = await RoundAsync(("bash", 300, 0, 0), ("read_file", 300, 0, 0));
		ctx.Assert(parallel >= 500 && parallel < 1500, $"WorkTime: calls in a round each add their own time, however much they overlap (got {parallel} ms)");
		long waiting = await RoundAsync(("any_tool", 200, 1000, 0));
		ctx.Assert(waiting >= 150 && waiting < 700, $"WorkTime: time a tool spends waiting on something else is not counted (got {waiting} ms)");
		long sub = await RoundAsync(("any_tool", 200, 1000, 60000));
		ctx.Assert(sub >= 60150 && sub < 60700, $"WorkTime: what a sub-call returns adds to the tool's own time (got {sub} ms)");
	}

	private static Session BuildSession(string id, TestCaptureTransport transport)
	{
		BeastSession data = new BeastSession(id, id, "m", "r", string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		return new Session(data, string.Empty, transport, false);
	}

	private static void TestRepeatedReportsBillOnce(TestContext ctx)
	{
		TestCaptureTransport transport = new TestCaptureTransport();
		Session              parent    = BuildSession("p",   transport);
		Session              child     = BuildSession("p_1", transport);

		child.RecordCost(1.00m);
		ctx.AssertEqual(1.00m, child.ReportCostTo(parent), "CostRollup: the first report carries the child's spend");
		ctx.AssertEqual(   0m, child.ReportCostTo(parent), "CostRollup: reporting again with no new spend carries nothing");

		// The human goes back and prompts the finished subagent.
		child.RecordCost(0.50m);
		ctx.AssertEqual(0.50m, child.ReportCostTo(parent), "CostRollup: a re-prompt reports only the new spend");
		ctx.AssertEqual(1.50m,           parent.TotalCost, "CostRollup: the parent holds the child's spend exactly once");
	}

	private static void TestReportedAmountSurvivesReload(TestContext ctx)
	{
		TestCaptureTransport transport = new TestCaptureTransport();
		Session              parent    = BuildSession("p",   transport);
		Session              child     = BuildSession("p_1", transport);

		child.RecordCost(2.00m);
		child.ReportCostTo(parent);

		string        json   = JsonSerializer.Serialize(child.Data, BeastJson.Persist.BeastSession);
		BeastSession? loaded = JsonSerializer.Deserialize(json, BeastJson.Persist.BeastSession);
		ctx.AssertNotNull(loaded, "CostRollup: the child deserializes");
		if (loaded != null)
		{
			ctx.AssertEqual(2.00m, loaded.CostReportedToParent, "CostRollup: the reported amount is persisted");
			Session reloaded = new Session(loaded, string.Empty, transport, false);
			ctx.AssertEqual(0m, reloaded.ReportCostTo(parent), "CostRollup: a reloaded child does not bill the parent again");
			reloaded.RecordCost(0.25m);
			ctx.AssertEqual(0.25m, reloaded.ReportCostTo(parent), "CostRollup: a reloaded child reports only its new spend");
		}
		ctx.AssertEqual(2.25m, parent.TotalCost, "CostRollup: the parent's total is right across the reload");
	}

	private static void TestGrandchildSpendReachesRoot(TestContext ctx)
	{
		TestCaptureTransport transport  = new TestCaptureTransport();
		Session              root       = BuildSession("p",     transport);
		Session              child      = BuildSession("p_1",   transport);
		Session              grandchild = BuildSession("p_1_1", transport);

		child.RecordCost(1.00m);
		grandchild.RecordCost(3.00m);
		grandchild.ReportCostTo(child);
		child.ReportCostTo(root);

		// The grandchild is re-prompted after everything above it has already reported.
		grandchild.RecordCost(0.10m);
		grandchild.ReportCostTo(child);
		child.ReportCostTo(root);
		ctx.AssertEqual(4.10m, root.TotalCost, "CostRollup: a grandchild's later spend reaches the root once");
	}

	private static void TestLegacyFileReadsAsReported(TestContext ctx)
	{
		BeastSession data = new BeastSession("p_1", "p_1", "m", "r", string.Empty, new List<CanonicalMessage>(), null, 5.00m, 0, 0, 0, false);
		JsonObject   node = JsonNode.Parse(JsonSerializer.Serialize(data, BeastJson.Persist.BeastSession))!.AsObject();
		node.Remove("costReportedToParent");

		BeastSession? loaded = JsonSerializer.Deserialize(node.ToJsonString(), BeastJson.Persist.BeastSession);
		ctx.AssertEqual(5.00m, loaded?.CostReportedToParent ?? 0m, "CostRollup: a file from before the field existed reads as already reported");
	}
}