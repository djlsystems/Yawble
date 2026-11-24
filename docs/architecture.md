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
- **One core per data folder.** First the core takes `<data>/core.lock` with FileShare.None (flock `LOCK_EX|LOCK_NB` on
  Linux and macOS) and holds it for its life; its pid goes into `core.lock` and `core.pid`. It then prints
  `YAWBLE-STARTING`, and the shell opens its window only after that line. A second core on the folder prints
  `fatal: another Yawble is using this data folder (pid N)` and exits with code 5, and the shell shows that line in a
  dialog with Quit (`Lifetime/DataFolderLock.cs`, `shell/src/main/lifetime-dialogs.ts`).
- **Removal notice.** Once a minute the core checks whether its executable was deleted (Linux: `/proc/self/exe` ends
  in ` (deleted)`; elsewhere: the path is gone) and whether `core.pid` is still the one it wrote, which fails when the
  data folder was removed or replaced. While either holds it pushes `app.removed`, and the shell shows "Yawble was
  uninstalled or its data was removed" with Quit (`Lifetime/RemovalWatch.cs`).
- **IPC folders (Linux, macOS).** The shell's `yawble-XXXXXX` folder is removed by the core on a clean exit, and
  also on a refused start. At startup the core sweeps the stale ones beside it, which means older than 2 minutes,
  holding only `*.sock` files and nested `yawble-*` folders, with no socket that answers. It deletes only dead sockets
  and empty folders (`Lifetime/RuntimeFolders.cs`).
- **Renderer.** `contextIsolation` and Chromium's sandbox are on; the renderer reaches the core only through
  `window.yawble` and only for the UI method prefixes the main process allows.

## Storage

- One SQLite database in WAL mode, `<data>/yawble.db`. Migrations are embedded
  `core/src/Yawble.Core/Storage/Migrations/NNNN_name.sql` files, applied in order once each and recorded in
  `schema_migrations`. A shipped migration is never edited; changes are new migrations.
- Transcripts: one owner-only file per run under `<data>/transcripts/`, indexed in SQLite.
- Logs: the database logs (`audit_log`, `gateway_events`, `run_records`, `integration_calls`, `integration_asks`,
  `usage_events`, `agent_changes`, `host_task_runs`, `transcripts`, `messages`, `board_trail`) and two log files: the
  diagnostic log (`<data>/logs/core-<day>.log` and `shell-<day>.log`: everything the core and the window process report
  on standard error, with its time) and `<data>/cloud-link-audit.log`. `LogService` (`core/src/Yawble.Core/Logs/`) pages,
  filters, sorts and prunes them all; a member's activity is one query across them. Retention is per log, in
  `app_meta` (`logs.retention.<log>`), applied by a daily sweep, which also forgets transcripts whose file is gone.
- Secrets never go into the database or a log (see [Connections and integrations](connections-and-integrations.md)).
- Host tasks (`core/src/Yawble.Core/Tasks/`): definitions, approvals and `host_task_runs`. How to set up a project's
  release and deploy jobs as host tasks: [Release and deploy host tasks](host-tasks-release.md).
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
- **A run that cannot start.** When the host cannot get a run ready (the member's clone, the team's shared view), the
  agent never starts. The batch goes back to the member's queue and is tried again 20 seconds later, up to 3 tries in
  all. Nobody is told about a try; only the last failure is reported. Reading or fetching branches from a repository
  path on this computer is also tried again, up to 3 times, when only git's transport failed ("Could not read from
  remote repository").
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
- **Floor rules** nobody can loosen: no model-vendor API or sign-in host, no `*.yawble.internal`, no IP literals, no
  wildcard other than one domain allow (below), no grant that equals, contains or lies inside an implicit denial or the
  app folder.
- **Implicit grants and denials.** Read/write: the member's `repo/`, `home/` and `tmp/`. Read-only: the detected CLI's
  install folders, the app's `bin/` and `roles/`. Denied: `data/`, the database, credentials, `grok-home/`, every
  other member and team, the CLI's own config and credential folders, and on macOS `~/.ssh` and `~/Library/Keychains`.
