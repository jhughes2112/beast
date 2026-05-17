![KanBeast](env/wwwroot/kanbeast.jpg)

**AI-powered autonomous coding agents in a Kanban workflow.**

KanBeast is a self-hosted system where AI agents autonomously work on tickets from a Kanban board. A **Manager** agent breaks down tickets into subtasks and coordinates work. A **Developer** agent writes code, runs builds, fixes errors, and commits changes. All work happens in isolated Docker containers with full access to development tools.


## How It Works

1. **Create a feature ticket** describing what you want built or fixed
2. **Drag to "Active"** to assign a worker
3. A Docker container spins up, pulls the git repo, and starts two agents: Manager and Developer
4. **Manager** reads the ticket, explores the codebase, and creates subtasks
5. **Developer** executes each subtask: writes code, runs commands, iterates until done
6. **Feature branches** keep commits separate so multiple tickets work in parallel
7. **Dashboard** updates in realtime with updates, costs, and important steps

## Features

- **Autonomous multi-agent system** - Manager orchestrates, Developer executes
- **Any LLM provider** - OpenAI, Anthropic, OpenRouter, local models using OpenAI-compatible APIs, with automatic adjustment for rate limiting and multiple LLM fallbacks
- **Full dev environment** - .NET, Node.js, Python, gcc, git, and more in each container
- **Git integration** - Clones repos, creates branches, commits and pushes changes
- **Cost limiting** - Per-ticket token usage and cost monitoring, or zero if you don't care; can be adjusted live
- **Context compaction** - Configuratble auto-compaction to stay within context limits for big tasks
- **Skills system** - Reusable knowledge files agents can read and create
- **Easy to customize** - Settings and Prompts can be changed without stopping the server, just stop and restart the tickets to pick up changes.
- **Simple settings** - All settings are GUI-configurable and straightforward (although git is still git)
- **Realtime Web UI** - Simple Kanban board to manage tickets

![KanBeast](env/wwwroot/kanbeast.gif)

## Quick Start

### Prerequisites

- Docker
- OpenAI compatible LLM API key (OpenAI, Anthropic, OpenRouter, llama.cpp, Ollama, vLLM)
- git repository credentials (Github, Gitea, GitLab, ...)

### 1. Clone 

```bash
git clone https://github.com/jhughes2112/kanbeast.git
cd kanbeast
```

### 2. Run with Docker

**Windows:**
You can double-click `run-docker.bat` to start the server and pull up the browser interface. Hit ^C in the server window to shut it down.

**Linux/Mac:**
```bash
./run-docker.sh
```

