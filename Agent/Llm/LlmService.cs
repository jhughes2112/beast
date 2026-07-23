using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public enum LlmExitReason
{
	Completed, ContextFull, Failed, Interrupted
}

// Reports running token counts and protocol-computed cost for the current in-flight assistant
// turn while a response streams. Provisional; superseded at commit by the authoritative payload.
public delegate void LiveUsageProgress(int inputTokens, int outputTokens, decimal turnCost, int cachedTokens);

// Result returned by LlmService after running a conversation to completion.
public class LlmResult
{
	public LlmExitReason ExitReason { get; }
	public string ErrorMessage { get; }
	// Provider finish/stop reason of the completing turn ("length"/"max_tokens" when the response
	// was cut off by the output limit). Empty for non-completing exits.
	public string FinishReason { get; }

	public LlmResult(LlmExitReason exitReason, string errorMessage)
		: this(exitReason, errorMessage, string.Empty)
	{
	}

	public LlmResult(LlmExitReason exitReason, string errorMessage, string finishReason)
	{
		ExitReason = exitReason;
		ErrorMessage = errorMessage;
		FinishReason = finishReason;
	}
}

// Manages an LLM provider and drives a conversation to completion with tool calling.
// Protocols own the native assistant turn (they write into BeastSession.<protocol>State directly
// and fan out to the other listeners). LlmService is concerned only with running the loop,
// dispatching tools, handling XML-tool-call fallback, and surfacing terminal results.
public class LlmService
{
	private readonly ProtocolProxy _handler;
	private readonly LlmModel _model;
	private readonly ModelAvailability _availability;

	// The role's flat model list this service belongs to. The service still represents exactly one model
	// (Model); the list just lets a caller ask the registry for the next lower-ranked model when this one is
	// sustained-rate-limited, and build a fresh service for it the same way /model does. A standalone
	// single-model service (e.g. web search) carries just its own id, so it never falls back.
	private readonly IReadOnlyList<string> _roleModelIds;

	public LlmModel Model => _model;
	public bool IsDown => _availability.IsDown;

	// The role's model list, read by the registry to find this service's slot and build the next fallback.
	public IReadOnlyList<string> RoleModelIds => _roleModelIds;

	// detectedProtocol is resolved by LlmRegistry.ProbeEndpointsAsync before any session starts.
	// availability is shared across all service instances for the same model so rate-limit and
	// down state set by one session are visible to others picking that model next. roleModelIds is the
	// role's priority list this model came from, so fallback can advance past it.
	public LlmService(LlmModel model, DetectedProtocol detectedProtocol, ModelAvailability availability, IReadOnlyList<string> roleModelIds)
	{
		_model = model;
		_availability = availability;
		_handler = new ProtocolProxy(model, detectedProtocol);
		_roleModelIds = roleModelIds;
	}

	// Tracer call: probe the provider with the dedicated token-counting endpoint (Anthropic /count_tokens,
// OpenAI Responses /responses/input_tokens/count) to get accurate input/cached token counts
// without generating a response. Falls back to max_output_tokens=1 for Chat Completions.
// Returns TracerResult with token counts or error status. Used before the real call to decide whether compaction is needed.
	public async Task<TracerResult> RunTracerAsync(Session conversation, Tool[] tools, string? forcedToolName, CancellationToken cancellationToken)
	{
		if (_availability.IsDown)
			return TracerResult.Failed($"LLM {_model.Config.Name} is permanently down");

		List<ToolDefinition> toolDefs = new List<ToolDefinition>();
		for (int i = 0; i < tools.Length; i++)
		{
			toolDefs.Add(tools[i].Definition);
		}

		return await _handler.CountTokensAsync(conversation.Bundle, toolDefs, forcedToolName, conversation.QueryLog, cancellationToken);
	}

