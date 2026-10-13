# Architecture

Yawble runs agent teams in a control container and its worker containers, on one volume. A person signs in to the web board, talks to their
**Concierge** (an interactive agent in a terminal), and the Concierge hands work to **teams**: a
**Manager** and headless **members**, each launched from an agent CLI (Claude Code, Codex, GitHub
Copilot CLI, Grok). A message log decides who wakes, when, and with what context.

## Components

| Part | Where | What it does |
|---|---|---|
| Host | `src/Harness.Host` | ASP.NET Core process: the HTTP API, the MCP server at `/mcp`, the built web app, the agent launcher, triggers, backups. |
| Contracts | `src/Harness.Contracts` | Shared records and interfaces. |
| Messaging | `src/Harness.Messaging` | The append-only message log (SQLite) and the pump that wakes subscribers. |
| Identity | `src/Harness.Identity` | Users, teams, principals and API keys. |
| Backlog, Kanban, Skills | `src/Harness.Backlog`, `src/Harness.Kanban`, `src/Harness.Skills` | The backlog store, the board projection, and the skill index. |
| Containers | `src/Harness.Containers` | One logical container per member: its subscription and its runs. |
| Pty, Streaming | `src/Harness.Pty*`, `src/Harness.Streaming` | The Concierge terminal and live output to the browser. |
| Web | `web/` | The Quasar (Vue 3) board. |
| Operator CLI | `cli/` | `yawble`, in Go: install, start, update, diagnose and expose an instance. |

`Harness` is the code name used in namespaces, environment variables (`HARNESS_*`) and file names.
The product name, Yawble, lives in the web app's presentation layer
(`web/src/presentation/product.ts`) and in the operator CLI.

## How work flows

- **The log is the source of truth.** Every instruction, hand-back, progress line and completion
  is a row in the message log. Each member's container subscribes to the rows addressed to it;
  the agent process exits after each run, the subscription does not.
- **Context comes from the ledger,** a projection of the log, not from an agent's transcript.
- **Agents talk to the platform through MCP tools only** (`tell`, `progress`, `handback`,
  `blocked`, `backlog`, `kanban`, `repo`, ...), authenticated with a per-member key. There is no
  agent-facing CLI and no HTTP fallback.
- **Workflows.** An instruction roots a workflow; `HARNESS_CAUSATION` carries it into each run so
  follow-up instructions join it. The Manager that owns a workflow declares it complete.
- **Admission.** `wip.maxRunning` bounds how many agents run at once across the instance (default
  the smaller of CPUs - 1 and the memory limit / `wip.memoryPerRunMb`; `GET /api/wip` names which). Work
  that cannot start waits in FIFO order and is shown as waiting, never failed. A Manager has one
  reserved slot so a pool full of members cannot starve it.
- **Repositories.** Each team clones its repositories under `repos/<Repo>/main` and every card gets
  its own git worktree. Agents branch and commit; the platform pushes a team's branches to origin
  when a run ends. Fetching, bringing current, merging to the default branch and opening a pull
  request are a person's actions in the Git dialog (the Concierge merges only when a person turns
  on `concierge.mayMerge`). A repository can also be a local one kept on the
  instance, named `local:<name>`; see [local-repositories.md](local-repositories.md).

## Data

- One data root (`--DataRoot`, else `HARNESS_DATA_ROOT`; `/data` in the container), one SQLite
  database (`messages.db`), one control process. Workers open neither the database nor the key
  ring; `all` is the single-process form, for tests and development.
- Schema changes are append-only steps per store; a shipped step is never edited.
- A team's activity view begins at its creation, or later where a person starts it from now
  (`PUT /api/teams/{team}/activity-from`). That instant narrows the team's activity chart and its
  token runs and nothing else: no ledger, hold or log row is changed, spend, caps and outcomes read
  every row, and clearing it shows all of the team's history again.
- The Host takes a daily `VACUUM INTO` copy of the database and keeps seven. See
  [ops/backups-logs-and-versions.md](ops/backups-logs-and-versions.md).

## Control and workers

- **Two images from one build.** The `Containerfile`'s `control` target holds the Host, git, gh and
  sqlite3, and no Node and no agent CLI; its `worker` target, the last stage, holds the toolchain
  agents use and installs the agent CLIs onto the volume. Both run the one Host from the one build
  stage, so a worker's version is control's, and both carry `harness`, `agent` and `worker` at the
  same ids. A release pushes `<image>:<version>` (control) and `<image>:<version>-worker`, then
  `latest` and `latest-worker`.
