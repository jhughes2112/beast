using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Drives one session chain to completion. Every session runs the same loop; the differences are
// configured, never discovered: the session itself carries its reply obligation (terminator tool,
// output budget, and work-turn budget, all persisted on BeastSession so they survive save/load
// and travel to compaction successors; root sessions carry none of them), the orchestrator
// resolves the parent linkage from the session id and holds the live completion callback for a
// waiting caller, the Role's tool list declares which capabilities ToolFactory grants, and the
// Role's prompts drive compaction and nudges.
//
// Sessions are never mutated in place. Compaction appends a fresh successor session to the chain
// and the handler advances to it; the reply obligation is handed to the successor while the
// predecessor keeps its full history — registered, saved, and replayable — as forensics. Once a
// session answers its caller, its obligation is cleared: it remains viable for conversation but
// can no longer reply as a tool.
public class SessionHandler
{
	// Advances along the chain when the session compacts.
	private Session     _activeSession;
	private LlmService? _service;
	private string?     _nextModel;
	private bool        _wantsCompact;

	// Terminator sink written by the tool callback, read after each dispatch round.
	private string? _terminatorValue;
	private bool    _terminatorSucceeded;
	private bool    _terminatorCalled;
	private int     _terminatorTokens;

	// Failure reason recorded when the model could not complete and no fallback was available.
	private string? _lastFailure;

	// Cost already rolled up into the parent for the active session; RollUpCost records only the
	// remainder, so the parent is billed exactly once however many times the rollup runs.
	private decimal _costRecordedToParent;

	// Last measured input token count, recorded by the tracer or after each successful turn.
	// Used to compute whether the context window is full without a live model call.
	private int _lastInputTokens;


	// Cancellation scope for the current turn cluster; replaced by ResetScope at each cluster
	// start and after a steering resume. The session's /cancel handler cancels the installed scope.
	private CancellationTokenSource _scope;

	public SessionHandler(Session session)
	{
		_activeSession = session;
		_scope         = new CancellationTokenSource();
	}

	// Drives the session chain until shutdown. Answering the caller does not end the run — the
	// session stays alive after its reply so the user can keep interacting with it. replayOnStart
	// is true only for a resumed root (the client has not seen its history yet); fresh children
	// have nothing committed, and revived sessions were already replayed by the restore pass or
	// streamed live.
	public async Task RunAsync(LlmRegistry registry, RoleService roleService, SettingsService settings, ITransportServer transport, ISessionOrchestrator orchestrator, WebSearchConfig? webSearchConfig, bool replayOnStart, CancellationToken ct)
	{
		if (replayOnStart)
			_activeSession.ReplayToTransport();
		_activeSession.SendStats();
		_activeSession.AnnounceToClient();

		const int kMaxWindDownTurns = 5;
		int   turn = 0;
		Role? role = null;

		ResetScope(ct);

		try
		{
			// Handlers run until shutdown or deletion: a session that exists is always serviced.
			// Budgets never end the run — running out of turns while a reply is owed forces the
			// answer out (step 7) and the session continues as a free-floating conversation.
			while (!ct.IsCancellationRequested && !_activeSession.Deleted)
			{
				// Budgets live on the session (part of its reply obligation), so they survive reload,
				// travel to compaction successors, and clear the moment the caller has been answered.
				int maxWork  = _activeSession.MaxWorkTurns > 0 ? _activeSession.MaxWorkTurns : int.MaxValue;
				int maxTotal = _activeSession.MaxWorkTurns > 0 ? _activeSession.MaxWorkTurns + kMaxWindDownTurns : int.MaxValue;

				// 1. Drain pending commands and queued text; refresh role, service, and completions.
				await DrainInput(roleService, registry, transport, ct);
				role = roleService.GetRole(_activeSession.Role);

				// Apply a queued /model right away — even when the session then parks idle — so the
				// choice takes visible effect immediately instead of waiting for the next turn.
				ApplyPendingModelSwitch(role, registry);
				RefreshService(role, registry);
				_activeSession.UpdateCompletions(BuildCompletionCandidates(roleService, registry));

				// 2. Compact when requested; the loop continues on the successor session. On failure,
				// drop the service so the next iteration re-selects a model that still fits the
				// conversation — the summarizer runs on throwaway stage sessions and leaves this
				// session's state untouched.
				if (_wantsCompact)
				{
					_wantsCompact = false;
					// User-requested /compact: the successor picks up wherever the predecessor stood,
					// so nothing is injected to restart it — it runs only if the history calls for it.
					if (!await CompactAsync(role, false, registry, roleService, transport, orchestrator, ct))
						_service = null;
				}

				// 3. Wait if there is nothing to do.
				if (!_activeSession.NeedsAttention() || _service == null || role == null)
				{
					await WaitForInputOrModelAsync(ct, role, registry, transport);
					continue;
				}

				// 3.5. Input that landed AFTER this iteration's drain goes back through the drain,
				// never straight into a turn. Racing it in broke visibly with attachments: Beast
				// sends "/attach" then the text as two frames, and a text arriving in the gap
				// between step 1 and here would start a cluster whose flush (correctly) refuses to
				// commit text with unresolved media — so the model was called with NOTHING new,
				// answered its own previous reply, and the user saw an unprompted second response.
				if (_activeSession.HasPending)
					continue;

				// 4. Run one turn cluster.
				_activeSession.EnsureNamedAndAnnounce();
				_activeSession.SendBusy();
				ResetScope(ct);

				// Wind-down only makes sense while the session still owes a reply to force out.
				bool windDown    = turn >= maxWork && _activeSession.OwesReply;
				bool contextFull = false;
				try
				{
					contextFull = await RunTurnClusterAsync(role, windDown, registry, roleService, settings, transport, webSearchConfig, orchestrator, ct);
				}
				catch (OperationCanceledException) when (_scope.IsCancellationRequested && !ct.IsCancellationRequested)
				{
					Console.Error.WriteLine($"[SessionHandler] Session {_activeSession.Id} turn interrupted between tool calls.");
					_activeSession.MarkInterrupted();
				}
				finally
				{
					_activeSession.SetDispatchScope(null);
					_activeSession.SendIdle();
					if (!_activeSession.Ephemeral)
						SaveSession(_activeSession);
				}

				// 5. Compact when the context filled mid-cluster (the turn was cut short, so the
				// successor resumes). Compaction sizes its work to the summarizing model's own
				// window, so no conversation is too large for it — reaching the failure path means
				// there was no usable model to compact WITH, or that compaction already ran and the
				// result STILL does not fit. Either way only a human can resolve it.
				// A provider can report overflow on a conversation that occupies almost none of its
				// window — a request sized wrong, or a configured window/output ceiling that does not
				// match what the provider actually allows. Compaction has nothing to remove there, and
				// running it anyway is worse than useless: the successor measures SMALLER, so the next
				// request asks for MORE output and is rejected again. That spun out 22 successor
				// sessions in a minute, each one a real API call and a session file. Compaction is only
				// an answer when there is something to compact.
				bool compactable = _activeSession.Budget.OverflowPlausible();
				if (contextFull && (!compactable || !await CompactAsync(role, true, registry, roleService, transport, orchestrator, ct)))
				{
					transport.Alert(_activeSession.Id, compactable
						? "The context window is full and compaction could not fix it — either no usable model was available to summarize with, or the conversation still does not fit after compacting. Use /model to switch to a model with a larger window, or /compact to retry."
						: $"Model '{_service?.Model.Config.Name ?? _activeSession.Model}' rejected the request as too large, but this conversation fills almost none of its window — compaction cannot fix that. The model's contextWindow or maxOutputTokens in settings.json most likely does not match what the provider really allows. Correct it, then /reload or /model.");
					_service = null;
					if (_lastFailure == null)
						_lastFailure = "the context window filled and compaction could not run";

					// Park until a human actually answers the alert. Dropping the service was not
					// enough on its own: the next iteration rebuilds one from the role, finds the
					// conversation still owing a turn, and runs straight back into the same wall —
					// which, against a provider that rejects instantly, is a spin that emits an
					// alert per pass and grew a session log to hundreds of megabytes before the app
					// died. The latch clears the moment any input arrives, including the /model and
					// /compact the alert asks for.
					_activeSession.MarkInterrupted();
				}

				// 6. Answer the caller at the first terminator call or failure. NotifyComplete clears
				// the reply obligation, so it fires at most once; the session stays alive afterwards
				// to accept new user input.
				if (_terminatorCalled || _lastFailure != null)
				{
					NotifyComplete(role.Name, orchestrator, true);
					_lastFailure      = null;
					_terminatorCalled = false;
				}
				turn++;

				// 7. Out of turns while still owing a reply: answer the caller now with whatever the
				// session produced (NotifyComplete salvages the last assistant text). The obligation —
				// and with it every budget — is cleared; from here on this is just a session the user
				// can chat with.
				if (_activeSession.OwesReply && turn >= maxTotal)
					NotifyComplete(role.Name, orchestrator, true);
			}
		}
		catch (OperationCanceledException)
		{
			if (!ct.IsCancellationRequested)
				Console.Error.WriteLine($"[SessionHandler] Session {_activeSession.Id} exited on unexpected OCE.");
		}
		finally
		{
			_scope.Dispose();
			if (!_activeSession.Ephemeral)
				SaveSession(_activeSession);
			RollUpCost(orchestrator);
			_activeSession.SendIdle();
			NotifyComplete(role?.Name ?? _activeSession.Role, orchestrator, false);

			// The loop only exits on shutdown, deletion, or an unhandled failure. Release the session
			// either way; after a failure with input already queued, hand it straight back to the
			// orchestrator so a fresh handler processes that input rather than leaving it to sit.
			_activeSession.DetachHandler();
			if (!ct.IsCancellationRequested && !_activeSession.Deleted && _activeSession.HasPending)
				orchestrator.EnsureHandler(_activeSession);
		}
	}

