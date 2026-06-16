using System;
using System.Collections.Generic;

// Provides the role definitions. Roles are generated in code (see Role's factory methods), not loaded
// from disk, so the role system is structured and versioned with the build.
public class RoleService
{
    public Dictionary<string, Role> Roles { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public RoleService()
    {
        LoadRoles();
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

        Role defaultRole = DefaultRole();
        Role taskRole = TaskRole();
        Role developerRole = DeveloperRole();
        Role reviewerRole = ReviewerRole();
        Role webRole = WebRole();

        Roles[defaultRole.Name] = defaultRole;
        Roles[taskRole.Name] = taskRole;
        Roles[developerRole.Name] = developerRole;
        Roles[reviewerRole.Name] = reviewerRole;
		Roles[webRole.Name] = webRole;
    }

    // ---- Role definitions ----
    // Each role is defined here so the role set is easy to extend without touching the Role data class.

    // Interactive entry point: read the project and decide what to do. No end-of-turn prompt, so it
    // behaves like ordinary chat — it responds and waits. start_task hands off to the Task role.
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
    // calls task_complete.
    private static Role TaskRole()
    {
		const string description = "To orchestrate the completion of a task";
        const string systemPrompt =
            """
            You are a capable orchestrator. Read the MEMORY.md file for project-level knowledge, read PLAN.md for tasks in progress.
            Carry out the objective by delegating units of work to subagents. When you call subagent you name the role to spawn and write the context for what it must do:
              - Assign the Developer role to make the actual code changes (it has full read/write/shell access).
              - Then assign the Reviewer role to inspect the result. An approved review commits the work and rebases the worktree branch onto main (linear, no merge commit); a rejected review returns comments for the Developer to address.
            Iterate Developer -> Reviewer until the review is approved. Ask clarifying questions only when truly blocked.
            """;
        const string summaryPrompt = """
            Update project-level learnings that have long-term value in MEMORY.md, update PLAN.md so that the status of the current task is reflected.
            Output only a summary of the preceding conversation retaining the objective, critical concepts, current status, discovered context, key next steps, and exact details that would help perform them. Be concise. Retain only that which will help complete the task.
            """;
        const string endOfTurnPrompt = "If the task is finished, call the task_complete tool with a status update. Otherwise keep working.";
        // Drives the task by delegating; it does not perform the work itself. task_complete is added in
        // code for Agent roles that have an end-of-turn prompt.
        List<string> tools = new List<string> { "read_file", "ls", "subagent" };
        return new Role("Task", description, RoleKind.Agent, new List<string> { "*" }, tools, systemPrompt, summaryPrompt, endOfTurnPrompt);
    }

    // A full-access worker invoked as a subagent: it makes the actual code changes in the worktree.
    // It is the only role with write access (write_file, edit_file, bash), so all implementation —
    // including fixes after a rejected review — is delegated to it. Finishes by calling return_to_caller.
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
        return new Role("Developer", description, RoleKind.Subagent, new List<string> { "local", "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }

    // A read-only reviewer invoked as a subagent. It inspects the Developer's changes but cannot modify
    // them — only the Developer has write access. It finishes by calling finish_review with an approval
    // flag and comments; on approval SubagentRunner commits and rebases the worktree branch onto main
    // (linear, no merge commit) and appends the git transcript to the review the orchestrator receives.
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
        return new Role("Reviewer", description, RoleKind.Subagent, new List<string> { "local", "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt);
    }

    // Used internally by the fetch_url tool (WebFetch.FetchRawAsync), which runs it as a single turn. It is
    // seeded with a URL, an objective, and the already-fetched page content, and replies with only what the
    // objective asks for. It has no tools and no end-of-turn loop — its reply is the answer.
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
