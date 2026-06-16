using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Spawns child agents on behalf of the root agent's subagent tool. A child is a real, announced,
// saved sub-session assigned a named role — its system prompt, model, and tools — seeded with a
// natural-language task. It runs to completion, terminating only when the model calls return_to_caller;
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

    public SubagentRunner(LlmRegistry registry, RoleService roleService, ITransportServer transport, Func<Session> currentSession)
    {
        _registry = registry;
        _roleService = roleService;
        _transport = transport;
        _currentSession = currentSession;
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
    public async Task<(bool ok, string text, int responseTokens)> RunSubagentAsync(string roleName, string prompt, int outputBudgetTokens, CancellationToken ct)
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

        string displayName = prompt.Length > 80 ? prompt.Substring(0, 80) : prompt;

        Session parent = _currentSession();
        BeastSession subData = new BeastSession(parent.AllocateChildId(), displayName, service.Model.ConfigId, role.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral, 0);
        Session subSession = new Session(subData, role.SystemPrompt, _transport, true);

        // Inject the worktree context at the top of the first prompt so the subagent knows where it is
        // operating (branch + path) rather than inferring it. Empty when the CWD is not a git checkout.
        string banner = await WorktreeBannerAsync(ct);
        string seededPrompt = string.IsNullOrEmpty(banner) ? prompt : $"{banner}\n\n{prompt}";
        subSession.AddUserMessage(seededPrompt);
        subSession.AnnounceToClient();
        parent.AddChild(subSession);

        // The child carries only its role's bound tools (no subagent tool), so nesting stops here.
        // fetch_url is injected by name like elsewhere (it needs the runWeb delegate). The terminator is
        // created in ToolFactory and added here: a Reviewer role (declaring finish_review) finishes with
        // finish_review, which carries an approval flag; every other subagent finishes with return_to_caller.
        ReturnSink sink = new ReturnSink();
        bool isReview = role.Tools.Contains("finish_review");
        string terminatorName = isReview ? "finish_review" : "return_to_caller";
        Tool terminatorTool = isReview
            ? ToolFactory.CreateFinishReviewTool((approved, comments) => { sink.Value = comments; sink.Approved = approved; sink.Returned = true; })
            : ToolFactory.CreateReturnToCallerTool(output => { sink.Value = output; sink.Returned = true; });

        // Each subagent run gets its own ReadFileExplorer: exploration is self-contained, so a subagent's
        // first read of a file always gets Explorer citations and never depends on what another agent read.
        List<Tool> withTerminator = new List<Tool>(role.BuiltTools);
        if (role.Tools.Contains("read_file"))
            withTerminator.Add(ToolFactory.CreateReadFileTool(new ReadFileExplorer(), _registry, _roleService, _currentSession));
        if (role.Tools.Contains("fetch_url"))
            withTerminator.Add(ToolFactory.CreateFetchUrlTool(_registry, _roleService, _currentSession));
        withTerminator.Add(terminatorTool);
        Tool[] fullTools = withTerminator.ToArray();
        Tool[] terminatorOnlyTools = new Tool[] { terminatorTool };

        subSession.SendBusy();
        try
        {
            // Generous turn cap so a working subagent can iterate, while still bounding runaway loops.
            const int kMaxTurns = 50;
            int responseTokens = 0;

            for (int turn = 1; turn <= kMaxTurns; turn++)
            {
                // On the last allotted turn the terminator is the only tool and is required, and the
                // output is hard-capped to the budget since there is no further turn to shorten it.
                bool lastTurn = turn == kMaxTurns;
                Tool[] turnTools = lastTurn ? terminatorOnlyTools : fullTools;
                string? forcedToolName = lastTurn ? terminatorName : null;
                int outputCap = lastTurn ? outputBudgetTokens : 0;

                ProtocolResult result = await service.RunToCompletionAsync(subSession, turnTools, forcedToolName, 0, outputCap, _transport, ct);
                if (result.Outcome != ProtocolCallOutcome.Success)
                    break;

                subSession.CommitAssistantTurn(result.Payload!);
                bool hasToolCalls = await ToolDispatch.DispatchAsync(result.Payload!, turnTools, subSession, _transport, ct);
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

                if (!hasToolCalls && !lastTurn)
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
                return (false, "The subagent finished without returning a result.", responseTokens);

            string output = sink.Value;

            // An approved review integrates the work: commit the worktree, then fast-forward the base branch
            // onto its feature branch. The git transcript is appended to the review regardless of outcome. A
            // failed integration (typically a rebase conflict) returns ok=false so the caller sees it as a
            // failure — the Task then gives the Developer (the only role with write access) another turn to
            // rebase and resolve, with the transcript naming exactly what must be fixed.
            if (isReview && sink.Approved)
            {
                (bool integrated, string integration) = await IntegrateApprovedBranchAsync(ct);
                output = $"{output}\n\n--- Integration ---\n{integration}";
                responseTokens = Math.Max(responseTokens, ToolDispatch.EstimateTokens(output));
                return (integrated, output, responseTokens);
            }

            return (true, output, responseTokens);
        }
        finally
        {
            if (!subSession.Ephemeral)
                SessionService.Save(subSession.Data, false);
            subSession.SendIdle();
        }
    }

    // Integrates an approved review with a strictly linear, rebase-based flow — never a merge commit, and
    // never left to the LLM. It commits everything in the current worktree onto its feature branch, finds
    // the base branch (the branch checked out in the primary worktree — the one the task was started from),
    // fast-forwards that base from its remote, rebases the feature branch onto it, then fast-forwards the
    // base onto the now-linear feature branch. After the rebase the feature branch is a strict descendant of
    // the base, so the final step can only ever fast-forward. It runs from the worktree's CWD and reaches the
    // base branch's checkout with git -C, since that branch is checked out in the primary worktree and cannot
    // be switched here. The base is derived from the primary worktree (git --git-common-dir), not guessed as
    // "main" or read from origin/HEAD, so it is correct for any base branch (master, develop, ...) and with
    // or without a remote. A rebase that hits conflicts is aborted cleanly and reported: that is the
    // Developer's to rebase and resolve, never an automatic merge. Returns (ok, transcript): ok is false on
    // any git failure, and the combined transcript is appended to the review either way.
    private static async Task<(bool ok, string transcript)> IntegrateApprovedBranchAsync(CancellationToken ct)
    {
        string script =
            "branch=$(git rev-parse --abbrev-ref HEAD)\n" +
            "common=$(git rev-parse --git-common-dir)\n" +
            "main_wt=$(cd \"$common\" && cd .. && pwd)\n" +
            "base=$(git -C \"$main_wt\" rev-parse --abbrev-ref HEAD)\n" +
            "if [ -z \"$base\" ] || [ \"$base\" = \"$branch\" ]; then echo \"Could not determine a distinct base branch (base='$base', feature='$branch').\"; exit 1; fi\n" +
            "echo \"Committing work on '$branch'...\"\n" +
            "git add -A\n" +
            "git commit -m \"Approved by reviewer on $branch\" || echo '(nothing to commit)'\n" +
            "echo \"Fast-forwarding base '$base' from its remote...\"\n" +
            "git -C \"$main_wt\" pull --ff-only || echo \"Note: could not pull '$base' (continuing with local '$base').\"\n" +
            "echo \"Rebasing '$branch' onto '$base' (linear history, no merge commit)...\"\n" +
            "if ! git rebase \"$base\"; then\n" +
            "  git rebase --abort 2>/dev/null\n" +
            "  echo \"Rebase of '$branch' onto '$base' hit conflicts and was aborted. The Developer must rebase onto '$base' and resolve them.\"\n" +
            "  exit 1\n" +
            "fi\n" +
            "echo \"Fast-forwarding base '$base' onto '$branch'...\"\n" +
            "git -C \"$main_wt\" merge --ff-only \"$branch\"\n";

        ToolResult result = await ShellTools.BashAsync("review_integrate", script, null, ct);

        StringBuilder sb = new StringBuilder();
        if (!string.IsNullOrEmpty(result.StdOut))
            sb.Append(result.StdOut);
        if (!string.IsNullOrEmpty(result.StdErr))
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(result.StdErr);
        }

        bool ok = result.ExitCode == 0;
        if (!ok)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append($"[integration failed: git exited {result.ExitCode}; the Developer must resolve this]");
        }

        string transcript = sb.Length > 0 ? sb.ToString() : "[integration produced no output]";
        return (ok, transcript);
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
