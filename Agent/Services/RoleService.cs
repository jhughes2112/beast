using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

// Provides the role definitions. The defaults are generated in code (see Role's factory methods) so the
// role system is versioned with the build, then externalized to the project's .beast/roles.json so the
// defaults can be edited: the file is written from the in-code set when missing, and loaded over the
// defaults when present. It is never stored in the home dir — the defaults already live in code.
public class RoleService
{
    public Dictionary<string, Role> Roles { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _workDirRolesPath;

    public RoleService(string workDir)
    {
        _workDirRolesPath = Path.Combine(workDir, ".beast", "roles.json");
        LoadRoles();
    }

    // The on-disk shape of roles.json: two blocks whose membership determines each role's kind.
    private sealed class RolesFile
    {
        [JsonPropertyName("agents")]
        public List<Role> Agents { get; set; } = new List<Role>();

        [JsonPropertyName("subagents")]
        public List<Role> Subagents { get; set; } = new List<Role>();
    }

    public Role? GetRole(string name)
    {
        Roles.TryGetValue(name, out Role? role);
        return role;
    }

    public void Reload()
    {
        LoadRoles();
    }

    // Names of the Subagent-kind roles — the only roles the subagent tool may target and the only
    // roles a SubagentSession may run.
    public List<Role> SubagentRoles()
    {
        List<Role> subagentRoles = new List<Role>();
        foreach (Role role in Roles.Values)
        {
            if (role.Kind == RoleKind.Subagent)
                subagentRoles.Add(role);
        }
        return subagentRoles;
    }

    private void LoadRoles()
    {
        Roles.Clear();

        // Build the in-code defaults first; they are authoritative and versioned with the build. The
        // Agents block holds the Agent-kind roles, the Subagents block the Subagent-kind ones.
        Role[] defaults =
        {
            DefaultRole(),
            DeveloperRole(),
            ReviewerRole(),
            ExplorerRole(),
            WebFetchRole(),
            WebSearchRole()
        };
        foreach (Role role in defaults)
            Roles[role.Name] = role;

        // Write the project's roles.json from the defaults when missing; otherwise load it and assign its
        // roles over the defaults so edits take effect (and any extra roles are added).
        if (!File.Exists(_workDirRolesPath))
            WriteRolesFile(_workDirRolesPath, defaults);
        else
            ApplyRolesFromFile(_workDirRolesPath);
    }

    // Serializes the current role set into roles.json, splitting it into the Agents and Subagents blocks
    // by kind. The kind itself is not written — the block a role sits in carries it.
    private static void WriteRolesFile(string path, IReadOnlyList<Role> roles)
    {
        RolesFile file = new RolesFile();
        foreach (Role role in roles)
        {
            if (role.Kind == RoleKind.Agent)
                file.Agents.Add(role);
            else
                file.Subagents.Add(role);
        }

        try
        {
            JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(file, options);

            string? dir = Path.GetDirectoryName(path);
            if (dir != null)
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: Failed to write roles.json at {path}: {ex.Message}");
        }
    }

    // Loads roles.json and assigns each block's roles over the in-code defaults in the dictionary. Each
    // role's kind comes from the block it appears in, not the file, so it is reconstructed here.
    private void ApplyRolesFromFile(string path)
    {
        RolesFile? file;
        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return;
            file = JsonSerializer.Deserialize<RolesFile>(json, ConfigJson.Options);
        }
        catch (JsonException ex)
        {
            string location = ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue
                ? $" (line {ex.LineNumber + 1}, column {ex.BytePositionInLine + 1})"
                : "";
            string detail = $"roles.json parse error at {path}{location}: {ex.Message}";

            Console.Error.WriteLine($"ERROR: Failed to parse {detail}");
            Console.Error.WriteLine("Fix it, or delete the file to regenerate defaults.");
            throw new ConfigException(detail);
        }
        catch (Exception ex)
        {
            string detail = $"roles.json load error at {path}: {ex.Message}";

            Console.Error.WriteLine($"ERROR: Failed to load {detail}");
            Console.Error.WriteLine("Fix it, or delete the file to regenerate defaults.");
            throw new ConfigException(detail);
        }

        if (file == null)
            return;

        foreach (Role role in file.Agents)
            AssignRole(role, RoleKind.Agent);
        foreach (Role role in file.Subagents)
            AssignRole(role, RoleKind.Subagent);
    }

