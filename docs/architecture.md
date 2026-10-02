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
- **Admission.** `wip.maxRunning` bounds how many agents run at once across the instance (default
  the smaller of CPUs - 1 and the memory limit / `wip.memoryPerRunMb`; `GET /api/wip` names which). Work
  that cannot start waits in FIFO order and is shown as waiting, never failed. A Manager has one
  reserved slot so a pool full of members cannot starve it.
- **Repositories.** Each team clones its repositories under `repos/<Repo>/main` and every card gets
  its own git worktree. Fetch, merge, push and pull requests are a person's actions in the Git
  dialog; agents branch and commit only. A repository can also be a local one kept on the
  instance, named `local:<name>`; see [local-repositories.md](local-repositories.md).

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
- A member's temporary files go in its own folder, `/tmp/member-<random>` (the Host's temp folder),
  and every headless run is started with `TMPDIR` set to it. The launch creates the folder on
  first use, as the agent and owner-only, and records it with a link `.tmpdir` in the member's
  workspace; a new workspace gets a new, empty folder. So no two members share one, and none
  shares the Host's `/tmp` files. The path is kept short on purpose: tools put Unix sockets in
  `TMPDIR` (the .NET runtime's named pipes, used by the test platform, build servers and the
  compiler server) and a socket path holds only 103 bytes, which a path inside a workspace can
  use up on its own. No extra variable is needed to run a test suite. The folder is a real one,
  not a link, so paths under `TMPDIR` resolve to themselves. It is not removed with the
  workspace. On Windows `TMPDIR` is `.tmp` in the workspace. The Concierge and the Host's own
  temporary files (prompt files, MCP configs) are unchanged.
- Platform credentials are stored as SHA-256 hashes. Session cookies are signed with an ASP.NET
  Data Protection key ring under `<dataRoot>/keys`.
- Every response carries `nosniff`, `Referrer-Policy: same-origin`, `X-Frame-Options: DENY` and a
  self-only Content Security Policy; uploaded documents are served under a sandboxed policy.
- Failed sign-ins are rate-limited per account and per client address.
- Provider keys and `GH_TOKEN` are given to the operator CLI (`yawble secret set`, `yawble github`),
  kept in an owner-only file on the operator's machine and handed to the container at `yawble up`.
  Nothing is baked into the image.
- An agent's **issued credential** is stored once per CLI command, as Data Protection ciphertext in
  the database, set and cleared only by a person (Admin → Agents) or the operator CLI's request
  file, each with its audit row. No response, log or file carries the value: a preset answers only
  whether it is set, by whom and when. You are responsible for your provider's terms when one credential is used by many runs.

## The shared agent home

Every agent CLI runs as the `agent` user with one shared home, `/data/agent-home`: the CLIs' own
sign-ins live there, and so does anything a person sets up for the Concierge (account connectors,
MCP servers, plugins, skills, memory and instruction files).

- **Two ways to sign in: the shared home or an issued credential.** Each preset has a source,
  chosen per preset in Admin → Agents or with `yawble agents source <preset> home|issued` (the
  `agents.credentialSource` setting). `home`, the default, signs in through the shared home exactly
  as before. Under `issued`, a run receives its command's credential in its own environment only,
  with a HOME of its own under its member temporary folder, removed when the run ends; it never
  reads the shared home's logins, and a member run whose credential is not set does not start - it
  never falls back to the home.
- **The credential is stored once per command, the source is chosen per preset.** One value is kept
  for each CLI command (`claude`, `codex`, `grok`, `copilot`) and shared by every preset that runs
  that command: setting the Claude key once serves both `claude` (the Concierge) and
  `claude-headless` (members) when their source is `issued`. It is set in Admin → Agents or with
  `yawble agents credential set <preset|command>`, which reads it from a hidden prompt or stdin and
  never from the command line. You are responsible for your provider's terms when one credential is used by many runs.
- **The Concierge keeps its home under `issued`, too.** It is given the credential beside its
  login; which of the two its CLI uses is the preset's measured `loginPrecedence`, and Admin →
  Agents states it: Claude uses the issued credential, Grok the person's login while one exists,
  and Codex's terminal interface is not measured. With no credential set, the Concierge starts on
  the person's own login.

- **The Concierge keeps every tool.** It is the person's own session and launches as it always has,
  with the signed-in account's connectors (for Claude, `claude_ai_*`: Gmail, Claude Docs, Calendar,
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