	// ---- Turn cluster ----

	// Runs assistant turns and tool dispatch until the model stops calling tools, the user steers,
	// or the run fails. Returns true when the context is full and the caller must compact.
	private async Task<bool> RunTurnClusterAsync(Role role, bool windDown, LlmRegistry registry, RoleService roleService, SettingsService settings, ITransportServer transport, WebSearchConfig? webSearchConfig, ISessionOrchestrator orchestrator, CancellationToken ct)
	{
		Tool[]  tools           = BuildTools(role, windDown, settings.Settings, registry, roleService, webSearchConfig, orchestrator);
		string? forcedTool      = windDown ? _activeSession.TerminatorName : null;
		bool    workToolsActive = _activeSession.WorkInProgress;
		bool    contextFull     = false;
		bool    turnComplete    = false;

		// Deleted ends the cluster immediately: MarkDeleted wakes a parked handler through the
		// input signal, and without this check the wake would read as steering and run more turns.
		while (!turnComplete && !contextFull && !ct.IsCancellationRequested && !_activeSession.Deleted)
		{
			// Reconcile service with any deferred /model switch before each LLM call.
			ApplyPendingModelSwitch(role, registry);
			LlmService? service = _service;
			if (service == null)
				break;

			contextFull = await CheckContextFullAsync(service, tools, transport, _scope.Token);
			if (contextFull)
				break;

			// No caller-imposed output cap: a session answers at the size its work warrants, and the
			// model's own ceiling (or the window remainder) is the only bound.
			ProtocolResult result = await service.RunToCompletionAsync(_activeSession, tools, forcedTool, GetCompactionReserve(), 0, true, transport, _scope.Token);

			if (result.Outcome == ProtocolCallOutcome.ContextFull)
			{
				contextFull = true;
			}
			else if (result.Outcome == ProtocolCallOutcome.Yielded)
			{
				// A retry backoff was interrupted because input arrived. Drain it here so a queued
				// /model applies at the loop top before the next attempt; not a failure, no fallback.
				await DrainInput(roleService, registry, transport, ct);
			}
			else if (result.Outcome == ProtocolCallOutcome.Interrupted)
			{
				turnComplete = !await TryResumeAfterInterruptAsync(role, roleService, registry, transport, ct);
			}
			else if (result.Outcome != ProtocolCallOutcome.Success)
			{
				string? failure = FallBackOrFail(service, result, registry, transport);
				if (failure != null)
				{
					_lastFailure = failure;
					turnComplete = true;
				}
			}
			else
			{
				_activeSession.CommitAssistantTurn(result.Payload!);
				if (result.Payload!.Usage.PromptTokens > 0)
					_lastInputTokens = result.Payload.Usage.PromptTokens;

				// WORKING fills the vacancy: a model that just served a turn becomes the role's
				// sticky preference only when none is set. A turn already in flight when the user
				// typed /model must not clobber that explicit choice before it ever dispatches.
				registry.RecordWorkingModel(role.Name, service.Model.ConfigId);

				bool hasToolCalls;
				try
				{
					hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, tools, _activeSession, transport, _scope.Token);
				}
				catch (OperationCanceledException) when (_scope.IsCancellationRequested && !ct.IsCancellationRequested)
				{
					Console.Error.WriteLine($"[SessionHandler] {role.Name} session {_activeSession.Id} dispatch cancelled.");

					// The assistant turn is already committed, so every tool call it made MUST be
					// answered — a call with no result is a malformed conversation that the strict
					// providers reject outright ("No tool output found for function call …") on the
					// very next request, taking the model down with it. ToolDispatch fills in a
					// cancelled result for each call before it throws; commit those.
					_activeSession.CommitToolResults(result.Payload!);

					turnComplete = !await TryResumeAfterInterruptAsync(role, roleService, registry, transport, ct);
					continue;
				}

				if (hasToolCalls)
					_activeSession.CommitToolResults(result.Payload!);

				// Drain any queued commands (e.g. /model) between tool rounds so they take
				// effect before the next LLM call rather than waiting for the turn to end.
				await DrainInput(roleService, registry, transport, ct);

				turnComplete = TurnComplete(role, windDown, hasToolCalls);

				// Rebuild the toolset when a tool toggled the work-in-progress state this round.
				if (!turnComplete && _activeSession.WorkInProgress != workToolsActive)
				{
					workToolsActive = _activeSession.WorkInProgress;
					tools           = BuildTools(role, windDown, settings.Settings, registry, roleService, webSearchConfig, orchestrator);
				}

				_activeSession.SendStats();
			}
		}
		return contextFull;
	}

	// Decides whether the turn cluster is finished after a successful assistant round. One policy
	// for every session; terminator behaviour engages only while the session owes a reply.
	private bool TurnComplete(Role role, bool windDown, bool hasToolCalls)
	{
		bool complete;
		if (_terminatorCalled)
		{
			// The reply is accepted at whatever size it is. Making a subagent rewrite its answer to
			// fit the caller's leftover room degraded the answer to protect a window the caller can
			// simply compact — the caller deals with the size when the reply lands.
			_terminatorTokens = _activeSession.LastTokenUsage?.CompletionTokens ?? 0;
			complete          = true;
		}
		else if (windDown)
		{
			_activeSession.AddUserMessage(Nudges.OutOfTurns(_activeSession.TerminatorName));
			complete = true;
		}
		else if (_activeSession.HasPending)
		{
			// New input arrived mid-round; end the cluster so the boundary drain applies it in order.
			complete = true;
		}
		else if (hasToolCalls)
		{
			complete = false;
		}
		else
		{
			// Plain response with no tool calls: nudge and end the turn, unless fresh user input
			// already drives the next one. Sessions that owe a reply are always steered back toward
			// the terminator; other sessions only while their work loop is in progress.
			string? nudge = null;
			if (_activeSession.OwesReply)
			{
				nudge = string.IsNullOrEmpty(role.EndOfTurnPrompt)
					? Nudges.ContinueTask(_activeSession.TerminatorName)
					: role.EndOfTurnPrompt;
			}
			else if (_activeSession.WorkInProgress && !string.IsNullOrEmpty(role.EndOfTurnPrompt))
			{
				nudge = role.EndOfTurnPrompt;
			}
			if (nudge != null && !TailIsUserMessage())
				_activeSession.AddUserMessage(nudge);
			complete = true;
		}
		return complete;
	}

	// True when the conversation already ends on user text (e.g. steering committed by the drain),
	// so an end-of-turn nudge would be redundant noise.
	private bool TailIsUserMessage()
	{
		IReadOnlyList<CanonicalMessage> messages = _activeSession.Bundle.Canonical.Messages;
		return messages.Count > 0 && messages[messages.Count - 1] is UserMessage;
	}

	// Estimates headroom and runs a cheap tracer call when close to the limit. Returns true when
	// the context is full and the turn must end in compaction.
	private async Task<bool> CheckContextFullAsync(LlmService service, Tool[] tools, ITransportServer transport, CancellationToken token)
	{
		bool full      = false;
		int  threshold = _activeSession.ContextWindow - GetCompactionReserve();

		// Only text appended since the last measurement counts: everything up to and including the
		// last assistant turn is already inside ContextLength, and unmeasured tool outputs are
		// covered by PendingReserve. Counting the whole history here made the estimate grow without
		// bound and fire the tracer on every turn of a long conversation.
		int pendingBytes = 0;
		IReadOnlyList<CanonicalMessage> messages = _activeSession.Bundle.Canonical.Messages;
		for (int i = messages.Count - 1; i >= 0; i--)
		{
			if (messages[i] is AssistantMessage)
				break;
			if (messages[i] is UserMessage um)
				pendingBytes += System.Text.Encoding.UTF8.GetByteCount(um.Text);
		}
		int estimate = _activeSession.ContextLength + (pendingBytes / 3) + _activeSession.Budget.PendingReserve;

		// A session nobody has measured yet must be counted before it is sized, not after it fails.
		// The chars/3 figure above is a gate, not a number to size a request from: it only decides
		// whether the real count is worth asking for, and on a fresh session — a compaction successor
		// especially — it reads near zero while the conversation holds a full summary. Sizing the
		// request from that produced a max_out of 29492 on top of an uncounted 7438-token prompt,
		// which the provider rejected as overflow, which compacted, which built another unmeasured
		// session. Counting first costs one call and makes every number after it real.
		bool unmeasured = _activeSession.ContextLength <= 0 && messages.Count > 0;

		if (estimate >= threshold || unmeasured)
		{
			TracerResult tracer = await service.RunTracerAsync(_activeSession, tools, null, token);
			if (tracer.Succeeded)
			{
				// TracerResult.InputTokens is the total prompt size (cached included) — adding
				// CachedTokens on top double-counted the cache and compacted prematurely. It lands
				// on the session as well as the budget: the two disagreeing is what let a counted
				// session still report ContextLength 0 to everything that reads it.
				_activeSession.RecordCountedContext(tracer.InputTokens, tracer.CachedTokens);
				_lastInputTokens = tracer.InputTokens;
				// Same current-context reading as Session.SendStats. The tracer measured the entire
				// prompt the next call would send, so cached plus the fresh remainder IS the whole
				// context here — including the previous turn's output, which the prompt now contains.
				// Reporting that output again as its own figure would count it twice, so it reads 0
				// until the turn about to start streams its own.
				int tracerFresh = tracer.InputTokens > tracer.CachedTokens ? tracer.InputTokens - tracer.CachedTokens : 0;
				transport.Stats(_activeSession.Id, _activeSession.Model + ReasoningEffort.DisplaySuffix(service.Model.Config.ReasoningEffort), _activeSession.Role,
					tracerFresh, 0,
					_activeSession.TotalCost, _activeSession.ContextWindow, tracer.InputTokens, tracer.CachedTokens);
				if (_lastInputTokens >= threshold)
				{
					transport.Status(_activeSession.Id, $"Context full ({_lastInputTokens}/{_activeSession.ContextWindow}), compacting...");
					full = true;
				}
			}
			else if (tracer.ContextBlown
				|| (estimate >= threshold && ProtocolHelpers.IsOverflowStatusCandidate(tracer.HttpStatus) && !ProtocolHelpers.IsAccountError(tracer.ErrorMessage ?? string.Empty)))
			{
				// ContextBlown means the body text matched a known overflow phrasing, which is
				// evidence on its own. The status check is the structural fallback and it rests
				// entirely on the estimate already standing at the compaction threshold: THERE a
				// client rejection is overflow however the server worded it. It says nothing of the
				// kind on a count of a session nobody has measured yet, which is why that case is
				// excluded — reading it as overflow would compact a conversation on no evidence
				// beyond "a request failed". An account error is never overflow either way.
				transport.Status(_activeSession.Id, $"Context exceeds limit ({tracer.ErrorMessage}), compacting...");
				full = true;
			}
		}
		return full;
	}

	// Swaps _service to a fallback model when one exists. Returns a failure message when no
	// fallback is available and the turn must end.
	private string? FallBackOrFail(LlmService service, ProtocolResult result, LlmRegistry registry, ITransportServer transport)
	{
		string? failure     = null;
		bool    rateLimited = result.Outcome == ProtocolCallOutcome.TooManyRetries;

		// The model failed this session: if it is still the role's sticky preference, clear it so
		// selection reverts to the ranked pecking order (a failure never wipes a NEWER choice).
		registry.ClearRolePreferredModel(_activeSession.Role, service.Model.ConfigId);

		// PendingReserve covers tool outputs appended since the last measurement — without it a
		// tool-heavy round can pick a fallback model the real conversation no longer fits in.
		LlmService? fallback = registry.CreateFallbackService(service, _activeSession.ContextLength + _activeSession.Budget.PendingReserve + GetCompactionReserve(), false);
		if (fallback != null)
		{
			_activeSession.QueryLog.FallbackTransition(service, fallback,
				rateLimited ? "Rate limited after retries" : "Model failed",
				string.IsNullOrEmpty(result.ErrorMessage) ? "(no error message)" : result.ErrorMessage);
			_service = fallback;
			_activeSession.UpdateModel(fallback.Model);
			_activeSession.SendStats();
			transport.Status(_activeSession.Id, $"{(rateLimited ? "Rate limited" : "Model failed")}; falling back to {fallback.Model.Config.Name}");
		}
		else
		{
			string detail = rateLimited
				? "Rate limited after too many retries, and no fallback model is available."
				: string.IsNullOrEmpty(result.ErrorMessage) ? "Model failed and no fallback model is available." : result.ErrorMessage;
			_activeSession.QueryLog.SessionFailure(_activeSession, service, detail, service.RoleModelIds.Count);
			// Every model in the role is exhausted — nothing the system can do; a human must add
			// credits, fix keys/config, or wait out the provider. Raise it loudly and persistently.
			transport.Alert(_activeSession.Id,
				$"Every model available to the '{_activeSession.Role}' role has failed. Last error: {detail}\n"
				+ "A human needs to intervene: add provider credits, fix API keys in settings.json, or wait out the rate limits — then /reload or /model to resume.");
			failure = string.IsNullOrEmpty(result.ErrorMessage) ? "all models failed" : result.ErrorMessage;
		}
		return failure;
	}

	// ---- Compaction ----

	// Compacts the active session into a fresh successor appended to the chain, then advances
	// _activeSession/_service to it. A mechanical elision pass runs first — no LLM call, real user
	// messages kept verbatim — and only when it cannot reclaim enough space does the full staged
	// summarization run. Neither sees a tool round still in flight: unanswered calls are held aside
	// and re-attached once compaction is done. Both successor shapes are headed by the ledger. The
	// reply obligation is handed to the successor — the predecessor can no longer answer as a tool
	// but is otherwise left intact: saved, registered, and replayable as forensics. Returns false
	// when no successor history could be produced or no service was available for the successor.
	// How many recent assistant turns to leave verbatim, tried in order. The first attempt summarizes
	// everything but the last couple of turns — the most reclaimed space — and each retreat hands the
	// summarizer LESS history to fold while keeping more of the recent conversation untouched. So a
	// summarize that cannot complete over the whole backlog is offered a smaller and smaller job
	// instead of simply failing, and whatever it does summarize is replaced wholesale.
	private static readonly int[] kRetreatTurns = new int[] { 2, 4, 8, 16, 32 };

	// Summarizes the conversation up to a retreat point and returns [summary] + the verbatim tail.
	// Keeping the tail is the point: a summary is a description of work, and the model still needs the
	// last few turns as they actually happened to carry on from them. Returns null only when every
	// retreat has been tried and none produced a summary.
	private async Task<List<CanonicalMessage>?> SummarizeWithRetreatAsync(List<CanonicalMessage> settled, Role role, LlmRegistry registry, RoleService roleService, ITransportServer transport, CancellationToken ct)
	{
		List<CanonicalMessage>? seed = null;

		for (int attempt = 0; attempt < kRetreatTurns.Length && seed == null; attempt++)
		{
			int split = MechanicalCompaction.TailStart(settled, kRetreatTurns[attempt]);
			if (split <= 0)
			{
				// The conversation has fewer turns than this retreat keeps, so there is nothing left
				// in front of the tail to summarize. Retreating further can only be emptier still.
				transport.Status(_activeSession.Id, "[Compaction] Nothing left to summarize ahead of the recent turns.");
				break;
			}

			List<CanonicalMessage> prefix = settled.GetRange(    0,                 split);
			List<CanonicalMessage> tail   = settled.GetRange(split, settled.Count - split);

			transport.Status(_activeSession.Id, $"[Compaction] Summarizing everything but the last {kRetreatTurns[attempt]} turns ({prefix.Count} messages)...");
			string? summary = await Summarizer.SummarizeAsync(_activeSession, prefix, role.SummaryPrompt, registry, roleService, transport, ct);

			if (!string.IsNullOrWhiteSpace(summary))
			{
				seed = new List<CanonicalMessage> { new UserMessage(summary!) };
				seed.AddRange(tail);
			}
			else if (attempt + 1 < kRetreatTurns.Length)
			{
				transport.Status(_activeSession.Id, $"[Compaction] That summarize did not complete; retreating to keep the last {kRetreatTurns[attempt + 1]} turns and summarize less.");
			}
		}

		if (seed == null)
			transport.Status(_activeSession.Id, "[Compaction] Summarization could not complete at any retreat point.");

		return seed;
	}

	private async Task<bool> CompactAsync(Role? role, bool resumeWork, LlmRegistry registry, RoleService roleService, ITransportServer transport, ISessionOrchestrator orchestrator, CancellationToken ct)
	{
		bool compacted = false;
		if (role == null)
		{
			transport.Status(_activeSession.Id, "[Compaction] No role available.");
		}
		else if (_activeSession.IsUncompactableSuccessor)
		{
			// Compaction just built this session and it has not managed a single turn since. Whatever
			// rejected its first request will reject the next successor identically, so compacting
			// again only spends a summarizer pass to arrive back here. Fail out to the caller, which
			// parks the session and asks a human for a model that fits.
			transport.Status(_activeSession.Id, "[Compaction] The freshly compacted conversation still does not fit — compacting again cannot help.");
		}
		else
		{
			transport.Status(_activeSession.Id, "[Compaction] Started.");

			// A tool round still in flight is not history yet: neither pass may rewrite half of a
			// call/result pair. Hold that tail aside, compact only the settled prefix, and re-attach
			// the tail verbatim afterwards. Ordinarily the tail is empty — the turn loop commits every
			// result before it ever compacts — but a session restored from an interrupted save, or one
			// the user compacts by hand mid-round, arrives here with calls still open.
			(List<CanonicalMessage> settled, List<CanonicalMessage> pending) = MechanicalCompaction.SplitPending(_activeSession.Data.Messages);
			if (pending.Count > 0)
				transport.Status(_activeSession.Id, $"[Compaction] Holding {pending.Count} in-flight message(s) aside.");

			List<CanonicalMessage>? seed = MechanicalCompaction.TryBuild(settled, role.EndOfTurnPrompt,
				_activeSession.ContextLength, _activeSession.ContextWindow);
			if (seed != null)
			{
				transport.Status(_activeSession.Id, "[Compaction] Stale tool traffic elided mechanically; no summarization needed.");
			}
			else if (!string.IsNullOrEmpty(role.SummaryPrompt))
			{
				seed = await SummarizeWithRetreatAsync(settled, role, registry, roleService, transport, ct);
			}
			else
			{
				transport.Status(_activeSession.Id, "[Compaction] No summary prompt available.");
			}

			// The ledger heads both successor shapes: files touched, with ranges, rebuilt fresh
			// from the predecessor's actual tool calls so repeated compactions never stack it up.
			if (seed != null)
			{
				string ledger = MechanicalCompaction.BuildLedger(_activeSession.Data.Messages);
				if (ledger.Length > 0)
					seed.Insert(0, new UserMessage(ledger));

				// The held-aside tail goes back on the end, after everything compaction produced, so
				// the calls sit in the same order the model made them. Any still unanswered is closed
				// with a note — its result is never coming, and a successor carrying an open call is
				// rejected by every protocol on its first request.
				if (pending.Count > 0)
				{
					MechanicalCompaction.CloseOpenCalls(pending);
					seed.AddRange(pending);
				}

				// Compaction interrupted work in progress, so the successor must pick it straight
				// back up. Both shapes now hand over history that can end on satisfied tool results —
				// nothing there asks the model for anything — so a resume message is appended unless
				// the seed already ends on user text. Checked last, once the verbatim tail and any
				// held-aside round are back in place, since those are what the seed ends on.
				if (resumeWork && MechanicalCompaction.NeedsResumePrompt(seed))
					seed.Add(new UserMessage(Nudges.ResumeAfterCompaction()));
			}

			LlmService? service = seed == null ? null : registry.CreateService(role, _activeSession.Model, 0, false);
			if (seed == null || service == null)
			{
				transport.Status(_activeSession.Id, "[Compaction] Failed.");
			}
			else
			{
				Session  predecessor = _activeSession;
				Session? parent      = orchestrator.FindParent(predecessor);

				// Hand the reply obligation (terminator, turn budget) to the successor before the
				// predecessor is saved, so a reload never resurrects two sessions both claiming to
				// answer the same caller.
				string terminatorName = predecessor.TerminatorName;
				int    maxWorkTurns   = predecessor.MaxWorkTurns;
				predecessor.ClearReplyObligation();

				predecessor.SetDispatchScope(null);
				predecessor.SendIdle();
				if (!predecessor.Ephemeral)
					SaveSession(predecessor);
				RollUpCost(orchestrator);

				// A child successor gets the next ID under the same parent; a root successor gets a
				// fresh GUID. Everything else about the two is identical.
				string successorId;
				if (parent != null)
				{
					successorId = parent.AllocateChildId();
					if (!parent.Ephemeral)
						SessionService.Save(parent.Data);
				}
				else
				{
					successorId = Guid.NewGuid().ToString();
				}

				// The successor's history is seeded directly as its message list — the mechanical
				// pass hands over the full elided conversation, the summarize path a single summary
				// message, both possibly headed by the ledger. Seeding the list verbatim (rather
				// than through OnUserMessage) keeps the ledger its own strippable message instead
				// of merging into adjacent user text.
				BeastSession successorData = new BeastSession(successorId, Session.IncrementDisplayName(predecessor.DisplayName),
					service.Model.ConfigId, role.Name, terminatorName,
					seed, null, 0m, 0, 0, 0, predecessor.Ephemeral);
				Session successor = new Session(successorData, role.SystemPrompt, transport, predecessor.IsSubagent);
				successor.MarkCompactionSuccessor();
				successor.SetMaxWorkTurns(maxWorkTurns);
				successor.UpdateModel(service.Model);
				if (predecessor.WorkInProgress)
					successor.BeginWork();
				successor.SetDispatchScope(_scope);

				// Announce the successor like any other new session, then ask the client to show it.
				// Nothing is torn down: compaction ADDS a session, it does not replace one, so the
				// client has nothing to invalidate and everything it already knows stays put. This
				// used to send a SessionReset first, which cleared the client's entire session list —
				// and since only the predecessor was re-announced behind it, every other live session
				// (sibling subagents, predecessors from earlier compactions) vanished from the client
				// while still running in the agent.
				//
				// Only a root asks for focus. A child compaction leaves the user wherever they were
				// looking; the successor simply appears in the tree.
				successor.AnnounceToClient();
				successor.ReplayToTransport();
				if (parent == null)
					transport.SessionActivate(successor.Id);
				else
					parent.AddChild(successor);
				if (!successor.Ephemeral)
					SaveSession(successor);
				orchestrator.RegisterSession(successor);

				// The caller waiting on the predecessor (if any) is now waiting on the successor,
				// which inherited the obligation to answer it.
				orchestrator.TransferCompletion(predecessor.Id, successor.Id);

				// This handler now drives the successor. The predecessor gets its own handler right
				// away — every session that exists is serviced — parked on the input wait until the
				// user actually talks to it (its context is full, so an unprompted turn would only
				// trigger another compaction).
				successor.TryAttachHandler();
				predecessor.DetachHandler();
				predecessor.MarkInterrupted();
				orchestrator.EnsureHandler(predecessor);

				_activeSession        = successor;
				_service              = service;
				_lastInputTokens      = 0;
				_costRecordedToParent = 0m;
				transport.Status(predecessor.Id, "[Compaction] Complete.");
				compacted = true;
			}
		}
		return compacted;
	}

	// ---- Input drain ----

	// True when the last measured input token count fills the current model's context window.
	// Recomputed each drain from the stored measurement; clears automatically when the model or
	// conversation changes so /model and /compact always resolve it without a sticky flag.
	private bool IsContextBlocked => _lastInputTokens > 0
		&& _activeSession.ContextWindow > 0
		&& _lastInputTokens >= _activeSession.ContextWindow - GetCompactionReserve();

	// Drains all pending commands and queued text for any session type.
	// When the context is full, plain text is dropped with a message;
	// /compact and /model are always let through to resolve the blocked state.
	// Also checks the session's NeedsRefresh flag: when set, re-fetches the role
	// and recreates the LlmService so /reload changes propagate immediately.
	private async Task DrainInput(RoleService roleService, LlmRegistry registry, ITransportServer transport, CancellationToken ct)
	{
		if (_activeSession.NeedsRefresh)
		{
			_activeSession.ClearRefresh();
			// Re-fetch role — it may have been modified or removed.
			Role? refreshedRole = roleService.GetRole(_activeSession.Role);
			if (refreshedRole == null)
			{
				// The session's role no longer exists in roles.json.
				// Clear service so the handler parks; send a status so the user knows.
				_service = null;
				transport.Status(_activeSession.Id, $"Role '{_activeSession.Role}' no longer exists after reload. This session is orphaned.");
			}
			else
			{
				// Force service recreation so updated model configs (endpoints, etc.)
				// from the reloaded settings take effect.
				_service = null;
				RefreshService(refreshedRole, registry);
				_activeSession.SendStats();
				transport.Status(_activeSession.Id, "Configuration reloaded for this session.");
			}
		}

		await DrainPendingAsync(registry, roleService, transport, ct);
	}

	// Resolves every staged file for this turn and commits the user message with whatever the
	// resolution produced: real attachments for a model that can see them, substituted text
	// otherwise. Each staged copy is deleted once consumed so the folder does not grow forever.
	private async Task DeliverWithAttachmentsAsync(string line, LlmRegistry registry, RoleService roleService, ITransportServer transport, CancellationToken ct)
	{
		// Taken (not read) so they can only ever be applied to this one message.
		List<string> taken = _activeSession.TakePendingAttachments();

		// Only markers whose file actually reached the agent are stripped: a marker the client
		// FAILED to stage stays in the text, so the request for that file is never silently erased.
		HashSet<string> stagedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string entry in taken)
		{
			int sep = entry.IndexOf('\x01');
			if (sep >= 0)
				stagedPaths.Add(entry.Substring(sep + 1));
		}

		StringBuilder         text        = new StringBuilder(MediaIntake.StripPathMarkers(line, stagedPaths));
		List<MediaAttachment> attachments = new List<MediaAttachment>();
		List<(string Display, MediaKind Kind, string Staged)> unsupported = new List<(string, MediaKind, string)>();

		foreach (string entry in taken)
		{
			int    sep          = entry.IndexOf('\x01');
			string stagedName   = sep >= 0 ? entry.Substring(0, sep) : entry;
			string originalPath = sep >= 0 ? entry.Substring(sep + 1) : string.Empty;

			(string note, MediaAttachment? attachment, string unsupportedDisplay, MediaKind kind) = await MediaIntake.ResolveAsync(
				stagedName, originalPath, _activeSession, registry, ct);

			if (attachment != null)
				attachments.Add(attachment);
			if (note.Length > 0)
				text.Append("\n\n").Append(note);
			if (unsupportedDisplay.Length > 0)
				unsupported.Add((unsupportedDisplay, kind, stagedName));
			else
				MediaIntake.DiscardStaged(stagedName);
		}

		_activeSession.Bundle.OnUserMessage(text.ToString(), attachments);

		// Media the current model cannot take is a human decision, not something to work around.
		// Substituting a description would not give the model what native input gives it — the
		// image tokens the attention heads actually reason over — so say plainly that the wrong
		// model is loaded, and name the ones that would work.
		Role? activeRole = roleService.GetRole(_activeSession.Role);
		foreach ((string display, MediaKind kind, string staged) in unsupported)
		{
			transport.Alert(_activeSession.Id, BuildCapabilityAlert(display, kind, registry, activeRole));
			MediaIntake.DiscardStaged(staged);
		}
	}

	// The red banner raised when a dropped file needs a modality the loaded model does not declare.
	// It names the file, what it needs, and the capable models IN THIS ROLE — /model can only
	// switch within the role's list, so suggesting anything outside it hands the user a command
	// that would be refused.
	private static string BuildCapabilityAlert(string display, MediaKind kind, LlmRegistry registry, Role? activeRole)
	{
		string         modality = MediaKinds.Modality(kind);
		List<LlmModel> capable  = activeRole != null
			? MediaKinds.CapableModels(registry, kind, activeRole.Models)
			: new List<LlmModel>();

		if (capable.Count == 0)
		{
			return $"'{display}' needs {modality} input, which the current model does not accept — and no model in this role does either. "
				+ "Enable a capable model in /config, or add one to this role's list in /role, then send it again.";
		}

		StringBuilder names = new StringBuilder();
		foreach (LlmModel model in capable)
		{
			if (names.Length > 0)
				names.Append('\n');
			names.Append("  /model ").Append(model.ConfigId).Append($"   (in:${model.Config.Cost.Input:0.00}/Mtok)");
		}

		return $"'{display}' needs {modality} input and the current model does not accept it, so it was NOT sent.\n"
			+ $"These enabled models do — switch with /model and send it again:\n{names}";
	}

	private async Task DrainPendingAsync(LlmRegistry registry, RoleService roleService, ITransportServer transport, CancellationToken ct)
	{
		while (_activeSession.TryDequeuePending(out string? line))
		{
			if (!line!.StartsWith("/", StringComparison.Ordinal))
			{
				if (IsContextBlocked)
				{
					// The text is dropped, so the media staged for it must go with it — leaving the
					// attachments armed silently glued the old file onto the next unrelated message
					// sent after a /compact or /model resolved the block.
					if (_activeSession.HasPendingAttachments)
					{
						foreach (string entry in _activeSession.TakePendingAttachments())
						{
							int sep = entry.IndexOf('\x01');
							MediaIntake.DiscardStaged(sep >= 0 ? entry.Substring(0, sep) : entry);
						}
					}
					transport.Status(_activeSession.Id, "Context window full — the message (and any attached files) was dropped. Use /compact or /model <id>, then send it again.");
				}
				else
				{
					// Files dropped into the input arrive as /attach lines immediately before the
					// text they belong to, so anything staged is folded into THIS turn.
					if (_activeSession.HasPendingAttachments)
					{
						await DeliverWithAttachmentsAsync(line, registry, roleService, transport, ct);
					}
					else
					{
						_activeSession.Bundle.OnUserMessage(line);
					}

					// New input on a completed session: clear status so the session runs again.
					if (_activeSession.Status != SessionStatus.Ongoing)
						_activeSession.ResumeFromComplete();
				}
				continue;
			}

			string  trimmed  = line.TrimStart('/').Trim();
			int     spaceIdx = trimmed.IndexOf(' ');
			string  verb     = (spaceIdx >= 0 ? trimmed.Substring(0, spaceIdx) : trimmed).ToLowerInvariant();
			string? args     = spaceIdx >= 0 ? trimmed.Substring(spaceIdx + 1).Trim() : null;

			switch (verb)
			{
				case "compact":
					_wantsCompact = true;
					if (_activeSession.Status != SessionStatus.Ongoing)
						_activeSession.ResumeFromComplete();
					break;
				case "model":
					if (args != null)
						QueueModelSwitch(args, roleService, registry, transport);
					break;
				case "effort":
					ApplyEffort(args, registry, transport);
					break;
				case "attach":
					// Staged-file notice from the client: "stagedName\x01originalPath". Held until
					// the message text arrives so the files land on the turn they belong to.
					if (args != null)
						_activeSession.AddPendingAttachment(args);
					break;
				case "help":
					transport.Output(_activeSession.Id, "Commands: /compact, /model <id>, /effort <none|minimal|low|medium|high|max>, /cancel");
					break;
				default:
					transport.Error(_activeSession.Id, $"Unknown command: /{verb}");
					break;
			}
		}
		transport.PendingQueue(_activeSession.Id, _activeSession.PeekAllPending());
	}

	// Reads or sets how hard the CURRENT model thinks. The change belongs to the model, not to this
	// session or its role: every session already using it picks the new level up on its next turn,
	// and it is written to settings so the next run starts there. Applies immediately — unlike
	// /model there is no queue, because nothing about the conversation has to be re-validated.
	//
	// A blank effort reports the level in force, which is never blank: an unconfigured model reads as
	// the default (ReasoningEffort.DefaultWord). An unrecognized word is refused rather than parsed,
	// since ReasoningEffort.Parse would otherwise read a typo as None and quietly stop the thinking.
	private void ApplyEffort(string? args, LlmRegistry registry, ITransportServer transport)
	{
		LlmModel? model = registry.GetModel(_activeSession.Model);
		if (model == null)
		{
			transport.Error(_activeSession.Id, $"No model is active for this session.");
			return;
		}

		string current = ReasoningEffort.DisplayWord(model.Config.ReasoningEffort);
		if (string.IsNullOrEmpty(args))
		{
			string level = string.IsNullOrEmpty(current) ? "none" : current;
			transport.Output(_activeSession.Id, $"{model.ConfigId} is thinking at '{level}'. Change it with /effort <none|minimal|low|medium|high|max>.");
			return;
		}

		string word = args.Trim().ToLowerInvariant();
		if (!ReasoningEffort.IsKnownWord(word))
		{
			transport.Error(_activeSession.Id, $"Unknown effort '{word}'. Use one of: none, minimal, low, medium, high, max.");
			return;
		}

		// "off" is accepted at the prompt but stored as the canonical word, so settings only ever
		// carry the vocabulary the rest of the system reads.
		string stored = word == "off" ? "none" : word;
		bool   saved  = registry.UpdateModelReasoning(model.ConfigId, stored, null);
		string suffix = saved ? "saved" : "for this run only — the model has no settings entry to write to";

		// Session carries a cached "(high)" suffix for its committed stat frames, stamped when the
		// model was set. Re-stamp it or this session would keep reporting the level it replaced.
		_activeSession.UpdateModel(model);
		transport.Status(_activeSession.Id, $"{model.ConfigId} now thinks at '{stored}' ({suffix}). It takes effect on the next turn, for every session using this model.");
	}

	// Validates a /model request and queues it; applied before the next LLM call (or immediately
	// when the session is idle). The request is resolved DIRECTLY and never substituted: asking
	// for a model that is down resets its availability and honors the choice, and an unknown or
	// out-of-role model is an error — previously GetModelForRole silently swapped in a different
	// model while the status message still named the one the user asked for.
	private void QueueModelSwitch(string args, RoleService roleService, LlmRegistry registry, ITransportServer transport)
	{
		int       spaceIdx    = args.IndexOf(' ');
		string    modelArg    = spaceIdx >= 0 ? args.Substring(0, spaceIdx) : args;
		Role?     role        = roleService.GetRole(_activeSession.Role);
		LlmModel? target      = registry.GetModel(modelArg);
		int       minRequired = _activeSession.ContextLength + GetCompactionReserve();

		bool inRole = false;
		if (role != null && target != null)
		{
			foreach (string id in role.Models)
			{
				if (string.Equals(id, target.ConfigId, StringComparison.OrdinalIgnoreCase))
				{
					inRole = true;
					break;
				}
			}
		}

		if (target == null)
		{
			transport.Error(_activeSession.Id, $"Unknown model: {modelArg}");
		}
		else if (!inRole)
		{
			transport.Error(_activeSession.Id, $"Model '{target.ConfigId}' is not in the '{_activeSession.Role}' role's model list.");
		}
		else if (target.Config.ContextWindow <= minRequired)
		{
			transport.Error(_activeSession.Id, $"Model '{target.ConfigId}' context window ({target.Config.ContextWindow}) is too small for the current conversation ({minRequired} tokens needed).");
		}
		else
		{
			// Canonical ConfigId everywhere (not the raw typed arg), so the later ordinal
			// comparisons in the apply and switch-back paths always match. Availability is reset
			// BEFORE anything selects against it — the whole point of an explicit /model on a
			// down model is to force a retry.
			registry.ResetAvailability(target.ConfigId);
			_nextModel = target.ConfigId;
			// Explicit user pick: overwrites the role preference unconditionally. It holds until
			// this model FAILS a dispatch (which clears it back to the pecking order) — an
			// in-flight turn on the old model cannot clobber it (RecordWorkingModel only fills
			// an empty slot).
			registry.SetRolePreferredModel(_activeSession.Role, target.ConfigId);
			_lastInputTokens = 0;
			transport.Status(_activeSession.Id, $"Model queued: {target.ConfigId}");
			if (_activeSession.Status != SessionStatus.Ongoing)
				_activeSession.ResumeFromComplete();
		}
	}

	// Applies a queued /model switch to the session and service, pushing fresh stats so the
	// client's model display reflects the switch the moment it lands.
	private void ApplyPendingModelSwitch(Role? role, LlmRegistry registry)
	{
		if (_nextModel != null)
		{
			if (_service == null || _nextModel != _service.Model.ConfigId)
			{
				LlmModel? target = registry.GetModel(_nextModel);
				if (target != null)
				{
					_activeSession.UpdateModel(target);
					RefreshService(role, registry);
					_activeSession.SendStats();
				}
			}
			_nextModel = null;
		}
	}

	// ---- Tool building ----

	// One toolset builder for every session. Which tools exist is configured by the role's tool
	// list (ToolFactory checks it); the terminator callback is supplied only while the session
	// owes a reply, and ToolFactory picks the matching terminator tool from the role.
	private Tool[] BuildTools(Role role, bool windDown, BeastSettings beastSettings, LlmRegistry registry, RoleService roleService, WebSearchConfig? webSearchConfig, ISessionOrchestrator orchestrator)
	{
		Tool[] full = ToolFactory.BuildForRole(
			beastSettings,
			role,
			registry,
			roleService,
			_activeSession,
			webSearchConfig,
			_activeSession.WorkInProgress,
			(roleName, displayName, prompt, maxWorkTurns, spawnCt) =>
				orchestrator.SpawnChildAsync(beastSettings, _activeSession, roleName, displayName, prompt, maxWorkTurns, spawnCt),
			() => _activeSession.BeginWork(),
			() => _activeSession.EndWork(),
			_activeSession.OwesReply ? Terminate : null);

		if (!windDown)
			return full;

		// Wind-down: restrict to the terminator tool only.
		Tool? terminator = null;
		foreach (Tool t in full)
		{
			if (string.Equals(t.Definition.Function.Name, _activeSession.TerminatorName, StringComparison.Ordinal))
			{
				terminator = t;
				break;
			}
		}
		return terminator != null ? new Tool[] { terminator } : full;
	}

	// Terminator tool callback: records the reply so the loop can deliver it to the caller. Every
	// terminator tool shares this shape — a success flag and an output string.
	private void Terminate(bool success, string output)
	{
		_terminatorSucceeded = success;
		_terminatorValue     = output;
		_terminatorCalled    = true;
	}

	// ---- Steering / idle waits ----

	// Installs a fresh dispatch cancellation scope linked to the ancestor token, replacing (and
	// disposing) the previous one. Clears the session's reference first so /cancel never races a
	// disposed scope.
	private void ResetScope(CancellationToken ct)
	{
		_activeSession.SetDispatchScope(null);
		_scope.Dispose();
		_scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_activeSession.SetDispatchScope(_scope);
	}

	// Parks the session until the user sends steering input after a direct /cancel, then installs
	// a fresh scope, re-drains input, and refreshes the service. Returns false when the wait is
	// cancelled by an ancestor or shutdown.
	private async Task<bool> TryResumeAfterInterruptAsync(Role? role, RoleService roleService, LlmRegistry registry, ITransportServer transport, CancellationToken ct)
	{
		bool resumed = false;
		_activeSession.SendIdle();
		try
		{
			// Wait until REAL input is queued. The input semaphore can hold a stale permit: any
			// line delivered while the turn was running signals it, and the mid-turn drains consume
			// the queue without consuming the permit. Without the HasPending check a single Escape
			// appeared not to stop the agent — the park woke instantly on the stale permit and the
			// turn resumed, until a second Escape found the semaphore empty and actually parked.
			// A deletion also releases this wait; it must read as "do not resume", not as steering.
			for (; ; )
			{
				await _activeSession.WaitForInputAsync(ct);
				if (ct.IsCancellationRequested || _activeSession.Deleted)
					break;
				if (_activeSession.HasPending)
				{
					resumed = true;
					break;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}

		if (resumed)
		{
			_activeSession.SendBusy();
			ResetScope(ct);
			await DrainInput(roleService, registry, transport, ct);
			RefreshService(role, registry);
		}
		return resumed;
	}

	// Waits for input or until the model becomes available again, whichever comes first.
	// When the model is immediately available, waits on the input signal so the loop always
	// has a real async yield point — without it the loop spins synchronously and starves other
	// async tasks (including the transport read loop that delivers user input).
	private async Task WaitForInputOrModelAsync(CancellationToken ct, Role? role, LlmRegistry registry, ITransportServer transport)
	{
		long waitMs = role != null ? registry.GetMillisecondsUntilAvailable(role) : 1000;

		if (waitMs == 0)
		{
			// Model is ready; block until the user sends input. This is the normal idle path
			// and must be a real await so other continuations (transport receive, etc.) can run.
			try
			{ await _activeSession.WaitForInputAsync(ct); }
			catch (OperationCanceledException) { }
			return;
		}

		int delayMs = waitMs == long.MaxValue ? 60000 : (int)Math.Min(waitMs, int.MaxValue);
		transport.Status(_activeSession.Id, waitMs == long.MaxValue
			? "No Models Available"
			: $"No Models Available, waiting {(int)Math.Ceiling(waitMs / 1000.0)}s");

		using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		Task waitTask                         = _activeSession.WaitForInputAsync(waitCts.Token);
		await Task.WhenAny(Task.Delay(delayMs, ct), waitTask);
		waitCts.Cancel();
		try
		{ await waitTask; }
		catch (OperationCanceledException) { }
	}

	// ---- Helpers ----

	// Ensures _service matches the session's model and is healthy; creates a replacement when it
	// is missing, down, or pointing at a different model. Keeps the old service when creation fails.
	// There is no switch-back to an earlier explicit choice: WORKING is preferred — whatever model
	// is currently serving stays, and each successful turn records it as the role's preference.
	private void RefreshService(Role? role, LlmRegistry registry)
	{
		if (_service == null || _service.IsDown || _service.Model.ConfigId != _activeSession.Model)
		{
			int         minCtx     = _activeSession.ContextLength + GetCompactionReserve();
			LlmService? newService = registry.CreateService(role, _activeSession.Model, minCtx, false);
			if (newService != null)
			{
				_activeSession.UpdateModel(newService.Model);
				_service = newService;
			}
		}
	}

	private int GetCompactionReserve()
	{
		return Math.Min((int)(_activeSession.ContextWindow * 0.1), 7500);
	}

	private void SaveSession(Session session)
	{
		// A deleted session must never be written again — its files were just removed, and a
		// late save from a still-unwinding handler would silently resurrect them.
		if (session.Deleted)
			return;

		if (session.InferDisplayName())
			session.AnnounceToClient();
		SessionService.Save(session.Data);
	}

	private List<string> BuildCompletionCandidates(RoleService roleService, LlmRegistry registry)
	{
		List<string> candidates  = new List<string> { "/compact", "/config", "/role", "/reload", "/model", "/finish", "/help" };
		Role?        activeRole  = roleService.GetRole(_activeSession.Role);
		LlmModel?    activeModel = activeRole != null
			? registry.GetModelForRole(activeRole, _activeSession.Model, _activeSession.ContextLength + GetCompactionReserve())
			: null;
		if (activeRole != null)
		{
			string       currentModelId = activeModel != null ? activeModel.ConfigId : _activeSession.Model + " (not available)";
			List<string> enabledModels  = registry.GetEnabledModelsForRole(activeRole);
			if (!string.IsNullOrEmpty(currentModelId) && enabledModels.Contains(currentModelId))
				candidates.Add("/model " + currentModelId + ModelPricingLabel(currentModelId, registry));
			foreach (string modelId in enabledModels)
			{
				if (modelId == currentModelId)
					continue;
				candidates.Add("/model " + modelId + ModelPricingLabel(modelId, registry));
			}
		}
		return candidates;
	}

	// The completion label for one model: what it costs, and what it can actually take as input.
	// Capabilities belong here because /model is the switch a user reaches for when the model in
	// front of them cannot read what they just dropped in.
	private string ModelPricingLabel(string modelId, LlmRegistry registry)
	{
		LlmModel? model = registry.GetModel(modelId);
		if (model == null)
			return string.Empty;

		CostConfig cost  = model.Config.Cost;
		string     label = $"  in:${cost.Input:0.00} out:${cost.Output:0.00} /Mtok";

		string modalities = ModalityLabel(model.Config);
		if (modalities.Length > 0)
			label += "  " + modalities;
		return label;
	}

	// Non-text input modalities, named rather than abbreviated: this shows up in a completion list
	// where there is room, and "image" reads better than a letter code.
	private static string ModalityLabel(ModelConfig config)
	{
		string label = string.Empty;
		foreach (string input in config.Input)
		{
			if (string.Equals(input, "text", StringComparison.OrdinalIgnoreCase))
				continue;
			label = label.Length == 0 ? input.ToLowerInvariant() : label + "," + input.ToLowerInvariant();
		}
		return label.Length == 0 ? string.Empty : "+" + label;
	}

	// Answers the caller once: delivers the terminator result, a failure report, or the salvaged
	// last assistant text, then clears the session's reply obligation — it remains viable for
	// conversation but can no longer respond as a tool. A no-op when no reply is owed, which is
	// what makes it safe to call from both the turn loop and the run's finally.
	// markStatus stamps the persisted termination status (Success/Failure) at the moment the reply
	// is delivered — this is the single place a struck-off session is labeled, so the caller moving
	// on (even to a replacement subagent) always leaves the session's fate visible and serialized.
	// The shutdown unwind passes false: a session unloaded mid-work is marked Incomplete by the
	// restore pass instead, and must not read as a deliberate failure.
	private void NotifyComplete(string roleName, ISessionOrchestrator orchestrator, bool markStatus)
	{
		if (_activeSession.OwesReply)
		{
			bool   ok;
			string output;
			int    tokens = _terminatorTokens;

			if (_terminatorCalled)
			{
				ok     = _terminatorSucceeded;
				output = _terminatorValue ?? string.Empty;
			}
			else
			{
				string salvaged = LastAssistantText();
				if (!string.IsNullOrEmpty(_lastFailure))
				{
					ok     = false;
					output = string.IsNullOrEmpty(salvaged)
						? $"The {roleName} subagent could not finish: {_lastFailure}."
						: $"The {roleName} subagent could not finish: {_lastFailure}.\n\nLast progress before it stopped:\n{salvaged}";
				}
				else if (string.IsNullOrEmpty(salvaged))
				{
					ok     = false;
					output = "The subagent finished without returning a result.";
				}
				else
				{
					ok     = true;
					output = salvaged;
					tokens = _activeSession.LastTokenUsage?.CompletionTokens ?? _terminatorTokens;
				}
			}

			// The session's duties are over one way or the other; persist how it ended so the F10
			// tree and status bar show it, and a reload remembers it without re-deriving. Leave the
			// delegation loop too — a struck-off session must not keep getting end-of-turn nudges
			// to continue work its caller has already written off.
			if (markStatus)
			{
				_activeSession.SetTerminationStatus(ok ? SessionStatus.Success : SessionStatus.Failure);
				_activeSession.EndWork();
			}

			// Bill the parent before the caller resumes so its cost display is current at the
			// moment the tool result lands.
			RollUpCost(orchestrator);
			orchestrator.CompleteSession(_activeSession.Id, ok, output, tokens);
			_activeSession.ClearReplyObligation();
			if (!_activeSession.Ephemeral)
				SaveSession(_activeSession);
		}
	}

	// Rolls the active session's spend up into the parent, recording only what has not been
	// recorded yet. Runs at reply time, at compaction hand-off, and at handler exit.
	private void RollUpCost(ISessionOrchestrator orchestrator)
	{
		Session? parent = orchestrator.FindParent(_activeSession);
		if (parent != null)
		{
			parent.RecordCost(_activeSession.TotalCost - _costRecordedToParent);
			_costRecordedToParent = _activeSession.TotalCost;
		}
	}

	private string LastAssistantText()
	{
		IReadOnlyList<CanonicalMessage> messages = _activeSession.Data.Messages;
		for (int i = messages.Count - 1; i >= 0; i--)
		{
			if (messages[i] is AssistantMessage am && !string.IsNullOrWhiteSpace(am.Text))
				return am.Text;
		}
		return string.Empty;
	}
}