    // Rebuilds a file-loaded role with the kind from its block (kind is not serialized) and assigns it
    // over any default of the same name. Skips nameless entries.
    private void AssignRole(Role role, RoleKind kind)
    {
        if (string.IsNullOrEmpty(role.Name))
            return;

        Role kinded = new Role(role.Name, role.Description, kind, role.Models, role.Tools, role.SystemPrompt, role.SummaryPrompt, role.EndOfTurnPrompt);
        Roles[kinded.Name] = kinded;
    }

    // ---- Role definitions ----
    // Each role is defined here so the role set is easy to extend without touching the Role data class.

    // Interactive entry point: read the project and decide what to do. No end-of-turn prompt, so it
    // behaves like ordinary chat — it responds and waits. The subagent tool hands concrete work off to
    // the Developer.  This should be a smart model.
    private static Role DefaultRole()
    {
		const string description = "Light conversation role";
        const string systemPrompt = """
			You are a helpful assistant with read-only privileges. Use tools to consider the state of the project, discuss it with the user, ask clarifying questions to understand scope.
			When there is clear direction enough to dispatch one or more concrete tasks, delegate to the Developer subagent with a clear objective. 
			The Developer makes the change, gets it reviewed and integrated, and reports back.
			""";
			
        const string summaryPrompt = """
            Output only a summary of the preceding conversation retaining the theme, critical concepts, current status, discovered context, most recent transaction in this discussion, and any other exact details that would help maintain continuity in a new conversation.  Be concise.
            """;
        List<string> tools = new List<string> { "read_file", "ls", "assign_work", "fetch_url", "search_web" };
        return new Role("Default", description, RoleKind.Agent, new List<string> { "*" }, tools, systemPrompt, summaryPrompt, string.Empty);
    }

    // A full-access worker invoked as a subagent: it makes the actual code changes in the worktree and
    // drives its own review. It is the only role with write access (write_file, edit_file, bash), so all
    // implementation — including fixes after a rejected review — happens here. It calls review_work to have
    // the Reviewer inspect and integrate the change, then finishes by calling task_complete. This should be
    // a smart model.
    private static Role DeveloperRole()
    {
		const string description = "Implements changes with full read/write/shell access, and gets them reviewed";
        const string summaryPrompt = """
			Output a concise summary of the preceding conversation preserving:
			- Goal & scope: what is being built, fixed, or investigated
			- Tech stack & environment: languages, frameworks, tools, versions, and any relevant config
			- Current status: what's working, what's broken, and where execution left off
			- Code context: key files, functions, classes, or logic discussed; include critical snippets verbatim if small enough
			- Errors & resolutions: any bugs encountered, root causes identified, and fixes applied or attempted
			- Decisions made: architectural choices, tradeoffs accepted, or approaches ruled out
			- Last action: the most recent code written, command run, or change made
			- Next steps: open tasks, unresolved issues, or what was about to be tried
			Be concise. Prioritize technical precision over narrative.
			""";
        const string systemPrompt =
            """
            You are a developer agent working in a git worktree. Use tools to make changes directly.
            When the changes are ready, call review_work for constructive feedback. Address anything in-scope for the work requested and call review_work again until it is approved.
            Once approved, call commit_and_rebase to finish the work and integrate it onto the base branch (ff merge only, no merge commits). If a conflict happens, resolve them, run 'git rebase --continue', then call commit_and_rebase again to finish.
            Be precise, directed, and maintain a sense of purpose without spending effort on high level considerations and interpretation. Consider the code the source of truth. Do not stray from the goal.
            If the goal cannot be achieved, be brief: report this and list what you attempted along with the exact output.
            Finish by calling task_complete with the review outcome and integration status.
            """;
        const string endOfTurnPrompt = "Are you finished? If so, review_work until it's approved. After approval, commit_and_rebase to check it in. Then call task_complete with the approval message.";
        // review_work, commit_and_rebase, and task_complete are markers: they have no registry entry and are
        // injected in code by SubagentRunner (review_work spawns the Reviewer; commit_and_rebase integrates the
        // work; task_complete is this role's terminator).
        List<string> tools = new List<string> { "bash", "read_file", "write_file", "edit_file", "ls", "fetch_url", "search_web", "review_work", "commit_and_rebase", "task_complete" };
        return new Role("Developer", description, RoleKind.Subagent, new List<string> { "*" }, tools, systemPrompt, summaryPrompt, endOfTurnPrompt);
    }

