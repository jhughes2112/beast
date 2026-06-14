using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


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

    public LlmModel Model => _model;
    public bool IsDown => _availability.IsDown;

    // detectedProtocol is resolved by LlmRegistry.ProbeEndpointsAsync before any session starts.
    // availability is shared across all service instances for the same model so rate-limit and
    // down state set by one session are visible to others picking that model next.
    public LlmService(LlmModel model, DetectedProtocol detectedProtocol, ModelAvailability availability)
    {
        _model = model;
        _availability = availability;
        _handler = new ProtocolProxy(model, detectedProtocol);
    }

	// forcedToolName (null = free choice) requires the model to call that exact tool this turn.
	// maxOutputCap (0 = none) hard-limits each response's max_tokens for capped sub-session retries.
	// guaranteed to fit the calling agent's allotted space without any post-hoc truncation.
	// Handles retry logic, rate limiting, and budget exhaustion. Returns ProtocolResult on each
	// successful call. Context-full detection happens in CommitTurn when adding content.
	 public async Task<ProtocolResult> RunToCompletionAsync(Session conversation, Tool[] tools, string? forcedToolName, int reserveTokens, int maxOutputCap, ITransportServer transport, CancellationToken cancellationToken)
	 {
		ProtocolResult result = ProtocolResult.Failed($"LLM {_model.Config.Name} is permanently down");

        if (_availability.IsDown==false)
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
                const int kMaxRateLimitRetries = 10;

                // Central context accounting for this turn. Seeded with the model's window and limits, the
                // compaction reserve, and the sub-session output cap. It owns all "how much room is left"
                // math: the context-full gate, completion sizing, and per-tool response reservations.
                ContextBudget budget = conversation.Budget;
                budget.Configure(_model.Config.ContextWindow, _model.Config.MaxOutputTokens, reserveTokens, maxOutputCap, conversation.ContextLength);

                for (; ; )
                {
                    if (rateLimitRetries <= kMaxRateLimitRetries)
                    {
                        TimeSpan delay = _availability.AvailableAt - DateTimeOffset.UtcNow;
                        if (delay > TimeSpan.Zero)
                        {
                            if (rateLimitRetries>0)
                            {
                                transport.Status(conversation.Id, $"Rate limited {(int)Math.Ceiling(delay.TotalSeconds)}s, retry ({rateLimitRetries}/{kMaxRateLimitRetries})");
                            }
                            await Task.Delay(delay, linked.Token);
                        }
                    }
                    else
                    {
                        // Rate limiting is a caller-side budget issue, not a model failure.
                        result = ProtocolResult.TooManyRetries();
                        break;
                    }

                    if (budget.IsExhausted())
                    {
                        // Context-full is a caller-side constraint, not a model failure.
                        // Detection happens in CommitTurn when ADDING content to the session.
                        result = ProtocolResult.ContextFull("Context budget exhausted");
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
                    string modelId = conversation.Model;
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
                        // RecordTurnUsage feeds the reported size into the budget (ContextBudget.RecordMeasurement),
                        // which resets pending reservations: the size already includes any prior tool outputs.
                        conversation.RecordTurnUsage(result.Payload!.Usage, result.Payload.Cost, result.Payload.CurrentContextSize);
                        break;
                    }
                    else if (result.Outcome == ProtocolCallOutcome.RateLimited)
                    {
                        rateLimitRetries++;
                        _availability.AvailableAt = result.RetryAfter ?? DateTimeOffset.UtcNow.AddSeconds(5);
                        // loop and retry
                    }
                    else if (result.Outcome == ProtocolCallOutcome.Transient)
                    {
                        _availability.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(300);  // give it five minutes to try another LLM
                        break;
                    }
                    else
                    {
                        _availability.AvailableAt = DateTimeOffset.MaxValue;
                        break;
                    }
                }
            }
			catch (OperationCanceledException ex)
			{
				// the user interrupted us
				if (turnToken.IsCancellationRequested && cancellationToken.IsCancellationRequested==false)
					{
						interrupted = true;
						result = result.Outcome == ProtocolCallOutcome.Success
							? ProtocolResult.Interrupted("Interrupted by user", result.Payload)
							: ProtocolResult.Interrupted("Interrupted by user");
					}
				else // The LLM cancelled on its own
				{
					result = ProtocolResult.Failed($"LLM call cancelled: {ex.Message}");
				}
			}
			catch (Exception ex)
			{
				// Cancellation without our token = client-side timeout (e.g. HttpClient). Surface it
				// as a failure so the user sees it instead of a silent retry loop.
				string reason = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
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
