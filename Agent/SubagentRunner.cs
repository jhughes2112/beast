using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Spawns child agents on behalf of a delegation tool (the root's assign_work, the Developer's review_work).
// A child is a real, announced, saved sub-session assigned a named role — its system prompt, model, and
// tools — seeded with a natural-language task. It runs to completion, terminating only when the model calls
// its terminator (return_to_caller, or task_complete for the Developer / finish_review for the Reviewer);
// a turn that ends with no tool call is re-prompted to use it. The reply is measured and fit to the
// caller's budget before it is returned.
//
// Ownership / accounting rules:
//   - The returned result must fit the calling agent's remaining room (outputBudgetTokens); the fit
//     loop re-prompts for a shorter reply and hard-caps as a last resort.
//   - Cost is spent the moment each call is made, so every turn's cost accumulates in the
//     sub-session and the whole sum is rolled up into the calling (root) session at the end.
//   - currentSession is read at call time because the owning runner's active session changes across
//     compaction and role transitions; the delegate keeps the captured tool handlers valid.
public class SubagentRunner
{
    private readonly LlmRegistry _registry;
    private readonly RoleService _roleService;
    private readonly ITransportServer _transport;
    private readonly Func<Session> _currentSession;
    private readonly WebSearchConfig? _webSearchConfig;

    public SubagentRunner(LlmRegistry registry, RoleService roleService, ITransportServer transport, Func<Session> currentSession, WebSearchConfig? webSearchConfig)
    {
        _registry = registry;
        _roleService = roleService;
        _transport = transport;
        _currentSession = currentSession;
        _webSearchConfig = webSearchConfig;
    }

    // Mutable sink the terminator handler (return_to_caller or finish_review) writes into; read by the
    // drive loop after each tool round. Fields, not properties: the handler mutates them directly and the
    // loop resets Returned when it asks for a shorter retry. Approved is set only by finish_review.
    private sealed class ReturnSink
    {
        public string? Value;
        public bool Returned;
        public bool Approved;
    }

    // Runs an explicitly-invoked subagent: a child session assigned the named role, seeded with the
    // caller's natural-language prompt, carrying that role's own tools plus return_to_caller. The session
    // runs to completion, terminating only when the model calls return_to_caller, and the result is fit to
    // the caller's outputBudgetTokens. Returns ok=false with the error text as the result when the role is
    // unknown/ineligible, no model is available, there is no budget, the role's enter/exit hook fails, or
    // the subagent never returned a result; the calling handler surfaces that text to the caller.
    public Task<(bool ok, string text, int responseTokens)> RunSubagentAsync(string roleName, string prompt, int outputBudgetTokens, CancellationToken ct)
    {
        // A top-level subagent (spawned by the root's subagent tool) is parented to the currently-running
        // root session, read at call time since the active session changes across compaction/role switches.
        return RunForParentAsync(_currentSession(), roleName, prompt, outputBudgetTokens, ct);
    }

