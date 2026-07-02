using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Drives one session to completion. Root sessions run indefinitely until cancelled; child sessions
// run until their terminator tool is called or their turn budget is exhausted. Behaviour is governed
// by the Role (compaction via SummaryPrompt, nudges via EndOfTurnPrompt) and by SessionRunConfig
// (structural differences: terminator, budget, turn cap, parent linkage).
//
// The active session may be replaced mid-run: root compaction returns a new session from RunAsync
// and the orchestrator restarts on it; child compaction swaps _activeSession in-place so the same
// handler continues on the compacted successor.
public class SessionHandler
{
	// May change when a child session compacts.
	private Session _activeSession;
	private LlmService? _service;
	private string? _nextModel;
	private bool _wantsCompact;

	// Terminator sink written by the tool callbacks, read after each dispatch round.
	private string? _terminatorValue;
	private bool _terminatorCalled;
	private bool _terminatorApproved;

	// Last measured input token count, recorded by the tracer or after each successful turn.
	// Used to compute whether the context window is full without a live model call.
	private int _lastInputTokens;

	// Guards NotifyChildComplete so it fires at most once (at the first terminator call or
	// failure). The child loop continues running after notifying, so later inputs are handled.
	private bool _notifiedComplete;

	public SessionHandler(Session session)
	{
		_activeSession = session;
	}

