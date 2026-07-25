![Beast in action](readme/beautyshot.jpg)

**It's your new favorite agent harness.**

Beast runs a fleet of cooperating LLM agents **sandboxed inside Docker**, driven from a polished Windows terminal UI. The model can read, write, run shells, browse the web, and hand work to other agents — and it physically cannot touch anything outside the folder you point it at, so it never asks permission.

**You own the model routing.** Every agent is a *Role*: an ordered list of models to try, the tools it may call, and its prompt. Put a cheap local model first and fall back to a premium API model only when you need it.

---

## Quick start

**You need:** Docker Desktop, and one API key *or* a local model server.

```bat
build.bat          :: produces beast.exe + the beastagent image
beast              :: run it in the folder you want to work in
```

First launch opens the config picker automatically. Add an endpoint, paste your key, spacebar the models you want. Done.

---

## `/config` — endpoints, models, web search

<!-- SCREENSHOT: readme/config-endpoints.png — the /config home screen showing endpoints + the web search section -->
![The /config screen](readme/config-endpoints.png)

Pick from 27 built-in endpoint presets (llama-server, OpenAI, Anthropic, OpenRouter, Gemini, xAI, Ollama, LM Studio, vLLM…) or type your own URL. Beast then **asks the endpoint what it has** — context windows, pricing, modalities, release dates — so you don't hand-write any of it.

<!-- SCREENSHOT: readme/config-models.png — the model list for an endpoint, newest first, showing age/context/price/modality columns -->
![Choosing models](readme/config-models.png)

Models sort newest-first. `space` toggles, `Enter` edits a value, type to filter, `Esc` saves.

- **Blank means auto** — anything left empty is re-discovered every launch, so nothing goes stale.
- **Only what can't be discovered gets saved.** Your `settings.json` stays tiny.
- **Disabling isn't forgetting** — a switched-off model keeps its settings.

**Web search** lives on the same screen. Six providers (OpenRouter · Perplexity · Anthropic · OpenAI · xAI · Gemini), each priced per 1k searches. Beast uses **the cheapest one you've enabled** and falls through to the next if it fails. No API key to enter — it borrows the key from the endpoint sharing its domain.

---

## `/role` — who uses which model, in what order

<!-- SCREENSHOT: readme/role.png — the /role editor showing a role name with arrows and its ordered model list -->
![The /role editor](readme/role.png)

`←→` switch role · `↑↓` pick a model · `+`/`-` move it up or down · `Esc` saves.

The first model that's available and fits gets the turn. That's the whole trick: local model first, premium as the fallback rather than the default.

| Role | Job |
|------|-----|
| **Default** | Chats with you. Read-only. Delegates real work. |
| **Developer** | Writes the code in an isolated git worktree. |
| **Reviewer** | Approves or rejects with actionable comments. |
| **Explorer / WebFetch / WebSearch / MediaReader** | Helpers behind individual tools. |

Chat with Default → Default assigns to Developer → Developer gets Reviewer's sign-off → commits. Press **F10** to watch any agent's live conversation.

<!-- SCREENSHOT: readme/sessions.png — F10 agent tree panel open beside a live conversation -->
![The agent tree](readme/sessions.png)

---

## Images and files

Drag a file onto the window, or **Alt+V** to paste a screenshot. (Ctrl+V is swallowed by Windows Terminal, which is a terminal thing, not a Beast thing.)

If the current model takes images, the image goes into the conversation properly — attention runs over the real pixels, and you can keep asking about it. If it doesn't, Beast tells you which of your enabled models *do*, so you can `/model` across and resend. It won't quietly swap in a text description and pretend that's the same thing.

---

## Keys and commands

| | |
|---|---|
| `Alt` + `↑↓` / `←→` | Scroll |
| `Click` | Expand / collapse a block |
| `Ctrl+O` | Cycle detail level |
| `F10` | Agent tree |
| `Alt+V` | Paste image or files |
| `Tab` | Accept command completion |

| Command | |
|---------|---|
| `/config` | Endpoints, models, web search |
| `/role` | Model order per role |
| `/model <id>` | Switch model now |
| `/compact` | Compact context |
| `/finish` | Integrate the worktree and exit |
| `/reload` `/verbose` `/quit` | |

**Exiting:** `/finish` folds the worktree away — but only once everything is committed. `Ctrl+C` leaves it intact to resume later.

---

## Scripting

```bat
beast --worktree featureName -p "Add a health check endpoint"
beast /p "Summarize the architecture"     :: no git, current folder
```

Dash and slash are interchangeable. `-p` must come last.

---

## Config files

Everything lives in **`~/.beast/`** — one place, every project.

| File | What |
|------|------|
| `settings.json` | Endpoints, enabled models, web search. Managed by `/config`. |
| `roles.json` | Role prompts, tools, model order. Managed by `/role`. |

A project can drop its own `.beast/roles.json` to override prompts for that repo. API keys stay in your home folder and never land in a project.

**Local models:** point at `host.docker.internal`, not `localhost` — the agent is in a container. `/config` fixes this for you automatically if you get it wrong.

---

## How it works

```
Beast (Windows TUI)  ──WebSocket──►  Agent (Docker, port 13131)
   launches the container                sandboxed to your project folder
   renders the UI, F10 agent tree        runs the conversation loop + tools
                                          spawns subagents, each its own session
```

Anthropic Messages, OpenAI ChatCompletions, and OpenAI Responses are all supported and streamed; the protocol is detected from the endpoint URL. The container ships Python, Node, Rust, Go, Java, Ruby, the .NET SDK, plus jq, sqlite3, pdftotext, pandoc, 7z and friends — so the agent can actually do the work.

---

MIT License
