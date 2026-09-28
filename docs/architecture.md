# Architecture

Yawble runs agent teams inside one container. A person signs in to the web board, talks to their
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
- **Admission.** `wip.maxRunning` bounds how many agents run at once across the instance. Work
  that cannot start waits in FIFO order and is shown as waiting, never failed. A Manager has one
  reserved slot so a pool full of members cannot starve it.
- **Repositories.** Each team clones its repositories under `repos/<Repo>/main` and every card gets
  its own git worktree. Fetch, merge, push and pull requests are a person's actions in the Git
  dialog; agents branch and commit only.

## Data

- One data root (`--DataRoot`, else `HARNESS_DATA_ROOT`; `/data` in the container), one SQLite
  database (`messages.db`), one Host process.
- Schema changes are append-only steps per store; a shipped step is never edited.
- The Host takes a daily `VACUUM INTO` copy of the database and keeps seven. See
  [ops/backups-logs-and-versions.md](ops/backups-logs-and-versions.md).

## Security model

- The container is the trust boundary. Inside it, the Host runs as the user `harness` and agents
  run as the user `agent` with no capabilities; the database, keys and logs are readable by the
  Host only.
- A headless agent receives only its own provider's API key (plus `GH_TOKEN` when its team has a
  GitHub remote). A member's platform key is passed in its environment and referenced, not
  written, in the MCP config files the launcher creates.
- Platform credentials are stored as SHA-256 hashes. Session cookies are signed with an ASP.NET
  Data Protection key ring under `<dataRoot>/keys`.
- Every response carries `nosniff`, `Referrer-Policy: same-origin`, `X-Frame-Options: DENY` and a
  self-only Content Security Policy; uploaded documents are served under a sandboxed policy.
- Failed sign-ins are rate-limited per account and per client address.
- Provider keys and `GH_TOKEN` are given to the operator CLI (`yawble secret set`, `yawble github`),
  kept in an owner-only file on the operator's machine and handed to the container at `yawble up`.
  Nothing is baked into the image.

## Operations

- [ops/releases-and-updates.md](ops/releases-and-updates.md): updating an instance and cutting a release.
- [ops/cli-releases-and-install.md](ops/cli-releases-and-install.md): releasing and installing the operator CLI.
- [ops/backups-logs-and-versions.md](ops/backups-logs-and-versions.md): backups, restores, logs and agent CLI versions.
- [triggers.md](triggers.md): what triggers cost, the wake choice and the daily token cap.
- [ops/folder-change-triggers.md](ops/folder-change-triggers.md): waking a member when files change in a folder.
