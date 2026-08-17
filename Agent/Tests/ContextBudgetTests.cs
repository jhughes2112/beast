// Unit tests for ContextBudget — the central context-window accounting. The type is public, so the
// tests call it directly (no reflection). Values are observed through MaxCompletionTokens, which
// reflects both the measured size and the outstanding charges for unmeasured tool output.
public static class ContextBudgetTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  ContextBudgetTests");

		TestMaxCompletionTokens(ctx);
		TestIsExhausted(ctx);
		TestChargeToolResults(ctx);
		TestRecordMeasurement(ctx);
		TestPendingReserve(ctx);
		TestOverflowPlausible(ctx);
	}

	private static void TestOverflowPlausible(TestContext ctx)
	{
		// At half occupancy a client rejection reads as overflow; below it, it does not.
		ContextBudget half = new ContextBudget();
		half.Configure(32000, 0, 0, 0, 16000);
		ctx.Assert(half.OverflowPlausible(), "OverflowPlausible: true at half the window");

		ContextBudget low = new ContextBudget();
		low.Configure(32000, 0, 0, 0, 1000);
		ctx.Assert(!low.OverflowPlausible(), "OverflowPlausible: false at low occupancy");

		// Pending tool reservations count as occupancy — they are real appended content the
		// provider has not measured yet.
		ContextBudget pending = new ContextBudget();
		pending.Configure(32000, 4096, 0, 0, 12000);
		pending.ChargeToolResults(5000);
		ctx.Assert(pending.OverflowPlausible(), "OverflowPlausible: pending tool output counts toward occupancy");

		// A fresh, never-measured conversation offers no structural evidence.
		ContextBudget fresh = new ContextBudget();
		fresh.Configure(32000, 0, 0, 0, 0);
		ctx.Assert(!fresh.OverflowPlausible(), "OverflowPlausible: false with no measurement");
	}

	private static void TestMaxCompletionTokens(TestContext ctx)
	{
		// The floor: everything up to the compaction line is the model's to use in one answer. With a
		// 32768 window, a 3276 reserve and 1000 measured, that is 28492 — and the model's own 8192
		// preference does NOT lower it. A large file write gets the whole window it has coming.
		ContextBudget filling = new ContextBudget();
		filling.Configure(32768, 8192, 3276, 0, 1000);
		ctx.AssertEqual<int?>(28492, filling.MaxCompletionTokens(), "MaxCompletionTokens: never less than the room before the compaction line");
		ctx.AssertEqual(8192, filling.OutputAllowance, "OutputAllowance: the model's own preference, unchanged");

		// The default ceiling is the case that actually bit: a model declaring no output limit was
		// handed 4096 with most of a 32k window free, truncating large writes for no reason.
		ContextBudget noCeiling = new ContextBudget();
		noCeiling.Configure(32768, 0, 3276, 0, 13000);
		ctx.AssertEqual<int?>(16492, noCeiling.MaxCompletionTokens(), "MaxCompletionTokens: an unconfigured model gets the free window, not the 4096 default");
		ctx.Assert(noCeiling.MaxCompletionTokens() > 4096, "MaxCompletionTokens: the default never caps below the free room");

		// Close to the line, the floor shrinks with it and the model's own ceiling takes over —
		// still clamped to what physically remains, because the provider rejects anything more.
		ContextBudget nearFull = new ContextBudget();
		nearFull.Configure(32768, 8192, 3276, 0, 29000);
		ctx.AssertEqual<int?>(3768, nearFull.MaxCompletionTokens(), "MaxCompletionTokens: clamped to the physical remainder near the top of the window");
		ctx.Assert(nearFull.MaxCompletionTokens() < nearFull.OutputAllowance, "MaxCompletionTokens: the shortfall is detectable, which is what flags a truncated reply");

		// An explicit caller cap only lowers the model's own preference; it cannot lower the floor,
		// which exists to stop truncation rather than to express anyone's preference.
		ContextBudget c = new ContextBudget();
		c.Configure(100000, 8192, 0, 2000, 1000);
		ctx.AssertEqual(2000, c.OutputAllowance, "OutputAllowance: an explicit cap tightens the preference");

		// The first request of a session has no measurement to reason from, so the floor does not
		// apply: the prompt is not empty just because nobody has counted it yet. This is the exact
		// shape that looped in the field — a compaction successor asked for 29492 of a 32768 window
		// on top of a 7438-token prompt, llama-server rejected the sum, the rejection read as
		// overflow, and the resulting successor did the same thing eleven times over.
		ContextBudget unmeasured = new ContextBudget();
		unmeasured.Configure(32768, 0, 3276, 0, 0);
		ctx.AssertEqual<int?>(4096, unmeasured.MaxCompletionTokens(), "MaxCompletionTokens: an unmeasured conversation asks only for its own allowance");
		ctx.Assert(unmeasured.MaxCompletionTokens() < 32768 - 7438, "MaxCompletionTokens: the first request leaves room for a prompt nobody has measured");

		// The floor switches on as soon as a response says how big the conversation really is.
		unmeasured.RecordMeasurement(1000);
		ctx.AssertEqual<int?>(28492, unmeasured.MaxCompletionTokens(), "MaxCompletionTokens: the floor applies once the size is known");

		// A window with nothing left asks for nothing, rather than a negative number.
		ContextBudget spent = new ContextBudget();
		spent.Configure(1000, 4096, 100, 0, 1000);
		ctx.AssertEqual<int?>(0, spent.MaxCompletionTokens(), "MaxCompletionTokens: zero when the window is physically full");
	}

	// Compaction triggers at the compaction reserve — with the standard 10% reserve, the 90% mark —
	// and NOT before. Reserving a whole response on top would compact around 65% and spend a quarter
	// of every window on turns that were only ever going to answer in a sentence.
	private static void TestIsExhausted(TestContext ctx)
	{
		ContextBudget atLimit = new ContextBudget();
		atLimit.Configure(1000, 250, 100, 0, 900);
		ctx.Assert(atLimit.IsExhausted(), "IsExhausted: true when measured + reserve reaches the window");

		ContextBudget under = new ContextBudget();
		under.Configure(1000, 250, 100, 0, 899);
		ctx.Assert(!under.IsExhausted(), "IsExhausted: false one token under the reserve boundary");

		// A 32k window with a 10% reserve runs to ~90% before compacting, so the ordinary turns in
		// between are never interrupted for room they were not going to use.
		ContextBudget realistic = new ContextBudget();
		realistic.Configure(32768, 8192, 3276, 0, 26000);
		ctx.Assert(!realistic.IsExhausted(), "IsExhausted: a 32k session at 79% keeps working");
		realistic.Configure(32768, 8192, 3276, 0, 29500);
		ctx.Assert(realistic.IsExhausted(), "IsExhausted: and compacts at 90%");
	}

	// The budget never rations tool output; it records what came back. A round bigger than the room
	// left is charged in full and simply reads as exhausted — that is the honest state, and it is
	// what routes the turn into compaction instead of into an impossible request.
	private static void TestChargeToolResults(TestContext ctx)
	{
		ContextBudget budget = new ContextBudget();
		budget.Configure(100000, 4096, 0, 0, 1000);

		budget.ChargeToolResults(20000);
		ctx.AssertEqual(20000, budget.PendingReserve, "ChargeToolResults: charges the round at its actual size");
		ctx.Assert(!budget.IsExhausted(), "ChargeToolResults: a round the window can still cover is not exhausting");

		// A single output larger than the whole remaining window is still charged at its real size
		// rather than being pretended smaller: the conversation IS over, and says so.
		ContextBudget overrun = new ContextBudget();
		overrun.Configure(32768, 4096, 3276, 0, 20000);
		overrun.ChargeToolResults(40000);
		ctx.Assert(overrun.IsExhausted(), "ChargeToolResults: an oversized output reads as exhausted");
		ctx.AssertEqual<int?>(0, overrun.MaxCompletionTokens(), "ChargeToolResults: no completion room once the window is overrun");

		// Zero and negative charges are no-ops, so an empty round cannot corrupt the accounting.
		ContextBudget empty = new ContextBudget();
		empty.Configure(100000, 4096, 0, 0, 1000);
		empty.ChargeToolResults(0);
		ctx.AssertEqual(0, empty.PendingReserve, "ChargeToolResults: an empty round charges nothing");
	}

	private static void TestRecordMeasurement(TestContext ctx)
	{
		ContextBudget budget = new ContextBudget();
		budget.Configure(100000, 200000, 0, 0, 1000);
		budget.ChargeToolResults(30000);

		// A provider response reports the true size and clears all pending charges.
		budget.RecordMeasurement(2000);
		ctx.AssertEqual(0, budget.PendingReserve, "RecordMeasurement: resets measured size and zeroes pending");
		ctx.Assert(!budget.IsExhausted(), "RecordMeasurement: the measured size is what fullness is judged on");
	}

	private static void TestPendingReserve(TestContext ctx)
	{
		ContextBudget budget = new ContextBudget();
		budget.Configure(100000, 200000, 0, 0, 1000);

		// Initially no pending reserve
		ctx.AssertEqual(0, budget.PendingReserve, "PendingReserve: zero initially");

		// A charged round accumulates: two tool results in the same turn both count.
		budget.ChargeToolResults(1500);
		budget.ChargeToolResults(2500);
		ctx.AssertEqual(4000, budget.PendingReserve, "PendingReserve: accumulates every charged tool output");

		// After recording measurement, pending reserve is cleared
		budget.RecordMeasurement(2000);
		ctx.AssertEqual(0, budget.PendingReserve, "PendingReserve: zero after RecordMeasurement");
	}
}