	// Drives the session until it naturally ends. For root sessions this runs until cancelled or
	// compaction; the returned Session may differ from the input (compacted successor). For child
	// sessions this runs until the terminator tool fires, the turn budget is exhausted, or the
	// parent cancels; the returned Session is always _activeSession.
	public async Task<Session> RunAsync(SessionRunConfig config, LlmRegistry registry, RoleService roleService, SettingsService settings, ITransportServer transport, ISessionOrchestrator orchestrator, WebSearchConfig? webSearchConfig, CancellationToken ct)
	{
		bool isRoot = config.Parent == null;

		_activeSession.ReplayToTransport();
		_activeSession.SendStats();
		_activeSession.AnnounceToClient();

		Role? role = roleService.GetRole(_activeSession.Role);
		_service = RefreshService(role, _activeSession, registry);
		_activeSession.UpdateCompletions(BuildCompletionCandidates(roleService, registry));

		const int kMaxWindDownTurns = 5;
		int maxWork = config.MaxWorkTurns > 0 ? config.MaxWorkTurns : int.MaxValue;
		int maxTotal = config.MaxWorkTurns > 0 ? config.MaxWorkTurns + kMaxWindDownTurns : int.MaxValue;
		int turn = 0;
		string? lastFailure = null;
		int responseTokens = 0;

		CancellationTokenSource scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_activeSession.SetDispatchScope(scope);

		try
		{
			while (!ct.IsCancellationRequested && turn < maxTotal)
			{
				// 1. Drain pending commands and queued text.
				DrainInput(roleService, registry, transport);

				// 2. Refresh role and service; update completions.
				role = roleService.GetRole(_activeSession.Role);
				LlmService? refreshed = RefreshService(role, _activeSession, registry);
				if (refreshed != null)
					_service = refreshed;
				_activeSession.UpdateCompletions(BuildCompletionCandidates(roleService, registry));

				// 3. Compact if requested.
				if (_wantsCompact)
				{
					_wantsCompact = false;
					if (isRoot)
					{
						Session? compacted = await CompactRootAsync(roleService, registry, transport, ct);
						if (compacted != null)
							return compacted;
					}
					else if (role != null && !string.IsNullOrEmpty(role.SummaryPrompt))
					{
						await CompactChildAsync(role, scope, config, registry, roleService, transport, orchestrator, ct);
					}
				}

				// 4. Wait if there is nothing to do.
				if (!_activeSession.NeedsAttention() || _service == null || role == null)
				{
					await WaitForInputOrModelAsync(ct, role, registry, transport);
					continue;
				}

				// 5. Run one turn cluster.
				_activeSession.EnsureNamedAndAnnounce();
				_activeSession.SendBusy();

				bool contextFull = false;

				scope.Dispose();
				scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
				_activeSession.SetDispatchScope(scope);

				bool windDown = turn >= maxWork;
				bool lastTurn = turn == maxTotal - 1;
				Tool[] tools = isRoot
					? BuildRootTools(role, _activeSession.WorkInProgress, settings.Settings, registry, roleService, webSearchConfig, orchestrator)
					: BuildChildTools(role, windDown, config, settings.Settings, registry, roleService, webSearchConfig, orchestrator);
				string? forcedTool = windDown ? config.TerminatorName : null;
				int outputCap = windDown ? config.OutputBudgetTokens : 0;

				bool workToolsActive = _activeSession.WorkInProgress;

				try
				{
					bool turnComplete = false;
					while (!turnComplete)
					{
						// Reconcile service with any deferred /model switch before each LLM call.
						if (_nextModel != null && (_service == null || _nextModel != _service.Model.ConfigId))
						{
							LlmModel? target = registry.GetModel(_nextModel);
							if (target != null)
							{
								_activeSession.UpdateModel(target);
								LlmService? switched = RefreshService(role, _activeSession, registry);
								if (switched != null)
									_service = switched;
								_nextModel = null;
							}
						}
						if (_service == null)
							break;
						LlmService service = _service;

						int compactionReserveInner = GetCompactionReserve();

						// Tracer: check context headroom before committing to a full call.
						int threshold = _activeSession.ContextWindow - compactionReserveInner;
						int pendingBytes = 0;
						foreach (CanonicalMessage msg in _activeSession.Bundle.Canonical.Messages)
						{
							if (msg is UserMessage um)
								pendingBytes += System.Text.Encoding.UTF8.GetByteCount(um.Text);
						}
						int estimate = _activeSession.ContextLength + (pendingBytes / 3) + _activeSession.Budget.PendingReserve;

						if (estimate >= threshold)
						{
							TracerResult tracer = await service.RunTracerAsync(_activeSession, tools, null, scope.Token);
							if (tracer.Succeeded)
							{
								_activeSession.Budget.RecordMeasurement(tracer.InputTokens);
								_lastInputTokens = tracer.InputTokens + tracer.CachedTokens;
								transport.Stats(_activeSession.Id, _activeSession.Model + ReasoningEffort.DisplaySuffix(service.Model.Config.ReasoningEffort), _activeSession.Role,
									_activeSession.CumulativeInputTokens, _activeSession.CumulativeOutputTokens,
									_activeSession.TotalCost, _activeSession.ContextWindow, tracer.InputTokens, tracer.CachedTokens);
								if (tracer.InputTokens + tracer.CachedTokens >= threshold)
								{
									transport.Status(_activeSession.Id, $"Context full ({tracer.InputTokens + tracer.CachedTokens}/{_activeSession.ContextWindow}), compacting...");
									contextFull = true;
									turnComplete = true;
									continue;
								}
							}
							else if (tracer.ContextBlown)
							{
								transport.Status(_activeSession.Id, $"Context exceeds limit ({tracer.ErrorMessage}), compacting...");
								contextFull = true;
								turnComplete = true;
								continue;
							}
						}

						ProtocolResult result = await service.RunToCompletionAsync(_activeSession, tools, forcedTool, compactionReserveInner, outputCap, transport, scope.Token);

						if (result.Outcome == ProtocolCallOutcome.ContextFull)
						{
							contextFull = true;
							turnComplete = true;
						}
						else if (result.Outcome == ProtocolCallOutcome.Interrupted)
						{
							if (ct.IsCancellationRequested)
							{
								turnComplete = true;
							}
							else
							{
								CancellationTokenSource? resumed = await WaitForSteeringAsync(scope, ct);
								if (resumed == null)
								{
									turnComplete = true;
								}
								else
								{
									scope = resumed;
									DrainInput(roleService, registry, transport);
									LlmService? steeredSvc = RefreshService(role, _activeSession, registry);
									if (steeredSvc != null)
										_service = steeredSvc;
								}
							}
						}
						else if (result.Outcome != ProtocolCallOutcome.Success)
						{
							int minCtx = _activeSession.ContextLength + compactionReserveInner;
							LlmService? fallback = registry.CreateFallbackService(service, minCtx);
							if (fallback != null)
							{
								_activeSession.QueryLog.FallbackTransition(service, fallback,
									result.Outcome == ProtocolCallOutcome.TooManyRetries ? "Rate limited after retries" : "Model failed",
									result.Outcome == ProtocolCallOutcome.TooManyRetries ? 10 : 5);
								_service = fallback;
								_activeSession.UpdateModel(fallback.Model);
								_activeSession.SendStats();
								transport.Status(_activeSession.Id, $"{(result.Outcome == ProtocolCallOutcome.TooManyRetries ? "Rate limited" : "Model failed")}; falling back to {fallback.Model.Config.Name}");
							}
							else
							{
								string detail = result.Outcome == ProtocolCallOutcome.TooManyRetries
									? "Rate limited after too many retries, and no fallback model is available."
									: string.IsNullOrEmpty(result.ErrorMessage) ? "Model failed and no fallback model is available." : result.ErrorMessage;
								_activeSession.QueryLog.SessionFailure(_activeSession, service, detail, service.RoleModelIds.Count);
								transport.Error(_activeSession.Id, detail);
								lastFailure = string.IsNullOrEmpty(result.ErrorMessage) ? "all models failed" : result.ErrorMessage;
								turnComplete = true;
							}
						}
						else
						{
							_activeSession.CommitAssistantTurn(result.Payload!);
							if (result.Payload!.Usage.PromptTokens > 0)
								_lastInputTokens = result.Payload.Usage.PromptTokens;
							bool hasToolCalls;
							try
							{
								hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, tools, _activeSession, transport, scope.Token);
							}
							catch (OperationCanceledException) when (scope.IsCancellationRequested && !ct.IsCancellationRequested)
							{
								Console.Error.WriteLine($"[SessionHandler] {role.Name} sub-session {_activeSession.Id} dispatch cancelled (ancestor cancel: {ct.IsCancellationRequested}).");
								if (ct.IsCancellationRequested)
								{
									turnComplete = true;
									break;
								}
								CancellationTokenSource? resumed = await WaitForSteeringAsync(scope, ct);
								if (resumed == null)
								{
									turnComplete = true;
									break;
								}
								scope = resumed;
								DrainInput(roleService, registry, transport);
								LlmService? cancelledSvc = RefreshService(role, _activeSession, registry);
								if (cancelledSvc != null)
									_service = cancelledSvc;
								continue;
							}

							if (hasToolCalls)
								_activeSession.CommitToolResults(result.Payload!);

							if (isRoot)
							{
								string? steering = _activeSession.TryDequeueLeadingText();
								if (!string.IsNullOrEmpty(steering))
									_activeSession.Bundle.OnUserMessage(steering);

								if (_activeSession.HasPending)
								{
									turnComplete = true;
								}
								else if (!string.IsNullOrEmpty(steering))
								{
									turnComplete = false;
								}
								else if (!hasToolCalls)
								{
									if (_activeSession.WorkInProgress && !string.IsNullOrEmpty(role.EndOfTurnPrompt))
									{
										_activeSession.AddUserMessage(role.EndOfTurnPrompt);
										turnComplete = false;
									}
									else
									{
										turnComplete = true;
									}
								}
								else
								{
									if (_activeSession.WorkInProgress != workToolsActive)
									{
										workToolsActive = _activeSession.WorkInProgress;
										tools = BuildRootTools(role, workToolsActive, settings.Settings, registry, roleService, webSearchConfig, orchestrator);
									}
									turnComplete = false;
								}
							}
							else
							{
								if (_terminatorCalled)
								{
									responseTokens = _activeSession.LastTokenUsage?.CompletionTokens ?? 0;
									if (config.OutputBudgetTokens > 0 && responseTokens > config.OutputBudgetTokens && !lastTurn)
									{
										_terminatorCalled = false;
										_activeSession.AddUserMessage(
											$"That output is about {responseTokens} tokens but must fit within {config.OutputBudgetTokens} tokens. "
											+ $"Call {config.TerminatorName} again with a shorter output, preserving the key details (file paths, line numbers, names, key output).");
									}
									else
									{
										turnComplete = true;
									}
								}
								else if (windDown)
								{
									_activeSession.AddUserMessage(
										$"You are out of working turns. Call the {config.TerminatorName} tool now with your final result, "
										+ "preserving the key details (file paths, line numbers, names, key output).");
									turnComplete = true;
								}
								else if (!hasToolCalls)
								{
									string nudge = string.IsNullOrEmpty(role.EndOfTurnPrompt)
										? $"Continue the task, then call the {config.TerminatorName} tool with your final result to finish."
										: role.EndOfTurnPrompt;
									_activeSession.AddUserMessage(nudge);
									turnComplete = true;
								}
								else
								{
									turnComplete = true;
								}
							}

							_activeSession.SendStats();
						}
					}
				}
				catch (OperationCanceledException) when (scope.IsCancellationRequested && !ct.IsCancellationRequested)
				{
					Console.Error.WriteLine($"[SessionHandler] Session {_activeSession.Id} turn interrupted between tool calls.");
					_activeSession.MarkInterrupted();
				}
				finally
				{
					_activeSession.SetDispatchScope(null);
					_activeSession.SendIdle();
					if (!_activeSession.Ephemeral)
						SaveRoot(_activeSession);
				}

				if (contextFull)
				{
					if (isRoot)
					{
						Session? compacted = await CompactRootAsync(roleService, registry, transport, ct);
						if (compacted != null)
							return compacted;
						_service = null;
					}
					else if (role != null && !string.IsNullOrEmpty(role.SummaryPrompt))
					{
						bool compacted = await CompactChildAsync(role, scope, config, registry, roleService, transport, orchestrator, ct);
						if (!compacted)
							transport.Status(_activeSession.Id, "Context window full and compaction failed. Use /model to switch to a larger model.");
					}
					else
					{
						transport.Status(_activeSession.Id, "Context window full. Use /compact to summarize or /model to switch to a larger model.");
					}
				}

				// Notify the caller once on the first terminator call or failure, then stay alive
				// to accept new user input.
				if ((_terminatorCalled || lastFailure != null) && !_notifiedComplete)
				{
					_notifiedComplete = true;
					NotifyChildComplete(lastFailure, responseTokens, role?.Name ?? _activeSession.Role, config);
					lastFailure = null;
					_terminatorCalled = false;
				}
				turn++;
			}
		}
		catch (OperationCanceledException)
		{
			if (!ct.IsCancellationRequested)
				Console.Error.WriteLine($"[SessionHandler] Session {_activeSession.Id} exited on unexpected OCE.");
		}
		finally
		{
			scope.Dispose();
			if (!_activeSession.Ephemeral)
				SaveRoot(_activeSession);
			if (config.Parent != null)
				config.Parent.RecordCost(_activeSession.TotalCost);
			_activeSession.SendIdle();
			if (!isRoot && !_notifiedComplete)
				NotifyChildComplete(lastFailure, responseTokens, role?.Name ?? _activeSession.Role, config);
		}