- **Request compiler.** A pure function emits a create-and-run request with explicit containment, an explicit
  environment, egress deny with the run's gateway as the only network proxy, UI off, clipboard none, and telemetry
  off. On Windows it forbids DACL mutation; on macOS keychain access is off, and the only extra Mach services a run
  may reach are `com.apple.SecurityServer` and `com.apple.trustd.agent` (TLS certificate checks) and
  `com.apple.system.opendirectoryd.libinfo` (user and group lookups). SecurityServer serves only keychains whose files
  the run can read, and `~/Library/Keychains` is always denied (the invariants require it), so no login-keychain item
  reaches a run; the data-protection keychain service stays closed.
- **macOS run temp folder.** Each macOS run gets its own folder, `<DARWIN_USER_TEMP_DIR>yr-<8 hex>` (the hex is from
  the SHA-256 of the run id). It is granted read-write to that run only, which also lets a Unix socket bind in it.
  `TMPDIR` points at it and `TMPPREFIX` is `<folder>/zsh`; `CLAUDE_CODE_TMPDIR` stays the member's `tmp/`. The point is
  length: a Unix socket path holds 104 bytes, and .NET puts a named pipe at `$TMPDIR/CoreFxPipe_<name>`. The member's
  `tmp/` is too long for any pipe, while this folder leaves room for a 24-character name (MSBuild's `MSBuild<pid>`).
  The launcher makes the folder before the run and removes it when the run ends, however it ends. Beside each folder
  (not inside it, where the run can write) is an owner-only marker, `yr-<8 hex>.owner`, that names the core that made
  it: a hash of its data folder, its pid and its process start time (a pid alone can be reused). It is removed with the
  folder. At start the core sweeps that temp folder, which covers a crash or a restart. Two app instances of one Mac
  user (a dev build next to the installed app) share it, so the sweep removes a `yr-*` folder only when it is this
  core's own and its run is not live, when its owner's process is gone (pid not running, or running with another start
  time), or when it has no readable marker and is more than 24 hours old. It keeps another live core's folders, removes
  a marker whose folder is gone, and never follows a link. The dotnet wrapper and the MSBuild variables are unchanged.
- **Allow localhost (macOS).** The request input has `AllowLocalhost`, set from the team's setting. The setting is off
  by default and set per team; turning it on or off is in the audit log, and a Cloud Link Visitor cannot set it (the
  core refuses the change).
  - When it is off, the request is the same as without the setting: `network.ingress` `default` is deny and there is
    no `hostLoopback` allow.
  - When it is on, a macOS request has `network.ingress` `default` and `hostLoopback` both `allow`. The SDK only
    accepts `hostLoopback` allow together with the ingress default allow: Seatbelt refuses `hostLoopback` allow while
    the ingress default is deny. The Seatbelt profile then allows outbound to `localhost:*`, and inbound and bind on
    any local address, not only loopback.
  - So while it is on, a server an agent starts on `0.0.0.0` can be reached from other machines on the network unless
    the macOS firewall blocks it. The person keeps the macOS firewall on; the setting's warning says so.
  - Egress stays deny with no rules, and the gateway is still the only proxy: outbound traffic goes only through the
    gateway.
  - Windows and Linux ignore it, because their sandboxes give each run its own loopback.
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
- **Allow matching.** An allowHosts entry is an exact host name or a domain allow, `*.<domain>`. An exact entry matches
  that name only (case-insensitive, compared as sent, so a trailing dot is another name). `*.digicert.com` matches every
  subdomain at any depth (`crl3.digicert.com`, `a.b.digicert.com`) and never the apex `digicert.com`, which is listed
  separately when wanted; a lookalike such as `evil-digicert.com` never matches, because the name must end in
  `.digicert.com`. The vendor floor wins over any entry (`Vhosts.HostAllowed`).
- **Domain allows and the Public Suffix List.** `FloorRules.HostProblem` accepts exactly one leading `*.` over a
  registrable domain (eTLD+1) and refuses everything else: bare `*`, `**.x`, `a.*.b`, a public suffix (`*.com`,
  `*.co.uk`, `*.github.io`), a name below the registrable domain (`*.sub.example.com`), any domain whose suffix is in
  the list's PRIVATE section (`*.pat.github.io`, `*.app.herokuapp.com`: anyone can own a sibling, so a wildcard there
  reaches hosts someone else controls), any domain with public suffixes under it (`*.amazonaws.com`, `*.windows.net`),
  a vendor or gateway domain, and the Codex and Copilot presets' own vendor domains (`FloorRules.PresetOnlyDomains`).
  The domain is held to the exact-host rules too, in plain ASCII letters, digits and hyphens with no `xn--` label.
  The same check runs for the member loosen block, the team allow list (`PolicyStore.AddTeamHost`, which also applies
  the ask filter to the domain) and the global allowed hosts; none of them adds the apex.
  `PublicSuffix.RegistrableDomain(host)` gives the eTLD+1 from both sections of the list (null for a suffix, a bare TLD,
  an IP, an IDN or a malformed name), and `PublicSuffix.DomainPatternFor(host)` gives the `*.<eTLD+1>` that may be
  offered, or null when only the exact host may be. The list is a pinned copy embedded in the core
  (`core/src/Yawble.Core/Policy/public_suffix_list.dat`, VERSION 2026-11-13_14-44-35_UTC, publicsuffix/list commit
  71c235dd3f88, SHA-256 8576b335b871dea99dc0fccd04138311bd43f7a8da256c31bf964f11e2f7c179) and is never fetched at run
  time; a newer copy is a reviewed change to that file and to the hash in `PublicSuffixTests`.
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
  `teams/<team>/members/<member>/repo`, checks out `members/<team>/<name>`, removes the remote and clears credential helpers.
