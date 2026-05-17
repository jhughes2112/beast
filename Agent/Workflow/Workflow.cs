using System;
using System.Collections.Generic;


// A step is a status, and you can transition from one step to another by meeting the condition of the transition.
public class WorkflowStep
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
	public string Role { get; set; } = string.Empty;
    public WorkflowTransition[] Transitions { get; set; } = Array.Empty<WorkflowTransition>();
    public string NextStep { get; set; } = string.Empty;
    public string? OnTimeout { get; set; }
    public string? OnFailure { get; set; }
}

public class WorkflowTransition
{
    public string Condition { get; set; } = string.Empty;
    public string NextStep { get; set; } = string.Empty;
}

public class Workflow
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<WorkflowStep> Steps { get; set; } = new List<WorkflowStep>();
}