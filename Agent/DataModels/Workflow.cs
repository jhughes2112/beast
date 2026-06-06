using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;


// A workflow defines a set of named states and a dedicated StateEvaluator role.
// Each state specifies a worker Role, a Query for the evaluator, and a Truths
// dictionary mapping possible evaluator conclusions to the next state name.
public class Workflow
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    // Role used to evaluate transitions after each worker turn.
    // The evaluator receives the state's Query and picks one Truth to determine the next state.
    [JsonPropertyName("stateEvaluatorRole")]
    public string StateEvaluatorRole { get; set; } = string.Empty;

    [JsonPropertyName("states")]
    public List<WorkflowState> States { get; set; } = new List<WorkflowState>();

    public WorkflowState? GetState(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        foreach (WorkflowState state in States)
        {
            if (string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase))
                return state;
        }
        return null;
    }

    public WorkflowState? GetFirstState()
    {
        return States.Count > 0 ? States[0] : null;
    }
}

// One node in the workflow state machine.
// Role is the worker role that handles user turns while in this state.
// Query is what the StateEvaluator is asked after each completed turn.
// Truths maps evaluator conclusions (free-form labels) to the next state name.
// An empty Query or empty Truths means no automatic transitions for this state.
public class WorkflowState
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    // Truth label → next state name. The evaluator picks one label; the orchestrator
    // looks up the corresponding state and transitions to it.
    [JsonPropertyName("truths")]
    public Dictionary<string, string> Truths { get; set; } = new Dictionary<string, string>();
}
