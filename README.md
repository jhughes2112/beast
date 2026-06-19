![Beast](readme/beast.jpg)

**It's your new favorite agent.**

## Architecture

```
Beast (Windows CLI / TUI)
  └── Launches a single docker container that does all the agent work
  └── maintains the connection to the agent and presents a nice TUI to the user
  └── multi-agent aware: an F10 session tree lets you watch and switch between the root
      agent and the subagents it spawns, each with its own live conversation

Agent (Docker container)
  └── Sandboxed LLM agent does not need to ask permission, it can't smash files outside the current folder anyway
  └── manages conversation lifecycle, drives conversation loop, presents a minimal set of tools to the LLM
  └── tools: file read/write/edit, ls, bash, web fetch, web search, and delegation to subagents
      (assign_work) with a review / commit-and-rebase workflow
  └── ships a full dev toolchain in the image: Python, Node, Rust, Go, Java, Ruby, the .NET SDK,
      plus file utilities (zip/unzip, 7z, pdftotext, pandoc, jq, sqlite3, …)
  └── talks to anything → Messages (Anthropic) / ChatCompletions (OpenAI) / Responses (OpenAI), all in streaming mode
```

## Quick Start

### Prerequisites
- Docker Desktop
- Local LLM or API keys (OpenAI, Anthropic, OpenRouter, etc)

### Build

