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
    public List<string> SubagentRoleNames()
    {
        List<string> names = new List<string>();
        foreach (Role role in Roles.Values)
        {
            if (role.Kind == RoleKind.Subagent)
                names.Add(role.Name);
        }
        return names;
    }

    private void LoadRoles()
    {
        Roles.Clear();

        Role defaultRole = DefaultRole();
        Role taskRole = TaskRole();
        Role toolsRole = ToolsRole();

        Roles[defaultRole.Name] = defaultRole;
        Roles[taskRole.Name] = taskRole;
        Roles[toolsRole.Name] = toolsRole;
    }

    // ---- Role definitions ----
    // Each role is defined here so the role set is easy to extend without touching the Role data class.

    // Interactive entry point: read the project and decide what to do. No end-of-turn prompt, so it
    // behaves like ordinary chat — it responds and waits. start_task hands off to the Task role.
    private static Role DefaultRole()
    {
        const string systemPrompt = "You are a helpful assistant. Use read_file and ls to consider the current project, discuss it with the user, and when there is a concrete task to do, call start_task with a clear objective to begin working it.";
        const string summaryPrompt = """
            Output only a summary of the preceding conversation retaining the theme, critical concepts, current status, discovered context, most recent transaction in this discussion, and any other exact details that would help maintain continuity in a new conversation.  Be concise.
            """;
        List<string> tools = new List<string> { "read_file", "ls", "start_task" };
        return new Role("Default", RoleKind.Agent, new List<string> { "*" }, tools, systemPrompt, summaryPrompt, string.Empty, string.Empty, string.Empty);
    }

    // Carries out a task by delegating to subagents. Kept on task by its end-of-turn prompt until it
    // calls task_complete.
    private static Role TaskRole()
    {
        const string systemPrompt = "You are a capable orchestrator. Read the MEMORY.md file for project-level knowledge, read PLAN.md for tasks in progress. Carry out the objective by delegating units of work to subagents, asking clarifying questions only when truly blocked.";
        const string summaryPrompt = """
            Update project-level learnings that have long-term value in MEMORY.md, update PLAN.md so that the status of the current task is reflected.
            Output only a summary of the preceding conversation retaining the objective, critical concepts, current status, discovered context, key next steps, and exact details that would help perform them. Be concise. Retain only that which will help complete the task.
            """;
        const string endOfTurnPrompt = "If the task is finished, call the task_complete tool with a status update. Otherwise keep working.";
        // Drives the task by delegating; it does not perform the work itself. task_complete is added in
        // code for Agent roles that have an end-of-turn prompt.
        List<string> tools = new List<string> { "read_file", "ls", "subagent" };
        return new Role("Task", RoleKind.Agent, new List<string> { "*" }, tools, systemPrompt, summaryPrompt, endOfTurnPrompt, string.Empty, string.Empty);
    }

    // A general worker invoked as a subagent. Finishes by calling return_to_caller.
    private static Role ToolsRole()
    {
        const string systemPrompt =
            """
            You are a worker agent carrying out a single delegated unit of work. Use your tools to accomplish the goal directly and report the minimum output needed.
            Do not interpret results beyond what the goal asks for.
            If the goal cannot be achieved, be brief: report this and list what you attempted along with the exact output.
            """;
        const string endOfTurnPrompt = "If you have achieved the goal, call the return_to_caller tool with the result. Otherwise keep working.";
        List<string> tools = new List<string> { "bash", "read_file", "write_file", "edit_file", "ls" };
        return new Role("Tools", RoleKind.Subagent, new List<string> { "*" }, tools, systemPrompt, string.Empty, endOfTurnPrompt, string.Empty, string.Empty);
    }
}
