# Role Transitions

## Design

State-machine behavior lives directly on **Role**. Each role can optionally specify:
- **end_of_turn_prompt** — a question posed to an evaluator after each completed turn
- **truths** — a map from evaluator answers (free-form labels) to the next role name
- **summary_prompt** — run in a fork before evaluation to let the model do bookkeeping (e.g. update MEMORY.md, PLAN.md)

Roles with a non-empty `end_of_turn_prompt` and `truths` trigger automatic transitions. After each completed
worker turn the orchestrator summarizes, then runs the evaluator against the same model/role, gets a truth label,
and transitions to the next role by name. Roles without `end_of_turn_prompt`/`truths` run in steady state —
the user drives all changes via `/role`.

---

## Flow

```
RunAsync loop
  -> GetRole(session.Data.Role)           // active role from roles.json
  -> RunTurnAsync(...)                    // one LLM turn: tool dispatch loop inside
  -> when Completed: AdvanceRoleAsync(session, service, role, ct)
       -> SummarizeAsync(role.SummaryPrompt, roleTools)  // runs the summary turn in-session, returns result text
       -> build eval message: summary + end_of_turn_prompt + truth options
       -> ephemeral session (same model/role) with only the Answer tool
       -> LLM calls Answer(answer)  (retries up to 3 times)
            answer must START with one truth label verbatim, then continue with a
            briefing for the next phase (accomplished / remaining / constraints)
       -> map matched truth label → next role name
       -> empty next-role = stop and return to user
       -> non-empty next-role = fresh session whose first user prompt is the full
          answer (truth + briefing), loop continues
  -> on ContextFull: RunCompactionAsync(role.SummaryPrompt) → retry with compacted session
  -> on Failed: end turn, fall back to another model or display error
```

---

## Role Data Model

```json
{
  "name": "QAEngineer",
  "models": ["claude-opus-4"],
  "tools": ["shell", "file"],
  "system_prompt": "You are a QA engineer...",
  "summary_prompt": "Update MEMORY.md and PLAN.md. Output a detailed summary of what was done, what was found, and what is left.",
  "end_of_turn_prompt": "Build and run the test suite. Are all tests passing?",
  "truths": {
    "All tests pass":    "Reviewer",
    "Test failures":     "Coder"
  }
}
```

- `end_of_turn_prompt` — question given to the evaluator after each completed turn; null/empty means no automatic transitions
- `summary_prompt` — run in a fork before evaluation; also used during context compaction; empty = no summary step
- `truths` — truth label → next role name; empty string as value = stop and return to user

Roles with no `end_of_turn_prompt` or `truths` behave in steady state — user-driven only.

---

## Example: Code-Review Chain

```json
[
  {
    "name": "Planner",
    "models": ["*"],
    "tools": ["file"],
    "system_prompt": "You are a planning agent. Work with the user to define the task.",
    "summary_prompt": "Update MEMORY.md and PLAN.md. Summarize the agreed plan and any open questions.",
    "end_of_turn_prompt": "Has the plan been finalized and confirmed by the user?",
    "truths": {
      "Plan is complete": "Coder"
    }
  },
  {
    "name": "Coder",
    "models": ["*"],
    "tools": ["shell", "file"],
    "system_prompt": "You are a coding agent. Implement the plan.",
    "summary_prompt": "Update MEMORY.md and PLAN.md. Summarize what was implemented and any blockers.",
    "end_of_turn_prompt": "Build the project. Does it compile without errors?",
    "truths": {
      "Builds successfully": "QAEngineer",
      "Build errors found":  "Coder"
    }
  },
  {
    "name": "QAEngineer",
    "models": ["*"],
    "tools": ["shell", "file"],
    "system_prompt": "You are a QA engineer. Run tests and verify quality.",
    "summary_prompt": "Update MEMORY.md and PLAN.md. Summarize test results and any failures.",
    "end_of_turn_prompt": "Run the tests. Are they all passing?",
    "truths": {
      "All tests pass":  "Reviewer",
      "Test failures":   "Coder"
    }
  },
  {
    "name": "Reviewer",
    "models": ["*"],
    "tools": ["file"],
    "system_prompt": "You are a code reviewer. Review changes for quality and correctness.",
    "summary_prompt": "Update MEMORY.md and PLAN.md. Summarize review findings and outcome.",
    "end_of_turn_prompt": "Has the code review been completed and approved?",
    "truths": {
      "Approved":           "Planner",
      "Changes requested":  "Coder"
    }
  }
]
```

---

## Commands

| Command | Effect |
|---|---|
| `/role <name>` | Switch to named role immediately |

---

## Files

- `Agent/DataModels/Role.cs` — `Role` class: name, models, tools, system_prompt, summary_prompt, end_of_turn_prompt, truths
- `Agent/Services/RoleService.cs` — loads roles from `roles.json`; merges user (~/.beast/) and project (.beast/) definitions
- `Agent/SessionRunner.cs` — `AdvanceRoleAsync`: summary fork, evaluator dispatch, truth matching, role transition

---

## What Is Not Yet Implemented

- Automatic transitions triggered by tool results mid-turn (not just post-turn evaluation)
- Parallel sessions / multi-session orchestration
- Passing worker session context directly to the evaluator (evaluator currently receives only the summary)
