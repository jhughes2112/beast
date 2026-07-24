using System;
using System.Collections.Generic;
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
	private Session _activeSession;
	private LlmService? _service;
	private string? _nextModel;
	private bool _wantsCompact;

	// Terminator sink written by the tool callback, read after each dispatch round.
	private string? _terminatorValue;
	private bool _terminatorSucceeded;
	private bool _terminatorCalled;
	private int _terminatorTokens;

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
		_scope = new CancellationTokenSource();
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
		int turn = 0;
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
				int maxWork = _activeSession.MaxWorkTurns > 0 ? _activeSession.MaxWorkTurns : int.MaxValue;
				int maxTotal = _activeSession.MaxWorkTurns > 0 ? _activeSession.MaxWorkTurns + kMaxWindDownTurns : int.MaxValue;

				// 1. Drain pending commands and queued text; refresh role, service, and completions.
				DrainInput(roleService, registry, transport);
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
					if (!await CompactAsync(role, registry, roleService, transport, orchestrator, ct))
						_service = null;
				}

				// 3. Wait if there is nothing to do.
				if (!_activeSession.NeedsAttention() || _service == null || role == null)
				{
					await WaitForInputOrModelAsync(ct, role, registry, transport);
					continue;
				}

				// 4. Run one turn cluster.
				_activeSession.EnsureNamedAndAnnounce();
				_activeSession.SendBusy();
				ResetScope(ct);

				// Wind-down only makes sense while the session still owes a reply to force out.
				bool windDown = turn >= maxWork && _activeSession.OwesReply;
				bool lastTurn = turn == maxTotal - 1;
				bool contextFull = false;
				try
				{
					contextFull = await RunTurnClusterAsync(role, windDown, lastTurn, registry, roleService, settings, transport, webSearchConfig, orchestrator, ct);
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

				// 5. Compact when the context filled mid-cluster. When that fails the session cannot
				// make further progress on this model: report it (which also unblocks a waiting
				// caller) and force a service re-check so the loop parks instead of spinning.
				if (contextFull && !await CompactAsync(role, registry, roleService, transport, orchestrator, ct))
				{
					transport.Alert(_activeSession.Id, "The context window is full and compaction failed — this session cannot continue by itself. Use /model to switch to a larger model, or /compact to retry once one is available.");
					_service = null;
					if (_lastFailure == null)
						_lastFailure = "the context window filled and compaction failed";
				}

				// 6. Answer the caller at the first terminator call or failure. NotifyComplete clears
				// the reply obligation, so it fires at most once; the session stays alive afterwards
				// to accept new user input.
				if (_terminatorCalled || _lastFailure != null)
				{
					NotifyComplete(role.Name, orchestrator, true);
					_lastFailure = null;
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
	private async Task<bool> RunTurnClusterAsync(Role role, bool windDown, bool lastTurn, LlmRegistry registry, RoleService roleService, SettingsService settings, ITransportServer transport, WebSearchConfig? webSearchConfig, ISessionOrchestrator orchestrator, CancellationToken ct)
	{
		Tool[] tools = BuildTools(role, windDown, settings.Settings, registry, roleService, webSearchConfig, orchestrator);
		string? forcedTool = windDown ? _activeSession.TerminatorName : null;
		int outputCap = windDown ? _activeSession.OutputBudgetTokens : 0;
		bool workToolsActive = _activeSession.WorkInProgress;
		bool contextFull = false;
		bool turnComplete = false;

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

			ProtocolResult result = await service.RunToCompletionAsync(_activeSession, tools, forcedTool, GetCompactionReserve(), outputCap, true, transport, _scope.Token);

			if (result.Outcome == ProtocolCallOutcome.ContextFull)
			{
				contextFull = true;
			}
			else if (result.Outcome == ProtocolCallOutcome.Yielded)
			{
				// A retry backoff was interrupted because input arrived. Drain it here so a queued
				// /model applies at the loop top before the next attempt; not a failure, no fallback.
				DrainInput(roleService, registry, transport);
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
					turnComplete = !await TryResumeAfterInterruptAsync(role, roleService, registry, transport, ct);
					continue;
				}

				if (hasToolCalls)
					_activeSession.CommitToolResults(result.Payload!);

				// Drain any queued commands (e.g. /model) between tool rounds so they take
				// effect before the next LLM call rather than waiting for the turn to end.
				DrainInput(roleService, registry, transport);

				turnComplete = TurnComplete(role, windDown, lastTurn, hasToolCalls);

				// Rebuild the toolset when a tool toggled the work-in-progress state this round.
				if (!turnComplete && _activeSession.WorkInProgress != workToolsActive)
				{
					workToolsActive = _activeSession.WorkInProgress;
					tools = BuildTools(role, windDown, settings.Settings, registry, roleService, webSearchConfig, orchestrator);
				}

				_activeSession.SendStats();
			}
		}
		return contextFull;
	}

	// Decides whether the turn cluster is finished after a successful assistant round. One policy
	// for every session; terminator behaviour engages only while the session owes a reply.
	private bool TurnComplete(Role role, bool windDown, bool lastTurn, bool hasToolCalls)
	{
		bool complete;
		if (_terminatorCalled)
		{
			_terminatorTokens = _activeSession.LastTokenUsage?.CompletionTokens ?? 0;
			int budget = _activeSession.OutputBudgetTokens;
			if (budget > 0 && _terminatorTokens > budget && !lastTurn)
			{
				_terminatorCalled = false;
				_activeSession.AddUserMessage(
					$"That output is about {_terminatorTokens} tokens but must fit within {budget} tokens. "
					+ $"Call {_activeSession.TerminatorName} again with a shorter output, preserving the key details (file paths, line numbers, names, key output).");
				complete = false;
			}
			else
			{
				complete = true;
			}
		}
		else if (windDown)
		{
			_activeSession.AddUserMessage(
				$"You are out of working turns. Call the {_activeSession.TerminatorName} tool now with your final result, "
				+ "preserving the key details (file paths, line numbers, names, key output).");
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
					? $"Continue the task, then call the {_activeSession.TerminatorName} tool with your final result to finish."
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
		bool full = false;
		int threshold = _activeSession.ContextWindow - GetCompactionReserve();

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

		if (estimate >= threshold)
		{
			TracerResult tracer = await service.RunTracerAsync(_activeSession, tools, null, token);
			if (tracer.Succeeded)
			{
				// TracerResult.InputTokens is the total prompt size (cached included) — adding
				// CachedTokens on top double-counted the cache and compacted prematurely.
				_activeSession.Budget.RecordMeasurement(tracer.InputTokens);
				_lastInputTokens = tracer.InputTokens;
				transport.Stats(_activeSession.Id, _activeSession.Model + ReasoningEffort.DisplaySuffix(service.Model.Config.ReasoningEffort), _activeSession.Role,
					_activeSession.CumulativeInputTokens, _activeSession.CumulativeOutputTokens,
					_activeSession.TotalCost, _activeSession.ContextWindow, tracer.InputTokens, tracer.CachedTokens);
				if (_lastInputTokens >= threshold)
				{
					transport.Status(_activeSession.Id, $"Context full ({_lastInputTokens}/{_activeSession.ContextWindow}), compacting...");
					full = true;
				}
			}
			else if (tracer.ContextBlown || ProtocolHelpers.IsOverflowStatusCandidate(tracer.HttpStatus))
			{
				// ContextBlown means the body text matched a known overflow phrasing. The status
				// check is the structural fallback: this tracer only ran because the estimate is
				// already at the compaction threshold, so a client rejection here is overflow
				// evidence regardless of how the server worded it.
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
		string? failure = null;
		bool rateLimited = result.Outcome == ProtocolCallOutcome.TooManyRetries;

		// The model failed this session: if it is still the role's sticky preference, clear it so
		// selection reverts to the ranked pecking order (a failure never wipes a NEWER choice).
		registry.ClearRolePreferredModel(_activeSession.Role, service.Model.ConfigId);

		// PendingReserve covers tool outputs appended since the last measurement — without it a
		// tool-heavy round can pick a fallback model the real conversation no longer fits in.
		LlmService? fallback = registry.CreateFallbackService(service, _activeSession.ContextLength + _activeSession.Budget.PendingReserve + GetCompactionReserve());
		if (fallback != null)
		{
			_activeSession.QueryLog.FallbackTransition(service, fallback,
				rateLimited ? "Rate limited after retries" : "Model failed",
				rateLimited ? 10 : 5);
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

	// Summarizes the active session into a fresh successor appended to the chain, then advances
	// _activeSession/_service to it. The reply obligation is handed to the successor — the
	// predecessor can no longer answer as a tool but is otherwise left intact: saved, registered,
	// and replayable as forensics. Returns false when no summary could be produced or no service
	// was available for the successor.
	private async Task<bool> CompactAsync(Role? role, LlmRegistry registry, RoleService roleService, ITransportServer transport, ISessionOrchestrator orchestrator, CancellationToken ct)
	{
		bool compacted = false;
		if (role == null || string.IsNullOrEmpty(role.SummaryPrompt))
		{
			transport.Status(_activeSession.Id, "[Compaction] No role or summary prompt available.");
		}
		else
		{
			transport.Status(_activeSession.Id, "[Compaction] Started.");
			string? summary = await Summarizer.SummarizeAsync(_activeSession, role.SummaryPrompt, registry, roleService, transport, ct);
			LlmService? service = string.IsNullOrWhiteSpace(summary) ? null : registry.CreateService(role, _activeSession.Model, 0);
			if (summary == null || service == null)
			{
				transport.Status(_activeSession.Id, "[Compaction] Failed.");
			}
			else
			{
				Session predecessor = _activeSession;
				Session? parent = orchestrator.FindParent(predecessor);

				// Hand the reply obligation (terminator, output budget, turn budget) to the successor
				// before the predecessor is saved, so a reload never resurrects two sessions both
				// claiming to answer the same caller.
				string terminatorName = predecessor.TerminatorName;
				int outputBudgetTokens = predecessor.OutputBudgetTokens;
				int maxWorkTurns = predecessor.MaxWorkTurns;
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

				BeastSession successorData = new BeastSession(successorId, Session.IncrementDisplayName(predecessor.DisplayName),
					service.Model.ConfigId, role.Name, terminatorName, outputBudgetTokens,
					new List<CanonicalMessage>(), null, 0m, 0, 0, 0, predecessor.Ephemeral);
				Session successor = new Session(successorData, role.SystemPrompt, transport, predecessor.IsSubagent);
				successor.SetMaxWorkTurns(maxWorkTurns);
				successor.UpdateModel(service.Model);
				if (predecessor.WorkInProgress)
					successor.BeginWork();
				successor.SetDispatchScope(_scope);
				successor.Bundle.Canonical.OnUserMessage(summary);

				// SessionReset FIRST for a root: it wipes the client's whole session view, so
				// anything announced or replayed before it is lost. The old order (announce, then
				// reset, then never replay a root successor) left the compacted chat displaying as
				// a raw GUID over an empty transcript, beside a named-but-empty predecessor.
				if (parent == null)
					transport.SessionReset(successor.Id);
				successor.AnnounceToClient();
				successor.ReplayToTransport();
				if (parent != null)
					parent.AddChild(successor);

				// The reset also erased the predecessor from the client; re-announce and replay it
				// so its full history remains browsable from the F10 tree.
				if (parent == null)
				{
					predecessor.AnnounceToClient();
					predecessor.SendStats();
					predecessor.ReplayToTransport();
				}
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

				_activeSession = successor;
				_service = service;
				_lastInputTokens = 0;
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
	private void DrainInput(RoleService roleService, LlmRegistry registry, ITransportServer transport)
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

		while (_activeSession.TryDequeuePending(out string? line))
		{
			if (!line!.StartsWith("/", StringComparison.Ordinal))
			{
				if (IsContextBlocked)
				{
					transport.Status(_activeSession.Id, "Context window full — use /compact or /model <id> before sending more input.");
				}
				else
				{
					_activeSession.Bundle.OnUserMessage(line);

					// New input on a completed session: clear status so the session runs again.
					if (_activeSession.Status != SessionStatus.Ongoing)
						_activeSession.ResumeFromComplete();
				}
				continue;
			}

			string trimmed = line.TrimStart('/').Trim();
			int spaceIdx = trimmed.IndexOf(' ');
			string verb = (spaceIdx >= 0 ? trimmed.Substring(0, spaceIdx) : trimmed).ToLowerInvariant();
			string? args = spaceIdx >= 0 ? trimmed.Substring(spaceIdx + 1).Trim() : null;

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
				case "help":
					transport.Output(_activeSession.Id, "Commands: /compact, /model <id>, /cancel");
					break;
				default:
					transport.Error(_activeSession.Id, $"Unknown command: /{verb}");
					break;
			}
		}
		transport.PendingQueue(_activeSession.Id, _activeSession.PeekAllPending());
	}

	// Validates a /model request and queues it; applied before the next LLM call (or immediately
	// when the session is idle). The request is resolved DIRECTLY and never substituted: asking
	// for a model that is down resets its availability and honors the choice, and an unknown or
	// out-of-role model is an error — previously GetModelForRole silently swapped in a different
	// model while the status message still named the one the user asked for.
	private void QueueModelSwitch(string args, RoleService roleService, LlmRegistry registry, ITransportServer transport)
	{
		int spaceIdx = args.IndexOf(' ');
		string modelArg = spaceIdx >= 0 ? args.Substring(0, spaceIdx) : args;
		Role? role = roleService.GetRole(_activeSession.Role);
		LlmModel? target = registry.GetModel(modelArg);
		int minRequired = _activeSession.ContextLength + GetCompactionReserve();

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
			(roleName, displayName, prompt, maxWorkTurns, budget, spawnCt) =>
				orchestrator.SpawnChildAsync(beastSettings, _activeSession, roleName, displayName, prompt, maxWorkTurns, budget, spawnCt),
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
		_terminatorValue = output;
		_terminatorCalled = true;
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
			DrainInput(roleService, registry, transport);
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
			try { await _activeSession.WaitForInputAsync(ct); }
			catch (OperationCanceledException) { }
			return;
		}

		int delayMs = waitMs == long.MaxValue ? 60000 : (int)Math.Min(waitMs, int.MaxValue);
		transport.Status(_activeSession.Id, waitMs == long.MaxValue
			? "No Models Available"
			: $"No Models Available, waiting {(int)Math.Ceiling(waitMs / 1000.0)}s");

		using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		Task waitTask = _activeSession.WaitForInputAsync(waitCts.Token);
		await Task.WhenAny(Task.Delay(delayMs, ct), waitTask);
		waitCts.Cancel();
		try { await waitTask; } catch (OperationCanceledException) { }
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
			int minCtx = _activeSession.ContextLength + GetCompactionReserve();
			LlmService? newService = registry.CreateService(role, _activeSession.Model, minCtx);
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
		List<string> candidates = new List<string> { "/compact", "/config", "/reload", "/model", "/finish", "/help" };
		Role? activeRole = roleService.GetRole(_activeSession.Role);
		LlmModel? activeModel = activeRole != null
			? registry.GetModelForRole(activeRole, _activeSession.Model, _activeSession.ContextLength + GetCompactionReserve())
			: null;
		if (activeRole != null)
		{
			string currentModelId = activeModel != null ? activeModel.ConfigId : _activeSession.Model + " (not available)";
			List<string> enabledModels = registry.GetEnabledModelsForRole(activeRole);
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

	private string ModelPricingLabel(string modelId, LlmRegistry registry)
	{
		LlmModel? model = registry.GetModel(modelId);
		if (model == null)
			return string.Empty;
		CostConfig cost = model.Config.Cost;
		return $"  in:${cost.Input:0.00} out:${cost.Output:0.00} /Mtok";
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
			bool ok;
			string output;
			int tokens = _terminatorTokens;

			if (_terminatorCalled)
			{
				ok = _terminatorSucceeded;
				output = _terminatorValue ?? string.Empty;
			}
			else
			{
				string salvaged = LastAssistantText();
				if (!string.IsNullOrEmpty(_lastFailure))
				{
					ok = false;
					output = string.IsNullOrEmpty(salvaged)
						? $"The {roleName} subagent could not finish: {_lastFailure}."
						: $"The {roleName} subagent could not finish: {_lastFailure}.\n\nLast progress before it stopped:\n{salvaged}";
				}
				else if (string.IsNullOrEmpty(salvaged))
				{
					ok = false;
					output = "The subagent finished without returning a result.";
				}
				else
				{
					ok = true;
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