- **The pod.** On Podman, control (`yawble`) and each worker (`yawble-worker-<n>`) run in one pod,
  so they share a network namespace and nothing else: `127.0.0.1:8080` is control in every
  container, which is what members are told (`HARNESS_URL`) and what a worker connects to
  (`ws://127.0.0.1:8080`, loopback, so plain `ws://` is accepted). On Docker each worker joins
  control's network namespace (`--network container:yawble`). Only control listens, and only the
  published port reaches it. A tunnel (`yawble remote`) runs in a container of its own.
- **The worker key** reaches the containers only as an env file. The entrypoint writes it to
  `/run/harness/worker-key`, readable only by that container's Host user, and unsets it before
  anything else runs, so no agent can read it from a process's environment.
- **Health and drain.** A worker is healthy exactly while it is connected to control: its Host
  keeps `/var/lib/harness-worker/connected` fresh, and the worker image's healthcheck reads it. The
  operator CLI drains a worker before stopping it by creating `drain` there: control places nothing
  new on it, its runs go on, and the CLI waits for them.
- **Sizes.** The operator's memory and CPU settings are per worker; control gets a fixed allowance
  of its own. A worker that hears nothing from control for 120 s exits, and the engine's restart
  policy starts it again.
- **Who reads what.** Control prepares the volume on every start; a worker changes no ownership on
  it and waits until control has. The database, the key ring and every record the doctor reads are
  `harness`'s alone; teams, documents, the agent home and the tool folders are group `agent`'s, which
  both Host users and the agent are in. Each worker's own state is in its container, not on the
  volume.

## Security model

- The container is the trust boundary. Inside it, control's Host runs as the user `harness`, a
  worker's Host as the user `worker`, and agents run as the user `agent` with no capabilities; the
  database, keys, logs and the doctor's records are readable by control's Host only. `worker`
  (10003, in group `agent`) reads on the volume exactly what `agent` can.
- A headless agent receives only its own provider's API key (plus `GH_TOKEN` when its team has a
  GitHub remote). A member's platform key is passed in its environment and referenced, not
  written, in the MCP config files the launcher creates.
- A member's temporary files go in its own folder, `<dataRoot>/tmp/member-<random>`
  (`/data/tmp/...` in the container), and every headless run is started with `TMPDIR` set to it.
  It is on the data volume, not in `/tmp`, because an engine may mount `/tmp` in memory, where
  every file a build or test leaves counts against the container's memory limit. The launch
  creates the folder on first use, as the agent and owner-only, and records it with a link
  `.tmpdir` in the member's workspace; a new workspace gets a new, empty folder. So no two members
  share one, and none shares the Host's files. The path is kept short: tools put Unix sockets in
  `TMPDIR` (the .NET runtime's named pipes, used by the test platform, build servers and the
  compiler server) and a socket path holds only 103 bytes. When the data root's path is too long
  to leave room for a socket, or the folder cannot be made, the Host uses `/tmp` instead and its
  log line "Member temporary folders" says which. The folder is a real one, not a link, so paths
  under `TMPDIR` resolve to themselves. It is not removed with the workspace. On Windows `TMPDIR` is
  `.tmp` in the workspace. The Concierge and the Host's own temporary files (prompt files, MCP
  configs) stay in the Host's temp folder.
- Platform credentials are stored as SHA-256 hashes. Session cookies are signed with an ASP.NET
  Data Protection key ring under `<dataRoot>/keys`.
- Every response carries `nosniff`, `Referrer-Policy: same-origin`, `X-Frame-Options: DENY` and a
  self-only Content Security Policy; uploaded documents are served under a sandboxed policy.
- Failed sign-ins are rate-limited per account and per client address.
- Provider keys and `GH_TOKEN` are given to the operator CLI (`yawble secret set`, `yawble github`),
  kept in an owner-only file on the operator's machine and handed to the container at `yawble up`.
  Nothing is baked into the image.

## The shared agent home

Every agent CLI runs as the `agent` user with one shared home, `/data/agent-home`: the CLIs' own
sign-ins live there, and so does anything a person sets up for the Concierge (account connectors,
MCP servers, plugins, skills, memory and instruction files).

- **One way to sign in: the shared home.** Every agent run - a member's and the Concierge's -
  signs in through the shared home, with the CLI's own login there or a provider key handed to the
  container. A member run keeps only its own command's credential variables (each preset's
  `credentialVariables`, measured on its CLI, and its `credentialVariable` in `auth-probes.json`)
  plus `GH_TOKEN` for a team with a GitHub remote; every other command's variables are removed at
  the spawn site, and what the run prints is redacted of every credential its environment carries.