```bat
build.bat
```
This produces beast.exe in the same directory (entirely using docker as the build environment) and also a local "beastagent" Docker image

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
| `-p <text>` | Prompt text; everything after `-p` is treated as the prompt. Must be the last argument. |
| `/worktree [name]` | Run in an isolated git worktree (created if it doesn't exist). See [Worktrees](#worktrees). Beast-only. |
| `/verbose` | Show all tool calls and thinking in the TUI, and raise the container's log level. Beast-only. |
| `/debug` | Attach to an already-running Agent on port 13131 instead of launching a container. Beast-only. |
| `/test` | Run the Beast transport tests locally (and the agent tests in the container). |
| `/help` | Show help. Beast-only. |
| `<switch>` | Any other switch is forwarded to the agent container (e.g. an agent command like `/session new`). |

### Agent Commands (can be provided on the CLI)

| Command | Description |
|---------|-------------|
| `/compact` | Run context compaction |
| `/clear` | Clear the conversation |
| `/reload` | Reload the config files (settings + roles) |
| `/model <id>` | Switch the active model for the current role |
| `/session new` | Start a fresh saved session |
| `/session none` | Start an ephemeral session (not saved to disk) |
| `/session <id>` | Switch to an existing session (by id or display name) |
| `/session delete <id>` | Delete a saved session |
| `/finish` | Integrate the worktree and exit. See [Worktrees](#worktrees) |
| `/cancel` | Interrupt the current turn |
| `/test` | Run agent tests |
| `/quit` | Exit agent |
| `/help` | Show commands |

> `/role` has been removed. A run's role is selected up front (and switches as work is delegated to subagents); see [Roles](#roles).

### Worktrees

Passing `/worktree [name]` automatically creates a dedicated git worktree and runs the agent inside it, so the work is isolated on its own branch and never touches your main checkout.

When the work is done, `/finish` integrates it and exits — but only if everything is committed properly: it checks that the worktree has no uncommitted changes and that its branch is fully merged into the base. If so, it folds the worktree away, deletes the now-merged branch, and shuts the container down.

If the worktree isn't clean, `/finish` refuses and reports exactly what is pending (uncommitted files or un-integrated commits). At that point you tell the agent how you want the unfinished files handled — finish and review the work, or reset the branch to discard it — and then run `/finish` again.

## Configuration

Settings are loaded from `.beast/settings.json` (project dir first, then `~/.beast/`).  Both are bound into the docker container so the agent can read them.

**Simply run beast and it produces a new set of default config files, so you can edit them.**  It is convenient to put API keys in your ~/.beast/ config files, so they are always available and you don't have to repeat yourself or scatter API keys all over the place.

You can configure providers and the available models (with a single flag to disable each model), edit the context compaction prompt, and also the continuation prompt that will be injected when tiered model routing is implemented.

### Roles

A Role bundles three things: an **ordered list of models** to use (priority order — the first one currently available wins), the **tools** that role may call, and the **system / continuation prompts** injected when it runs. The in-code defaults are authoritative and versioned with the build; they are written to `.beast/roles.json` on first run, and your edits there (or in `~/.beast/roles.json`) override them and can add new roles.

The reason roles exist is to put *you* in control of the model selection order and the prompts. Front-load a cheap local model so it does the bulk of the work, and fall back to a premium API model only when the local one isn't available — this saves an enormous number of expensive tokens. Tune each role's prompt however you see fit for its job.

**Roles are also the workflow.** Each role is a node, and delegation between roles (`assign_work` → `review_work` → `commit_and_rebase`) wires them into a pipeline. The built-in Default → Developer → Reviewer loop below is just the most obviously useful one; more roles and chains can be added.

Built-in roles:
- **Default** — the conversational driver you talk to; read-only tools, delegates real work to subagents
- **Developer** — full read/write/shell access in a git worktree; makes the change, gets it reviewed, integrates it
- **Reviewer** — read-only; approves or rejects a Developer's change with actionable comments
- **Explorer** — first-pass discovery; returns a line-number roadmap of a file so callers read only the relevant regions
- **WebFetch** — backs the `fetch_url` tool; reads a fetched resource and returns only what the goal asks for
- **WebSearch** — backs the `search_web` tool; returns live results filtered to the goal

In the default loop, Default delegates with `assign_work`, the Developer implements and calls `review_work` until the Reviewer approves, then `commit_and_rebase` integrates the change onto the base branch.

### Web Fetch & Search

`fetch_url` does not dump a raw page into context. The resource is downloaded to `/tmp/` and handed to the WebFetch role as a set of files — the raw bytes, plus a tags-stripped text view and a tag skeleton for HTML — and the role returns only what your goal asked for. Binary resources are handled too: PDFs are extracted with `pdftotext`, and archives/office documents are identified with `file` and unpacked with the matching tool (unzip/7z, pandoc) before reading. The original filename (from `Content-Disposition` or the URL) is preserved on the raw file. `search_web` similarly runs the query through the WebSearch role and returns filtered results.

### Workflows

Roles already serve as the workflow today: each role is a node, and delegation between them forms the graph (see [Roles](#roles)). A richer declarative layer on top of that — naming graph-like flows such as Planning → Architecting → Implementing → Testing → Finaling, with explicit criteria for branching between roles (or completing the flow) — is still planned.

### Data Driven

All these Roles and Workflow folders are modifiable and potentially could be written and edited by the Agent, and a Tool will be added to allow it to /reload its own settings.  This provides for a smart model to generate its own workflow, edit the system prompts for roles, and generally learn to improve its own performance over time.

### Compaction

Context limits are a fact of life.  Compaction happens as a USER message, is provided no tools, and whatever the model outputs becomes the first user message in a clean session.  This avoids breaking prompt cache and follows best practices, but has a 4k hardcoded summary size.  That may need adjusting or turned into a setting.

## How It Works

1. Beast launches Agent Docker container
2. Beast connects via WebSocket to Agent's server
3. User sends messages/prompts through Beast CLI
4. AgentOrchestrator receives messages, resolves role and model
5. LlmService runs conversation loop:
   - Sends API request to LLM
   - Parses tool calls from response
   - Dispatches tools
   - Handles rate limiting, retries, errors
6. Tools can spawn subagents (their own sessions); the TUI streams each one and the F10 tree
   lets you switch between them — the view also auto-follows the agent currently doing work
7. Session state persisted after each turn

## License

MIT License
