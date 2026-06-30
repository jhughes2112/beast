// Unit tests for ContextBudget — the central context-window accounting. The type is public, so the
// tests call it directly (no reflection). Values are observed through MaxCompletionTokens, which
// reflects both the measured size and the outstanding pending reservations.
public static class ContextBudgetTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  ContextBudgetTests");

		TestMaxCompletionTokens(ctx);
		TestIsExhausted(ctx);
		TestReserveHoldsFullReservation(ctx);
		TestRecordMeasurement(ctx);
		TestPendingReserve(ctx);
	}

	private static void TestMaxCompletionTokens(TestContext ctx)
	{
		// Clamped by the window's remaining room when the output ceiling is larger.
		ContextBudget a = new ContextBudget();
		a.Configure(1000, 5000, 0, 0, 900);
		ctx.AssertEqual<int?>(100, a.MaxCompletionTokens(), "MaxCompletionTokens: clamps to window remainder");

		// Clamped by the model's output ceiling when the window has plenty of room.
		ContextBudget b = new ContextBudget();
		b.Configure(100000, 4096, 0, 0, 1000);
		ctx.AssertEqual<int?>(4096, b.MaxCompletionTokens(), "MaxCompletionTokens: clamps to output ceiling");

		// Sub-session cap tightens further than the output ceiling.
		ContextBudget c = new ContextBudget();
		c.Configure(100000, 8192, 0, 2000, 1000);
		ctx.AssertEqual<int?>(2000, c.MaxCompletionTokens(), "MaxCompletionTokens: clamps to sub-session cap");

		// No output ceiling and no cap: unbounded, let the provider decide.
		ContextBudget d = new ContextBudget();
		d.Configure(100000, 0, 0, 0, 1000);
		ctx.AssertNull(d.MaxCompletionTokens(), "MaxCompletionTokens: null when unbounded");

		// No room left: zero, never negative.
		ContextBudget e = new ContextBudget();
		e.Configure(1000, 4096, 0, 0, 1000);
		ctx.AssertEqual<int?>(0, e.MaxCompletionTokens(), "MaxCompletionTokens: zero when window full");

		// The compaction reserve is held free: output is the window remainder MINUS the reserve.
		ContextBudget f = new ContextBudget();
		f.Configure(10000, 8000, 2000, 0, 1000);
		// available = 10000 - 1000 - 0 - 2000 = 7000.
		ctx.AssertEqual<int?>(7000, f.MaxCompletionTokens(), "MaxCompletionTokens: subtracts the compaction reserve");

		// When only the compaction reserve would be left, there is no room to complete.
		ContextBudget g = new ContextBudget();
		g.Configure(10000, 8000, 2000, 0, 8000);
		// available = 10000 - 8000 - 0 - 2000 = 0.
		ctx.AssertEqual<int?>(0, g.MaxCompletionTokens(), "MaxCompletionTokens: zero when only the compaction reserve remains");
	}

	private static void TestIsExhausted(TestContext ctx)
	{
		ContextBudget atLimit = new ContextBudget();
		atLimit.Configure(1000, 0, 100, 0, 900);
		ctx.Assert(atLimit.IsExhausted(), "IsExhausted: true when measured + reserve reaches window");

		ContextBudget under = new ContextBudget();
		under.Configure(1000, 0, 100, 0, 899);
		ctx.Assert(!under.IsExhausted(), "IsExhausted: false one token under the reserve boundary");
	}

	private static void TestReserveHoldsFullReservation(TestContext ctx)
	{
		// Window 100000, output ceiling large enough to be re-floored to the 4096 default reserve,
		// so the window remainder (not the ceiling) is what MaxCompletionTokens reports.
		ContextBudget budget = new ContextBudget();
		budget.Configure(100000, 200000, 0, 0, 1000);

		// round = 100000 - 1000(measured) - 4096(response reserve) - 0(compaction) = 94904.
		int perTool = budget.ReserveToolResponses(1);
		ctx.AssertEqual(94904, perTool, "ReserveToolResponses: per-tool budget from window remainder");

		// The whole reservation stays charged — never settled down to an estimate of what the tool
		// actually returned — so the room left for the completion holds steady until the next measurement.
		// available = 100000 - 1000 - 94904 - 0 = 4096.
		ctx.AssertEqual<int?>(4096, budget.MaxCompletionTokens(), "ReserveToolResponses: full reservation stays charged");

		// With a compaction reserve set, the round is smaller and the completion room excludes the
		// reserve, so it is genuinely held free on top of the response room.
		ContextBudget reserved = new ContextBudget();
		reserved.Configure(100000, 200000, 5000, 0, 1000);
		// round = 100000 - 1000 - 4096 - 5000 = 89904.
		int perTool2 = reserved.ReserveToolResponses(1);
		ctx.AssertEqual(89904, perTool2, "ReserveToolResponses: compaction reserve shrinks the tool round");
		// available = 100000 - 1000 - 89904 - 5000 = 4096.
		ctx.AssertEqual<int?>(4096, reserved.MaxCompletionTokens(), "ReserveToolResponses: completion room excludes the compaction reserve");
	}

	private static void TestRecordMeasurement(TestContext ctx)
	{
		ContextBudget budget = new ContextBudget();
		budget.Configure(100000, 200000, 0, 0, 1000);
		budget.ReserveToolResponses(1);

		// A provider response reports the true size and clears all pending reservations.
		budget.RecordMeasurement(2000);
		ctx.AssertEqual<int?>(98000, budget.MaxCompletionTokens(), "RecordMeasurement: resets measured size and zeroes and zeroes pending");
	}

	private static void TestPendingReserve(TestContext ctx)
	{
		ContextBudget budget = new ContextBudget();
		budget.Configure(100000, 200000, 0, 0, 1000);

		// Initially no pending reserve
		ctx.AssertEqual(0, budget.PendingReserve, "PendingReserve: zero initially");

		// After reserving tool responses, pending reserve is set
		budget.ReserveToolResponses(1);
		ctx.AssertEqual(94904, budget.PendingReserve, "PendingReserve: reflects tool response reservation");

		// After recording measurement, pending reserve is cleared
		budget.RecordMeasurement(2000);
		ctx.AssertEqual(0, budget.PendingReserve, "PendingReserve: zero after RecordMeasurement");
	}
}