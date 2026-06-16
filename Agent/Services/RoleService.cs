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
            TaskRole(),
            DeveloperRole(),
            ReviewerRole(),
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
            Console.Error.WriteLine($"ERROR: Failed to parse roles.json at {path}");
            Console.Error.WriteLine($"       {ex.Message}");
            if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
            {
                Console.Error.WriteLine($"       Line {ex.LineNumber + 1}, column {ex.BytePositionInLine + 1}");
            }
            Console.Error.WriteLine("Fix it, or delete the file to regenerate defaults.");
            throw new ConfigException("roles.json parse error");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to load roles.json from {path}");
            Console.Error.WriteLine($"       {ex.Message}");
            Console.Error.WriteLine("Fix it, or delete the file to regenerate defaults.");
            throw new ConfigException("roles.json load error");
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
    // behaves like ordinary chat — it responds and waits. start_task hands off to the Task role.  This should be a smart model.
    private static Role DefaultRole()
    {
		const string description = "Light conversation role";
        const string systemPrompt = "You are a helpful assistant. Use read_file and ls to consider the current project, discuss it with the user, and when there is a concrete task to do, call start_task with a clear objective to begin working it.";
        const string summaryPrompt = """
            Output only a summary of the preceding conversation retaining the theme, critical concepts, current status, discovered context, most recent transaction in this discussion, and any other exact details that would help maintain continuity in a new conversation.  Be concise.
            """;
        List<string> tools = new List<string> { "read_file", "ls", "start_task", "fetch_url", "search_web" };
        return new Role("Default", description, RoleKind.Agent, new List<string> { "*" }, tools, systemPrompt, summaryPrompt, string.Empty);
    }

    // Carries out a task by delegating to subagents. Kept on task by its end-of-turn prompt until it
    // calls task_complete.  This does not need to be a very smart model, just capable of calling a few tools and track task progress.
    private static Role TaskRole()
    {
		const string description = "To orchestrate the completion of a task";
        const string systemPrompt =
            """
            You are a capable orchestrator. Read the MEMORY.md file for project-level knowledge, read PLAN.md for tasks in progress.
            Carry out the objective by delegating units of work to subagents. When you call subagent you name the role to spawn and write the context for what it must do:
              - Assign the Developer role to make the actual code changes (it has full read/write/shell access).
              - Then assign the Reviewer role to inspect the result. An approved review commits the work and rebases the worktree branch onto main (linear, no merge commit); a rejected review returns comments for the Developer to address.
            Iterate between Developer and Reviewer until the review is approved. Ask clarifying questions only when truly blocked.
            """;
        const string summaryPrompt = """
            Update project-level learnings that have long-term value in MEMORY.md, update PLAN.md so that the status of the current task is reflected.
            Output only a summary of the preceding conversation retaining the objective, critical concepts, current status, discovered context, key next steps, and exact details that would help perform them. Be concise. Retain only that which will help complete the task.
            """;
        const string endOfTurnPrompt = "If the task is finished, call the task_complete tool with a status update. Otherwise keep working.";
        // Drives the task by delegating; it does not perform the work itself. task_complete is added in
        // code for Agent roles that have an end-of-turn prompt.
        List<string> tools = new List<string> { "read_file", "ls", "subagent" };
        return new Role("Task", description, RoleKind.Agent, new List<string> { "local", "*" }, tools, systemPrompt, summaryPrompt, endOfTurnPrompt);
    }

    // A full-access worker invoked as a subagent: it makes the actual code changes in the worktree.
    // It is the only role with write access (write_file, edit_file, bash), so all implementation —
    // including fixes after a rejected review — is delegated to it. Finishes by calling return_to_caller. This should be a smart model.
    private static Role DeveloperRole()
    {
		const string description = "Implements changes with full read/write/shell access";
        const string systemPrompt =
            """
            You are a developer agent carrying out a delegated unit of work in a git worktree. Use your tools to make the change directly: read what you need, edit files, and run commands to build and verify.
            Do not interpret results beyond what the goal asks for.
            If the goal cannot be achieved, be brief: report this and list what you attempted along with the exact output.
            """;
        const string endOfTurnPrompt = "If you have achieved the goal, call the return_to_caller tool with the result. Otherwise keep working.";
        List<string> tools = new List<string> { "bash", "read_file", "write_file", "edit_file", "ls", "fetch_url", "search_web" };
        return new Role("Developer", description, RoleKind.Subagent, new List<string> { "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }

    // A read-only reviewer invoked as a subagent. It inspects the Developer's changes but cannot modify
    // them — only the Developer has write access. It finishes by calling finish_review with an approval
    // flag and comments; on approval SubagentRunner commits and rebases the worktree branch onto main
    // (linear, no merge commit) and appends the git transcript to the review the orchestrator receives. This should be a smart model, but different from the Developer.
    private static Role ReviewerRole()
    {
		const string description = "Reviews changes read-only; approves and merges, or rejects with comments";
        const string systemPrompt =
            """
            You are a reviewer agent with read-only access to the worktree. Inspect the changes against the goal you were given: check correctness, scope, and that nothing obviously broke.
            You cannot modify files. If anything needs changing, reject with specific, actionable comments for the developer.
            When finished, call finish_review: approved=true to accept (this automatically commits the work and rebases the worktree branch onto main — you never run git yourself), or approved=false with comments describing exactly what must be fixed.
            """;
        const string endOfTurnPrompt = "When you have reached a decision, call the finish_review tool. Otherwise keep reviewing.";
        // finish_review is the terminator, created in code and added by SubagentRunner; it has no registry
        // entry, so it is listed here only as the marker that selects this role's terminator.
        List<string> tools = new List<string> { "read_file", "ls", "fetch_url", "search_web", "finish_review" };
        return new Role("Reviewer", description, RoleKind.Subagent, new List<string> { "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }

    // Used internally by the fetch_url tool (WebFetch.FetchRawAsync), which runs it as a single turn. It is
    // seeded with a URL, an objective, and the already-fetched page content, and replies with only what the
    // objective asks for. It has no tools and no end-of-turn loop — its reply is the answer. This should be a dumb model.
    private static Role WebRole()
    {
		const string description = "To request a URL and return only the useful parts";
        const string systemPrompt =
            """
            You are given a URL, an objective, and the already-fetched text content of that page. Reply with exactly what the objective asks for — precise but thorough so the response retains maximum value.
            """;
        List<string> tools = new List<string>();
        return new Role("Web", description, RoleKind.Subagent, new List<string> { "local", "*" }, tools, systemPrompt, string.Empty, string.Empty);
    }
}
