![Beast in action](readme/beautyshot.jpg)

**It's your new favorite agent harness.**

Beast is a multi-agent LLM automation framework for Windows. A polished terminal UI drives a fleet of cooperating agents that run **sandboxed inside Docker** — so the model can read, write, run shells, fetch the web, and delegate work to other agents without ever touching anything outside the folder you point it at.

The headline idea: **you own the model routing and the prompts.** Every agent is a *Role* — an ordered list of models to try, the tools it may call, and the system prompt that drives it. Front-load a cheap local model for the grunt work and fall back to a premium API model only when you need it. Wire roles together and they become a workflow: a Default agent that chats with you, a Developer that writes the code in an isolated git worktree, and a Reviewer that signs off before anything is committed.

## Why Beast

- **Sandboxed by design** — the agent runs in a container scoped to your project folder. No permission prompts, because it physically can't escape the sandbox.
- **You control the routing** — every role lists models in priority order. The first available one wins, so cheap/local models do the bulk and premium models are the fallback, not the default.
- **Real multi-agent workflow** — `assign_work → review_work → commit_and_rebase` turns Default → Developer → Reviewer into a self-checking pipeline. An F10 agent tree lets you watch each agent's live conversation.
- **Talks to anything** — Anthropic Messages, OpenAI ChatCompletions, and OpenAI Responses APIs, all streaming. The protocol is auto-detected from the endpoint URL. OpenRouter, Ollama, vLLM, and direct vendor APIs all just work.
- **Batteries-included image** — the container ships Python, Node, Rust, Go, Java, Ruby, the .NET SDK, plus zip/unzip, 7z, pdftotext, pandoc, jq, sqlite3, and more, so the agent can actually do the work.
- **Git worktree isolation** — `/worktree` runs the whole thing on a dedicated branch that never touches your main checkout until you say so.

## Architecture

```
Beast (Windows CLI / TUI)
  └── Launches a single Docker container that does all the agent work
  └── Maintains the WebSocket connection to the agent and presents the TUI
  └── Multi-agent aware: an F10 agent tree lets you watch and switch between the root
      agent and the subagents it spawns, each with its own live conversation

Agent (Docker container, port 13131)
  └── Sandboxed — it can't touch files outside the mounted project folder, so it never asks permission
  └── Manages conversation lifecycle, drives the conversation loop, exposes a minimal tool set to the LLM
  └── Tools: file read/write/edit, ls, bash, web fetch, web search, and delegation to subagents
      (assign_work) with a review / commit-and-rebase workflow
  └── Ships a full dev toolchain in the image: Python, Node, Rust, Go, Java, Ruby, the .NET SDK,
      plus file utilities (zip/unzip, 7z, pdftotext, pandoc, jq, sqlite3, …)
  └── Talks to anything → Messages (Anthropic) / ChatCompletions (OpenAI) / Responses (OpenAI), streaming
```

It's a two-process design. **Beast** is the Windows host: it manages the Docker container and renders the UI. **Agent** is the LLM runtime that lives inside the container, executes tools, and talks to the providers. They communicate over a local WebSocket.

## Quick Start

### Prerequisites
- Docker Desktop
- At least one LLM you can reach: an API key (OpenAI, Anthropic, OpenRouter, …) **or** a local model server (Ollama, vLLM, …)

### 1. Build

```bat
build.bat
```

If you have Docker Desktop installed, that is all you need.  The build script runs entirely inside a docker image and produces `beast.exe` which you will want to put on your PATH, so you can run it from wherever you need it.  This script also produces a local `beastagent` image that beast uses to run agents.

### 2. Generate the default configs

Just run it once to create default config files:

```bat
beast -p "Say hello"
```

