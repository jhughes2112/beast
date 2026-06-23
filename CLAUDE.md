# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Beast** is a .NET 10 (C#) multi-agent LLM automation framework. It has two components:
- **Beast** — Windows CLI host that manages Agent Docker containers via WebSocket
- **Agent** — Core LLM runtime that runs inside Docker, executes tools, and talks to LLM providers

## Build Commands

```bat
# Full build: beast.exe + beastagent Docker image
build.bat

# Build only Beast CLI (self-contained exe)
dotnet publish Beast\Beast.csproj -c Release -r win-x64 --self-contained -o build\bin\release\beast-win-x64

# Build and run Agent directly (without Docker, for debugging)
dotnet run --project Agent\Agent.csproj -- --debug

# Restore packages
dotnet restore Beast.slnx
```

## Running Tests

Tests are an in-process harness inside the Agent project — not xUnit/NUnit. They run by passing a `--test` flag (or similar) to the Agent executable. Each test file is a static class with a `Test(TestContext ctx)` method.

```bash
# Run all agent tests
dotnet run --project Agent\Agent.csproj -- --test
```

Individual test classes (in `Agent/Tests/`): `LlmServiceTests`, `FileToolsTests`, `ShellToolsTests`, `WebToolsTests`, `PerModelLlmTests`, `ContextBudgetTests`, `FixJsonTests`, `ProtocolSwitchTests`.

## Architecture

### Two-Process Design

```
Beast (Windows CLI / TUI)
  └── Docker container management (Docker.DotNet)
  └── WebSocket client → connects to Agent inside Docker

Agent (Docker container)
  └── WebSocket server (port 13131)
  └── AgentOrchestrator → manages conversation lifecycle (via SessionRunner / SubagentRunner)
      └── LlmService → runs LLM conversation loop
          └── Protocol classes → Anthropic / ChatCompletions / Responses API (routed by ProtocolProxy)
          └── Tool instances → file, search, shell, web
```

### Agent Startup Flow

`Program.cs` → loads `SettingsService` → loads `RoleService` → builds `LlmRegistry` (which calls `ToolFactory.Build()`) → starts the WebSocket server (`TransportWebSocketServer`) → `AgentOrchestrator` runs the message loop.

### Key Agent Directories

- **Agent/Protocols/** — Wire-protocol classes: `ProtocolAnthropic` (messages API), `ProtocolChatCompletions`, `ProtocolResponses`, plus `ProtocolProxy` which detects the protocol from the endpoint URL and routes calls. Each converts between Beast's canonical conversation format and the wire format.
- **Agent/ProtocolListeners/** — `CanonicalConversation` (the source-of-truth conversation state), `ListenerBundle` (fans events out to multiple listeners), `ListenerTransport`.
- **Agent/Llm/** — `LlmService` (conversation loop, tool dispatch, rate limiting), `LlmRegistry` (holds all configured models, builds the shared tool set), `LlmModel` (a single API endpoint connection), `ReasoningEffort`.
- **Agent/Services/** — `SettingsService` (loads `.beast/settings.json`), `RoleService` (loads `.beast/roles.json`, holds the built-in default roles), `SessionService` (persists conversations).
- **Agent/Tools/** — All tools (concrete `Tool` instances). Registered explicitly in `ToolFactory.Build()` (no reflection).
- **Agent/DataModels/** — Shared data structures: `BeastSettings`, `BeastSession`, `CanonicalMessage`, `Role`, `TokenUsageInfo`.
- **Conversation orchestration** — `AgentOrchestrator.cs`, `SessionRunner.cs`, `SubagentRunner.cs`, `Session.cs` (no separate `Workflow` directory).
- **Agent/Transport/** — `ITransportServer`, WebSocket server, console debug transport.

### Configuration

Settings are loaded at startup from `.beast/settings.json` (project dir first, then `~/.beast/`). To reach a provider on the host from inside the container, point its `baseUrl` at `host.docker.internal`; on a native/debugger run `ProtocolProxy` transparently falls back to `localhost` if `host.docker.internal` is unreachable.

Roles are defined in `.beast/roles.json` or fall back to built-in defaults in `RoleService.cs` (the `Role` type lives in `DataModels/Role.cs`). Role filtering controls which tools `LlmService` exposes to the LLM.

## Coding Standards

- **No `var`** — always explicit types
- **No LINQ** — use `foreach` loops
- **No default parameters** — all parameters must be explicit; never assign made-up default values to properties (use `""`, `0`, or `null`). This includes `CancellationToken` — never `= default`.
- **No `CancellationToken.None` in production code** — if a method takes a token, thread a real one through. Only tests may pass `CancellationToken.None`.
- **No sync-over-async** — never `.GetAwaiter().GetResult()`, `.Result`, or a blocking `.Wait()` outside tests; make the method `async` and `await`, or keep the work synchronous.
- **ANSI braces** — opening brace on its own line
- **Single return** at the bottom of functions (success as the first `if` branch, failure in `else`)
- **No partial classes**
- **No setters** — mutate internally or create new objects
- **Explicit `using` directives** — `ImplicitUsings` is disabled
- **Minimal namespacing** — don't proliferate namespaces
- **Nullable** — declare as nullable where appropriate, handle explicitly
- **String interpolation** (`$"..."`) not concatenation
- **Short, cohesive methods** — one-line comments only, no XML doc blocks
- **Inline declarations** where natural: `dictionary.TryGetValue(key, out Type? value)`
- **No speculative abstractions** — only what the task requires
- **No defensive over-engineering** — trust other functions do what they're expected to do
- **Tuples over KeyValuePair** — use implicit tuples with named parameters
- **Async hygiene** — if an async method has no awaits, make it synchronous or return `Task.FromResult`
- **Visibility** — narrow to `private` unless `protected`/`public` is required; prefer locals over fields

## Workflow When Editing

- Gather all required context (search + read relevant files) before editing.
- Read each file before editing it. Modify files in place; do not delete and rewrite.
- After changes: build, fix all errors, report only new warnings.
- Never remove existing comments — update them if incorrect.
- Keep diff readability: put blank lines around new logical blocks.
- If async method has no awaits, convert to synchronous or return `Task.FromResult`.
- Do not ask the user to run commands — just make the change.

## What Not To Do

- No speculative refactors or library migrations.
- No abstractions added "for future use".
- No TODOs in committed code.
- No regex for code editing — edit files directly.
