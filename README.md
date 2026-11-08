![Beast in action](readme/beautyshot.jpg)

**It's your new favorite agent harness.**

Each Beast instance run manages a workflow of LLM agents **sandboxed in Docker**, driven from a polished terminal UI on Windows, macOS or Linux. The model can read, write, run shells, browse the web, and hand off work to other agents. The only way it can reach outside the sandbox goes through a `host_command` tool, guarded by the **HallMonitor**: you decide the trust level (Ask, Block, Allow) and if you configure a Jev-style Decider model, it auto-approves safe commands and asks you only when it isn't sure.

**You own the model routing.** Every agent is a *Role*: an ordered list of models to try, the tools it may call, and its prompt. Put a cheap local model first and fall back to a premium API model only when you need it.

**Bring the plan you already pay for.** Beast runs on a **ChatGPT subscription** or a **Claude subscription**, and on **any per-token API endpoint**, including local servers. And you decide which does what.  When a subscription hits its usage limit, Beast continues with the next model automatically.  If you're rate limited, it retries or falls back to another model.

---

## Quick start

**Getting Started:** 
1. Install Docker Desktop.  
2. Run `build.bat` (Windows) or `./build.sh` (Mac, Linux)
3. The build installs `beast` to `~/.local/bin` (`%USERPROFILE%\.local\bin` on Windows), the same place Claude Code installs `claude`. If that folder is not on your PATH yet, the build says so; add it
4. cd /wherever/you/want/to/work
5. beast
 
Notes: The build process itself runs inside a docker image and outputs the executable right next to the build script, super easy. (Don't run `build-all.bat`, that produces Beast for every OS/CPU combination, so it's just for testing and takes a long time.)

First launch will open the config picker automatically. Use /config to get back there later.

---

## Recent changes

