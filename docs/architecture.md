# Architecture

Yawble is two processes on the person's computer and one sandbox per agent run.

```
┌──────────── Electron shell ────────────┐     local IPC      ┌──────────────── yawble-core (.NET) ────────────────┐
│ windows, menus, tray, notifications     │ ◄────────────────► │ all state: SQLite, bus, scheduler, policy, git,     │
│ renderer (Vue): window.yawble API only  │  pipe / socket     │ credentials, gateway, launcher, integrations        │
└─────────────────────────────────────────┘                    └───────────────┬────────────────────────────────────┘
                                                                              │ one request per run
                                                         ┌────────────────────▼───────────────────┐
                                                         │ sandbox: agent CLI + yawble-bridge      │
                                                         │ network: only the run's gateway         │
                                                         └─────────────────────────────────────────┘
```

## Repository layout

```
core/
  src/Yawble.Core/          the core: IPC server, bus, scheduler, policy, launcher, gateway, storage
  src/Yawble.CodeBrowser/   read-only browsing of team repositories and branches
  tests/                    xUnit v3
  tools/Yawble.Companion/   yawble: the companion CLI and MCP server
  tools/Yawble.SandboxCheck/  the sandbox release gate
bridge/
  src/Yawble.Bridge/        yawble-bridge: runs inside each sandbox, relays the CLI's MCP to the gateway
  src/Yawble.Run/           yawble-run: the in-sandbox start-up wrapper
  src/Yawble.GrokAuth/      yawble-grok-auth: Grok's auth-provider command inside a sandbox
  src/Yawble.GuardCheck/    yawble-guardcheck: the guardrail check's probe
  src/Yawble.DotnetShim/    dotnet.exe wrapper placed first on the sandbox PATH on Windows
code-browser/               the code browser's renderer pieces
shell/                      Electron + Vue 3 + TypeScript
  src/main/                 starts the core, relays IPC, owns windows
  src/preload/              window.yawble, the renderer's only API
  src/renderer/             the window
  src/shared/               frame codec and API types shared with the core
packaging/                  installers per OS
toolchain/                  fetches wxc-exec.exe for Windows from the pinned sandbox release
sandbox.lock.json           the pinned sandbox release, request contract and telemetry setting
```

## Shell and core

- **The core holds all state.** The shell never opens the database, never opens a network port and never sees a
  secret. It starts the core, relays the renderer's calls, forwards the core's pushes and draws windows.
- **Transport.** A named pipe `\\.\pipe\yawble-<random>` with `CurrentUserOnly` on Windows; a unix socket in a fresh
  0700 folder (socket 0600) on macOS and Linux. No TCP.
- **Frames.** `[u32 big-endian length][u8 kind][payload]`; kind 1 is JSON, kind 2 carries terminal bytes.
- **Auth.** The shell starts the core with a 256-bit token in `YAWBLE_IPC_TOKEN`. The first request on every
  connection must be `auth` with that token, or the core closes the connection.
- **Lifetime.** The core prints `YAWBLE-READY <address>` when it listens and exits when its stdin closes, so it never
  outlives the shell.
- **Renderer.** `contextIsolation` and Chromium's sandbox are on; the renderer reaches the core only through
  `window.yawble` and only for the UI method prefixes the main process allows.

## Storage

- One SQLite database in WAL mode, `<data>/yawble.db`. Migrations are embedded
  `core/src/Yawble.Core/Storage/Migrations/NNNN_name.sql` files, applied in order once each and recorded in
  `schema_migrations`. A shipped migration is never edited; changes are new migrations.
- Transcripts: one owner-only file per run under `<data>/transcripts/`, indexed in SQLite.
- Secrets never go into the database or a log (see [Connections and integrations](connections-and-integrations.md)).
- Backups: a daily automatic copy and on-demand backups in `<data>/backups/`; restore is staged and applied at the
  next start.

The app folder (`%LOCALAPPDATA%\Yawble`, `~/Library/Application Support/Yawble`, `~/.local/share/yawble`) holds
`data/` (database, credentials fallback, logs, transcripts, backups), `teams/` (team repositories, member clones,
homes and temp folders) and `grok-home/`.

## The bus and the scheduler

- **Log.** Messages carry sequence, type, payload, source, correlation, causation and depth. Addressing is by type,
  `agent.instruction.<team>/<member>`. A Manager also receives its team's `agent.completed`, `agent.failed` and
  `agent.handback`.
- **Delivery.** The pump writes each delivery in the same transaction that moves the subscriber's cursor.
- **Runs.** A member with undelivered work joins the global run queue. The scheduler admits the oldest entry whenever
  fewer than `app.maxConcurrentAgents` sandboxes are open. Member runs, Manager runs and interactive terminal sessions
  all share that one limit, first come, first served across teams. One admission takes up to 16 deliveries of the
  oldest workflow as one numbered batch.
- **Tools.** Agents act on the bus through MCP tools (tell, progress, handback, blocked, needs_decision,
  workflow_complete, status and more). Identity and causation come from the run, never from arguments. Members tell
  only their Manager; a chain of tells deeper than the limit is refused.
- **Restart.** A run left open by a stopped core is recorded as interrupted and reported once; it is never re-run.
  Queued entries keep their order.

## The sandbox

Each agent run is one sandbox from Microsoft's MXC SDK (`Microsoft.Mxc.Sdk`): ProcessContainer on Windows,
bubblewrap on Linux, Seatbelt on macOS. The core builds the request itself and submits exact JSON through the SDK's
native library.