- **The Concierge keeps every tool.** It is the person's own session and launches with the signed-in account's connectors (for Claude, `claude_ai_*`: Gmail, Claude Docs, Calendar,
  Drive), the home's MCP servers, plugins and skills. That is an accepted risk, like the Concierge's
  provider keys: text it reads - a document, a team's output, a README - can ask it to use the
  person's accounts.
- **A member gets only the platform's tools**: the `harness` MCP server and its CLI's own local
  tools. Each headless Agent preset declares `isolation` - the launch arguments and environment
  that switch the account and the home off for a member, the CLI's own tools it may use, and the
  gaps no switch reaches - and a headless preset without one is shown as *not verified*. Nothing in
  the home is edited or removed to do it. Members never get the Concierge's tools, even when the
  Concierge tells them what to do.
- **What a person may put in the home:** the CLIs' sign-ins and their own settings for the
  Concierge. **What belongs in Connections instead:** anything account-scoped an agent should act
  on - a mailbox, a drive, a calendar, an API. A connection reaches a member only when a person
  binds it ([connections.md](connections.md)).
- **What still reaches a member from the home**, measured on the CLIs in the image and listed in
  each built-in preset's `gaps`: Codex reads `~/.codex/AGENTS.md`; Grok reads MCP servers from
  `~/.grok/config.toml` (where the Host writes its own `harness` entry) and skills from
  `~/.grok/skills` and `~/.claude/skills`; Copilot reads `~/.copilot`'s MCP config, plugins,
  skills and instructions. Do not put anything there that a member must not have.
- **A preset's local tools can depend on the model.** Copilot with `--model auto` offers
  `apply_patch` and `rg` on one model and `create`, `edit` and `grep` on another, so its allowed
  list holds every set measured here; a model not yet measured may be offered a file tool the
  list lacks, and the per-run check names it. Grok's subagent sessions add
  `wait_commands_or_subagents`. Claude's are pinned by `--tools` whatever the model.
- **A member's commits carry no AI attribution unless a person turns it on.** The setting
  `agents.commitAttribution` (Admin > Settings > System, off by default) decides whether a member's
  CLI may add its own co-author trailer or "generated with" line to the commits and pull requests
  it writes. While it is off, each headless preset's `attribution` declaration is applied to a
  member's launch: arguments beside the isolation arguments, variables after the preset's and the
  team's env, with notes saying what was measured on the CLI in the image. Claude is passed
  `--settings {"attribution":{"commit":"","pr":""}}`, which outranks a checkout's own
  `.claude/settings.json`. Codex adds its trailer only for an account that enables commit
  attribution and has no launch switch for it; Grok adds none; Copilot adds `Co-authored-by:
  Copilot` and turns it off only through `includeCoAuthoredBy: false` in a settings file, which no
  launch switch reaches, so a Copilot member's commits may still carry it. The Concierge launches
  as its preset says. The platform's own commits carry no trailer whatever the setting.
- **Checked before a run, too.** At start and after every catalog save, the Host asks each
  installed CLI what it would load, with the CLI's own listing and no model call (`claude mcp list`
  and `plugin list`; `codex mcp list`, `features list` and `debug prompt-input`; `grok inspect`;
  `copilot mcp`/`plugin`/`skill list`), run as the agent user: a member's preset with its
  isolation, the Concierge's without it. `yawble doctor` (the `agent tools` row and block) and
  Admin → Agents show each preset as *isolated*, *foreign tools found* (named), *not verified* or
  *not measured*, with its gaps; the Concierge's connectors and servers are listed as
  information. Skills and hooks are listed, never counted as tools. `claude mcp list` starts each
  configured stdio server to check it, as the Concierge's own launch does. The result is
  `GET /api/agents/tools`, and `<dataRoot>/agent-tools.json` for `--doctor`.

## Operations

- [ops/releases-and-updates.md](ops/releases-and-updates.md): updating an instance and cutting a release.
- [ops/cli-releases-and-install.md](ops/cli-releases-and-install.md): releasing and installing the operator CLI.
- [ops/backups-logs-and-versions.md](ops/backups-logs-and-versions.md): backups, restores, logs and agent CLI versions.
- [triggers.md](triggers.md): what triggers cost, the wake choice and the daily token cap.
- [ops/folder-change-triggers.md](ops/folder-change-triggers.md): waking a member when files change in a folder.