		return _activeSession;
	}

	private async Task<Session?> CompactRootAsync(RoleService roleService, LlmRegistry registry, ITransportServer transport, CancellationToken ct)
	{
		Role? role = roleService.GetRole(_activeSession.Role);
		if (role == null || string.IsNullOrEmpty(role.SummaryPrompt))
		{
			transport.Status(_activeSession.Id, "[Compaction] No role or summary prompt available.");
			return null;
		}

		transport.Status(_activeSession.Id, "[Compaction] Started.");
		string? summary = await Summarizer.SummarizeAsync(_activeSession, role.SummaryPrompt, Array.Empty<Tool>(), registry, roleService, transport, ct);
		if (string.IsNullOrWhiteSpace(summary))
		{
			transport.Status(_activeSession.Id, "[Compaction] Failed.");
			return null;
		}

		string newDisplayName = Session.IncrementDisplayName(_activeSession.DisplayName);
		string newId = Guid.NewGuid().ToString();
		SaveRoot(_activeSession);

		BeastSession freshData = new BeastSession(newId, newDisplayName, _activeSession.Model, _activeSession.Role,
			new List<CanonicalMessage>(), null, 0m, 0, 0, 0, _activeSession.Ephemeral);
		Session newSession = new Session(freshData, role.SystemPrompt, transport, false);
		if (_activeSession.WorkInProgress)
			newSession.BeginWork();

		newSession.AnnounceToClient();
		transport.SessionReset(newSession.Id);
		newSession.Bundle.Canonical.OnUserMessage(summary);
		if (!newSession.Ephemeral)
			SaveRoot(newSession);

		transport.Status(_activeSession.Id, "[Compaction] Complete.");
		return newSession;
	}

	// Summarizes _activeSession, creates a new child session under the same parent, and updates
	// _activeSession and _service so the loop continues on the compacted successor.
	private async Task<bool> CompactChildAsync(Role role, CancellationTokenSource scope, SessionRunConfig config, LlmRegistry registry, RoleService roleService, ITransportServer transport, ISessionOrchestrator orchestrator, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(role.SummaryPrompt))
		{
			transport.Status(_activeSession.Id, "[Compaction] No summary prompt available.");
			return false;
		}

		transport.Status(_activeSession.Id, "[Compaction] Started.");
		string? summary = await Summarizer.SummarizeAsync(_activeSession, role.SummaryPrompt, Array.Empty<Tool>(), registry, roleService, transport, ct);
		if (string.IsNullOrWhiteSpace(summary))
		{
			transport.Status(_activeSession.Id, "[Compaction] Failed.");
			return false;
		}

		LlmService? newService = registry.CreateService(role, _activeSession.Model, 0);
		if (newService == null)
		{
			transport.Status(_activeSession.Id, "[Compaction] Failed to create a fresh model service.");
			return false;
		}

		Session predecessor = _activeSession;
		predecessor.SetDispatchScope(null);
		predecessor.SendIdle();
		if (!predecessor.Ephemeral)
			SessionService.Save(predecessor.Data);

		// New child ID under the same grandparent. If there is no parent (restored orphan), use a new GUID.
		string childId;
		bool parentEphemeral;
		if (config.Parent != null)
		{
			childId = config.Parent.AllocateChildId();
			if (!config.Parent.Ephemeral)
				SessionService.Save(config.Parent.Data);
			parentEphemeral = config.Parent.Ephemeral;
		}
		else
		{
			childId = Guid.NewGuid().ToString();
			parentEphemeral = predecessor.Ephemeral;
		}

		string newDisplayName = Session.IncrementDisplayName(predecessor.DisplayName);
		BeastSession compactedData = new BeastSession(childId, newDisplayName, newService.Model.ConfigId, role.Name,
			new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parentEphemeral);
		Session compacted = new Session(compactedData, role.SystemPrompt, transport, true);
		compacted.UpdateModel(newService.Model);
		compacted.SetDispatchScope(scope);
		compacted.SendBusy();
		compacted.Bundle.Canonical.OnUserMessage(summary);
		compacted.AnnounceToClient();
		compacted.ReplayToTransport();
		if (config.Parent != null)
			config.Parent.AddChild(compacted);

		_activeSession = compacted;
		_service = newService;
		orchestrator.UnregisterSession(predecessor.Id);
		orchestrator.RegisterSession(compacted);
		transport.Status(predecessor.Id, "[Compaction] Complete.");
		return true;
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
	private void DrainInput(RoleService roleService, LlmRegistry registry, ITransportServer transport)
	{
		while (_activeSession.TryDequeuePending(out string? line))
		{
			if (!line!.StartsWith("/", StringComparison.Ordinal))
			{
				if (IsContextBlocked)
				{
					transport.Status(_activeSession.Id, "Context window full — use /compact or /model <id> before sending more input.");
					continue;
				}
				_activeSession.Bundle.OnUserMessage(line);

				// New input on a completed session: clear status so the session runs again.
				if (_activeSession.Status != SessionStatus.Ongoing)
					_activeSession.ResumeFromComplete();
				continue;
			}

			string trimmed = line.TrimStart('/').Trim();
			string verb;
			string? args = null;
			int spaceIdx = trimmed.IndexOf(' ');
			if (spaceIdx >= 0)
			{
				verb = trimmed.Substring(0, spaceIdx).ToLowerInvariant();
				args = trimmed.Substring(spaceIdx + 1).Trim();
			}
			else
			{
				verb = trimmed.ToLowerInvariant();
			}

			switch (verb)
			{
				case "compact":
					_wantsCompact = true;
					if (_activeSession.Status != SessionStatus.Ongoing)
						_activeSession.ResumeFromComplete();
					break;
				case "model":
					if (args != null)
					{
						int modelArgSpace = args.IndexOf(' ');
						string modelArg = modelArgSpace >= 0 ? args.Substring(0, modelArgSpace) : args;
						Role? modelRole = roleService.GetRole(_activeSession.Role);
						LlmModel? targetModel = modelRole != null ? registry.GetModelForRole(modelRole, modelArg, 0) : null;
						if (targetModel == null)
						{
							transport.Error(_activeSession.Id, $"Unknown model: {modelArg}");
						}
						else
						{
							int minRequired = _activeSession.ContextLength + GetCompactionReserve();
							if (targetModel.Config.ContextWindow <= minRequired)
							{
								transport.Error(_activeSession.Id, $"Model '{modelArg}' context window ({targetModel.Config.ContextWindow}) is too small for the current conversation ({minRequired} tokens needed).");
							}
							else
							{
								_nextModel = targetModel.ConfigId;
								_activeSession.MarkModelUserSelected(modelArg);
								registry.ResetAvailability(modelArg);
								_lastInputTokens = 0;
								transport.Status(_activeSession.Id, $"Model queued: {modelArg}");
								if (_activeSession.Status != SessionStatus.Ongoing)
									_activeSession.ResumeFromComplete();
							}
						}
					}
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

	// ---- Tool building ----

	private Tool[] BuildRootTools(Role role, bool workInProgress, BeastSettings beastSettings, LlmRegistry registry, RoleService roleService, WebSearchConfig? webSearchConfig, ISessionOrchestrator orchestrator)
	{
		return ToolFactory.BuildForRole(
			beastSettings,
			role,
			registry,
			roleService,
			_activeSession,
			webSearchConfig,
			workInProgress,
			(prompt, budget, spawnCt) => orchestrator.SpawnChildAsync(beastSettings, _activeSession, "Developer", prompt, budget, spawnCt),
			() => _activeSession.BeginWork(),
			() => _activeSession.EndWork(),
			null, null, null, null);
	}

	private Tool[] BuildChildTools(Role role, bool windDown, SessionRunConfig config, BeastSettings beastSettings, LlmRegistry registry, RoleService roleService, WebSearchConfig? webSearchConfig, ISessionOrchestrator orchestrator)
	{
		bool isReview = string.Equals(config.TerminatorName, "finish_review", StringComparison.Ordinal);
		bool isDeveloper = string.Equals(config.TerminatorName, "task_complete", StringComparison.Ordinal);

		Tool[] full = ToolFactory.BuildForRole(
			beastSettings,
			role,
			registry,
			roleService,
			_activeSession,
			webSearchConfig,
			false,
			null, null, null,
			isDeveloper
				? (Func<string, int, CancellationToken, Task<(bool ok, string text, int responseTokens)>>)
				  ((prompt, budget, reviewCt) => orchestrator.SpawnChildAsync(beastSettings, _activeSession, "Reviewer", prompt, budget, reviewCt))
				: null,
			isDeveloper
				? (Action<string>)(output => { _terminatorValue = output; _terminatorCalled = true; })
				: null,
			isReview
				? (Action<bool, string>)((approved, comments) => { _terminatorValue = comments; _terminatorApproved = approved; _terminatorCalled = true; })
				: null,
			(!isReview && !isDeveloper)
				? (Action<string>)(output => { _terminatorValue = output; _terminatorCalled = true; })
				: null);

		if (!windDown)
			return full;

		// Wind-down: restrict to the terminator tool only.
		Tool? terminator = null;
		foreach (Tool t in full)
		{
			if (string.Equals(t.Definition.Function.Name, config.TerminatorName, StringComparison.Ordinal))
			{
				terminator = t;
				break;
			}
		}
		return terminator != null ? new Tool[] { terminator } : full;
	}

	// ---- Steering / idle waits ----

	// Parks the child session until the user sends steering input after a direct /cancel, then
	// returns a fresh scope. Returns null if the wait is cancelled by an ancestor or shutdown.
	private async Task<CancellationTokenSource?> WaitForSteeringAsync(CancellationTokenSource current, CancellationToken ct)
	{
		_activeSession.SendIdle();
		try
		{
			await _activeSession.WaitForInputAsync(ct);
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		if (ct.IsCancellationRequested)
			return null;

		_activeSession.SendBusy();
		_activeSession.SetDispatchScope(null);
		current.Dispose();
		CancellationTokenSource fresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_activeSession.SetDispatchScope(fresh);
		return fresh;
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

	private LlmService? RefreshService(Role? role, Session session, LlmRegistry registry)
	{
		if (_service != null && !_service.IsDown && _service.Model.ConfigId == session.Model)
			return null;
		int minCtx = session.ContextLength + GetCompactionReserve();
		LlmService? newService = registry.CreateService(role, session.Model, minCtx);
		if (newService != null)
			session.UpdateModel(newService.Model);
		return newService;
	}

	private int GetCompactionReserve()
	{
		return Math.Min((int)(_activeSession.ContextWindow * 0.1), 7500);
	}

	private void SaveRoot(Session session)
	{
		if (session.InferDisplayName())
			session.AnnounceToClient();
		SessionService.Save(session.Data);
	}

	private List<string> BuildCompletionCandidates(RoleService roleService, LlmRegistry registry)
	{
		List<string> candidates = new List<string> { "/compact", "/reload", "/model", "/finish", "/help" };
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

	// Calls OnComplete with the right result once the child loop exits. Handles the three outcomes:
	// terminator called cleanly, run failed with a known reason, or turns exhausted (salvage last text).
	private void NotifyChildComplete(string? lastFailure, int responseTokens, string roleName, SessionRunConfig config)
	{
		if (config.OnComplete == null)
			return;

		if (_terminatorCalled)
		{
			string output = _terminatorValue ?? string.Empty;
			bool isReview = string.Equals(config.TerminatorName, "finish_review", StringComparison.Ordinal);
			if (isReview)
				output = _terminatorApproved ? $"[APPROVED]\n{output}" : $"[REJECTED]\n{output}";
			config.OnComplete(true, output, responseTokens);
			return;
		}

		string salvaged = LastAssistantText();
		if (!string.IsNullOrEmpty(lastFailure))
		{
			string message = string.IsNullOrEmpty(salvaged)
				? $"The {roleName} subagent could not finish: {lastFailure}."
				: $"The {roleName} subagent could not finish: {lastFailure}.\n\nLast progress before it stopped:\n{salvaged}";
			config.OnComplete(false, message, responseTokens);
			return;
		}

		if (string.IsNullOrEmpty(salvaged))
		{
			config.OnComplete(false, "The subagent finished without returning a result.", responseTokens);
			return;
		}

		config.OnComplete(true, salvaged, _activeSession.LastTokenUsage?.CompletionTokens ?? responseTokens);
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
