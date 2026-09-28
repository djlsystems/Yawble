# Plugin members

A **plugin member** is a team member backed by an installed executable instead of a coding-agent
CLI. It is hired, woken, queued, stopped and shown exactly like an agent member. The same member
runtime hosts both; only the runner behind it differs.

```
Team
 └─ MemberRuntime            (per member: subscription, queue, admission, marks, rows, Stop)
     └─ IMemberRunner         (MemberRunnerRouter picks per invocation from team_members.agent)
         ├─ AgentMemberRunner   "claude-headless", "codex-headless", ...  → agent CLI
         └─ PluginMemberRunner  "plugin:<id>"                             → plugin executable
              both launch through ChildProcess: setsid, the agent user, process-group kill
```

What a plugin member is **not**:

- It is not an agent. It has no system prompt and runs no model. It holds no platform credential,
  so it has no `HARNESS_KEY` and cannot call MCP tools.
- It is not a container. It runs as a child process of the Host, as the same non-root `agent` user
  agents use. There is no Docker or Podman sibling container, and root is never needed.
- It is not registered in code. A plugin is a directory with a manifest, read at start and on
  rescan. Nothing is compiled in.

Status: manifest v1 and protocol `harness.member/1`, with plugin skills, published events and
hiring by a Manager (see [Skills](#skills), [Events](#events) and [Hiring](#hiring)). The
marketplace, downloading, signing and updating are out of scope.

## Install a plugin

There are two install paths. Both check the same rules, lay the plugin out the same way, set the
same owner and modes, switch `active` to it and print the Host's verdict:

- **From your computer.** `yawble plugin install <folder>` copies a version you built on the machine
  that runs the instance into it (below).
- **From inside the instance.** A plugin a team built in its worktree, or the Concierge in its
  workspace, is already in the container. `yawble plugin install --from-instance <path>` installs it
  where it is, with no copy out of the container and back
  ([Install from a folder inside the instance](#install-from-a-folder-inside-the-instance)).

Install a built plugin version from your computer with the operator CLI:

```sh
yawble plugin install <folder>
```

`<folder>` is one built version of a plugin: it holds `plugin.json` and everything the manifest names.
That is the whole install. There is no copy, `chown`, `chmod` or rescan to do by hand, and no restart.
The command:

1. **Checks the manifest before anything is copied.** It uses the Host's rules (below) and names the
   field that is wrong. It also checks that the executable and every skill are files inside the
   folder after symlinks are resolved. It does not require an execute bit, because step 3 sets one.
2. **Copies the folder into the running instance** at `/data/plugins/<id>/<version>/`, through the
   instance's engine (Podman or Docker, `podman cp` / `docker cp`). It creates `/data/plugins` if it
   is missing. An existing `<id>/<version>` is refused unless you pass `--force`.
3. **Sets ownership and modes, whatever they were on your computer.** Everything becomes
   `harness:agent`, with directories `0750`, files `0640`, and the manifest's executable (and every
   `platforms` entry) `0750`. A folder copied from Windows has no execute bit, and without this step
   the Host refuses the plugin with "chmod +x it".
4. **Writes `active`** naming that version, so installing a new version switches to it. Earlier version
   directories are kept, for going back: `yawble plugin install <older folder> --force`.
5. **Has the Host rescan, and prints the Host's verdict**: installed, or refused with its reason (exit 1).

`yawble plugin list` shows every version on the volume, whether it is active, and what the Host made
of it. `yawble plugin remove <id> [--version <v>]` removes a plugin or one kept version. It asks first
(`--yes` answers), and it refuses while a member is hired on the plugin, naming the members.

**How the Host is told, without a person's key.** The CLI writes a nonce to `/data/plugins/.rescan`
through the engine, as root in the container. The Host (`PluginRescanRequests`) looks for that file
once a second. When it finds one, it calls the same `PluginCatalog.Rescan` that `POST /api/plugins/rescan`
calls. It then writes what it now holds, with that nonce, to `/data/plugins/.rescan-report.json`: installed
plugins, refused directories with the reason, and the members hired on each plugin. The CLI reads that
report back. Only the Host user and root can write in `/data/plugins` (`harness:agent 0750`), so an
agent cannot ask for a rescan. The report names members of every team, so the Host writes it `0600`.
An image from before this command does not answer. The CLI says so after 20 seconds, and a restart
(`yawble down`, `yawble up`) loads the files it has already put in place.

### Install from a folder inside the instance

A plugin built inside the instance - by a team in its worktree, or by the Concierge in its workspace -
is installed where it is, with no copy out to the operator's computer. Both ways are a person's:

- **The web app:** Admin → Plugins → "Install from a folder…" picks the built version folder and calls
  `POST /api/plugins/install` with `{ "path": "<absolute folder>", "replace": false }` (humans-only).
- **The operator CLI:** `yawble plugin install --from-instance <path>` asks the running Host through a
  request file, the same seam as the rescan: it writes `{"request": "<nonce>", "path": ..., "replace": ...}`
  to `/data/plugins/.install`, and the Host answers in `/data/plugins/.install-report.json`
  (`0600`) with that nonce, `status` (200, 400 or 409), `id`, `version`, `installed`, `replaced` and
  `reason`. `--force` is `replace`.

One implementation serves both (`PluginInstaller`), so they check, write and answer alike:

1. **Refused before anything is written**, each with a sentence naming why:
   - a path that is not absolute, not a folder, or outside the data root (`/data`);
   - a path through a symlink that leads out of the data root;
   - a symlink inside the folder that leads out of it, or that is absolute (a copy of it would still
     point at the source, which the agent that built it can change);
   - a folder already under `/data/plugins`;
   - a manifest the catalog would refuse, by the catalog's own check: its rules, the executable and
     every skill inside the folder after symlinks, and every runtime it `requires`;
   - an id and version already installed, unless replacing was asked for (409).
2. **The layout, ownership and modes** of the CLI's install: `/data/plugins/<id>/<version>/`, owned by
   the Host user with the `agent` group, directories `0750`, files `0640`, the manifest's executable and
   every `platforms` entry `0750`. The copy is staged beside the target and moved into place.
3. **`active`** names the new version, and the catalog is rescanned.
4. **The Host's verdict** is the answer: installed, or refused with its reason.
5. **A `plugins.installed` row** is appended to `tenant_events`: who, the id and version, the source
   folder, whether it replaced one, and the verdict (`by: "operator"` for the CLI).

An agent still cannot install: the route is humans-only, and only root and the Host user can write the
request file.

```sh
yawble plugin install --from-instance /data/teams/<team>/repos/<repo>/<tree>/build/<id>/<version> [--force]
```

The CLI prints the Host's verdict and exits 1 on a refusal (400), an installed version without
`--force` (409), or `installed: false`. If the Host does not answer within 60 seconds (an image from
before this command), it says so and withdraws the request, so a later Host never carries it out.

The layout the Host reads, whatever put it there:

```
plugins/<id>/<version>/plugin.json   the manifest
plugins/<id>/<version>/...           its executable, skills, anything it ships
plugins/<id>/active                  one line: the version in use
```

- **Identity is the manifest's `id`, not the path.** The directory must match `id`, and the version
  directory must match `version`.
- **`active` picks the version.** Without an `active` file, a plugin with exactly one version
  directory uses that version.
- **Where the executable may live.** It must resolve inside its version directory after symlinks
  are resolved, and it must be executable.
- **Ownership.** Plugins are owned by the Host user and readable and executable by the `agent` group,
  never writable by `agent`. A member must not be able to replace a plugin's files. The container's
  entrypoint (`scripts/prepare-volume.sh`) creates `/data/plugins` as `harness:agent 0750` and
  re-applies these modes at every start, keeping each file's execute bits.

The Host prints one line per installed plugin at start and at every rescan. It also prints one line
per refused directory, naming the field that is wrong. `GET /api/plugins` lists the same thing. A
person can still rescan with `POST /api/plugins/rescan`. A member already pointing at the plugin runs
the new version on its next wake.

### Example: sample-echo

`samples/plugins/sample-echo` is the proof-of-concept plugin. It is deterministic: it upper-cases or
reverses each instruction's text. It is a .NET console app with no dependencies, and its launcher
runs it with `dotnet`, which the image guarantees.

Run the build **from the repository root**. The sample's project takes its target framework from
the repository's build properties, so a copy of `samples/` built anywhere else fails with
`NETSDK1013`. The build writes to a folder **outside** the repository, so it leaves nothing untracked.

```sh
# From the repository root: build version 0.1.0 outside the repository, then install it.
B=~/plugins-build/sample-echo/0.1.0
dotnet publish samples/plugins/sample-echo/src -c Release -o "$B/lib"
cp samples/plugins/sample-echo/plugin.json samples/plugins/sample-echo/sample-echo "$B/"
cp -r samples/plugins/sample-echo/skills "$B/"
yawble plugin install "$B"
```

On Windows, in PowerShell:

```powershell
# From the repository root.
$B = "$HOME\plugins-build\sample-echo\0.1.0"
dotnet publish samples/plugins/sample-echo/src -c Release -o "$B\lib"
Copy-Item samples/plugins/sample-echo/plugin.json, samples/plugins/sample-echo/sample-echo $B
Copy-Item -Recurse samples/plugins/sample-echo/skills $B
yawble plugin install $B
```

It prints the Host's verdict:

```
copied sample-echo 0.1.0 to /data/plugins/sample-echo/0.1.0 and made it the active version
the Host reports sample-echo 0.1.0 installed; hire it as plugin:sample-echo
```

What has been verified, and what has not:

- **Verified here, on Linux.** The build above, with the launcher's execute bit removed as a Windows
  copy leaves it, then the real `yawble` binary. The `podman` it ran was a stand-in script that ran
  each `exec` locally, mapped `/data` to a temporary data root, and turned `cp` into a local copy. The
  Host was a real one, started on that data root with no `plugins` directory. `plugin install`
  created `plugins/`, set the modes above (the launcher `0750`), wrote `active`, and printed "the Host
  reports sample-echo 0.1.0 installed" about a second later, with no restart and no key. A repeat was
  refused without `--force`, and `--force` replaced it. A second version became active with the first
  kept. `plugin list` and each `plugin remove` refusal behaved as described, and a broken manifest
  was refused by field before anything was copied.
- **Not verified.** A real Podman or Docker, a real container, the image's `harness` and `agent` users
  (`chown` was a stub), and a build made on Windows. In particular, not yet run: what
  `podman cp`/`docker cp` do with a folder from a Windows host, and a hired sample-echo running after
  this install on a real instance.

### Example: sample-echo-go

`samples/plugins/sample-echo-go` is the same plugin in Go, and the template for a connector (see
[Choosing a language](#choosing-a-language)). `build.sh` builds two static binaries
(`CGO_ENABLED=0`, `GOOS=linux`, `GOARCH=amd64` and `arm64`) into `bin/linux-x64/` and
`bin/linux-arm64/`, which the manifest's `platforms` map names; the Host runs the one for its
processor. It needs no runtime from the image, so its manifest has no `requires`.

```sh
# From the repository root, on your computer or inside the instance.
samples/plugins/sample-echo-go/build.sh ~/plugins-build/sample-echo-go/0.1.0
yawble plugin install ~/plugins-build/sample-echo-go/0.1.0

# Built inside the instance, in a team's tree: install it where it is.
yawble plugin install --from-instance /data/teams/<team>/repos/<repo>/<tree>/build/sample-echo-go/0.1.0
```

`PluginGoTemplateEndToEndTests` builds both binaries, checks each is a static ELF for its processor,
installs the folder, hires it and runs it on the Host's processor.

### Hire it into a team

Hire it through the ordinary member route, as a person, naming `plugin:<id>` as the member's Agent:

```http
POST /api/teams/{team}/containers
Content-Type: application/json

{
  "name": "Echo",
  "agent": "plugin:sample-echo",
  "config":  { "mode": "reverse" },
  "secrets": { "token": "SAMPLE_ECHO_TOKEN" }
}
```

- **`config`** is checked against the manifest. Refused at hire, with the field named: an unknown
  field, a value of the wrong type or outside its `enum`, or a missing `required` field with no
  default.
- **`secrets`** binds each secret the manifest names to a **logical key**, never to a value (see
  [Configuration and secrets](#configuration-and-secrets)). A `required` secret whose key is not set
  is refused at hire.
- **What you get.** The member appears on the team's board with `kind: "plugin"`. Work it is told
  through `tell`, the board, a trigger or a schedule reaches it through the same queue any member
  has. Its result is its `agentContainer.completed` row's `output`, and that row wakes the Manager
  like any member's.
- **What is refused.**
  - `config` and `secrets` on an Agent member.
  - A plugin that is not installed.
  - Repointing a member between an Agent and a plugin. Hire a new member instead: an agent member's
    credential and environment must not survive into a plugin member.

### In the web UI

- **Add member.** The dialog offers each installed plugin (from `GET /api/plugins`) after the Agent
  presets. Choosing one shows its manifest's `config` fields as inputs, prefilled with their
  defaults, and each of its `secrets` by name: what a person types there is the logical key, never a
  value. The hire goes through the route above. An empty optional field or an unnamed optional
  secret is left out of the request.
- **Member card.** A `kind: plugin` card shows no live view, no tokens and no spend. It names the
  plugin instead of an Agent and words its failures without provider or spend terms. Its history
  button lists its earlier runs, and each run opens to what it reported.
- **Earlier runs.** `GET /api/teams/{team}/members/{member}/runs` lists every finished run of a plugin
  member, one per run (not one per batched message), newest first. Each run carries `output`: its
  result output, or a failure's launch error. The plugin's own failure sentence is on the card's
  failed mark, not on the run's row. There is no transcript, so `runs/{seq}/transcript` answers 404.
  A run that blocked every item it was given writes no completed or failed row. It is listed from its
  last `blocked` row with `outcome: blocked`, `output` null and `reason` set to that row's reason, and
  the card's history opens it onto `Blocked: <reason>`. While the member is running, an item its
  current run has already blocked is not listed: the run is not over, and it is listed once it is
  (`An_item_blocked_by_a_run_still_working_is_not_listed_as_a_finished_run`). An agent member's
  listing is unchanged: such an agent run recorded no transcript, so there is nothing to open.
- **Team KPI card.** On a team whose every member is a plugin, the Tokens tile reads `none` with no
  advice about usage capture, and the workflow tile draws no budget line or bar.

## Manifest v1 (`plugin.json`)

```json
{
  "schemaVersion": 1,
  "id": "sample-echo",
  "name": "Sample Echo",
  "description": "Deterministic test plugin: transforms each instruction's text.",
  "version": "0.1.0",
  "protocol": "harness.member/1",
  "executable": { "path": "sample-echo", "args": [] },
  "timeoutSeconds": 60,
  "config":  { "mode": { "type": "string", "enum": ["upper", "reverse"], "default": "upper" } },
  "secrets": { "token": { "description": "A demo credential.", "required": false } },
  "events":  { "publishes": [ { "type": "done", "summary": "Text was transformed.", "highVolume": false,
                                "fields": [ { "name": "length", "kind": "number", "summary": "Output length." } ] } ] },
  "skills":  ["skills/sample-echo.md"]
}
```

| Field | Status | Rule |
|---|---|---|
| `schemaVersion` | required | Must be `1`. A later number is refused by name rather than misread. |
| `id` | required | `^[a-z0-9][a-z0-9-]{0,47}$`. Stable forever. Members name it as `plugin:<id>`. |
| `name`, `description` | required | Shown in `GET /api/plugins`. |
| `version` | required | A plain version (`0.1.0`). Must equal its directory name. |
| `protocol` | required | `harness.member/1`, the only protocol this Host speaks. |
| `executable.path`, `executable.args` | required / optional | A relative path inside the version directory. `args` is a fixed argv with no substitution. |
| `timeoutSeconds` | optional | An **idle** clock: this long with no `progress` record ends the run. Default 300. |
| `config` | optional | Name → `{type: string, number, bool or list; enum; default; required; setBy; description}`. Flat in v1. A `list` is a list of strings, default `[]`. See [List settings](#list-settings). |
| `secrets` | optional | Name → `{description, required}`. A `value` key is refused. |
| `events.publishes` | optional | `type` is a suffix: lowercase letters, digits, `-` and `.`. The full type is `plugin.<id>.<type>`. `highVolume` (default false) and `fields` (`name`, `kind`: `string`, `number`, `boolean` or `list`, `summary`) are optional; `source` cannot be declared. `inLedger` is not read in v1: a plugin event always reaches the ledger. See [Events](#events). |
| `skills` | optional | Files inside the plugin, in the same front-matter format as a skill. Indexed at load and on every rescan. See [Skills](#skills). |
| `requires` | optional | The runtimes the plugin needs from the image: `dotnet`, `node`, `python3`. See [Runtimes](#runtimes-requires). |
| `platforms` | reserved, validated | `{"linux-x64": "bin/x64/p", "linux-arm64": "bin/arm64/p"}`. When present it takes precedence over `executable.path` for this machine. |
| `actions`, `consumes`, `health`, `signature`, `publisher`, `minHostVersion`, `permits` | reserved | Kept, and not acted on. |

Any other key is ignored and named on the start-up line. This lets a manifest written for a later
Host still load.

### List settings

`"type": "list"` holds a list of strings: an allowlist, a set of scopes. Its default is `[]` when the
manifest gives none, so the plugin always receives an array in the request's `config`. An optional
`enum` limits each item, and a value holding anything else is refused naming the item:

```json
"config": {
  "sendAllowlist": { "type": "list", "setBy": "person", "description": "Addresses it may send to." },
  "scopes":        { "type": "list", "enum": ["read", "send"], "default": ["read"] }
}
```

Every other type keeps its rules. A Host from before list settings refuses a manifest that uses one,
naming the field, as it refuses any unknown type. The web's settings editor shows a list as chips.

### Runtimes: `requires`

A plugin is self-contained: beyond what the image guarantees, everything it needs lives in its own
folder. `requires` names what it needs from the image, from exactly these: `dotnet`, `node`,
`python3`. A self-contained binary (Go) needs none and omits it.

```json
"requires": ["dotnet"]
```

The catalog looks each one up on the Host's `PATH` when it loads and at every rescan, and refuses a
plugin whose runtime is missing, naming it ("it needs python3, which is not installed on this
instance"), rather than letting its first run fail. A name outside the list is refused naming the
name. The install checks the same before it writes.

## Protocol `harness.member/1`

**Launch.**
- **Process.** The executable runs through `setsid` as the `agent` user (the same launch agents
  use), in the member's workspace, with the manifest's `args`.
- **Environment.** Only an allowlist is inherited: `PATH`, `HOME`, `LANG`, `LC_ALL`, `TZ`, `TMPDIR`,
  `DOTNET_ROOT`, `DOTNET_CLI_HOME`, plus `HARNESS_CAUSATION`. No provider key, no `HARNESS_KEY`, no
  agent-vendor variable.
- **End of run.** When the run ends, its whole process group is killed.

**stdin**: one JSON document, then EOF.

```json
{
  "protocol": "harness.member/1",
  "plugin": { "id": "sample-echo", "version": "0.1.0" },
  "member": { "id": "Mixed/Echo", "team": "Mixed", "name": "Echo" },
  "causation": 42,
  "correlation": 40,
  "work": [
    { "seq": 42, "type": "agentContainer.instruction.Mixed/Echo", "source": "Mixed/Manager",
      "correlation": 40, "payload": { "instruction": "hello" }, "instruction": "hello" }
  ],
  "config": { "mode": "reverse" },
  "secrets": { "token": "…resolved now…" },
  "workingDirectory": "/data/teams/Mixed/workspaces/Echo",
  "worktrees": []
}
```

- **`work`** is the batch, oldest first, all from one workflow. Up to 16 messages can arrive in one
  run.
- **`config`** is the manifest's defaults with this member's own values on top.
- **`secrets`** holds only the secrets this member binds, resolved at the moment the run starts.

**stdout**: JSON Lines, one record per line.

| Record | Effect |
|---|---|
| `{"t":"progress","status":"…"}` | A progress row on the card's feed. It resets the idle clock. |
| `{"t":"blocked","reason":"…"}` | Marks the member blocked. With `"item": n` (1-based) only that batch item is blocked. |
| `{"t":"needsDecision","question":"…"}` | Marks the member as waiting for a decision. |
| `{"t":"handback","delivered":"…"}` | Hands the work back and wakes the Manager once. |
| `{"t":"result","ok":true,"output":"…"}` | The run's result. Send exactly one, last. `ok:false` with `"error"` is a failure in those words. Add `"quiet":true` to finish without waking anyone; see [Quiet runs](#quiet-runs). |
| `{"t":"publish","type":"…","payload":{…}}` | Publishes one of the events the manifest declares. See [Events](#events). |

- **Same path as agents.** The first four records go through the same code as an agent's MCP tools
  (`MemberReports`). A plugin's report and an agent's report are therefore the same row, the same
  mark and the same wake.
- **Other lines.** A line that is not a record is kept as output text and is never treated as an
  error.
- **stderr.** It is appended to the output.

### Quiet runs

`{"t":"result","ok":true,"output":"…","quiet":true}` says the run found nothing
anyone needs to be woken for. It is for a plugin that polls on a schedule: without it, every run's
`completed` row wakes the Manager, a paid model run, to learn that nothing happened.

- **The run is still recorded.** Its `agentContainer.completed` row is written as usual, marked
  `"quiet": true`, and the card, the run history and the feed show it.
- **Nobody is woken by that row.** The pump passes over every subscriber on it, the same way it
  passes over a Manager on a `completed` row marked `handedBack`. That includes a person's own
  trigger on the plugin's `completed` rows: a trigger that must see every run should watch the
  plugin's event instead.
- **Never quiet towards a member that is waiting.** Quiet is for work nobody waits on: a schedule's
  poll, a trigger, a person's tell. When the instruction came from another member (a Manager's
  `tell`), the row is written without the quiet mark and wakes as any completion does, so a Manager
  that asked `list is:unread` always gets its answer, even an empty one, and its workflow ends.
- **What quiet does not suppress:**
  - A failure: `ok:false`, a non-zero exit, a timeout or a Stop. A failure is never quiet, whatever
    the record says, and wakes as always.
  - An event the run published. Its triggers fire as usual, so "publish one event per new item,
    finish quiet" is the polling pattern.
  - A hand-back in the run. It wakes the Manager on its own row.
- **The workflow still ends.** A quiet run in a workflow the plugin owns ends that workflow the same
  way a non-quiet one does.
- **Optional.** A plugin that never sends `quiet` behaves exactly as before.

Pinned by `PluginQuietRunTests` and
`PluginHostProbes.A_quiet_answer_to_the_managers_own_instruction_still_wakes_the_manager`.

### Poll with plugins, spend models only when something happened

A plugin watches for free and publishes an event when it finds something; an agent reacts to that
event. A model member on a short schedule is almost always better as a plugin plus an event trigger.

- A **schedule trigger** on the plugin member polls every few minutes. The plugin publishes one
  event per new item and finishes; a run with nothing new costs no tokens.
- An **event trigger** on the agent member, on the plugin's event type, wakes it only when there is
  something to act on.

The trigger's own settings bound what is left (see [triggers.md](triggers.md)):

- **Wake the Manager when a run ends.** A new trigger wakes the Manager only when its run hands back
  or fails, so a plugin poll that finds nothing wakes nobody, `quiet` or not. Under **Always** (every
  trigger made before the setting), `quiet` is still how a plugin says "nothing happened". Under
  **Never**, not even a failure wakes the Manager; it still shows on the card and in the feed.
- **Daily token cap.** The billable tokens a trigger's runs, and the Manager runs they woke, may
  spend in a day; past it, fires are skipped until the next day. A plugin run reports no usage, so
  it counts as not measured and never towards the cap - the cap matters on the agent's event trigger.

The Triggers dialog asks for confirmation before a schedule more frequent than every 5 minutes on an
agent member, naming what a run of it has cost and suggesting this shape. A plugin member has no
minimum.

**Outcome.**

| What happened | Result |
|---|---|
| Exit 0 and an `ok` result | Success. |
| No result | Failure: "The plugin exited N without a result record." |
| `ok:false` | Failure, in the plugin's own words. |
| Exit ≠ 0 | Failure: "The plugin exited N." |
| No progress for `timeoutSeconds` | Failure with class `timeout`. |
| A person's Stop | Class `interrupted`, marked `stoppedByPerson`. |
| The Host shutting down | Class `interrupted`. |

Plugin runs report no token usage, so the spend bound never counts them. A plugin run takes a WIP
slot like any member's.

## Configuration and secrets

- **Ordinary configuration.** Stored per member in `team_member_config` (schema step `auth-008`).
  It is checked against the manifest at hire and delivered in the request's `config`. It is never
  put in the environment.
- **Secrets are logical keys.** A member stores `{"token": "ACME_STORAGE_KEY"}`, the key only. The
  Host never writes the value to the database, the manifest or the plugin directory. Cloning a team
  carries the member's configuration and its keys, never a value.
- **Setting a secret.** The value is resolved at each run from the Host's own environment, which is
  where `yawble secret set NAME` values already arrive: set it, then restart the Host.
  - Keys starting `HARNESS_` and model-provider keys are refused.
  - The value reaches the plugin only on stdin, never in argv or the environment.
  - **Redaction is a net, not a guarantee.** Every text the plugin writes - its result output and
    error, its stderr, the text of each `progress`, `blocked`, `needsDecision` and `handback`
    record, and every string in a published event's payload - has each bound value replaced with
    `[redacted]` before it is stored or reported, and then the platform's diagnostic redaction runs
    over it (named values such as `password=...`, and credential-shaped strings). The rules, each
    pinned by a test in `PluginMemberRunnerTests`:
    - **Longest value first**, so a value that is a prefix of another cannot leave the other's tail
      behind (`Overlapping_secrets_are_redacted_longest_first`).
    - **JSON-escaped forms too.** A line that is not a record is kept as the plugin wrote it, so a
      value serialised with JSON escapes (the default encoder writes `+` as `\u002B`) is also
      matched in its escaped forms (`A_secret_json_escaped_in_a_raw_line_is_redacted`), including
      `/` written as `\/`, as PHP and some Java encoders do
      (`A_secret_with_an_escaped_solidus_in_a_raw_line_is_redacted`).
    - **In any case.** `DEMO-TOKEN-2026` is caught for the bound value `demo-token-2026`
      (`A_secret_is_redacted_whatever_its_case`).
    - **In every record** (`A_bound_secret_in_a_report_record_is_redacted_before_it_is_reported`)
      and in a published payload (`A_declared_publish_is_appended_and_an_undeclared_one_warned_once`).
    - **A payload's keys and numbers refuse the publish.** A published payload whose property name,
      at any depth, would be changed by redaction, or whose number (or `true`/`false`/`null`) has
      JSON text that would be, is **dropped**, not rewritten, with one `progress` warning row that
      names neither. Renaming a key or turning a number into `[redacted]` would silently change the
      shape a trigger or `{event.*}` token reads (`A_secret_as_a_publish_key_or_number_refuses_the_publish`,
      `A_number_matching_a_secret_refuses_the_publish`). For a secret that is itself a number, a
      payload number is also compared by VALUE, so `2.0261234e7` for `20261234` is refused the same
      way (`A_number_equal_in_value_to_a_secret_refuses_the_publish`).

    A plugin that writes a secret otherwise TRANSFORMED - reversed, base64, cut in pieces - defeats
    all of it, and the result reaches the log. Do not write secrets out at all.
  - A bound value shorter than 4 characters cannot be redacted without mangling the text around
    it, so it is refused: at hire if it is already set, and on every run
    (`A_secret_too_short_to_redact_is_refused_before_launch`).
- **Worked example.** An Azure Storage plugin would declare `config.account`, `config.container`
  and `secrets.accountKey`. Its member would store
  `{"config": {"account": "acme", "container": "invoices"}, "secrets": {"accountKey": "ACME_STORAGE_KEY"}}`.
  The operator runs `yawble secret set ACME_STORAGE_KEY`.
- **Swapping the store.** Only the resolver (`ISecretStore`) knows where values live. An encrypted
  store can replace it later, and no plugin or binding changes.

### Changing a plugin member's settings after hire

A person changes a plugin member's configuration and secret bindings without hiring a new member:
**Member settings → Settings**, the same form Add member shows, filled with what is stored. It is
generated from the manifest: one typed input per `config` field (text, number box, toggle, dropdown
for an `enum`, chips for a `list`), each with its description, default, whether it is required, and
"Set by a person only" for a `setBy: person` field; "Reset to default" per field; secrets as key-name
inputs; and a read-only view of the JSON that will be stored. It saves only the fields that differ from
their default.

- `GET /api/teams/{team}/members/{member}/plugin-settings` answers `plugin`, `version`, the stored
  `config`, `secrets` as logical keys (never a value), and the manifest's `fields` and `secretFields`.
- `PUT` the same route with `{ "config": {...}, "secrets": {...} }` replaces both. It is validated
  exactly as a hire is - field and secret names, types, `enum` (per item for a list), required fields,
  the key's form, a reserved or provider key, a required secret not set on this Host, a value too short
  to redact - and refused with a 400 naming the field, with nothing written.
- The save and its `member.plugin-settings-changed` row in `tenant_events` (the plugin and the NAMES of
  the fields and secrets that changed, never a value) are one transaction.
- It takes effect on the member's next run: the runner reads the settings at every run.
- Both routes are humans-only. A Manager cannot change a member's settings after hire, as it cannot
  change a member's instructions.

## Skills

A plugin's `skills` files tell a Manager how to use it. They are indexed whenever the plugins are
loaded or rescanned, however the rescan is started (`PluginCatalog.Attach`).

- **Kind `plugin`, locked.** Stored with the plugin id as `source` (schema step `skill-003`). A
  person cannot edit, rename or delete one (403), and a custom skill may not take a name beginning
  `plugin-` (409). Pinned by `PluginSkillsTests.Plugin_skills_are_replaced_per_source_and_locked`
  and `PluginSkillDiscoveryTests.A_hired_plugins_skill_is_found_named_listed_absent_and_locked`.
- **Names are forced.** A file naming the plugin itself (`<id>` or `plugin-<id>`) becomes
  `plugin-<id>`; any other becomes `plugin-<id>-<name>`. A plugin cannot shadow `manager` or any
  other skill. Pinned by `PluginSkillsTests.A_plugin_skills_name_is_forced_into_the_plugins_namespace`.
- **Roles `manager member`** unless the file's `roles` narrows them; `any` means both, and
  `concierge` is never offered. A file that cannot be read refuses the plugin, by name. Pinned by
  `PluginSkillsTests.A_skill_file_narrows_its_roles_but_never_widens_them` and
  `A_skill_file_that_cannot_be_read_refuses_the_plugin_by_name`.
- **Replaced per plugin, in one transaction** (`ReplacePluginSkillsAsync`); an uninstalled plugin's
  skills are removed on the next rescan. Pinned by
  `PluginSkillDiscoveryTests.A_rescan_indexes_a_newly_installed_plugins_skill_and_drops_an_uninstalled_ones`.
- **Never in a prompt's "Available skills"**, so a prompt does not grow with every install. A
  Manager finds a plugin's skill three ways: its **roster** names each plugin member on its team, as
  `Echo (plugin sample-echo: <description> - skill plugin-sample-echo)`; **`skills_search`** finds
  every installed plugin's skill; and the **`hiring`** tool lists every installed plugin with its id,
  one line and skill. Pinned by `PluginSkillDiscoveryTests.A_hired_plugins_skill_is_found_named_listed_absent_and_locked`.

## Events

A plugin publishes with a `publish` record:
`{"t":"publish","type":"done","payload":{"length":4}}`.

- **Only a declared suffix.** The type is always `plugin.<id>.<suffix>`, and `<suffix>` must be one
  the manifest's `events.publishes` declares. The record may name the bare suffix or the full type.
  Anything else - an undeclared suffix, another plugin's type, a platform type such as
  `agentContainer.completed` - is dropped, with one `progress` warning row per type per run. A
  platform type is refused a second time where every publish is appended (`MemberReports.PublishAsync`).
  Pinned by `PluginMemberEndToEndTests.C_An_undeclared_or_forged_publish_is_dropped_with_one_warning_row`.
- **Joins the workflow.** The row's source is the member's qualified id, so its team is the
  member's, and its causation is the message the run is handling. Its payload is a JSON object no
  larger than the member's `ExcerptChars` (4,000 by default), every string redacted; a key or a
  number that holds a secret drops the publish (see Secrets above). Pinned by
  `PluginMemberRunnerTests.A_declared_publish_is_appended_and_an_undeclared_one_warned_once` and
  `A_publish_over_the_members_artifact_limit_is_dropped`.
- **Everything reads the union.** `EventCatalog.For(type)` answers the platform's types and the
  installed plugins' (`IPluginEventRegistry`, the plugin catalog, read on each call so a rescan is
  seen at once). Triggers and their filters, `{event.<field>}` tokens, `GET /api/events` and the
  high-volume rule all go through it, so a plugin event declared `highVolume: true` is refused as a
  language-model member's subscription or trigger by the existing checks. Pinned by
  `PluginEventsTests` and
  `PluginMemberEndToEndTests.C_A_plugins_publish_reaches_a_trigger_on_its_team_in_the_same_workflow`.
- **sample-echo** declares `done` (field `length`) and publishes it on every run that completes.
  `publish:<type>` in an instruction makes it publish that type too, for trying the refusals.

## Hiring

A person hires a plugin through `POST /api/teams/{team}/containers` with `agent: "plugin:<id>"`.
**A Manager may hire any installed plugin onto its own team** with the `member` tool:
`plugin` (the id the `hiring` tool lists), `config`, and `secrets`.

- It is validated against the manifest exactly as a person's hire is. It is not bound by the team's
  agent allowlist, which chooses the Agents a hire may run; a plugin runs no model.
- **Secrets are logical keys a person has already set and bound on this team.** A Manager never
  handles a value, and it may bind only a key that a person's hire has already bound on a member
  of its own team, and that is still set on the Host. Anything else, optional or not, is refused
  with a reason that names the key and no value. That includes `PATH`, `HOSTNAME` or any other
  variable the Host happens to carry, a key set on the Host that no person has bound here, and one
  bound only on another team. So the first member that uses a new key is hired by a person. Being
  "set on the Host" is not enough on its own: the secret store is the Host's whole environment,
  and the Host cannot see which names `secret set` wrote, because that list lives in the
  operator's env file outside the container. A person's binding is a deliberate choice for this
  team. A Manager cannot grow the set, and firing the last member that bound a key removes the key
  from it. Pinned by `PluginManagerHiringTests.A_manager_binds_only_keys_a_person_has_bound_on_its_team`.
- Its own team only: the route's `{team}` is checked against the Manager's credential.

Pinned by `PluginManagerHiringTests`.

## A workflow a person starts by telling a plugin

A workflow is its owner's to declare: the member its root instruction addressed. When a person
tells a plugin member directly, the plugin owns the workflow, and a plugin holds no permit to call
`workflow-complete`. So **the platform declares a workflow whose owner cannot declare** (holds no
`Progress` permit) at the first run end in it, by any member, that succeeded and leaves nothing
working it: nobody running, nothing pending or undelivered, not paused, and no unfinished card. The
`workflow.completed` row is the owner's, with `declaredByPlatform: true`. A failed or stopped run
leaves the workflow open for a person. An agent owner is unchanged: it declares for itself
(`UndeclarableWorkflows`). Pinned by
`PluginMemberEndToEndTests.E1_A_workflow_a_person_starts_by_telling_a_plugin_ends_completed`.

## Plugins that act outward: the house rule

A plugin **acts outward** when it sends, posts, pays, deletes, or does anything else a person cannot
take back in the outside system. Every such plugin follows this rule; a spec for one names each
setting below.

- **The default mode never acts outward.** A mail plugin creates a draft for the person to send;
  any other plugin does a dry run and reports what it would have done. A member hired with no mode
  set is in this mode.
- **Acting for real needs an allowlist.** The real mode is a separate `config` value, and it needs a
  non-empty allowlist setting in the member's `config` (for mail: the recipient addresses and whole
  domains it may send to).
  - A member hired or run in the real mode with an empty allowlist is **refused**, with a sentence
    naming the allowlist setting. Check it at hire (a manifest cannot express "required when"
    today, so the plugin also checks it on every run) and fail the run with `ok:false` in those
    words.
  - An action whose target is outside the allowlist is **refused and reported, never sent**. The
    run reports it (`blocked` for that item, or in its result), and nothing reaches the outside
    system.
  - There is no "act for anyone" mode.
- **Only a person widens it.** The mode, the allowlist and the scope limits are `config` a person
  sets when hiring. No command, instruction or incoming content can widen them: a command line
  cannot name a recipient outside the list or switch the mode, and a plugin never reads settings
  out of what it fetched. Incoming content (an email body, a web page) is untrusted, and it reaches
  a Manager's context, so a Manager told by that content to send somewhere else must be unable to.
  **The platform enforces this:** mark those fields `"setBy": "person"` in the manifest. An
  agent's hire (a Manager's `member` tool, or a Concierge) may leave such a field at its default or
  omit it, and is refused any other value with a sentence naming the field; only a person's hire
  sets it. For mail: `sendMode` (default `draft`) and `sendAllowlist` are both `"setBy": "person"`.
- **Scope limits are settings too.** Which mailbox rule a watcher follows, which folders a member
  may move mail to, which bucket it may delete from: each is a `config` value, not a choice made
  per command.

The built-in skill `authoring-plugins` (offered to the Concierge and the Manager) carries this rule
in plain words, with the checklist for writing a plugin's spec as a backlog item.

## Choosing a language

The protocol is JSON on stdin and stdout, so a plugin may be written in any language whose program
runs in the image. Every run is a new process, so start-up is paid on every run.

- **Go is the default** for connectors to REST APIs, clouds, databases, queues and mail: one small
  static binary per processor, no runtime, a start measured in milliseconds. Template:
  `samples/plugins/sample-echo-go`.
- **.NET** when the best or only SDK for the target system is .NET: SharePoint, Dynamics, Exchange
  on-premises, SAP, heavy Office documents. Template: `samples/plugins/sample-echo`.
- **Python** when the library the plugin needs exists only in Python.

A plugin's spec says which language and why: find which language has the official SDK for the
target system, and record the choice and the reason. The built-in `authoring-plugins` skill carries
these rules.

### Self-contained plugins

The image guarantees the .NET runtime, Node and Python 3, and nothing else. Everything a plugin needs
beyond that lives in its own folder:

- **Go:** libraries are compiled into the binary; build with `CGO_ENABLED=0` so it links nothing
  from the image.
- **.NET:** `dotnet publish` puts every NuGet package into the plugin's folder beside its build.
- **Python:** packages go in a virtual environment inside the plugin's folder, and the launcher runs
  that environment's interpreter.

Nothing a plugin needs is ever added to the image, and a plugin must not rely on a system package
someone happened to install. The runtimes it does need from the image are named in the manifest's
`requires` (`"dotnet"`, `"node"`, `"python3"`); the .NET template declares `["dotnet"]`, and a Go
plugin declares none.

## Where it is pinned

- **Agents are unchanged.** `MemberGoldenTests` pins an agent member's rows, prompt, context,
  environment, persisted row and snapshot on goldens recorded before the refactor. Re-record them
  only with `HARNESS_UPDATE_GOLDENS=1`, and only for a change that is meant to be visible.
- **The end-to-end proof of concept.** `PluginMemberEndToEndTests` installs and hires sample-echo on
  the real Host. It also asserts that the pump (`Harness.Containers`) has no code naming plugins.
  `PluginGoTemplateEndToEndTests` does the same for sample-echo-go, built for both processors.
- **The pump stays generic, structurally.** `PumpArchitectureTests` reads the pump's compiled IL:
  `MemberRef` is used only by `KindOf` in `MemberRuntime.Snapshot`, and a member's implementation -
  `ContainerDefinition.Agent`, `ContainerSnapshot.Agent` and `Kind`, `MemberRef.KindOf` - is
  followed through the stack, locals, fields and return values and may only be PASSED ON (to the
  invocation the router receives, the snapshot, the definition, a delegate the Host handed in, or
  logging), never transformed or compared. Its self-tests prove a comparison, `ToLowerInvariant().StartsWith`,
  `Split(':')[0] ==` and a comparison on `Snapshot().Kind` are all caught. The whole
  `ContainerDefinition` carries the Agent too, so it is followed the same way, but checked against a
  deny-list. It may not reach `ToString`, `Equals` (the instance method or `object.Equals`),
  `GetHashCode`, `PrintMembers`, `Deconstruct`, the equality operators, string formatting or
  interpolation, `System.Text.Json`, reflection, or logging. Each of those has a self-test in
  `The_scan_catches_a_violation`, and `PrintMembers` has `The_definition_rules_forbid_PrintMembers`.
  A read of another field, such as `Environment.Count`, is a capability, and it is allowed.
- **Plugins run as `agent`.** `PluginLaunchUserTests` launches a plugin through a real user switch
  and checks it runs as `agent` and reads its `0750` directory. Where the process cannot switch
  users it skips, naming why; the release suite runs it as root in the product image.
- **The web UI and the runs route.** `PluginMemberRunsTests` (runs listed with their output, one per
  run), and the web mount specs `add-member-plugin`, `container-card-plugin` and
  `team-kpi-plugin-only`.
- **The pieces.** `PluginMemberRunnerTests` (protocol), `PluginCatalogTests` (manifest v1),
  `PluginMemberRegistryTests` (hire, restore, repoint), `ChildProcessTests` (the shared launcher)
  and `MemberReportsTests` (the shared report path).
- **The install.** `cli/internal/cli/plugin_test.go` pins each `yawble plugin` command and each
  refusal against a scripted engine, including an install from a folder whose launcher has no execute
  bit. It also runs the container scripts under a real `sh`: the modes, a setgid parent, `active`, and
  a kept version. `plugin install --from-instance` is pinned against a faked request/report exchange:
  the request it writes (`replace` only with `--force`), no copy or rescan of its own, the Host's
  verdict for a good folder, a refused manifest, an installed version without and with `--force`,
  and a folder outside the data root, and a request the Host never answers being withdrawn; its
  request, report and withdraw scripts run under `sh`. `PluginInstallRouteTests` pins the Host's side.
  `cli/internal/plugin` pins the manifest rules against both samples.
  `PluginRescanRequestTests` shows a plugin installed after start is listed with no restart, and pins
  the report's refusals, its hired members and its `0600` mode. `PrepareVolumeTests` pins
  `/data/plugins` as `harness:agent 0750`, created when missing.
- **Install from inside the instance, settings after hire.** `PluginInstallRouteTests` installs a built
  folder from inside the data root through the route (modes, `active`, the tenant row) and through the
  `.install` request file, and pins each refusal: outside the data root, a symlink out of it, a symlink
  inside leaving the folder, an existing version without replace, and a manifest the catalog refuses;
  a Manager is refused. It pins the settings route: validated like a hire, never a secret value, the
  tenant row, and a Manager refused. `PluginManifestListAndRequiresTests` pins list settings and
  `requires`; `PluginMemberRunnerTests.A_list_setting_is_delivered_in_the_requests_config_and_defaults_to_empty`
  pins a list reaching the plugin.
