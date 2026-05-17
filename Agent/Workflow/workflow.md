# KanBeast Agent Workflow System

## Overview

The KanBeast agent uses a **role-based workflow system** where each conversation is assigned a role that determines:
- Which LLM models are preferred
- Which tools the agent is allowed to use
- Whether sub-agents can be spawned
- Which system prompt template to load

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Program.cs (CLI Entry Point)                                │
│  ├── Options: --role, --reload, --prompt, --interactive      │
│  ├── SettingsService: loads .beast/settings.json             │
│  ├── RoleService: loads .beast/roles.json                    │
│  ├── LlmRegistry: holds all LLM model connections            │
│  ├── ToolsFactory.Build(): creates Dictionary<string, ITool> │
│  └── ToolRegistry: wraps tool dictionary for injection       │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│  WorkerSession (static singleton)                            │
│  ├── LlmProxy: LlmRegistry                                   │
│  ├── Prompts: Dictionary<string, string>                     │
│  ├── CancellationToken                                       │
│  └── ToolRegistry: the global tool registry                  │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│  AgentOrchestrator                                           │
│  ├── Manages conversation lifecycle                          │
│  ├── Reads incoming framed messages from transport           │
│  ├── Triggers LLM runs when conversation needs attention     │
│  ├── Periodically saves session state                        │
│  └── Handles /reload command                                 │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│  LlmService                                                 │
│  ├── Runs conversation loop with LLM                        │
│  ├── Builds tool list from ToolRegistry filtered by role    │
│  ├── Sends API requests to OpenAI-compatible endpoint       │
│  ├── Parses tool calls from LLM responses                   │
│  ├── Dispatches tool execution                              │
│  └── Handles rate limiting, retries, errors                 │
└────────────────────────┬────────────────────────────────────┘
                         │
            ┌────────────┴────────────┐
            ▼                         ▼
┌───────────────────────┐  ┌───────────────────────────────┐
│ ITool Implementations │  │ LLM Conversation              │
│                       │  │                               │
│ • FileTools           │  │ CompactingConversation        │
│ • SearchTools         │  │ SinglePromptConversation      │
│ • WebTools            │  │                               │
│ • ShellTools          │  │ Messages flow through here    │
│ • PersistentShellTools│  │                               │
│ • MemoryTools         │  │ Tool calls are injected as    │
│ • AgentTools          │  │ tool role messages            │
└───────────────────────┘  └───────────────────────────────┘
```

## Tool Dispatch Flow

1. **Startup**: `ToolsFactory.Build(registry, webCache)` creates all tool instances
2. **Wiring**: Each tool class receives its dependencies via constructor injection
3. **Registry**: Tools are stored in a `Dictionary<string, ITool>` keyed by snake_case name
4. **Role Filtering**: When LlmService starts a conversation, it filters the global tool dictionary by the role's `AllowedToolNames`
5. **Serialization**: Filtered tools are converted to `Tool[]` via `ToolBridge.FromITools()` for the OpenAI API
6. **Execution**: When the LLM calls a tool, LlmService looks it up by name and invokes it through the `Handler` delegate

## Role Configuration

Roles can be defined in two ways:

### 1. Code Defaults (LLMRole.cs)
Built-in roles with hardcoded tool lists and model preferences:
- **Planning** — read-only tools for analysis
- **PlanningActive** — adds write/edit tools for active planning
- **Developer** — full toolset including web, search, files
- **DeveloperSubagent** — focused subset for autonomous sub-agents
- **PlanningSubagent** — focused subset for autonomous planning
- **Compaction** — read/write/summarize for context compaction

### 2. Dynamic from JSON (roles.json)
Place a `roles.json` file in `.beast/roles.json` (project or home directory):

```json
[
  {
    "name": "MyCustomRole",
    "preferredModels": ["openai/gpt-4o", "anthropic/claude-3-5-sonnet"],
    "allowedToolNames": ["read_file", "write_file", "get_web_page"],
    "supportsSubagents": false,
    "systemPromptKey": "my-custom-role"
  }
]
```

### Priority
1. Project roles: `.beast/roles.json`
2. Global roles: `~/.beast/roles.json`
3. Built-in code defaults

## CLI Commands

### `--role list|ls`
Lists all available roles with their model preferences and allowed tools.

### `--reload`
Reloads settings, roles, and model configurations from disk without restarting the process.

## Key Design Decisions

### No Subagent Launcher Tools
`StartDeveloper`, `StartInspectionAgent`, and `StartSubAgent` have been **removed**. The rationale:
- Sub-agents required a different tool set than the caller, creating confusion
- All work can be accomplished with the caller's own tools
- The `/role` system now cleanly controls which tools are available

### Instance-Based Tools with DI
All tool classes are instance-based with constructor-injected dependencies:
- **WebTools**: receives `LlmRegistry` and `WebCache`
- **FileTools**, **SearchTools**, **ShellTools**: no external dependencies
- **AgentTools**: accesses shared state through `WorkerSession` static facade

### WebCache with TTL
Web responses are cached for 30 seconds by default to prevent:
- Rate limiting from external sites
- Unnecessary network traffic
- Repeated expensive fetches within the same conversation

### ToolsFactory is a Pure Function
`ToolsFactory.Build()` takes dependencies and returns a dictionary. It holds no state. The dictionary is then passed to wherever tools are needed.

## Error Handling Strategy

Tools follow a consistent error handling pattern:
1. **Validate inputs first** — return descriptive error messages for bad input
2. **Catch specific exceptions** — handle `OperationCanceledException` separately
3. **Graceful degradation** — never crash the LLM loop; always return a ToolResult
4. **WebTools special handling** — cache errors don't poison the cache; HTTP errors are cached with short TTL to avoid retrying broken endpoints

## Future Work

- [ ] Add tool call tracing/logging for debugging
- [ ] Implement tool permission system per user/session
- [ ] Add tool versioning for backward compatibility
- [ ] Implement streaming tool results for long-running operations
- [ ] Add tool metadata (category, tags) for better organization