    // Runs a subagent under an explicit parent session. The public entry point parents to the root; the
    // Developer's review_work tool parents the Reviewer to the Developer's own sub-session so the session
    // tree nests correctly.
    private async Task<(bool ok, string text, int responseTokens)> RunForParentAsync(Session parent, string roleName, string prompt, int outputBudgetTokens, CancellationToken ct)
    {
        if (outputBudgetTokens <= 0)
            return (false, "No output budget remaining for a subagent.", 0);

        Role? role = _roleService.GetRole(roleName);
        if (role == null)
            return (false, $"Unknown role '{roleName}'.", 0);

        // Only Subagent-kind roles may run in a SubagentSession; an Agent role here is a caller error.
        if (role.Kind != RoleKind.Subagent)
            return (false, $"Role '{roleName}' is not a subagent role.", 0);

        LlmService? service = _registry.CreateService(role, string.Empty, 0);
        if (service == null)
            return (false, $"No model available for role '{roleName}'.", 0);

        // Name the sub-session "{Role} {task}" so the session tree shows which subagent it is and what it
        // was asked to do, the way root sessions show "{Role} {first message}". The task is the prompt's
        // first line, trimmed to keep the label to a single short row.
        int promptNewline = prompt.IndexOf('\n');
        string promptHead = (promptNewline >= 0 ? prompt.Substring(0, promptNewline) : prompt).Trim();
        if (promptHead.Length > 60)
            promptHead = promptHead.Substring(0, 60);
        string displayName = promptHead.Length > 0 ? $"{role.Name} {promptHead}" : role.Name;

        // Allocate the child id and immediately persist the parent so its bumped ChildCounter reaches disk.
        // The counter lives only in memory until a save; without this, a reload restores the parent's old
        // counter and the next subagent reissues this very id, overwriting this session's file on disk. A root
        // parent updates lastSession.json (harmless — it is already the last session); a subagent parent does not.
        string childId = parent.AllocateChildId();
        if (!parent.Ephemeral)
            SessionService.Save(parent.Data, !parent.IsSubagent);

        BeastSession subData = new BeastSession(childId, displayName, service.Model.ConfigId, role.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral, 0);
        Session subSession = new Session(subData, role.SystemPrompt, _transport, true);

        // The constructor no longer displays the system prompt; this sub-session has no other replay path,
        // so emit its (system-only) history to the client now. The seed user message displays when flushed.
        subSession.ReplayToTransport();

        // Inject the worktree context at the top of the first prompt so the subagent knows where it is
        // operating (branch + path) rather than inferring it. Empty when the CWD is not a git checkout.
        string banner = await WorktreeBannerAsync(ct);
        string seededPrompt = string.IsNullOrEmpty(banner) ? prompt : $"{banner}\n\n{prompt}";
        subSession.AddUserMessage(seededPrompt);
        subSession.AnnounceToClient();
        parent.AddChild(subSession);

        // The child carries its role's bound tools plus injected-by-name tools, but never the subagent tool,
        // so a child cannot fan out arbitrarily. The terminator is created in ToolFactory and selected by the
        // role's declared marker: a Reviewer (finish_review) finishes with finish_review, which carries an
        // approval flag; a Developer (task_complete) finishes with task_complete; every other subagent
        // finishes with return_to_caller.
        ReturnSink sink = new ReturnSink();
        bool isReview = role.Tools.Contains("finish_review");
        bool isDeveloper = role.Tools.Contains("task_complete");
        string terminatorName;
        Tool terminatorTool;
        if (isReview)
        {
            terminatorName = "finish_review";
            terminatorTool = ToolFactory.CreateFinishReviewTool((approved, comments) => { sink.Value = comments; sink.Approved = approved; sink.Returned = true; });
        }
        else if (isDeveloper)
        {
            terminatorName = "task_complete";
            terminatorTool = ToolFactory.CreateTaskCompleteTool(output => { sink.Value = output; sink.Returned = true; });
        }
        else
        {
            terminatorName = "return_to_caller";
            terminatorTool = ToolFactory.CreateReturnToCallerTool(output => { sink.Value = output; sink.Returned = true; });
        }

        // read_file is a plain raw reader; find_relevant_file_sections gets its own FileSummarizer so a subagent's digest of a
        // file is self-contained and never depends on what another agent read. The summarize/fetch/search tools
        // parent their helper sessions to this sub-session — not _currentSession (the root) — so the Explorer/Web
        // helpers an explicitly-invoked subagent spawns nest under the subagent that actually made the call. A
        // sub-session is fixed for its lifetime (no compaction/role switch), so capturing it directly is safe,
        // unlike the root whose active session is read at call time.
        List<Tool> withTerminator = new List<Tool>(role.BuiltTools);
        if (role.Tools.Contains("read_file"))
            withTerminator.Add(ToolFactory.CreateReadFileTool());
        if (role.Tools.Contains("find_relevant_file_sections"))
            withTerminator.Add(ToolFactory.CreateSummarizeFileTool(new FileSummarizer(), _registry, _roleService, () => subSession));
        if (role.Tools.Contains("fetch_url"))
            withTerminator.Add(ToolFactory.CreateFetchUrlTool(_registry, _roleService, () => subSession));
        if (role.Tools.Contains("search_web"))
        {
            Tool? searchWebTool = ToolFactory.CreateSearchWebTool(_webSearchConfig, _roleService, () => subSession);
            if (searchWebTool != null)
                withTerminator.Add(searchWebTool);
        }
        // review_work spawns a Reviewer parented to this sub-session (the Developer); it returns the verdict
        // only. The Developer integrates approved work itself with commit_and_rebase, so both are injected here.
        if (role.Tools.Contains("review_work"))
            withTerminator.Add(ToolFactory.CreateReviewWorkTool((reviewPrompt, budget, reviewCt) => RunForParentAsync(subSession, "Reviewer", reviewPrompt, budget, reviewCt)));
        if (role.Tools.Contains("commit_and_rebase"))
            withTerminator.Add(ToolFactory.CreateCommitAndRebaseTool());
        withTerminator.Add(terminatorTool);
        Tool[] fullTools = withTerminator.ToArray();
        Tool[] terminatorOnlyTools = new Tool[] { terminatorTool };

        // This subagent's own cancellation scope, linked to the caller's token. A /cancel on an ancestor
        // cascades down through the link and ends the whole subtree; a /cancel targeting this subagent alone
        // trips only this scope (ct stays live), which the loop below treats as "wait for the user to steer"
        // rather than returning to the caller — the caller's delegating tool call stays blocked meanwhile.
        CancellationTokenSource scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
        subSession.SetDispatchScope(scope);

        subSession.SendBusy();
        try
        {
            // Generous working turn cap so a working subagent can iterate. After it is reached the model
            // is given no further work tools and a few extra "wind-down" turns whose only job is to call
            // the terminator. The terminator cannot be truly forced (providers ignore tool_choice when
            // extended thinking is on), so a single wrap-up turn that calls the wrong tool — or no tool —
            // must not discard everything done so far. Instead we keep nudging across the wind-down turns
            // until the terminator is actually called, and salvage the last assistant text if it never is.
            const int kMaxWorkTurns = 50;
            const int kMaxWindDownTurns = 5;
            const int kMaxTurns = kMaxWorkTurns + kMaxWindDownTurns;
            int responseTokens = 0;

            for (int turn = 1; turn <= kMaxTurns; turn++)
            {
                // In the wind-down phase the terminator is the only tool and is requested, and the output
                // is hard-capped to the budget since the work is over and only the final result is wanted.
                bool windDown = turn > kMaxWorkTurns;
                bool lastTurn = turn == kMaxTurns;
                Tool[] turnTools = windDown ? terminatorOnlyTools : fullTools;
                string? forcedToolName = windDown ? terminatorName : null;
                int outputCap = windDown ? outputBudgetTokens : 0;

                ProtocolResult result = await service.RunToCompletionAsync(subSession, turnTools, forcedToolName, 0, outputCap, _transport, scope.Token);

                // A /cancel that tripped this scope (Interrupted is how the LLM call reports it). If it cascaded
                // from an ancestor (ct is down too) the whole subtree ends; otherwise it targeted this subagent
                // directly, so idle for user steering and resume with a fresh scope instead of returning.
                if (result.Outcome == ProtocolCallOutcome.Interrupted)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    CancellationTokenSource? resumed = await WaitForSteeringAsync(subSession, scope, ct);
                    if (resumed == null)
                        break;
                    scope = resumed;
                    continue;
                }
                if (result.Outcome != ProtocolCallOutcome.Success)
                    break;

                subSession.CommitAssistantTurn(result.Payload!);
                bool hasToolCalls;
                try
                {
                    hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, turnTools, subSession, _transport, scope.Token);
                }
                catch (OperationCanceledException) when (scope.IsCancellationRequested)
                {
                    // Same as above, but the cancel landed while a tool was running (dispatch throws rather than
                    // returning Interrupted). The aborted round's dangling tool calls are repaired on the next turn.
                    if (ct.IsCancellationRequested)
                        break;
                    CancellationTokenSource? resumed = await WaitForSteeringAsync(subSession, scope, ct);
                    if (resumed == null)
                        break;
                    scope = resumed;
                    continue;
                }
                if (hasToolCalls)
                    subSession.CommitToolResults(result.Payload!);

                if (sink.Returned)
                {
                    // The terminator call carries the reply; its turn's completion tokens are the
                    // server-measured size we charge against the caller's budget.
                    responseTokens = subSession.LastTokenUsage?.CompletionTokens ?? 0;
                    if (responseTokens <= outputBudgetTokens || lastTurn)
                        break;

                    // Complete but over budget with turns remaining: ask for a shorter return next turn.
                    sink.Returned = false;
                    subSession.AddUserMessage(
                        $"That output is about {responseTokens} tokens but must fit within {outputBudgetTokens} tokens. "
                        + $"Call {terminatorName} again with a shorter output, preserving the key details (file paths, line numbers, names, key output).");
                    continue;
                }

                if (lastTurn)
                    break;

                if (windDown)
                {
                    // Out of working turns and the terminator still was not called — a bare text turn, or a
                    // wrong/failed tool call (its error result is already in context). Press it to finish via
                    // the terminator rather than ending the session and discarding the work it has done.
                    subSession.AddUserMessage(
                        $"You are out of working turns. Call the {terminatorName} tool now with your final result, "
                        + "preserving the key details (file paths, line numbers, names, key output).");
                }
                else if (!hasToolCalls)
                {
                    // A turn that ends with no tool call cannot terminate the subagent: nudge it with the
                    // role's end-of-turn prompt (data-driven) to keep working and finish via its terminator.
                    string nudge = string.IsNullOrEmpty(role.EndOfTurnPrompt)
                        ? $"Continue the task, then call the {terminatorName} tool with your final result to finish."
                        : role.EndOfTurnPrompt;
                    subSession.AddUserMessage(nudge);
                }
            }

