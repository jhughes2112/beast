using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;


// Loads and provides Workflow definitions from ~/.beast/workflows/*.json.
// Creates a default workflow on first run. Similar in structure to RoleService.
public class WorkflowService
{
    private readonly string _workflowsPath;
    private readonly Dictionary<string, Workflow> _workflows = new Dictionary<string, Workflow>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Workflow> Workflows => _workflows;

    public WorkflowService(string userProfilePath)
    {
        _workflowsPath = Path.Combine(userProfilePath, ".beast", "workflows");
        LoadAll();
    }

    public Workflow? GetWorkflow(string name)
    {
        _workflows.TryGetValue(name, out Workflow? workflow);
        return workflow;
    }

    // Returns the worker role name for a given workflow state, or null if workflow/state not found.
    public string? GetRoleForState(string workflowName, string stateName)
    {
        Workflow? workflow = GetWorkflow(workflowName);
        if (workflow == null) return null;
        WorkflowState? state = workflow.GetState(stateName);
        return state?.Role;
    }

    // Returns the StateEvaluator role name for a workflow, or null if workflow not found or not set.
    public string? GetStateEvaluatorRole(string workflowName)
    {
        Workflow? workflow = GetWorkflow(workflowName);
        if (workflow == null) return null;
        return string.IsNullOrEmpty(workflow.StateEvaluatorRole) ? null : workflow.StateEvaluatorRole;
    }

    // Returns the name of the first state in a workflow, or null if workflow not found or empty.
    public string? GetInitialStateName(string workflowName)
    {
        Workflow? workflow = GetWorkflow(workflowName);
        return workflow?.GetFirstState()?.Name;
    }

    public void Reload()
    {
        _workflows.Clear();
        LoadAll();
    }

    private void LoadAll()
    {
        Directory.CreateDirectory(_workflowsPath);

        string defaultPath = Path.Combine(_workflowsPath, "default.json");
        if (!File.Exists(defaultPath))
            WriteDefaultWorkflow(defaultPath);

        foreach (string file in Directory.GetFiles(_workflowsPath, "*.json"))
        {
            LoadFile(file);
        }
    }

    private void WriteDefaultWorkflow(string path)
    {
        Workflow defaultWorkflow = new Workflow
        {
            Id = "default",
            StateEvaluatorRole = string.Empty,
            States = new List<WorkflowState>
            {
                new WorkflowState { Name = "default", Role = "Default", Query = string.Empty }
            }
        };
        string json = JsonSerializer.Serialize(defaultWorkflow, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        _workflows[defaultWorkflow.Id] = defaultWorkflow;
    }

    private void LoadFile(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            Workflow? workflow = JsonSerializer.Deserialize<Workflow>(json);
            if (workflow != null && !string.IsNullOrEmpty(workflow.Id))
                _workflows[workflow.Id] = workflow;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: Failed to load workflow from {path}: {ex.Message}");
        }
    }
}