    // A read-only reviewer invoked by the Developer through review_work. It inspects the Developer's changes
    // but cannot modify them — only the Developer has write access. It finishes by calling finish_review with
    // an approval flag and comments; on approval SubagentRunner commits and rebases the worktree branch onto
    // its base branch (linear, no merge commit) and appends the git transcript to the review the Developer
    // receives. This should be a smart model, but different from the Developer.
    private static Role ReviewerRole()
    {
		const string description = "Reviews changes read-only; approves or rejects with comments";
		const string summaryPrompt = """
			Output a concise summary of the preceding conversation preserving:
			- Review target: PR/branch/commit being reviewed, its stated purpose, and the repo/codebase context
			- Scope covered: files, modules, or sections already reviewed vs. still pending
			- Issues found: bugs, logic errors, security concerns, performance problems, or anti-patterns flagged; include severity and file/line references where noted
			- Positives noted: good patterns, clean abstractions, or well-handled edge cases worth acknowledging
			- Standards & conventions: style guide, architectural patterns, or team norms applied during review
			- Unresolved questions: things to verify, clarify with the author, or cross-reference elsewhere in the codebase
			- Author exchanges: any discussion, pushback, or clarifications already given
			- Current verdict: approve, request changes, or still undecided — and why
			- Last position: the most recent file, function, or block examined
			- Next focus: what was about to be reviewed or followed up on
			Be concise. Preserve file names, function names, and issue descriptions exactly.
			""";
        const string systemPrompt =
            """
            You are a reviewer agent with read-only access to the worktree. Inspect the indicated changes against the goal you were given: check correctness, scope, code quality and that nothing obviously broke.
            If you need any outputs from build tools, use the bash tool but do not modify any files. Approve with caveats, concerns, and indicate whether follow-on tasks should be added to the plan. 
            If the code quality violates standards, has bugs, needs more cases handled, or in any other case should be improved, reject with specific, actionable comments for the developer.
            Call finish_review to complete this review round.
            """;
        const string endOfTurnPrompt = "When you have reached a decision, call the finish_review tool. Otherwise keep reviewing.";
        // finish_review is the terminator, created in code and added by SubagentRunner; it has no registry
        // entry, so it is listed here only as the marker that selects this role's terminator.
        List<string> tools = new List<string> { "bash", "read_file", "ls", "fetch_url", "search_web", "finish_review" };
        return new Role("Reviewer", description, RoleKind.Subagent, new List<string> { "*" }, tools, systemPrompt, summaryPrompt, endOfTurnPrompt);
    }