- **Policy.** A global policy plus a per-member override. An override only tightens: hosts are intersected, timeouts
  and caps take the minimum, denials are a union, grants must lie inside the grantable roots. Only a separate
  **loosen** block widens, and saving it needs a confirmation that matches the exact list of what it loosens.
- **Floor rules** nobody can loosen: no model-vendor API or sign-in host, no `*.yawble.internal`, no IP literals or
  wildcards, no grant that equals, contains or lies inside an implicit denial or the app folder.
- **Implicit grants and denials.** Read/write: the member's `repo/`, `home/` and `tmp/`. Read-only: the detected CLI's
  install folders, the app's `bin/` and `roles/`. Denied: `data/`, the database, credentials, `grok-home/`, every
  other member and team, the CLI's own config and credential folders, and on macOS `~/.ssh` and `~/Library/Keychains`.
- **Request compiler.** A pure function emits a create-and-run request with explicit containment, an explicit
  environment, egress deny with the run's gateway as the only network proxy, UI off, clipboard none, and telemetry
  off. On Windows it forbids DACL mutation; on macOS keychain access is off.
- **Invariants.** Every compiled request is checked before launch, so a forbidden option can never run. Golden files
  (`core/tests/Yawble.Core.Tests/Golden/`) hold the exact request per OS and CLI; regenerate them with
  `YAWBLE_UPDATE_GOLDEN=1` and review the diff.
- **Agent CLIs from the person's install.** At every launch the core detects each CLI and runs the real program by
  absolute path. Self-update is off inside the sandbox and the install folders are read-only.

### Release gate

`dotnet run --project core/tools/Yawble.SandboxCheck [-- --offline]` fails when the pinned sandbox release is a
pre-release, when `sandbox.lock.json`, `Directory.Packages.props` and the restored SDK disagree, or when the sandbox
no longer accepts a contract-1.0.0 request with telemetry off. Two malformed control requests must be refused, or the
gate fails because the check could no longer tell the difference.

## Gateway, credentials and bridge

No model credential ever enters a sandbox.

- **Per-run gateway.** Each run gets its own gateway on `127.0.0.1:<ephemeral>`, handed to the sandbox as its only
  network proxy, and a random run token (`YAWBLE_RUN_TOKEN`, also every CLI's credential placeholder). The token
  works only for its run and stops when the run ends.
- **Virtual hosts.** `bus.yawble.internal/mcp` serves the run's MCP tools. Model hosts such as
  `model.yawble.internal` forward to the vendor's API: each forward checks the token and a path allowlist, swaps in the
  real credential, passes headers through and streams the response. `CONNECT host:443` is allowed only for the run's
  allowed hosts and never for vendor API or sign-in hosts or private addresses. Denials go to the gateway log.
- **Credentials.** Kept in Windows Credential Manager, the macOS Keychain or the Linux Secret Service; when no Secret
  Service answers, in a 0600 file under `data/credentials/` with a warning. Sign-ins the CLIs make themselves run in
  host terminal panes outside any sandbox, with every other credential withheld from their environment.
- **yawble-bridge** relays the CLI's stdio MCP to the bus through the proxy with the run token as bearer. A transport
  failure becomes a JSON-RPC error, so the CLI never hangs.

## Launcher and supervisor

One run: credential mode, layout, effective policy, the run's gateway, the compiled request, folders and seeded config
files, spawn (headless, or on a PTY for a terminal), supervisor, post-run checks, gateway verdict, run record and
`agent.completed` / `agent.failed`.

- Wall-clock timeout, idle watchdog and Stop kill the whole sandbox, not a process group. A sandbox still alive 5 s
  after the kill is recorded.
- After a run, a CLI credential file that is not the expected placeholder for that run is deleted and recorded as a
  guardrail event.
- Credential failures come from the gateway log, not the exit code, so the card shows the real reason.

## Team repositories and git

- **Team repository.** Each team has a local repository on the host (one the app creates, or a folder the person
  picks, or a clone of a URL). It is never granted to a sandbox. A team can have several repositories, writable or
  read-only.
- **Member clones.** Before a member's first run the host makes `git clone --local --no-hardlinks` at
  `teams/<team>/members/<member>/repo`, checks out `members/<name>`, removes the remote and clears credential helpers.
- **After each run** the host strips remotes, credential, url, http and include sections, object alternates and
  credential files, records what it found, and fetches the member's branch into the team repository. Once an agent
  has used a clone, the host never runs git inside it again: its hooks and config are untrusted.
- **Host git** for clones reads no system or global config, drops inherited `GIT_*` and allows only the `file`
  transport. Pushing to the person's remote uses the person's own git, only through actions the person approves.

## Guardrail check

The guardrail check proves the fence from inside it. The core compiles exactly the request a member's next run would
get, swaps only the command for `yawble-guardcheck`, runs it in a real sandbox with a credential-less gateway, and
judges each attempt on the host: canaries in the app's data, other members and teams, the person's secrets; direct
network and DNS; guessed and missing tokens; hosts off the allowlist; signals and credential stores. A write counts as
allowed only if the file reached the host. Run it from Settings or with
`yawble-core guardrail-check --data-dir <app folder>/data`.

## Companion and Cloud Link

- **Companion** (`yawble`): a local-only control endpoint, a per-launch pipe or socket announced in a user-only
  `control.json`. Programs pair once per name with a level; destructive calls wait for approval in the window. See the
  [README](../README.md#the-yawble-companion).
- **Cloud Link**: the core connects out to the cloud service; no inbound port is opened. Remote viewers get a
  restricted set of methods. See [Cloud Link](cloud-link.md).
