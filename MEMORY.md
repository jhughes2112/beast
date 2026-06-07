# Beast Project Memory

## Build & Run

```bash
# Full build: beast.exe + beastagent Docker image
build.bat

# Build only Beast CLI
dotnet publish Beast\Beast.csproj -c Release -r win-x64 --self-contained -o build\bin\release\beast-win-x64

# Run Agent directly for debugging
dotnet run --project Agent\Agent.csproj -- --debug
```

## Code Completion Handling

**Location:** `Beast/Display/DisplayScreen.cs` lines 1607-1650 (Enter handler), 1658-1709 (Tab handler)

The input area supports command completion via Tab and Enter:
- **Tab**: Cycles through or accepts highlighted completion from popup
- **Enter** (modified): If a completion popup is active (`_completionActive && _completionMatches.Count > 0`), Enter first accepts the highlighted entry by replacing `inputBuffer` with the match, then submits. If no popup is active, Enter submits as before.

Completion popup state variables: `_completionActive`, `_completionMatches` (List<string>), `_completionIndex`
Match index for Tab cycling: `matchIndex`
Completion mode flag: `inCompletion` (true when inline completion has been cycled via Tab without popup)

## Coding Standards

- No `var`, no LINQ, no default parameters
- Opening brace on its own line (ANSI braces)
- Single return at bottom of functions
- No partial classes, no setters
- Explicit `using` directives (no ImplicitUsings)
- String interpolation over concatenation
- Short, cohesive methods with one-line comments only

## Architecture Overview

### Core Components

**Agent (Agent/)** - The LLM orchestration layer:
- `AgentOrchestrator` - Main loop managing sessions, LLM turns, compaction, role transitions
- `Session` - Wraps BeastSession with listener bundle, transport, and input queue
- `LlmService` - Drives conversation to completion with tool calling
- `ProtocolProxy` - Detects protocol (Anthropic, ChatCompletions, Responses) and routes calls
- `ProtocolChatCompletions` / `ProtocolAnthropic` / `ProtocolResponses` - Wire protocol implementations
- `ProtocolProxy` - Detects protocol via probes, routes calls, injects extras (headers, OpenRouter routing)
- `LlmRegistry` / `LlmModel` - Model catalog and live service instances

**Beast (Beast/)** - The CLI/TUI client:
- `DisplayScreen` - Full TUI using Screen compositing system with ANSI output
- `DisplayConsole` - Non-interactive streaming display
- `Screen` / `AnsiToScreen` / `MarkdownAnsi` / `AnsiString` - Rendering pipeline
- `BeastApp` - Entry point, connects to Agent via WebSocket

**Data Models** (Agent/DataModels/):
- `BeastSession` - Persisted conversation state (canonical ChatCompletions format + token/cost tracking)
- `BeastSettings` - Provider/model configuration (user + project merge)
- `Role` - Model preferences, tools, system prompt, automatic transitions
- `TokenUsageInfo` - Per-turn token counts

**Services** (Agent/Services/):
- `SettingsService` - Loads/merges settings from user profile + project
- `RoleService` - Loads/merges roles from user profile + project
- `SessionService` - Persists sessions to .beast/sessions/

**Tools** (Agent/Tools/):
- `FileTools` - read_file, edit_file, write_file (with global file lock)
- `ShellTools` - bash (async, streaming, timeout)
- `WebFetch` - fetch_page (30s cache, HTML stripping)
- `WebSearchOpenrouter` - search_web via OpenRouter plugin

**Protocols** (Agent/Protocols/):
- `IProtocolListener` - Callback interface for protocol events
- `ListenerBundle` - Fans out to multiple listeners (canonical + transport)
- `ProtocolChatCompletions` - OpenAI-compatible, strict alternation
- `ProtocolAnthropic` - Native SDK, preserves signed thinking
- `ProtocolResponses` - Stateful chaining via previous_response_id
- `ProtocolProxy` - Detection + routing + extras injection

**Transport** (Agent/Transport/):
- `ITransportServer` - Interface for client communication
- `TransportWebSocketServer` - WebSocket on port 13131
- `TransportConsoleDebug` - Console output for debugging

### Key Architectural Patterns

1. **Canonical State**: `BeastSession.ChatCompletionsState` is the single source of truth (ChatCompletions wire format). Protocol listeners maintain native runtime state in-memory and rehydrate from canonical on load/switch.

2. **Listener Bundle**: `ListenerBundle` fans out events to canonical store + transport simultaneously. Protocols implement `IProtocolListener` to stay in sync.

3. **Session Isolation**: Each `Session` is independent with its own bundle, input queue, and cancellation token. Multiple sessions can run concurrently.

4. **Availability Tracking**: `LlmService` tracks per-model availability with backoff timers. `LlmRegistry` exposes availability for model selection.

5. **Role Transitions**: Roles define `EndOfTurnPrompt` + `Truths` map. After each turn, the evaluator runs and can transition to a new role automatically.

6. **Compaction**: `/compact` triggers summarization via a forked temp session. The summary becomes a new system prompt; history is truncated.

### Protocol Details

- **ChatCompletions**: OpenAI-compatible, strict user/assistant alternation, tool role for results
- **Anthropic**: Uses Anthropic.SDK, preserves signed thinking blocks in native `List<Message>` state
- **Responses**: Stateful chaining via `previous_response_id`, incremental input items after rehydration

### File Organization

```
/workspace/
├── Agent/
│   ├── AgentOrchestrator.cs      # Main orchestration loop
│   ├── Session.cs                # Session runtime wrapper
│   ├── LlmService.cs             # LLM conversation driver
│   ├── ProtocolProxy.cs          # Protocol detection & routing
│   ├── Protocols/                # Protocol implementations
│   ├── Tools/                    # Tool definitions (file, shell, web)
│   ├── Transport/                # WebSocket/Console transports
│   ├── Services/                 # Settings, Roles, Sessions
│   └── DataModels/               # Core data types
├── Beast/
│   ├── DisplayScreen.cs          # TUI with Screen compositing
│   ├── DisplayConsole.cs         # Non-interactive display
│   ├── Screen.cs                 # Cell buffer + compositing
│   ├── AnsiToScreen.cs           # ANSI parser → Screen
│   ├── MarkdownAnsi.cs           # Markdown → ANSI lines
│   ├── AnsiString.cs             # ANSI-aware string ops
│   └── BeastApp.cs               # CLI entry point
└── build.bat
```

## Recent Changes

### Enter Key Accepts Code Completion (Completed)
- **File:** `Beast/Display/DisplayScreen.cs` (lines 1607-1623)
- **Change:** Modified the Enter key handler to check for an active completion popup. When a popup is active with highlighted entries, pressing Enter first replaces the input buffer with the selected match, then proceeds to send it — combining acceptance and submission into one keystroke.
- **Before:** Only Tab accepted completions; Enter immediately sent raw input.
- **After:** Both Tab and Enter accept a completion entry from the popup before proceeding (Tab only sends if no popup active, Enter always submits).