            // Cost is spent regardless of whether the subagent ever returned a usable result: roll the
            // sub-session's entire spend up into the calling agent so the root's cost reflects total spend.
            parent.RecordCost(subSession.TotalCost);

            if (sink.Value == null)
            {
                // The subagent never called its terminator (e.g. it kept calling the wrong tool through the
                // wind-down turns). Rather than throw away everything it produced, salvage its last assistant
                // text and return that — a partial answer is far less wasteful than discarding a long run.
                string salvaged = LastAssistantText(subSession);
                if (string.IsNullOrEmpty(salvaged))
                    return (false, "The subagent finished without returning a result.", responseTokens);
                return (true, salvaged, subSession.LastTokenUsage?.CompletionTokens ?? responseTokens);
            }

            string output = sink.Value;

            // A review is pure feedback now — it never touches git. Prefix the verdict so the Developer can act
            // on it directly: integrate with commit_and_rebase when approved, or address the comments and call
            // review_work again when rejected.
            if (isReview)
                output = sink.Approved ? $"[APPROVED]\n{output}" : $"[REJECTED]\n{output}";

            return (true, output, responseTokens);
        }
        finally
        {
            subSession.SetDispatchScope(null);
            scope.Dispose();
            if (!subSession.Ephemeral)
                SessionService.Save(subSession.Data, false);
            subSession.SendIdle();
        }
    }

    // Parks a directly-cancelled subagent until the user sends steering input, then disposes the old scope and
    // returns a fresh one (already registered) so the loop can resume. Returns null (caller should end) if the
    // wait is cancelled by an ancestor or app shutdown. The user's text is flushed in by the next BeginTurn.
    private async Task<CancellationTokenSource?> WaitForSteeringAsync(Session subSession, CancellationTokenSource current, CancellationToken ct)
    {
        subSession.SendIdle();
        try
        {
            await subSession.WaitForInputAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        if (ct.IsCancellationRequested)
            return null;

        subSession.SendBusy();
        subSession.SetDispatchScope(null);
        current.Dispose();
        CancellationTokenSource fresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        subSession.SetDispatchScope(fresh);
        return fresh;
    }

    // Returns the most recent assistant text in the sub-session, used to salvage a partial result when the
    // subagent ran out of turns without ever calling its terminator. Empty when no assistant message carried
    // any text (only tool calls).
    private static string LastAssistantText(Session session)
    {
        IReadOnlyList<CanonicalMessage> messages = session.Data.Messages;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage assistant && !string.IsNullOrWhiteSpace(assistant.Text))
                return assistant.Text;
        }
        return string.Empty;
    }

    // Builds the worktree context line injected at the top of a subagent's first prompt: the branch and
    // working directory it operates in. Returns empty when the CWD is not a git checkout so non-git tasks
    // are unaffected. Role-neutral phrasing: the Developer works here, the Reviewer reads here.
    private static async Task<string> WorktreeBannerAsync(CancellationToken ct)
    {
        string script =
            "branch=$(git rev-parse --abbrev-ref HEAD 2>/dev/null)\n" +
            "[ -z \"$branch\" ] && exit 0\n" +
            "echo \"$branch\"\n" +
            "pwd\n";

        ToolResult result = await ShellTools.BashAsync("subagent_worktree", script, null, ct);
        if (result.ExitCode != 0)
            return string.Empty;

        string[] lines = result.StdOut.Trim().Split('\n');
        if (lines.Length < 2)
            return string.Empty;

        string branch = lines[0].Trim();
        string path = lines[1].Trim();
        if (string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(path))
            return string.Empty;

        return $"[Worktree] Your working directory is the git worktree '{path}', on branch '{branch}'. All file reads, edits, and commands operate here.";
    }
}
