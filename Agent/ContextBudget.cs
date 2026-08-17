using System;


// Central token accounting for one conversation's context window. Models the window as a heap:
// the measured conversation plus any outstanding tool-response reservations are "allocated"; the
// rest, minus the compaction reserve, is free for the next completion. Owned by Session so there is
// one obvious place that knows how much room the session has, instead of the math being re-derived
// inline at every call site.
//
// The budget never dictates how big an OUTPUT may be — not a tool's, not a subagent's, and not the
// model's own completion. It used to do all three by dividing up whatever room was left, which meant
// a nearly-full conversation asked a subagent for a review in six tokens and asked the model to write
// a file in 2,717. Both are impossible demands that produce useless work, and the truncated work then
// re-enters the context and confuses the model that produced it. Sizes are now decided by what the
// work needs; the budget's only job is to KNOW what that costs — ChargeToolResults records what came
// back, OutputAllowance states what the next answer needs — and to say when the window can no longer
// cover it, which is the signal to compact. Compaction exists precisely to make that room.
//
// A tool output the provider has not measured yet is charged the size stamped on the result (a
// sub-session's exact provider measurement, or the raw handler's own estimate) until the next
// response reports the true context size and RecordMeasurement folds it in.
public class ContextBudget
{
	// Output room assumed for a model that declares no ceiling of its own, so a response is always
	// both bounded and big enough to be worth having.
	private const int kDefaultOutputBudget = 4096;

	// Config, set at turn start. The model (and therefore the window/limits) can change between turns.
	private int _windowSize;
	private int _maxOutputTokens;
	private int _compactionReserve;
	private int _outputCap;

	// Authoritative context size from the last provider response.
	private int _measured;

	// Sum of the tool outputs appended since that response and not yet folded into a measurement.
	// Carried into the next request's sizing so input + output stays inside the window.
	private int _pendingReserve;

	public ContextBudget()
	{
	}

	// Returns the pending tool-response reservation (for pessimistic estimation before tracer call).
	public int PendingReserve => _pendingReserve;

	// Seeds the per-turn window and limits and the authoritative starting size. Clears any pending
	// reservation: a fresh turn begins with no outstanding tool outputs.
	public void Configure(int windowSize, int maxOutputTokens, int compactionReserve, int outputCap, int measuredContextSize)
	{
		_windowSize        = windowSize;
		_maxOutputTokens   = maxOutputTokens;
		_compactionReserve = compactionReserve;
		_outputCap         = outputCap;
		_measured          = measuredContextSize;
		_pendingReserve    = 0;
	}

	// The input we would send next — the measured conversation plus any not-yet-measured tool output
	// — already leaves no room above the compaction reserve. With a 10% reserve this is the 90% mark,
	// and it stays there: reserving a whole response on top would compact around 65% and throw away
	// a quarter of every window on turns that were only ever going to answer in a sentence. The rare
	// turn that genuinely needs more room than is left is caught when it happens, by the truncation
	// check in LlmService, rather than by making every turn pay for it in advance.
	public bool IsExhausted()
	{
		return _measured + _pendingReserve + _compactionReserve >= _windowSize;
	}

	// Structural overflow evidence: the measured conversation plus outstanding reservations fill
	// at least half the window. When a provider rejects a request with a client error in this
	// state, overflow is the dominant explanation — tokenizers disagree across providers and a
	// local server's real window (e.g. llama-server's -c) can be smaller than the configured one —
	// whereas a genuinely malformed request would have failed at low occupancy from the first
	// turn. Both figures are provider-measured or reserved, never estimated.
	public bool OverflowPlausible()
	{
		return _measured + _pendingReserve >= _windowSize / 2;
	}

	// Max output tokens to request next. The FLOOR is everything standing between the conversation
	// and the compaction line: whatever room exists before this session compacts anyway is room the
	// model may use in one answer, so the ceiling is never smaller than that. A configured ceiling
	// only ever raises it, never lowers it — a model that declares no output limit was being handed
	// the 4096-token default with 16k of window free, which truncates a large file write for no
	// reason at all. Models cannot see their own context size, so a truncated write reads to them as
	// a failed write: they start the file again from the top, the context grows, and the next attempt
	// is truncated sooner. That loop is the thing this floor exists to prevent.
	//
	// The only hard limit above it is physical: providers reject a request whose prompt plus
	// max_tokens overruns the window, so the result is clamped to what is genuinely left. Never
	// null: every request carries a limit.
	//
	// The floor needs a real measurement to stand on, and the FIRST request of a session has none:
	// _measured is 0 because only a provider response can set it, so "the room left" reads as the
	// whole window while the prompt about to be sent is not empty at all. Asking for the window on
	// top of that prompt is exactly the request providers reject (llama-server counts prompt +
	// max_tokens against -c), and the rejection reads as overflow, which compacts, which starts a
	// successor whose measurement is once again 0 — a compaction loop that never advances. Until a
	// response says how big this conversation is, the model's own allowance is the whole story.
	public int? MaxCompletionTokens()
	{
		long physical = _windowSize - _measured - _pendingReserve;
		if (physical <= 0)
			return 0;

		long want = OutputAllowance;
		if (_measured > 0)
		{
			// Room before the compaction line — the floor, since reaching that line compacts anyway.
			long toCompactionLine = physical - _compactionReserve;
			if (want < toCompactionLine)
				want = toCompactionLine;
		}
		if (want > physical)
			want = physical;

		return (int)want;
	}

	// The answer size this model asks for on its own account: its configured ceiling (or the default
	// when it has none), tightened by an explicit caller cap. A PREFERENCE, not a limit — the floor
	// above overrides it upward whenever the window has more room to give. Nothing here depends on
	// how full the conversation is, which is what makes it a usable reference point for "was that
	// response cut short because the window was tight, or because the model simply ran long?".
	public int OutputAllowance
	{
		get
		{
			int allowance = _maxOutputTokens > 0 ? _maxOutputTokens : kDefaultOutputBudget;
			if (_outputCap > 0 && _outputCap < allowance)
				allowance = _outputCap;
			return allowance;
		}
	}

	// Records a completed tool round's size against the window. Charging what actually came back can
	// push the pending total past the window, and that is the honest reading: the conversation really
	// is over its window, IsExhausted says so, and the turn loop compacts before the next request
	// rather than pretending the output was smaller than it is.
	public void ChargeToolResults(int tokens)
	{
		if (tokens > 0)
			_pendingReserve += tokens;
	}

	// A provider response reported the new context size, which already includes every tool output
	// appended since the last one. The pending reservations are now fully accounted for.
	public void RecordMeasurement(int exactContextSize)
	{
		_measured       = exactContextSize;
		_pendingReserve = 0;
	}
}