	// forcedToolName (null = free choice) requires the model to call that exact tool this turn.
	// maxOutputCap (0 = none) hard-limits each response's max_tokens for capped sub-session retries.
	// guaranteed to fit the calling agent's allotted space without any post-hoc truncation.
	// Handles retry logic, rate limiting, and budget exhaustion. Returns ProtocolResult on each
	// successful call. Context-full detection happens in CommitTurn when adding content.
	// yieldOnInput: when true, a retry backoff is interrupted the moment session input arrives and
	// the call returns Yielded so the caller can drain it (a queued /model applies) and re-enter.
	// Callers whose sessions receive no interactive input (summarizer, web search, tests) pass
	// false — their pending queue never drains mid-call, so yielding would spin.
	public async Task<ProtocolResult> RunToCompletionAsync(Session conversation, Tool[] tools, string? forcedToolName, int reserveTokens, int maxOutputCap, bool yieldOnInput, ITransportServer transport, CancellationToken cancellationToken)
	{
		ProtocolResult result = ProtocolResult.Failed($"LLM {_model.Config.Name} is permanently down");

		if (_availability.IsDown == false)
		{
			CancellationToken turnToken = conversation.BeginTurn();
			bool interrupted = false;
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(turnToken, cancellationToken);
			try
			{
				List<ToolDefinition> toolDefs = new List<ToolDefinition>();
				for (int i = 0; i < tools.Length; i++)
				{
					toolDefs.Add(tools[i].Definition);
				}

				int rateLimitRetries = 0;
				int transientRetries = 0;
				const int kMaxRateLimitRetries = 10;
				const int kMaxTransientRetries = 5;

				// Central context accounting for this turn. Seeded with the model's window and limits, the
				// compaction reserve, and the sub-session output cap. It owns all "how much room is left"
				// math: the context-full gate, completion sizing, and per-tool response reservations.
				ContextBudget budget = conversation.Budget;
				budget.Configure(_model.Config.ContextWindow, _model.Config.MaxOutputTokens, reserveTokens, maxOutputCap, conversation.ContextLength);

				for (; ; )
				{
					// Honor any backoff a prior attempt (or a prior turn) recorded — a rate-limit RetryAfter or
					// a transient-error backoff — before making the next call. Both retry budgets are bounded, so
					// an exhausted budget escalates to TooManyRetries below rather than parking here forever.
					// The wait is chunked so queued input can interrupt it: without this, a session waiting out
					// minutes of rate-limit backoff was deaf to /model and steering until the backoff elapsed.
					if (_availability.AvailableAt > DateTimeOffset.UtcNow)
					{
						bool inputArrived = false;
						while (_availability.AvailableAt > DateTimeOffset.UtcNow)
						{
							if (yieldOnInput && conversation.HasPending)
							{
								inputArrived = true;
								break;
							}
							TimeSpan remaining = _availability.AvailableAt - DateTimeOffset.UtcNow;
							TimeSpan slice = remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250);
							if (slice > TimeSpan.Zero)
								await Task.Delay(slice, linked.Token);
						}
						if (inputArrived)
						{
							result = ProtocolResult.Yielded();
							break;
						}
					}

					if (budget.IsExhausted())
					{
						// Context-full is a caller-side constraint, not a model failure.
						// Detection happens in CommitTurn when ADDING content to the session.
						result = ProtocolResult.ContextFull("Context budget exhausted");
						conversation.QueryLog.ModelFailure(_model, _handler, "ContextFull", null, "Context budget exhausted", 0, 0, null, false);
						break;
					}

					// Size the response against the exact context plus any not-yet-reported tool outputs,
					// so input + output stays within the window even right after a tool round.
					int? maxCompletionTokens = budget.MaxCompletionTokens();

					// Provisional live stats while the turn streams. Protocols report inputTokens as the
					// whole-conversation input the provider bills this turn (Anthropic via StreamStart
					// usage, Responses via response.created usage), which is exactly what the committed
					// frame reports as promptTokens. outputTokens is the per-turn completion count. The
					// displayed in/out counters are the absolute session totals, so the live frame adds
					// the in-flight turn on top of the persisted cumulative baselines; contextTokens
					// stays as the current context occupancy. These are superseded at commit by the
					// authoritative cumulative values.
					decimal costBaseline = conversation.TotalCost; // locals captured by closure for provisional stats
					int contextBaseline = conversation.ContextLength;
					int inputBaseline = conversation.CumulativeInputTokens;
					int outputBaseline = conversation.CumulativeOutputTokens;
					string modelId = conversation.Model + ReasoningEffort.DisplaySuffix(_model.Config.ReasoningEffort);
					string role = conversation.Role;
					int contextWindow = _model.Config.ContextWindow;
					LiveUsageProgress onProgress = (inputTokens, outputTokens, turnCost, cachedTokens) =>
						{
							transport.Stats(conversation.Id, modelId, role, inputBaseline + inputTokens, outputBaseline + outputTokens, costBaseline + turnCost, contextWindow, contextBaseline, cachedTokens);
						};

					result = await _handler.ExecuteAsync(conversation.Bundle, toolDefs, forcedToolName, maxCompletionTokens, onProgress, transport, conversation.QueryLog, linked.Token);

					if (result.Outcome == ProtocolCallOutcome.Success)
					{
						conversation.RecordCost(result.Payload!.Cost);

						// Repair the response's tool calls in place (fuzzy name correction, argument fixups),
						// writing them back into the payload so the committed turn carries clean calls. A call
						// that cannot be repaired makes the turn unusable — and would 400 some providers on
						// re-send — so the broken turn is dropped (never committed) and re-requested as a
						// transient. No special tracking: an unrepairable tool call is just another transient.
						Dictionary<string, Tool> toolLookup = new Dictionary<string, Tool>(tools.Length);
						foreach (Tool t in tools)
							toolLookup[t.Definition.Function.Name] = t;
						string? unrepairable = ToolDispatch.FixToolCalls(result.Payload!, toolLookup);
						if (unrepairable == null)
							break;

						// Tell the model what was wrong before the retry, so it corrects the call instead of
						// blindly re-rolling the same mistake. It goes in as a user message: the broken assistant
						// turn was never committed, so there is no tool call to answer with a tool result.
						conversation.Bundle.OnUserMessage($"A tool call in your previous response was invalid and was discarded — it never ran. {unrepairable}. Call the tool again with every required argument supplied correctly.");
						result = ProtocolResult.Transient(unrepairable, null);
					}

					if (result.Outcome == ProtocolCallOutcome.ContextFull)
					{
						// The provider itself rejected the request as over the model's window — possible right
						// after a cross-provider fallback, whose tokenizers count differently, so the measured
						// size passed the gate but the real prompt did not fit. Retrying verbatim can never
						// succeed and the model is healthy: hand ContextFull to the caller so it compacts.
						conversation.QueryLog.ModelFailure(_model, _handler, "ContextFull", null, result.ErrorMessage ?? "Provider reported context overflow", 0, 0, null, false);
						break;
					}

					if (result.Outcome == ProtocolCallOutcome.RateLimited)
					{
						rateLimitRetries++;
						if (rateLimitRetries > kMaxRateLimitRetries)
						{
							// Sustained rate limiting is a caller-side budget issue, not a model failure. The
							// caller falls back to the next model in the role's list (RoleModelIds) on this outcome.
							conversation.QueryLog.ModelFailure(_model, _handler, "TooManyRetries", 429, "Rate limited after maximum retries", rateLimitRetries, kMaxRateLimitRetries, result.RetryAfter, true);
							result = ProtocolResult.TooManyRetries();
							break;
						}
						_availability.ExtendBackoff(result.RetryAfter ?? DateTimeOffset.UtcNow.AddSeconds(5));
						int waitSeconds = (int)Math.Ceiling(Math.Max(0, (_availability.AvailableAt - DateTimeOffset.UtcNow).TotalSeconds));
						transport.Status(conversation.Id, $"Rate limited {waitSeconds}s, retry ({rateLimitRetries}/{kMaxRateLimitRetries})");
						conversation.QueryLog.ModelFailure(_model, _handler, "RateLimited", 429, result.ErrorMessage ?? "Rate limited by provider", rateLimitRetries, kMaxRateLimitRetries, result.RetryAfter, false);
						// loop and retry once the backoff is honored at the top of the loop
					}
					else if (result.Outcome == ProtocolCallOutcome.Transient)
					{
						// A recoverable error (5xx/overload/network/timeout). Back off and retry on the SAME model
						// a few times so a momentary blip does not abandon an in-progress turn. Only once the
						// retries are spent do we give up — the caller then falls back to the next model in the
						// role's list instead of the model being killed outright.
						transientRetries++;
						if (transientRetries > kMaxTransientRetries)
						{
							// Surface the actual last error (e.g. "HTTP 500: ...") as a failure rather than
							// collapsing it into TooManyRetries, which the caller would otherwise report as
							// rate-limiting and bury the real cause. Rate-limit exhaustion (above) keeps
							// TooManyRetries; this transient path carries its reason.
							string message = string.IsNullOrEmpty(result.ErrorMessage) ?
								"Transient errors persisted after repeated retries." :
								$"Transient errors persisted after {kMaxTransientRetries} retries: {result.ErrorMessage}";
							conversation.QueryLog.ModelFailure(_model, _handler, "Failed", null, message, transientRetries, kMaxTransientRetries, result.RetryAfter, true);
							result = ProtocolResult.Failed(message);
							break;
						}
						// Prefer the server-stated retry time when the response carried one (the helper already
						// folds in a one-second margin); otherwise fall back to exponential backoff capped at 60s.
						if (result.RetryAfter.HasValue)
							_availability.ExtendBackoff(result.RetryAfter.Value);
						else
							_availability.ExtendBackoff(DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, 1 << (transientRetries - 1))));
						int backoffSeconds = (int)Math.Ceiling(Math.Max(0, (_availability.AvailableAt - DateTimeOffset.UtcNow).TotalSeconds));
						transport.Status(conversation.Id, $"Transient error, retry ({transientRetries}/{kMaxTransientRetries}) in {backoffSeconds}s: {result.ErrorMessage}");
						conversation.QueryLog.ModelFailure(_model, _handler, "Transient", null, result.ErrorMessage ?? "Transient error", transientRetries, kMaxTransientRetries, result.RetryAfter, false);
						// loop and retry once the backoff is honored at the top of the loop
					}
					else
					{
						// Unrecoverable (auth failure, unknown protocol): mark the model down so it is not
						// retried, and tell the HUMAN loudly — this class of failure (bad API key, wrong
						// endpoint, closed account) is never something the system can fix by itself.
						conversation.QueryLog.ModelFailure(_model, _handler, "Failed", null, result.ErrorMessage ?? "Permanent failure", 0, 0, null, true);
						_availability.ExtendBackoff(DateTimeOffset.MaxValue);
						transport.Alert(conversation.Id,
							$"Model '{_model.Config.Name}' has been marked unavailable: {result.ErrorMessage ?? "permanent failure"}\n"
							+ "This needs a human fix — check the API key, endpoint, or provider account in settings.json, then /reload (or /model) to retry.");
						break;
					}
				}
			}
			catch (OperationCanceledException ex)
			{
				// Either token tripping is an intentional cancel, not a model failure: turnToken is this
				// session's own /cancel (during the LLM call), while cancellationToken is the runner's whole-turn
				// scope — also cancelled by /cancel, plus by a parent or app shutdown. Only the session's own
				// /cancel sets the wait state; a parent/shutdown just unwinds. A cancel with neither token tripped
				// is a client-side timeout and is surfaced as a failure.
				if (turnToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
				{
					interrupted = turnToken.IsCancellationRequested;
					conversation.QueryLog.ModelFailure(_model, _handler, "Interrupted", null, "Interrupted by user", 0, 0, null, false);
					result = result.Outcome == ProtocolCallOutcome.Success
						? ProtocolResult.Interrupted("Interrupted by user", result.Payload)
						: ProtocolResult.Interrupted("Interrupted by user", null);
				}
				else
				{
					// Neither of our tokens tripped, so this OCE came from the transport itself — almost always
					// the HttpClient timeout elapsing while a slow or queued model never started responding.
					// Surface it explicitly as a timeout (Transient, so the caller retries/falls back) instead
					// of an opaque "cancelled". A TimeoutException inner confirms the HttpClient.Timeout case.
					bool timedOut = ex is TaskCanceledException && ex.InnerException is TimeoutException;
					string message = timedOut ?
						"LLM request timed out: the model did not respond before the HTTP client timeout elapsed (too slow, or queued behind other requests)." :
						$"LLM request was cancelled by the transport (not by the user): {ex}";
					conversation.QueryLog.ModelFailure(_model, _handler, timedOut ? "Timeout" : "TransportCancelled", null, message, 0, 0, null, true);
					result = ProtocolResult.Transient(message, null);
				}
			}
			catch (Exception ex)
			{
				// Cancellation without our token = client-side timeout (e.g. HttpClient). Surface it
				// as a failure so the user sees it instead of a silent retry loop.
				string reason = ex.InnerException != null ? ex.InnerException.Message : ex.ToString();
				conversation.QueryLog.ModelFailure(_model, _handler, "Exception", null, reason, 0, 0, null, true);
				result = ProtocolResult.Failed($"LLM call cancelled unexpectedly (client-side timeout): {reason}");
			}
			finally
			{
				conversation.EndTurn(interrupted);
			}
		}
		return result;
	}
}