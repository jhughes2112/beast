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
            WebRole()
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
        const string systemPrompt = "You are a helpful assistant. Use read_file and ls to consider the current project, discuss it with the user, and when there is a concrete task to do, delegate it to the Developer subagent with a clear objective. The Developer makes the change, gets it reviewed and integrated, and reports back.";
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
        const string systemPrompt =
            """
            You are a developer agent working in a git worktree. Use tools to make changes directly.
            When the changes are ready, call review_work for constructive feedback. Address anything in-scope for the work requested and call review_work again until it is approved.
            Once approved, call commit_and_rebase to finish the work and integrate it onto the base branch (ff merge only, no merge commits). If a conflict happens, resolve them, run 'git rebase --continue', then call commit_and_rebase again to finish.
            Be precise, directed, and maintain a sense of purpose without spending effort on high level considerations and interpretation. Consider the code the source of truth. Do not stray from the goal.
            If the goal cannot be achieved, be brief: report this and list what you attempted along with the exact output.
            Finish by calling task_complete with the review outcome and integration status.
            """;
        const string endOfTurnPrompt = "Are you finished?  If so, review_work until it's approved. After approval, commit_and_rebase to check it in.  Then call task_complete with the approval message.";
        // review_work, commit_and_rebase, and task_complete are markers: they have no registry entry and are
        // injected in code by SubagentRunner (review_work spawns the Reviewer; commit_and_rebase integrates the
        // work; task_complete is this role's terminator).
        List<string> tools = new List<string> { "bash", "read_file", "write_file", "edit_file", "ls", "fetch_url", "search_web", "review_work", "commit_and_rebase", "task_complete" };
        return new Role("Developer", description, RoleKind.Subagent, new List<string> { "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }

    // A read-only reviewer invoked by the Developer through review_work. It inspects the Developer's changes
    // but cannot modify them — only the Developer has write access. It finishes by calling finish_review with
    // an approval flag and comments; on approval SubagentRunner commits and rebases the worktree branch onto
    // its base branch (linear, no merge commit) and appends the git transcript to the review the Developer
    // receives. This should be a smart model, but different from the Developer.
    private static Role ReviewerRole()
    {
		const string description = "Reviews changes read-only; approves or rejects with comments";
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
        return new Role("Reviewer", description, RoleKind.Subagent, new List<string> { "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
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
            You are the first-pass discovery agent that creates a very brief roadmap for an indicated file that pertains to the goal and cite them so the caller can read them directly.
            Reply with undecorated citations in the form the read_file tool takes: file path, starting line number, number of lines. On each entry, note naming the functions or variables of importance. 
            Do not propose changes or speculate beyond what the content shows.
            """;
        const string endOfTurnPrompt = "When you have found the relevant locations, call return_to_caller with your citations. Otherwise keep looking.";
        List<string> tools = new List<string>();
        return new Role("Explorer", description, RoleKind.Subagent, new List<string> { "local", "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }

    // Used internally by the fetch_url tool (WebFetch.FetchRawAsync). It is seeded with a URL, an objective,
    // and the paths of the files a fetch saved to /tmp/ (raw bytes, plus stripped-text and tag-skeleton views
    // for HTML), and is given read_file and bash (injected by HelperSession, see ToolFactory.CreateWebHelperTools)
    // to inspect, parse, or download them. It replies with only what the objective asks for via return_to_caller
    // (forced on the last turn). This should be a capable-enough model to pick the right file and parse it.
    private static Role WebRole()
    {
		const string description = "To reduce context by returning the useful parts of a web page";
        const string systemPrompt =
            """
            A web page has been fetched and stored in /tmp/ as multiple different versions of the same file. Consider the provided objective, and use read_file and bash to inspect these files: prefer the stripped-text view for prose, the tag-skeleton view to locate a section, and the raw file when you need exact markup or non-HTML data. 
            If the resource was too large to download, fetch it yourself with curl or wget via bash. 
            Reply with exactly what the objective asks for — precise but thorough so the response retains maximum value. 
            Be cautious about exceeding your context length, there is no compaction, you just fail.
            """;
        const string endOfTurnPrompt = "If you are finished, call return_to_caller with it. Otherwise keep working.";
        // Resolved against the helper tool set (ToolFactory.BuildHelperTools), not the main registry: read_file
        // is the raw line-numbered reader (no Explorer round-trip) and bash is plain bash.
        List<string> tools = new List<string> { "read_file", "bash" };
        return new Role("Web", description, RoleKind.Subagent, new List<string> { "local", "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }
}
