using System;


// Central token accounting for one conversation's context window. Models the window as a heap:
// the measured conversation plus any outstanding tool-response reservations are "allocated"; the
// rest, minus the compaction reserve, is free for the next completion. Owned by Session so there is
// one obvious place that knows how much room the session has, instead of the math being re-derived
// inline at every call site.
//
// Every number the budget reasons about is either a provider-reported measurement (the context size)
// or a reservation we ourselves allocated and truncated a tool output to fit — never a token-count
// estimate. A tool output the provider has not measured yet is charged the FULL reservation it was
// handed (a guaranteed upper bound) until the next response reports the exact new context size and
// RecordMeasurement folds it in. So the figures used to decide whether a request fits are always
// conservative: the real input can never exceed measured + pendingReserve.
public class ContextBudget
{
	// Room kept free for the next response when neither an output limit nor a sub-session cap is set,
	// so a round of tool outputs can never fill the window and size the next request to zero output.
	private const int kDefaultOutputBudget = 4096;

	// Config, set at turn start. The model (and therefore the window/limits) can change between turns.
	private int _windowSize;
	private int _maxOutputTokens;
	private int _compactionReserve;
	private int _outputCap;

	// Authoritative context size from the last provider response.
	private int _measured;

	// Sum of tool-response reservations appended since that response and not yet folded into a
	// measurement. Carried into the next request's sizing so input + output stays inside the window.
	private int _pendingReserve;

	public ContextBudget()
	{
	}

	// Seeds the per-turn window and limits and the authoritative starting size. Clears any pending
	// reservation: a fresh turn begins with no outstanding tool outputs.
	public void Configure(int windowSize, int maxOutputTokens, int compactionReserve, int outputCap, int measuredContextSize)
	{
		_windowSize = windowSize;
		_maxOutputTokens = maxOutputTokens;
		_compactionReserve = compactionReserve;
		_outputCap = outputCap;
		_measured = measuredContextSize;
		_pendingReserve = 0;
	}

	// The input we would send next — the measured conversation plus any not-yet-measured tool
	// reservations — already leaves no room above the compaction reserve. Including the pending
	// reservation here is what keeps a tool-heavy round from passing the gate and then overflowing.
	public bool IsExhausted()
	{
		return _measured + _pendingReserve + _compactionReserve >= _windowSize;
	}

	// Max output tokens to request next: whatever the window has left after the measured conversation,
	// any pending tool outputs, AND the compaction reserve, tightened by the model's output ceiling and
	// the sub-session cap. Subtracting the compaction reserve is what keeps it genuinely free, so input
	// + output always lands at least that far inside the window. Null means "unbounded" — let the
	// provider use its own default — and only happens when neither bound is configured.
	public int? MaxCompletionTokens()
	{
		long available = _windowSize - _measured - _pendingReserve - _compactionReserve;
		if (available <= 0)
			return 0;

		int? result = null;
		if (_maxOutputTokens > 0)
			result = (int)Math.Min(available, _maxOutputTokens);
		if (_outputCap > 0)
			result = result.HasValue ? Math.Min(result.Value, _outputCap) : (int)Math.Min(available, _outputCap);

		return result;
	}

	// Allocates the round's tool-response budget out of the room left after the response reserve and
	// the compaction reserve, split evenly across the calls so the combined outputs always fit. Returns
	// the per-tool budget (which each tool output is truncated to fit) and records the WHOLE round as
	// pending. The reservation is held in full — never reduced by an estimate of what a tool actually
	// returned — until the next provider response measures the real size, so the budget can only ever
	// over-count outstanding tool output, never under-count it.
	public int ReserveToolResponses(int count)
	{
		if (count <= 0)
			return 0;

		int round = Math.Max(0, _windowSize - _measured - ResponseReserve - _compactionReserve);
		int perTool = round / count;
		_pendingReserve += perTool * count;
		return perTool;
	}

	// A provider response reported the new context size, which already includes every tool output
	// appended since the last one. The pending reservations are now fully accounted for.
	public void RecordMeasurement(int exactContextSize)
	{
		_measured = exactContextSize;
		_pendingReserve = 0;
	}

	// Room held free for the model's next response: its configured output ceiling, tightened by the
	// sub-session cap, falling back to the default floor when neither is usable.
	private int ResponseReserve
	{
		get
		{
			int budget = _maxOutputTokens > 0 ? _maxOutputTokens : 0;
			if (_outputCap > 0 && (budget == 0 || _outputCap < budget))
				budget = _outputCap;
			if (budget == 0 || budget >= _windowSize)
				budget = kDefaultOutputBudget;
			return budget;
		}
	}
}