# Workflow Feature

## Design Intent

**Workflow** is a data-driven state machine. Each state specifies:
- A **worker Role** — the agent that handles user turns while in that state
- A **Query** — what the StateEvaluator is asked after each completed worker turn
- A **Truths** dictionary — mapping possible evaluator conclusions to next state names

**StateEvaluator** is a dedicated role defined at the workflow level. After each worker turn completes, the orchestrator invokes the StateEvaluator with the state's Query and the list of Truths. The evaluator (which may use tools — shell, file access, etc.) investigates and responds with one of the truth labels. The orchestrator maps that label to the next state and transitions.

**Session** is an independent data object. Inputs are fed in; outputs come out. The session's turn runner is simple and single-purpose — it does not decide whether to compact, switch models, or evaluate transitions. Those are orchestrator-level policies.

**Turn** ends when the model produces a terminal response (completed, no more tool calls), or when the infrastructure can't continue without a decision from above (context full, fatal error, user cancel). The orchestrator decides what happens next.

---

## Architecture

```
AgentOrchestrator loop
  -> ResolveRole(conversation)              // reads workflow.state → role
  -> GetServiceForRole(role, model, ctx)    // picks LlmService (prefers current model)
  -> RunLlmTurnAsync(...)                   // one turn: tool dispatch loop inside
       returns LlmResult { ExitReason, ErrorMessage }
  -> if Completed: RunStateEvaluatorAsync(conversation)
       -> looks up current workflow state
       -> if state has Query + Truths:
            -> resolves workflow.StateEvaluatorRole
            -> invokes StateEvaluator in ephemeral session with Query + truth options
            -> evaluator uses its role's tools to investigate, responds with a truth label
            -> MatchTruth(response, truths) -> next state name
       -> conversation.WorkflowState = nextState
  -> on ContextFull: compact
  -> on Failed: display error
```

---

## Data Model

### Workflow
```json
{
  "id": "development",
  "stateEvaluatorRole": "StateEvaluator",
  "states": [ ... ]
}
```
- `id` — unique name used to reference this workflow
- `stateEvaluatorRole` — role used to evaluate state transitions; empty = no automatic transitions
- `states` — ordered list of `WorkflowState` nodes; first state is the entry point

### WorkflowState
```json
{
  "name": "Testing",
  "role": "QAEngineer",
  "query": "Build and run the test suite and look for any errors or regressions.",
  "truths": {
    "No errors or warnings":    "CommitState",
    "Warnings but no errors":   "CodeCleanupState",
    "Less than five errors":    "CodeCleanupState",
    "More than five errors":    "FailureAnalysisState"
  }
}
```
- `name` — unique state name within the workflow
- `role` — the worker role (e.g., QAEngineer) that handles user turns in this state
- `query` — prompt given to the StateEvaluator after each completed worker turn; empty = no evaluation
- `truths` — truth label → next state name; empty = no automatic transitions

### StateEvaluator Invocation
After a `Completed` worker turn, if the current state has a non-empty `query` and non-empty `truths`:
1. The orchestrator resolves the workflow's `stateEvaluatorRole`.
2. An ephemeral session is created for the evaluator.
3. The evaluator's system prompt is applied, then the query + truth options are presented as a user message.
4. The evaluator runs with its role's full tool set (it can build, run tests, read files, etc.).
5. The evaluator responds with one of the truth labels verbatim.
6. `MatchTruth(response, truths)` finds the first label (case-insensitive substring) and returns its next-state name.
7. If a match is found, `conversation.WorkflowState` is updated and a status is shown.

---

## Turn Model

`LlmService.RunToCompletionAsync` is the "fire and get a real answer" abstraction for one model.
It owns all provider-level retry mechanics (rate limit backoff, up to 10 retries) so the orchestrator
never sees transient provider noise — only terminal outcomes: Completed, ContextFull, Failed, Interrupted.

The orchestrator's job is policy: which model to use, when to compact, how to advance the workflow.
LlmService's job is reliability: make that one model actually produce an answer.

---

## Example Multi-State Workflow

```json
{
  "id": "code-review",
  "stateEvaluatorRole": "StateEvaluator",
  "states": [
    {
      "name": "Planning",
      "role": "Planner",
      "query": "Has the plan been finalized and approved?",
      "truths": {
        "Plan is complete": "Coding"
      }
    },
    {
      "name": "Coding",
      "role": "Coder",
      "query": "Build the project and check if the code compiles without errors.",
      "truths": {
        "Builds successfully":  "Testing",
        "Build errors found":   "Coding"
      }
    },
    {
      "name": "Testing",
      "role": "QAEngineer",
      "query": "Run the test suite and check for failures or regressions.",
      "truths": {
        "All tests pass":       "Review",
        "Test failures found":  "Coding"
      }
    },
    {
      "name": "Review",
      "role": "Reviewer",
      "query": "Has the code review been completed and approved?",
      "truths": {
        "Approved":             "Planning",
        "Changes requested":    "Coding"
      }
    }
  ]
}
```

Model selection: each role specifies its own preferred model list. The orchestrator calls
`GetServiceForRole(role, conversation.Model, ...)` which keeps the current model when it appears
in the new role's list (good for prompt caching) and falls back to the role's first available
model when switching roles.

---

## Commands

| Command | Effect |
|---|---|
| `/workflow <name>` | Switch to named workflow; WorkflowState = first state |
| `/state <name>` | Jump directly to named state in current workflow |

---

## Files

- `Agent/DataModels/Workflow.cs` — Workflow, WorkflowState (Query + Truths)
- `Agent/DataModels/BeastSession.cs` — Workflow + WorkflowState string fields; serialized
- `Agent/Services/WorkflowService.cs` — loads workflows; creates default; GetStateEvaluatorRole
- `Agent/Llm/LlmService.cs` — handles rate-limit retries internally; exposes Completed/ContextFull/Failed/Interrupted
- `Agent/AgentOrchestrator.cs` — RunStateEvaluatorAsync; MatchTruth; /workflow + /state commands
- `Agent/Tests/WorkflowTests.cs` — unit tests for WorkflowService, data loading, and truth matching

---

## What Is Not Yet Implemented

- Workflow-specific tools (e.g., a dedicated `pick_truth` tool the StateEvaluator calls explicitly)
  — currently the evaluator responds with a truth label as text; MatchTruth does substring matching
- Parallel sessions / multi-session orchestration
- Flexible compaction policies (currently: compact on context_full, same as before)
- Passing worker session context to the StateEvaluator (currently the evaluator starts fresh)
- Automatic transitions triggered by tool results mid-turn (not just post-turn evaluation)