- **HallMonitor** — a new decider role judges every command the agent wants to run on your host as allow, block or unsure, and only unsure ask for confirmation
- **HallMonitor auto calibration** — a decision model it hasn't used before is calibrated against a built-in allow/deny command set
- **`host_command` tool** — Beast attempts to work on your local machine safely (no guarantees, your security posture is your responsibility)
- **Cross-platform builds** — Windows, Mac, Linux, amd64, arm64
- **Session launcher** — resume and control is much simpler now
- **`sessionsDir` setting** — configurable location for worktree checkouts and conversation logs, so you can move it from `~/.beast/` to a drive with more space
- **Decider model support**
- **`/clear` and auto-resume**
- **Improved role prompts**
- **`settings.json`** — API keys are separated from prompts, tools, and decider config files so they can be edited or deleted and regenerated automatically.
- **Smarter `commit_and_rebase`** — picks up your git identity from the git folder
- **GPU-accelerated Chromium** — headless screenshots now take seconds not minutes on machines with a GPU.  Mac will still be slow unless screenshots are taken via host_command, there's no GPU support in Docker.
- **Vulkan 1.4 container** — uses lavapipe CPU driver so you can do headless dev on graphical apps inside the sandbox
- **Lingering media** — images stay in context until new images are requested, keeping the prefix cache warm.
- **Multi-image tool calls** — `inspect_media` takes several images at once with a `max_dimension` the model controls, with drag and drop support
- **Repeatable page screenshots** — `browser_screenshot` runs any page for an exact number of timesteps, with its clocks, CSS animations, timers and random numbers controlled, and gives the same bytes every time
- **Usage-limit auto-resume** — when a subscription window runs out, or an API is rate limited, Beast waits for the reset and continues the turn by itself.
- **OpenRouter batch model** — `:batch` aren't supported, so they're filtered out now
- **Repeatable runs** — give a model a seed and Beast removes the differences it can control, so a change you make is the only thing that changes the run. See [Repeatable runs](#repeatable-runs)
- **Two-step `fetch_url`** — getting a page can take a different path each time, so that work no longer shapes the answer. The fetching agent can only return file paths, or name one file for a fresh agent to summarize from the goal and that file alone
- **A seed per turn** — a seeded run adds one to the seed for each model reply already in the conversation. With one seed on every request, a run that copied one turn into its context repeated it 27 times
- **Steadier `curl` output** — per-request headers like `age` and `cf-ray` are removed from HTTP responses in seeded runs, because they differ on every request and pushed runs apart
- **Output ask retries** — a server that refuses a request only because Beast asked for too much output now gets a smaller ask, even if it refuses mid-stream. These used to look like a full context, which stalled small conversations
- **`tools/probe_determinism.py`** — tests whether a chat completions endpoint gives repeatable output, and which situations break it

---

## `/config` — endpoints, models, web search

<!-- SCREENSHOT: readme/config-endpoints.png — the /config home screen showing endpoints + the web search section -->
![The /config screen](readme/config-endpoints.png)

Pick from 27 built-in endpoint presets or bring your own URL. Beast then **discovers what it serves automatically**.

<!-- SCREENSHOT: readme/config-models.png — the model list for an endpoint, newest first, showing age/context/price/modality columns -->
![Choosing models](readme/config-models.png)

Models sort newest-first. `space` toggles, `Enter` edits a value, type to filter, `Esc` saves.

- **Blank means auto** — anything left empty is re-discovered every launch, so nothing goes stale.
- **Only what can't be discovered gets saved.** Your `settings.json` stays tiny.
- **Disabling isn't forgetting** — a switched-off model keeps its settings.

**Web search** lives on the same screen. Six providers supported. Beast uses the **cheapest one enabled** and falls through to the next if it fails.

---

## `/role` — assign models and fallback order

<!-- SCREENSHOT: readme/role.png — the /role editor showing a role name with arrows and its ordered model list -->
![The /role editor](readme/role.png)

`←→` switch role · `↑↓` pick a model · `+`/`-` move it up or down · `Esc` saves.

The first model that's available (and the context fits) gets the next turn. Providers can be flaky, but Beast keeps on going.

| Role | Job |
|------|-----|
| **Default** | Chats with you. Mostly read-only. Delegates real work. |
| **Developer** | Writes code, and if you use git, does it in an isolated worktree. |
| **Reviewer** | Checks the Developer's work. |
| **Explorer / WebFetch / WebSearch / MediaReader** | Cheap helpers that prevent context bloat in expensive models.  This lets you run small local models to do simple tasks not pay smart model prices. |
| **HallMonitor** | (optional) Checks the safety of host commands. |

### Typical Workflow: 
Default → Assigns work to Developer → Reviewer Approve/Reject → Developer commits.

<!-- SCREENSHOT: readme/sessions.png — F10 agent tree panel open beside a live conversation -->
![The agent tree](readme/sessions.png)

---

## Sessions

When you start `beast`, it always pulls up a list of "unfinished" sessions.  You can connect and continue any existing session, or start a new one.  Be aware that where you run beast from matters.  A **bare session** sandboxes the agent right in the folder you ran it from.  A folder can have several, each with its own name and conversations, all working on the same files.  Inside a git repo you can also start a **worktree session**, which sandboxes the agent **at the root of the git repo, in its own worktree**.  That way it has access to everything without needing to reach through to the host machine as often.

**Worktrees and session logs all live under `~/.beast/` by default, not in your project.** This keeps your project folder clean and makes resuming any project on your machine simpler.  When you Finish a session you are asking Beast to **delete this worktree** because all the work has been merged (by the Developer), then delete the session.  You can control the `sessionsDir` in `$HOME/.beast/settings.json` if your main disk is low on space:

```json
"sessionsDir": "D:\\BeastSessions"
```

Caution: If you move a worktree session's folder, go to your main repo and run `git worktree repair <new checkout path>` so it knows where it lives.

`/clear` starts a new conversation and keeps the old one in the F10 list to reopen later.  You can view every version of every conversation this way.

---

## HallMonitor — approving host commands

![HallMonitor Role](readme/deciders.png)

Sometimes you want work done directly on your machine. Both the Default and Developer roles can do this with the `host_command`.

Every host command is checked with the **HallMonitor** first. If you have a Decider model configured, it will return one of three verdicts:

| Verdict | What happens |
|---------|--------------|
| **Allow** | Runs. |
| **Block** | Refused, and the agent is told why. |
| **Unsure** | Whatever you picked on the `/config` screen: *ask me*, *block it*, or *run it*. |

If you don't set up a Decider model or it's unavailable, ALL commands are considered Unsure, and will follow your instructions accordingly.  If you set it to *run it* and an LLM nukes your home directory, this is on you.

### Approvals

![Approving a host command](readme/host-popup.png)

When a command reaches you for confirmation, you make the call:

- **Always** — run this exact command without asking from now on.
- **Yes** — run once.
- **No** — refuse once.
- **Never** — refuse this exact command from now on.

Always and never answers go in `~/.beast/decisions.json`, shared by every session. Matching must be exact-string.  You can edit the file by hand, and it's re-read before every decision.  The next time a model calibrates, your decisions become part of the calibration set.

**Auto-Calibration.** The first time HallMonitor sees a model, it measures that model against a hefty built-in set of known-safe and known-dangerous commands, plus your always/never answers, to set the model's allow and block boundaries. A popup shows the calibration progress. Until calibration finishes, all commands are treated as Unsure. Results per model are kept in `~/.beast/hallmonitor.json` and may recalibrate from time to time as the algorithm changes.

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
| `/effort <level>` | Reasoning effort: none, minimal, low, medium, high, max |
| `/compact` | Compact context |
| `/clear` | Start a new conversation, keep the old one |
| `/finish` | If the current worktree is finished, delete it and remove this session |
| `/reload` `/verbose` `/quit` | |

**Exiting:** A worktree session's agent Docker container stops, but until it's removed, all temp files and work in progress is still there.  You can resume it anytime.  A bare session's container is removed on exit, since its work is already in your folder; its conversations and logs are kept, and the next launch starts a fresh container.  If you are using git worktrees, even if you delete the agent container, the worktree is still on your disk.  The Developer's job is to `commit_and_rebase` the worktree onto whatever branch it came from when work is done.  However, **Finish** means to remove the worktree, which Beast will only do if your commits are merged in.

---

## Scripting

Maybe this is useful?  Beast can be run non-interactively too.  Examples:

```bat
beast --worktree featureName -p "Add a health check endpoint"
beast --worktree none /p "Summarize the architecture"
```

`--worktree` skips the session menu and opens or creates the specific worktree session. `none` means use this folder's most recently used bare session, which is also what a non-interactive run without `--worktree` does. Dash and slash are interchangeable on the command line.  For passing in prompts, `-p` must come last because anything after it is passed in as the prompt for convenience.

---

## Repeatable runs

If a run repeats exactly, you can change one thing, a prompt, a tool, a model setting, and know that any difference came from that change. That takes more than a seed, because one differing byte in any tool result sends the rest of the run somewhere else. So in a seeded run, Beast also:

- counts time instead of reading the clock, so file times and anything a script asks the clock for come out the same
- makes `browser_screenshot` run a page for an exact number of timesteps and give the same bytes every time
- gives shell output that varies from run to run a fixed form, or removes it: HTTP dates and per-request headers, `ls -l` dates, and graphics context addresses
- tells the model what has to stay the same on every run
- keeps `fetch_url`'s answer apart from the work of fetching
- adds one to the seed for each model reply already in the conversation, so no two turns share their random draws. One seed on every request made a copied turn come out the same again and again, and the run looped

None of this helps if the inference engine itself doesn't repeat, and a seed alone doesn't prove it does. Some engines repeat a request sent alone, then give different output when another request overlaps it.

### Checking an endpoint

`tools/probe_determinism.py` tests any chat completions endpoint. It's a single file that needs only Python 3, and it doesn't use Beast. Give it the URL, the API key (or `none`) and the model:

```bash
python3 tools/probe_determinism.py http://localhost:8080/v1 none my-model --quick
python3 tools/probe_determinism.py https://api.example.com/v1 "$API_KEY" my-model
```

Each detector tests one cause: run-to-run repeats, seeds, cache state, batch size, requests joining or leaving partway through, arrival order, logprobs and vision. Greedy requests rule out sampling, each detector first checks that its plain request repeats, and overlap is confirmed from stream timing. It's based on [Defeating Nondeterminism in LLM Inference](https://thinkingmachines.ai/blog/defeating-nondeterminism-in-llm-inference/).

| Verdict | Meaning |
|---|---|
| `HOLDS` | The situation happened and the output didn't change. That means no evidence of this cause, not proof it's absent |
| `BREAKS` | The plain request repeated, and this situation changed the output |
| `DRIFT` | The text matched, but the logprobs moved |
| `NOISY` | The plain request didn't repeat, so this cause can't be isolated |
| `NOT_REACHED` | The situation never happened, for example because requests queued instead of overlapping |
| `ERROR`, `SKIP` | A request failed, or the endpoint lacks what the detector needs |

A summary prints as it runs, and the full results go in a `determinism` folder in the current directory (`--out` changes it). `--list` shows every detector.

- **Keep the endpoint idle.** Other clients change the batches, and the probe can't see them.
- **It sends a lot of requests.** On a paid API, that costs real money.
- **`--quick` shows where to look, it isn't a verdict.** Rare differences need more tries, so raise `--rounds` when the answer matters.
- **The first request after a load isn't tested.** The chat completions API has no way to unload a model.

---

## Config files

Configs live in **`~/.beast/`**

A project can have its own `/project/.beast/roles.json` to override system prompts for that repo. Your API keys are entered once only, and stay in your home folder.

**Local models:** If you run your LLM on the host machine or in another Docker container, remember the agent is in Docker, so it reaches the host at `host.docker.internal`, not `localhost`. `/config` fixes this for you automatically if you get it wrong.

---

## Security model

The Beast agent program runs as root inside the container, but everything the LLM runs inside it are not.  This is nice because it means folders mounted for the agent, like the API keys and the `~/.beast/` folder where logs are stored, cannot be read or modified by the model.  It cannot steal your keys, cover its tracks, or hack your githooks to backdoor your host machine and elevate access.  All host commands are gated and can be as loose or locked down as you want.  

Sometimes a task requires packages installed that aren't built into the container, so `apt_install` is provided that allows plainly named packages to be installed.  pip, npm, cargo, and go install into `/home/beast/` which is the unprivileged user inside the container.  This all goes away when the session is deleted.

Basically, the model is allowed to modify the environment as it needs, but mainly sticks to `/workspace/` and `/tmp/`.

Finally, any top-level dot-folders are read only to the model.  This means `.git`, `.github`, `.vscode`, `.venv`, including any new ones it creates, are not writeable.  This prevents attacks on CI and editor configs.  Any edits to that would have to go through a `host_command` instead, which is actively monitored.

THIS IS NOT A SUBSTITUTE FOR DILIGENCE.  Beast makes a best effort at protecting the user, but these are essentially rogue alien programming agents invited into your computer with the hope they are well behaved.  I cannot be held responsible for the outputs of any LLM.  If you don't agree with that, you may not use this software.

---

MIT License
