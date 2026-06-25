using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Agent.Services;


public enum LlmExitReason
{
	Completed, ContextFull, Failed, Interrupted
}

// Reports running token counts and protocol-computed cost for the current in-flight assistant
// turn while a response streams. Provisional; superseded at commit by the authoritative payload.
public delegate void LiveUsageProgress(int inputTokens, int outputTokens, decimal turnCost);

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

	// forcedToolName (null = free choice) requires the model to call that exact tool this turn.
	// maxOutputCap (0 = none) hard-limits each response's max_tokens for capped sub-session retries.
	// guaranteed to fit the calling agent's allotted space without any post-hoc truncation.
	// Handles retry logic, rate limiting, and budget exhaustion. Returns ProtocolResult on each
	// successful call. Context-full detection happens in CommitTurn when adding content.
	public async Task<ProtocolResult> RunToCompletionAsync(Session conversation, Tool[] tools, string? forcedToolName, int reserveTokens, int maxOutputCap, ITransportServer transport, CancellationToken cancellationToken)
	{
		ProtocolResult result = ProtocolResult.Failed($"LLM {_model.Config.Name} is permanently down");

		if (_availability.IsDown == false)
		{
			conversation.UpdateModel(_model);
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
				TimeSpan delay = _availability.AvailableAt - DateTimeOffset.UtcNow;
				if (delay > TimeSpan.Zero)
					await Task.Delay(delay, linked.Token);

				if (budget.IsExhausted())
				{
					// Context-full is a caller-side constraint, not a model failure.
					// Detection happens in CommitTurn when ADDING content to the session.
					result = ProtocolResult.ContextFull("Context budget exhausted");
					Log.ModelFailure(
						modelId: _model.ConfigId,
						modelName: _model.Config.Name,
						endpoint: _model.Endpoint,
						protocol: _handler.GetDetectedProtocol().ToString(),
						failureType: "ContextFull",
						httpStatusCode: null,
						errorMessage: "Context budget exhausted",
						retryCount: 0,
						maxRetries: 0,
						retryAfter: null,
						willFallback: false);
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
					LiveUsageProgress onProgress = (inputTokens, outputTokens, turnCost) =>
						{
							transport.Stats(conversation.Id, modelId, role,
								inputBaseline + inputTokens,
								outputBaseline + outputTokens,
								costBaseline + turnCost,
								contextWindow, contextBaseline);
						};

					result = await _handler.ExecuteAsync(conversation.Bundle, toolDefs, forcedToolName, maxCompletionTokens, onProgress, transport, conversation.Id, conversation.QueryLog, linked.Token);

					if (result.Outcome == ProtocolCallOutcome.Success)
					{
						conversation.RecordCost(result.Payload!.Cost);

						// Repair the response's tool calls in place (fuzzy name correction, argument fixups),
						// writing them back into the payload so the committed turn carries clean calls. A call
						// that cannot be repaired makes the turn unusable — and would 400 some providers on
						// re-send — so the broken turn is dropped (never committed) and re-requested as a
						// transient. No special tracking: an unrepairable tool call is just another transient.
						string? unrepairable = ToolDispatch.FixToolCalls(result.Payload!, tools);
						if (unrepairable == null)
							break;

						// Tell the model what was wrong before the retry, so it corrects the call instead of
						// blindly re-rolling the same mistake. It goes in as a user message: the broken assistant
						// turn was never committed, so there is no tool call to answer with a tool result.
						conversation.Bundle.OnUserMessage($"A tool call in your previous response was invalid and was discarded — it never ran. {unrepairable}. Call the tool again with every required argument supplied correctly.");
						result = ProtocolResult.Transient(unrepairable, null);
					}

					if (result.Outcome == ProtocolCallOutcome.RateLimited)
					{
						rateLimitRetries++;
						if (rateLimitRetries > kMaxRateLimitRetries)
						{
							// Sustained rate limiting is a caller-side budget issue, not a model failure. The
							// caller falls back to the next model in the role's list (RoleModelIds) on this outcome.
							Log.ModelFailure(
								modelId: _model.ConfigId,
								modelName: _model.Config.Name,
								endpoint: _model.Endpoint,
								protocol: _handler.GetDetectedProtocol().ToString(),
								failureType: "TooManyRetries",
								httpStatusCode: 429,
								errorMessage: "Rate limited after maximum retries",
								retryCount: rateLimitRetries,
								maxRetries: kMaxRateLimitRetries,
								retryAfter: result.RetryAfter,
								willFallback: true);
							result = ProtocolResult.TooManyRetries();
							break;
						}
						_availability.AvailableAt = result.RetryAfter ?? DateTimeOffset.UtcNow.AddSeconds(5);
						int waitSeconds = (int)Math.Ceiling(Math.Max(0, (_availability.AvailableAt - DateTimeOffset.UtcNow).TotalSeconds));
						transport.Status(conversation.Id, $"Rate limited {waitSeconds}s, retry ({rateLimitRetries}/{kMaxRateLimitRetries})");
						Log.ModelFailure(
							modelId: _model.ConfigId,
							modelName: _model.Config.Name,
							endpoint: _model.Endpoint,
							protocol: _handler.GetDetectedProtocol().ToString(),
							failureType: "RateLimited",
							httpStatusCode: 429,
							errorMessage: result.ErrorMessage ?? "Rate limited by provider",
							retryCount: rateLimitRetries,
							maxRetries: kMaxRateLimitRetries,
							retryAfter: result.RetryAfter,
							willFallback: false);
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
							Log.ModelFailure(
								modelId: _model.ConfigId,
								modelName: _model.Config.Name,
								endpoint: _model.Endpoint,
								protocol: _handler.GetDetectedProtocol().ToString(),
								failureType: "Failed",
								httpStatusCode: null,
								errorMessage: string.IsNullOrEmpty(result.ErrorMessage)
									? "Transient errors persisted after repeated retries."
									: $"Transient errors persisted after {kMaxTransientRetries} retries: {result.ErrorMessage}",
								retryCount: transientRetries,
								maxRetries: kMaxTransientRetries,
								retryAfter: result.RetryAfter,
								willFallback: true);
							result = ProtocolResult.Failed(string.IsNullOrEmpty(result.ErrorMessage)
								? "Transient errors persisted after repeated retries."
								: $"Transient errors persisted after {kMaxTransientRetries} retries: {result.ErrorMessage}");
							break;
						}
						// Prefer the server-stated retry time when the response carried one (the helper already
						// folds in a one-second margin); otherwise fall back to exponential backoff capped at 60s.
						if (result.RetryAfter.HasValue)
							_availability.AvailableAt = result.RetryAfter.Value;
						else
							_availability.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, 1 << (transientRetries - 1)));
						int backoffSeconds = (int)Math.Ceiling(Math.Max(0, (_availability.AvailableAt - DateTimeOffset.UtcNow).TotalSeconds));
						transport.Status(conversation.Id, $"Transient error, retry ({transientRetries}/{kMaxTransientRetries}) in {backoffSeconds}s: {result.ErrorMessage}");
						Log.ModelFailure(
							modelId: _model.ConfigId,
							modelName: _model.Config.Name,
							endpoint: _model.Endpoint,
							protocol: _handler.GetDetectedProtocol().ToString(),
							failureType: "Transient",
							httpStatusCode: null,
							errorMessage: result.ErrorMessage ?? "Transient error",
							retryCount: transientRetries,
							maxRetries: kMaxTransientRetries,
							retryAfter: result.RetryAfter,
							willFallback: false);
						// loop and retry once the backoff is honored at the top of the loop
					}
					else
					{
						// Unrecoverable (auth failure, unknown protocol): mark the model down so it is not retried.
						Log.ModelFailure(
							modelId: _model.ConfigId,
							modelName: _model.Config.Name,
							endpoint: _model.Endpoint,
							protocol: _handler.GetDetectedProtocol().ToString(),
							failureType: "Failed",
							httpStatusCode: null,
							errorMessage: result.ErrorMessage ?? "Permanent failure",
							retryCount: 0,
							maxRetries: 0,
							retryAfter: null,
							willFallback: true);
						_availability.AvailableAt = DateTimeOffset.MaxValue;
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
					Log.ModelFailure(
						modelId: _model.ConfigId,
						modelName: _model.Config.Name,
						endpoint: _model.Endpoint,
						protocol: _handler.GetDetectedProtocol().ToString(),
						failureType: "Interrupted",
						httpStatusCode: null,
						errorMessage: "Interrupted by user",
						retryCount: 0,
						maxRetries: 0,
						retryAfter: null,
						willFallback: false);
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
					Log.ModelFailure(
						modelId: _model.ConfigId,
						modelName: _model.Config.Name,
						endpoint: _model.Endpoint,
						protocol: _handler.GetDetectedProtocol().ToString(),
						failureType: timedOut ? "Timeout" : "TransportCancelled",
						httpStatusCode: null,
						errorMessage: timedOut
							? "LLM request timed out: the model did not respond before the HTTP client timeout elapsed (too slow, or queued behind other requests)."
							: $"LLM request was cancelled by the transport (not by the user): {ex}",
						retryCount: 0,
						maxRetries: 0,
						retryAfter: null,
						willFallback: true);
					result = timedOut
						? ProtocolResult.Transient("LLM request timed out: the model did not respond before the HTTP client timeout elapsed (too slow, or queued behind other requests).", null)
						: ProtocolResult.Transient($"LLM request was cancelled by the transport (not by the user): {ex}", null);
				}
			}
			catch (Exception ex)
			{
				// Cancellation without our token = client-side timeout (e.g. HttpClient). Surface it
				// as a failure so the user sees it instead of a silent retry loop.
				string reason = ex.InnerException != null ? ex.InnerException.Message : ex.ToString();
				Log.ModelFailure(
					modelId: _model.ConfigId,
					modelName: _model.Config.Name,
					endpoint: _model.Endpoint,
					protocol: _handler.GetDetectedProtocol().ToString(),
					failureType: "Exception",
					httpStatusCode: null,
					errorMessage: reason,
					retryCount: 0,
					maxRetries: 0,
					retryAfter: null,
					willFallback: true,
					stackTrace: ex.StackTrace);
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