- **After each run** the host strips remotes, credential, url, http and include sections, object alternates and
  credential files, records what it found, and fetches the member's branch into the team repository. Once an agent
  has used a clone, the host never runs git inside it again: its hooks and config are untrusted.
- **Host git** for clones reads no system or global config, drops inherited `GIT_*` and allows only the `file`
  transport. Pushing to the person's remote uses the person's own git, only through actions the person approves.
- **Which git.** The core runs the git on PATH, 2.38 or later (merges use `git merge-tree --write-tree`). At start,
  and at each check, a git found only in the person's or the machine's current PATH or in Git for Windows' own
  folders has its folder put first on the core's PATH, so a git installed while the app runs is used at once, by the
  host and on the next run's PATH. `git.check` reports it; `git.install` (the window only, never the Cloud Link)
  runs `winget install --id Git.Git --scope user`, or `winget upgrade` for an old one.
- **Hosts.** Nothing in git handling depends on where origin is hosted. Pull requests are the one host-specific
  feature: read, opened and merged for an origin on github.com only, through the person's `gh`.

## Guardrail check

The guardrail check proves the fence from inside it. The core compiles exactly the request a member's next run would
get, swaps only the command for `yawble-guardcheck`, runs it in a real sandbox with a credential-less gateway, and
judges each attempt on the host: canaries in the app's data, other members and teams, the person's secrets; direct
network and DNS; guessed and missing tokens; hosts off the allowlist; signals and credential stores. A write counts as
allowed only if the file reached the host. Run it from Settings or with
`yawble-core guardrail-check --data-dir <app folder>/data`.

## Companion and Cloud Link

- **Companion** (`yawble-companion`): a local-only control endpoint, a per-launch pipe or socket announced in a user-only
  `control.json`. Programs pair once per name with a level; destructive calls wait for approval in the window. An
  approval request is held in memory by `CompanionService` until the person decides it, the client cancels or replaces
  it, 7 days pass, or the app restarts. It keeps the key its plan was worked out from, never a confirmation token: on
  approval the plan is worked out again and runs only under the same key, with a token issued then. See the
  [README](../README.md#the-yawble-companion).
- **Cloud Link**: the core connects out to the cloud service; no inbound port is opened. Remote viewers get a
  restricted set of methods. See [Cloud Link](cloud-link.md).
