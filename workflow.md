# Role Transitions

## Design

State-machine behavior lives directly on **Role**. Each role can optionally specify:
- **query** — a question posed to an evaluator after each completed turn
- **evaluatorRole** — the role that investigates and answers the query
- **truths** — a map from evaluator answers (free-form labels) to the next role name

Roles with a non-empty `query` and `truths` trigger automatic transitions. After each completed
worker turn the orchestrator runs the evaluator, gets a truth label, and transitions to the next
role by name. Roles without `query`/`truths` run in steady state — the user drives all changes via `/role`.

---

## Flow

```
RunAsync loop
  -> GetRole(session.Data.Role)           // active role from roles.json
  -> RunTurnAsync(...)                    // one LLM turn: tool dispatch loop inside
  -> if Completed: AdvanceRoleAsync(session, service, role, ct)
       -> if role.Query or role.Truths is empty: return null (no transition)
       -> if role.EvaluatorRole is empty: return null (no transition)
       -> SummarizeAsync(session)         // non-mutating fork
       -> run evaluatorRole with Answer tool + summary + query + truth options
       -> evaluator calls Answer(truth)
       -> map truth → next role name
       -> create fresh session; inject summary if non-null transition
  -> on ContextFull: compact
  -> on Failed: display error
```

---

## Role Data Model

```json
{
  "name": "QAEngineer",
  "models": ["claude-opus-4"],
  "tools": ["shell", "file"],
  "system_prompt": "You are a QA engineer...",
  "query": "Build and run the test suite. Check for any failures or regressions.",
  "evaluatorRole": "StateEvaluator",
  "truths": {
    "All tests pass":    "Reviewer",
    "Test failures":     "Coder"
  }
}
```

- `query` — prompt given to the evaluator; null/empty means no automatic transitions
- `evaluatorRole` — role that evaluates after each turn; typically a lightweight role with shell/file access
- `truths` — truth label → next role name; null/empty means no automatic transitions

Roles with no `query`, `evaluatorRole`, or `truths` behave exactly as before — steady-state, user-driven.

---

## Example: Code-Review Chain

```json
[
  {
    "name": "Planner",
    "models": ["*"],
    "tools": ["file"],
    "system_prompt": "You are a planning agent. Work with the user to define the task.",
    "query": "Has the plan been finalized and confirmed by the user?",
    "evaluatorRole": "StateEvaluator",
    "truths": {
      "Plan is complete": "Coder"
    }
  },
  {
    "name": "Coder",
    "models": ["*"],
    "tools": ["shell", "file"],
    "system_prompt": "You are a coding agent. Implement the plan.",
    "query": "Build the project. Does it compile without errors?",
    "evaluatorRole": "StateEvaluator",
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
    "query": "Run the tests. Are they all passing?",
    "evaluatorRole": "StateEvaluator",
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
    "query": "Has the code review been completed and approved?",
    "evaluatorRole": "StateEvaluator",
    "truths": {
      "Approved":           "Planner",
      "Changes requested":  "Coder"
    }
  },
  {
    "name": "StateEvaluator",
    "models": ["*"],
    "tools": ["shell", "file"],
    "system_prompt": "You are a state evaluator. Use your tools to investigate, then call Answer with exactly one of the provided truth labels verbatim."
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

- `Agent/DataModels/LLMRole.cs` — `Role` class: name, models, tools, system_prompt, query, evaluatorRole, truths
- `Agent/Services/RoleService.cs` — loads roles from `roles.json`; merges user (~/.beast/) and project (.beast/) definitions
- `Agent/AgentOrchestrator.cs` — `AdvanceRoleAsync`: evaluator dispatch, truth matching, role transition

---

## What Is Not Yet Implemented

- Workflow-specific tools (e.g., a dedicated `pick_truth` tool the evaluator calls explicitly)
  — currently the evaluator responds via the Answer tool; MatchTruth does exact-match (case-insensitive)
- Passing worker session context directly to the evaluator (evaluator currently receives only the summary)
- Automatic transitions triggered by tool results mid-turn (not just post-turn evaluation)
- Parallel sessions / multi-session orchestration