    // Used internally by the read_file tool (ReadFileExplorer.ExploreAsync) on the first read of a file in a
    // session. It is seeded with the caller's goal, the file path, and a line-numbered window of that file,
    // and replies with citations the caller can read directly: file, start line, and line count. It runs via
    // HelperSession, finishing by calling return_to_caller (forced on the last turn). This should be a fast
    // model, since it runs on every first read for discovery.
    private static Role ExplorerRole()
    {
		const string description = "To reduce context by providing a brief roadmap of a file relevant to a goal";
        const string systemPrompt =
            """
			You are a first-pass discovery agent. Given a goal and a block of content from the target file, generate an index of regions relevant to the stated goal so the caller can read them directly using read_file.
			Call the return_to_caller tool and deliver the citations as the output argument.
			The format should clearly include the range of line numbers and what the agent will find there in this exact form:
			lines <starting_line> to <ending_line> -> <names of functions and variables>
			lines <starting_line> to <ending_line> -> <names of functions and variables>
			
			Rules:
			Cite only regions whose content directly relates to the goal. Omit unrelated code, to increase signal to noise ratio.
			Line numbers must be exact; do not approximate.
			Name only symbols visible within the cited region.
			No prose, no markdown, no preamble: citations only.
			Do not describe behavior, propose changes, or speculate beyond what the content explicitly shows.
			""";
        const string endOfTurnPrompt = "Report the relevant locations with the return_to_caller tool.";
        List<string> tools = new List<string>();
        return new Role("Explorer", description, RoleKind.Subagent, new List<string> { "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }

    // Used internally by the fetch_url tool (WebFetch.FetchRawAsync). It is seeded with a URL, a goal,
    // and the paths of the files a fetch saved to /tmp/ (raw bytes, plus stripped-text and tag-skeleton views
    // for HTML), and is given read_file and bash (injected by HelperSession, see ToolFactory.CreateWebHelperTools)
    // to inspect, parse, or download them. It replies with only what the goal asks for via return_to_caller
    // (forced on the last turn). This should be a capable-enough model to pick the right file and parse it.
    private static Role WebFetchRole()
    {
		const string description = "To reduce context by returning the useful parts of a web page";
        const string systemPrompt =
			"""
			You are a web content extraction agent. The fetched resource has been saved to /tmp/ in several different convenient formats. Use whichever view best serves the goal: html-stripped text for readable content, the html tag skeleton for structure and navigation, and the unmodified raw file.
			If the resource was larger than 1mb, it will be truncated or missing. Fetch it yourself via bash.
			Your job is to filter out low value content. Return exactly what is asked for in the goal, be precise and complete, but minimal. If the page is paywalled, blocked, or uninformative, say so immediately.
			""";
        const string endOfTurnPrompt = "Report the relevant content with the return_to_caller tool.";
        // Resolved against the helper tool set (ToolFactory.BuildHelperTools), not the main registry: read_file
        // is the raw line-numbered reader (no Explorer round-trip) and bash is plain bash.
        List<string> tools = new List<string> { "read_file", "bash" };
        return new Role("WebFetch", description, RoleKind.Subagent, new List<string> { "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }

    // Used internally by the search_web tool (WebSearchOpenrouter.SearchWebAsync). It runs on the dedicated
    // OpenRouter web-search model configured under .beast settings (not a registry model), which retrieves
    // live results through the OpenRouter web plugin before the model answers. This role then returns only
    // what the caller's goal asks for via return_to_caller (forced on the last turn). It has no tools of its
    // own — the retrieval happens inside the model's own step, so there is nothing to inspect on disk. Its
    // Models list is empty because the service is built from the search model directly, not picked by role.
    private static Role WebSearchRole()
    {
		const string description = "To reduce context by returning only the web search results that serve the goal";
        const string systemPrompt =
            """
			You are a web search agent. The query has already been searched on the live web and the results are in front of you.
			Read them and return exactly what the goal asks for: precise, complete, and minimal, keeping source URLs where they are useful.
			Omit navigation, ads, and unrelated hits. If nothing relevant was found, or the search was blocked, say so plainly.
			Deliver your answer by calling the return_to_caller tool.
			""";
        const string endOfTurnPrompt = "Report the relevant findings with the return_to_caller tool.";
        List<string> tools = new List<string>();
        return new Role("WebSearch", description, RoleKind.Subagent, new List<string>(), tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }
}