Open a browser window to [http://localhost:8080](http://localhost:8080)

## Configuration

### settings.json

| Section | Description |
|---------|-------------|
| `llmConfigs` | Array of LLM endpoints. First one is used. Supports any OpenAI-compatible API. |
| `gitConfig` | Repository URL and authentication (SSH key, password, or API token) |
| `managerCompaction` | Context summarization settings for Manager agent |
| `developerCompaction` | Context summarization settings for Developer agent |

### Authentication Options

**GitHub/GitLab with token:**

Any git server that uses an api token should work. Create your repo, copy your token and fill out the following fields. Security Note: It's good practice to make an api key just for agentic use, not your personal one with full access to everything you do, but do what you want. I'm not responsible.
```
  repositoryUrl: https://github.com/you/repo.git
  apiToken:      ghp_xxxxx
  username:      Your Name
  email:         you@example.com
```

**SSH key:**

Any git server that you have configured with an SSH key will work fine. Security Note: It's a good idea to make a new SSH key for agentic purposes, separately from your personal one. If an agent decides to publish your key on Facebook, that's on you.
```
  repositoryUrl: git@github.com:you/repo.git
  sshKey:        -----BEGIN OPENSSH PRIVATE KEY-----\n...\n-----END OPENSSH PRIVATE KEY-----
  username:      Your Name
  email:         you@example.com
```

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                    KanBeast Server                   │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────┐  │
│  │  Kanban UI  │  │ Ticket API  │  │ Orchestrator │  │
│  └─────────────┘  └─────────────┘  └──────────────┘  │
└──────────────────────────────────────────────────────┘
                            │
                      Docker Socket
                            │
        ┌───────────────────┼───────────────────┐
┌───────▼───────┐   ┌───────▼───────┐   ┌───────▼───────┐
│ kanbeast-     │   │ kanbeast-     │   │ kanbeast-     │
│ agent-1      │   │ agent-2      │   │ agent-3      │
│ ┌───────────┐ │   │ ┌───────────┐ │   │ ┌───────────┐ │
│ │  Manager  │ │   │ │  Manager  │ │   │ │  Manager  │ │
│ │   Agent   │ │   │ │   Agent   │ │   │ │   Agent   │ │
│ └─────┬─────┘ │   │ └─────┬─────┘ │   │ └─────┬─────┘ │
│       │       │   │       │       │   │       │       │
│ ┌─────▼─────┐ │   │ ┌─────▼─────┐ │   │ ┌─────▼─────┐ │
│ │ Developer │ │   │ │ Developer │ │   │ │ Developer │ │
│ │   Agent   │ │   │ │   Agent   │ │   │ │   Agent   │ │
│ └───────────┘ │   │ └───────────┘ │   │ └───────────┘ │
│      git      │   │      git      │   │      git      │
└───────────────┘   └───────────────┘   └───────────────┘
```

## The /env/ Folder

| Content | Host Folder | Container Folder |
|---------|-------------|------------------|
| Settings | `/env/settings.json` | `/workspace/settings.json` |
| Prompts | `/env/prompts/` | `/workspace/promtps/` |
| Skills | `/env/skills/` | `/workspace/skills/` |
| Dashboard | `/env/wwwroot/` | `/workspace/wwwroot/` |
| Tickets | `/env/tickets/` | `/workspace/tickets/` |
| Conversations | `/env/logs/` | `/workspace/logs`/ |
| Repo | not mapped | `/repo/` |

**Pay attention, this is important.**

The `server` docker container maps the `/env/` folder to `/workspace/` inside the container. This means you can edit the system prompts, inspect the logs and tickets directly, add skills, and add content easily that exists outside of the repo.

When the server starts a `worker` container, it replicates all mapped volumes, which means however you configure the server, you are also configuring the workers.  **They all share volume mappings.** This is good because one worker can create a Skill and all of them now can access it. Workers read the settings.json file on startup (only) from this mapped folder.

The repo is not stored under this folder structure specifically to prevent multiple processes colliding with the same git installation.  **Each worker clones the repo** separately into their local containers on startup.  For very large repos, this can be a burden, just be aware of it.

## Skills

Agents can read and write skill files in `/workspace/skills/`. These are markdown files containing project-specific knowledge, patterns, and commands.  I didn't include much, because what you want is certainly going to be different than what I want.

Agents discover skills at startup and can create new ones when they learn something reusable.

## Project Structure

```
kanbeast/
├── KanBeast.Server/     # Web server, ticket API, worker orchestrator
├── KanBeast.Agent/     # Agent runtime, LLM integration, tools
├── env/                 # Runtime environment (mounted as /workspace)
│   ├── settings.json    # Configuration
│   ├── prompts/         # Agent system prompts
│   ├── skills/          # Reusable knowledge files
│   └── wwwroot/         # Kanban UI
├── Dockerfile           # Container image with dev tools
└── run-docker.bat       # Quick start script
```

## Development

I built everything in Visual Studio 2026.  You can use whatever you want.  The only strangeness you might find is I created a dummy project in the /env/ folder to allow for Copilot agents to "see" all the files in there.  It has no build steps.

### Running locally, not in Docker

The server requires Docker to spawn worker containers, but you can run it locally to debug server operations.  The majority of it is a REST api to handle ticket modifications, which you can hit with curl.  When running locally, I doubt workers will spawn correctly because of how volume mappings are being propagated.  So, comment that out and run the worker manually with the same command line, working directory set to /env/ to find the settings.json and other data.

### Customizing settings, prompts

Settings and system prompts are only read once up when the worker launches.  So to pick up changes, move the ticket back to Backlog, wait a few seconds for the worker to finish committing its work in progress to git, then drag it back to Active again.

## Limitations/Future Work

- Agents will do their best, but for some LLMs their best is not very good.  The system prompt may be poor for your specific project or task.  If you learn something that works well, please post your findings.
- Cost depends entirely on your LLM provider and ticket complexity. I have not yet attempted to have the Manager "pick" which LLM to use based on the task or price or other factors, although that is an interesting idea.
- Agents can and will make mistakes.  In an important repo, you might want to rewrite the **finish-branch** skill to trigger a pull request rather than YOLO after an AI decides another AI's work is :chefskiss:.

![KanBeast](env/wwwroot/kanbeast.png)

## License

MIT License - see [LICENSE](LICENSE)

## Contributing

Contributions welcome! Please open an issue first to discuss significant changes.
