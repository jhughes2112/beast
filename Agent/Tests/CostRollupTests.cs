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