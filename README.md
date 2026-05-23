![Beast](env/wwwroot/beast.jpg)

**It's your new favorite agent.**

## Architecture

```
Beast (Windows CLI / TUI)
  └── Launches a single docker container that does all the agent work
  └── maintains the connection to the agent and presents a nice TUI to the user

Agent (Docker container)
  └── Sandboxed LLM agent does not need to ask permission, it can't smash files outside the current folder anyway
  └── manages conversation lifecycle, drives conversation loop, presents a minimal set of tools to the LLM
  └── supports file read/write/edit, bash, file glob, and web fetch and web search
  └── talks to anything → Messages (Anthropic) / ChatCompletions (OpenAI) / Responses (OpenAI), all in streaming mode
```

## Quick Start

### Prerequisites
- .NET 10 SDK
- Docker Desktop
- Local LLM or API keys (OpenAI, Anthropic, OpenRouter, etc)

### Build

```bat
# Full build: Produces beast.exe and "beastagent" Docker image
build.bat
```

### Run Tests
The /p prompt mode removes the TUI and outputs directly to stdout for easy scripting, also useful when running tests.
The /verbose flag tells the client to print all tools and thinking details, and increases the logging level on the Docker container as well.
```bat
beast.exe /verbose /test /p "Hello world"
```

Agent tests are an in-process harness, and will test LLM handling and web search only if they are configured.

## CLI Usage

Note, dash or slash do the same thing on the command line.  The last command, if provided, must always be /prompt.
```bat
beast [/command1 /command2 ...] [-p <prompt text>]
```

Each command by default continues the last session from the current folder (stored in /.beast/ subfolder). If you want a fresh session, add "/session new" to the CLI.

### Options

| Option | Description |
|--------|-------------|
| `<switch>` | Any command switch forwarded to the agent container |
| `-p <text>` | Prompt text; everything after `-p` is treated as the prompt |
| `--test` | Run Beast transport tests locally |
| `--verbose` | Show diagnostic debug output from the Agent |
| `--help` | Show help |

### Agent Commands (can be provided on the CLI)

| Command | Description |
|---------|-------------|
| `/compact` | Run context compaction |
| `/clear` | Clear session |
| `/reload` | Reload config files |
| `/role <id>` | Set role |
| `/model <id>` | Set model |
| `/session <id>` | Switch session |
| `/test` | Run agent tests |
| `/quit` | Exit agent |
| `/help` | Show commands |

## Configuration

Settings are loaded from `.beast/settings.json` (project dir first, then `~/.beast/`).  Both are bound into the docker container so the agent can read them.

**Simply run beast and it produces a new set of default config files, so you can edit them.**  It is convenient to put API keys in your ~/.beast/ config files, so they are always available and you don't have to repeat yourself or scatter API keys all over the place.

You can configure providers and the available models (with a single flag to disable each model), edit the context compaction prompt, and also the continuation prompt that will be injected when tiered model routing is implemented.

### Roles

A Role identifies the tools that are available and which models can be used for that role, and also the system prompt injected when using that role. These can be defined once in `~/.beast/roles.json` or per-project in `.beast/roles.json`.

Built-in roles:
- **Default** — this has access to all tools and models

### Workflows

Not implemented yet.  This is intended to be a flexible way to create graph-like workflows like Planning -> Architecting -> Implementing -> Testing -> Finaling, where a Role is a node and a workflow describes criteria for branching between roles (or completing the workflow).

### Data Driven

All these Roles and Workflow folders are modifiable and potentially could be written and edited by the Agent, and a Tool will be added to allow it to /reload its own settings.  This provides for a smart model to generate its own workflow, edit the system prompts for roles, and generally learn to improve its own performance over time.

### Compaction

Context limits are a fact of life.  Compaction happens as a USER message, is provided no tools, and whatever the model outputs becomes the first user message in a clean session.  This avoids breaking prompt cache and follows best practices, but has a 4k hardcoded summary size.  That may need adjusting or turned into a setting.

## How It Works

1. Beast launches Agent Docker container
2. Beast connects via WebSocket to Agent's server (port 8765)
3. User sends messages/prompts through Beast CLI
4. AgentOrchestrator receives messages, resolves role and model
5. LlmService runs conversation loop:
   - Sends API request to LLM
   - Parses tool calls from response
   - Dispatches tools
   - Handles rate limiting, retries, errors
6. Session state persisted after each turn

## Environment Variables

| Variable | Description |
|----------|-------------|
| `BEAST_HOST` | Rewrites localhost provider URLs (used inside Docker to reach host) |

## License

MIT License