This creates `~/.beast/settings.json` (providers, models, API keys — see [Configuration](#configuration)) and also the per-project `.beast/roles.json` file. Add your API keys once in `~/.beast/settings.json`, flip `"enabled": true` on the models you want to use, and you're ready to go.  

Generally speaking, the per-project `roles.json` doesn't need editing unless you want to control the model priority order per role, or want to tweak the system prompts to your liking.

### 3. Interactive Use

For interactive use, go to the folder you want to work in and simply run:
```bat
beast
```

In each case, Beast launches a Docker container in a git worktree. The Default agent is for chatting and inspecting the project. If there is work to do that requires modifying files, it delegates to the Developer to implement (which may be a different model).  The Developer asks the Reviewer to check its work (which is ideally from a different model family), and if approved, commits the changes back to the originating branch.

Alt-up/down - scrolls vertically
Alt-left/right - scrolls horizontally
Click - expand/contract a block
^O - change global level of detail
/commands - autocomplete and will show you only relevant completions, press tab to accept the completion and keep typing, or enter to just accept and submit immediately

#### How to Exit

To fold down a git worktree and delete the session, cleaning up after yourself, the `/finish` command checks that all files are committed before deleting the worktree.

```bat
/finish
```

To exit a session that has incomplete work (and be able to resume it later), simply press control-C or control-D.  This leaves the worktree and all modified files intact, so you can resume it later.

```bat
^C or ^D
```

## CLI Usage

For scripted agents using a git worktree:
```bat
beast --worktree featureName -p "Say hello"
```

For scripted agents without using git at all:
```bat
beast /p "Say hello"
```

Dash and slash are interchangeable on the command line. The prompt, if given, must be the last argument.

```bat
beast [/command1 /command2 ...] [-p <prompt text>]
```

### Options

| Option | Description |
|--------|-------------|
| `-p <text>` | Prompt text; everything after `-p` is the prompt. Must be the last argument. |
| `/worktree [name]` | Selects the git worktree in an isolated git worktree (created if it doesn't exist). See [Worktrees](#worktrees). |
| `/verbose` | Show all tool calls and thinking in the TUI, and raise the container's log level. Beast-only. |
| `/debug` | Attach to an already-running Agent on port 13131 instead of launching a container (for native/debugger runs). Beast-only. |
| `/test` | Run the Beast transport tests locally (and the agent tests in the container). |
| `/help` | Show help. Beast-only. |

### Interactive/Agent Commands (also valid on the CLI)

| Command | Description |
|---------|-------------|
| `/compact` | Run context compaction now |
| `/reload` | Reload the config files (settings + roles) |
| `/model <id>` | Switch the active model for the current role |
| `/finish` | Integrate the worktree and exit. See [Worktrees](#worktrees) |
| `/test` | Run agent tests |
| `/quit` | Exit the agent |
| `/help` | Show commands |

### Worktrees

`/worktree [name]` creates a dedicated git worktree and runs the agent inside it, so the work is isolated on its own branch and never touches your main checkout.

When the work is done, `/finish` integrates it and exits — **but only if everything is committed**. It checks that the worktree has no uncommitted changes and that its branch is fully merged into the base. If so, it folds the worktree away, deletes the merged branch, and shuts the container down. If the worktree isn't clean, `/finish` refuses and reports exactly what's pending; tell the agent how to handle the unfinished files (finish and review, or reset to discard), then run `/finish` again.

## Configuration

Settings load from `.beast/settings.json` (project dir first, then `~/.beast/`). Both are bound into the container so the agent can read them. Running Beast once generates a fully commented default `~/.beast/settings.json` with example entries for OpenRouter, Anthropic, OpenAI, and Ollama — every model starts **disabled** so you opt in to exactly what you want.

It's convenient to keep API keys in `~/.beast/settings.json` so they're always available and never scattered across projects.

### A minimal `settings.json`

```jsonc
{
  "providers": [
    {
      "baseUrl": "https://api.anthropic.com/v1/messages",
      "apiKey": "sk-ant-...",
      "models": [
        {
          "id": "claude-3-5-sonnet-20241022",
          "name": "Claude 3.5 Sonnet",
          "enabled": true,
          "contextWindow": 200000,
          "reasoningEffort": "medium",
          "cost": { "input": 3.0, "output": 15.0, "cacheRead": 0.3, "cacheWrite": 3.75 }
        }
      ]
    }
  ]
}
```

Per-model knobs worth knowing:

- **`baseUrl`** — the protocol is auto-detected from the endpoint route (`/messages` → Anthropic, `/chat/completions` → ChatCompletions, `/responses` → Responses). Point it anywhere that speaks one of those.
- **`enabled`** — flip models on/off without deleting them.
- **`reasoningEffort`** — a single word (`none`/`minimal`/`low`/`medium`/`high`/`max`) translated to each provider's native control automatically (Anthropic thinking budget, OpenAI reasoning effort, etc.). You never deal with the raw numbers.
- **`cost`** — input/output/cache prices per million tokens, used for live cost accounting in the UI.
- **`extras`** — JSON merged verbatim into the request body (e.g. `temperature`, or an OpenRouter `provider` routing object). Null/empty values are skipped, so the defaults carry self-documenting placeholders.
- **`headers`** — extra HTTP headers copied onto the request (e.g. `OpenAI-Organization`).

#### Using a local model (Ollama / vLLM)

The agent runs inside Docker, so point a local provider's `baseUrl` at **`host.docker.internal`** (not `localhost`) so the container can reach the model server on your host:

```jsonc
{ "baseUrl": "http://host.docker.internal:11434/v1/chat/completions", "apiKey": "ollama", "models": [ /* … */ ] }
```

When you run natively for debugging (`/debug`), Beast automatically falls back to `localhost` if `host.docker.internal` isn't reachable.

### Roles

A **Role** bundles three things: an **ordered list of models** (priority order — the first currently-available one wins), the **tools** that role may call, and the **system / summary / end-of-turn prompts** injected when it runs. The in-code defaults are authoritative and versioned with the build; they're written to `.beast/roles.json` on first run, and your edits there (or in `~/.beast/roles.json`) override them and can add new roles. A `*` in a model list expands to all enabled models, in order.

Roles exist to put *you* in control of model selection and prompts. Front-load a cheap local model to do the bulk of the work and fall back to a premium API model only when the local one isn't available — this saves an enormous number of expensive tokens. Tune each role's prompt for its job.

**Roles are nodes in the workflow.** The built-in Default → Developer → Reviewer loop is the most obviously useful workflow; it would require a small code change to make this more flexible, but even with frontier models, the division of context tends to not be beneficial adding more. YMMV.

| Role | Kind | Access | Job |
|------|------|--------|-----|
| **Default** | Agent | read-only | The conversational driver you talk to; delegates real work to the Developer via `assign_work`. |
| **Developer** | Subagent | full read/write/shell, in a worktree | Makes the actual change, calls `review_work` until approved, then `commit_and_rebase` to integrate it. |
| **Reviewer** | Subagent | read-only | Approves or rejects the Developer's change with actionable comments (`finish_review`). |
| **Explorer** | Helper | — | First-pass discovery; returns a line-number roadmap of a file so callers read only the relevant regions. Backs `find_relevant_file_sections`. |
| **WebFetch** | Helper | read_file, bash | Backs `fetch_url`; reads a fetched resource and returns only what the goal asks for. |
| **WebSearch** | Helper | — | Backs `search_web`; returns live results filtered to the goal. |

Chat with Default.  Default asks Developer to do things.  Developer asks Reviewer to look at and approve or reject its work. Any of these may use Explorer, WebFetch, or WebSearch depending on the task and requirements.  You can steer any of these by using up/down arrow and typing in a message.

### Tools

The agent is given a deliberately small, sharp tool set. Which tools a role can call is set per-role.

| Tool | What it does |
|------|--------------|
| `read_file` | Read a file with line numbers |
| `find_relevant_file_sections` | Run the Explorer over a file and return a line-number roadmap (saves context) |
| `write_file` / `edit_file` | Create or modify files (Developer only) |
| `ls` | List directory contents |
| `bash` / `readonly_bash` | Run a shell command; the read-only variant locks down anything that could mutate the tree |
| `fetch_url` | Fetch a web resource and return only the parts that serve the goal (see below) |
| `search_web` | Live web search, filtered to the goal |

### Web Fetch & Search

`fetch_url` never dumps a raw page into context. The resource is downloaded to `/tmp/` and handed to the WebFetch role as a set of files — the raw bytes, a tags-stripped text view, and a tag skeleton for HTML — and the role returns only what your goal asked for. Binary resources are handled too: PDFs are extracted with `pdftotext`, archives and office docs are identified with `file` and unpacked with the matching tool (unzip/7z, pandoc) before reading. The original filename (from `Content-Disposition` or the URL) is preserved. `search_web` runs the query through the WebSearch role and returns filtered results; configure it under `webSearch` in settings (OpenRouter's web plugin is supported out of the box).

### Compaction

Context limits are a fact of life. Compaction runs as a **user** message with no tools available, and whatever the model outputs becomes the first user message in a clean session. This avoids breaking the prompt cache and follows best practice. The summary budget is controlled by `compactionReserveTokens` in settings.

### Data-driven & self-improving

Roles and prompts live entirely in editable JSON. Because the agent can read and write files and `/reload` its own config, a capable model can in principle generate its own workflows, tune the system prompts for each role, and improve its own performance over time. A richer declarative workflow layer — naming graph-like flows (Planning → Architecting → Implementing → Testing) with explicit branch criteria — is planned on top of the role graph that already exists today.

## How It Works

1. Beast launches the Agent Docker container.
2. Beast connects to the Agent's WebSocket server (port 13131).
3. You send messages/prompts through the Beast CLI/TUI.
4. The `AgentOrchestrator` receives messages and resolves the role and model.
5. `LlmService` runs the conversation loop: send the API request, parse tool calls, dispatch tools, handle rate limiting, retries, and errors (including automatic retry/fallback to the next model when a provider is overloaded).
6. Tools can spawn subagents (each its own session); the TUI streams each one, and the F10 tree lets you switch between them — the view also auto-follows whichever agent is currently working.
7. Session state is persisted after each turn.

## License

MIT License
